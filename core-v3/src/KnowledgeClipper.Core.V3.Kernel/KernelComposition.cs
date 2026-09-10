using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.CompatibilityGateway;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.Transport;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

namespace KnowledgeClipper.Core.V3.Kernel;

internal enum WorkerRuntimeProfile
{
    Production,
    AuthorityBoundFakeTestOnly
}

internal static class KernelComposition
{
    public static async Task<NamedPipeKernelServer> CreateNamedPipeServerAsync(
        string repositoryRoot,
        string capabilityBindingPath,
        string legacyDataRoot,
        string pipeName,
        CancellationToken cancellationToken,
        WorkerRuntimeProfile runtimeProfile = WorkerRuntimeProfile.Production,
        TimeSpan? fakeWorkerDelay = null)
    {
        var catalog = await AdmittedCapabilityCatalog.LoadAsync(
            repositoryRoot,
            capabilityBindingPath,
            cancellationToken).ConfigureAwait(false);
        var registry = new OperationRegistry(new IOperationHandler[]
        {
            new ContentSettingsDescribeOperation(),
            new ArtifactPublishOperation(),
            new AnkiProbeOperation(),
            new RemNoteSettingsOperation("remnote.discover-target"),
            new RemNoteSettingsOperation("remnote.probe"),
            new ReadAloudCardsOperation(),
            new QuestionWorkflowOperation("question.initial"),
            new QuestionWorkflowOperation("question.follow-up"),
            new QuestionWorkflowOperation("question.make-cards")
        });
        var hasAvailableWorkerCapability = catalog.Document.Operations.Any(item => item.Available && item.WorkerBinding.Mode == "worker");
        IWorkerRuntime workerRuntime = hasAvailableWorkerCapability
            ? runtimeProfile == WorkerRuntimeProfile.AuthorityBoundFakeTestOnly
                ? await AuthorityBoundFakeWorkerRuntime.CreateAsync(catalog, repositoryRoot, cancellationToken, fakeWorkerDelay).ConfigureAwait(false)
                : await WorkerProcessRuntime.CreateAsync(
                    catalog,
                    repositoryRoot,
                    Path.Combine(legacyDataRoot, "worker-process-v3"),
                    cancellationToken).ConfigureAwait(false)
            : new DisabledWorkerRuntime();
        var application = new KernelApplication(
            catalog,
            registry,
            workerRuntime,
            new FileEventStore(Path.Combine(legacyDataRoot, "events-v3")),
            new FileCommandIndex(Path.Combine(legacyDataRoot, "command-index-v3")),
            new FileArtifactStore(Path.Combine(legacyDataRoot, "artifact-store-v3"), "kernel-v3"));
        application.RecoverIncompleteCommands();
        var gateway = new KernelCompatibilityGateway(application, legacyDataRoot);
        return new NamedPipeKernelServer(pipeName, gateway);
    }
}
