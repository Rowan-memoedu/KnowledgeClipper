using System.Diagnostics;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace QualityV3.CorePhase2Architecture.Tests;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string snapshotRoot = null!;
    private static string hostDll = null!;
    private static string workRoot = null!;
    private static string receiptPath = null!;

    private static async Task<int> Main(string[] args)
    {
        var values = ParseArguments(args);
        snapshotRoot = Path.GetFullPath(GetRequired(values, "snapshot-root"));
        hostDll = Path.GetFullPath(GetRequired(values, "host-dll"));
        workRoot = Path.GetFullPath(GetRequired(values, "work-root"));
        receiptPath = Path.GetFullPath(GetRequired(values, "receipt"));
        Directory.CreateDirectory(workRoot);

        var startedAt = DateTimeOffset.UtcNow;
        var tests = new List<TestResult>();
        await RunAsync(tests, "production-host-excludes-authority-bound-fake", VerifyProductionHostBoundaryAsync);
        await RunAsync(tests, "acceptance-is-decoupled-from-slow-worker-and-reconnect-observes", VerifySlowWorkerAcceptanceAsync);
        await RunAsync(tests, "artifact-identity-and-replay-side-effects-are-stable", VerifyArtifactIdentityAndReplayAsync);
        await RunAsync(tests, "crash-after-session-claim-recovers-terminal-and-session", VerifySessionClaimCrashRecoveryAsync);
        await RunAsync(tests, "public-output-roles-and-exactly-one-kind-cardinality", VerifyTypedOutputRolesAsync);

        var passed = tests.Count == 5 && tests.All(item => item.Status == "PASS");
        var receipt = new
        {
            schemaVersion = "quality-v3-core-phase2-architecture-tests.v1",
            startedAtUtc = startedAt,
            completedAtUtc = DateTimeOffset.UtcNow,
            status = passed ? "PASS" : "FAIL",
            immutableSnapshotRoot = snapshotRoot,
            hostAssembly = hostDll,
            tests,
            boundaries = new
            {
                ownerReceiptTrusted = false,
                productionWorkerInvoked = false,
                fakeRuntimeRequiresExplicitTestMode = true,
                realAnkiCalled = false,
                realContentWorkerCalled = false,
                remNoteCalled = false,
                minerUCalled = false,
                speakerCalled = false,
                candidatePromotion = "NO-GO"
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions) + "\n");
        Console.WriteLine($"QUALITY_PHASE2_ARCHITECTURE_RESULT {(passed ? "PASS" : "FAIL")} {tests.Count(item => item.Status == "PASS")}/5");
        Console.WriteLine($"QUALITY_PHASE2_ARCHITECTURE_RECEIPT {receiptPath}");
        return passed ? 0 : 1;
    }

    private static async Task RunAsync(List<TestResult> tests, string name, Func<Task<object>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var evidence = await action();
            tests.Add(new TestResult(name, "PASS", stopwatch.ElapsedMilliseconds, evidence, null));
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            tests.Add(new TestResult(name, "FAIL", stopwatch.ElapsedMilliseconds, null, exception.ToString()));
            Console.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private static async Task<object> VerifyProductionHostBoundaryAsync()
    {
        var hostSourcePath = Path.Combine(snapshotRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Host", "Program.cs");
        var compositionPath = Path.Combine(snapshotRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Kernel", "KernelComposition.cs");
        var hostSource = await File.ReadAllTextAsync(hostSourcePath);
        var composition = await File.ReadAllTextAsync(compositionPath);
        Require(hostSource.Contains("WorkerRuntimeProfile.Production", StringComparison.Ordinal), "Host does not default explicitly to the production runtime profile.");
        Require(hostSource.Contains("authority-bound-fake-test-only", StringComparison.Ordinal), "Host has no explicit test-only fake-runtime switch.");
        Require(!hostSource.Contains("AuthorityBoundFakeWorkerRuntime", StringComparison.Ordinal), "Host entrypoint directly instantiates the fake runtime.");
        var cursor = 0;
        var fakeCreationCount = 0;
        const string fakeFactory = "AuthorityBoundFakeWorkerRuntime.CreateAsync";
        while ((cursor = composition.IndexOf(fakeFactory, cursor, StringComparison.Ordinal)) >= 0)
        {
            fakeCreationCount++;
            var windowStart = Math.Max(0, cursor - 320);
            var windowLength = Math.Min(composition.Length - windowStart, 520);
            var context = composition.Substring(windowStart, windowLength);
            Require(context.Contains("WorkerRuntimeProfile.AuthorityBoundFakeTestOnly", StringComparison.Ordinal), "Fake runtime construction is not guarded by the explicit test-only profile.");
            cursor += fakeFactory.Length;
        }
        Require(fakeCreationCount > 0, "Explicit fake test injection path is missing.");

        var productionData = NewGateDirectory("gate1-production");
        string productionMode;
        try
        {
            await using var production = await StartHostAsync(productionData, fakeTestMode: false, fakeDelayMilliseconds: null);
            var productionClient = NewClient(production.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 10);
            var productionMetrics = await ReadMetricsAsync(productionClient);
            Require(!productionMetrics.Mode.Contains("fake", StringComparison.OrdinalIgnoreCase), "Production Host reported a fake worker runtime mode.");
            productionMode = productionMetrics.Mode;
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("Production Core cannot start", StringComparison.Ordinal) ||
            exception.Message.Contains("real WorkerProcessRuntime", StringComparison.Ordinal))
        {
            productionMode = "FAIL-CLOSED-NO-REAL-RUNTIME";
        }

        var fakeData = NewGateDirectory("gate1-explicit-fake");
        await using var fake = await StartHostAsync(fakeData, fakeTestMode: true, fakeDelayMilliseconds: 0);
        var fakeClient = NewClient(fake.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 10);
        var fakeMetrics = await ReadMetricsAsync(fakeClient);
        Require(fakeMetrics.Mode.Contains("fake", StringComparison.OrdinalIgnoreCase), "Explicit test mode did not inject the fake runtime.");
        return new { fakeCreationCount, productionMode, explicitTestMode = fakeMetrics.Mode };
    }

    private static async Task<object> VerifySlowWorkerAcceptanceAsync()
    {
        const int fakeDelayMilliseconds = 1800;
        const int sdkRequestTimeoutMilliseconds = 450;
        var dataRoot = NewGateDirectory("gate2-slow-worker");
        await using var host = await StartHostAsync(dataRoot, fakeTestMode: true, fakeDelayMilliseconds);
        var setupClient = NewClient(host.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 10);
        var question = await setupClient.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-slow-question-artifact", ArtifactPurpose.Question, "Why is a derivative local?"));

        var shortClient = NewClient(host.PipeName, sdkRequestTimeoutMilliseconds, observationTimeoutSeconds: 10);
        var stopwatch = Stopwatch.StartNew();
        var accepted = await shortClient.QuestionInitialAsync(new QuestionInitialRequest("quality-slow-session", question, Array.Empty<TypedArtifactHandle>()));
        var acceptanceMilliseconds = stopwatch.ElapsedMilliseconds;
        Require(!string.IsNullOrWhiteSpace(accepted.OperationId), "Slow command acceptance did not return an operationId.");
        Require(acceptanceMilliseconds < fakeDelayMilliseconds, "Command response waited for the slow Worker execution.");

        var reconnectClient = NewClient(host.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
        var terminal = await WaitForTerminalProjectionAsync(reconnectClient, accepted.OperationId, TimeSpan.FromSeconds(15));
        var terminalMilliseconds = stopwatch.ElapsedMilliseconds;
        Require(terminal.State == QuestionProjectionState.WaitingInput, "Reconnected typed projection did not reach WaitingInput.");
        Require(terminalMilliseconds >= sdkRequestTimeoutMilliseconds, "Worker did not remain active beyond the SDK request timeout.");
        return new
        {
            accepted.OperationId,
            acceptanceMilliseconds,
            sdkRequestTimeoutMilliseconds,
            workerDelayMilliseconds = fakeDelayMilliseconds,
            terminalMilliseconds,
            terminalState = terminal.State.ToString(),
            reconnectedClientObservedCompletion = true
        };
    }

    private static async Task<object> VerifyArtifactIdentityAndReplayAsync()
    {
        var dataRoot = NewGateDirectory("gate3-artifact-replay");
        await using var host = await StartHostAsync(dataRoot, fakeTestMode: true, fakeDelayMilliseconds: 0);
        var client = NewClient(host.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
        const string identicalText = "What is the chain rule?";
        var inputA = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-artifact-command-a", ArtifactPurpose.Question, identicalText));
        var inputB = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-artifact-command-b", ArtifactPurpose.Question, identicalText));
        Require(inputA.Token != inputB.Token, "Different commands with identical content collided at artifact publication.");

        var initialA = await client.QuestionInitialAsync(new QuestionInitialRequest("quality-session-a", inputA, Array.Empty<TypedArtifactHandle>()));
        var initialB = await client.QuestionInitialAsync(new QuestionInitialRequest("quality-session-b", inputB, Array.Empty<TypedArtifactHandle>()));
        Require((await WaitForTerminalProjectionAsync(client, initialA.OperationId, TimeSpan.FromSeconds(15))).State == QuestionProjectionState.WaitingInput, "Session A did not complete its initial answer.");
        Require((await WaitForTerminalProjectionAsync(client, initialB.OperationId, TimeSpan.FromSeconds(15))).State == QuestionProjectionState.WaitingInput, "Session B did not complete its initial answer.");
        var answerA = ReadCompletedArtifacts(dataRoot, initialA.OperationId).Single();
        var answerB = ReadCompletedArtifacts(dataRoot, initialB.OperationId).Single();
        Require(answerA.ArtifactId != answerB.ArtifactId, "Different sessions produced the same answer artifact identity.");

        var answerHandleA = new TypedArtifactHandle(answerA);
        var makeRequest = new QuestionMakeCardsRequest("quality-replay-make-cards", initialA.SessionId, answerHandleA);
        var firstMake = await client.QuestionMakeCardsAsync(makeRequest);
        Require((await WaitForTerminalProjectionAsync(client, firstMake.OperationId, TimeSpan.FromSeconds(15))).State == QuestionProjectionState.Succeeded, "First make-cards command did not succeed.");
        var firstOutputIds = ReadCompletedArtifacts(dataRoot, firstMake.OperationId).Select(item => item.ArtifactId).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var artifactCountBeforeReplay = CountCommittedArtifacts(dataRoot);
        var metricsBeforeReplay = await ReadMetricsAsync(client);

        var replay = await client.QuestionMakeCardsAsync(makeRequest);
        Require(replay.OperationId == firstMake.OperationId, "Exact replay returned a different operationId.");
        Require((await WaitForTerminalProjectionAsync(client, replay.OperationId, TimeSpan.FromSeconds(15))).State == QuestionProjectionState.Succeeded, "Replay did not resolve to the completed operation.");
        var replayOutputIds = ReadCompletedArtifacts(dataRoot, replay.OperationId).Select(item => item.ArtifactId).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var artifactCountAfterReplay = CountCommittedArtifacts(dataRoot);
        var metricsAfterReplay = await ReadMetricsAsync(client);
        Require(firstOutputIds.SequenceEqual(replayOutputIds, StringComparer.Ordinal), "Exact replay returned different artifact identities.");
        Require(artifactCountAfterReplay == artifactCountBeforeReplay, "Exact replay created new artifacts.");
        Require(metricsAfterReplay.AnkiExportAttempts == metricsBeforeReplay.AnkiExportAttempts, "Exact replay attempted another Anki export.");
        Require(metricsAfterReplay.AnkiExternalWriteCount == metricsBeforeReplay.AnkiExternalWriteCount, "Exact replay performed another Anki write.");
        return new
        {
            inputTokensDistinct = true,
            answerArtifactA = answerA.ArtifactId,
            answerArtifactB = answerB.ArtifactId,
            replayOperationId = replay.OperationId,
            replayArtifactIds = replayOutputIds,
            artifactCountBeforeReplay,
            artifactCountAfterReplay,
            replayWorkerAttemptDelta = metricsAfterReplay.AnkiExportAttempts - metricsBeforeReplay.AnkiExportAttempts,
            replayAnkiWriteDelta = metricsAfterReplay.AnkiExternalWriteCount - metricsBeforeReplay.AnkiExternalWriteCount
        };
    }

    private static async Task<object> VerifySessionClaimCrashRecoveryAsync()
    {
        var dataRoot = NewGateDirectory("gate4-session-claim-crash");
        string sessionId;
        TypedArtifactHandle baselineAnswer;
        await using (var initialHost = await StartHostAsync(dataRoot, fakeTestMode: true, fakeDelayMilliseconds: 0))
        {
            var client = NewClient(initialHost.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
            var question = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-crash-baseline-question", ArtifactPurpose.Question, "What is continuity?"));
            var initial = await client.QuestionInitialAsync(new QuestionInitialRequest("quality-crash-session", question, Array.Empty<TypedArtifactHandle>()));
            var baselineProjection = await WaitForTerminalProjectionAsync(client, initial.OperationId, TimeSpan.FromSeconds(15));
            Require(baselineProjection.State == QuestionProjectionState.WaitingInput, "Baseline session did not reach WaitingInput.");
            sessionId = initial.SessionId;
            baselineAnswer = GetRequiredRole(baselineProjection, "AnswerArtifact");
        }

        string interruptedOperationId;
        var claimsBeforeCrash = CountSessionClaims(dataRoot, sessionId);
        await using (var slowHost = await StartHostAsync(dataRoot, fakeTestMode: true, fakeDelayMilliseconds: 8000))
        {
            var setupClient = NewClient(slowHost.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
            var followUpArtifact = await setupClient.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-crash-follow-up-artifact", ArtifactPurpose.QuestionFollowUp, "Give a topological intuition."));
            var shortClient = NewClient(slowHost.PipeName, requestTimeoutMilliseconds: 450, observationTimeoutSeconds: 15);
            var accepted = await shortClient.QuestionFollowUpAsync(new QuestionFollowUpRequest("quality-crash-follow-up", sessionId, followUpArtifact));
            interruptedOperationId = accepted.OperationId;
            await WaitForSessionClaimCountAsync(dataRoot, sessionId, claimsBeforeCrash + 1, TimeSpan.FromSeconds(5));
            await slowHost.StopAsync();
        }

        await using var recoveredHost = await StartHostAsync(dataRoot, fakeTestMode: true, fakeDelayMilliseconds: 0);
        var recoveredClient = NewClient(recoveredHost.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
        var interruptedProjection = await WaitForTerminalProjectionAsync(recoveredClient, interruptedOperationId, TimeSpan.FromSeconds(10));
        Require(interruptedProjection.State == QuestionProjectionState.Failed, "Recovered read-only follow-up did not reach an explicit Failed terminal state.");
        Require(LastSessionEventType(dataRoot, sessionId) == "question.session-command-aborted", "Recovered read-only session was not explicitly reopened.");

        var legalFollowUpArtifact = await recoveredClient.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-recovery-follow-up-artifact", ArtifactPurpose.QuestionFollowUp, "Now explain sequentially."));
        var legalFollowUp = await recoveredClient.QuestionFollowUpAsync(new QuestionFollowUpRequest("quality-recovery-follow-up", sessionId, legalFollowUpArtifact));
        var legalProjection = await WaitForTerminalProjectionAsync(recoveredClient, legalFollowUp.OperationId, TimeSpan.FromSeconds(15));
        Require(legalProjection.State == QuestionProjectionState.WaitingInput, "Recovered session rejected a legal subsequent follow-up.");
        Require(GetRequiredRole(legalProjection, "AnswerArtifact").Token != baselineAnswer.Token, "Legal post-recovery follow-up reused the baseline answer identity.");

        var writeDataRoot = NewGateDirectory("gate4-external-write-crash");
        string writeSessionId;
        TypedArtifactHandle writeAnswer;
        await using (var initialWriteHost = await StartHostAsync(writeDataRoot, fakeTestMode: true, fakeDelayMilliseconds: 0))
        {
            var client = NewClient(initialWriteHost.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
            var question = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-write-crash-question", ArtifactPurpose.Question, "What is compactness?"));
            var initial = await client.QuestionInitialAsync(new QuestionInitialRequest("quality-write-crash-session", question, Array.Empty<TypedArtifactHandle>()));
            var projection = await WaitForTerminalProjectionAsync(client, initial.OperationId, TimeSpan.FromSeconds(15));
            Require(projection.State == QuestionProjectionState.WaitingInput, "External-write crash fixture did not reach WaitingInput.");
            writeSessionId = initial.SessionId;
            writeAnswer = GetRequiredRole(projection, "AnswerArtifact");
        }

        const string crashedWriteRequestId = "quality-write-crash-make-cards";
        string interruptedWriteOperationId;
        var dispatchesBeforeCrash = CountSessionEvents(writeDataRoot, writeSessionId, "question.worker-dispatch-started");
        await using (var slowWriteHost = await StartHostAsync(writeDataRoot, fakeTestMode: true, fakeDelayMilliseconds: 8000))
        {
            var shortClient = NewClient(slowWriteHost.PipeName, requestTimeoutMilliseconds: 450, observationTimeoutSeconds: 15);
            var accepted = await shortClient.QuestionMakeCardsAsync(new QuestionMakeCardsRequest(crashedWriteRequestId, writeSessionId, writeAnswer));
            interruptedWriteOperationId = accepted.OperationId;
            await WaitForSessionEventCountAsync(writeDataRoot, writeSessionId, "question.worker-dispatch-started", dispatchesBeforeCrash + 1, TimeSpan.FromSeconds(5));
            await slowWriteHost.StopAsync();
        }

        await using var recoveredWriteHost = await StartHostAsync(writeDataRoot, fakeTestMode: true, fakeDelayMilliseconds: 0);
        var recoveredWriteClient = NewClient(recoveredWriteHost.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
        var unknownProjection = await WaitForTerminalProjectionAsync(recoveredWriteClient, interruptedWriteOperationId, TimeSpan.FromSeconds(10));
        Require(unknownProjection.State == QuestionProjectionState.Degraded, "Dispatched external-write crash was not preserved as Degraded/unknown.");
        Require(LastSessionEventType(writeDataRoot, writeSessionId) == "question.session-unknown-external-state", "External-write session was incorrectly reopened after dispatch.");

        var artifactsBeforeBlockedRetries = CountCommittedArtifacts(writeDataRoot);
        var metricsBeforeBlockedRetries = await ReadMetricsAsync(recoveredWriteClient);
        var replay = await recoveredWriteClient.QuestionMakeCardsAsync(new QuestionMakeCardsRequest(crashedWriteRequestId, writeSessionId, writeAnswer));
        Require(replay.OperationId == interruptedWriteOperationId, "Exact replay of the unknown external write changed operation identity.");
        Require((await WaitForTerminalProjectionAsync(recoveredWriteClient, replay.OperationId, TimeSpan.FromSeconds(10))).State == QuestionProjectionState.Degraded, "Exact replay lost the unknown external state.");
        var blockedRetry = await recoveredWriteClient.QuestionMakeCardsAsync(new QuestionMakeCardsRequest("quality-write-crash-new-command", writeSessionId, writeAnswer));
        Require((await WaitForTerminalProjectionAsync(recoveredWriteClient, blockedRetry.OperationId, TimeSpan.FromSeconds(10))).State == QuestionProjectionState.Failed, "Unknown external-write session accepted a new automatic write attempt.");
        var metricsAfterBlockedRetries = await ReadMetricsAsync(recoveredWriteClient);
        Require(CountCommittedArtifacts(writeDataRoot) == artifactsBeforeBlockedRetries, "Unknown external-write recovery created new artifacts.");
        Require(metricsAfterBlockedRetries.AnkiExportAttempts == metricsBeforeBlockedRetries.AnkiExportAttempts, "Unknown external-write recovery retried the worker.");
        Require(metricsAfterBlockedRetries.AnkiExternalWriteCount == metricsBeforeBlockedRetries.AnkiExternalWriteCount, "Unknown external-write recovery performed another Anki write.");
        return new
        {
            sessionId,
            interruptedOperationId,
            interruptedTerminalState = interruptedProjection.State.ToString(),
            legalSubsequentOperationId = legalFollowUp.OperationId,
            legalSubsequentState = legalProjection.State.ToString(),
            sessionClaimCountAfterRecovery = CountSessionClaims(dataRoot, sessionId),
            externalWriteSessionId = writeSessionId,
            externalWriteOperationId = interruptedWriteOperationId,
            externalWriteTerminalState = unknownProjection.State.ToString(),
            externalWriteSessionTerminalEvent = LastSessionEventType(writeDataRoot, writeSessionId),
            blockedRetryWorkerAttemptDelta = metricsAfterBlockedRetries.AnkiExportAttempts - metricsBeforeBlockedRetries.AnkiExportAttempts,
            blockedRetryAnkiWriteDelta = metricsAfterBlockedRetries.AnkiExternalWriteCount - metricsBeforeBlockedRetries.AnkiExternalWriteCount,
            blockedRetryArtifactDelta = CountCommittedArtifacts(writeDataRoot) - artifactsBeforeBlockedRetries
        };
    }

    private static async Task<object> VerifyTypedOutputRolesAsync()
    {
        var projectionType = typeof(QuestionProjectionSnapshot);
        Require(projectionType.GetProperty("Artifacts") is null, "Question projection still exposes a generic artifact list.");
        foreach (var role in new[] { "AnswerArtifact", "CardBundleArtifact", "DestinationEvidenceArtifact" })
        {
            var property = projectionType.GetProperty(role)
                ?? throw new InvalidOperationException("Question projection lacks public typed role " + role + ".");
            Require(property.PropertyType == typeof(TypedArtifactHandle), $"Role '{role}' is not a TypedArtifactHandle.");
        }

        var kernelSource = await File.ReadAllTextAsync(Path.Combine(snapshotRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Kernel", "Application", "KernelApplication.cs"));
        foreach (var kind in new[] { "question-answer", "question-card-bundle", "anki-export-evidence" })
        {
            Require(kernelSource.Contains(kind, StringComparison.Ordinal), "Kernel projection does not map artifact kind " + kind + ".");
        }
        Require(kernelSource.Contains("GetExactlyOneArtifact", StringComparison.Ordinal) &&
                kernelSource.Contains("projection_artifact_cardinality_invalid", StringComparison.Ordinal),
            "Kernel has no explicit exactly-one artifact-kind validator.");

        var invalidDataRoot = NewGateDirectory("gate5-invalid-cardinality");
        SeedInvalidProjection(invalidDataRoot, "quality-role-missing", Array.Empty<ArtifactReference>());
        var duplicateAnswer = PlaceholderArtifact("duplicate-answer", "question-answer", "1.0.0");
        SeedInvalidProjection(invalidDataRoot, "quality-role-duplicate", new[] { duplicateAnswer, duplicateAnswer });
        await using (var invalidHost = await StartHostAsync(invalidDataRoot, fakeTestMode: true, fakeDelayMilliseconds: 0))
        {
            var invalidClient = NewClient(invalidHost.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 10);
            await RequireProjectionCardinalityFailureAsync(invalidClient, "quality-role-missing");
            await RequireProjectionCardinalityFailureAsync(invalidClient, "quality-role-duplicate");
        }

        var dataRoot = NewGateDirectory("gate5-role-flow");
        await using var host = await StartHostAsync(dataRoot, fakeTestMode: true, fakeDelayMilliseconds: 0);
        var client = NewClient(host.PipeName, requestTimeoutMilliseconds: 3000, observationTimeoutSeconds: 15);
        var question = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("quality-role-question", ArtifactPurpose.Question, "What is a tangent?"));
        var initialSubmission = await client.QuestionInitialAsync(new QuestionInitialRequest("quality-role-session", question, Array.Empty<TypedArtifactHandle>()));
        var initial = await WaitForTerminalProjectionAsync(client, initialSubmission.OperationId, TimeSpan.FromSeconds(15));
        var answer = GetRequiredRole(initial, "AnswerArtifact");
        Require(answer.Reference.Kind == "question-answer", "AnswerArtifact is not bound by question-answer kind.");

        var makeSubmission = await client.QuestionMakeCardsAsync(new QuestionMakeCardsRequest("quality-role-make-cards", initialSubmission.SessionId, answer));
        var make = await WaitForTerminalProjectionAsync(client, makeSubmission.OperationId, TimeSpan.FromSeconds(15));
        var cardBundle = GetRequiredRole(make, "CardBundleArtifact");
        var destinationEvidence = GetRequiredRole(make, "DestinationEvidenceArtifact");
        Require(cardBundle.Reference.Kind == "question-card-bundle", "CardBundle is not bound by question-card-bundle kind.");
        Require(destinationEvidence.Reference.Kind == "anki-export-evidence", "DestinationEvidence is not bound by anki-export-evidence kind.");
        Require(typeof(OperationSubmission).GetProperty("Artifacts") is null, "Accepted operation submission still exposes a generic artifact list.");
        Require(typeof(QuestionOperationSubmission).GetProperty("Artifacts") is null, "Accepted question submission still exposes a generic artifact list.");
        return new
        {
            publicRoles = new[] { "AnswerArtifact", "CardBundleArtifact", "DestinationEvidenceArtifact" },
            genericArtifactListAbsent = true,
            missingKindRejected = true,
            duplicateKindRejected = true,
            answerKind = answer.Reference.Kind,
            cardBundleKind = cardBundle.Reference.Kind,
            destinationEvidenceKind = destinationEvidence.Reference.Kind
        };
    }

    private static TypedArtifactHandle GetRequiredRole(QuestionProjectionSnapshot projection, string role)
    {
        var value = typeof(QuestionProjectionSnapshot).GetProperty(role)?.GetValue(projection) as TypedArtifactHandle;
        return value ?? throw new InvalidOperationException("Projection role is absent: " + role + ".");
    }

    private static async Task RequireProjectionCardinalityFailureAsync(CoreV3Client client, string operationId)
    {
        try
        {
            _ = await client.GetQuestionProjectionAsync(operationId);
        }
        catch (CoreV3ClientException exception) when (exception.Code == "projection_artifact_cardinality_invalid")
        {
            return;
        }
        throw new InvalidOperationException("Projection did not reject invalid artifact cardinality for " + operationId + ".");
    }

    private static void SeedInvalidProjection(string dataRoot, string operationId, IReadOnlyList<ArtifactReference> artifacts)
    {
        var store = new FileEventStore(Path.Combine(dataRoot, "events-v3"));
        store.Append(operationId, 0, new[]
        {
            new KernelEvent(
                KernelSchemas.Event,
                "evt-" + Guid.NewGuid().ToString("N"),
                operationId,
                Sequence: 0,
                "operation.completed",
                EventVersion: 1,
                DateTimeOffset.UtcNow,
                "quality-role-cardinality",
                "quality-role-cardinality",
                "question.initial",
                "read-only-external",
                JsonSerializer.SerializeToElement(new { result = new { status = "waiting_input" } }, JsonOptions),
                artifacts)
        });
    }

    private static ArtifactReference PlaceholderArtifact(string id, string kind, string contract)
        => new(KernelSchemas.ArtifactReference, "artifact-" + id, kind, contract, new ArtifactDigest("sha256", new string('A', 64)), "kernel-v3");

    private static CoreV3Client NewClient(string pipeName, int requestTimeoutMilliseconds, int observationTimeoutSeconds)
        => new(pipeName, new CoreV3ClientOptions(
            TimeSpan.FromMilliseconds(requestTimeoutMilliseconds),
            TimeSpan.FromSeconds(observationTimeoutSeconds),
            TimeSpan.FromMilliseconds(75)));

    private static async Task<QuestionProjectionSnapshot> WaitForTerminalProjectionAsync(CoreV3Client client, string operationId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        QuestionProjectionSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await client.GetQuestionProjectionAsync(operationId);
            if (last.State != QuestionProjectionState.Running) return last;
            await Task.Delay(75);
        }
        throw new TimeoutException($"Operation '{operationId}' remained {last?.State.ToString() ?? "unobserved"}.");
    }

    private static IReadOnlyList<ArtifactReference> ReadCompletedArtifacts(string dataRoot, string operationId)
    {
        var store = new FileEventStore(Path.Combine(dataRoot, "events-v3"));
        var terminal = store.ReadStream(operationId).LastOrDefault(item => item.EventType == "operation.completed")
            ?? throw new InvalidDataException($"Operation '{operationId}' has no completed event.");
        return terminal.Artifacts;
    }

    private static int CountSessionClaims(string dataRoot, string sessionId)
    {
        var store = new FileEventStore(Path.Combine(dataRoot, "events-v3"));
        return store.ReadStream("question-session-" + sessionId).Count(item => item.EventType == "question.session-command-claimed");
    }

    private static int CountSessionEvents(string dataRoot, string sessionId, string eventType)
    {
        var store = new FileEventStore(Path.Combine(dataRoot, "events-v3"));
        return store.ReadStream("question-session-" + sessionId).Count(item => item.EventType == eventType);
    }

    private static string LastSessionEventType(string dataRoot, string sessionId)
    {
        var store = new FileEventStore(Path.Combine(dataRoot, "events-v3"));
        return store.ReadStream("question-session-" + sessionId).Last().EventType;
    }

    private static async Task WaitForSessionClaimCountAsync(string dataRoot, string sessionId, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (CountSessionClaims(dataRoot, sessionId) >= expected) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The durable session claim was not observed before the crash cut point.");
    }

    private static async Task WaitForSessionEventCountAsync(string dataRoot, string sessionId, string eventType, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (CountSessionEvents(dataRoot, sessionId, eventType) >= expected) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Session event '{eventType}' was not observed before the crash cut point.");
    }

    private static int CountCommittedArtifacts(string dataRoot)
    {
        var root = Path.Combine(dataRoot, "artifact-store-v3");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "artifact.json", SearchOption.AllDirectories).Count() : 0;
    }

    private static async Task<WorkerRuntimeMetrics> ReadMetricsAsync(CoreV3Client client)
    {
        var result = await client.QueryProtocolAsync(new QueryEnvelope(
            KernelSchemas.Query,
            "quality-metrics-" + Guid.NewGuid().ToString("N"),
            "vertical-slice.metrics",
            Subject: null,
            "read-your-writes",
            Cursor: null,
            JsonSerializer.SerializeToElement(new { }, JsonOptions)), CancellationToken.None);
        return result.Data.Deserialize<WorkerRuntimeMetrics>(JsonOptions)
            ?? throw new InvalidDataException("Worker runtime metrics were invalid.");
    }

    private static async Task<HostInstance> StartHostAsync(string dataRoot, bool fakeTestMode, int? fakeDelayMilliseconds)
    {
        var pipeName = "KnowledgeClipper.Core.V3.Quality." + Guid.NewGuid().ToString("N");
        var binding = Path.Combine(snapshotRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
        var start = new ProcessStartInfo
        {
            FileName = ResolveDotnetSdk(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { hostDll, "--repository-root", snapshotRoot, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName })
        {
            start.ArgumentList.Add(argument);
        }
        if (fakeTestMode)
        {
            start.ArgumentList.Add("--runtime-mode");
            start.ArgumentList.Add("authority-bound-fake-test-only");
        }
        if (fakeDelayMilliseconds.HasValue)
        {
            start.ArgumentList.Add("--fake-worker-delay-ms");
            start.ArgumentList.Add(fakeDelayMilliseconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var process = new Process { StartInfo = start };
        Require(process.Start(), "Core Host process did not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (ready != "CORE_V3_READY " + pipeName)
            {
                var error = process.HasExited ? await process.StandardError.ReadToEndAsync() : "Host did not emit its readiness line.";
                throw new InvalidOperationException($"Core Host readiness failed. actual='{ready}' error='{error}'");
            }
            return new HostInstance(process, pipeName);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private static string NewGateDirectory(string name)
    {
        var path = Path.Combine(workRoot, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Gate evidence directory already exists: " + path);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveDotnetSdk()
    {
        const string sdk = @"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe";
        return File.Exists(sdk) ? sdk : throw new FileNotFoundException("Pinned project SDK is missing.", sdk);
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Arguments must be --name value pairs.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Arguments must be --name value pairs.");
            values.Add(args[index][2..], args[index + 1]);
        }
        return values;
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Missing --" + name + ".");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record TestResult(string Name, string Status, long DurationMilliseconds, object? Evidence, string? Error);

    private sealed class HostInstance : IAsyncDisposable
    {
        internal HostInstance(Process process, string pipeName) { Process = process; PipeName = pipeName; }
        internal Process Process { get; }
        internal string PipeName { get; }

        internal async Task StopAsync()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Process.Dispose();
        }
    }
}
