using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Desktop.Core
{
    internal static class CommonContractAdapter
    {
        internal const string CommonSchemaVersion = "1.0.0";

        internal static CommonTaskCommandV1 BuildSubmitCommand(string commandId, string operationId, string payloadRef, string idempotencyKey, DateTime issuedAtUtc)
        {
            return BuildCommand(commandId, operationId, null, "task.submit", payloadRef, idempotencyKey, issuedAtUtc);
        }

        internal static CommonTaskCommandV1 BuildCommand(string commandId, string operationId, string taskId, string type, string payloadRef, string idempotencyKey, DateTime issuedAtUtc)
        {
            return new CommonTaskCommandV1
            {
                schemaVersion = CommonSchemaVersion,
                commandId = commandId,
                operationId = operationId,
                taskId = taskId,
                type = type,
                issuedAt = issuedAtUtc.ToUniversalTime().ToString("o"),
                idempotencyKey = idempotencyKey,
                payloadRef = payloadRef,
                expectedState = null
            };
        }

        internal static CommonTaskQueryV1 BuildRecentQuery(string queryId, int limit)
        {
            if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException("limit");
            return new CommonTaskQueryV1 { schemaVersion = CommonSchemaVersion, queryId = queryId, type = "task.query_recent", taskId = null, limit = limit, cursor = null, statusFilter = null };
        }

        internal static CommonTaskQueryV1 BuildRecentQueryV2(string queryId, int limit)
        {
            if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException("limit");
            return new CommonTaskQueryV1 { schemaVersion = "2.0.0", queryId = queryId, type = "task.query_recent", taskId = null, limit = limit, cursor = null, statusFilter = null };
        }

        internal static CommonTaskQueryV1 BuildRecentQueryV22(string queryId, int limit)
        {
            if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException("limit");
            return new CommonTaskQueryV1 { schemaVersion = "2.2.0", queryId = queryId, type = "task.query_recent", taskId = null, limit = limit, cursor = null, statusFilter = null };
        }

        internal static CommonTaskQueryV1 BuildTaskGetV22(string queryId, string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("taskId");
            return new CommonTaskQueryV1 { schemaVersion = "2.2.0", queryId = queryId, type = "task.get", taskId = taskId, limit = 0, cursor = null, statusFilter = null };
        }

        internal static CommonTaskQueryV1 BuildRecentQueryV23(string queryId, int limit)
        {
            if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException("limit");
            return new CommonTaskQueryV1
            {
                schemaVersion = "2.3.0", queryId = queryId, type = "task.query_recent", taskId = null, limit = limit, cursor = null,
                statusFilter = new List<string> { "created", "validating", "queued", "running", "waiting_input", "retry_wait", "cancelling", "succeeded", "partial", "failed", "cancelled" }
            };
        }

        internal static CommonTaskQueryV1 BuildTaskGetV23(string queryId, string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("taskId");
            return new CommonTaskQueryV1 { schemaVersion = "2.3.0", queryId = queryId, type = "task.get", taskId = taskId, limit = 0, cursor = null, statusFilter = null };
        }

        internal static CommonTaskQueryV1 BuildTaskGetV24(string queryId, string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("taskId");
            return new CommonTaskQueryV1 { schemaVersion = "2.4.0", queryId = queryId, type = "task.get", taskId = taskId, limit = 0, cursor = null, statusFilter = null };
        }

        internal static SubmissionResult MapReceipt(CommonTaskCommandReceiptV1 receipt)
        {
            if (receipt == null || receipt.schemaVersion != CommonSchemaVersion) return new SubmissionResult { accepted = false, taskId = null, noticeCode = "common.receipt-invalid" };
            return new SubmissionResult { accepted = receipt.accepted, taskId = receipt.taskId, noticeCode = receipt.accepted ? "common.command-accepted" : "common.command-rejected" };
        }

        internal static RecentTaskProjection MapRecent(CommonTaskQueryResultV1 result)
        {
            if (result == null || (result.schemaVersion != CommonSchemaVersion && result.schemaVersion != "2.0.0" && result.schemaVersion != "2.2.0" && result.schemaVersion != "2.3.0" && result.schemaVersion != "2.4.0") || result.items == null || result.items.Count == 0)
                return new RecentTaskProjection { available = false, taskId = null, displayState = "none" };
            var item = result.items[0];
            var v2 = result.schemaVersion == "2.0.0" || result.schemaVersion == "2.2.0" || result.schemaVersion == "2.3.0" || result.schemaVersion == "2.4.0";
            var hasLegacyQuestion = (result.schemaVersion == "2.2.0" || result.schemaVersion == "2.3.0") && item.answerAvailable && AutomationCatalogV11.IsDecisionTargetToken(item.questionTargetToken) && item.questionTurnCount.HasValue && item.questionTurnCount.Value >= 1 && item.questionAnswerRef != null && item.questionAnswerRef.kind == "question.answer.text";
            var active = result.schemaVersion == "2.4.0" ? item.activeQuestionSession : null;
            var hasActiveQuestion = item.answerAvailable && IsActiveQuestionSession(item, active);
            var questionReady = hasLegacyQuestion || hasActiveQuestion;
            var ocr = result.schemaVersion == "2.3.0" || result.schemaVersion == "2.4.0" ? MapOcrHumanResolution(item) : null;
            return new RecentTaskProjection
            {
                available = true, taskId = item.taskId, intent = item.intent, displayState = MapStatus(item.status),
                decisionKind = v2 ? item.decisionKind : null, decisionTargetToken = v2 ? item.decisionTargetToken : null,
                attentionRequired = item.attentionRequired,
                answerAvailable = questionReady, questionTargetToken = questionReady ? item.questionTargetToken : null,
                questionTurnCount = questionReady ? item.questionTurnCount : null, questionAnswerRef = questionReady ? item.questionAnswerRef : null,
                questionSessionRef = hasActiveQuestion ? active.sessionRef : null,
                ocrHumanResolution = ocr
            };
        }

        private static bool IsActiveQuestionSession(CommonTaskQueryItemV1 item, CommonActiveQuestionSessionV1 active)
        {
            return item != null && active != null && active.binding != null &&
                AutomationCatalogV11.IsDecisionTargetToken(active.targetToken) && active.targetToken == item.questionTargetToken &&
                active.binding.taskId == item.taskId && !string.IsNullOrWhiteSpace(active.binding.runId) && !string.IsNullOrWhiteSpace(active.binding.stageId) &&
                !string.IsNullOrWhiteSpace(active.binding.attemptId) && !string.IsNullOrWhiteSpace(active.binding.operationId) &&
                !string.IsNullOrWhiteSpace(active.sessionRef) && active.answeredTurnCount >= 1 && item.questionTurnCount == active.answeredTurnCount &&
                IsArtifact(active.sourceAnswerRef, "question.answer") && IsArtifact(active.displayAnswerRef, "question.answer.text") &&
                item.questionAnswerRef != null && active.displayAnswerRef.artifactRef == item.questionAnswerRef.artifactRef &&
                active.displayAnswerRef.manifestPath == item.questionAnswerRef.manifestPath && active.displayAnswerRef.expectedSha256 == item.questionAnswerRef.expectedSha256;
        }

        private static OcrHumanResolutionProjection MapOcrHumanResolution(CommonTaskQueryItemV1 item)
        {
            var value = item == null ? null : item.ocrHumanResolution;
            if (value == null || item.status != "waiting_input" || !item.attentionRequired || item.decisionKind != "ocr-human-resolution" ||
                !AutomationCatalogV11.IsDecisionTargetToken(item.decisionTargetToken) || value.decisionKind != "ocr-human-resolution" ||
                value.targetToken != item.decisionTargetToken || !AutomationCatalogV11.IsDecisionTargetToken(value.targetToken) ||
                !IsArtifact(value.sourceProofreadRef, "ocr-proofread-bundle") || !IsArtifact(value.originalTextRef, "ocr.original.text") ||
                !IsArtifact(value.candidateTextRef, "ocr.candidate.text") || value.diff == null || value.reason == null || value.reason.Length == 0 ||
                value.allowedActions == null || !value.allowedActions.SequenceEqual(new[] { "accept_original", "submit_correction", "cancel" })) return null;
            var serializer = new JavaScriptSerializer { MaxJsonLength = NamedPipeCommonIpcClient.MaximumMessageBytes };
            return new OcrHumanResolutionProjection
            {
                decisionKind = value.decisionKind, targetToken = value.targetToken,
                sourceProofreadRef = value.sourceProofreadRef, originalTextRef = value.originalTextRef, candidateTextRef = value.candidateTextRef,
                diffJson = serializer.Serialize(value.diff), reasonJson = serializer.Serialize(value.reason),
                allowedActions = new List<string>(value.allowedActions)
            };
        }

        private static bool IsArtifact(QuestionAnswerArtifactReference value, string kind)
        {
            return value != null && !string.IsNullOrWhiteSpace(value.artifactRef) && value.artifactRef.Length <= 128 && value.kind == kind &&
                value.contractVersion == "1.0.0" && !string.IsNullOrWhiteSpace(value.manifestPath) && IsSha256(value.expectedSha256);
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (var i = 0; i < value.Length; i++) if (!((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f'))) return false;
            return true;
        }

        internal static string MapStatus(string status)
        {
            switch (status)
            {
                case "created":
                case "validating":
                case "queued":
                case "retry_wait": return "queued";
                case "running":
                case "cancelling": return "running";
                case "waiting_input": return "awaiting-user";
                case "succeeded": return "succeeded";
                case "partial": return "partial";
                case "failed": return "failed";
                case "cancelled": return "cancelled";
                default: return "unknown";
            }
        }
    }

    internal sealed class CommonTaskCommandV1
    {
        public string schemaVersion { get; set; }
        public string commandId { get; set; }
        public string operationId { get; set; }
        public string taskId { get; set; }
        public string type { get; set; }
        public string issuedAt { get; set; }
        public string idempotencyKey { get; set; }
        public string payloadRef { get; set; }
        public string expectedState { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            var value = new Dictionary<string, object>
            {
                { "schemaVersion", schemaVersion }, { "commandId", commandId }, { "operationId", operationId }, { "taskId", taskId },
                { "type", type }, { "issuedAt", issuedAt }, { "idempotencyKey", idempotencyKey }, { "payloadRef", payloadRef }
            };
            if (!string.IsNullOrWhiteSpace(expectedState)) value.Add("expectedState", expectedState);
            return value;
        }
    }

    internal sealed class CommonTaskCommandReceiptV1
    {
        public string schemaVersion { get; set; }
        public string commandId { get; set; }
        public bool accepted { get; set; }
        public string taskId { get; set; }
        public string operationId { get; set; }
        public string status { get; set; }
        public string timestamp { get; set; }
        public object error { get; set; }
        public string durableEventRef { get; set; }
    }

    internal sealed class CommonStageControlRequestV1
    {
        public string taskId { get; set; }
        public string operation { get; set; }
        public string selector { get; set; }
        public string reason { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            return new Dictionary<string, object>
            {
                { "taskId", taskId }, { "operation", operation }, { "selector", selector }, { "reason", reason }
            };
        }
    }

    internal sealed class CommonStageControlReceiptV1
    {
        public string schemaVersion { get; set; }
        public string controlId { get; set; }
        public bool accepted { get; set; }
        public string taskId { get; set; }
        public string operation { get; set; }
        public string selector { get; set; }
        public string reason { get; set; }
        public string status { get; set; }
        public CommonStageControlResolutionV1 resolved { get; set; }
        public string timestamp { get; set; }
        public string durableEventRef { get; set; }
        public string receiptRef { get; set; }
        public CommonStageControlErrorV1 error { get; set; }
    }

    internal sealed class CommonStageControlResolutionV1
    {
        public string runId { get; set; }
        public string stageId { get; set; }
        public string attemptId { get; set; }
        public string operationId { get; set; }
        public string cancelSignalRef { get; set; }
    }

    internal sealed class CommonStageControlErrorV1
    {
        public string code { get; set; }
        public string category { get; set; }
        public bool retryable { get; set; }
        public string message { get; set; }
    }

    internal sealed class CommonModuleActionRequestV1
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public string payloadRef { get; set; }
        public string idempotencyKey { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", schemaVersion }, { "actionId", actionId }, { "operation", operation },
                { "payloadRef", payloadRef }, { "idempotencyKey", idempotencyKey }
            };
        }
    }

    internal sealed class CommonModuleActionReceiptV1
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public bool accepted { get; set; }
        public string status { get; set; }
        public string inputRef { get; set; }
        public string inputSha256 { get; set; }
        public string outputRef { get; set; }
        public string outputSha256 { get; set; }
        public string catalogSnapshotRef { get; set; }
        public string catalogSnapshotSha256 { get; set; }
        public int externalWriteCount { get; set; }
        public bool physicalAudio { get; set; }
        public bool automaticRetryAllowed { get; set; }
        public string idempotencyKey { get; set; }
        public string completedAt { get; set; }
        public CommonModuleActionErrorV1 error { get; set; }
    }

    internal sealed class CommonModuleActionErrorV1
    {
        public string code { get; set; }
        public string message { get; set; }
        public bool retryable { get; set; }
    }

    internal sealed class CommonModuleActionRequestV2
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public string payloadRef { get; set; }
        public string idempotencyKey { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", schemaVersion }, { "actionId", actionId }, { "operation", operation },
                { "payloadRef", payloadRef }, { "idempotencyKey", idempotencyKey }
            };
        }
    }

    internal sealed class CommonModuleActionReceiptV2
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public bool accepted { get; set; }
        public string status { get; set; }
        public string inputRef { get; set; }
        public string inputSha256 { get; set; }
        public string outputRef { get; set; }
        public string outputSha256 { get; set; }
        public string catalogSnapshotRef { get; set; }
        public string catalogSnapshotSha256 { get; set; }
        public int externalWriteCount { get; set; }
        public bool physicalAudio { get; set; }
        public bool automaticRetryAllowed { get; set; }
        public string idempotencyKey { get; set; }
        public string completedAt { get; set; }
        public CommonModuleActionErrorV1 error { get; set; }
    }

    internal sealed class CommonModuleActionRequestV3
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public string payloadRef { get; set; }
        public string idempotencyKey { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", schemaVersion }, { "actionId", actionId }, { "operation", operation },
                { "payloadRef", payloadRef }, { "idempotencyKey", idempotencyKey }
            };
        }
    }

    internal sealed class CommonModuleActionReceiptV3
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public bool accepted { get; set; }
        public string status { get; set; }
        public string inputRef { get; set; }
        public string inputSha256 { get; set; }
        public string outputRef { get; set; }
        public string outputSha256 { get; set; }
        public string catalogSnapshotRef { get; set; }
        public string catalogSnapshotSha256 { get; set; }
        public int externalWriteCount { get; set; }
        public bool physicalAudio { get; set; }
        public bool automaticRetryAllowed { get; set; }
        public string idempotencyKey { get; set; }
        public string completedAt { get; set; }
        public CommonModuleActionErrorV1 error { get; set; }
    }

    internal sealed class CommonModuleActionRequestV4
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public string payloadRef { get; set; }
        public string idempotencyKey { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", schemaVersion }, { "actionId", actionId }, { "operation", operation },
                { "payloadRef", payloadRef }, { "idempotencyKey", idempotencyKey }
            };
        }
    }

    internal sealed class CommonModuleArtifactOutputV4
    {
        public string referenceType { get; set; }
        public string kind { get; set; }
        public string contractVersion { get; set; }
        public string rootRef { get; set; }
        public string path { get; set; }
        public string expectedSha256 { get; set; }
    }

    internal sealed class CommonModuleActionReceiptV4
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public bool accepted { get; set; }
        public string status { get; set; }
        public string inputRef { get; set; }
        public string inputSha256 { get; set; }
        public string outputRef { get; set; }
        public string outputSha256 { get; set; }
        public int externalWriteCount { get; set; }
        public bool physicalAudio { get; set; }
        public bool automaticRetryAllowed { get; set; }
        public string idempotencyKey { get; set; }
        public string completedAt { get; set; }
        public CommonModuleActionErrorV1 error { get; set; }
        public string executionStatus { get; set; }
        public string taskId { get; set; }
        public string runId { get; set; }
        public string artifactRootRef { get; set; }
        public List<CommonModuleArtifactOutputV4> outputRefs { get; set; }
    }

    internal sealed class CommonModuleActionRequestV5
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public string payloadRef { get; set; }
        public string idempotencyKey { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", schemaVersion }, { "actionId", actionId }, { "operation", operation },
                { "payloadRef", payloadRef }, { "idempotencyKey", idempotencyKey }
            };
        }
    }

    internal sealed class CommonModuleActionReceiptV5
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public bool accepted { get; set; }
        public string status { get; set; }
        public string inputRef { get; set; }
        public string inputSha256 { get; set; }
        public string outputRef { get; set; }
        public string outputSha256 { get; set; }
        public int externalWriteCount { get; set; }
        public bool physicalAudio { get; set; }
        public bool automaticRetryAllowed { get; set; }
        public string idempotencyKey { get; set; }
        public string completedAt { get; set; }
        public CommonModuleActionErrorV1 error { get; set; }
        public string executionStatus { get; set; }
        public List<CommonModuleArtifactOutputV4> outputRefs { get; set; }
    }

    internal sealed class CommonModuleActionRequestV6
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public string payloadRef { get; set; }
        public string idempotencyKey { get; set; }
        internal IDictionary<string, object> ToWireObject()
        {
            return new Dictionary<string, object> { { "schemaVersion", schemaVersion }, { "actionId", actionId }, { "operation", operation }, { "payloadRef", payloadRef }, { "idempotencyKey", idempotencyKey } };
        }
    }

    internal sealed class CommonModuleActionReceiptV6
    {
        public string schemaVersion { get; set; }
        public string actionId { get; set; }
        public string operation { get; set; }
        public bool accepted { get; set; }
        public string status { get; set; }
        public string inputRef { get; set; }
        public string inputSha256 { get; set; }
        public string outputRef { get; set; }
        public string outputSha256 { get; set; }
        public string catalogSnapshotRef { get; set; }
        public string catalogSnapshotSha256 { get; set; }
        public int externalWriteCount { get; set; }
        public bool physicalAudio { get; set; }
        public bool automaticRetryAllowed { get; set; }
        public string idempotencyKey { get; set; }
        public string completedAt { get; set; }
        public CommonModuleActionErrorV1 error { get; set; }
        public string executionStatus { get; set; }
        public List<CommonModuleArtifactOutputV4> outputRefs { get; set; }
    }

    internal sealed class CommonTaskQueryV1
    {
        public string schemaVersion { get; set; }
        public string queryId { get; set; }
        public string type { get; set; }
        public string taskId { get; set; }
        public int limit { get; set; }
        public string cursor { get; set; }
        public List<string> statusFilter { get; set; }

        internal IDictionary<string, object> ToWireObject()
        {
            var value = new Dictionary<string, object> { { "schemaVersion", schemaVersion }, { "queryId", queryId }, { "type", type } };
            if (!string.IsNullOrWhiteSpace(taskId)) value.Add("taskId", taskId);
            if (type == "task.query_recent") value.Add("limit", limit);
            if (cursor != null) value.Add("cursor", cursor);
            if (statusFilter != null) value.Add("statusFilter", statusFilter);
            return value;
        }
    }

    internal sealed class CommonTaskQueryResultV1
    {
        public string schemaVersion { get; set; }
        public string queryId { get; set; }
        public string type { get; set; }
        public string nextCursor { get; set; }
        public List<CommonTaskQueryItemV1> items { get; set; }
    }

    internal sealed class CommonTaskQueryItemV1
    {
        public string taskId { get; set; }
        public string operationId { get; set; }
        public string intent { get; set; }
        public string status { get; set; }
        public string createdAt { get; set; }
        public string updatedAt { get; set; }
        public bool attentionRequired { get; set; }
        public bool replayable { get; set; }
        public string summaryCode { get; set; }
        public string decisionKind { get; set; }
        public string decisionTargetToken { get; set; }
        public bool answerAvailable { get; set; }
        public string questionTargetToken { get; set; }
        public int? questionTurnCount { get; set; }
        public QuestionAnswerArtifactReference questionAnswerRef { get; set; }
        public CommonOcrHumanResolutionProjectionV1 ocrHumanResolution { get; set; }
        public CommonActiveQuestionSessionV1 activeQuestionSession { get; set; }
    }

    internal sealed class CommonActiveQuestionSessionV1
    {
        public string targetToken { get; set; }
        public CommonQuestionSessionBindingV1 binding { get; set; }
        public string sessionRef { get; set; }
        public QuestionAnswerArtifactReference sourceAnswerRef { get; set; }
        public QuestionAnswerArtifactReference displayAnswerRef { get; set; }
        public int answeredTurnCount { get; set; }
    }

    internal sealed class CommonQuestionSessionBindingV1
    {
        public string taskId { get; set; }
        public string runId { get; set; }
        public string stageId { get; set; }
        public string attemptId { get; set; }
        public string operationId { get; set; }
    }

    internal sealed class CommonOcrHumanResolutionProjectionV1
    {
        public string decisionKind { get; set; }
        public string targetToken { get; set; }
        public QuestionAnswerArtifactReference sourceProofreadRef { get; set; }
        public QuestionAnswerArtifactReference originalTextRef { get; set; }
        public QuestionAnswerArtifactReference candidateTextRef { get; set; }
        public object[] diff { get; set; }
        public object[] reason { get; set; }
        public string[] allowedActions { get; set; }
    }
}
