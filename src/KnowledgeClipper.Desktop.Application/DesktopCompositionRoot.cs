using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed class DesktopCompositionRoot
    {
        internal DesktopCompositionRoot(
            ITaskCommandPort taskCommands,
            ITaskQueryPort taskQueries,
            IModuleActionPort moduleActions,
            IStageControlPort stageControl,
            IArtifactGateway artifacts,
            ISettingsRepository settings,
            ICredentialStore credentials,
            IWindowActivationPort windows,
            QuestionObservationPolicy questionObservationPolicy = null,
            ICurrentTextCapturePort currentTextCapture = null,
            IReadToAnkiInputArtifactPort readToAnkiInputArtifacts = null,
            IReadToAnkiPort readToAnki = null,
            ReadToAnkiObservationPolicy readToAnkiObservationPolicy = null,
            IFlashcardDraftPort flashcardDraft = null)
        {
            TaskCommands = taskCommands ?? throw new ArgumentNullException(nameof(taskCommands));
            TaskQueries = taskQueries ?? throw new ArgumentNullException(nameof(taskQueries));
            ModuleActions = moduleActions ?? throw new ArgumentNullException(nameof(moduleActions));
            StageControl = stageControl ?? throw new ArgumentNullException(nameof(stageControl));
            Artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            Windows = windows ?? throw new ArgumentNullException(nameof(windows));

            State = new DesktopStateStore();
            if (flashcardDraft != null) FlashcardDraftUseCases = new FlashcardDraftPreviewUseCases(flashcardDraft);
            Action<FlashcardDraftProjection> projectQuestionDraft = FlashcardDraftUseCases == null ? null : FlashcardDraftUseCases.Project;
            ModuleActionUseCases = new ModuleActionUseCases(ModuleActions, State);
            SettingsUseCases = new SettingsUseCases(Settings, State);
            QuestionObserver = new QuestionObserver(TaskQueries, Artifacts, State, questionObservationPolicy ?? QuestionObservationPolicy.Default, projectQuestionDraft);
            QuestionUseCases = new QuestionUseCases(TaskCommands, QuestionObserver, State);
            QuestionUseCases.Preview = FlashcardDraftUseCases;
            if (FlashcardDraftUseCases != null) FlashcardDraftUseCases.FollowUpEligibility = QuestionUseCases.PreviewFollowUpUnavailableReason;
            if ((currentTextCapture == null) != (readToAnkiInputArtifacts == null) || (currentTextCapture == null) != (readToAnki == null))
                throw new ArgumentException("read-to-anki-ports-must-be-bound-together");
            if (currentTextCapture != null)
            {
                Action<FlashcardDraftProjection> projectDraft = FlashcardDraftUseCases == null ? null : FlashcardDraftUseCases.Project;
                ReadToAnkiUseCases = new ReadToAnkiUseCases(currentTextCapture, readToAnkiInputArtifacts, readToAnki, State, readToAnkiObservationPolicy, projectDraft);
                if (FlashcardDraftUseCases != null) FlashcardDraftUseCases.ConfirmationObserver = ReadToAnkiUseCases.ObserveConfirmedDraft;
            }
            var handlers = new Dictionary<ApplicationCommandKind, Func<ApplicationCommand, CancellationToken, Task>>
            {
                [ApplicationCommandKind.AnkiProbe] = async (command, token) => { await ModuleActionUseCases.ExecuteAsync(ModuleActionKind.AnkiProbe, command.Arguments, token).ConfigureAwait(false); },
                [ApplicationCommandKind.RemNoteDiscoverTarget] = async (command, token) => { await ModuleActionUseCases.ExecuteAsync(ModuleActionKind.RemNoteDiscoverTarget, command.Arguments, token).ConfigureAwait(false); },
                [ApplicationCommandKind.RemNoteProbe] = async (command, token) => { await ModuleActionUseCases.ExecuteAsync(ModuleActionKind.RemNoteProbe, command.Arguments, token).ConfigureAwait(false); },
                [ApplicationCommandKind.QuestionInitial] = async (command, token) => { await QuestionUseCases.SubmitInitialAsync(command.Text, token).ConfigureAwait(false); },
                [ApplicationCommandKind.QuestionFollowUp] = async (command, token) => { await QuestionUseCases.SubmitFollowUpAsync(command.Text, token).ConfigureAwait(false); },
                [ApplicationCommandKind.QuestionMakeCards] = async (command, token) => { await QuestionUseCases.MakeCardsAsync(command.CardStructureId, token).ConfigureAwait(false); }
            };
            if (ReadToAnkiUseCases != null)
            {
                handlers[ApplicationCommandKind.ClipToAnki] = async (_, token) => { await ReadToAnkiUseCases.StartAsync(false, token).ConfigureAwait(false); };
                handlers[ApplicationCommandKind.ReadToAnki] = async (_, token) => { await ReadToAnkiUseCases.StartAsync(true, token).ConfigureAwait(false); };
                handlers[ApplicationCommandKind.ReadToAnkiReplay] = async (_, token) => { await ReadToAnkiUseCases.ReplayAsync(token).ConfigureAwait(false); };
                handlers[ApplicationCommandKind.ReadToAnkiStop] = async (command, token) => { await ReadToAnkiUseCases.StopAsync(command.PlaybackTarget, token).ConfigureAwait(false); };
                handlers[ApplicationCommandKind.ReadToAnkiResumeChoice] = async (command, token) =>
                {
                    if (!command.LanguageChoice.HasValue) throw new InvalidOperationException("read-to-anki-language-choice-required");
                    await ReadToAnkiUseCases.ResumeLanguageChoiceAsync(command.LanguageChoice.Value, token).ConfigureAwait(false);
                };
                handlers[ApplicationCommandKind.ReadToAnkiResolve] = async (command, token) =>
                {
                    if (command.CardResolution == null) throw new InvalidOperationException("read-to-anki-card-resolution-required");
                    await ReadToAnkiUseCases.ResolveCardAsync(command.CardResolution, token).ConfigureAwait(false);
                };
            }
            Commands = new UnifiedApplicationCommandBinding(new ApplicationCommandDispatcher(handlers));
        }

        internal ITaskCommandPort TaskCommands { get; }
        internal ITaskQueryPort TaskQueries { get; }
        internal IModuleActionPort ModuleActions { get; }
        internal IStageControlPort StageControl { get; }
        internal IArtifactGateway Artifacts { get; }
        internal ISettingsRepository Settings { get; }
        internal ICredentialStore Credentials { get; }
        internal IWindowActivationPort Windows { get; }
        internal DesktopStateStore State { get; }
        internal ModuleActionUseCases ModuleActionUseCases { get; }
        internal SettingsUseCases SettingsUseCases { get; }
        internal QuestionObserver QuestionObserver { get; }
        internal QuestionUseCases QuestionUseCases { get; }
        internal ReadToAnkiUseCases ReadToAnkiUseCases { get; }
        internal FlashcardDraftPreviewUseCases FlashcardDraftUseCases { get; }
        internal UnifiedApplicationCommandBinding Commands { get; }
    }
}
