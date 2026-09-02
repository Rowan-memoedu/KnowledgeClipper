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

    internal sealed record DesktopViewProjection(
        long Revision,
        QuestionViewProjection Question,
        AnkiReadinessViewProjection AnkiReadiness);

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
                    anki?.NoticeCode)));
        }

        private static bool IsSettled(OperationStatus status) =>
            status == OperationStatus.Succeeded || status == OperationStatus.WaitingInput;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            application.State.Changed -= Render;
        }
    }
}
