using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Desktop.Tests
{
    internal sealed class FakeNamedPipeOrchestratorServer
    {
        private readonly string pipeName;
        private readonly string jobsRoot;
        private readonly int expectedRequests;
        private readonly Thread thread;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 1048576 };
        private Exception failure;
        private string lastTaskId;
        private string lastIntent;
        private int answerVersion;
        private bool ocrHumanResolutionScenario;
        private string ocrStatus = "waiting_input";
        private int ocrResolutionCount;
        private IDictionary<string, object> ocrSourceProofreadRef;
        private IDictionary<string, object> ocrOriginalTextRef;
        private IDictionary<string, object> ocrCandidateTextRef;

        internal readonly List<string> MessageTypes = new List<string>();
        internal readonly List<string> CommandTypes = new List<string>();
        internal readonly List<IDictionary<string, object>> LanguagePracticePayloads = new List<IDictionary<string, object>>();
        internal readonly List<IDictionary<string, object>> QuestionActionPayloads = new List<IDictionary<string, object>>();
        internal readonly List<IDictionary<string, object>> OcrHumanResolutionPayloads = new List<IDictionary<string, object>>();
        internal readonly List<IDictionary<string, object>> StageControlPayloads = new List<IDictionary<string, object>>();
        internal readonly List<IDictionary<string, object>> ModuleActionPayloads = new List<IDictionary<string, object>>();
        internal readonly List<string> QueryTypes = new List<string>();
        internal readonly List<string> QuerySchemaVersions = new List<string>();
        internal string DecisionKind = null;
        internal string DecisionTargetToken = null;
        internal string QuestionTargetToken = new string('a', 64);
        internal int QuestionTurnCount = 1;
        internal IDictionary<string, object> QuestionAnswerRef;
        internal bool SawBom;
        internal bool SawCrLf;

        internal FakeNamedPipeOrchestratorServer(string pipeName, string jobsRoot, int expectedRequests)
        {
            this.pipeName = pipeName;
            this.jobsRoot = Path.GetFullPath(jobsRoot);
            this.expectedRequests = expectedRequests;
            thread = new Thread(Run) { IsBackground = true, Name = "RQC desktop fake pipe server" };
        }

        internal void Start() { thread.Start(); }

        internal void EnableOcrHumanResolutionScenario(string taskId)
        {
            if (thread.IsAlive) throw new InvalidOperationException("OCR scenario must be configured before the fake server starts");
            ocrHumanResolutionScenario = true;
            lastTaskId = taskId;
            lastIntent = "clip.card";
            DecisionKind = "ocr-human-resolution";
            DecisionTargetToken = new string('d', 64);
            ocrStatus = "waiting_input";
            ocrResolutionCount = 0;
            ocrSourceProofreadRef = CreateArtifact(taskId, "ocr.proofread.bundle.0001", "ocr-proofread-bundle", "application/json", "{\"schemaVersion\":\"ocr-proofread-bundle/1.0.0\"}");
            ocrOriginalTextRef = CreateArtifact(taskId, "ocr.original.text.0001", "ocr.original.text", "text/plain", "OCR 原文");
            ocrCandidateTextRef = CreateArtifact(taskId, "ocr.candidate.text.0001", "ocr.candidate.text", "text/plain", "AI 候选");
        }

        internal void Complete()
        {
            if (!thread.Join(10000)) throw new InvalidOperationException("fake pipe server did not receive all expected requests");
            if (failure != null) throw new InvalidOperationException("fake pipe server failed", failure);
        }

        private void Run()
        {
            try
            {
                for (var index = 0; index < expectedRequests; index++)
                {
                    using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        var requestBytes = ReadFrame(server);
                        var request = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(requestBytes)) as IDictionary<string, object>;
                        if (request == null) throw new InvalidDataException("request envelope is not an object");
                        var correlationId = Convert.ToString(request["correlationId"]);
                        var protocolVersion = Convert.ToString(request["protocolVersion"]);
                        var messageType = Convert.ToString(request["messageType"]);
                        MessageTypes.Add(messageType);
                        IDictionary<string, object> response;
                        if (messageType == "task-command") response = CommandResponse(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "task-query") response = QueryResponse(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "stage-control" && protocolVersion == "1.1") response = StageControlResponse(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "module-action" && protocolVersion == "1.2") response = ModuleActionResponse(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "module-action" && protocolVersion == "1.3") response = ModuleActionResponseV2(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "module-action" && protocolVersion == "1.4") response = ModuleActionResponseV3(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "module-action" && protocolVersion == "1.5") response = ModuleActionResponseV4(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "module-action" && protocolVersion == "1.6") response = ModuleActionResponseV5(correlationId, request["payload"] as IDictionary<string, object>);
                        else if (messageType == "module-action" && protocolVersion == "1.7") response = ModuleActionResponseV6(correlationId, request["payload"] as IDictionary<string, object>);
                        else throw new InvalidDataException("unexpected messageType " + messageType);
                        var responseBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(response));
                        server.Write(responseBytes, 0, responseBytes.Length);
                        server.WriteByte((byte)'\n');
                        server.Flush();
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        }

        private IDictionary<string, object> CommandResponse(string correlationId, IDictionary<string, object> command)
        {
            if (command == null) throw new InvalidDataException("command payload missing");
            var commandType = Convert.ToString(command["type"]);
            var commandId = Convert.ToString(command["commandId"]);
            var operationId = Convert.ToString(command["operationId"]);
            var payloadRef = Convert.ToString(command["payloadRef"]);
            var taskId = command["taskId"] == null ? null : Convert.ToString(command["taskId"]);
            CommandTypes.Add(commandType);
            if (commandType == "task.submit")
            {
                var manifestPath = ResolveWithin(jobsRoot, payloadRef);
                if (!File.Exists(manifestPath) || Path.GetFileName(manifestPath) != "task.manifest.v1.json") throw new InvalidDataException("task manifest payload is missing");
                var manifest = serializer.DeserializeObject(File.ReadAllText(manifestPath, Encoding.UTF8)) as IDictionary<string, object>;
                taskId = Convert.ToString(manifest["taskId"]);
                lastIntent = Convert.ToString(manifest["intent"]);
                if (lastIntent == "clip.question") CreateQuestionAnswerArtifacts(taskId);
            }
            else
            {
                var taskRoot = ResolveWithin(jobsRoot, taskId);
                var payloadPath = ResolveWithin(taskRoot, payloadRef);
                if (!File.Exists(payloadPath)) throw new InvalidDataException("existing-task command payload is missing");
                if (commandType == "task.provide_input")
                {
                    var payload = serializer.DeserializeObject(File.ReadAllText(payloadPath, Encoding.UTF8)) as IDictionary<string, object>;
                    if (payload != null && Convert.ToString(payload["schemaVersion"]) == "knowledge-clipper-language-practice-choice.v2") LanguagePracticePayloads.Add(payload);
                    if (payload != null && Convert.ToString(payload["schemaVersion"]) == "knowledge-clipper-question-action.v2")
                    {
                        ValidateQuestionAction(taskId, payload); QuestionActionPayloads.Add(payload);
                        if (Convert.ToString(payload["action"]) == "question.follow-up") { QuestionTurnCount++; QuestionTargetToken = new string((char)('a' + Math.Min(QuestionTurnCount - 1, 5)), 64); CreateQuestionAnswerArtifacts(taskId); }
                    }
                    if (payload != null && Convert.ToString(payload["schemaVersion"]) == "knowledge-clipper-ocr-human-resolution-action.v1")
                    {
                        ValidateOcrHumanResolutionAction(taskId, payload);
                        OcrHumanResolutionPayloads.Add(payload);
                        ocrResolutionCount++;
                        if (Convert.ToString(payload["action"]) == "cancel") ocrStatus = "cancelled";
                        else if (ocrResolutionCount == 1)
                        {
                            ocrStatus = "waiting_input";
                            DecisionTargetToken = new string('e', 64);
                        }
                        else ocrStatus = "succeeded";
                    }
                }
            }
            lastTaskId = taskId;
            return Envelope(correlationId, "task-command-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "commandId", commandId }, { "accepted", true }, { "taskId", taskId },
                { "operationId", operationId }, { "status", "running" }, { "timestamp", "2026-08-25T12:00:00Z" },
                { "error", null }, { "durableEventRef", "runs/run-fake/events/000001-command-accepted.json" }
            }, null);
        }

        private IDictionary<string, object> StageControlResponse(string correlationId, IDictionary<string, object> payload)
        {
            if (payload == null || !new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "taskId", "operation", "selector", "reason" }) ||
                Convert.ToString(payload["taskId"]) != lastTaskId || Convert.ToString(payload["operation"]) != "speech.playback" ||
                Convert.ToString(payload["selector"]) != "current-active" || Convert.ToString(payload["reason"]) != "user_stop")
                throw new InvalidDataException("direct stage-control payload drifted");
            StageControlPayloads.Add(payload);
            return Envelope(correlationId, "stage-control-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "2.3.0" }, { "controlId", "control-fake" }, { "accepted", true },
                { "taskId", lastTaskId }, { "operation", "speech.playback" }, { "selector", "current-active" }, { "reason", "user_stop" },
                { "status", "requested" }, { "resolved", new Dictionary<string, object> { { "runId", "run-fake" }, { "stageId", "speech-playback" }, { "attemptId", "attempt-playback" }, { "operationId", "operation-playback" }, { "cancelSignalRef", "runs/run-fake/stages/speech-playback/attempts/attempt-playback/cancel.json" } } },
                { "timestamp", "2026-08-28T03:00:00Z" }, { "durableEventRef", "runs/run-fake/events/000010-stage-control-requested.json" },
                { "receiptRef", "runs/run-fake/controls/control-fake/receipt.json" }, { "error", null }
            }, null, "1.1");
        }

        private IDictionary<string, object> ModuleActionResponse(string correlationId, IDictionary<string, object> payload)
        {
            if (payload == null || !new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "actionId", "operation", "payloadRef", "idempotencyKey" }) ||
                Convert.ToString(payload["schemaVersion"]) != "knowledge-clipper-module-action.v1") throw new InvalidDataException("module-action payload drifted");
            var actionId = Convert.ToString(payload["actionId"]); var operation = Convert.ToString(payload["operation"]); var inputRef = Convert.ToString(payload["payloadRef"]); var idempotencyKey = Convert.ToString(payload["idempotencyKey"]);
            if (!new[] { "speech.voices", "speech.preview", "remnote.probe" }.Contains(operation, StringComparer.Ordinal) || inputRef != "module-actions/" + actionId + "/input.json") throw new InvalidDataException("module-action identity drifted");
            var inputPath = ResolveWithin(jobsRoot, inputRef); var inputBytes = File.ReadAllBytes(inputPath); var inputHash = Sha256(inputBytes);
            if (!string.Equals(inputHash, idempotencyKey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("module-action idempotency hash drifted");
            ModuleActionPayloads.Add(payload);
            var outputRef = "module-actions/" + actionId + "/output.json";
            var outputBytes = new UTF8Encoding(false).GetBytes(operation == "remnote.probe"
                ? "{\"schemaVersion\":\"remnote-public-probe-result.v2\",\"state\":\"healthy\",\"admission\":\"ready\",\"targetBinding\":{\"matched\":true}}"
                : operation == "speech.preview" ? "{\"schemaVersion\":\"speech-settings-speaker-preview-output.v1\",\"status\":\"completed\"}"
                : "{\"schemaVersion\":\"speech-settings-voice-discovery-output.v1\",\"status\":\"ready\",\"provider\":\"edge-tts\",\"catalogVersion\":\"fake\",\"catalogRevision\":\"edge-tts-20260830T120000Z-aaaaaaaaaaaa\",\"catalogSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"updatedAtUtc\":\"2026-08-30T12:00:00Z\",\"voices\":[]}");
            WriteOnce(ResolveWithin(jobsRoot, outputRef), outputBytes);
            string catalogRef = null; string catalogHash = null;
            if (operation == "speech.voices")
            {
                catalogHash = Sha256(outputBytes); catalogRef = "module-state/speech/voice-catalog/" + catalogHash + ".json";
                WriteOnce(ResolveWithin(jobsRoot, catalogRef), outputBytes);
            }
            return Envelope(correlationId, "module-action-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-module-action-receipt.v1" }, { "actionId", actionId }, { "operation", operation }, { "accepted", true }, { "status", operation == "speech.preview" ? "completed" : "ready" },
                { "inputRef", inputRef }, { "inputSha256", inputHash }, { "outputRef", outputRef }, { "outputSha256", Sha256(outputBytes) },
                { "catalogSnapshotRef", catalogRef }, { "catalogSnapshotSha256", catalogHash }, { "externalWriteCount", 0 }, { "physicalAudio", operation == "speech.preview" },
                { "automaticRetryAllowed", false }, { "idempotencyKey", idempotencyKey }, { "completedAt", "2026-08-30T12:00:00Z" }, { "error", null }
            }, null, "1.2");
        }

        private IDictionary<string, object> ModuleActionResponseV2(string correlationId, IDictionary<string, object> payload)
        {
            if (payload == null || !new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "actionId", "operation", "payloadRef", "idempotencyKey" }) ||
                Convert.ToString(payload["schemaVersion"]) != "knowledge-clipper-module-action.v2") throw new InvalidDataException("module-action-v2 payload drifted");
            var actionId = Convert.ToString(payload["actionId"]); var operation = Convert.ToString(payload["operation"]); var inputRef = Convert.ToString(payload["payloadRef"]); var idempotencyKey = Convert.ToString(payload["idempotencyKey"]);
            if (operation != "remnote.discover-target" || inputRef != "module-actions/" + actionId + "/input.json") throw new InvalidDataException("module-action-v2 identity drifted");
            var inputPath = ResolveWithin(jobsRoot, inputRef); var inputBytes = File.ReadAllBytes(inputPath); var inputHash = Sha256(inputBytes);
            if (!string.Equals(inputHash, idempotencyKey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("module-action-v2 idempotency hash drifted");
            ModuleActionPayloads.Add(payload);
            var outputRef = "module-actions/" + actionId + "/output.json";
            var outputBytes = new UTF8Encoding(false).GetBytes("{\"schemaVersion\":\"remnote-target-discovery-result.v1\",\"state\":\"healthy\",\"admission\":\"ready\",\"selection\":{\"resolution\":\"unique\",\"resolvedRootRemId\":\"fake-root\",\"observedTitle\":\"剪藏\",\"exactReadback\":true,\"persistableBinding\":true},\"reasonCodes\":[]}");
            WriteOnce(ResolveWithin(jobsRoot, outputRef), outputBytes);
            return Envelope(correlationId, "module-action-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-module-action-receipt.v2" }, { "actionId", actionId }, { "operation", operation }, { "accepted", true }, { "status", "ready" },
                { "inputRef", inputRef }, { "inputSha256", inputHash }, { "outputRef", outputRef }, { "outputSha256", Sha256(outputBytes) },
                { "catalogSnapshotRef", null }, { "catalogSnapshotSha256", null }, { "externalWriteCount", 0 }, { "physicalAudio", false },
                { "automaticRetryAllowed", false }, { "idempotencyKey", idempotencyKey }, { "completedAt", "2026-09-01T05:00:00Z" }, { "error", null }
            }, null, "1.3");
        }

        private IDictionary<string, object> ModuleActionResponseV3(string correlationId, IDictionary<string, object> payload)
        {
            if (payload == null || !new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "actionId", "operation", "payloadRef", "idempotencyKey" }) ||
                Convert.ToString(payload["schemaVersion"]) != "knowledge-clipper-module-action.v3") throw new InvalidDataException("module-action-v3 payload drifted");
            var actionId = Convert.ToString(payload["actionId"]); var operation = Convert.ToString(payload["operation"]); var inputRef = Convert.ToString(payload["payloadRef"]); var idempotencyKey = Convert.ToString(payload["idempotencyKey"]);
            if (!new[] { "speech.voices", "speech.preview", "remnote.probe", "remnote.discover-target", "anki.initialize-managed" }.Contains(operation, StringComparer.Ordinal) || inputRef != "module-actions/" + actionId + "/input.json") throw new InvalidDataException("module-action-v3 identity drifted");
            var inputPath = ResolveWithin(jobsRoot, inputRef); var inputBytes = File.ReadAllBytes(inputPath); var inputHash = Sha256(inputBytes);
            if (idempotencyKey.Length != 64 || idempotencyKey.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidDataException("module-action-v3 idempotency key invalid");
            ModuleActionPayloads.Add(payload);
            var outputRef = "module-actions/" + actionId + "/output.json";
            string json;
            if (operation == "anki.initialize-managed")
            {
                var input = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(inputBytes)) as IDictionary<string, object>;
                if (input == null || Convert.ToString(input["schemaVersion"]) != "anki-managed-initialization-action.v1") throw new InvalidDataException("Anki initialization input schema drifted");
                var authorization = input["authorization"] as IDictionary<string, object>;
                if (authorization == null || Convert.ToString(authorization["mode"]) != "explicit-user-action") throw new InvalidDataException("Anki initialization was not explicitly authorized");
                json = "{\"schemaVersion\":\"anki-managed-initialization-status.v1\",\"status\":\"initialized\",\"checkedAt\":\"2026-09-01T06:00:00Z\",\"operation\":\"initialize-managed\",\"effectClass\":\"provider-write\",\"networkScope\":\"loopback-only\",\"externalMutation\":true,\"permission\":\"granted\",\"apiVersion\":6,\"activeProfileSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"authorization\":{\"mode\":\"explicit-user-action\",\"scope\":\"managed-models-and-decks\"},\"models\":[],\"decks\":[],\"writes\":{\"modelsCreated\":0,\"decksCreated\":0},\"actionsObserved\":[],\"mutatingActionsObserved\":[],\"forbiddenActionsObserved\":[],\"automaticRetryAllowed\":false,\"replayPolicy\":\"explicit-reconcile-query-then-create-missing\",\"notesMediaAndExistingResourcesModified\":false}";
            }
            else if (operation == "remnote.discover-target") json = "{\"schemaVersion\":\"remnote-target-discovery-result.v1\",\"state\":\"healthy\",\"admission\":\"ready\",\"selection\":{\"resolution\":\"unique\",\"resolvedRootRemId\":\"fake-root\",\"observedTitle\":\"剪藏\",\"exactReadback\":true,\"persistableBinding\":true},\"reasonCodes\":[]}";
            else if (operation == "remnote.probe") json = "{\"schemaVersion\":\"remnote-public-probe-result.v2\",\"state\":\"healthy\",\"admission\":\"ready\",\"targetBinding\":{\"matched\":true}}";
            else if (operation == "speech.preview") json = "{\"schemaVersion\":\"speech-settings-speaker-preview-output.v1\",\"status\":\"completed\"}";
            else json = "{\"schemaVersion\":\"speech-settings-voice-discovery-output.v1\",\"status\":\"ready\",\"provider\":\"edge-tts\",\"catalogVersion\":\"fake-v3\",\"catalogRevision\":\"edge-tts-20260901T060000Z-bbbbbbbbbbbb\",\"catalogSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"updatedAtUtc\":\"2026-09-01T06:00:00Z\",\"voices\":[]}";
            var outputBytes = new UTF8Encoding(false).GetBytes(json); WriteOnce(ResolveWithin(jobsRoot, outputRef), outputBytes);
            string catalogRef = null; string catalogHash = null;
            if (operation == "speech.voices") { catalogHash = Sha256(outputBytes); catalogRef = "module-state/speech/voice-catalog/" + catalogHash + ".json"; WriteOnce(ResolveWithin(jobsRoot, catalogRef), outputBytes); }
            return Envelope(correlationId, "module-action-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-module-action-receipt.v3" }, { "actionId", actionId }, { "operation", operation }, { "accepted", true }, { "status", operation == "anki.initialize-managed" ? "initialized" : operation == "speech.preview" ? "completed" : "ready" },
                { "inputRef", inputRef }, { "inputSha256", inputHash }, { "outputRef", outputRef }, { "outputSha256", Sha256(outputBytes) },
                { "catalogSnapshotRef", catalogRef }, { "catalogSnapshotSha256", catalogHash }, { "externalWriteCount", 0 }, { "physicalAudio", operation == "speech.preview" },
                { "automaticRetryAllowed", false }, { "idempotencyKey", idempotencyKey }, { "completedAt", "2026-09-01T06:00:00Z" }, { "error", null }
            }, null, "1.4");
        }

        private IDictionary<string, object> ModuleActionResponseV4(string correlationId, IDictionary<string, object> payload)
        {
            if (payload == null || !new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "actionId", "operation", "payloadRef", "idempotencyKey" }) ||
                Convert.ToString(payload["schemaVersion"]) != "knowledge-clipper-module-action.v4" || Convert.ToString(payload["operation"]) != "anki.probe") throw new InvalidDataException("module-action-v4 payload drifted");
            var actionId = Convert.ToString(payload["actionId"]); var inputRef = Convert.ToString(payload["payloadRef"]); var idempotencyKey = Convert.ToString(payload["idempotencyKey"]);
            if (inputRef != "module-actions/" + actionId + "/input.json") throw new InvalidDataException("module-action-v4 identity drifted");
            var inputPath = ResolveWithin(jobsRoot, inputRef); var inputBytes = File.ReadAllBytes(inputPath); var inputHash = Sha256(inputBytes);
            if (!string.Equals(inputHash, idempotencyKey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("module-action-v4 idempotency hash drifted");
            var input = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(inputBytes)) as IDictionary<string, object>;
            if (input == null || Convert.ToString(input["schemaVersion"]) != "anki-readonly-probe-action.v1" || Convert.ToString(input["effectClass"]) != "provider-read-only" || Convert.ToBoolean(input["externalMutation"])) throw new InvalidDataException("anki read-only probe action drifted");
            ModuleActionPayloads.Add(payload);
            var taskId = Convert.ToString(input["taskId"]); var runId = "run-" + actionId; var packageRootRef = Convert.ToString(input["packageRootRef"]);
            var attempt = "runs/" + runId + "/stages/anki-probe/attempts/attempt-fake";
            var artifactPath = attempt + "/outputs/anki-connection-status/v0001/artifact.json";
            var statusPath = attempt + "/outputs/anki-connection-status/v0001/connection-status.json";
            var stdoutPath = attempt + "/stdout.log"; var stderrPath = attempt + "/stderr.log"; var operationsPath = attempt + "/operations.ndjson";
            var statusBytes = new UTF8Encoding(false).GetBytes("{\"schemaVersion\":\"anki-connection-status.v1\",\"status\":\"ready\",\"operation\":\"probe\",\"effectClass\":\"provider-read-only\",\"externalMutation\":false,\"mutatingActionsObserved\":[],\"apiVersion\":6,\"models\":[],\"decks\":[],\"initializationWouldWrite\":false,\"migrationRequired\":false}");
            var stdoutBytes = new UTF8Encoding(false).GetBytes("ready\n"); var stderrBytes = new byte[0]; var operationsBytes = new UTF8Encoding(false).GetBytes("{\"action\":\"probe\",\"status\":\"ready\"}\n");
            WriteOnce(ResolveWithin(jobsRoot, packageRootRef + "/" + statusPath), statusBytes); WriteOnce(ResolveWithin(jobsRoot, packageRootRef + "/" + stdoutPath), stdoutBytes);
            WriteOnce(ResolveWithin(jobsRoot, packageRootRef + "/" + stderrPath), stderrBytes); WriteOnce(ResolveWithin(jobsRoot, packageRootRef + "/" + operationsPath), operationsBytes);
            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "artifactId", "anki-connection-status" }, { "kind", "anki-connection-status" }, { "contractVersion", "1.0.0" }, { "mediaType", "application/json" },
                { "files", new object[] {
                    new Dictionary<string, object> { { "path", stdoutPath }, { "sha256", Sha256(stdoutBytes) }, { "bytes", stdoutBytes.LongLength }, { "mediaType", "text/plain" } },
                    new Dictionary<string, object> { { "path", statusPath }, { "sha256", Sha256(statusBytes) }, { "bytes", statusBytes.LongLength }, { "mediaType", "application/json" } },
                    new Dictionary<string, object> { { "path", operationsPath }, { "sha256", Sha256(operationsBytes) }, { "bytes", operationsBytes.LongLength }, { "mediaType", "application/x-ndjson" } },
                    new Dictionary<string, object> { { "path", stderrPath }, { "sha256", Sha256(stderrBytes) }, { "bytes", stderrBytes.LongLength }, { "mediaType", "text/plain" } }
                } },
                { "createdAt", "2026-09-01T09:20:00Z" }, { "producer", new Dictionary<string, object> { { "stageId", "anki-probe" }, { "workerId", "export.anki" }, { "workerVersion", "1.0.0" }, { "attemptId", "attempt-fake" } } },
                { "inputRefs", new object[0] }, { "dataClass", "sensitive" }, { "metadata", new Dictionary<string, object> { { "entrypoint", statusPath } } }
            };
            var manifestBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(manifest)); WriteOnce(ResolveWithin(jobsRoot, packageRootRef + "/" + artifactPath), manifestBytes);
            var resultPath = attempt + "/result.json";
            var resultBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "requestId", "request-" + actionId }, { "operationId", "operation-" + actionId }, { "taskId", taskId }, { "runId", runId },
                { "stageId", "anki-probe" }, { "attemptId", "attempt-fake" }, { "worker", new Dictionary<string, object> { { "id", "export.anki" }, { "version", "1.0.0" } } },
                { "contractVersion", "1.0.0" }, { "idempotencyKey", idempotencyKey }, { "status", "completed" }, { "outputs", new object[0] }, { "error", null },
                { "startedAt", "2026-09-01T09:20:00Z" }, { "finishedAt", "2026-09-01T09:20:01Z" }, { "warnings", new object[0] }, { "payload", new Dictionary<string, object> { { "mode", "pipeline" } } }
            }));
            WriteOnce(ResolveWithin(jobsRoot, packageRootRef + "/" + resultPath), resultBytes);
            return Envelope(correlationId, "module-action-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-module-action-receipt.v4" }, { "actionId", actionId }, { "operation", "anki.probe" }, { "accepted", true }, { "status", "ready" },
                { "inputRef", inputRef }, { "inputSha256", inputHash }, { "outputRef", packageRootRef + "/" + resultPath }, { "outputSha256", Sha256(resultBytes) },
                { "externalWriteCount", 0 }, { "physicalAudio", false }, { "automaticRetryAllowed", false }, { "idempotencyKey", idempotencyKey }, { "completedAt", "2026-09-01T09:20:01Z" }, { "error", null },
                { "executionStatus", "completed" }, { "taskId", taskId }, { "runId", runId }, { "artifactRootRef", packageRootRef },
                { "outputRefs", new object[] { new Dictionary<string, object> { { "referenceType", "artifact" }, { "kind", "anki-connection-status" }, { "contractVersion", "1.0.0" }, { "rootRef", packageRootRef }, { "path", artifactPath }, { "expectedSha256", Sha256(manifestBytes) } } } }
            }, null, "1.5");
        }

        private IDictionary<string, object> ModuleActionResponseV5(string correlationId, IDictionary<string, object> payload)
        {
            if (payload == null || !new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "actionId", "operation", "payloadRef", "idempotencyKey" }) ||
                Convert.ToString(payload["schemaVersion"]) != "knowledge-clipper-module-action.v5") throw new InvalidDataException("module-action-v5 payload drifted");
            var actionId = Convert.ToString(payload["actionId"]); var operation = Convert.ToString(payload["operation"]); var inputRef = Convert.ToString(payload["payloadRef"]); var idempotencyKey = Convert.ToString(payload["idempotencyKey"]);
            if (!new[] { "content.settings.describe", "content.settings.copy", "content.settings.validate", "content.settings.diff" }.Contains(operation, StringComparer.Ordinal) || inputRef != "module-actions/" + actionId + "/input.json") throw new InvalidDataException("module-action-v5 identity drifted");
            var inputBytes = File.ReadAllBytes(ResolveWithin(jobsRoot, inputRef)); var inputHash = Sha256(inputBytes);
            if (!string.Equals(inputHash, idempotencyKey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("module-action-v5 input hash drifted");
            var input = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(inputBytes)) as IDictionary<string, object>;
            string kind; IDictionary<string, object> result;
            if (operation == "content.settings.describe")
            {
                kind = "content-settings-description";
                result = new Dictionary<string, object>
                {
                    { "schemaVersion", "content-settings-adapter/1.0.0" },
                    { "worker", new Dictionary<string, object> { { "id", "content.deepseek" } } },
                    { "provider", new Dictionary<string, object> { { "apiKey", new Dictionary<string, object> { { "valueExposed", false } } } } },
                    { "credentialsPolicy", new Dictionary<string, object> { { "taskPackages", "forbidden" } } }
                };
            }
            else
            {
                var root = Path.GetFullPath(Convert.ToString(input[operation == "content.settings.copy" ? "targetRoot" : "root"]));
                if (operation == "content.settings.copy")
                {
                    if (Directory.Exists(root)) throw new InvalidDataException("content settings copy target already exists");
                    Directory.CreateDirectory(Path.Combine(root, "prompts")); Directory.CreateDirectory(Path.Combine(root, "config"));
                    File.WriteAllText(Path.Combine(root, "prompts", "main.md"), "# 用户 Prompt\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "config", "categories.v2.json"), "{\"schemaVersion\":\"category-registry/2.0.0\",\"categories\":[]}", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "content-settings.draft.json"), "{}", new UTF8Encoding(false));
                }
                var promptBytes = File.ReadAllBytes(Path.Combine(root, "prompts", "main.md")); var registryBytes = File.ReadAllBytes(Path.Combine(root, "config", "categories.v2.json"));
                var promptFile = new Dictionary<string, object> { { "path", "main.md" }, { "sha256", Sha256(promptBytes) }, { "bytes", promptBytes.LongLength } };
                var promptSet = new Dictionary<string, object> { { "sha256", Sha256(promptBytes) }, { "files", new object[] { promptFile } } };
                var categoryRegistry = new Dictionary<string, object> { { "sha256", Sha256(registryBytes) }, { "bytes", registryBytes.LongLength } };
                var validation = new Dictionary<string, object>
                {
                    { "schemaVersion", "content-settings-validation/1.0.0" }, { "draftRoot", root }, { "valid", true },
                    { "warnings", new object[0] }, { "errors", new object[0] }, { "settingsRevisionCandidate", "content-settings-fake-v5" },
                    { "promptSet", promptSet }, { "categoryRegistry", categoryRegistry }
                };
                if (operation == "content.settings.copy") { kind = "content-settings-copy-result"; result = new Dictionary<string, object> { { "schemaVersion", "content-settings-copy-result/1.0.0" }, { "targetRoot", root }, { "validation", validation } }; }
                else if (operation == "content.settings.validate") { kind = "content-settings-validation"; result = validation; }
                else { kind = "content-settings-diff"; result = new Dictionary<string, object>(validation); result["schemaVersion"] = "content-settings-diff/1.0.0"; }
            }
            ModuleActionPayloads.Add(payload);
            var outputRef = "module-actions/" + actionId + "/result.json"; var outputBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(result)); WriteOnce(ResolveWithin(jobsRoot, outputRef), outputBytes);
            return Envelope(correlationId, "module-action-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-module-action-receipt.v5" }, { "actionId", actionId }, { "operation", operation }, { "accepted", true }, { "status", "completed" },
                { "inputRef", inputRef }, { "inputSha256", inputHash }, { "outputRef", outputRef }, { "outputSha256", Sha256(outputBytes) }, { "catalogSnapshotRef", null }, { "catalogSnapshotSha256", null },
                { "externalWriteCount", 0 }, { "physicalAudio", false }, { "automaticRetryAllowed", false }, { "idempotencyKey", idempotencyKey }, { "completedAt", "2026-09-01T09:45:00Z" }, { "error", null }, { "executionStatus", "completed" }, { "taskId", null }, { "runId", null }, { "artifactRootRef", null },
                { "outputRefs", new object[] { new Dictionary<string, object> { { "referenceType", "file" }, { "kind", kind }, { "contractVersion", "1.0.0" }, { "rootRef", "jobs-root" }, { "path", outputRef }, { "expectedSha256", Sha256(outputBytes) } } } }
            }, null, "1.6");
        }

        private IDictionary<string, object> ModuleActionResponseV6(string correlationId, IDictionary<string, object> payload)
        {
            if (payload == null || Convert.ToString(payload["schemaVersion"]) != "knowledge-clipper-module-action.v6") throw new InvalidDataException("module-action-v6 payload drifted");
            var actionId = Convert.ToString(payload["actionId"]); var operation = Convert.ToString(payload["operation"]); var inputRef = Convert.ToString(payload["payloadRef"]); var idempotencyKey = Convert.ToString(payload["idempotencyKey"]);
            if (!new[] { "remnote.probe", "speech.voices", "speech.preview" }.Contains(operation, StringComparer.Ordinal) || inputRef != "module-actions/" + actionId + "/input.json") throw new InvalidDataException("module-action-v6 identity drifted");
            var inputBytes = File.ReadAllBytes(ResolveWithin(jobsRoot, inputRef)); var inputHash = Sha256(inputBytes);
            if (!string.Equals(inputHash, idempotencyKey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("module-action-v6 input hash drifted");
            var input = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(inputBytes)) as IDictionary<string, object>;
            if (operation == "speech.preview")
            {
                var worker = input["workerInput"] as IDictionary<string, object>; var authorization = input["authorization"] as IDictionary<string, object>;
                if (Convert.ToString(input["schemaVersion"]) != "speech-settings-speaker-preview-action.v2" || Convert.ToString(authorization["mode"]) != "explicit-user-action" || Convert.ToString(authorization["actionId"]) != actionId || Convert.ToString(authorization["previewLanguage"]) != Convert.ToString(worker["previewLanguage"]) || Convert.ToBoolean(input["automaticRetryAllowed"])) throw new InvalidDataException("speech preview authorization drifted");
            }
            string kind; string contractVersion; string status; string json;
            if (operation == "remnote.probe") { kind = "remnote-public-probe-result"; contractVersion = "2.0.0"; status = "ready"; json = "{\"schemaVersion\":\"remnote-public-probe-result.v2\",\"state\":\"healthy\",\"admission\":\"ready\",\"targetBinding\":{\"matched\":true}}"; }
            else if (operation == "speech.voices") { kind = "speech-voice-discovery-result"; contractVersion = "1.0.0"; status = "ready"; json = "{\"schemaVersion\":\"speech-settings-voice-discovery-output.v1\",\"status\":\"ready\",\"provider\":\"edge-tts\",\"catalogVersion\":\"fake-v6\",\"catalogRevision\":\"edge-tts-20260901T100000Z-cccccccccccc\",\"catalogSha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"updatedAtUtc\":\"2026-09-01T10:00:00Z\",\"voices\":[]}"; }
            else { kind = "speech-speaker-preview-result"; contractVersion = "1.0.0"; status = "completed"; json = "{\"schemaVersion\":\"speech-settings-speaker-preview-output.v1\",\"status\":\"completed\"}"; }
            ModuleActionPayloads.Add(payload);
            var outputRef = "module-actions/" + actionId + "/result.json"; var outputBytes = new UTF8Encoding(false).GetBytes(json); WriteOnce(ResolveWithin(jobsRoot, outputRef), outputBytes);
            string catalogRef = null; string catalogHash = null;
            if (operation == "speech.voices") { catalogHash = Sha256(outputBytes); catalogRef = "module-state/speech/voice-catalog/" + catalogHash + ".json"; WriteOnce(ResolveWithin(jobsRoot, catalogRef), outputBytes); }
            return Envelope(correlationId, "module-action-receipt", true, new Dictionary<string, object>
            {
                { "schemaVersion", "knowledge-clipper-module-action-receipt.v6" }, { "actionId", actionId }, { "operation", operation }, { "accepted", true }, { "status", status },
                { "inputRef", inputRef }, { "inputSha256", inputHash }, { "outputRef", outputRef }, { "outputSha256", Sha256(outputBytes) }, { "catalogSnapshotRef", catalogRef }, { "catalogSnapshotSha256", catalogHash },
                { "externalWriteCount", 0 }, { "physicalAudio", operation == "speech.preview" }, { "automaticRetryAllowed", false }, { "idempotencyKey", idempotencyKey }, { "completedAt", "2026-09-01T10:00:00Z" }, { "error", null }, { "executionStatus", "completed" }, { "taskId", null }, { "runId", null }, { "artifactRootRef", null },
                { "outputRefs", new object[] { new Dictionary<string, object> { { "referenceType", "file" }, { "kind", kind }, { "contractVersion", contractVersion }, { "rootRef", "jobs-root" }, { "path", outputRef }, { "expectedSha256", Sha256(outputBytes) } } } }
            }, null, "1.7");
        }

        private IDictionary<string, object> QueryResponse(string correlationId, IDictionary<string, object> query)
        {
            if (query == null) throw new InvalidDataException("query payload missing");
            QueryTypes.Add(Convert.ToString(query["type"])); QuerySchemaVersions.Add(Convert.ToString(query["schemaVersion"]));
            var items = new List<object>();
            var requestedTask = Convert.ToString(query["type"]) == "task.get" ? Convert.ToString(query["taskId"]) : lastTaskId;
            if (!string.IsNullOrWhiteSpace(lastTaskId) && (Convert.ToString(query["type"]) != "task.get" || requestedTask == lastTaskId))
            {
                var status = ocrHumanResolutionScenario ? ocrStatus : "waiting_input";
                var waitingForInput = status == "waiting_input";
                var item = new Dictionary<string, object>
                {
                    { "taskId", lastTaskId }, { "operationId", "operation-fake" }, { "intent", lastIntent ?? "clip.question" },
                    { "status", status }, { "createdAt", "2026-08-25T12:00:00Z" }, { "updatedAt", "2026-08-25T12:00:01Z" },
                    { "attentionRequired", waitingForInput }, { "replayable", false }, { "summaryCode", "task." + status }
                };
                if (Convert.ToString(query["schemaVersion"]) == "2.0.0" || Convert.ToString(query["schemaVersion"]) == "2.2.0" || Convert.ToString(query["schemaVersion"]) == "2.3.0" || Convert.ToString(query["schemaVersion"]) == "2.4.0")
                {
                    item.Add("decisionKind", waitingForInput ? DecisionKind : null);
                    item.Add("decisionTargetToken", waitingForInput ? DecisionTargetToken : null);
                }
                if (Convert.ToString(query["schemaVersion"]) == "2.2.0" || Convert.ToString(query["schemaVersion"]) == "2.3.0" || Convert.ToString(query["schemaVersion"]) == "2.4.0")
                {
                    var question = lastIntent == "clip.question" && QuestionAnswerRef != null;
                    item.Add("answerAvailable", question);
                    item.Add("questionTargetToken", question ? QuestionTargetToken : null);
                    item.Add("questionTurnCount", question ? (object)QuestionTurnCount : null);
                    item.Add("questionAnswerRef", question ? QuestionAnswerRef : null);
                    if (Convert.ToString(query["schemaVersion"]) == "2.3.0" || Convert.ToString(query["schemaVersion"]) == "2.4.0")
                    {
                        item.Add("ocrHumanResolution", ocrHumanResolutionScenario && waitingForInput ? new Dictionary<string, object>
                        {
                            { "decisionKind", "ocr-human-resolution" }, { "targetToken", DecisionTargetToken },
                            { "sourceProofreadRef", ocrSourceProofreadRef }, { "originalTextRef", ocrOriginalTextRef }, { "candidateTextRef", ocrCandidateTextRef },
                            { "diff", new object[] { new Dictionary<string, object> { { "segmentIndex", 0 }, { "operation", "substitution" }, { "start", 0 }, { "end", 6 }, { "before", "OCR 原文" }, { "after", "AI 候选" } } } },
                            { "reason", new object[] { "insufficient_context" } },
                            { "allowedActions", new object[] { "accept_original", "submit_correction", "cancel" } }
                        } : null);
                    }
                    if (Convert.ToString(query["schemaVersion"]) == "2.4.0")
                    {
                        item.Add("activeQuestionSession", question ? new Dictionary<string, object>
                        {
                            { "targetToken", QuestionTargetToken },
                            { "binding", new Dictionary<string, object> { { "taskId", lastTaskId }, { "runId", "run-question-fake" }, { "stageId", "question-answer" }, { "attemptId", "attempt-question-fake" }, { "operationId", "operation-question-fake" } } },
                            { "sessionRef", "runs/run-question-fake/stages/question-answer/attempts/attempt-question-fake/question-session.json" },
                            { "sourceAnswerRef", new Dictionary<string, object> { { "artifactRef", "question.answer.source.fake" }, { "kind", "question.answer" }, { "contractVersion", "1.0.0" }, { "manifestPath", "artifacts/question.answer.source.fake/artifact.json" }, { "expectedSha256", new string('a', 64) } } },
                            { "displayAnswerRef", QuestionAnswerRef }, { "answeredTurnCount", QuestionTurnCount }
                        } : null);
                    }
                }
                items.Add(item);
            }
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", Convert.ToString(query["schemaVersion"]) }, { "queryId", Convert.ToString(query["queryId"]) },
                { "type", Convert.ToString(query["type"]) }, { "nextCursor", null }, { "items", items }
            };
            return Envelope(correlationId, "task-query-result", true, payload, null);
        }

        private void ValidateQuestionAction(string taskId, IDictionary<string, object> payload)
        {
            if (Convert.ToString(payload["targetToken"]) != QuestionTargetToken) throw new InvalidDataException("question target token was not forwarded unchanged");
            var action = Convert.ToString(payload["action"]);
            if (action == "question.follow-up")
            {
                if (Convert.ToInt32(payload["turnIndex"]) != QuestionTurnCount) throw new InvalidDataException("question turn index is stale");
                ValidateArtifactReference(taskId, payload["questionTurnRef"] as IDictionary<string, object>, "question.turn");
                if (!new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "action", "targetToken", "turnIndex", "questionTurnRef" })) throw new InvalidDataException("follow-up payload contains unsupported fields");
            }
            else if (action == "question.make-cards")
            {
                if (payload.ContainsKey("cardStructureRef")) ValidateArtifactReference(taskId, payload["cardStructureRef"] as IDictionary<string, object>, "question.card-structure");
                var allowed = payload.ContainsKey("cardStructureRef") ? new[] { "schemaVersion", "action", "targetToken", "cardStructureRef" } : new[] { "schemaVersion", "action", "targetToken" };
                if (!new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(allowed)) throw new InvalidDataException("make-cards payload contains unsupported fields");
            }
            else throw new InvalidDataException("unknown question action");
        }

        private void ValidateOcrHumanResolutionAction(string taskId, IDictionary<string, object> payload)
        {
            if (!ocrHumanResolutionScenario || ocrStatus != "waiting_input") throw new InvalidDataException("OCR resolution is not waiting for input");
            if (Convert.ToString(payload["decisionKind"]) != "ocr-human-resolution" || Convert.ToString(payload["targetToken"]) != DecisionTargetToken)
                throw new InvalidDataException("OCR resolution target token was not forwarded unchanged");
            if (string.IsNullOrWhiteSpace(Convert.ToString(payload["reason"]))) throw new InvalidDataException("OCR resolution reason is required");
            var action = Convert.ToString(payload["action"]);
            if (action == "submit_correction")
            {
                if (!new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "decisionKind", "targetToken", "action", "reason", "correctionRef", "diffs" }))
                    throw new InvalidDataException("OCR correction payload contains unsupported fields");
                ValidateArtifactReference(taskId, payload["correctionRef"] as IDictionary<string, object>, "ocr.human-correction.text");
                var diffs = ((System.Collections.IEnumerable)payload["diffs"]).Cast<object>().Select(value => value as IDictionary<string, object>).ToArray();
                if (diffs.Length == 0 || diffs.Any(value => value == null)) throw new InvalidDataException("OCR correction diffs are missing");
            }
            else if (action == "accept_original" || action == "cancel")
            {
                if (!new HashSet<string>(payload.Keys, StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "decisionKind", "targetToken", "action", "reason" }))
                    throw new InvalidDataException("OCR accept/cancel payload contains correction fields");
            }
            else throw new InvalidDataException("unknown OCR resolution action");
        }

        private void ValidateArtifactReference(string taskId, IDictionary<string, object> reference, string expectedKind)
        {
            if (reference == null || Convert.ToString(reference["kind"]) != expectedKind || Convert.ToString(reference["contractVersion"]) != "1.0.0") throw new InvalidDataException("artifact reference kind is invalid");
            var taskRoot = ResolveWithin(jobsRoot, taskId); var manifestPath = ResolveWithin(taskRoot, Convert.ToString(reference["manifestPath"]));
            var bytes = File.ReadAllBytes(manifestPath);
            if (!string.Equals(Sha256(bytes), Convert.ToString(reference["expectedSha256"]), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("artifact manifest hash mismatch");
            var manifest = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as IDictionary<string, object>;
            if (manifest == null || Convert.ToString(manifest["artifactId"]) != Convert.ToString(reference["artifactRef"]) || Convert.ToString(manifest["kind"]) != expectedKind) throw new InvalidDataException("artifact manifest identity mismatch");
            var files = ((System.Collections.IEnumerable)manifest["files"]).Cast<object>().Select(item => item as IDictionary<string, object>).ToArray();
            if (files.Length != 1) throw new InvalidDataException("artifact payload count invalid");
            var payloadPath = ResolveWithin(taskRoot, Convert.ToString(files[0]["path"])); var payloadBytes = File.ReadAllBytes(payloadPath);
            if (payloadBytes.LongLength != Convert.ToInt64(files[0]["bytes"]) || !string.Equals(Sha256(payloadBytes), Convert.ToString(files[0]["sha256"]), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("artifact payload integrity mismatch");
        }

        private void CreateQuestionAnswerArtifacts(string taskId)
        {
            answerVersion++;
            var taskRoot = ResolveWithin(jobsRoot, taskId); var folder = "runs/fake-question/outputs/answer-" + answerVersion.ToString("D4");
            var structuredPath = folder + "/answer.json"; var structuredBytes = new UTF8Encoding(false).GetBytes("{\"schemaVersion\":\"question-answer/1.0.0\",\"answer_text\":\"fake answer " + answerVersion + "\"}");
            WriteOnce(ResolveWithin(taskRoot, structuredPath), structuredBytes);
            var structuredId = "question.answer.structured." + answerVersion.ToString("D4"); var structuredManifestPath = folder + "/structured-artifact.json";
            var structuredManifestBytes = ManifestBytes(structuredId, "question.answer", "application/json", structuredPath, structuredBytes, new object[0]);
            WriteOnce(ResolveWithin(taskRoot, structuredManifestPath), structuredManifestBytes);
            var structuredRef = new Dictionary<string, object> { { "artifactRef", structuredId }, { "kind", "question.answer" }, { "contractVersion", "1.0.0" }, { "manifestPath", structuredManifestPath }, { "expectedSha256", Sha256(structuredManifestBytes) } };

            var textPath = folder + "/answer.txt"; var textBytes = new UTF8Encoding(false).GetBytes("fake answer " + answerVersion); WriteOnce(ResolveWithin(taskRoot, textPath), textBytes);
            var textId = "question.answer.text." + answerVersion.ToString("D4"); var textManifestPath = folder + "/text-artifact.json";
            var textManifestBytes = ManifestBytes(textId, "question.answer.text", "text/plain", textPath, textBytes, new object[] { structuredRef }); WriteOnce(ResolveWithin(taskRoot, textManifestPath), textManifestBytes);
            QuestionAnswerRef = new Dictionary<string, object> { { "artifactRef", textId }, { "kind", "question.answer.text" }, { "contractVersion", "1.0.0" }, { "manifestPath", textManifestPath }, { "expectedSha256", Sha256(textManifestBytes) } };
        }

        private IDictionary<string, object> CreateArtifact(string taskId, string artifactId, string kind, string mediaType, string content)
        {
            var taskRoot = ResolveWithin(jobsRoot, taskId);
            var folder = "runs/fake-ocr/outputs/" + artifactId;
            var extension = mediaType == "text/plain" ? ".txt" : ".json";
            var payloadPath = folder + "/payload" + extension;
            var payloadBytes = new UTF8Encoding(false).GetBytes(content);
            WriteOnce(ResolveWithin(taskRoot, payloadPath), payloadBytes);
            var manifestPath = folder + "/artifact.json";
            var manifestBytes = ManifestBytes(artifactId, kind, mediaType, payloadPath, payloadBytes, new object[0]);
            WriteOnce(ResolveWithin(taskRoot, manifestPath), manifestBytes);
            return new Dictionary<string, object> { { "artifactRef", artifactId }, { "kind", kind }, { "contractVersion", "1.0.0" }, { "manifestPath", manifestPath }, { "expectedSha256", Sha256(manifestBytes) } };
        }

        private byte[] ManifestBytes(string artifactId, string kind, string mediaType, string path, byte[] payload, object[] inputRefs)
        {
            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "artifactId", artifactId }, { "kind", kind }, { "contractVersion", "1.0.0" }, { "mediaType", mediaType },
                { "files", new object[] { new Dictionary<string, object> { { "path", path }, { "sha256", Sha256(payload) }, { "bytes", payload.LongLength }, { "mediaType", mediaType } } } },
                { "createdAt", "2026-08-26T00:00:00Z" }, { "producer", new Dictionary<string, object> { { "stageId", "fake-question" }, { "workerId", "core.fake" }, { "workerVersion", "1.0.0" }, { "attemptId", "attempt-fake" } } },
                { "inputRefs", inputRefs }, { "dataClass", "personal" }, { "metadata", new Dictionary<string, object> { { "entrypoint", path } } }
            };
            return new UTF8Encoding(false).GetBytes(serializer.Serialize(manifest));
        }

        private static void WriteOnce(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); if (File.Exists(path)) throw new IOException("immutable fake artifact exists"); File.WriteAllBytes(path, bytes); }
        private static string Sha256(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        private byte[] ReadFrame(Stream stream)
        {
            var bytes = new List<byte>();
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException("request ended before LF");
                if (value == (byte)'\n') break;
                bytes.Add((byte)value);
                if (bytes.Count > 1048577) throw new InvalidDataException("request frame too large");
            }
            if (bytes.Count > 0 && bytes[bytes.Count - 1] == (byte)'\r') { SawCrLf = true; bytes.RemoveAt(bytes.Count - 1); }
            if (bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) SawBom = true;
            return bytes.ToArray();
        }

        private static IDictionary<string, object> Envelope(string correlationId, string messageType, bool ok, object payload, object error, string protocolVersion = "1.0")
        {
            return new Dictionary<string, object>
            {
                { "protocolVersion", protocolVersion }, { "correlationId", correlationId }, { "messageType", messageType },
                { "ok", ok }, { "payload", payload }, { "error", error }
            };
        }

        private static string ResolveWithin(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("relative path missing");
            var normalizedRoot = Path.GetFullPath(root);
            var target = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (target != normalizedRoot && !target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("path escaped root");
            return target;
        }
    }
}
