using System;
using System.Collections.Generic;
using System.IO;

namespace KnowledgeClipper.Speech.Worker
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            return Run(args, Console.Out, Console.Error);
        }

        internal static int Run(string[] args, TextWriter output, TextWriter error)
        {
            try
            {
                if (args == null || args.Length == 0) throw new ArgumentException("用法: <worker> describe --json | run --request <absolute-request-json> | settings describe/adapt | settings companion describe/status/probe/voices/preview");
                if (args[0] == "describe")
                {
                    if (args.Length != 2 || args[1] != "--json") throw new ArgumentException("describe 只接受 --json");
                    output.WriteLine(WorkerCli.Serialize(WorkerCli.Describe()));
                    return 0;
                }
                if (args[0] == "settings") return RunSettings(args, output);
                if (args[0] == "run") return RunRequest(ParseStandardRun(args), output);
                if (args[0].StartsWith("--", StringComparison.Ordinal)) return RunDeprecated(args, output, error);
                throw new ArgumentException("未知命令: " + args[0]);
            }
            catch (Exception ex)
            {
                var contract = ex as SpeechContractException;
                error.WriteLine(WorkerCli.Serialize(new Dictionary<string, object>
                {
                    { "schemaVersion", "1.0.0" }, { "status", "failed" },
                    { "error", new Dictionary<string, object>
                        {
                            { "code", contract == null ? "worker_cli_error" : contract.Code },
                            { "category", contract == null ? "worker" : "contract" }, { "retryable", false }, { "message", ex.Message }
                        }
                    }
                }));
                return 2;
            }
        }

        private static int RunRequest(string requestPath, TextWriter output)
        {
            var context = WorkerCli.DeriveRunContext(requestPath);
            var result = context.Envelope.operation == "speech.synthesize" ? RunSynthesis(context) : RunPlayback(context);
            output.WriteLine(WorkerCli.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "requestId", result.requestId }, { "operation", context.Envelope.operation }, { "status", result.status }
            }));
            return result.status == "completed" || result.status == "cancelled" ? 0 : 1;
        }

        private static int RunSettings(string[] args, TextWriter output)
        {
            if (args.Length >= 3 && args[1] == "companion") return RunSettingsCompanion(args, output);
            if (args.Length == 3 && args[1] == "describe" && args[2] == "--json")
            {
                output.WriteLine(WorkerCli.Serialize(SpeechSettingsAdapterCli.Describe()));
                return 0;
            }
            if (args.Length == 5 && args[1] == "adapt" && args[2] == "--input" && args[4] == "--json")
            {
                output.WriteLine(WorkerCli.Serialize(SpeechSettingsAdapterCli.AdaptFile(args[3])));
                return 0;
            }
            throw new ArgumentException("settings 只接受 describe --json 或 adapt --input <absolute-json> --json");
        }

        private static int RunSettingsCompanion(string[] args, TextWriter output)
        {
            if (args.Length == 4 && args[2] == "describe" && args[3] == "--json")
            {
                output.WriteLine(WorkerCli.Serialize(SpeechSettingsCompanion.Describe()));
                return 0;
            }
            if (args.Length == 6 && (args[2] == "status" || args[2] == "probe") && args[3] == "--input" && args[5] == "--json")
            {
                output.WriteLine(WorkerCli.Serialize(SpeechSettingsCompanion.ProbeFile(args[4], args[2] == "probe")));
                return 0;
            }
            if (args.Length == 6 && args[2] == "voices" && args[3] == "--input" && args[5] == "--json")
            {
                output.WriteLine(WorkerCli.Serialize(SpeechVoiceDiscovery.ExecuteFile(args[4])));
                return 0;
            }
            if (args.Length == 6 && args[2] == "preview" && args[3] == "--input" && args[5] == "--json")
            {
                output.WriteLine(WorkerCli.Serialize(SpeechSpeakerPreview.ExecuteFile(args[4])));
                return 0;
            }
            throw new ArgumentException("settings companion 只接受 describe/status/probe/voices/preview 的固定 JSON 入口");
        }

        private static WorkerResultV1 RunSynthesis(WorkerRunContext context)
        {
            string edgeTts;
            try { edgeTts = WorkerCli.ConfiguredTool(context.Envelope, "edgeTtsPath", "KNOWLEDGE_CLIPPER_EDGE_TTS", "REMNOTE_QUICK_CLIPPER_EDGE_TTS", "edge-tts"); }
            catch (Exception ex) { return SynthesisWorker.RunRejected(context.JobRoot, context.AttemptDirectory, context.Envelope, ex); }
            return new SynthesisWorker(new SpeechRequestReader(), new EdgeTtsSynthesizer(edgeTts, 300000))
                .RunAsync(context.JobRoot, context.AttemptDirectory).GetAwaiter().GetResult();
        }

        private static WorkerResultV1 RunPlayback(WorkerRunContext context)
        {
            PlaybackWorkItem work;
            try { work = new PlaybackRequestReader().ReadAndValidate(context); }
            catch (Exception ex) { return PlaybackWorker.RunRejected(context.JobRoot, context.AttemptDirectory, context.Envelope, ex); }
            string ffplay;
            try { ffplay = WorkerCli.ConfiguredTool(context.Envelope, "ffplayPath", "KNOWLEDGE_CLIPPER_FFPLAY", "REMNOTE_QUICK_CLIPPER_FFPLAY", "ffplay"); }
            catch (Exception ex) { return PlaybackWorker.RunRejected(context.JobRoot, context.AttemptDirectory, context.Envelope, ex); }
            using (var coordinator = new PlaybackCoordinator(new FfplayAudioPlayerFactory(ffplay)))
                return new PlaybackWorker(coordinator).RunPlayAsync(context.JobRoot, context.AttemptDirectory, work).GetAwaiter().GetResult();
        }

        private static string ParseStandardRun(string[] args)
        {
            if (args.Length != 3 || args[1] != "--request" || string.IsNullOrWhiteSpace(args[2]))
                throw new ArgumentException("run 只接受 --request <absolute-request-json>");
            return args[2];
        }

        private static int RunDeprecated(string[] args, TextWriter output, TextWriter error)
        {
            var values = ParseLegacy(args);
            if (!values.ContainsKey("command") || values["command"] != "synthesize")
                throw new ArgumentException("deprecated 入口仅支持 --command synthesize");
            error.WriteLine("DEPRECATED: use run --request <absolute-request-json>; registry entrypoint must not use legacy arguments");
            var worker = new SynthesisWorker(
                new SpeechRequestReader(),
                new EdgeTtsSynthesizer(Required(values, "edge-tts"), 300000));
            var result = worker.RunAsync(Required(values, "job-root"), Required(values, "attempt-dir")).GetAwaiter().GetResult();
            output.WriteLine(result.status + " " + (result.error == null ? "audio_ready" : result.error.code));
            return result.status == "completed" ? 0 : 1;
        }

        private static Dictionary<string, string> ParseLegacy(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new ArgumentException("参数必须为 --name value");
                var name = args[i].Substring(2);
                if (result.ContainsKey(name)) throw new ArgumentException("重复参数 --" + name);
                result[name] = args[++i];
            }
            return result;
        }

        private static string Required(Dictionary<string, string> values, string name)
        {
            string value;
            if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException("缺少 --" + name);
            return value;
        }
    }
}
