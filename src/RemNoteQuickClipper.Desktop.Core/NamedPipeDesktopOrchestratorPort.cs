using System;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class NamedPipeDesktopOrchestratorPort : IDesktopOrchestratorPort
    {
        private readonly ICommonIpcClient client;
        private readonly IDesktopCommonCommandSource commands;
        private string lastTaskId;

        internal NamedPipeDesktopOrchestratorPort(ICommonIpcClient client, IDesktopCommonCommandSource commands)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (commands == null) throw new ArgumentNullException("commands");
            this.client = client;
            this.commands = commands;
        }

        public SubmissionResult SubmitCapture(CaptureBundleV1 bundle)
        {
            return Send(commands.PrepareCapture(bundle), false);
        }

        public QuestionResult SubmitQuestion(string text, bool followUp, string taskId)
        {
            if (followUp && string.IsNullOrWhiteSpace(taskId)) return new QuestionResult { accepted = false, answerAvailable = false, noticeCode = "question.no-orchestrator-task" };
            try
            {
                RecentTaskProjection current = null;
                if (followUp)
                {
                    current = QueryTask(taskId);
                    if (current == null || !current.answerAvailable) return new QuestionResult { accepted = false, answerAvailable = false, noticeCode = "question.workflow-projection-unavailable" };
                }
                var receipt = client.SendCommand(commands.PrepareQuestion(text, followUp, taskId, current == null ? null : current.questionTargetToken, current == null ? null : current.questionTurnCount));
                var mapped = CommonContractAdapter.MapReceipt(receipt);
                if (mapped.accepted)
                {
                    lastTaskId = mapped.taskId;
                }
                return new QuestionResult { accepted = mapped.accepted, answerAvailable = false, taskId = mapped.taskId, noticeCode = mapped.noticeCode };
            }
            catch (CommonIpcException ex) { return new QuestionResult { accepted = false, answerAvailable = false, noticeCode = ex.Code }; }
            catch (Exception) { return new QuestionResult { accepted = false, answerAvailable = false, noticeCode = "desktop.command-preparation-failed" }; }
        }

        public SubmissionResult MakeCards(string taskId, string cardStructureRef)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return new SubmissionResult { accepted = false, noticeCode = "question.no-orchestrator-task" };
            var current = QueryTask(taskId);
            if (current == null || !current.answerAvailable) return new SubmissionResult { accepted = false, noticeCode = "question.workflow-projection-unavailable" };
            return Send(commands.PrepareMakeCards(cardStructureRef, taskId, current.questionTargetToken), false);
        }

        public SubmissionResult ProvideLanguagePracticeInput(string taskId, string decisionTargetToken, string selection)
        {
            return Send(commands.PrepareLanguagePracticeInput(taskId, decisionTargetToken, selection), false);
        }

        public SubmissionResult ProvideOcrHumanResolution(string taskId, string decisionTargetToken, string action, string correctedText, string candidateText)
        {
            return Send(commands.PrepareOcrHumanResolution(taskId, decisionTargetToken, action, correctedText, candidateText), false);
        }

        public PlaybackResult Replay()
        {
            var taskId = ResolveLatestTaskId();
            if (string.IsNullOrWhiteSpace(taskId)) return new PlaybackResult { accepted = false, noticeCode = "playback.no-recent-task" };
            var result = Send(commands.PrepareReplay(taskId), false);
            return new PlaybackResult { accepted = result.accepted, state = result.accepted ? "replay-available" : null, noticeCode = result.noticeCode };
        }

        public PlaybackResult StopPlayback()
        {
            var taskId = ResolveLatestTaskId();
            if (string.IsNullOrWhiteSpace(taskId)) return new PlaybackResult { accepted = false, state = null, noticeCode = "playback.no-recent-task" };
            try
            {
                var receipt = client.SendStageControl(commands.PreparePlaybackStop(taskId));
                var valid = receipt != null && receipt.schemaVersion == "2.3.0" && receipt.accepted && receipt.resolved != null;
                return new PlaybackResult
                {
                    accepted = valid,
                    state = valid ? "stop-requested" : null,
                    noticeCode = valid ? "playback.stop-requested" :
                        receipt != null && receipt.error != null ? receipt.error.code : "common.receipt-invalid"
                };
            }
            catch (CommonIpcException ex) { return new PlaybackResult { accepted = false, state = null, noticeCode = ex.Code }; }
            catch (Exception) { return new PlaybackResult { accepted = false, state = null, noticeCode = "desktop.command-preparation-failed" }; }
        }

        public RecentTaskProjection QueryRecentTask()
        {
            try
            {
                var recent = CommonContractAdapter.MapRecent(client.Query(commands.PrepareRecentQuery()));
                if (recent.available) lastTaskId = recent.taskId;
                return recent;
            }
            catch (CommonIpcException) { return new RecentTaskProjection { available = false, displayState = "unknown", taskId = null }; }
            catch (Exception) { return new RecentTaskProjection { available = false, displayState = "unknown", taskId = null }; }
        }

        public RecentTaskProjection QueryTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return new RecentTaskProjection { available = false, displayState = "unknown", taskId = null };
            try
            {
                var result = CommonContractAdapter.MapRecent(client.Query(commands.PrepareTaskGet(taskId)));
                if (result.available && !string.Equals(result.taskId, taskId, StringComparison.Ordinal)) return new RecentTaskProjection { available = false, displayState = "unknown", taskId = null };
                if (result.available) lastTaskId = result.taskId;
                return result;
            }
            catch (CommonIpcException) { return new RecentTaskProjection { available = false, displayState = "unknown", taskId = taskId }; }
            catch (Exception) { return new RecentTaskProjection { available = false, displayState = "unknown", taskId = taskId }; }
        }

        private SubmissionResult Send(CommonTaskCommandV1 command, bool question)
        {
            try
            {
                var result = CommonContractAdapter.MapReceipt(client.SendCommand(command));
                if (result.accepted)
                {
                    lastTaskId = result.taskId;
                }
                return result;
            }
            catch (CommonIpcException ex) { return new SubmissionResult { accepted = false, noticeCode = ex.Code }; }
            catch (Exception) { return new SubmissionResult { accepted = false, noticeCode = "desktop.command-preparation-failed" }; }
        }

        private string ResolveLatestTaskId()
        {
            if (!string.IsNullOrWhiteSpace(lastTaskId)) return lastTaskId;
            var recent = QueryRecentTask();
            return recent.available ? recent.taskId : null;
        }
    }
}
