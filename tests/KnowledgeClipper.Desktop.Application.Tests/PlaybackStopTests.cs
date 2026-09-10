using System;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.TestFixtures;

namespace KnowledgeClipper.Desktop.Application.Tests
{
    internal static partial class Program
    {
        private static async Task PlaybackStopTargetsRegression()
        {
            var target = new PlaybackTarget("stop-op", "opaque-token", "read-action", "attempt-1");
            foreach (var end in new[] { ReadToAnkiPlaybackStatus.Stopped, ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPlaybackStatus.Unknown })
            {
                var f = new PlaybackFlowFixture { Control = new(target.OperationId, ReadToAnkiPlaybackStatus.Playing, true, target, 10) };
                var s = new DesktopStateStore(); s.Reduce(v => v with { ReadToAnki = new(target.OperationId, ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.WritingAnki, false, null) });
                var u = new ReadToAnkiUseCases(f, f, f, s, new(2, TimeSpan.Zero, TimeSpan.Zero));
                await u.RefreshPlaybackControlAsync(CancellationToken.None);
                var accepted = await u.StopAsync(s.Snapshot().ReadToAnki.PlaybackTarget, CancellationToken.None);
                Assert(accepted.Accepted && s.Snapshot().ReadToAnki.Playback == ReadToAnkiPlaybackStatus.StopRequested && s.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.WritingAnki, "Stop acceptance is only requested; pipeline continues: " + end);
                Assert(!(await u.StopAsync(target, CancellationToken.None)).Accepted && f.Stops.Count == 1, "double Stop invokes one captured target: " + end);
                f.Terminal.SetResult(new(target.OperationId, end, false, target, 30));
                await u.ObservationCompletion;
                Assert(s.Snapshot().ReadToAnki.Playback == end && s.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.WritingAnki, "same-target worker proof controls terminal, including natural finish race: " + end);
                if (end != ReadToAnkiPlaybackStatus.Unknown)
                {
                    f.Control=new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,false,target with {ActionCommandId=f.ReplayCommand,AttemptId="replay-attempt"},41);
                    f.Projections.Enqueue(new(target.OperationId, ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "replayed") { ActionState="completed", TerminalCommandId=f.ReplayCommand, TerminalSequence=40 });
                    Assert((await u.ReplayAsync(CancellationToken.None)).Accepted, "verified terminal can replay from existing business result: " + end);
                    await u.ObservationCompletion;
                    Assert(f.Replays.Count == 1 && s.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.WritingAnki, "Replay never synthesizes or rewrites pipeline after Stop: " + end);
                }
                else Assert(!(await u.ReplayAsync(CancellationToken.None)).Accepted && f.Replays.Count == 0, "Unknown Stop cannot replay");
            }
            foreach (var changedState in new[] { false, true })
            {
                var f = new PlaybackFlowFixture { Control=new(target.OperationId, ReadToAnkiPlaybackStatus.Playing, true, target, 10) };
                var s = new DesktopStateStore(); s.Reduce(v => v with { ReadToAnki=new(target.OperationId, ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.Completed, false, null) });
                var u = new ReadToAnkiUseCases(f,f,f,s,new(1,TimeSpan.Zero,TimeSpan.Zero));
                await u.RefreshPlaybackControlAsync(CancellationToken.None); await u.StopAsync(target,CancellationToken.None);
                var next=target with { TargetToken="new-token", AttemptId="attempt-2", ActionCommandId="new-replay" };
                if(changedState) s.Reduce(v=>v with {ReadToAnki=v.ReadToAnki with {PlaybackTarget=next,Playback=ReadToAnkiPlaybackStatus.Playing,CanStop=true}});
                f.Terminal.SetResult(new(target.OperationId,ReadToAnkiPlaybackStatus.Stopped,false,changedState?target:next,31));
                await u.ObservationCompletion;
                Assert(s.Snapshot().ReadToAnki.Playback==(changedState?ReadToAnkiPlaybackStatus.Playing:ReadToAnkiPlaybackStatus.Unknown), "late/different-target Stop never claims new round stopped: "+changedState);
            }
            foreach(var currentProof in new[]{false,true})
            {
                var replayTarget=target with {ActionCommandId="replay-current"};
                var f=new PlaybackFlowFixture {Control=new(target.OperationId,ReadToAnkiPlaybackStatus.Stopped,false,currentProof?replayTarget:target,60)};
                f.Projections.Enqueue(new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,ReadToAnkiPipelineStatus.Completed,false,"cancelled") {ActionState="cancelled",TerminalCommandId=f.ReplayCommand,TerminalSequence=50});
                var s=new DesktopStateStore();s.Reduce(v=>v with {ReadToAnki=new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,ReadToAnkiPipelineStatus.Completed,false,null)});
                var u=new ReadToAnkiUseCases(f,f,f,s,new(1,TimeSpan.Zero,TimeSpan.Zero));await u.ReplayAsync(CancellationToken.None);await u.ObservationCompletion;
                Assert(s.Snapshot().ReadToAnki.Playback==(currentProof?ReadToAnkiPlaybackStatus.Stopped:ReadToAnkiPlaybackStatus.Unknown), "cancelled plus stale Idle needs this Replay round typed terminal proof: "+currentProof);
                Assert(ReadToAnkiUseCases.CanReplay(s.Snapshot().ReadToAnki)==currentProof,"only verified Stopped is replayable");
            }
            var waiting=new ReadToAnkiFeatureState("pending",ReadToAnkiPlaybackStatus.Stopped,ReadToAnkiPipelineStatus.Reviewing,false,null){VerifiedPlaybackTerminal=true};
            Assert(!ReadToAnkiUseCases.CanReplay(waiting)&&ReadToAnkiUseCases.CanReplay(waiting with {Pipeline=ReadToAnkiPipelineStatus.AwaitingCardConfirmation}),"Stopped waits for business result; pending card confirmation permits Replay");
            foreach(var mode in new[]{"rejected-natural","exception-natural","other-target","unavailable"})
            {
                var f=new PlaybackFlowFixture {Control=new(target.OperationId,ReadToAnkiPlaybackStatus.Playing,true,target,10),RejectStop=true,ThrowStop=mode=="exception-natural"};
                var s=new DesktopStateStore();s.Reduce(v=>v with {ReadToAnki=new(target.OperationId,ReadToAnkiPlaybackStatus.Playing,ReadToAnkiPipelineStatus.Completed,false,null)});
                var u=new ReadToAnkiUseCases(f,f,f,s,new(1,TimeSpan.Zero,TimeSpan.Zero));await u.RefreshPlaybackControlAsync(CancellationToken.None);
                f.Control=mode=="unavailable"?null:new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,false,mode=="other-target"?target with {AttemptId="foreign"}:target,30);
                var result=await u.StopAsync(target,CancellationToken.None);
                var natural=mode is "rejected-natural" or "exception-natural";
                Assert(!result.Accepted&&s.Snapshot().ReadToAnki.Playback==(natural?ReadToAnkiPlaybackStatus.IdleReplayable:ReadToAnkiPlaybackStatus.Unknown),"natural finish before stop acceptance is recovered only by same-target terminal: "+mode);
                Assert(f.Stops.Count==1&&f.ControlQueries==2&&f.Replays.Count==0&&s.Snapshot().ReadToAnki.Pipeline==ReadToAnkiPipelineStatus.Completed,"one read-only rejection check; no stop resend or automatic replay: "+mode);
            }
        }
    }
}
