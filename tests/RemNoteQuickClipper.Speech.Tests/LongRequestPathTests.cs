using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using KnowledgeClipper.Speech.Worker;

namespace KnowledgeClipper.Speech.Tests
{
    internal static class LongRequestPathTests
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        // The original request is read only. All CLI runs consume new isolated copies
        // with the runtime pinned to FakeTool, including playback.
        private static int Main(string[] args)
        {
            try
            {
                var cases = new[] {
                    RunCase(args[0], Path.Combine(args[1], "short"), 0, args[2], args[3]),
                    RunCase(args[0], Path.Combine(args[1], "same-length"), 266, args[2], args[3]),
                    RunCase(args[0], Path.Combine(args[1], "independent-deep"), 440, args[2], args[3])
                };
                Write(Path.Combine(args[1], "long-request-receipt.json"), new {
                    status = "passed", sourceRequest = args[0], sourceRequestSha256 = SpeechRequestReader.HashFile(args[0]),
                    workerVersion = WorkerCli.WorkerVersion, workerSha256 = SpeechRequestReader.HashFile(args[2]),
                    physicalSpeakerInvocations = 0, networkTtsCalls = 0, cases = cases
                });
                Console.WriteLine("PASS long request regression " + args[1]);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string root)
        {
            var template = Fixture.Create(root, "Independent long request regression.", null);
            var worker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.Worker.exe");
            var fake = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.FakeTool.exe");
            RunCase(template.RequestPath, Path.Combine(root, "cli-long"), 440, worker, fake);
        }

        internal static object RunCase(string sourceRequest, string caseRoot, int minimumLength, string worker, string fake)
        {
            var source = WorkerCli.DeriveRunContext(sourceRequest);
            var envelope = Read<WorkerRequestEnvelopeV1>(sourceRequest);
            var suffix = Path.Combine("jobs", envelope.taskId, envelope.attemptPath.Replace('/', '\\'), "request.json");
            var parent = caseRoot;
            while (Path.Combine(parent, suffix).Length < minimumLength)
            {
                var remaining = minimumLength - Path.Combine(parent, suffix).Length;
                parent = Path.Combine(parent, new string('x', Math.Min(60, Math.Max(1, remaining - 1))));
            }
            var job = Path.Combine(parent, "jobs", envelope.taskId);
            var requestPath = SpeechRequestReader.ResolveJobRelativePath(job, envelope.attemptPath + "/request.json");
            envelope.configSnapshot = new Dictionary<string, object> { { "edgeTtsPath", fake } };
            foreach (var relative in envelope.inputRefs.Select(r => r.manifestPath).Concat(new[] { envelope.payload.spokenText.path }).Distinct())
            {
                var destination = SpeechRequestReader.ResolveJobRelativePath(job, relative);
                LongPathFileSystem.CreateDirectory(LongPathFileSystem.GetDirectoryName(destination));
                LongPathFileSystem.CopyFileNew(SpeechRequestReader.ResolveJobRelativePath(source.JobRoot, relative), destination);
            }
            Write(requestPath, envelope);
            var invocationLog = Path.Combine(caseRoot, "fake-invocations.log");
            Execute(worker, requestPath, invocationLog);
            var synthPath = SpeechRequestReader.ResolveJobRelativePath(job, envelope.resultPath);
            var synth = Read<WorkerResultV1>(synthPath);
            Require(synth.status == "completed" && synth.outputs.Count == 1, "synthesis completed with one artifact");
            var manifestPath = SpeechRequestReader.ResolveJobRelativePath(job, synth.outputs[0].manifestPath);
            Require(SpeechRequestReader.HashFile(manifestPath) == synth.outputs[0].expectedSha256, "manifest hash");
            var manifest = Read<ArtifactManifestV1>(manifestPath);
            var audio = manifest.files.Single();
            var audioPath = SpeechRequestReader.ResolveJobRelativePath(job, audio.path);
            Require(LongPathFileSystem.FileLength(audioPath) == audio.bytes && SpeechRequestReader.HashFile(audioPath) == audio.sha256, "audio bytes/hash");

            var playback = Read<WorkerRequestEnvelopeV1>(requestPath);
            playback.operation = "speech.playback"; playback.stageId = "speech-playback";
            playback.attemptId = "play-" + Guid.NewGuid().ToString("N");
            playback.requestId = "request-" + playback.attemptId;
            playback.attemptPath = "runs/" + playback.runId + "/stages/" + playback.stageId + "/attempts/" + playback.attemptId;
            playback.outputPath = playback.attemptPath + "/outputs";
            playback.resultPath = playback.attemptPath + "/result.json";
            playback.progressPath = playback.attemptPath + "/progress.ndjson";
            playback.cancelSignal.path = playback.attemptPath + "/cancel.json";
            playback.payload = null; playback.inputRefs = synth.outputs;
            playback.configSnapshot = new Dictionary<string, object> { { "ffplayPath", fake } };
            var playbackRequest = SpeechRequestReader.ResolveJobRelativePath(job, playback.attemptPath + "/request.json");
            var playbackDocument = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(playback));
            playbackDocument["payload"] = new Dictionary<string, object>();
            Write(playbackRequest, playbackDocument);
            Execute(worker, playbackRequest, invocationLog);
            var playResultPath = SpeechRequestReader.ResolveJobRelativePath(job, playback.resultPath);
            var playResult = Read<WorkerResultV1>(playResultPath);
            Require(playResult.status == "completed" && (string)playResult.payload["playbackState"] == "idle_replayable", "natural completion");
            Require(!(bool)playResult.payload["cacheHit"], "playback not reused");
            var invocations = File.ReadAllLines(invocationLog);
            Require(invocations.Length == 2 && invocations.Count(l => l.Contains("--write-media")) == 1 && invocations.Count(l => l.Contains("-nodisp")) == 1, "exactly one fake synth and fake player");

            // Keep job/output guards effective even when every parent exceeds MAX_PATH.
            var wrongJobRequest = Path.Combine(parent, "jobs", "wrong-task", envelope.attemptPath.Replace('/', '\\'), "request.json");
            Write(wrongJobRequest, envelope);
            ExpectContract("task_path_mismatch", delegate { WorkerCli.DeriveRunContext(wrongJobRequest); });
            envelope.outputPath = "../outside";
            ExpectContract("unsafe_artifact_path", delegate { SpeechRequestReader.ValidateEnvelope(job, requestPath, envelope, "speech-synthesize", "speech.synthesize"); });
            envelope.outputPath = envelope.attemptPath + "/outputs";
            envelope.cancelSignal.path = "elsewhere/cancel.json";
            ExpectContract("unsafe_cancellation_signal", delegate { SpeechRequestReader.ValidateEnvelope(job, requestPath, envelope, "speech-synthesize", "speech.synthesize"); });

            Console.WriteLine("PASS request=" + requestPath.Length + " job=" + job.Length + " audio=" + audioPath.Length + " bytes=" + audio.bytes + " idle_replayable; guards retained");
            return new { request = requestPath, requestLength = requestPath.Length, jobRootLength = job.Length,
                synthResult = synthPath, playbackRequest = playbackRequest, playbackResult = playResultPath,
                audioPath = audioPath, audioPathLength = audioPath.Length, audioBytes = audio.bytes, audioSha256 = audio.sha256,
                synthStatus = synth.status, playbackState = playResult.payload["playbackState"], playbackCacheHit = playResult.payload["cacheHit"],
                fakeSynthInvocations = 1, fakePlayerInvocations = 1, boundaryRejections = new[] { "task_path_mismatch", "unsafe_artifact_path", "unsafe_cancellation_signal" } };
        }

