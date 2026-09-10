using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.Application.Tests
{
    internal static partial class Program
    {
        private static ReadToAnkiPlaybackCommandResult ReplayAccepted(string op) => new(true, "replay-accepted")
        { OperationId = op, CommandId = "replay-new", AcceptedSequence = 40 };

        private static ReadToAnkiProjection ReplayProjection(string op, string command = "replay-new", long sequence = 40,
            string action = "completed", ReadToAnkiPlaybackStatus playback = ReadToAnkiPlaybackStatus.IdleReplayable) =>
            new(op, playback, ReadToAnkiPipelineStatus.Completed, false, "replay-" + action)
            { ActionState = action, ActiveCommandId = action == "running" ? command : null, TerminalCommandId = command, TerminalSequence = sequence };

        private static async Task ReplayIdentityRegression()
        {
            const string op = "read-replay-identity";
            var fake = new FakePorts();
            var root = fake.Root(readPolicy: new ReadToAnkiObservationPolicy(6, TimeSpan.Zero, TimeSpan.Zero), flashcardDraft: new FakeFlashcardDraftPort());
            root.State.Reduce(value => value with { ReadToAnki = new(op, ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "original-completed") });
            var draft = new FlashcardDraftRevision(op, "original-token", "original-revision", new string('a', 64), FlashcardDraftSource.ReadAloud,
                new[] { new FlashcardDraftCard("context", "r", new string('b', 64), "context front", "context back", "language_context"),
                        new FlashcardDraftCard("writing", "r", new string('c', 64), "writing front", "writing back", "language_writing") }, new[] { "Anki" });
            root.FlashcardDraftUseCases.Project(new(FlashcardDraftStatus.Completed, draft, 1, "completed", false));
            var retained = root.FlashcardDraftUseCases.Current;
            fake.ReplayResults.Enqueue(ReplayAccepted(op));
            fake.ReadToAnkiProjectionTasks.Enqueue(Task.FromResult(ReplayProjection(op, "old-natural-completion", 900)));
            fake.ReadToAnkiProjectionTasks.Enqueue(Task.FromResult(ReplayProjection(op, sequence: 39)));
            fake.ReadToAnkiProjectionTasks.Enqueue(Task.FromResult(ReplayProjection(op, action: "running")));
            var terminal = new TaskCompletionSource<ReadToAnkiProjection>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.ReadToAnkiProjectionTasks.Enqueue(terminal.Task);
            var accepted = await root.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None);
            await fake.ProjectionWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pending = root.State.Snapshot().ReadToAnki;
            Assert(accepted.CommandId == "replay-new" && accepted.AcceptedSequence == 40 && pending.PlaybackCommandId == "replay-new" && pending.PlaybackAcceptedSequence == 40,
                "Replay action command and sequence survive UseCase and state");
            Assert(pending.Playback == ReadToAnkiPlaybackStatus.Accepted && !root.ReadToAnkiUseCases.ObservationCompletion.IsCompleted,
                "late natural completion, lower sequence, and running with stale idle playback cannot release current Replay");
            var duplicate = await root.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None);
            Assert(!duplicate.Accepted && fake.ReplayOperationIds.Count == 1, "accepted-but-unsettled Replay blocks another click");
            terminal.SetResult(ReplayProjection(op));
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(root.State.Snapshot().ReadToAnki.Playback == ReadToAnkiPlaybackStatus.IdleReplayable, "only matching Replay command terminal releases replayability");
            Assert(ReferenceEquals(retained.Draft, root.FlashcardDraftUseCases.Current.Draft) && root.FlashcardDraftUseCases.Current.Status == retained.Status && retained.Draft.Cards.Count == 2 && retained.Draft.Cards.Select(c => c.CardId).SequenceEqual(new[] { "context", "writing" }),
                "Read both source retains identical two cards during Replay");
            Assert(fake.ReadToAnkiSubmissions.Count == 0 && fake.TextCaptureCount == 0 && fake.PublishedCaptureArtifacts.Count == 0,
                "Replay does not call capture, synth submission, Content or exporter route");

            foreach (var mode in new[] { "timeout", "query-error", "failed", "unknown", "missing-identity", "wrong-operation", "stopped" })
            {
                var f = new FakePorts(); var r = f.Root(readPolicy: OneReadProjection());
                r.State.Reduce(v => v with { ReadToAnki = new(op, ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, null) });
                f.ReplayResults.Enqueue(mode == "missing-identity" ? new(true, "accepted-without-identity") : mode == "wrong-operation" ? ReplayAccepted("foreign-op") : ReplayAccepted(op));
                if (mode == "query-error") f.ReadToAnkiProjectionTasks.Enqueue(Task.FromException<ReadToAnkiProjection>(new InvalidOperationException("offline")));
                else f.ReadToAnkiProjections.Enqueue(mode switch {
                    "failed" => ReplayProjection(op, action: "failed"),
                    "unknown" => ReplayProjection(op, action: "unknown_external_state"),
                    "stopped" => ReplayProjection(op, action: "cancelled", playback: ReadToAnkiPlaybackStatus.Stopped),
                    _ => ReplayProjection(op, "old-command") });
                await r.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None); await r.ReadToAnkiUseCases.ObservationCompletion;
                var expected = mode == "failed" ? ReadToAnkiPlaybackStatus.Failed : ReadToAnkiPlaybackStatus.Unknown;
                Assert(r.State.Snapshot().ReadToAnki.Playback == expected && r.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Completed,
                    "Replay " + mode + " preserves completed pipeline and exposes truthful playback state");
                await r.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None);
                Assert(f.ReplayOperationIds.Count == 1 && f.ReadToAnkiSubmissions.Count == 0, "Replay " + mode + " has no retry or pipeline resubmit");
            }
            var clip = new FakePorts(); var clipRoot = clip.Root();
            clipRoot.State.Reduce(v => v with { ReadToAnki = new("clip-op", ReadToAnkiPlaybackStatus.NotRequested, ReadToAnkiPipelineStatus.Completed, false, null) });
            Assert(!(await clipRoot.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None)).Accepted && clip.ReplayOperationIds.Count == 0, "Clip has zero playback commands");
            await ReplayDoesNotSupersedePipelineObservation();
            await LateReplayCannotOverwriteNewOperation();
        }

        private static async Task ReplayDoesNotSupersedePipelineObservation()
        {
            const string op = "parallel-read";
            var f = new FakePorts(); var r = f.Root(readPolicy: new(3, TimeSpan.Zero, TimeSpan.Zero));
            f.ReadToAnkiResults.Enqueue(new(true, op, "initial"));
            f.ReadToAnkiProjectionTasks.Enqueue(Task.FromResult(new ReadToAnkiProjection(op, ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.WritingAnki, false, "pipeline-active")));
            var pipeline = new TaskCompletionSource<ReadToAnkiProjection>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.ReadToAnkiProjectionTasks.Enqueue(pipeline.Task);
            f.ReadToAnkiProjectionTasks.Enqueue(Task.FromResult(ReplayProjection(op, action: "failed")));
            f.ReplayResults.Enqueue(ReplayAccepted(op));
            await r.ReadToAnkiUseCases.StartAsync(CancellationToken.None);
            await f.ProjectionWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await r.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None);
            Assert(r.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.WritingAnki && r.State.Snapshot().ReadToAnki.Playback == ReadToAnkiPlaybackStatus.Failed,
                "Replay failure never applies its pipeline projection over the independently running exporter pipeline");
            pipeline.SetResult(new(op, ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "original-pipeline-completed"));
            await r.ReadToAnkiUseCases.ObservationCompletion;
            Assert(r.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Completed && r.State.Snapshot().ReadToAnki.Playback == ReadToAnkiPlaybackStatus.Failed && f.ReadToAnkiSubmissions.Count == 1,
                "original pipeline observer survives Replay and its late natural-playback state cannot overwrite Replay failure");
        }

        private static async Task LateReplayCannotOverwriteNewOperation()
        {
            var f = new FakePorts { BlockReplay = true }; var r = f.Root(readPolicy: OneReadProjection());
            r.State.Reduce(v => v with { ReadToAnki = new("old-op", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, null) });
            f.ReplayResults.Enqueue(ReplayAccepted("old-op"));
            var old = r.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None);
            await f.ReplayEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            r.State.Reduce(v => v with { ReadToAnki = new("new-op", ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.Reviewing, false, "new-active") });
            f.ReplayRelease.SetResult(true); await old;
            Assert(r.State.Snapshot().ReadToAnki.OperationId == "new-op" && r.State.Snapshot().ReadToAnki.Playback == ReadToAnkiPlaybackStatus.Playing && r.State.Snapshot().ReadToAnki.NoticeCode == "new-active",
                "late Replay acceptance cannot mutate or observe a newer operation");
        }
    }
}
