using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.Core;
using KnowledgeClipper.Desktop.TestFixtures;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        private static int PlaybackControlUiCanary(string root)
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("immutable-playback-ui-evidence-exists");
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var target = new PlaybackTarget("ui-read", "private-opaque-token", "initial-action", "attempt-initial");
            var playback = new PlaybackFlowFixture { Control = new(target.OperationId, ReadToAnkiPlaybackStatus.Playing, true, target, 10) };
            var q = new QuestionFlowFixture();
            var app = new DesktopCompositionRoot(q,q,q,q,q,q,q,q,null,playback,playback,playback,new(2,TimeSpan.Zero,TimeSpan.Zero),q);
            var controller = new DesktopController(new FakeCaptureProvider(null),new FakeScreenshotProvider(null),new FakeDesktopOrchestratorPort(),new FakeClock(DateTime.UtcNow),new FakeIdProvider());
            using var owner = new BoundClipperForm(LegacyDesktopAdapter.CreateBoundClipperDependencies(Config(Path.Combine(root,"local")),controller,false,app));
            try
            {
                owner.Show(); Pump(20);
                app.State.Reduce(v=>v with {ReadToAnki=new(target.OperationId,ReadToAnkiPlaybackStatus.Playing,ReadToAnkiPipelineStatus.CreatingCards,false,null)});
                owner.ContextMenuStrip.Show(owner,new Point(20,20)); Pump(20);
                var stop=owner.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(x=>x.Name=="knowledge-clipper.desktop.playback.stop");
                var status=Find<Button>(owner,"knowledge-clipper.desktop.status");
                Assert(stop.Enabled&&stop.AccessibleName==stop.Name,"real Stop menu enables only after typed target snapshot");
                using(var help=JsonDocument.Parse(status.AccessibleDescription))
                    Assert(help.RootElement.GetProperty("AttemptId").GetString()==target.AttemptId&&!status.AccessibleDescription.Contains(target.TargetToken),"status HelpText exposes round identity without target token");
                stop.PerformClick();Pump(10);
                Assert(playback.Stops.Count==1&&playback.Stops[0]==target&&app.State.Snapshot().ReadToAnki.Playback==ReadToAnkiPlaybackStatus.StopRequested&&status.Text.Contains("已请求停止"),"real Stop click captures one target and does not pretend accepted is stopped");
                stop.PerformClick();Pump(10);
                Assert(playback.Stops.Count==1,"second real Stop click produces no extra command");
                app.State.Reduce(v=>v with {ReadToAnki=v.ReadToAnki with {Pipeline=ReadToAnkiPipelineStatus.AwaitingCardConfirmation}});
                playback.Terminal.SetResult(new(target.OperationId,ReadToAnkiPlaybackStatus.Stopped,false,target,22));
                WaitUntil(()=>app.State.Snapshot().ReadToAnki.Playback==ReadToAnkiPlaybackStatus.Stopped,2000,"stopped proof");Pump(20);
                Assert(app.State.Snapshot().ReadToAnki.Pipeline==ReadToAnkiPipelineStatus.AwaitingCardConfirmation&&status.Text.Contains("重播"),"verified Stop preserves pending cards and enables replay after business result");
                playback.Control=new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,false,target,31);
                playback.ReplayGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
                playback.ProjectionGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
                status.PerformClick();Pump(20);
                owner.ContextMenuStrip.Close();owner.ContextMenuStrip.Show(owner,new Point(20,20));Pump(20);
                Assert(app.State.Snapshot().ReadToAnki.Playback==ReadToAnkiPlaybackStatus.Accepted&&!owner.CandidateCanaryReadToAnkiState.CanReplay&&!stop.Enabled,"actual menu refresh cannot apply old Idle while Replay submission has no command identity");
                playback.ReplayGate.SetResult(new(true,"accepted"){OperationId=target.OperationId,CommandId=playback.ReplayCommand,AcceptedSequence=40});Pump(20);
                owner.ContextMenuStrip.Close();owner.ContextMenuStrip.Show(owner,new Point(20,20));Pump(20);
                Assert(app.State.Snapshot().ReadToAnki.Playback==ReadToAnkiPlaybackStatus.Accepted&&!owner.CandidateCanaryReadToAnkiState.CanReplay&&!stop.Enabled,"actual menu refresh rejects previous round after Replay accepted but new target not registered");
                var replayTarget=target with {ActionCommandId=playback.ReplayCommand,AttemptId="attempt-replay",TargetToken="new-opaque-token"};
                playback.Control=new(target.OperationId,ReadToAnkiPlaybackStatus.Playing,true,replayTarget,41);
                owner.ContextMenuStrip.Close();owner.ContextMenuStrip.Show(owner,new Point(20,20));Pump(20);
                Assert(stop.Enabled&&app.State.Snapshot().ReadToAnki.PlaybackTarget==replayTarget,"actual menu refresh enables Stop only for current Replay target");
                playback.ProjectionGate.SetResult(new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,ReadToAnkiPipelineStatus.Completed,false,"natural") {ActionState="completed",TerminalCommandId=playback.ReplayCommand,TerminalSequence=42});
                WaitUntil(()=>app.State.Snapshot().ReadToAnki.Playback==ReadToAnkiPlaybackStatus.IdleReplayable,2000,"current Replay terminal");Pump(20);
                Assert(playback.Replays.Count==1&&playback.Replays[0]==target.OperationId&&app.State.Snapshot().ReadToAnki.Playback==ReadToAnkiPlaybackStatus.IdleReplayable&&!owner.CandidateCanaryReadToAnkiState.CanStop,"real Replay status click routes through the same Application and current-command observer with terminal CanStop false");
                Assert(app.State.Snapshot().ReadToAnki.Pipeline==ReadToAnkiPipelineStatus.AwaitingCardConfirmation&&q.Confirmations.Count==0&&q.Submissions.Count==0,"Stop and Replay never clear confirm state or invoke Question/export");
                owner.ContextMenuStrip.Close();playback.ReplayGate=null;playback.ProjectionGate=null;playback.ReplayCommand="replay-without-menu";
                var naturalTarget=replayTarget with {ActionCommandId=playback.ReplayCommand,AttemptId="attempt-no-menu",TargetToken="no-menu-token"};
                playback.Control=new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,false,naturalTarget,60);
                playback.Projections.Enqueue(new(target.OperationId,ReadToAnkiPlaybackStatus.IdleReplayable,ReadToAnkiPipelineStatus.Completed,false,"natural") {ActionState="completed",TerminalCommandId=playback.ReplayCommand,TerminalSequence=60});
                status.PerformClick();Pump(20);
                using(var help=JsonDocument.Parse(status.AccessibleDescription))
                    Assert(playback.Replays.Count==2&&help.RootElement.GetProperty("ActionCommandId").GetString()==naturalTarget.ActionCommandId&&help.RootElement.GetProperty("AttemptId").GetString()==naturalTarget.AttemptId&&!help.RootElement.GetProperty("CanStop").GetBoolean(),"natural Replay publishes its actual typed round identity without opening a menu");
                owner.ContextMenuStrip.Close();
                using(var bitmap=new Bitmap(owner.Width,owner.Height)){owner.DrawToBitmap(bitmap,owner.ClientRectangle);bitmap.Save(Path.Combine(root,"playback-ui.png"));}
                File.WriteAllText(Path.Combine(root,"receipt.json"),JsonSerializer.Serialize(new{status="PASS",assertions,stopCommands=playback.Stops.Count,replayCommands=playback.Replays.Count,physicalInput=false,externalCalls=0,speaker=0},new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine("WINDOWS_PLAYBACK_CONTROL PASS assertions="+assertions);return 0;
            }
            finally{owner.Close();Pump(10);}
        }
    }
}