        private static void Execute(string worker, string request, string log)
        {
            var info = new ProcessStartInfo(worker, "run --request \"" + request + "\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetPathRoot(worker)
            };
            info.EnvironmentVariables["KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH"] = log;
            info.EnvironmentVariables["KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS"] = "10";
            using (var process = Process.Start(info))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("offline CLI timeout"); }
                var attempt = LongPathFileSystem.GetDirectoryName(request);
                LongPathFileSystem.WriteAllTextNew(Path.Combine(attempt, "probe-stdout.log"), stdout.Result, Utf8);
                LongPathFileSystem.WriteAllTextNew(Path.Combine(attempt, "probe-stderr.log"), stderr.Result, Utf8);
                Require(process.ExitCode == 0, "CLI exit " + process.ExitCode + ": " + stderr.Result + stdout.Result);
            }
        }

        private static T Read<T>(string path) { return Json.Deserialize<T>(LongPathFileSystem.ReadAllText(path, Utf8)); }
        private static void Write(string path, object value) {
            LongPathFileSystem.CreateDirectory(LongPathFileSystem.GetDirectoryName(path));
            LongPathFileSystem.WriteAllTextNew(path, Json.Serialize(value), Utf8);
        }
        private static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }
        private static void ExpectContract(string code, Action action) {
            try { action(); } catch (SpeechContractException ex) { Require(ex.Code == code, code + " got " + ex.Code); return; }
            throw new Exception("Expected " + code);
        }
    }
}
