using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed record QuestionViewProjection(
        OperationStatus Status,
        string TaskId,
        int TurnCount,
        bool AnswerAvailable,
        string AnswerText,
        string NoticeCode,
        bool CanFollowUp,
        bool CanMakeCards);

    internal sealed record AnkiReadinessViewProjection(
        OperationStatus Status,
        bool CurrentTargetReady,
        bool OptionalRoutesUninitialized,
        string CurrentTarget,
        IReadOnlyList<string> UninitializedRoutes,
        string NoticeCode);

    internal sealed record ReadToAnkiViewProjection(
        string OperationId,
        ReadToAnkiPlaybackStatus Playback,
        ReadToAnkiPipelineStatus Pipeline,
        bool AttentionRequired,
        bool CanReplay,
        bool NeedsLanguageChoice,
        bool IsProcessing,
        string NoticeCode)
    {
        internal CardResolutionPrompt CardResolution { get; init; }
        internal PlaybackTarget PlaybackTarget { get; init; }
        internal bool CanStop { get; init; }
    }

    internal sealed record DesktopViewProjection(
        long Revision,
        QuestionViewProjection Question,
        AnkiReadinessViewProjection AnkiReadiness,
        ReadToAnkiViewProjection ReadToAnki);

    internal interface IDesktopApplicationView
    {
        void Render(DesktopViewProjection projection);
    }

    // This is the only production View switch seam. It carries typed Application commands
    // inward and typed state projections outward; shell position and sizing remain shell-owned.
    internal sealed class DesktopViewBinding : IDisposable
    {
        private readonly DesktopCompositionRoot application;
        private readonly IDesktopApplicationView view;
        private bool disposed;

        internal DesktopViewBinding(DesktopCompositionRoot application, IDesktopApplicationView view)
        {
            this.application = application ?? throw new ArgumentNullException(nameof(application));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            application.State.Changed += Render;
            Render(application.State.Snapshot());
        }

        internal Task InvokeAsync(InvocationSource source, ApplicationCommand command, CancellationToken cancellationToken)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DesktopViewBinding));
            return application.Commands.InvokeAsync(source, command, cancellationToken);
        }

        private void Render(DesktopApplicationState state)
        {
            var question = state.Question;
            var readToAnki = state.ReadToAnki;
            state.ModuleActions.TryGetValue(ModuleActionKind.AnkiProbe, out var anki);
            var readiness = anki?.AnkiReadiness;
            view.Render(new DesktopViewProjection(
                state.Revision,
                new QuestionViewProjection(
                    question.Status,
                    question.TaskId,
                    question.TurnCount,
                    question.AnswerAvailable,
                    question.AnswerText,
                    question.NoticeCode,
                    question.AnswerAvailable && IsSettled(question.Status),
                    question.AnswerAvailable && IsSettled(question.Status)),
                new AnkiReadinessViewProjection(
                    anki?.Status ?? OperationStatus.Idle,
                    readiness?.CurrentTargetReady ?? false,
                    readiness?.OptionalRoutesUninitialized ?? false,
                    readiness?.CurrentTarget,
                    readiness?.UninitializedRoutes ?? Array.Empty<string>(),
                    anki?.NoticeCode),
                new ReadToAnkiViewProjection(
                    readToAnki.OperationId,
                    readToAnki.Playback,
                    readToAnki.Pipeline,
                    readToAnki.AttentionRequired,
                    ReadToAnkiUseCases.CanReplay(readToAnki),
                    readToAnki.Pipeline == ReadToAnkiPipelineStatus.WaitingUserChoice && readToAnki.AttentionRequired,
                    IsReadToAnkiProcessing(readToAnki),
                    readToAnki.NoticeCode) { CardResolution = readToAnki.CardResolution, PlaybackTarget = readToAnki.PlaybackTarget, CanStop = readToAnki.CanStop }));
        }

        private static bool IsSettled(OperationStatus status) =>
            status == OperationStatus.Succeeded || status == OperationStatus.WaitingInput;

        private static bool IsReadToAnkiProcessing(ReadToAnkiFeatureState state) =>
            state.Playback is ReadToAnkiPlaybackStatus.Accepted or ReadToAnkiPlaybackStatus.Playing ||
            state.Pipeline is ReadToAnkiPipelineStatus.Accepted or ReadToAnkiPipelineStatus.Reviewing or ReadToAnkiPipelineStatus.CreatingCards or ReadToAnkiPipelineStatus.WritingAnki;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            application.State.Changed -= Render;
        }
    }
}
