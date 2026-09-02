using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper.Desktop.Tests
{
    internal static class Program
    {
        private static int failures;

        private static int Main(string[] args)
        {
            if (args.Length != 3 || !File.Exists(args[0]) || !File.Exists(args[1]) || !File.Exists(args[2])) { Console.WriteLine("FAIL v1, v1.1 and v1.2 contract paths required"); return 2; }
            Run("catalog", TestCatalog);
            Run("module-capability-descriptor-fail-closed", TestModuleCapabilityDescriptor);
            Run("contract-code-parity", delegate { TestContractParity(args[0]); });
            Run("contract-v1.1-code-parity", delegate { TestContractParityV11(args[1]); });
            Run("contract-v1.2-inline-input-parity", delegate { TestContractParityV12(args[2]); });
            Run("common-contract-adapter", TestCommonContractAdapter);
            Run("whole-task-cancel-remains-explicit", TestWholeTaskCancelRemainsExplicit);
            Run("context-fail-closed", TestContextFailClosed);
            Run("multi-screenshot-groups", TestMultiScreenshotGroups);
            Run("capture-bundle-invariants", TestCaptureBundleInvariants);
            Run("capture-artifact-materialization", TestCaptureArtifactMaterialization);
            Run("screenshot-cancel-and-undo", TestScreenshotCancelAndUndo);
            Run("question-follow-up-cards", TestQuestionFlow);
            Run("inline-question-reuses-authoritative-flow", TestInlineQuestionFlow);
            Run("question-cancel-and-language-practice", TestQuestionCancelAndLanguagePractice);
            Run("question-answer-artifact-reader", TestQuestionAnswerArtifactReader);
            Run("ocr-human-resolution-artifact-reader", TestOcrHumanResolutionArtifactReader);
            Run("ocr-human-resolution-semantic-actions", TestOcrHumanResolutionSemanticActions);
            Run("ocr-human-resolution-command-payloads", TestOcrHumanResolutionCommandPayloads);
            Run("playback-and-recent-task", TestPlaybackAndRecentTask);
            Run("semantic-test-port", TestSemanticTestPort);
            Run("named-pipe-semantic-actions-direct-stage-control-no-task-cancel", TestNamedPipeSemanticActions);
            Run("named-pipe-question-cancel", TestNamedPipeQuestionCancel);
            Run("named-pipe-language-practice-input", TestNamedPipeLanguagePracticeInput);
            Run("named-pipe-ocr-human-resolution", TestNamedPipeOcrHumanResolution);
            Run("named-pipe-public-module-actions-v1.2-v1.3-v1.4", TestNamedPipeModuleActions);
            Console.WriteLine(failures == 0 ? "PASS desktop automation core" : "FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void TestInlineQuestionFlow()
        {
            var orchestrator = new FakeDesktopOrchestratorPort();
            var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), orchestrator);
            controller.Execute(Action(AutomationCatalog.QuestionOpen));
            var submitted = controller.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = "快捷提问" }));
            var dismissed = controller.DismissQuestionDialogKeepingSession();
            Assert(submitted.noticeCode == "question.submitted" && orchestrator.Questions.SequenceEqual(new[] { "question:快捷提问" }), "inline question uses the existing question.submit path exactly once");
            Assert(dismissed.foregroundState == "idle" && dismissed.questionSession.open && dismissed.questionSession.answerAvailable && dismissed.lastAction == AutomationCatalog.QuestionSubmit, "dismissing the visible answer window restores idle without losing the accepted question session or inventing another semantic action");
        }

        private static void TestCatalog()
        {
            Assert(AutomationCatalog.SemanticActions.Length >= 19, "required action count");
            Assert(AutomationCatalog.SemanticActions.Distinct(StringComparer.Ordinal).Count() == AutomationCatalog.SemanticActions.Length, "actions unique");
            Assert(AutomationCatalog.AutomationIds.Values.Distinct(StringComparer.Ordinal).Count() == AutomationCatalog.AutomationIds.Count, "automation IDs unique");
            foreach (var action in AutomationCatalog.SemanticActions) Assert(AutomationCatalog.AutomationIds.ContainsKey(action), "automation ID for " + action);
        }

        private static void TestModuleCapabilityDescriptor()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-module-descriptor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "descriptor.json");
                File.WriteAllText(path, "{\"schemaVersion\":\"image-ocr-capability-config-descriptor.v2-canary\",\"moduleId\":\"image.ocr.mineru\",\"capabilities\":[{\"capabilityId\":\"offline-readiness\",\"controlKind\":\"diagnostic-action\",\"display\":{\"title\":\"Offline readiness\",\"tier\":\"developer-diagnostics\"},\"availability\":{\"state\":\"available\"},\"statusProbe\":{\"actionId\":\"image.ocr.offline-readiness\"},\"sideEffects\":{\"externalUpload\":false,\"requiresConsent\":false}},{\"capabilityId\":\"live-connectivity\",\"controlKind\":\"diagnostic-action\",\"display\":{\"title\":\"Live connectivity\",\"tier\":\"developer-diagnostics\"},\"availability\":{\"state\":\"available\"},\"statusProbe\":{\"actionId\":\"image.ocr.live-connectivity\"},\"sideEffects\":{\"externalUpload\":true,\"requiresConsent\":true}}]}");
                var descriptor = ModuleCapabilityDescriptorLoader.TryLoad(path);
                Assert(descriptor != null && descriptor.ModuleId == "image.ocr.mineru" && descriptor.Actions.Count == 2, "generic module descriptor identity and actions parsed");
                Assert(descriptor.Actions.Single(item => item.CapabilityId == "offline-readiness").OfflineSafe, "offline readiness is classified from declared side effects");
                Assert(!descriptor.Actions.Single(item => item.CapabilityId == "live-connectivity").OfflineSafe && descriptor.Actions.Single(item => item.CapabilityId == "live-connectivity").RequiresConsent, "live connectivity remains explicit external consent work");
                Assert(ModuleCapabilityDescriptorLoader.TryLoad(Path.Combine(root, "missing.json")) == null, "missing additive descriptor stays pending without guessed fields");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void TestContractParity(string path)
        {
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
            Assert(root != null, "contract object");
            var actions = ((IEnumerable)root["semanticActions"]).Cast<object>().Select(Convert.ToString).ToArray();
            Assert(actions.SequenceEqual(AutomationCatalog.SemanticActions), "action order and values match authority");
            var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in (IEnumerable)root["automationIds"])
            {
                var item = raw as IDictionary<string, object>;
                if (item == null || item["semanticAction"] == null) continue;
                mappings.Add(Convert.ToString(item["semanticAction"]), Convert.ToString(item["automationId"]));
            }
            Assert(mappings.Count == AutomationCatalog.AutomationIds.Count, "mapping count matches authority");
            foreach (var pair in AutomationCatalog.AutomationIds) Assert(mappings.ContainsKey(pair.Key) && mappings[pair.Key] == pair.Value, "mapping matches " + pair.Key);
        }

        private static void TestContractParityV11(string path)
        {
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
            Assert(root != null && Convert.ToString(root["contractId"]) == "knowledge-clipper.desktop-automation" && Convert.ToString(root["contractVersion"]) == AutomationCatalogV11.ContractVersion, "v1.1 neutral contract identity");
            var actions = ((IEnumerable)root["semanticActions"]).Cast<object>().Select(Convert.ToString).ToArray();
            Assert(actions.SequenceEqual(AutomationCatalogV11.SemanticActions), "v1.1 action order and values match authority");
            var mappings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var raw in (IEnumerable)root["automationIds"])
            {
                var item = raw as IDictionary<string, object>;
                var id = Convert.ToString(item["automationId"]);
                Assert(id.StartsWith("knowledge-clipper.desktop.", StringComparison.Ordinal), "v1.1 automation ID uses neutral identity");
                if (item["semanticAction"] == null) continue;
                var action = Convert.ToString(item["semanticAction"]);
                if (!mappings.ContainsKey(action)) mappings[action] = new List<string>();
                mappings[action].Add(id);
            }
            foreach (var pair in AutomationCatalogV11.AutomationIds) Assert(mappings.ContainsKey(pair.Key) && mappings[pair.Key].Contains(pair.Value), "v1.1 canonical mapping matches " + pair.Key);
            Assert(mappings[AutomationCatalogV11.LanguagePracticeSelect].Count == 3, "three language-practice choice automation IDs share one semantic action");
        }

        private static void TestContractParityV12(string path)
        {
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
            Assert(root != null && Convert.ToString(root["contractId"]) == "knowledge-clipper.desktop-automation" && Convert.ToString(root["contractVersion"]) == AutomationCatalogV12.ContractVersion, "v1.2 additive contract identity");
            var values = ((IEnumerable)root["automationIds"]).Cast<object>().Select(item => item as IDictionary<string, object>).ToArray();
            Assert(values.Length == 1 && Convert.ToString(values[0]["automationId"]) == AutomationCatalogV12.InlineQuestionInputAutomationId && values[0]["semanticAction"] == null, "v1.2 adds only the stable neutral inline TextBox automation ID");
            Assert(((IEnumerable)root["semanticActions"]).Cast<object>().Count() == 0, "v1.2 adds no semantic action or private business path");
        }

        private static void TestCommonContractAdapter()
        {
            var command = CommonContractAdapter.BuildSubmitCommand("desktop-command-0001", "desktop-operation-0001", "commands/desktop-command-0001/payload.json", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));
            Assert(command.schemaVersion == "1.0.0" && command.type == "task.submit" && command.taskId == null, "submit command maps common contract");
            Assert(!command.ToWireObject().ContainsKey("expectedState"), "optional null command property omitted on wire");
            var query = CommonContractAdapter.BuildRecentQuery("desktop-query-0001", 10);
            Assert(query.type == "task.query_recent" && query.limit == 10, "recent query maps common contract");
            Assert(!query.ToWireObject().ContainsKey("taskId") && !query.ToWireObject().ContainsKey("statusFilter"), "optional null query properties omitted on wire");
            var questionGet = CommonContractAdapter.BuildTaskGetV22("desktop-query-question-0001", "desktop-task-0001");
            Assert(questionGet.schemaVersion == "2.2.0" && questionGet.type == "task.get" && Convert.ToString(questionGet.ToWireObject()["taskId"]) == "desktop-task-0001" && !questionGet.ToWireObject().ContainsKey("limit"), "question workflow uses the v2.2 task.get contract");
            var activeQuestionGet = CommonContractAdapter.BuildTaskGetV24("desktop-query-question-0002", "desktop-task-0001");
            Assert(activeQuestionGet.schemaVersion == "2.4.0" && activeQuestionGet.type == "task.get" && Convert.ToString(activeQuestionGet.ToWireObject()["taskId"]) == "desktop-task-0001", "active question workflow uses only the task-scoped v2.4 task.get contract");
            var ocrRecent = CommonContractAdapter.BuildRecentQueryV23("desktop-query-ocr-0001", 20);
            Assert(ocrRecent.schemaVersion == "2.3.0" && ocrRecent.statusFilter.Count == 11 && ocrRecent.ToWireObject().ContainsKey("statusFilter"), "OCR-aware recent query uses the exact v2.3 status-filtered shape");
            var receipt = CommonContractAdapter.MapReceipt(new CommonTaskCommandReceiptV1 { schemaVersion = "1.0.0", accepted = true, taskId = "desktop-task-0001" });
            Assert(receipt.accepted && receipt.taskId == "desktop-task-0001", "receipt projection");
            var result = new CommonTaskQueryResultV1
            {
                schemaVersion = "1.0.0",
                items = new List<CommonTaskQueryItemV1> { new CommonTaskQueryItemV1 { taskId = "desktop-task-0001", status = "waiting_input", attentionRequired = true } }
            };
            var recent = CommonContractAdapter.MapRecent(result);
            Assert(recent.available && recent.displayState == "awaiting-user" && recent.attentionRequired, "common status and attention requirement projected to desktop semantics");
            var token = new string('a', 64);
            var ocrItem = new CommonTaskQueryItemV1
            {
                taskId = "desktop-task-ocr-0001", status = "waiting_input", attentionRequired = true, decisionKind = "ocr-human-resolution", decisionTargetToken = token,
                ocrHumanResolution = new CommonOcrHumanResolutionProjectionV1
                {
                    decisionKind = "ocr-human-resolution", targetToken = token, sourceProofreadRef = Reference("proof", "ocr-proofread-bundle"),
                    originalTextRef = Reference("original", "ocr.original.text"), candidateTextRef = Reference("candidate", "ocr.candidate.text"),
                    diff = new object[0], reason = new object[] { new Dictionary<string, object> { { "reason", "insufficient_context" } } }, allowedActions = new[] { "accept_original", "submit_correction", "cancel" }
                }
            };
            var ocrProjection = CommonContractAdapter.MapRecent(new CommonTaskQueryResultV1 { schemaVersion = "2.3.0", items = new List<CommonTaskQueryItemV1> { ocrItem } });
            Assert(ocrProjection.ocrHumanResolution != null && ocrProjection.ocrHumanResolution.targetToken == token && ocrProjection.ocrHumanResolution.reasonJson.Contains("insufficient_context"), "v2.3 query projects only the target-bound OCR resolution session and inline diagnostics");
            ocrItem.attentionRequired = false;
            Assert(CommonContractAdapter.MapRecent(new CommonTaskQueryResultV1 { schemaVersion = "2.3.0", items = new List<CommonTaskQueryItemV1> { ocrItem } }).ocrHumanResolution == null, "OCR resolution projection fails closed without attentionRequired");
            Assert(CommonContractAdapter.MapStatus("retry_wait") == "queued" && CommonContractAdapter.MapStatus("cancelling") == "running", "non-terminal status projection");
        }

        private static void TestQuestionAnswerArtifactReader()
        {
            var root = Path.Combine(Path.GetTempPath(), "KnowledgeClipper-answer-reader-" + Guid.NewGuid().ToString("N"));
            var jobs = Path.Combine(root, "jobs"); var taskId = "task-question-answer-0001"; var job = Path.Combine(jobs, taskId);
            var payloadRelative = "runs/r1/outputs/question-answer/v0001/answer.md";
            var manifestRelative = "runs/r1/outputs/question-answer/v0001/artifact.json";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(job, payloadRelative.Replace('/', Path.DirectorySeparatorChar))));
                var payload = new UTF8Encoding(false).GetBytes("第一条经过校验的答案。\r\n第二行。");
                File.WriteAllBytes(Path.Combine(job, payloadRelative.Replace('/', Path.DirectorySeparatorChar)), payload);
                var serializer = new JavaScriptSerializer();
                var ancestryPayloadRelative = "runs/r1/outputs/question-answer/v0001/structured.json";
                var ancestryManifestRelative = "runs/r1/outputs/question-answer/v0001/structured-artifact.json";
                var ancestryPayload = new UTF8Encoding(false).GetBytes("{\"schemaVersion\":\"question.answer.v1\",\"answer\":\"structured\"}");
                File.WriteAllBytes(Path.Combine(job, ancestryPayloadRelative.Replace('/', Path.DirectorySeparatorChar)), ancestryPayload);
                var ancestryManifest = new Dictionary<string, object>
                {
                    { "schemaVersion", "1.0.0" }, { "artifactId", "question.answer.structured.0001" }, { "kind", "question.answer" }, { "contractVersion", "1.0.0" }, { "mediaType", "application/json" },
                    { "files", new object[] { new Dictionary<string, object> { { "path", ancestryPayloadRelative }, { "sha256", Sha256(ancestryPayload) }, { "bytes", ancestryPayload.LongLength }, { "mediaType", "application/json" } } } },
                    { "createdAt", "2026-08-26T00:00:00Z" }, { "producer", new Dictionary<string, object>() }, { "inputRefs", new object[0] }, { "dataClass", "personal" },
                    { "metadata", new Dictionary<string, object> { { "entrypoint", ancestryPayloadRelative } } }
                };
                var ancestryManifestBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(ancestryManifest));
                File.WriteAllBytes(Path.Combine(job, ancestryManifestRelative.Replace('/', Path.DirectorySeparatorChar)), ancestryManifestBytes);
                var manifest = new Dictionary<string, object>
                {
                    { "schemaVersion", "1.0.0" }, { "artifactId", "question.answer.text.0001" }, { "kind", "question.answer.text" }, { "contractVersion", "1.0.0" }, { "mediaType", "text/plain" },
                    { "files", new object[] { new Dictionary<string, object> { { "path", payloadRelative }, { "sha256", Sha256(payload) }, { "bytes", payload.LongLength }, { "mediaType", "text/plain" } } } },
                    { "createdAt", "2026-08-26T00:00:00Z" }, { "producer", new Dictionary<string, object>() },
                    { "inputRefs", new object[] { new Dictionary<string, object> { { "artifactRef", "question.answer.structured.0001" }, { "kind", "question.answer" }, { "contractVersion", "1.0.0" }, { "manifestPath", ancestryManifestRelative }, { "expectedSha256", Sha256(ancestryManifestBytes) } } } }, { "dataClass", "personal" },
                    { "metadata", new Dictionary<string, object> { { "entrypoint", payloadRelative } } }
                };
                var manifestBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(manifest));
                var manifestPath = Path.Combine(job, manifestRelative.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllBytes(manifestPath, manifestBytes);
                var reference = new QuestionAnswerArtifactReference { artifactRef = "question.answer.text.0001", kind = "question.answer.text", contractVersion = "1.0.0", manifestPath = manifestRelative, expectedSha256 = Sha256(manifestBytes) };
                var reader = new FileQuestionAnswerArtifactReader(jobs);
                var valid = reader.Read(taskId, reference);
                Assert(valid.ready && valid.text == "第一条经过校验的答案。\r\n第二行。", "verified manifest and payload bytes are displayed unchanged");
                var structuredReference = reference.Copy(); structuredReference.kind = "question.answer";
                Assert(reader.Read(taskId, structuredReference).noticeCode == "question.answer.reference-invalid", "desktop refuses to parse structured question.answer JSON directly");

                var absolute = reference.Copy(); absolute.manifestPath = manifestPath;
                Assert(reader.Read(taskId, absolute).noticeCode == "question.answer.manifest-path-invalid", "absolute manifest path is rejected");
                var traversal = reference.Copy(); traversal.manifestPath = "../outside/artifact.json";
                Assert(reader.Read(taskId, traversal).noticeCode == "question.answer.manifest-path-invalid", "manifest traversal is rejected");
                var manifestMismatch = reference.Copy(); manifestMismatch.expectedSha256 = new string('0', 64);
                Assert(reader.Read(taskId, manifestMismatch).noticeCode == "question.answer.manifest-hash-mismatch", "manifest hash mismatch is rejected");

                var altered = (byte[])payload.Clone(); altered[altered.Length - 1] ^= 1; File.WriteAllBytes(Path.Combine(job, payloadRelative.Replace('/', Path.DirectorySeparatorChar)), altered);
                Assert(reader.Read(taskId, reference).noticeCode == "question.answer.payload-hash-mismatch", "payload hash mismatch is rejected");
                File.WriteAllBytes(Path.Combine(job, payloadRelative.Replace('/', Path.DirectorySeparatorChar)), payload.Take(payload.Length - 1).ToArray());
                Assert(reader.Read(taskId, reference).noticeCode == "question.answer.payload-bytes-mismatch", "payload byte count mismatch is rejected");

                var pathEscapeManifest = new Dictionary<string, object>(manifest);
                pathEscapeManifest["files"] = new object[] { new Dictionary<string, object> { { "path", "../../outside.md" }, { "sha256", Sha256(payload) }, { "bytes", payload.LongLength }, { "mediaType", "text/plain" } } };
                var pathEscapeBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(pathEscapeManifest)); File.WriteAllBytes(manifestPath, pathEscapeBytes);
                var pathEscapeReference = reference.Copy(); pathEscapeReference.expectedSha256 = Sha256(pathEscapeBytes);
                Assert(reader.Read(taskId, pathEscapeReference).noticeCode == "question.answer.payload-path-invalid", "payload traversal is rejected after manifest verification");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void TestOcrHumanResolutionArtifactReader()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-ocr-resolution-reader-" + Guid.NewGuid().ToString("N"));
            var jobs = Path.Combine(root, "jobs"); var taskId = "task-ocr-resolution-reader";
            try
            {
                var original = WriteTextArtifact(jobs, taskId, "ocr.original.fixture", "ocr.original.text", "OCR 原文");
                var candidate = WriteTextArtifact(jobs, taskId, "ocr.candidate.fixture", "ocr.candidate.text", "AI 候选");
                string markdownPayloadPath;
                var proofread = WriteOcrProvenanceFixture(jobs, taskId, out markdownPayloadPath);
                var reader = new FileOcrHumanResolutionArtifactReader(jobs);
                Assert(reader.ReadText(taskId, original, "ocr.original.text").text == "OCR 原文" && reader.ReadText(taskId, candidate, "ocr.candidate.text").text == "AI 候选", "verified OCR original and candidate text are read unchanged");
                var provenance = reader.ReadProvenance(taskId, proofread);
                Assert(provenance.ready && provenance.markdownText == "# OCR Markdown" && provenance.structuredText.Contains("structured"), "proofread ancestry exposes only hash-verified Markdown and structured OCR payloads");
                var absolute = original.Copy(); absolute.manifestPath = Path.Combine(jobs, taskId, original.manifestPath.Replace('/', Path.DirectorySeparatorChar));
                Assert(reader.ReadText(taskId, absolute, "ocr.original.text").noticeCode == "ocr-resolution.path-invalid", "absolute OCR manifest path is rejected");
                var wrongKind = original.Copy(); wrongKind.kind = "ocr.candidate.text";
                Assert(reader.ReadText(taskId, wrongKind, "ocr.original.text").noticeCode == "ocr-resolution.reference-invalid", "OCR text kind mismatch fails closed");
                var badHash = original.Copy(); badHash.expectedSha256 = new string('0', 64);
                Assert(reader.ReadText(taskId, badHash, "ocr.original.text").noticeCode == "ocr-resolution.manifest-hash-mismatch", "OCR manifest hash mismatch fails closed");
                var manifestPath = Path.Combine(jobs, taskId, original.manifestPath.Replace('/', Path.DirectorySeparatorChar));
                var manifest = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(manifestPath, Encoding.UTF8)) as IDictionary<string, object>;
                var files = ((IEnumerable)manifest["files"]).Cast<object>().Select(value => value as IDictionary<string, object>).ToArray();
                File.AppendAllText(Path.Combine(jobs, taskId, Convert.ToString(files[0]["path"]).Replace('/', Path.DirectorySeparatorChar)), "tampered", new UTF8Encoding(false));
                Assert(reader.ReadText(taskId, original, "ocr.original.text").noticeCode == "ocr-resolution.payload-integrity-mismatch", "OCR payload byte or hash drift fails closed");
                File.AppendAllText(Path.Combine(jobs, taskId, markdownPayloadPath.Replace('/', Path.DirectorySeparatorChar)), "tampered", new UTF8Encoding(false));
                Assert(reader.ReadProvenance(taskId, proofread).noticeCode == "ocr-resolution.payload-integrity-mismatch", "nested Markdown drift prevents the human-review evidence window from opening");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void TestOcrHumanResolutionSemanticActions()
        {
            var token = new string('a', 64);
            var projection = new OcrHumanResolutionProjection
            {
                decisionKind = AutomationCatalogV13.OcrDecisionKind, targetToken = token,
                sourceProofreadRef = Reference("proof", "ocr-proofread-bundle"), originalTextRef = Reference("original", "ocr.original.text"), candidateTextRef = Reference("candidate", "ocr.candidate.text"),
                diffJson = "[]", reasonJson = "[{\"reason\":\"insufficient_context\"}]", allowedActions = new List<string> { "accept_original", "submit_correction", "cancel" }
            };
            var orchestrator = new FakeDesktopOrchestratorPort();
            var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), orchestrator);
            Action setRecent = delegate
            {
                orchestrator.RecentTask = new RecentTaskProjection { available = true, taskId = "task-ocr-resolution", intent = "clip.card", displayState = "awaiting-user", attentionRequired = true, decisionKind = AutomationCatalogV13.OcrDecisionKind, decisionTargetToken = token, ocrHumanResolution = projection.Copy() };
                controller.Execute(ActionV11(AutomationCatalog.TaskStatusQueryRecent));
            };
            setRecent();
            var accepted = controller.Execute(ActionV13(AutomationCatalogV13.OcrAcceptOriginal));
            Assert(accepted.noticeCode == "ocr-human-resolution.input-submitted" && accepted.recentTask.displayState == "queued" && orchestrator.OcrHumanResolutionInputs.Last().Contains(":accept_original::"), "accept-original is a stable semantic action and closes only the projected OCR decision");
            setRecent();
            var corrected = controller.Execute(ActionV13(AutomationCatalogV13.OcrSubmitCorrection, new SemanticActionArguments { text = "CANARY", candidateText = "CANAR" }));
            Assert(corrected.noticeCode == "ocr-human-resolution.input-submitted" && orchestrator.OcrHumanResolutionInputs.Last().EndsWith(":submit_correction:CANARY:CANAR"), "edited continuation forwards verified candidate and exact corrected text");
            setRecent();
            var cancelled = controller.Execute(ActionV13(AutomationCatalogV13.OcrCancel));
            Assert(cancelled.noticeCode == "ocr-human-resolution.cancel-submitted" && cancelled.recentTask.displayState == "cancelled" && orchestrator.OcrHumanResolutionInputs.Last().Contains(":cancel::"), "OCR cancel is a durable semantic input rather than a local fake success");
            orchestrator.RecentTask.attentionRequired = false; controller.Execute(ActionV11(AutomationCatalog.TaskStatusQueryRecent));
            var rejected = controller.Execute(ActionV13(AutomationCatalogV13.OcrAcceptOriginal));
            Assert(rejected.noticeCode == "ocr-human-resolution.decision-not-projected" && orchestrator.OcrHumanResolutionInputs.Count == 3, "OCR actions fail closed without waiting_input attentionRequired projection");
        }

        private static void TestOcrHumanResolutionCommandPayloads()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-ocr-resolution-command-" + Guid.NewGuid().ToString("N"));
            var jobs = Path.Combine(root, "jobs"); var taskId = "task-ocr-command"; Directory.CreateDirectory(Path.Combine(jobs, taskId));
            try
            {
                var source = new FileDesktopCommonCommandSource(jobs, new FakeClock(new DateTime(2026, 8, 30, 11, 30, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider());
                var token = new string('b', 64);
                var corrected = source.PrepareOcrHumanResolution(taskId, token, "submit_correction", "B😀", "A😀");
                Assert(corrected.type == "task.provide_input", "OCR correction reuses common task.provide_input");
                var payload = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(jobs, taskId, corrected.payloadRef.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8)) as IDictionary<string, object>;
                Assert(payload.Count == 7 && Convert.ToString(payload["schemaVersion"]) == "knowledge-clipper-ocr-human-resolution-action.v1" && Convert.ToString(payload["decisionKind"]) == "ocr-human-resolution" && Convert.ToString(payload["targetToken"]) == token && Convert.ToString(payload["action"]) == "submit_correction", "OCR correction payload has the exact public identity and opaque Core token");
                var correctionRef = payload["correctionRef"] as IDictionary<string, object>;
                Assert(correctionRef != null && Convert.ToString(correctionRef["kind"]) == "ocr.human-correction.text" && Convert.ToString(correctionRef["contractVersion"]) == "1.0.0", "edited text is published as one immutable correction artifact reference");
                var diffs = ((IEnumerable)payload["diffs"]).Cast<object>().Select(value => value as IDictionary<string, object>).ToArray();
                Assert(diffs.Length == 1 && Convert.ToString(diffs[0]["operation"]) == "substitution" && Convert.ToInt32(diffs[0]["end"]) == 2 && Convert.ToString(diffs[0]["before"]) == "A😀" && Convert.ToString(diffs[0]["after"]) == "B😀", "correction diff uses Unicode scalar indices and exact before/after text");
                var accept = source.PrepareOcrHumanResolution(taskId, token, "accept_original", null, null);
                var acceptPayload = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(jobs, taskId, accept.payloadRef.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8)) as IDictionary<string, object>;
                Assert(acceptPayload.Count == 5 && !acceptPayload.ContainsKey("correctionRef") && !acceptPayload.ContainsKey("diffs"), "accept-original forbids correction fields");
                var cancel = source.PrepareOcrHumanResolution(taskId, token, "cancel", null, null);
                var cancelPayload = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(jobs, taskId, cancel.payloadRef.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8)) as IDictionary<string, object>;
                Assert(cancelPayload.Count == 5 && Convert.ToString(cancelPayload["action"]) == "cancel" && !cancelPayload.ContainsKey("correctionRef"), "cancel is one target-bound public payload with no task.cancel substitution");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void TestWholeTaskCancelRemainsExplicit()
        {
            var root = Path.Combine(Path.GetTempPath(), "rqc-desktop-task-cancel-" + Guid.NewGuid().ToString("N"));
            var jobsRoot = Path.Combine(root, "jobs");
            var taskId = "task-explicit-cancel";
            Directory.CreateDirectory(Path.Combine(jobsRoot, taskId));
            try
            {
                var source = new FileDesktopCommonCommandSource(jobsRoot, new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider());
                var command = source.PrepareTaskCancel(taskId);
                Assert(command.type == "task.cancel", "explicit whole-task cancellation keeps common task.cancel");
                var payloadPath = Path.Combine(jobsRoot, taskId, command.payloadRef.Replace('/', Path.DirectorySeparatorChar));
                var payload = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(payloadPath, Encoding.UTF8)) as IDictionary<string, object>;
                Assert(payload != null && Convert.ToString(payload["reason"]) == "task_cancel", "whole-task cancellation is not mislabeled as playback stop");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void TestContextFailClosed()
        {
            var capture = new FakeCaptureProvider(new[] { TextCapture("clipboard-text", false) });
            var orchestrator = new FakeDesktopOrchestratorPort();
            var controller = Controller(capture, new FakeScreenshotProvider(null), orchestrator);
            controller.Execute(Action(AutomationCatalog.ContextSet, new SemanticActionArguments { enabled = true, granularity = "sentence" }));
            var state = controller.Execute(Action(AutomationCatalog.ClipCapture));
            Assert(state.foregroundState == "context-required", "context unavailable stops");
            Assert(orchestrator.SubmittedBundles.Count == 0, "no silent submission");
            state = controller.Execute(Action(AutomationCatalog.ContextContinueWithout));
            Assert(state.foregroundState == "idle", "explicit downgrade completes");
            Assert(orchestrator.SubmittedBundles.Count == 1, "explicit downgrade submits");
            var bundle = orchestrator.SubmittedBundles[0];
            Assert(bundle.source == "clipboard-text", "fallback source preserved");
            Assert(bundle.modeSnapshot.contextEnabled && !bundle.modeSnapshot.contextApplied, "mode snapshot records non-applied context");
        }

        private static void TestMultiScreenshotGroups()
        {
            var screenshots = new FakeScreenshotProvider(new[]
            {
                Shot("capture.image.question.0001", "1111111111111111111111111111111111111111111111111111111111111111"),
                Shot("capture.image.answer.0001", "2222222222222222222222222222222222222222222222222222222222222222"),
                Shot("capture.image.question.0002", "3333333333333333333333333333333333333333333333333333333333333333")
            });
            var orchestrator = new FakeDesktopOrchestratorPort();
            var controller = Controller(new FakeCaptureProvider(null), screenshots, orchestrator);
            controller.Execute(Action(AutomationCatalog.OriginalImageSet, new SemanticActionArguments { enabled = true }));
            controller.Execute(Action(AutomationCatalog.ScreenshotStart));
            controller.Execute(Action(AutomationCatalog.OriginalImageSet, new SemanticActionArguments { enabled = false }));
            controller.Execute(Action(AutomationCatalog.ScreenshotGroupAnswer));
            controller.Execute(Action(AutomationCatalog.ScreenshotContinue));
            controller.Execute(Action(AutomationCatalog.ScreenshotGroupQuestion));
            var review = controller.Execute(Action(AutomationCatalog.ScreenshotContinue));
            Assert(review.screenshotSession.questionImageCount == 2 && review.screenshotSession.answerImageCount == 1, "visible grouped counts");
            controller.Execute(Action(AutomationCatalog.ScreenshotFinish));
            Assert(orchestrator.SubmittedBundles.Count == 1, "one screenshot bundle");
            var bundle = orchestrator.SubmittedBundles[0];
            Assert(bundle.questionImages.SequenceEqual(new[] { "capture.image.question.0001", "capture.image.question.0002" }), "question order explicit");
            Assert(bundle.answerImages.SequenceEqual(new[] { "capture.image.answer.0001" }), "answer order explicit");
            Assert(bundle.modeSnapshot.imageHandling == "original", "mode frozen at screenshot session start");
            Assert(!bundle.modeSnapshot.contextEnabled && !bundle.modeSnapshot.contextApplied, "screenshot never claims context mode");
            Assert(screenshots.RequestedGroups.SequenceEqual(new[] { "question", "answer", "question" }), "provider receives explicit groups");
        }

        private static void TestCaptureBundleInvariants()
        {
            var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(new[] { Shot("capture.image.question.0001", "1111111111111111111111111111111111111111111111111111111111111111") }), new FakeDesktopOrchestratorPort());
            controller.Execute(Action(AutomationCatalog.ScreenshotStart));
            var orchestrator = new FakeDesktopOrchestratorPort();
            controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(new[] { Shot("capture.image.question.0001", "1111111111111111111111111111111111111111111111111111111111111111") }), orchestrator);
            controller.Execute(Action(AutomationCatalog.ScreenshotStart));
            controller.Execute(Action(AutomationCatalog.ScreenshotFinish));
            var valid = orchestrator.SubmittedBundles.Single();
            CaptureBundleValidator.Validate(valid);
            valid.answerImages.Add(valid.questionImages[0]);
            AssertThrows("capture-bundle.image-groups-overlap", delegate { CaptureBundleValidator.Validate(valid); });

            var boundaryRef = "capture." + new string('a', 120);
            Assert(boundaryRef.Length == 128, "common identifier boundary fixture length");
            var boundaryBundle = ScreenshotBundle(boundaryRef);
            CaptureBundleValidator.Validate(boundaryBundle);

            var incompatibleRef = boundaryRef + "a";
            Assert(incompatibleRef.Length == 129, "common identifier overflow fixture length");
            var incompatibleOrchestrator = new FakeDesktopOrchestratorPort();
            controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(new[] { Shot(incompatibleRef, "4444444444444444444444444444444444444444444444444444444444444444") }), incompatibleOrchestrator);
            controller.Execute(Action(AutomationCatalog.ScreenshotStart));
            AssertThrows("capture-bundle.artifact-ref-common-incompatible", delegate { controller.Execute(Action(AutomationCatalog.ScreenshotFinish)); });
            Assert(incompatibleOrchestrator.SubmittedBundles.Count == 0, "desktop entry rejects common-incompatible capture ref before submission");
        }

        private static void TestScreenshotCancelAndUndo()
        {
            var screenshots = new FakeScreenshotProvider(new[]
            {
                Shot("capture.image.question.0001", "1111111111111111111111111111111111111111111111111111111111111111"),
                Shot("capture.image.question.0002", "2222222222222222222222222222222222222222222222222222222222222222")
            });
            var orchestrator = new FakeDesktopOrchestratorPort();
            var controller = Controller(new FakeCaptureProvider(null), screenshots, orchestrator);
            controller.Execute(Action(AutomationCatalog.ScreenshotStart));
            controller.Execute(Action(AutomationCatalog.ScreenshotContinue));
            var state = controller.Execute(Action(AutomationCatalog.ScreenshotUndoLast));
            Assert(state.screenshotSession.questionImageCount == 1, "undo only last frame");
            state = controller.Execute(Action(AutomationCatalog.ScreenshotCancel));
            Assert(!state.screenshotSession.active && state.foregroundState == "idle", "cancel clears active state");
            Assert(orchestrator.SubmittedBundles.Count == 0, "cancel does not submit");
        }

        private static void TestCaptureArtifactMaterialization()
        {
            var root = Path.Combine(Path.GetTempPath(), "rqc-desktop-materialize-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var sourcePath = Path.Combine(root, "staged.png"); var bytes = new byte[] { 1, 2, 3, 4, 5 }; File.WriteAllBytes(sourcePath, bytes);
                var hash = BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                var bundle = ScreenshotBundle("capture.image.materialized");
                bundle.artifacts[0].relativePath = "inputs/capture/materialized.png"; bundle.artifacts[0].sha256 = hash; bundle.artifacts[0].bytes = bytes.Length; bundle.artifacts[0].sourcePath = sourcePath;
                var jobs = Path.Combine(root, "jobs");
                new FileDesktopCommonCommandSource(jobs, new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider()).PrepareCapture(bundle);
                var materialized = Directory.GetFiles(jobs, "materialized.png", SearchOption.AllDirectories);
                Assert(materialized.Length == 1 && File.ReadAllBytes(materialized[0]).SequenceEqual(bytes), "staged capture artifact copied exactly once");
                var bundleJson = File.ReadAllText(Directory.GetFiles(jobs, "capture-bundle.v1.json", SearchOption.AllDirectories).Single());
                Assert(!bundleJson.Contains("sourcePath"), "desktop-only staging path excluded from capture contract payload");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void TestQuestionFlow()
        {
            var orchestrator = new FakeDesktopOrchestratorPort();
            var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), orchestrator);
            controller.Execute(Action(AutomationCatalog.QuestionOpen));
            var state = controller.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = "问题" }));
            Assert(state.foregroundState == "question-answer-ready" && state.questionSession.turnCount == 1, "answer ready");
            var duplicate = controller.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = "错误的新初问" }));
            Assert(duplicate.noticeCode == "question.follow-up-required" && orchestrator.Questions.SequenceEqual(new[] { "question:问题" }), "an answer-ready session rejects a second initial question instead of creating another task");
            state = controller.Execute(Action(AutomationCatalog.QuestionFollowUp, new SemanticActionArguments { text = "追问" }));
            Assert(state.questionSession.turnCount == 2, "follow-up appended");
            state = controller.Execute(Action(AutomationCatalog.QuestionMakeCards, new SemanticActionArguments { cardStructureRef = "fixture.card-structure.basic" }));
            Assert(state.foregroundState == "idle" && !state.questionSession.open, "cards closes question session");
            Assert(orchestrator.Questions.SequenceEqual(new[] { "question:问题", "follow-up:追问" }), "turn order preserved");
            Assert(orchestrator.CardStructureRefs.Single() == "fixture.card-structure.basic", "opaque structure reference forwarded");

            var runningPort = new FakeDesktopOrchestratorPort { AnswerImmediately = false };
            var runningController = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), runningPort);
            runningController.Execute(Action(AutomationCatalog.QuestionOpen));
            runningController.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = "运行中的问题" }));
            var whileRunning = runningController.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = "重复提交" }));
            Assert(whileRunning.noticeCode == "question.submission-in-progress" && runningPort.Questions.SequenceEqual(new[] { "question:运行中的问题" }), "running session rejects duplicate initial submission before another command is prepared");
        }

        private static void TestQuestionCancelAndLanguagePractice()
        {
            var orchestrator = new FakeDesktopOrchestratorPort();
            var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), orchestrator);
            controller.Execute(Action(AutomationCatalog.QuestionOpen));
            var state = controller.CloseQuestionInteraction();
            Assert(state.foregroundState == "idle" && !state.questionSession.open && state.noticeCode == "question.dialog-cancelled", "question dialog cancellation restores idle without cancelling a task");
            Assert(orchestrator.Questions.Count == 0 && orchestrator.LanguagePracticeInputs.Count == 0, "local question cancellation emits no orchestrator command");

            state = controller.Execute(ActionV11(AutomationCatalogV11.LanguagePracticeSelect, new SemanticActionArguments { selection = AutomationCatalogV11.LanguagePracticeBoth }));
            Assert(state.noticeCode == "language-practice.decision-not-projected" && orchestrator.LanguagePracticeInputs.Count == 0, "desktop never guesses a language-practice decision");

            var choices = new[] { AutomationCatalogV11.LanguagePracticeMeaningReading, AutomationCatalogV11.LanguagePracticeWriting, AutomationCatalogV11.LanguagePracticeBoth };
            var targetToken = new string('a', 64);
            foreach (var choice in choices)
            {
                orchestrator.RecentTask = new RecentTaskProjection { available = true, taskId = "task-language-" + choice, displayState = "awaiting-user", decisionKind = AutomationCatalogV11.LanguagePracticeDecisionKind, decisionTargetToken = targetToken };
                state = controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
                Assert(state.recentTask.decisionKind == AutomationCatalogV11.LanguagePracticeDecisionKind && state.recentTask.decisionTargetToken == targetToken, "explicit decision kind and target token are projected without desktop classification");
                state = controller.Execute(ActionV11(AutomationCatalogV11.LanguagePracticeSelect, new SemanticActionArguments { selection = choice }));
                Assert(state.noticeCode == "language-practice.input-submitted" && state.lastAction == AutomationCatalogV11.LanguagePracticeSelect && state.recentTask.decisionKind == null, "accepted selection is opaque task input and closes the decision prompt");
            }
            Assert(orchestrator.LanguagePracticeInputs.SequenceEqual(choices.Select(choice => "task-language-" + choice + ":" + targetToken + ":" + choice)), "all three fixed selections and projected target tokens reach the fake port unchanged");

            orchestrator.RecentTask = new RecentTaskProjection { available = true, taskId = "task-language-invalid", displayState = "awaiting-user", decisionKind = AutomationCatalogV11.LanguagePracticeDecisionKind, decisionTargetToken = new string('b', 64) };
            controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
            state = controller.Execute(ActionV11(AutomationCatalogV11.LanguagePracticeSelect, new SemanticActionArguments { selection = "desktop-guessed-choice" }));
            Assert(state.noticeCode == "language-practice.selection-invalid" && orchestrator.LanguagePracticeInputs.Count == 3, "unknown selection fails closed before the port");

            orchestrator.RecentTask = new RecentTaskProjection { available = true, taskId = "task-language-bad-token", displayState = "awaiting-user", decisionKind = AutomationCatalogV11.LanguagePracticeDecisionKind, decisionTargetToken = new string('A', 64) };
            controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
            state = controller.Execute(ActionV11(AutomationCatalogV11.LanguagePracticeSelect, new SemanticActionArguments { selection = AutomationCatalogV11.LanguagePracticeBoth }));
            Assert(state.noticeCode == "language-practice.decision-not-projected" && orchestrator.LanguagePracticeInputs.Count == 3, "non-canonical target token fails closed before the port");
        }

        private static void TestPlaybackAndRecentTask()
        {
            var orchestrator = new FakeDesktopOrchestratorPort { RecentTask = new RecentTaskProjection { available = true, taskId = "task-recent", displayState = "awaiting-user", attentionRequired = true } };
            var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), orchestrator);
            var state = controller.Execute(Action(AutomationCatalog.PlaybackReplay));
            Assert(state.playback == "playing", "replay state");
            state = controller.Execute(Action(AutomationCatalog.PlaybackStop));
            Assert(state.playback == "stopped", "stop state");
            state = controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
            Assert(state.recentTask.available && state.recentTask.taskId == "task-recent" && state.recentTask.displayState == "awaiting-user" && state.recentTask.attentionRequired, "recent task projection preserves attention requirement internally");
        }

        private static void TestSemanticTestPort()
        {
            var port = new SemanticTestPort(Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), new FakeDesktopOrchestratorPort()));
            var bad = port.Execute(new SemanticActionEnvelope { schemaVersion = "wrong", requestId = "x", action = AutomationCatalog.ClipCapture });
            Assert(!bad.ok && bad.errorCode == "test-port.unsupported-schema-version", "schema rejected");
            var good = port.Execute(Action(AutomationCatalog.ContextSet, new SemanticActionArguments { enabled = true, granularity = "paragraph" }));
            Assert(good.ok && good.state.contextMode.enabled && good.state.contextMode.granularity == "paragraph", "semantic action drives real controller");
        }

        private static void TestNamedPipeSemanticActions()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "rqc-desktop-pipe-" + Guid.NewGuid().ToString("N"));
            var jobsRoot = Path.Combine(testRoot, "jobs");
            Directory.CreateDirectory(jobsRoot);
            var pipeName = "RqcDesktopFake." + Guid.NewGuid().ToString("N");
            var server = new FakeNamedPipeOrchestratorServer(pipeName, jobsRoot, 11);
            server.Start();
            try
            {
                var nextCorrelation = 0;
                var client = new NamedPipeCommonIpcClient(pipeName, 5000, delegate { nextCorrelation++; return "desktop-correlation-" + nextCorrelation.ToString("D4"); });
                var source = new FileDesktopCommonCommandSource(jobsRoot, new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider());
                var controller = Controller(
                    new FakeCaptureProvider(new[] { TextCapture("selection", false) }),
                    new FakeScreenshotProvider(null),
                    new NamedPipeDesktopOrchestratorPort(client, source));

                var state = controller.Execute(Action(AutomationCatalog.ClipCapture));
                Assert(state.recentTask.available && state.recentTask.displayState == "queued", "capture semantic action submits through real pipe client");

                controller.Execute(Action(AutomationCatalog.QuestionOpen));
                state = controller.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = "管道问题" }));
                Assert(state.foregroundState == "question-awaiting-answer", "question submit waits for durable query state");
                state = controller.RefreshActiveQuestion();
                Assert(state.foregroundState == "question-answer-ready" && state.questionSession.answerAvailable, "query result unlocks follow-up semantically");
                var firstAnswer = new FileQuestionAnswerArtifactReader(jobsRoot).Read(state.recentTask.taskId, state.questionSession.answerArtifact);
                Assert(firstAnswer.ready && firstAnswer.text == "fake answer 1", "v2.2 questionAnswerRef is verified and read from the current task package");

                state = controller.Execute(Action(AutomationCatalog.QuestionFollowUp, new SemanticActionArguments { text = "管道追问" }));
                Assert(state.foregroundState == "question-awaiting-answer", "follow-up travels as immutable provide-input command");
                state = controller.RefreshActiveQuestion();
                Assert(state.questionSession.answerAvailable, "follow-up completion re-enters answer-ready state");
                var secondAnswer = new FileQuestionAnswerArtifactReader(jobsRoot).Read(state.recentTask.taskId, state.questionSession.answerArtifact);
                Assert(secondAnswer.ready && secondAnswer.text == "fake answer 2", "follow-up query replaces the projected answer with the next verified text artifact");
                WriteCardStructureArtifact(jobsRoot, state.recentTask.taskId, "fixture.card-structure.pipe");
                state = controller.Execute(Action(AutomationCatalog.QuestionMakeCards, new SemanticActionArguments { cardStructureRef = "fixture.card-structure.pipe" }));
                Assert(state.foregroundState == "idle", "make-cards command accepted through pipe");

                state = controller.Execute(Action(AutomationCatalog.PlaybackReplay));
                Assert(state.playback == "replay-available", "replay command acknowledged without claiming playback started");
                state = controller.Execute(Action(AutomationCatalog.PlaybackStop));
                Assert(state.playback == "stop-requested", "accepted stage-scoped stop is projected without claiming terminal playback state");
                Assert(state.noticeCode == "playback.stop-requested", "playback stop uses the public v2.3 stage-control receipt");
                state = controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
                Assert(state.recentTask.available && state.recentTask.displayState == "awaiting-user", "recent task query maps common status over pipe");

                server.Complete();
                Assert(server.MessageTypes.Count == 11, "task-scoped question queries, preflights and stage-scoped playback stop are explicit IPC requests");
                Assert(server.CommandTypes.SequenceEqual(new[] { "task.submit", "task.submit", "task.provide_input", "task.provide_input", "task.provide_input" }), "playback stop uses direct stage-control and never task.cancel");
                Assert(server.MessageTypes.Count(value => value == "stage-control") == 1, "playback stop uses the append-only direct stage-control IPC message");
                Assert(server.StageControlPayloads.Count == 1 && Convert.ToString(server.StageControlPayloads[0]["operation"]) == "speech.playback" && Convert.ToString(server.StageControlPayloads[0]["selector"]) == "current-active" && Convert.ToString(server.StageControlPayloads[0]["reason"]) == "user_stop", "desktop publishes the exact v2.3 playback selector without run or attempt guesses");
                Assert(server.QueryTypes.Zip(server.QuerySchemaVersions, (type, version) => new { type, version }).All(item => item.type == "task.query_recent" ? item.version == "2.3.0" : item.version == "2.4.0") && server.QueryTypes.Count(value => value == "task.query_recent") == 1 && server.QueryTypes.Count(value => value == "task.get") == 4, "active question refresh and action preflights use only v2.4 task.get while the later global status action alone uses query_recent v2.3");
                Assert(server.QuestionActionPayloads.Count == 2 && Convert.ToString(server.QuestionActionPayloads[0]["action"]) == "question.follow-up" && Convert.ToString(server.QuestionActionPayloads[1]["action"]) == "question.make-cards", "follow-up and make-cards use only public question-action.v2 payloads");
                var followUpRef = (IDictionary<string, object>)server.QuestionActionPayloads[0]["questionTurnRef"];
                Assert(Convert.ToString(followUpRef["artifactRef"]) == "question.turn.0002" && Convert.ToString(followUpRef["manifestPath"]) == "artifacts/question.turn.0002/artifact.json", "follow-up publishes the canonical ordinal question.turn artifact identity");
                var followManifestPath = Path.Combine(jobsRoot, state.recentTask.taskId, "artifacts", "question.turn.0002", "artifact.json");
                var followDocument = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(followManifestPath));
                var followFiles = ((IEnumerable)followDocument["files"]).Cast<object>().ToArray();
                var payloadPath = Convert.ToString(((IDictionary<string, object>)followFiles[0])["path"]);
                Assert(payloadPath.StartsWith("commands/command-", StringComparison.Ordinal) && payloadPath.EndsWith("/question-turn/payload.txt", StringComparison.Ordinal), "follow-up text remains owned by its accepted command directory");
                Assert(Directory.GetFiles(jobsRoot, "desktop-action.json", SearchOption.AllDirectories).All(path => { var value = File.ReadAllText(path); return !value.Contains("question.follow-up") && !value.Contains("question.make-cards"); }), "new question workflow never uses legacy desktop-action.v1 payloads");
                Assert(!server.SawBom && !server.SawCrLf, "desktop emits UTF-8 without BOM and LF delimiter");
                Assert(Directory.GetFiles(jobsRoot, "task.manifest.v1.json", SearchOption.AllDirectories).Length == 2, "capture and question immutable task packages created");
            }
            finally
            {
                if (Directory.Exists(testRoot) && Path.GetFileName(testRoot).StartsWith("rqc-desktop-pipe-", StringComparison.Ordinal)) Directory.Delete(testRoot, true);
            }
        }

        private static void TestNamedPipeModuleActions()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-module-action-pipe-" + Guid.NewGuid().ToString("N"));
            var jobs = Path.Combine(root, "jobs"); Directory.CreateDirectory(jobs);
            try
            {
                var pipe = "KnowledgeClipperModuleAction." + Guid.NewGuid().ToString("N");
                var server = new FakeNamedPipeOrchestratorServer(pipe, jobs, 9); server.Start();
                var client = new NamedPipeCommonIpcClient(pipe, 5000);
                var operations = new[] { "speech.voices", "speech.preview", "remnote.probe" };
                var receipts = new List<CommonModuleActionReceiptV1>();
                foreach (var operation in operations)
                {
                    var actionId = "test-" + operation.Replace('.', '-'); var payloadRef = "module-actions/" + actionId + "/input.json";
                    var path = Path.Combine(jobs, payloadRef.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var bytes = new UTF8Encoding(false).GetBytes("{\"operation\":\"" + operation + "\"}"); File.WriteAllBytes(path, bytes); var hash = Sha256(bytes);
                    receipts.Add(client.SendModuleAction(new CommonModuleActionRequestV1 { schemaVersion = "knowledge-clipper-module-action.v1", actionId = actionId, operation = operation, payloadRef = payloadRef, idempotencyKey = hash }));
                }
                var discoveryActionId = "test-remnote-discover-target"; var discoveryPayloadRef = "module-actions/" + discoveryActionId + "/input.json";
                var discoveryPath = Path.Combine(jobs, discoveryPayloadRef.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(discoveryPath));
                var discoveryBytes = new UTF8Encoding(false).GetBytes("{\"schemaVersion\":\"remnote-target-discovery-request.v1\",\"selector\":{\"rootTitle\":\"剪藏\",\"match\":\"exact\",\"uniqueness\":\"required\"}}"); File.WriteAllBytes(discoveryPath, discoveryBytes); var discoveryHash = Sha256(discoveryBytes);
                var discoveryReceipt = client.SendModuleActionV2(new CommonModuleActionRequestV2 { schemaVersion = "knowledge-clipper-module-action.v2", actionId = discoveryActionId, operation = "remnote.discover-target", payloadRef = discoveryPayloadRef, idempotencyKey = discoveryHash });
                var v3Receipts = new List<CommonModuleActionReceiptV3>();
                foreach (var operation in operations.Concat(new[] { "remnote.discover-target", "anki.initialize-managed" }))
                {
                    var actionId = "test-v3-" + operation.Replace('.', '-'); var payloadRef = "module-actions/" + actionId + "/input.json";
                    var path = Path.Combine(jobs, payloadRef.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var json = operation == "anki.initialize-managed"
                        ? "{\"schemaVersion\":\"anki-managed-initialization-action.v1\",\"authorization\":{\"mode\":\"explicit-user-action\"}}"
                        : "{\"operation\":\"" + operation + "\"}";
                    var bytes = new UTF8Encoding(false).GetBytes(json); File.WriteAllBytes(path, bytes); var key = Sha256(Encoding.UTF8.GetBytes(actionId));
                    v3Receipts.Add(client.SendModuleActionV3(new CommonModuleActionRequestV3 { schemaVersion = "knowledge-clipper-module-action.v3", actionId = actionId, operation = operation, payloadRef = payloadRef, idempotencyKey = key }));
                }
                server.Complete();
                Assert(server.MessageTypes.All(value => value == "module-action") && server.ModuleActionPayloads.Select(value => Convert.ToString(value["operation"])).SequenceEqual(operations.Concat(new[] { "remnote.discover-target" }).Concat(operations).Concat(new[] { "remnote.discover-target", "anki.initialize-managed" })), "public module actions preserve v1/v2 compatibility and use the authority-defined protocol 1.4 envelope for all v3 actions");
                Assert(receipts.All(value => value.accepted && value.externalWriteCount == 0 && !value.automaticRetryAllowed) && !receipts[0].physicalAudio && receipts[1].physicalAudio && !receipts[2].physicalAudio, "module-action receipts preserve read-only and explicit physical-audio boundaries");
                Assert(receipts[0].catalogSnapshotRef != null && receipts[0].catalogSnapshotSha256 != null && receipts.Skip(1).All(value => value.catalogSnapshotRef == null), "only speech voice discovery projects a persisted catalog snapshot identity");
                Assert(discoveryReceipt.accepted && discoveryReceipt.schemaVersion == "knowledge-clipper-module-action-receipt.v2" && discoveryReceipt.operation == "remnote.discover-target" && discoveryReceipt.externalWriteCount == 0 && !discoveryReceipt.physicalAudio && !discoveryReceipt.automaticRetryAllowed, "RemNote discovery receives the strict v2 receipt with zero writes, zero audio, and no automatic retry");
                Assert(v3Receipts.All(value => value.accepted && value.schemaVersion == "knowledge-clipper-module-action-receipt.v3" && !value.automaticRetryAllowed) && v3Receipts.Single(value => value.operation == "anki.initialize-managed").status == "initialized", "IPC 1.4 returns strict v3 receipts and preserves explicit Anki managed-initialization status");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void TestNamedPipeQuestionCancel()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-question-cancel-" + Guid.NewGuid().ToString("N")); var jobs = Path.Combine(root, "jobs"); Directory.CreateDirectory(jobs);
            var pipeName = "KnowledgeClipperQuestionCancel." + Guid.NewGuid().ToString("N");
            try
            {
                var source = new FileDesktopCommonCommandSource(jobs, new FakeClock(new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider());
                var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), new NamedPipeDesktopOrchestratorPort(new NamedPipeCommonIpcClient(pipeName, 5000), source));
                controller.Execute(Action(AutomationCatalog.QuestionOpen)); var state = controller.CloseQuestionInteraction();
                Assert(state.foregroundState == "idle", "opening and cancelling a new question session requires no recent-task query or command");
                Assert(!Directory.GetFiles(jobs, "*", SearchOption.AllDirectories).Any(), "question cancel creates no task package or payload artifacts");
            }
            finally { if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("knowledge-clipper-question-cancel-", StringComparison.Ordinal)) Directory.Delete(root, true); }
        }

        private static void TestNamedPipeLanguagePracticeInput()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "knowledge-clipper-language-pipe-" + Guid.NewGuid().ToString("N"));
            var jobsRoot = Path.Combine(testRoot, "jobs"); Directory.CreateDirectory(jobsRoot);
            var pipeName = "KnowledgeClipperLanguageFake." + Guid.NewGuid().ToString("N");
            var targetToken = new string('e', 64);
            var server = new FakeNamedPipeOrchestratorServer(pipeName, jobsRoot, 3) { DecisionKind = AutomationCatalogV11.LanguagePracticeDecisionKind, DecisionTargetToken = targetToken };
            server.Start();
            try
            {
                var nextCorrelation = 0;
                var client = new NamedPipeCommonIpcClient(pipeName, 5000, delegate { nextCorrelation++; return "language-correlation-" + nextCorrelation.ToString("D4"); });
                var source = new FileDesktopCommonCommandSource(jobsRoot, new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider());
                var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), new NamedPipeDesktopOrchestratorPort(client, source));
                controller.Execute(Action(AutomationCatalog.QuestionOpen));
                var state = controller.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = "语言练习输入" }));
                Assert(state.recentTask.available, "question task is submitted before the language decision query");
                state = controller.RefreshActiveQuestion();
                Assert(state.recentTask.decisionKind == AutomationCatalogV11.LanguagePracticeDecisionKind && state.recentTask.decisionTargetToken == targetToken, "v2 query result projects the authoritative target token");
                state = controller.Execute(ActionV11(AutomationCatalogV11.LanguagePracticeSelect, new SemanticActionArguments { selection = AutomationCatalogV11.LanguagePracticeWriting }));
                Assert(state.noticeCode == "language-practice.input-submitted", "language choice is accepted through the real Named Pipe client");
                server.Complete();
                Assert(server.MessageTypes.SequenceEqual(new[] { "task-command", "task-query", "task-command" }) && server.CommandTypes.SequenceEqual(new[] { "task.submit", "task.provide_input" }) && server.QueryTypes.SequenceEqual(new[] { "task.get" }) && server.QuerySchemaVersions.SequenceEqual(new[] { "2.4.0" }), "language flow creates a new session without query_recent, uses task-scoped v2.4 task.get, then common task.provide_input");
                var payload = server.LanguagePracticePayloads.Single();
                Assert(payload.Count == 4 && Convert.ToString(payload["schemaVersion"]) == "knowledge-clipper-language-practice-choice.v2" && Convert.ToString(payload["decisionKind"]) == "language-practice" && Convert.ToString(payload["targetToken"]) == targetToken && Convert.ToString(payload["choice"]) == "writing", "immutable language payload has exactly the four authoritative fields");
                var payloadFiles = Directory.GetFiles(jobsRoot, "language-practice-choice.v2.json", SearchOption.AllDirectories);
                Assert(payloadFiles.Length == 1 && !File.ReadAllText(payloadFiles[0]).Contains("clientBinding") && !File.ReadAllText(payloadFiles[0]).Contains("export"), "desktop writes one immutable target-neutral payload without client binding or exporter data");
            }
            finally { if (Directory.Exists(testRoot) && Path.GetFileName(testRoot).StartsWith("knowledge-clipper-language-pipe-", StringComparison.Ordinal)) Directory.Delete(testRoot, true); }
        }

        private static void TestNamedPipeOcrHumanResolution()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-ocr-pipe-" + Guid.NewGuid().ToString("N"));
            var jobs = Path.Combine(root, "jobs"); Directory.CreateDirectory(jobs);
            try
            {
                var pipe = "KnowledgeClipperOcrResolution." + Guid.NewGuid().ToString("N");
                var server = new FakeNamedPipeOrchestratorServer(pipe, jobs, 5); server.EnableOcrHumanResolutionScenario("task-ocr-pipe-0001"); server.Start();
                var controller = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), new NamedPipeDesktopOrchestratorPort(new NamedPipeCommonIpcClient(pipe, 5000), new FileDesktopCommonCommandSource(jobs, new FakeClock(new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider())));
                var state = controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
                Assert(state.recentTask.ocrHumanResolution != null && state.recentTask.attentionRequired && state.recentTask.decisionTargetToken == new string('d', 64), "v2.3 Named Pipe query projects the exact waiting OCR decision [available=" + state.recentTask.available + ", state=" + state.recentTask.displayState + ", attention=" + state.recentTask.attentionRequired + ", kind=" + state.recentTask.decisionKind + ", token=" + state.recentTask.decisionTargetToken + ", projection=" + (state.recentTask.ocrHumanResolution != null) + "]");
                state = controller.Execute(ActionV13(AutomationCatalogV13.OcrAcceptOriginal));
                Assert(state.noticeCode == "ocr-human-resolution.input-submitted", "accept-original is submitted through common task.provide_input");
                state = controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
                Assert(state.recentTask.ocrHumanResolution != null && state.recentTask.decisionTargetToken == new string('e', 64), "a still-unresolved Core session reprojects a new opaque token for another dialog");
                state = controller.Execute(ActionV13(AutomationCatalogV13.OcrSubmitCorrection, new SemanticActionArguments { text = "人工修正文", candidateText = "AI 候选" }));
                Assert(state.noticeCode == "ocr-human-resolution.input-submitted", "edited correction is submitted through the same durable input path");
                state = controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
                Assert(state.recentTask.displayState == "succeeded" && state.recentTask.ocrHumanResolution == null, "successful continuation returns to a terminal observable task state");
                server.Complete();
                Assert(server.CommandTypes.SequenceEqual(new[] { "task.provide_input", "task.provide_input" }) && server.CommandTypes.All(value => value != "task.cancel"), "OCR continuation never uses whole-task cancel or direct worker calls");
                Assert(server.OcrHumanResolutionPayloads.Count == 2 && Convert.ToString(server.OcrHumanResolutionPayloads[0]["action"]) == "accept_original" && Convert.ToString(server.OcrHumanResolutionPayloads[1]["action"]) == "submit_correction", "fake server validates both public OCR action shapes and immutable correction artifacts");

                var cancelPipe = "KnowledgeClipperOcrCancel." + Guid.NewGuid().ToString("N");
                var cancelServer = new FakeNamedPipeOrchestratorServer(cancelPipe, jobs, 3); cancelServer.EnableOcrHumanResolutionScenario("task-ocr-pipe-0002"); cancelServer.Start();
                var cancelController = Controller(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), new NamedPipeDesktopOrchestratorPort(new NamedPipeCommonIpcClient(cancelPipe, 5000), new FileDesktopCommonCommandSource(jobs, new FakeClock(new DateTime(2026, 8, 30, 12, 1, 0, DateTimeKind.Utc)), new SequentialCommonIdProvider())));
                cancelController.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
                state = cancelController.Execute(ActionV13(AutomationCatalogV13.OcrCancel));
                Assert(state.noticeCode == "ocr-human-resolution.cancel-submitted", "dialog cancel is a durable OCR decision and remains distinct from whole-task cancel");
                state = cancelController.Execute(Action(AutomationCatalog.TaskStatusQueryRecent));
                Assert(state.recentTask.displayState == "cancelled" && state.recentTask.ocrHumanResolution == null, "cancel receipt is observable without a fake stopped state");
                cancelServer.Complete();
                Assert(cancelServer.CommandTypes.SequenceEqual(new[] { "task.provide_input" }) && Convert.ToString(cancelServer.OcrHumanResolutionPayloads.Single()["action"]) == "cancel", "OCR cancel uses only the public durable input action");
            }
            finally { if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("knowledge-clipper-ocr-pipe-", StringComparison.Ordinal)) Directory.Delete(root, true); }
        }

        private static void WriteCardStructureArtifact(string jobsRoot, string taskId, string artifactRef)
        {
            var payloadRelative = "artifacts/" + artifactRef + "/structure.json"; var manifestRelative = "artifacts/" + artifactRef + "/artifact.json";
            var taskRoot = Path.Combine(jobsRoot, taskId); var payload = new UTF8Encoding(false).GetBytes("{\"schemaVersion\":\"question-card-structure/1.0.0\"}");
            var payloadPath = Path.Combine(taskRoot, payloadRelative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)); File.WriteAllBytes(payloadPath, payload);
            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "artifactId", artifactRef }, { "kind", "question.card-structure" }, { "contractVersion", "1.0.0" }, { "mediaType", "application/json" },
                { "files", new object[] { new Dictionary<string, object> { { "path", payloadRelative }, { "sha256", Sha256(payload) }, { "bytes", payload.LongLength }, { "mediaType", "application/json" } } } },
                { "createdAt", "2026-08-26T00:00:00Z" }, { "producer", new Dictionary<string, object> { { "stageId", "fixture" }, { "workerId", "fixture" }, { "workerVersion", "1.0.0" }, { "attemptId", "fixture" } } }, { "inputRefs", new object[0] }, { "dataClass", "personal" }
            };
            File.WriteAllBytes(Path.Combine(taskRoot, manifestRelative.Replace('/', Path.DirectorySeparatorChar)), new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(manifest)));
        }

        private static DesktopController Controller(ICaptureProvider capture, IScreenshotProvider screenshot, IDesktopOrchestratorPort orchestrator)
        {
            return new DesktopController(capture, screenshot, orchestrator, new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)), new FakeIdProvider());
        }

        private static SemanticActionEnvelope Action(string action, SemanticActionArguments arguments = null)
        {
            return new SemanticActionEnvelope { schemaVersion = AutomationCatalog.ActionSchemaVersion, requestId = Guid.NewGuid().ToString("N"), action = action, arguments = arguments };
        }

        private static SemanticActionEnvelope ActionV11(string action, SemanticActionArguments arguments = null)
        {
            return new SemanticActionEnvelope { schemaVersion = AutomationCatalogV11.ActionSchemaVersion, requestId = Guid.NewGuid().ToString("N"), action = action, arguments = arguments };
        }

        private static SemanticActionEnvelope ActionV13(string action, SemanticActionArguments arguments = null)
        {
            return new SemanticActionEnvelope { schemaVersion = AutomationCatalogV13.ActionSchemaVersion, requestId = Guid.NewGuid().ToString("N"), action = action, arguments = arguments };
        }

        private static QuestionAnswerArtifactReference Reference(string artifactRef, string kind)
        {
            return new QuestionAnswerArtifactReference { artifactRef = artifactRef, kind = kind, contractVersion = "1.0.0", manifestPath = "artifacts/" + artifactRef + "/artifact.json", expectedSha256 = new string('c', 64) };
        }

        private static QuestionAnswerArtifactReference WriteTextArtifact(string jobsRoot, string taskId, string artifactRef, string kind, string text)
        {
            var payloadRelative = "runs/fixture/" + artifactRef + "/payload.txt";
            var manifestRelative = "runs/fixture/" + artifactRef + "/artifact.json";
            var payload = new UTF8Encoding(false).GetBytes(text);
            var payloadPath = Path.Combine(jobsRoot, taskId, payloadRelative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)); File.WriteAllBytes(payloadPath, payload);
            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "artifactId", artifactRef }, { "kind", kind }, { "contractVersion", "1.0.0" }, { "mediaType", "text/plain" },
                { "files", new object[] { new Dictionary<string, object> { { "path", payloadRelative }, { "sha256", Sha256(payload) }, { "bytes", payload.LongLength }, { "mediaType", "text/plain" } } } },
                { "createdAt", "2026-08-30T11:30:00Z" }, { "producer", new Dictionary<string, object>() }, { "inputRefs", new object[0] }, { "dataClass", "sensitive" },
                { "metadata", new Dictionary<string, object> { { "entrypoint", payloadRelative } } }
            };
            var manifestBytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(manifest)); var manifestPath = Path.Combine(jobsRoot, taskId, manifestRelative.Replace('/', Path.DirectorySeparatorChar)); File.WriteAllBytes(manifestPath, manifestBytes);
            return new QuestionAnswerArtifactReference { artifactRef = artifactRef, kind = kind, contractVersion = "1.0.0", manifestPath = manifestRelative, expectedSha256 = Sha256(manifestBytes) };
        }

        private static QuestionAnswerArtifactReference WriteOcrProvenanceFixture(string jobsRoot, string taskId, out string markdownPayloadPath)
        {
            var serializer = new JavaScriptSerializer();
            markdownPayloadPath = "runs/fixture/ocr.markdown.fixture/content.md";
            var markdownBytes = new UTF8Encoding(false).GetBytes("# OCR Markdown");
            var markdown = WriteArtifactFixture(jobsRoot, taskId, "ocr.markdown.fixture", "image-ocr.markdown", "text/markdown; charset=utf-8", markdownPayloadPath, markdownBytes, new object[0]);
            var structuredPayloadPath = "runs/fixture/ocr.structured.fixture/content.json";
            var structuredBytes = new UTF8Encoding(false).GetBytes("{\"structured\":true}");
            var structured = WriteArtifactFixture(jobsRoot, taskId, "ocr.structured.fixture", "image-ocr.structured", "application/json", structuredPayloadPath, structuredBytes, new object[0]);
            var markdownNested = ReferenceDictionary(markdown); markdownNested.Add("payload_path", markdownPayloadPath); markdownNested.Add("payload_sha256", Sha256(markdownBytes));
            var structuredNested = ReferenceDictionary(structured); structuredNested.Add("payload_path", structuredPayloadPath); structuredNested.Add("payload_sha256", Sha256(structuredBytes));
            var proofreadPayloadPath = "runs/fixture/ocr.proofread.fixture/proofread.json";
            var proofreadPayload = new Dictionary<string, object>
            {
                { "schema_version", "ocr-proofread-bundle/1.0.0" }, { "release_state", "unresolved" },
                { "items", new object[] { new Dictionary<string, object>
                    {
                        { "item_id", "item-1" }, { "state", "unresolved" },
                        { "provenance", new Dictionary<string, object> { { "schema_version", "ocr-provenance/1.0.0" }, { "producer_worker_id", "image.ocr.mineru" }, { "markdown_artifact", markdownNested }, { "structured_artifact", structuredNested } } },
                        { "source_ref", new Dictionary<string, object> { { "path", markdownPayloadPath }, { "sha256", Sha256(markdownBytes) } } }
                    } } }
            };
            var proofreadBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(proofreadPayload));
            return WriteArtifactFixture(jobsRoot, taskId, "ocr.proofread.fixture", "ocr-proofread-bundle", "application/json", proofreadPayloadPath, proofreadBytes, new object[] { ReferenceDictionary(markdown), ReferenceDictionary(structured) });
        }

        private static QuestionAnswerArtifactReference WriteArtifactFixture(string jobsRoot, string taskId, string artifactRef, string kind, string mediaType, string payloadRelative, byte[] payload, object[] inputRefs)
        {
            var taskRoot = Path.Combine(jobsRoot, taskId); var payloadPath = Path.Combine(taskRoot, payloadRelative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)); File.WriteAllBytes(payloadPath, payload);
            var manifestRelative = "runs/fixture/" + artifactRef + "/artifact.json";
            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "artifactId", artifactRef }, { "kind", kind }, { "contractVersion", "1.0.0" }, { "mediaType", mediaType },
                { "files", new object[] { new Dictionary<string, object> { { "path", payloadRelative }, { "sha256", Sha256(payload) }, { "bytes", payload.LongLength }, { "mediaType", mediaType } } } },
                { "createdAt", "2026-08-30T11:40:00Z" }, { "producer", new Dictionary<string, object>() }, { "inputRefs", inputRefs }, { "dataClass", "sensitive" },
                { "metadata", new Dictionary<string, object> { { "entrypoint", payloadRelative } } }
            };
            var manifestBytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(manifest)); var manifestPath = Path.Combine(taskRoot, manifestRelative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)); File.WriteAllBytes(manifestPath, manifestBytes);
            return new QuestionAnswerArtifactReference { artifactRef = artifactRef, kind = kind, contractVersion = "1.0.0", manifestPath = manifestRelative, expectedSha256 = Sha256(manifestBytes) };
        }

        private static Dictionary<string, object> ReferenceDictionary(QuestionAnswerArtifactReference reference)
        {
            return new Dictionary<string, object> { { "artifactRef", reference.artifactRef }, { "kind", reference.kind }, { "contractVersion", reference.contractVersion }, { "manifestPath", reference.manifestPath }, { "expectedSha256", reference.expectedSha256 } };
        }

        private static CaptureResult TextCapture(string source, bool contextAvailable)
        {
            return new CaptureResult
            {
                hasContent = true,
                source = source,
                artifacts = new List<CaptureArtifact> { Artifact("capture.text.selection.0001", "text/plain", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") },
                selectedTextArtifactRef = "capture.text.selection.0001",
                contextAvailable = contextAvailable,
                contextSegments = null
            };
        }

        private static ScreenshotResult Shot(string artifactRef, string hash)
        {
            return new ScreenshotResult { outcome = "captured", artifact = Artifact(artifactRef, "image/png", hash), noticeCode = "fake.captured" };
        }

        private static CaptureArtifact Artifact(string artifactRef, string mediaType, string hash)
        {
            return new CaptureArtifact { artifactRef = artifactRef, relativePath = "inputs/" + artifactRef + (mediaType == "image/png" ? ".png" : ".txt"), sha256 = hash, bytes = 10, mediaType = mediaType, encoding = mediaType.StartsWith("text/") ? "utf-8" : null };
        }

        private static CaptureBundleV1 ScreenshotBundle(string artifactRef)
        {
            return new CaptureBundleV1
            {
                schemaVersion = AutomationCatalog.CaptureBundleSchemaVersion,
                captureId = "capture-boundary-fixture",
                createdAt = "2026-08-25T12:00:00.0000000Z",
                intent = "screenshot",
                source = "screenshot",
                modeSnapshot = new CaptureModeSnapshot { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "original" },
                artifacts = new List<CaptureArtifact> { Artifact(artifactRef, "image/png", "5555555555555555555555555555555555555555555555555555555555555555") },
                selectedTextArtifactRef = null,
                contextSegments = null,
                questionImages = new List<string> { artifactRef },
                answerImages = new List<string>(),
                questionTurns = new List<QuestionTurnReference>()
            };
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertThrows(string expected, Action action)
        {
            try { action(); }
            catch (InvalidOperationException ex) { Assert(ex.Message == expected, "expected " + expected + " but got " + ex.Message); return; }
            throw new InvalidOperationException("expected exception " + expected);
        }

        private sealed class SequentialCommonIdProvider : ICommonIdProvider
        {
            private int next;
            public string NewId(string prefix) { next++; return prefix + "-" + next.ToString("D4"); }
        }
    }
}
