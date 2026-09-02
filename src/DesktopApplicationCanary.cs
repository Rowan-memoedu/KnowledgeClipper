using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Desktop.Application;

namespace KnowledgeClipper
{
    internal static class DesktopApplicationCanary
    {
        private static int assertions;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2)
                {
                    VerifyExistingArtifact(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
                    Console.WriteLine("DESKTOP_APPLICATION_LIVE_ARTIFACT_CANARY PASS assertions=" + assertions);
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException("expected no arguments or <jobRoot> <manifestPath>");
                RunPortableRegression();
                Console.WriteLine("DESKTOP_APPLICATION_CANARY PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("DESKTOP_APPLICATION_CANARY FAIL " + exception);
                return 1;
            }
        }

        private static void VerifyExistingArtifact(string jobRoot, string manifestPath)
        {
            var reference = ReferenceFromManifest(jobRoot, manifestPath);
            var state = AnkiProbeProjection.FromArtifact(jobRoot, reference);
            Assert(state.Execution == ExecutionState.Completed, "real four-file artifact projects a completed execution");
            Assert(state.Business == BusinessState.Ready || state.Business == BusinessState.NeedsInitialization || state.Business == BusinessState.MigrationRequired, "real four-file artifact projects its producer business status");
            Assert(state.Persistence == PersistenceState.Persisted, "real four-file artifact is persisted only after every declared file verifies");
            Assert(state.Status == "ready" || state.Status == "needs_initialization" || state.Status == "migration_required", "real four-file artifact status remains producer-declared");
        }

        private static void RunPortableRegression()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-desktop-application-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var ready = CreateFixture(Path.Combine(root, "ready"), "ready", false, false);
                var readyState = AnkiProbeProjection.FromArtifact(ready.JobRoot, ready.Reference);
                Assert(readyState.IsReady && readyState.Execution == ExecutionState.Completed && readyState.Business == BusinessState.Ready, "ready four-file artifact projects completed and ready");
                Assert(readyState.Persistence == PersistenceState.Persisted && readyState.Interaction == InteractionState.Ready, "ready artifact state dimensions are explicit");

                var needsInitialization = CreateFixture(Path.Combine(root, "needs-initialization"), "needs_initialization", false, false);
                var pendingState = AnkiProbeProjection.FromArtifact(needsInitialization.JobRoot, needsInitialization.Reference);
                Assert(pendingState.NeedsInitialization && pendingState.Execution == ExecutionState.Completed, "needs_initialization is a completed probe rather than a failed execution");
                Assert(pendingState.Business == BusinessState.NeedsInitialization && pendingState.Interaction == InteractionState.AttentionRequired, "needs_initialization remains observable business attention");

                var tampered = CreateFixture(Path.Combine(root, "tampered-sidecar"), "ready", false, false);
                File.AppendAllText(Path.Combine(tampered.JobRoot, tampered.SidecarRelativePath.Replace('/', Path.DirectorySeparatorChar)), "tamper", Encoding.UTF8);
                AssertThrows("desktop.artifact-file-bytes-mismatch", delegate { AnkiProbeProjection.FromArtifact(tampered.JobRoot, tampered.Reference); }, "tampered non-entrypoint sidecar is rejected before hash projection");

                var missing = CreateFixture(Path.Combine(root, "missing-sidecar"), "ready", false, false);
                File.Delete(Path.Combine(missing.JobRoot, missing.SidecarRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                AssertThrows("desktop.artifact-file-path-invalid", delegate { AnkiProbeProjection.FromArtifact(missing.JobRoot, missing.Reference); }, "missing declared sidecar is rejected");

                var duplicate = CreateFixture(Path.Combine(root, "duplicate-path"), "ready", true, false);
                AssertThrows("desktop.artifact-file-path-duplicate", delegate { AnkiProbeProjection.FromArtifact(duplicate.JobRoot, duplicate.Reference); }, "duplicate declared file path is rejected");

                var reordered = CreateFixture(Path.Combine(root, "entrypoint-last"), "ready", false, true);
                var reorderedState = AnkiProbeProjection.FromArtifact(reordered.JobRoot, reordered.Reference);
                Assert(reorderedState.IsReady, "metadata.entrypoint is authoritative regardless of file order and sidecars");

                SettingsDraftRegression();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void SettingsDraftRegression()
        {
            var originalTarget = new RemNoteTargetSettings("persisted-rem-id", "旧目标");
            var remnote = new RemNoteDestinationSettings("remnote-destination-settings.v1", false, originalTarget,
                new RemNoteAdvancedSettings("mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd"));
            var profile = new WorkflowSettingsProfile("默认", new WorkflowRouteSettings("cards", Array.Empty<string>()),
                new CaptureSettings(false, "sentence", "ocr"), null, remnote);
            var document = new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v3", "default",
                new Dictionary<string, WorkflowSettingsProfile>(StringComparer.Ordinal) { ["default"] = profile });
            var draft = SettingsDraft.Open(document, new string('a', 64));
            draft.StageVerifiedRemNoteBinding("verified-rem-id", "剪藏");
            Assert(draft.Persistence == PersistenceState.VerifiedPending && draft.RemNoteBindingStatus == "verified_pending", "verified discovery becomes an explicit pending draft binding");
            Assert(draft.TryGetRemNoteTarget(out var pendingId, out var pendingTitle) && pendingId == "verified-rem-id" && pendingTitle == "剪藏", "same-window probe reads the verified pending binding without requiring destination enablement");
            var prepared = draft.PrepareCommit();
            Assert(prepared.Profiles["default"].Remnote.Target.RootRemId == "verified-rem-id" && draft.Execution == ExecutionState.Submitting, "save prepares one validated immutable workflow document");
            draft.MarkPersisted(new string('b', 64));
            Assert(draft.Persistence == PersistenceState.Persisted && draft.PersistedRevision == new string('b', 64), "successful atomic store commit advances the persisted revision");

            var failed = SettingsDraft.Open(document, new string('c', 64));
            failed.RecordRemNoteDiscoveryFailure();
            Assert(failed.TryGetRemNoteTarget(out var unchangedId, out _) && unchangedId == "persisted-rem-id" && !failed.HasPendingChanges, "ambiguous timeout or failure preserves the old persisted binding");
            var discarded = SettingsDraft.Open(document, new string('d', 64));
            discarded.StageVerifiedRemNoteBinding("discarded-rem-id", "临时目标");
            var restored = discarded.Discard();
            Assert(restored.Profiles["default"].Remnote.Target.RootRemId == "persisted-rem-id" && discarded.Persistence == PersistenceState.Rejected, "cancel discards the draft and returns the original document");
        }

        private static Fixture CreateFixture(string jobRoot, string status, bool duplicatePath, bool entrypointLast)
        {
            Directory.CreateDirectory(jobRoot);
            var artifactId = "anki-evidence-fixture";
            var baseRelative = "runs/run-1/stages/anki-probe/attempts/attempt-1/outputs/";
            var entrypoint = baseRelative + "connection-status.json";
            var operations = baseRelative + "operations.ndjson";
            var preflight = baseRelative + "preflight.json";
            var receipt = baseRelative + "receipt.json";
            var statusBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "anki-connection-status.v1",
                operation = "probe",
                effectClass = "provider-read-only",
                externalMutation = false,
                status,
                apiVersion = 6,
                models = new[] { new { noteTypeKey = "knowledge.basic", status = status == "ready" ? "ready" : "missing" } },
                decks = new[] { new { routeKey = "cards", status = status == "ready" ? "ready" : "missing" } },
                mutatingActionsObserved = Array.Empty<string>(),
                initializationWouldWrite = status != "ready",
                migrationRequired = false
            }, ContractJson.Options);
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [entrypoint] = statusBytes,
                [operations] = Encoding.UTF8.GetBytes("{\"action\":\"getVersion\",\"status\":\"completed\"}\n"),
                [preflight] = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"anki-probe-preflight.v1\",\"status\":\"ready\"}"),
                [receipt] = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"anki-probe-receipt.v1\",\"writes\":0}")
            };
            foreach (var pair in payloads)
            {
                var path = Path.Combine(jobRoot, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, pair.Value);
            }
            var ordered = entrypointLast
                ? new[] { operations, preflight, receipt, entrypoint }
                : new[] { entrypoint, operations, preflight, receipt };
            var files = ordered.Select(path => new
            {
                path,
                sha256 = Sha(payloads[path]),
                bytes = payloads[path].LongLength,
                mediaType = path.EndsWith(".ndjson", StringComparison.Ordinal) ? "application/x-ndjson" : "application/json"
            }).ToList();
            if (duplicatePath) files.Add(files[1]);
            var manifestRelative = "artifacts/" + artifactId + "/artifact.json";
            var manifestPath = Path.Combine(jobRoot, manifestRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0.0",
                artifactId,
                kind = "anki-connection-status",
                contractVersion = "1.0.0",
                mediaType = "application/vnd.knowledge-clipper.anki-connection-status+json",
                files,
                metadata = new { entrypoint }
            }, ContractJson.Options);
            File.WriteAllBytes(manifestPath, manifestBytes);
            return new Fixture
            {
                JobRoot = jobRoot,
                SidecarRelativePath = operations,
                Reference = new DesktopArtifactReference(artifactId, "anki-connection-status", "1.0.0", manifestRelative, Sha(manifestBytes))
            };
        }

        private static DesktopArtifactReference ReferenceFromManifest(string jobRoot, string manifestPath)
        {
            var fullRoot = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!manifestPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("live artifact manifest is outside job root");
            var bytes = File.ReadAllBytes(manifestPath);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return new DesktopArtifactReference(
                root.GetProperty("artifactId").GetString(),
                root.GetProperty("kind").GetString(),
                root.GetProperty("contractVersion").GetString(),
                Path.GetRelativePath(jobRoot, manifestPath).Replace('\\', '/'),
                Sha(bytes));
        }

        private static void AssertThrows(string expectedCode, Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException exception) when (exception.Message == expectedCode) { Assert(true, message); return; }
            throw new InvalidOperationException("Assertion failed: " + message + " (expected " + expectedCode + ")");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
            assertions++;
            Console.WriteLine("PASS " + message);
        }

        private static string Sha(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }

        private sealed class Fixture
        {
            internal string JobRoot { get; set; }
            internal string SidecarRelativePath { get; set; }
            internal DesktopArtifactReference Reference { get; set; }
        }
    }
}
