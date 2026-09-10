using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.TestFixtures;

namespace KnowledgeClipper.Desktop.CoreGateway.Tests
{
    internal static partial class Program
    {
        private static async Task QuestionSessionSdkBindings()
        {
            // Synthetic opaque handle identities only: no Core artifacts or network are created.
            var frozen = (TypedArtifactHandle)RuntimeHelpers.GetUninitializedObject(typeof(TypedArtifactHandle));
            var input = (TypedArtifactHandle)RuntimeHelpers.GetUninitializedObject(typeof(TypedArtifactHandle));
            var config = "initial-configuration";
            var published = new List<string>();
            QuestionInitialRequest first = null;
            QuestionFollowUpRequest next = null;
            var makes = new List<QuestionMakeCardsRequest>();
            var bridge = new QuestionSessionSdkBridge(() => config,
                (json, ct) => { published.Add(json); return Task.FromResult(frozen); },
                (request, ct) => { first = request; return Task.FromResult(new QuestionOperationSubmission("initial-op", "real-response-session", 1, true)); },
                (request, ct) => { next = request; return Task.FromResult(new QuestionOperationSubmission("follow-op", request.SessionId, 2, true)); },
                (request, ct) => { makes.Add(request); return Task.FromResult(new QuestionOperationSubmission("make-op-" + makes.Count, request.SessionId, 3, true)); });
            await bridge.InitialAsync("initial-request", input, Array.Empty<TypedArtifactHandle>(), CancellationToken.None);
            config = "changed-settings-must-not-rebind";
            await bridge.FollowUpAsync("follow-request", first == null ? null : "real-response-session", input, CancellationToken.None);
            await bridge.MakeCardsAsync("make-request-one", "real-response-session", input, CancellationToken.None);
            await bridge.MakeCardsAsync("make-request-two", "real-response-session", input, CancellationToken.None);
            Assert(first.Authorization == null && next.Authorization == null, "frozen SDK initial and follow-up none policies carry no write authorization");
            Assert(published.Count == 1 && published[0] == "initial-configuration" && ReferenceEquals(first.AnkiConfiguration, frozen) && ReferenceEquals(next.AnkiConfiguration, frozen) && ReferenceEquals(makes[1].AnkiConfiguration, frozen), "session initial freezes one configuration handle across settings changes follow-up and make-cards");
            var a = bridge.AuthorizationForOperation("make-op-2", "confirm-one");
            var b = bridge.AuthorizationForOperation("make-op-2", "confirm-two");
            Assert(a.AuthorizationId != b.AuthorizationId && makes[0].Authorization.AuthorizationId != makes[1].Authorization.AuthorizationId && a.Scope == b.Scope, "explicit make-cards and confirmation each use a fresh public builder authorization with the locked scope");
            var rejects = 0;
            try { await bridge.FollowUpAsync("unknown", "borrowed-read-session", input, CancellationToken.None); } catch (SdkDomainException) { rejects++; }
            try { bridge.AuthorizationForOperation("initial-op", "stale-confirm"); } catch (SdkDomainException) { rejects++; }
            Assert(rejects == 2, "unknown sessions and superseded operation confirmations cannot borrow another authorization or configuration");
        }

        private static async Task QuestionBindingNegatives()
        {
            var fake = new QuestionFlowFixture(); var root = fake.Root();
            await root.QuestionUseCases.SubmitInitialAsync("legitimate initial", CancellationToken.None);
            var old = root.FlashcardDraftUseCases.Current.Draft;
            var oldBinding = new FlashcardDraftConfirmation(old.OperationId, old.DraftToken, old.Revision, old.Sha256);
            var count = fake.Submissions.Count;
            var stale = await root.QuestionUseCases.SubmitPreviewFollowUpAsync("stale", oldBinding with { Sha256 = new string('f', 64) }, CancellationToken.None);
            var whitespace = await root.QuestionUseCases.SubmitPreviewFollowUpAsync("  ", oldBinding, CancellationToken.None);
            var cancelled = await root.QuestionUseCases.SubmitPreviewFollowUpAsync("cancel", oldBinding, new CancellationToken(true));
            Assert(!stale.Accepted && !whitespace.Accepted && !cancelled.Accepted && fake.Submissions.Count == count, "rendered identity mismatch whitespace and pre-send cancellation create zero commands");
            root.FlashcardDraftUseCases.Project(new(FlashcardDraftStatus.AwaitingConfirmation, old with { Source = FlashcardDraftSource.ReadAloud }, 5, "read", true));
            var read = await root.QuestionUseCases.SubmitFollowUpAsync("cannot borrow question", CancellationToken.None);
            Assert(!read.Accepted && !root.FlashcardDraftUseCases.Current.CanFollowUp && fake.Submissions.Count == count, "read and clip draft cannot borrow a retained question session");
            fake.MissingSession = true;
            var missing = await root.QuestionUseCases.SubmitInitialAsync("missing session", CancellationToken.None);
            Assert(!missing.Accepted && root.State.Snapshot().Question.SessionId == null && root.State.Snapshot().Question.TaskId == missing.TaskId && root.State.Snapshot().Question.Status == OperationStatus.Unknown && fake.QueryCount == 1, "missing SessionId rejects before querying and never falls back to prior session or TaskId");
            var direct = QuestionReducers.Submitted(DesktopApplicationState.Empty with { Question = DesktopApplicationState.Empty.Question with { SessionId = "old" } }, new(true, "task-with-no-session", "accepted"));
            Assert(direct.Question.SessionId == null && direct.Question.Status == OperationStatus.Unknown, "reducer defense also rejects accepted missing-session receipts");

            var late = new QuestionFlowFixture { QueryGate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var latest = late.Root();
            var pending = latest.QuestionUseCases.SubmitInitialAsync("old pending query", CancellationToken.None);
            var oldTask = latest.State.Snapshot().Question.TaskId;
            latest.State.Reduce(value => QuestionReducers.Submitted(QuestionReducers.Submitting(value), new TaskCommandResult(true, "new-operation", "accepted") { SessionId = "new-session" }));
            var newer = QuestionFlowFixture.Draft("new-operation", 8);
            latest.FlashcardDraftUseCases.Project(new(FlashcardDraftStatus.AwaitingConfirmation, newer, 8, "new", true));
            late.QueryGate.SetResult(late.Projections[oldTask]); await pending;
            Assert(latest.State.Snapshot().Question.TaskId == "new-operation" && latest.State.Snapshot().Question.SessionId == "new-session" && latest.FlashcardDraftUseCases.Current.Draft.OperationId == "new-operation" && latest.FlashcardDraftUseCases.Current.Status == FlashcardDraftStatus.AwaitingConfirmation, "late old operation answer and submit continuation cannot overwrite newer operation session or draft");
            latest.State.Reduce(value => QuestionReducers.Projected(value, late.Projections[oldTask], new("late answer")));
            Assert(latest.State.Snapshot().Question.TaskId == "new-operation" && latest.State.Snapshot().Question.SessionId == "new-session", "projection reducer independently refuses mismatched operation identity");
        }
    }
}
