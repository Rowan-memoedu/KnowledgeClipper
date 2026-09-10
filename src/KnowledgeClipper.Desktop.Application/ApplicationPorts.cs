using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal interface ITaskCommandPort
    {
        Task<TaskCommandResult> SubmitQuestionAsync(QuestionSubmission submission, CancellationToken cancellationToken);
        Task<TaskCommandResult> MakeCardsAsync(MakeCardsSubmission submission, CancellationToken cancellationToken);
    }

    internal interface ITaskQueryPort
    {
        Task<TaskProjection> GetRecentAsync(CancellationToken cancellationToken);
        Task<QuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken);
    }

    internal interface ICurrentTextCapturePort
    {
        Task<CapturedPlainText> CaptureOnceAsync(CancellationToken cancellationToken);
    }

    internal interface IReadToAnkiInputArtifactPort
    {
        Task<ArtifactHandle> PublishCaptureTextAsync(ImmutablePlainText value, CancellationToken cancellationToken);
    }

    // Core owns orchestration. Desktop sees one use case and typed projections only.
    internal interface IReadToAnkiPort
    {
        Task<ReadToAnkiSubmissionResult> SubmitAsync(ReadToAnkiSubmission submission, CancellationToken cancellationToken);
        Task<ReadToAnkiProjection> GetProjectionAsync(string operationId, CancellationToken cancellationToken);
        Task<ReadToAnkiSubmissionResult> ResumeLanguageChoiceAsync(ReadToAnkiChoiceSubmission submission, CancellationToken cancellationToken);
        Task<ReadToAnkiSubmissionResult> ResolveCardAsync(CardResolutionSubmission submission, CancellationToken cancellationToken);
        Task<ReadToAnkiPlaybackCommandResult> ReplayAsync(string operationId, CancellationToken cancellationToken);
    }

    internal interface IReadPlaybackControlPort
    {
        Task<PlaybackControlSnapshot> GetPlaybackControlAsync(string operationId, CancellationToken cancellationToken);
        Task<PlaybackStopAcceptance> StopPlaybackAsync(PlaybackTarget target, CancellationToken cancellationToken);
        Task<PlaybackControlSnapshot> WaitPlaybackStopAsync(PlaybackStopAcceptance accepted, CancellationToken cancellationToken);
    }

    internal interface IModuleActionPort
    {
        Task<ModuleActionResult> ExecuteAsync(ModuleActionRequest request, CancellationToken cancellationToken);
    }

    internal interface IStageControlPort
    {
        Task<StageControlResult> RequestPlaybackStopAsync(string taskId, CancellationToken cancellationToken);
    }

    internal interface IArtifactGateway
    {
        Task<T> ReadAsync<T>(ArtifactHandle artifact, CancellationToken cancellationToken);
        Task<ArtifactHandle> PublishAsync<T>(string kind, T value, CancellationToken cancellationToken);
    }

    internal interface ISettingsRepository
    {
        Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken);
        Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken cancellationToken);
    }

    internal interface ICredentialStore
    {
        Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken cancellationToken);
        Task StoreAsync(string credentialId, string secret, CancellationToken cancellationToken);
    }

    internal interface IWindowActivationPort
    {
        Task<WindowActivationSnapshot> CaptureAsync(CancellationToken cancellationToken);
        Task<bool> RestoreAsync(long nativeWindowId, CancellationToken cancellationToken);
    }
}
