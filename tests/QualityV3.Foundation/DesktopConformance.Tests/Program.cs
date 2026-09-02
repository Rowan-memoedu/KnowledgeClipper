using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Application.V3;

namespace QualityV3.DesktopConformance.Tests
{
    internal static class Program
    {
        private static int assertions;

        private static async Task<int> Main()
        {
            try
            {
                await ObserverExceptionTerminates();
                await ArtifactExceptionTerminates();
                await ObserverCancellationTerminates();
                await ObserverTimeoutTerminates();
                await InvocationSourcesUseOneBinding();
                await DuplicateQuestionIsNotConcurrent();
                Console.WriteLine("QUALITY_V3_DESKTOP_CONFORMANCE_TESTS PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("QUALITY_V3_DESKTOP_CONFORMANCE_TESTS FAIL " + exception);
                return 1;
            }
        }

        private static async Task ObserverExceptionTerminates()
        {
            var ports = new FakePorts { QueryException = new IOException("quality-query-failure") };
            ports.QuestionResults.Enqueue(new TaskCommandResult(true, "q-exception", "accepted"));
            var root = ports.Root(new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));
            await root.QuestionUseCases.SubmitInitialAsync("exception", CancellationToken.None);
            Assert(root.State.Snapshot().Question.Status == OperationStatus.Failed, "observer query exception reaches Failed");
            Assert(root.State.Snapshot().Question.NoticeCode == "question-observation-IOException", "observer query exception preserves typed failure notice");
        }

        private static async Task ArtifactExceptionTerminates()
        {
            var ports = new FakePorts { ArtifactException = new InvalidDataException("quality-artifact-failure") };
            ports.QuestionResults.Enqueue(new TaskCommandResult(true, "q-artifact", "accepted"));
            ports.QuestionProjections.Enqueue(new QuestionProjection("q-artifact", OperationStatus.Succeeded, 1, true, new ArtifactHandle("a-1", "answer"), "answer-ready"));
            var root = ports.Root(new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));
            await root.QuestionUseCases.SubmitInitialAsync("artifact", CancellationToken.None);
            Assert(root.State.Snapshot().Question.Status == OperationStatus.Failed, "answer artifact exception reaches Failed");
            Assert(root.State.Snapshot().Question.NoticeCode == "question-answer-InvalidDataException", "answer artifact exception preserves typed failure notice");
        }

