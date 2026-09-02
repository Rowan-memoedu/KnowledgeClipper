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
