using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Desktop.Core
{
    internal interface IDesktopCommonCommandSource
    {
        CommonTaskCommandV1 PrepareCapture(CaptureBundleV1 bundle);
        CommonTaskCommandV1 PrepareQuestion(string text, bool followUp, string existingTaskId, string questionTargetToken, int? questionTurnCount);
        CommonTaskCommandV1 PrepareMakeCards(string cardStructureRef, string taskId, string questionTargetToken);
        CommonTaskCommandV1 PrepareLanguagePracticeInput(string taskId, string decisionTargetToken, string choice);
        CommonTaskCommandV1 PrepareOcrHumanResolution(string taskId, string decisionTargetToken, string action, string correctedText, string candidateText);
        CommonTaskCommandV1 PrepareReplay(string taskId);
        CommonStageControlRequestV1 PreparePlaybackStop(string taskId);
        CommonTaskCommandV1 PrepareTaskCancel(string taskId);
        CommonTaskQueryV1 PrepareRecentQuery();
        CommonTaskQueryV1 PrepareTaskGet(string taskId);
    }

    internal interface ICommonIdProvider
    {
        string NewId(string prefix);
    }

    internal interface IDesktopTaskSettingsPublisher
    {
        IReadOnlyList<DesktopAdditionalInput> PrepareInputs(string taskRoot, string taskId, string intent);
        string Publish(string taskRoot, string taskId, string intent, CaptureBundleV1 bundle, string captureArtifactRef, string captureManifestPath, string captureManifestSha256, IReadOnlyList<DesktopAdditionalInput> additionalInputs);
    }

    internal sealed class DesktopAdditionalInput
    {
        public string ArtifactRef { get; set; }
        public string Kind { get; set; }
        public string ContractVersion { get; set; }
        public string ManifestPath { get; set; }
        public string ExpectedSha256 { get; set; }
        public string SettingsRevisionId { get; set; }
    }

    internal sealed class SystemCommonIdProvider : ICommonIdProvider
    {
        public string NewId(string prefix) { return prefix + "-" + Guid.NewGuid().ToString("N"); }
    }

    internal sealed class FileDesktopCommonCommandSource : IDesktopCommonCommandSource
    {
        private static readonly Regex Identifier = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
        private readonly string jobsRoot;
        private readonly IClock clock;
        private readonly ICommonIdProvider ids;
        private readonly IDesktopTaskSettingsPublisher settingsPublisher;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = NamedPipeCommonIpcClient.MaximumMessageBytes };

        internal FileDesktopCommonCommandSource(string jobsRoot, IClock clock, ICommonIdProvider ids) : this(jobsRoot, clock, ids, null) { }

        internal FileDesktopCommonCommandSource(string jobsRoot, IClock clock, ICommonIdProvider ids, IDesktopTaskSettingsPublisher settingsPublisher)
        {
            if (string.IsNullOrWhiteSpace(jobsRoot)) throw new ArgumentException("jobsRoot");
            this.jobsRoot = Path.GetFullPath(jobsRoot);
            if (clock == null) throw new ArgumentNullException("clock");
            if (ids == null) throw new ArgumentNullException("ids");
            this.clock = clock;
            this.ids = ids;
            this.settingsPublisher = settingsPublisher;
            Directory.CreateDirectory(this.jobsRoot);
        }

        public CommonTaskCommandV1 PrepareCapture(CaptureBundleV1 bundle)
        {
            CaptureBundleValidator.Validate(bundle);
            var intent = bundle.intent == "read" ? "clip.read-aloud" : bundle.intent == "question" ? "clip.question" : "clip.card";
            var output = bundle.intent == "read" ? "audio.speech" : bundle.intent == "question" ? "question.answer" : "card-bundle";
            return WriteNewCaptureTask(bundle, intent, output, null);
        }

        public CommonTaskCommandV1 PrepareQuestion(string text, bool followUp, string existingTaskId, string questionTargetToken, int? questionTurnCount)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text");
            if (!followUp)
            {
                Dictionary<string, byte[]> files;
                var initialBundle = BuildQuestionBundle(text.Trim(), "inputs/question-turn-0001.txt", out files);
                CaptureBundleValidator.Validate(initialBundle);
                return WriteNewCaptureTask(initialBundle, "clip.question", "question.answer", files);
            }
            RequireIdentifier(existingTaskId, "existingTaskId");
            if (!AutomationCatalogV11.IsDecisionTargetToken(questionTargetToken) || !questionTurnCount.HasValue || questionTurnCount.Value < 1) throw new InvalidOperationException("question.workflow-projection-invalid");
            var commandId = NewId("command");
            var operationId = NewId("operation");
            var turnBytes = new UTF8Encoding(false).GetBytes(text.Trim());
            // The artifact identity is ordinal and task-global, while its payload stays
            // owned by the accepted command directory. Core validates both invariants.
            var turnRef = "question.turn." + (questionTurnCount.Value + 1).ToString("D4");
            var turnPath = "commands/" + commandId + "/question-turn/payload.txt";
            var turnManifestPath = "artifacts/" + turnRef + "/artifact.json";
            WriteBytesOnce(ResolveTaskPath(existingTaskId, turnPath), turnBytes);
            var turnManifest = ArtifactManifest(turnRef, "question.turn", "text/plain", turnPath, turnBytes, "desktop-question-turn");
            var turnManifestBytes = SerializeBytes(turnManifest);
            WriteBytesOnce(ResolveTaskPath(existingTaskId, turnManifestPath), turnManifestBytes);
            var turnReference = new QuestionAnswerArtifactReference { artifactRef = turnRef, kind = "question.turn", contractVersion = "1.0.0", manifestPath = turnManifestPath, expectedSha256 = Sha256(turnManifestBytes) };
            var payloadRef = "commands/" + commandId + "/question-action.v2.json";
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-question-action.v2" }, { "action", "question.follow-up" },
                { "targetToken", questionTargetToken }, { "turnIndex", questionTurnCount.Value }, { "questionTurnRef", turnReference }
            };
            var payloadBytes = SerializeBytes(payload);
            WriteBytesOnce(ResolveTaskPath(existingTaskId, payloadRef), payloadBytes);
            return ExistingCommand(commandId, operationId, existingTaskId, "task.provide_input", payloadRef, payloadBytes);
        }

        public CommonTaskCommandV1 PrepareMakeCards(string cardStructureRef, string taskId, string questionTargetToken)
        {
            RequireIdentifier(taskId, "taskId");
            if (!AutomationCatalogV11.IsDecisionTargetToken(questionTargetToken)) throw new InvalidOperationException("question.workflow-projection-invalid");
            var commandId = NewId("command"); var operationId = NewId("operation");
            var payloadRef = "commands/" + commandId + "/question-action.v2.json";
            var payload = new Dictionary<string, object> { { "schemaVersion", "knowledge-clipper-question-action.v2" }, { "action", "question.make-cards" }, { "targetToken", questionTargetToken } };
            if (!string.IsNullOrWhiteSpace(cardStructureRef)) payload.Add("cardStructureRef", ResolveArtifactReference(taskId, cardStructureRef.Trim(), "question.card-structure"));
            var payloadBytes = SerializeBytes(payload); WriteBytesOnce(ResolveTaskPath(taskId, payloadRef), payloadBytes);
            return ExistingCommand(commandId, operationId, taskId, "task.provide_input", payloadRef, payloadBytes);
        }

        public CommonTaskCommandV1 PrepareReplay(string taskId)
        {
            return PrepareOpaqueTaskAction("playback.replay", null, taskId, "task.provide_input");
        }

        public CommonStageControlRequestV1 PreparePlaybackStop(string taskId)
        {
            RequireIdentifier(taskId, "taskId");
            return new CommonStageControlRequestV1
            {
                taskId = taskId,
                operation = "speech.playback",
                selector = "current-active",
                reason = "user_stop"
            };
        }

        public CommonTaskCommandV1 PrepareLanguagePracticeInput(string taskId, string decisionTargetToken, string choice)
        {
            RequireIdentifier(taskId, "taskId");
            if (!AutomationCatalogV11.IsDecisionTargetToken(decisionTargetToken)) throw new InvalidOperationException("language-practice.decision-target-token-invalid");
            if (choice != AutomationCatalogV11.LanguagePracticeMeaningReading && choice != AutomationCatalogV11.LanguagePracticeWriting && choice != AutomationCatalogV11.LanguagePracticeBoth) throw new InvalidOperationException("language-practice.selection-invalid");
            var commandId = NewId("command");
            var operationId = NewId("operation");
            var payloadRef = "commands/" + commandId + "/language-practice-choice.v2.json";
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-language-practice-choice.v2" },
                { "decisionKind", "language-practice" },
                { "targetToken", decisionTargetToken },
                { "choice", choice }
            };
            var payloadBytes = SerializeBytes(payload);
            WriteBytesOnce(ResolveTaskPath(taskId, payloadRef), payloadBytes);
            return ExistingCommand(commandId, operationId, taskId, "task.provide_input", payloadRef, payloadBytes);
        }

        public CommonTaskCommandV1 PrepareOcrHumanResolution(string taskId, string decisionTargetToken, string action, string correctedText, string candidateText)
        {
            RequireIdentifier(taskId, "taskId");
            if (!AutomationCatalogV11.IsDecisionTargetToken(decisionTargetToken)) throw new InvalidOperationException("ocr-human-resolution.decision-target-token-invalid");
            if (action != "accept_original" && action != "submit_correction" && action != "cancel") throw new InvalidOperationException("ocr-human-resolution.action-invalid");
            var commandId = NewId("command");
            var operationId = NewId("operation");
            var payloadRef = "commands/" + commandId + "/ocr-human-resolution-action.v1.json";
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-ocr-human-resolution-action.v1" },
                { "decisionKind", "ocr-human-resolution" }, { "targetToken", decisionTargetToken }, { "action", action },
                { "reason", action == "accept_original" ? "The user explicitly accepts the frozen OCR spelling." : action == "cancel" ? "The user cancelled this unresolved task." : "The user supplied an exact correction." }
            };
            if (action == "submit_correction")
            {
                if (string.IsNullOrWhiteSpace(correctedText)) throw new InvalidOperationException("ocr-human-resolution.correction-empty");
                if (string.Equals(correctedText, candidateText ?? "", StringComparison.Ordinal)) throw new InvalidOperationException("ocr-human-resolution.correction-unchanged");
                var correctionBytes = new UTF8Encoding(false).GetBytes(correctedText);
                var correctionRef = NewId("ocr.human-correction");
                var correctionPath = "commands/" + commandId + "/ocr-correction/payload.txt";
                var correctionManifestPath = "artifacts/" + correctionRef + "/artifact.json";
                WriteBytesOnce(ResolveTaskPath(taskId, correctionPath), correctionBytes);
                var correctionManifest = ArtifactManifest(correctionRef, "ocr.human-correction.text", "text/plain", correctionPath, correctionBytes, "desktop-ocr-human-resolution");
                var correctionManifestBytes = SerializeBytes(correctionManifest);
                WriteBytesOnce(ResolveTaskPath(taskId, correctionManifestPath), correctionManifestBytes);
                payload.Add("correctionRef", new QuestionAnswerArtifactReference { artifactRef = correctionRef, kind = "ocr.human-correction.text", contractVersion = "1.0.0", manifestPath = correctionManifestPath, expectedSha256 = Sha256(correctionManifestBytes) });
                var before = candidateText ?? "";
                var operation = before.Length == 0 ? "insertion" : "substitution";
                payload.Add("diffs", new object[] { new Dictionary<string, object> { { "segmentIndex", 0 }, { "operation", operation }, { "start", 0 }, { "end", UnicodeScalarCount(before) }, { "before", before }, { "after", correctedText } } });
            }
            var payloadBytes = SerializeBytes(payload);
            WriteBytesOnce(ResolveTaskPath(taskId, payloadRef), payloadBytes);
            return ExistingCommand(commandId, operationId, taskId, "task.provide_input", payloadRef, payloadBytes);
        }

        public CommonTaskCommandV1 PrepareTaskCancel(string taskId)
        {
            RequireIdentifier(taskId, "taskId");
            var commandId = NewId("command");
            var operationId = NewId("operation");
            var payloadRef = "commands/" + commandId + "/cancel-payload.json";
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", CommonContractAdapter.CommonSchemaVersion },
                { "taskId", taskId },
                { "operationId", operationId },
                { "reason", "task_cancel" }
            };
            var payloadBytes = SerializeBytes(payload);
            WriteBytesOnce(ResolveTaskPath(taskId, payloadRef), payloadBytes);
            return ExistingCommand(commandId, operationId, taskId, "task.cancel", payloadRef, payloadBytes);
        }

        public CommonTaskQueryV1 PrepareRecentQuery()
        {
            return CommonContractAdapter.BuildRecentQueryV23(NewId("query"), 20);
        }

        public CommonTaskQueryV1 PrepareTaskGet(string taskId)
        {
            RequireIdentifier(taskId, "taskId"); return CommonContractAdapter.BuildTaskGetV24(NewId("query"), taskId);
        }

        private static int UnicodeScalarCount(string value)
        {
            var count = 0;
            for (var index = 0; index < (value ?? "").Length; index++, count++)
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
            return count;
        }

        private CommonTaskCommandV1 WriteNewCaptureTask(CaptureBundleV1 bundle, string intent, string requestedOutput, IDictionary<string, byte[]> additionalFiles)
        {
            var taskId = NewId("task");
            var operationId = NewId("operation");
            var commandId = NewId("command");
            var taskRoot = Path.Combine(jobsRoot, taskId);
            if (Directory.Exists(taskRoot)) throw new IOException("Immutable task package already exists: " + taskId);
            Directory.CreateDirectory(taskRoot);
            if (additionalFiles != null)
                foreach (var pair in additionalFiles) WriteBytesOnce(ResolveTaskPath(taskId, pair.Key), pair.Value);

            MaterializeCaptureArtifacts(taskId, bundle);

            var bundleRef = NewId("capture.bundle");
            var bundleRelative = "inputs/capture-bundle.v1.json";
            var bundleBytes = SerializeBytes(bundle);
            WriteBytesOnce(ResolveTaskPath(taskId, bundleRelative), bundleBytes);

            var artifactManifestRelative = "artifacts/" + bundleRef + "/artifact.json";
            var artifactManifest = new Dictionary<string, object>
            {
                { "schemaVersion", CommonContractAdapter.CommonSchemaVersion },
                { "artifactId", bundleRef },
                { "kind", "capture-bundle" },
                { "contractVersion", "1.0.0" },
                { "mediaType", "application/vnd.rqc.capture-bundle+json" },
                { "files", new object[] { new Dictionary<string, object> { { "path", bundleRelative }, { "sha256", Sha256(bundleBytes) }, { "bytes", bundleBytes.Length }, { "mediaType", "application/vnd.rqc.capture-bundle+json" } } } },
                { "createdAt", clock.UtcNow.ToUniversalTime().ToString("o") },
                { "producer", new Dictionary<string, object> { { "stageId", "capture" }, { "workerId", "desktop-capture" }, { "workerVersion", "1.0.0" }, { "attemptId", NewId("attempt") } } },
                { "inputRefs", new object[0] },
                { "dataClass", "sensitive" },
                { "metadata", new Dictionary<string, object> { { "entrypoint", bundleRelative } } }
            };
            var artifactManifestBytes = SerializeBytes(artifactManifest);
            WriteBytesOnce(ResolveTaskPath(taskId, artifactManifestRelative), artifactManifestBytes);

            var settingsInputs = settingsPublisher == null ? new List<DesktopAdditionalInput>() : settingsPublisher.PrepareInputs(taskRoot, taskId, intent).ToList();

            var planRelative = "plans/desktop-intent.v1.json";
            var plan = new Dictionary<string, object>
            {
                { "schemaVersion", "desktop-intent.v1" }, { "taskId", taskId }, { "intent", intent },
                { "inputArtifactRef", bundleRef }, { "requestedOutputs", new object[] { requestedOutput } }
            };
            WriteBytesOnce(ResolveTaskPath(taskId, planRelative), SerializeBytes(plan));

            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", CommonContractAdapter.CommonSchemaVersion },
                { "taskId", taskId },
                { "createdAt", clock.UtcNow.ToUniversalTime().ToString("o") },
                { "createdBy", new Dictionary<string, object> { { "application", "KnowledgeClipper.Desktop" }, { "version", "1.0.0" } } },
                { "intent", intent },
                { "executionProfile", intent == "clip.read-aloud" ? "parity-remnote" : "cards-remnote" },
                { "inputRefs", new object[] { new Dictionary<string, object> { { "artifactRef", bundleRef }, { "kind", "capture-bundle" }, { "contractVersion", "1.0.0" }, { "expectedSha256", Sha256(artifactManifestBytes) } } }.Concat(settingsInputs.Select(item => (object)new Dictionary<string, object> { { "artifactRef", item.ArtifactRef }, { "kind", item.Kind }, { "contractVersion", item.ContractVersion }, { "expectedSha256", item.ExpectedSha256 } })).ToArray() },
                { "requestedOutputs", new object[] { requestedOutput } },
                { "initialPlanRef", planRelative },
                { "retentionClass", "standard" },
                { "metadata", new Dictionary<string, object> { { "desktopCaptureId", bundle.captureId } } }
            };
            var manifestBytes = SerializeBytes(manifest);
            var manifestRelativeToJobs = taskId + "/task.manifest.v1.json";
            WriteBytesOnce(Path.Combine(taskRoot, "task.manifest.v1.json"), manifestBytes);
            if (settingsPublisher != null)
            {
                var published = settingsPublisher.Publish(taskRoot, taskId, intent, bundle, bundleRef, artifactManifestRelative, Sha256(artifactManifestBytes), settingsInputs);
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(published)), taskRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("settings.manifest-v2-outside-task-root");
                manifestRelativeToJobs = taskId + "/" + Path.GetFileName(published);
                manifestBytes = File.ReadAllBytes(published);
            }
            return CommonContractAdapter.BuildSubmitCommand(commandId, operationId, manifestRelativeToJobs, Sha256(Encoding.UTF8.GetBytes(manifestRelativeToJobs + "|" + Sha256(manifestBytes))), clock.UtcNow);
        }

        private void MaterializeCaptureArtifacts(string taskId, CaptureBundleV1 bundle)
        {
            if (bundle == null || bundle.artifacts == null) return;
            foreach (var artifact in bundle.artifacts)
            {
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.sourcePath)) continue;
                var source = Path.GetFullPath(artifact.sourcePath);
                if (!File.Exists(source)) throw new FileNotFoundException("Capture artifact staging file is missing.", source);
                var bytes = File.ReadAllBytes(source);
                if (bytes.LongLength != artifact.bytes || !string.Equals(Sha256(bytes), artifact.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Capture artifact staging bytes do not match the immutable bundle metadata: " + artifact.artifactRef);
                WriteBytesOnce(ResolveTaskPath(taskId, artifact.relativePath), bytes);
            }
        }

        private CaptureBundleV1 BuildQuestionBundle(string text, string relativePath, out Dictionary<string, byte[]> files)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var artifactRef = NewId("capture.question.user");
            var captureId = NewId("capture");
            files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { { relativePath, bytes } };
            return new CaptureBundleV1
            {
                schemaVersion = AutomationCatalog.CaptureBundleSchemaVersion,
                captureId = captureId,
                createdAt = clock.UtcNow.ToUniversalTime().ToString("o"),
                intent = "question",
                source = "question",
                modeSnapshot = new CaptureModeSnapshot { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "ocr" },
                artifacts = new List<CaptureArtifact> { new CaptureArtifact { artifactRef = artifactRef, relativePath = relativePath, sha256 = Sha256(bytes), bytes = bytes.Length, mediaType = "text/plain", encoding = "utf-8" } },
                selectedTextArtifactRef = null,
                contextSegments = null,
                questionImages = new List<string>(),
                answerImages = new List<string>(),
                questionTurns = new List<QuestionTurnReference> { new QuestionTurnReference { turnIndex = 1, role = "user", artifactRef = artifactRef } }
            };
        }

        private CommonTaskCommandV1 PrepareOpaqueTaskAction(string action, string reference, string taskId, string commandType)
        {
            RequireIdentifier(taskId, "taskId");
            var commandId = NewId("command");
            var operationId = NewId("operation");
            var payloadRef = "commands/" + commandId + "/desktop-action.json";
            var payload = new Dictionary<string, object> { { "schemaVersion", "desktop-action.v1" }, { "action", action } };
            if (!string.IsNullOrWhiteSpace(reference)) payload.Add("reference", reference);
            var payloadBytes = SerializeBytes(payload);
            WriteBytesOnce(ResolveTaskPath(taskId, payloadRef), payloadBytes);
            return ExistingCommand(commandId, operationId, taskId, commandType, payloadRef, payloadBytes);
        }

        private Dictionary<string, object> ArtifactManifest(string artifactRef, string kind, string mediaType, string payloadPath, byte[] payloadBytes, string stageId)
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", CommonContractAdapter.CommonSchemaVersion }, { "artifactId", artifactRef }, { "kind", kind }, { "contractVersion", "1.0.0" }, { "mediaType", mediaType },
                { "files", new object[] { new Dictionary<string, object> { { "path", payloadPath }, { "sha256", Sha256(payloadBytes) }, { "bytes", payloadBytes.Length }, { "mediaType", mediaType } } } },
                { "createdAt", clock.UtcNow.ToUniversalTime().ToString("o") },
                { "producer", new Dictionary<string, object> { { "stageId", stageId }, { "workerId", "desktop-capture" }, { "workerVersion", "1.0.0" }, { "attemptId", NewId("attempt") } } },
                { "inputRefs", new object[0] }, { "dataClass", "sensitive" }
            };
        }

        private QuestionAnswerArtifactReference ResolveArtifactReference(string taskId, string artifactRef, string expectedKind)
        {
            RequireIdentifier(artifactRef, "artifactRef");
            var taskRoot = ResolveTaskPath(taskId, "artifacts");
            if (!Directory.Exists(taskRoot)) throw new InvalidOperationException("question.card-structure-reference-not-found");
            QuestionAnswerArtifactReference match = null;
            foreach (var manifestPath in Directory.GetFiles(Path.GetFullPath(Path.Combine(jobsRoot, taskId)), "artifact.json", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(manifestPath);
                var manifest = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as IDictionary<string, object>;
                if (manifest == null || Convert.ToString(manifest["artifactId"]) != artifactRef || Convert.ToString(manifest["kind"]) != expectedKind) continue;
                if (Convert.ToString(manifest["contractVersion"]) != "1.0.0" || match != null) throw new InvalidOperationException("question.card-structure-reference-ambiguous");
                var relative = manifestPath.Substring(Path.GetFullPath(Path.Combine(jobsRoot, taskId)).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                match = new QuestionAnswerArtifactReference { artifactRef = artifactRef, kind = expectedKind, contractVersion = "1.0.0", manifestPath = relative, expectedSha256 = Sha256(bytes) };
            }
            if (match == null) throw new InvalidOperationException("question.card-structure-reference-not-found");
            return match;
        }

        private CommonTaskCommandV1 ExistingCommand(string commandId, string operationId, string taskId, string type, string payloadRef, byte[] payloadBytes)
        {
            return CommonContractAdapter.BuildCommand(commandId, operationId, taskId, type, payloadRef, Sha256(Encoding.UTF8.GetBytes(type + "|" + taskId + "|" + payloadRef + "|" + Sha256(payloadBytes))), clock.UtcNow);
        }

        private string ResolveTaskPath(string taskId, string relativePath)
        {
            RequireIdentifier(taskId, "taskId");
            var taskRoot = Path.GetFullPath(Path.Combine(jobsRoot, taskId));
            var target = Path.GetFullPath(Path.Combine(taskRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(taskRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Task payload path escapes the immutable package.");
            return target;
        }

        private byte[] SerializeBytes(object value) { return new UTF8Encoding(false).GetBytes(serializer.Serialize(value)); }

        private static void WriteBytesOnce(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) throw new IOException("Immutable file already exists: " + path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private string NewId(string prefix)
        {
            var value = ids.NewId(prefix);
            RequireIdentifier(value, prefix);
            return value;
        }

        private static void RequireIdentifier(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !Identifier.IsMatch(value)) throw new InvalidOperationException("common.identifier-invalid:" + name);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                var result = hash.ComputeHash(bytes);
                var builder = new StringBuilder(result.Length * 2);
                foreach (var value in result) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
