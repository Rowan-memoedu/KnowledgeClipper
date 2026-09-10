using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.TestFixtures
{
    internal sealed class PlaybackFlowFixture : IReadPlaybackControlPort, IReadToAnkiPort, ICurrentTextCapturePort, IReadToAnkiInputArtifactPort
    {
        internal readonly List<PlaybackTarget> Stops = new();
        internal readonly List<string> Replays = new();
        internal readonly Queue<ReadToAnkiProjection> Projections = new();
        internal PlaybackControlSnapshot Control;
        internal TaskCompletionSource<PlaybackControlSnapshot> Terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ReplayCommand = "replay-current";
        internal bool RejectStop { get; set; }
        internal bool ThrowStop { get; set; }
        internal int ControlQueries;
        internal TaskCompletionSource<ReadToAnkiPlaybackCommandResult> ReplayGate { get; set; }
        internal TaskCompletionSource<ReadToAnkiProjection> ProjectionGate { get; set; }
        public Task<PlaybackControlSnapshot> GetPlaybackControlAsync(string operationId, CancellationToken token) { ControlQueries++; return Task.FromResult(Control); }
        public Task<PlaybackStopAcceptance> StopPlaybackAsync(PlaybackTarget target, CancellationToken token)
        { Stops.Add(target); if(ThrowStop) throw new InvalidOperationException("no_active_attempt"); return Task.FromResult(new PlaybackStopAcceptance(!RejectStop, "stop-command", 21, target)); }
        public Task<PlaybackControlSnapshot> WaitPlaybackStopAsync(PlaybackStopAcceptance accepted, CancellationToken token) => Terminal.Task;
        public Task<ReadToAnkiPlaybackCommandResult> ReplayAsync(string op, CancellationToken token)
        { Replays.Add(op); return ReplayGate?.Task ?? Task.FromResult(new ReadToAnkiPlaybackCommandResult(true, "replay-accepted") { OperationId = op, CommandId = ReplayCommand, AcceptedSequence = 40 }); }
        public Task<ReadToAnkiProjection> GetProjectionAsync(string op, CancellationToken token) => ProjectionGate?.Task ?? Task.FromResult(Projections.Dequeue());
        public Task<ReadToAnkiSubmissionResult> SubmitAsync(ReadToAnkiSubmission value, CancellationToken token) => throw new InvalidOperationException("fixture forbids pipeline restart");
        public Task<ReadToAnkiSubmissionResult> ResumeLanguageChoiceAsync(ReadToAnkiChoiceSubmission value, CancellationToken token) => throw new InvalidOperationException("fixture forbids choice");
        public Task<ReadToAnkiSubmissionResult> ResolveCardAsync(CardResolutionSubmission value, CancellationToken token) => throw new InvalidOperationException("fixture forbids resolution");
        public Task<CapturedPlainText> CaptureOnceAsync(CancellationToken token) => throw new InvalidOperationException("fixture forbids capture");
        public Task<ArtifactHandle> PublishCaptureTextAsync(ImmutablePlainText value, CancellationToken token) => throw new InvalidOperationException("fixture forbids artifact creation");
    }
}