        private static async Task ObserverCancellationTerminates()
        {
            var ports = new FakePorts { BlockQuery = true };
            ports.QuestionResults.Enqueue(new TaskCommandResult(true, "q-cancel", "accepted"));
            var root = ports.Root(new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));
            using var cancellation = new CancellationTokenSource();
            var pending = root.QuestionUseCases.SubmitInitialAsync("cancel", cancellation.Token);
            await ports.QueryEntered.Task;
            cancellation.Cancel();
            await pending;
            Assert(root.State.Snapshot().Question.Status == OperationStatus.Cancelled, "observer cancellation reaches Cancelled");
            Assert(root.State.Snapshot().Question.NoticeCode == "question-observation-cancelled", "observer cancellation preserves terminal notice");
        }

        private static async Task ObserverTimeoutTerminates()
        {
            var ports = new FakePorts();
            ports.QuestionResults.Enqueue(new TaskCommandResult(true, "q-timeout", "accepted"));
            ports.QuestionProjections.Enqueue(new QuestionProjection("q-timeout", OperationStatus.Running, 0, false, null, "running"));
            ports.QuestionProjections.Enqueue(new QuestionProjection("q-timeout", OperationStatus.Running, 0, false, null, "running"));
            var root = ports.Root(new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));
            await root.QuestionUseCases.SubmitInitialAsync("timeout", CancellationToken.None);
            Assert(root.State.Snapshot().Question.Status == OperationStatus.Failed, "observer exhaustion reaches Failed");
            Assert(root.State.Snapshot().Question.NoticeCode == "question-observation-timeout", "observer exhaustion preserves timeout notice");
        }

        private static async Task InvocationSourcesUseOneBinding()
        {
            var ports = new FakePorts();
            var root = ports.Root();
            var observed = new List<InvocationSource>();
            root.Commands.Invoked += (source, _) => observed.Add(source);
            foreach (var source in new[] { InvocationSource.Pointer, InvocationSource.Keyboard, InvocationSource.UiAutomation })
                await root.Commands.InvokeAsync(source, new ApplicationCommand(ApplicationCommandKind.AnkiProbe), CancellationToken.None);
            Assert(observed.SequenceEqual(new[] { InvocationSource.Pointer, InvocationSource.Keyboard, InvocationSource.UiAutomation }), "pointer keyboard and UIA traverse the same binding instance");
            Assert(ports.ModuleRequests.Count == 3 && ports.ModuleRequests.All(value => value.Kind == ModuleActionKind.AnkiProbe), "invocation source cannot alter the dispatched command");
        }

        private static async Task DuplicateQuestionIsNotConcurrent()
        {
            var ports = new FakePorts { BlockSubmit = true };
            ports.QuestionResults.Enqueue(new TaskCommandResult(true, "q-one", "accepted"));
            ports.QuestionProjections.Enqueue(new QuestionProjection("q-one", OperationStatus.WaitingInput, 0, false, null, "waiting"));
            var root = ports.Root(new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));
            var first = root.QuestionUseCases.SubmitInitialAsync("first", CancellationToken.None);
            await ports.SubmitEntered.Task;
            var duplicate = await root.QuestionUseCases.SubmitInitialAsync("duplicate-enter", CancellationToken.None);
            Assert(!duplicate.Accepted && duplicate.NoticeCode == "question-command-in-progress", "duplicate Enter is rejected while a command is in flight");
            Assert(ports.QuestionSubmissions.Count == 1 && ports.MaxConcurrentSubmits == 1, "duplicate Enter cannot create concurrent submissions");
            ports.SubmitRelease.TrySetResult(true);
            await first;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
            assertions++;
            Console.WriteLine("PASS " + message);
        }

        private sealed class FakePorts : ITaskCommandPort, ITaskQueryPort, IModuleActionPort, IStageControlPort, IArtifactGateway, ISettingsRepository, ICredentialStore, IWindowActivationPort
        {
            internal readonly Queue<TaskCommandResult> QuestionResults = new();
            internal readonly Queue<QuestionProjection> QuestionProjections = new();
            internal readonly List<QuestionSubmission> QuestionSubmissions = new();
            internal readonly List<ModuleActionRequest> ModuleRequests = new();
            internal readonly TaskCompletionSource<bool> QueryEntered = NewSignal();
            internal readonly TaskCompletionSource<bool> SubmitEntered = NewSignal();
            internal readonly TaskCompletionSource<bool> SubmitRelease = NewSignal();
            internal Exception QueryException;
            internal Exception ArtifactException;
            internal bool BlockQuery;
            internal bool BlockSubmit;
            internal int MaxConcurrentSubmits;
            private int concurrentSubmits;

            internal DesktopCompositionRoot Root(QuestionObservationPolicy policy = null) => new(this, this, this, this, this, this, this, this, policy);

            public async Task<TaskCommandResult> SubmitQuestionAsync(QuestionSubmission submission, CancellationToken cancellationToken)
            {
                var concurrent = Interlocked.Increment(ref concurrentSubmits);
                MaxConcurrentSubmits = Math.Max(MaxConcurrentSubmits, concurrent);
                QuestionSubmissions.Add(submission);
                SubmitEntered.TrySetResult(true);
                try
                {
                    if (BlockSubmit) await SubmitRelease.Task.WaitAsync(cancellationToken);
                    return QuestionResults.Dequeue();
                }
                finally { Interlocked.Decrement(ref concurrentSubmits); }
            }

            public Task<TaskCommandResult> MakeCardsAsync(MakeCardsSubmission submission, CancellationToken cancellationToken) => throw new NotSupportedException();

            public async Task<QuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken)
            {
                QueryEntered.TrySetResult(true);
                if (BlockQuery) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (QueryException != null) throw QueryException;
                return QuestionProjections.Dequeue();
            }

            public Task<TaskProjection> GetRecentAsync(CancellationToken cancellationToken) => Task.FromResult<TaskProjection>(null);

            public Task<ModuleActionResult> ExecuteAsync(ModuleActionRequest request, CancellationToken cancellationToken)
            {
                ModuleRequests.Add(request);
                return Task.FromResult(new ModuleActionResult(true, OperationStatus.Succeeded, "ok", new Dictionary<string, object>(), null, false, false));
            }

            public Task<StageControlResult> RequestPlaybackStopAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(new StageControlResult(true, "stopped", "ok"));

            public Task<T> ReadAsync<T>(ArtifactHandle artifact, CancellationToken cancellationToken)
            {
                if (ArtifactException != null) throw ArtifactException;
                return Task.FromResult(default(T));
            }

            public Task<ArtifactHandle> PublishAsync<T>(string kind, T value, CancellationToken cancellationToken) => Task.FromResult(new ArtifactHandle("published", kind));
            public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new SettingsSnapshot("default", "r1", new Dictionary<string, object>()));
            public Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken cancellationToken) => Task.FromResult(settings);
            public Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken cancellationToken) => Task.FromResult(new CredentialMetadata(credentialId, false, false, "test"));
            public Task StoreAsync(string credentialId, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<WindowActivationSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new WindowActivationSnapshot(1, "runtime", "selection", 1));
            public Task<bool> RestoreAsync(long nativeWindowId, CancellationToken cancellationToken) => Task.FromResult(true);

            private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
