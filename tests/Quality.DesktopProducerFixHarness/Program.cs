using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper.Quality.DesktopProducerFixHarness
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.GetType().FullName + ": " + exception.Message);
                Console.Error.WriteLine(exception.StackTrace ?? string.Empty);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length != 1 || Directory.Exists(args[0]))
            {
                Console.Error.WriteLine("Expected a new output directory.");
                return 2;
            }
            var jobs = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(Path.Combine(jobs, "task-quality-followup"));
            var source = new FileDesktopCommonCommandSource(jobs, new FakeClock(new DateTime(2026, 9, 1, 4, 30, 0, DateTimeKind.Utc)), new SequentialIds());
            var command = source.PrepareQuestion("质量追问", true, "task-quality-followup", new string('a', 64), 1);
            var serializer = new JavaScriptSerializer();
            var actionPath = Path.Combine(jobs, "task-quality-followup", command.payloadRef.Replace('/', Path.DirectorySeparatorChar));
            var action = (IDictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(actionPath));
            var reference = (IDictionary<string, object>)action["questionTurnRef"];
            Require(Convert.ToString(reference["artifactRef"]) == "question.turn.0002", "artifactRef is not canonical ordinal");
            Require(Convert.ToString(reference["manifestPath"]) == "artifacts/question.turn.0002/artifact.json", "manifestPath is not task-global canonical");
            var manifestPath = Path.Combine(jobs, "task-quality-followup", Convert.ToString(reference["manifestPath"]).Replace('/', Path.DirectorySeparatorChar));
            Require(Sha(manifestPath) == Convert.ToString(reference["expectedSha256"]), "manifest reference hash mismatch");
            var manifest = (IDictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(manifestPath));
            Require(Convert.ToString(manifest["artifactId"]) == "question.turn.0002", "manifest artifact identity mismatch");
            var file = (IDictionary<string, object>)((IEnumerable)manifest["files"]).Cast<object>().Single();
            var payloadRelative = Convert.ToString(file["path"]);
            Require(payloadRelative == "commands/command-0001/question-turn/payload.txt", "payload is not command-owned");
            var payloadPath = Path.Combine(jobs, "task-quality-followup", payloadRelative.Replace('/', Path.DirectorySeparatorChar));
            Require(File.ReadAllText(payloadPath, Encoding.UTF8) == "质量追问", "payload text drifted");
            Require(new FileInfo(payloadPath).Length == Convert.ToInt64(file["bytes"]) && Sha(payloadPath) == Convert.ToString(file["sha256"]), "payload bytes/hash mismatch");
            Require(!File.Exists(Path.Combine(jobs, "task-quality-followup", "commands", "command-0001", "question-turn", "artifact.json")), "legacy command-owned manifest was emitted");
            Require(!File.Exists(Path.Combine(jobs, "task-quality-followup", "commands", "command-0001", "question-turn", "turn.txt")), "legacy turn.txt was emitted");
            Console.WriteLine(serializer.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-quality-desktop-followup-producer.v1" },
                { "status", "passed" },
                { "commandId", command.commandId },
                { "payloadRef", command.payloadRef },
                { "questionTurnRef", reference },
                { "manifestSha256", Sha(manifestPath) },
                { "payload", new Dictionary<string, object> { { "path", payloadRelative }, { "bytes", new FileInfo(payloadPath).Length }, { "sha256", Sha(payloadPath) }, { "text", "质量追问" } } },
                { "legacyPathsEmitted", 0 }
            }));
            return 0;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static string Sha(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var digest = SHA256.Create())
                return BitConverter.ToString(digest.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class SequentialIds : ICommonIdProvider
        {
            private int next;
            public string NewId(string prefix) { next++; return prefix + "-" + next.ToString("D4"); }
        }
    }
}
