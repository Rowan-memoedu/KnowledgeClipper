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
            QuestionObservationPolicy questionObservationPolicy = null)
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
            ModuleActionUseCases = new ModuleActionUseCases(ModuleActions, State);
            SettingsUseCases = new SettingsUseCases(Settings, State);
            QuestionObserver = new QuestionObserver(TaskQueries, Artifacts, State, questionObservationPolicy ?? QuestionObservationPolicy.Default);
            QuestionUseCases = new QuestionUseCases(TaskCommands, QuestionObserver, State);
            var handlers = new Dictionary<ApplicationCommandKind, Func<ApplicationCommand, CancellationToken, Task>>
            {
                [ApplicationCommandKind.AnkiProbe] = async (_, token) => { await ModuleActionUseCases.ExecuteAsync(ModuleActionKind.AnkiProbe, null, token).ConfigureAwait(false); },
                [ApplicationCommandKind.RemNoteDiscoverTarget] = async (_, token) => { await ModuleActionUseCases.ExecuteAsync(ModuleActionKind.RemNoteDiscoverTarget, null, token).ConfigureAwait(false); },
                [ApplicationCommandKind.QuestionInitial] = async (command, token) => { await QuestionUseCases.SubmitInitialAsync(command.Text, token).ConfigureAwait(false); },
                [ApplicationCommandKind.QuestionFollowUp] = async (command, token) => { await QuestionUseCases.SubmitFollowUpAsync(command.Text, token).ConfigureAwait(false); },
                [ApplicationCommandKind.QuestionMakeCards] = async (command, token) => { await QuestionUseCases.MakeCardsAsync(command.CardStructureId, token).ConfigureAwait(false); }
            };
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
        internal UnifiedApplicationCommandBinding Commands { get; }
    }
}
