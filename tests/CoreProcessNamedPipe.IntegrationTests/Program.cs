using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

var projectRoot = Path.GetFullPath(args.Length >= 1 ? args[0] : throw new ArgumentException("Expected project root."));
var dotnet = Path.GetFullPath(args.Length >= 2 ? args[1] : throw new ArgumentException("Expected dotnet executable."));
var orchestratorRuntime = Path.GetFullPath(args.Length >= 3
    ? args[2]
    : Path.Combine(projectRoot, "orchestrator", "bin", "Release", "net8.0", "KnowledgeClipper.Orchestrator.dll"));
var fakeWorker = "tests/FakeWorker/bin/Release/net8.0/RemNoteQuickClipper.FakeWorker.exe";
Assert(File.Exists(orchestratorRuntime), "Release orchestrator runtime is missing.");
Assert(File.Exists(Path.Combine(projectRoot, fakeWorker.Replace('/', Path.DirectorySeparatorChar))), "Release fake worker is missing.");

var receiptPath = Environment.GetEnvironmentVariable("KC_CORE_PROCESS_RECEIPT");
var testRoot = string.IsNullOrWhiteSpace(receiptPath)
    ? Path.Combine(Path.GetTempPath(), "kc-core-process", Guid.NewGuid().ToString("N"))
    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!, "evidence");
if (Directory.Exists(testRoot)) throw new IOException("Append-only Core process evidence root already exists: " + testRoot);
var registryPath = Path.Combine(testRoot, "registry.json");
await AtomicFile.WriteJsonOnceAsync(registryPath, Registry(fakeWorker));

var ipc15Root = Path.Combine(testRoot, "ipc15");
var ipc15JobsRoot = Path.Combine(ipc15Root, "jobs");
Directory.CreateDirectory(ipc15JobsRoot);
var ipc15PipeName = "KnowledgeClipper.CoreProcess." + Guid.NewGuid().ToString("N");
var ipc15Path = Path.Combine(ipc15Root, "ipc.json");
await AtomicFile.WriteJsonOnceAsync(ipc15Path, Ipc(ipc15PipeName, "1.5"));
var ipc15 = await RunSingleVersionProcessAsync(dotnet, orchestratorRuntime, ipc15JobsRoot, registryPath, projectRoot, ipc15Path, "1.5", () => RunAnkiProbeAsync(ipc15JobsRoot, ipc15PipeName));

var ipc16Root = Path.Combine(testRoot, "ipc16");
var ipc16JobsRoot = Path.Combine(ipc16Root, "jobs");
Directory.CreateDirectory(ipc16JobsRoot);
var ipc16PipeName = "KnowledgeClipper.CoreProcess." + Guid.NewGuid().ToString("N");
var ipc16Path = Path.Combine(ipc16Root, "ipc.json");
await AtomicFile.WriteJsonOnceAsync(ipc16Path, Ipc(ipc16PipeName, "1.6"));
var ipc16 = await RunSingleVersionProcessAsync(dotnet, orchestratorRuntime, ipc16JobsRoot, registryPath, projectRoot, ipc16Path, "1.6", () => RunContentSettingsAsync(ipc16JobsRoot, ipc16PipeName));

var jobsRoot = Path.Combine(testRoot, "jobs");
Directory.CreateDirectory(jobsRoot);
var pipeName = "KnowledgeClipper.CoreProcess." + Guid.NewGuid().ToString("N");
var ipcPath = Path.Combine(testRoot, "ipc.json");
await AtomicFile.WriteJsonOnceAsync(ipcPath, Ipc(pipeName, "1.7"));

using var process = StartCoreProcess(dotnet, orchestratorRuntime, jobsRoot, registryPath, projectRoot, ipcPath);
var stdoutTask = process.StandardOutput.ReadToEndAsync();
var stderrTask = process.StandardError.ReadToEndAsync();
try
{
    var probe = await RunAnkiProbeAsync(jobsRoot, pipeName);
    var contentSettings = await RunContentSettingsAsync(jobsRoot, pipeName);
    var typedPublicSettings = await RunTypedPublicSettingsAsync(jobsRoot, pipeName);
    var taskTruth = await RunTaskGetAsync(jobsRoot, pipeName);
    var report = new
    {
        schemaVersion = "knowledge-clipper-core-process-named-pipe-canary.v1",
        status = "passed",
        processBoundary = Path.GetExtension(orchestratorRuntime).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? "published KnowledgeClipper.Orchestrator.exe serve"
            : "KnowledgeClipper.Orchestrator.dll serve",
        orchestratorRuntime = new { path = orchestratorRuntime, sha256 = Sha(await File.ReadAllBytesAsync(orchestratorRuntime)) },
        transport = "windows-named-pipe",
        fakeWorkerRole = "worker-only-real-artifact-producer",
        displayProjectionReturnedByFake = false,
        finalProcessProtocolMatrix = new[] { ipc15, ipc16, new { protocolVersion = "1.7", startup = "passed", typedAction = "passed" } },
        probe,
        contentSettings,
        typedPublicSettings,
        taskTruth
    };
    if (!string.IsNullOrWhiteSpace(receiptPath)) await AtomicFile.WriteJsonOnceAsync(Path.GetFullPath(receiptPath), report);
    Console.WriteLine(JsonSerializer.Serialize(report, ContractJson.Options));
    return 0;
}
finally
{
    if (!process.HasExited) process.Kill(true);
    await process.WaitForExitAsync();
    _ = await stdoutTask;
    var stderr = await stderrTask;
    if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
    if (string.IsNullOrWhiteSpace(receiptPath) && Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
}

static async Task<object> RunTypedPublicSettingsAsync(string jobsRoot, string pipeName)
{
    var remnote = await RunTypedPublicActionAsync(jobsRoot, pipeName, "typed-remnote-probe-process-canary", "remnote.probe", new
    {
        schemaVersion = "remnote-public-probe-request.v2", probeId = "typed-remnote-probe-process-canary", authorizationScope = "isolated-canary", timeoutMs = 15000,
        operations = new[] { "status", "search", "list", "read" }, target = new { rootRemId = "fake-root", rootTitle = "Knowledge Clipper Canary" }
    }, 'a', false, "ready", "remnote-public-probe-result");

    var voices = await RunTypedPublicActionAsync(jobsRoot, pipeName, "typed-speech-voices-process-canary", "speech.voices", new
    {
        schemaVersion = "speech-settings-voice-discovery-input.v1", mode = "validate_only", requestedLanguages = new[] { "zh-CN" }, cacheMaxAgeSeconds = 3600,
        deployment = new { edgeTtsPath = (string?)null, ffplayPath = (string?)null },
        catalogSnapshot = new { provider = "edge-tts", catalogVersion = "fake", updatedAtUtc = "2026-09-01T00:00:00Z", voices = Array.Empty<object>() }
    }, 'b', false, "ready", "speech-voice-discovery-result");

    var rejectedActionId = "typed-speech-preview-rejected-process-canary";
    var rejectedRoot = Path.Combine(jobsRoot, "module-actions", rejectedActionId);
    Directory.CreateDirectory(rejectedRoot);
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(rejectedRoot, "input.json"), PreviewAction(rejectedActionId, "authorization-drift"));
    var rejectedRequest = new
    {
        protocolVersion = "1.7", correlationId = rejectedActionId, messageType = "module-action",
        payload = new { schemaVersion = "knowledge-clipper-module-action.v6", actionId = rejectedActionId, operation = "speech.preview", payloadRef = $"module-actions/{rejectedActionId}/input.json", idempotencyKey = new string('c', 64) }
    };
    using (var rejected = JsonDocument.Parse(await RoundTripAsync(pipeName, rejectedRequest)))
        Assert(!rejected.RootElement.GetProperty("ok").GetBoolean() && !File.Exists(Path.Combine(rejectedRoot, "receipt.json")), "Speech preview authorization drift did not fail closed before worker dispatch.");

    var previewActionId = "typed-speech-preview-process-canary";
    var preview = await RunTypedPublicActionAsync(jobsRoot, pipeName, previewActionId, "speech.preview", PreviewAction(previewActionId, previewActionId), 'd', true, "completed", "speech-speaker-preview-result");
    var previewResultPath = Path.Combine(jobsRoot, preview.Path.Replace('/', Path.DirectorySeparatorChar));
    using var previewResult = JsonDocument.Parse(await File.ReadAllBytesAsync(previewResultPath));
    Assert(previewResult.RootElement.GetProperty("voiceSnapshot").GetProperty("source").GetString() == "fake-offline", "Speech preview Canary was not the Fake/offline route.");
    return new
    {
        status = "passed", protocolVersion = "1.7",
        remnote = new { status = "ready", allowedOperations = new[] { "status", "search", "list", "read" }, externalWrites = 0, typedResultSha256 = remnote.Sha256 },
        speechVoices = new { status = "ready", settingsMutations = 0, physicalSpeakerPlayback = false, typedResultSha256 = voices.Sha256 },
        speechPreview = new { status = "completed", authorizationMode = "explicit-user-action", authorizationDriftRejected = true, fakeOffline = true, physicalSpeakerInvocationsDuringCanary = 0, automaticRetryAllowed = false, typedResultSha256 = preview.Sha256 }
    };
}

static object PreviewAction(string actionId, string authorizedActionId) => new
{
    schemaVersion = "speech-settings-speaker-preview-action.v2",
    workerInput = new { schemaVersion = "speech-settings-speaker-preview-input.v1", scope = "project", effectiveAt = "preview_only", action = "speaker.preview", explicitSideEffectAuthorization = true, previewLanguage = "zh-CN", adapterInput = new { } },
    authorization = new { mode = "explicit-user-action", operation = "speaker.preview", actionId = authorizedActionId, previewLanguage = "zh-CN", authorizedAt = "2026-09-01T00:00:00Z" },
    effectClass = "physical-audio-single-attempt", automaticRetryAllowed = false
};

static async Task<(string Path, string Sha256)> RunTypedPublicActionAsync(string jobsRoot, string pipeName, string actionId, string operation, object input, char idempotencyDigit, bool physicalAudio, string expectedStatus, string expectedKind)
{
    var actionRoot = Path.Combine(jobsRoot, "module-actions", actionId);
    Directory.CreateDirectory(actionRoot);
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(actionRoot, "input.json"), input);
    var request = new
    {
        protocolVersion = "1.7", correlationId = actionId, messageType = "module-action",
        payload = new { schemaVersion = "knowledge-clipper-module-action.v6", actionId, operation, payloadRef = $"module-actions/{actionId}/input.json", idempotencyKey = new string(idempotencyDigit, 64) }
    };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, request));
    Assert(response.RootElement.GetProperty("ok").GetBoolean(), $"{operation} typed public IPC response failed.");
    var receipt = response.RootElement.GetProperty("payload");
    Assert(receipt.GetProperty("schemaVersion").GetString() == "knowledge-clipper-module-action-receipt.v6" && receipt.GetProperty("accepted").GetBoolean(), $"{operation} typed receipt identity drifted.");
    Assert(receipt.GetProperty("status").GetString() == expectedStatus && receipt.GetProperty("executionStatus").GetString() == "completed", $"{operation} business/execution status drifted.");
    Assert(receipt.GetProperty("externalWriteCount").GetInt32() == 0 && receipt.GetProperty("physicalAudio").GetBoolean() == physicalAudio && !receipt.GetProperty("automaticRetryAllowed").GetBoolean(), $"{operation} effect boundary drifted.");
    var typed = receipt.GetProperty("outputRefs").EnumerateArray().Single();
    Assert(typed.GetProperty("referenceType").GetString() == "file" && typed.GetProperty("kind").GetString() == expectedKind && typed.GetProperty("rootRef").GetString() == "jobs-root", $"{operation} typed output identity drifted.");
    var path = typed.GetProperty("path").GetString()!;
    var sha = typed.GetProperty("expectedSha256").GetString()!;
    Assert(File.Exists(Path.Combine(jobsRoot, path.Replace('/', Path.DirectorySeparatorChar))) && Sha(await File.ReadAllBytesAsync(Path.Combine(jobsRoot, path.Replace('/', Path.DirectorySeparatorChar)))) == sha, $"{operation} typed output is not real and SHA-bound.");
    return (path, sha);
}

static async Task<object> RunContentSettingsAsync(string jobsRoot, string pipeName)
{
    var userSettingsParent = Path.Combine(Path.GetTempPath(), "kc-core-content-settings-canary");
    Directory.CreateDirectory(userSettingsParent);
    var targetRoot = Path.Combine(userSettingsParent, Guid.NewGuid().ToString("N"));
    var describe = await RunContentActionAsync(jobsRoot, pipeName, "content-settings-describe-process-canary", "content.settings.describe",
        new { schemaVersion = "content-settings-action.v1", action = "content.settings.describe", effectClass = "local-filesystem-read-only", externalMutation = false }, '5');
    Assert(describe.Kind == "content-settings-description", "Content settings describe did not return its typed result.");

    var rejectedTarget = Path.Combine(userSettingsParent, Guid.NewGuid().ToString("N"));
    var rejectedActionId = "content-settings-copy-rejected-process-canary";
    var rejectedActionRoot = Path.Combine(jobsRoot, "module-actions", rejectedActionId);
    Directory.CreateDirectory(rejectedActionRoot);
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(rejectedActionRoot, "input.json"), new
    {
        schemaVersion = "content-settings-action.v1", action = "content.settings.copy", targetRoot = rejectedTarget,
        effectClass = "local-filesystem-create-once", externalMutation = true,
        authorization = new { mode = "explicit-user-action", operation = "copy", targetRoot = rejectedTarget + "-drift", createOnly = true }
    });
    var rejectedRequest = new
    {
        protocolVersion = "1.6", correlationId = rejectedActionId, messageType = "module-action",
        payload = new { schemaVersion = "knowledge-clipper-module-action.v5", actionId = rejectedActionId, operation = "content.settings.copy", payloadRef = $"module-actions/{rejectedActionId}/input.json", idempotencyKey = new string('9', 64) }
    };
    using (var rejectedResponse = JsonDocument.Parse(await RoundTripAsync(pipeName, rejectedRequest)))
        Assert(!rejectedResponse.RootElement.GetProperty("ok").GetBoolean() && !Directory.Exists(rejectedTarget), "Content settings copy did not fail closed on target authorization drift.");

    var copyPayload = new
    {
        schemaVersion = "content-settings-action.v1", action = "content.settings.copy", targetRoot,
        effectClass = "local-filesystem-create-once", externalMutation = true,
        authorization = new { mode = "explicit-user-action", operation = "copy", targetRoot, createOnly = true }
    };
    var copy = await RunContentActionAsync(jobsRoot, pipeName, "content-settings-copy-process-canary", "content.settings.copy", copyPayload, '6', replay: true);
    Assert(copy.Kind == "content-settings-copy-result" && Directory.Exists(targetRoot), "Content settings copy did not create the exact authorized target.");
    Assert(File.Exists(Path.Combine(targetRoot, "content-settings.draft.json")), "Content settings copy omitted its draft metadata.");

    var validate = await RunContentActionAsync(jobsRoot, pipeName, "content-settings-validate-process-canary", "content.settings.validate",
        new { schemaVersion = "content-settings-action.v1", action = "content.settings.validate", root = targetRoot, effectClass = "local-filesystem-read-only", externalMutation = false }, '7');
    var diff = await RunContentActionAsync(jobsRoot, pipeName, "content-settings-diff-process-canary", "content.settings.diff",
        new { schemaVersion = "content-settings-action.v1", action = "content.settings.diff", root = targetRoot, effectClass = "local-filesystem-read-only", externalMutation = false }, '8');
    Assert(validate.Kind == "content-settings-validation" && diff.Kind == "content-settings-diff", "Content settings read-only results lost their typed identities.");
    return new
    {
        status = "passed", protocolVersion = "1.6", worker = "content.deepseek",
        operations = new[] { "content.settings.describe", "content.settings.copy", "content.settings.validate", "content.settings.diff" },
        copyPolicy = "explicit-user-action+exact-target+create-once+no-automatic-retry", authorizationDriftRejected = true, externalWrites = 0,
        describeResultSha256 = describe.Sha256, copyResultSha256 = copy.Sha256,
        validateResultSha256 = validate.Sha256, diffResultSha256 = diff.Sha256
    };
}

static async Task<(string Kind, string Sha256)> RunContentActionAsync(string jobsRoot, string pipeName, string actionId, string operation, object input, char idempotencyDigit, bool replay = false)
{
    var actionRoot = Path.Combine(jobsRoot, "module-actions", actionId);
    Directory.CreateDirectory(actionRoot);
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(actionRoot, "input.json"), input);
    var request = new
    {
        protocolVersion = "1.6", correlationId = actionId, messageType = "module-action",
        payload = new { schemaVersion = "knowledge-clipper-module-action.v5", actionId, operation, payloadRef = $"module-actions/{actionId}/input.json", idempotencyKey = new string(idempotencyDigit, 64) }
    };
    var responseText = await RoundTripAsync(pipeName, request);
    using var response = JsonDocument.Parse(responseText);
    Assert(response.RootElement.GetProperty("ok").GetBoolean(), $"{operation} IPC response failed.");
    var receipt = response.RootElement.GetProperty("payload");
    Assert(receipt.GetProperty("schemaVersion").GetString() == "knowledge-clipper-module-action-receipt.v5", $"{operation} receipt version drifted.");
    Assert(receipt.GetProperty("accepted").GetBoolean() && receipt.GetProperty("status").GetString() == "completed" && receipt.GetProperty("executionStatus").GetString() == "completed", $"{operation} did not complete.");
    Assert(receipt.GetProperty("externalWriteCount").GetInt32() == 0 && !receipt.GetProperty("automaticRetryAllowed").GetBoolean(), $"{operation} external-write/retry boundary drifted.");
    var typed = receipt.GetProperty("outputRefs").EnumerateArray().Single();
    Assert(typed.GetProperty("referenceType").GetString() == "file" && typed.GetProperty("rootRef").GetString() == "jobs-root", $"{operation} did not return a Core-owned file reference.");
    var resultPath = Path.Combine(jobsRoot, typed.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
    var sha = typed.GetProperty("expectedSha256").GetString()!;
    Assert(File.Exists(resultPath) && Sha(await File.ReadAllBytesAsync(resultPath)) == sha, $"{operation} result reference is not real and SHA-bound.");
    if (replay)
    {
        using var replayResponse = JsonDocument.Parse(await RoundTripAsync(pipeName, request));
        Assert(replayResponse.RootElement.GetProperty("payload").GetProperty("outputSha256").GetString() == sha, $"{operation} replay did not return the immutable prior receipt.");
    }
    return (typed.GetProperty("kind").GetString()!, sha);
}

static async Task<object> RunAnkiProbeAsync(string jobsRoot, string pipeName)
{
    const string actionId = "anki-probe-process-canary";
    const string taskId = "settings-anki-probe-process-canary";
    var actionRoot = Path.Combine(jobsRoot, "module-actions", actionId);
    var packageRoot = Path.Combine(actionRoot, "package");
    Directory.CreateDirectory(packageRoot);
    var config = await PublishArtifactAsync(packageRoot, "anki-config-process-canary", "anki-export-config", Encoding.UTF8.GetBytes("{}"));
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(packageRoot, "task.manifest.v1.json"), new
    {
        schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow,
        createdBy = new { application = "CoreProcessNamedPipe.IntegrationTests", version = "1.0.0" },
        intent = "clip.card", executionProfile = "cards-anki",
        inputRefs = new[] { new { artifactRef = config.ArtifactRef, kind = config.Kind, contractVersion = config.ContractVersion, expectedSha256 = config.ExpectedSha256 } },
        requestedOutputs = new[] { "anki-connection-status" }, initialPlanRef = "plans/anki-probe.v1.json", retentionClass = "evidence"
    });
    var packageRef = $"module-actions/{actionId}/package";
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(actionRoot, "input.json"), new
    {
        schemaVersion = "anki-readonly-probe-action.v1", taskId, packageRootRef = packageRef, configRef = config,
        effectClass = "provider-read-only", externalMutation = false
    });
    var request = new
    {
        protocolVersion = "1.5", correlationId = "anki-probe-process", messageType = "module-action",
        payload = new { schemaVersion = "knowledge-clipper-module-action.v4", actionId, operation = "anki.probe", payloadRef = $"module-actions/{actionId}/input.json", idempotencyKey = new string('4', 64) }
    };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, request));
    Assert(response.RootElement.GetProperty("ok").GetBoolean(), "Anki probe IPC response failed.");
    var receipt = response.RootElement.GetProperty("payload");
    Assert(receipt.GetProperty("schemaVersion").GetString() == "knowledge-clipper-module-action-receipt.v4", "Anki probe receipt version drifted.");
    Assert(receipt.GetProperty("executionStatus").GetString() == "completed" && receipt.GetProperty("status").GetString() == "ready", "Execution completion and business readiness were not kept distinct.");
    Assert(receipt.GetProperty("taskId").GetString() == taskId && receipt.GetProperty("runId").GetString() == $"run-{actionId}", "Anki probe task/run binding drifted.");
    Assert(receipt.GetProperty("externalWriteCount").GetInt32() == 0 && !receipt.GetProperty("physicalAudio").GetBoolean(), "Anki probe was not read-only.");
    var typedOutput = receipt.GetProperty("outputRefs").EnumerateArray().Single();
    Assert(typedOutput.GetProperty("referenceType").GetString() == "artifact" && typedOutput.GetProperty("kind").GetString() == "anki-connection-status", "Anki probe did not return a typed output reference.");
    var manifestPath = Path.Combine(packageRoot, typedOutput.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
    Assert(File.Exists(manifestPath) && Sha(await File.ReadAllBytesAsync(manifestPath)) == typedOutput.GetProperty("expectedSha256").GetString(), "Typed Anki output reference is not a real SHA-bound artifact.");
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
    Assert(manifest.RootElement.GetProperty("files").GetArrayLength() == 4, "Anki process canary did not exercise a four-file typed artifact.");
    var payloadRelative = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    using var status = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(packageRoot, payloadRelative.Replace('/', Path.DirectorySeparatorChar))));
    Assert(status.RootElement.GetProperty("status").GetString() == "ready" && !status.RootElement.GetProperty("externalMutation").GetBoolean(), "Typed Anki artifact did not preserve raw readiness facts.");
    var resultPath = Path.Combine(jobsRoot, receipt.GetProperty("outputRef").GetString()!.Replace('/', Path.DirectorySeparatorChar));
    Assert(File.Exists(resultPath) && Sha(await File.ReadAllBytesAsync(resultPath)) == receipt.GetProperty("outputSha256").GetString(), "Execution result ref is not real and SHA-bound.");
    return new
    {
        status = "passed", executionStatus = "completed", readiness = "ready", typedOutputKind = "anki-connection-status", taskId,
        runId = $"run-{actionId}", externalWrites = 0, artifactRootRef = packageRef, declaredFileCount = 4,
        outputManifestPath = typedOutput.GetProperty("path").GetString(), outputManifestSha256 = typedOutput.GetProperty("expectedSha256").GetString(),
        executionResultRef = receipt.GetProperty("outputRef").GetString(), executionResultSha256 = receipt.GetProperty("outputSha256").GetString()
    };
}

static async Task<object> RunTaskGetAsync(string jobsRoot, string pipeName)
{
    const string taskId = "task-question-truth-canary";
    const string runId = "run-question-truth-canary";
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new { schemaVersion = "1.0.0", taskId, intent = "clip.question", createdAt = DateTimeOffset.UtcNow });
    var source = new ArtifactReference("question-answer-source", "question.answer", "1.0.0", "artifacts/question-answer-source/artifact.json", new string('a', 64));
    var display = new ArtifactReference("question-answer-display", "question.answer.text", "1.0.0", "artifacts/question-answer-display/artifact.json", new string('b', 64));
    var binding = new QuestionWorkflowBinding(taskId, runId, "question-answer", "attempt-question-1", "operation-question-1");
    var token = new string('c', 64);
    var store = new EventStore(jobRoot, runId);
    await store.AppendAsync("task.state", taskId, new { status = "validating" });
    await store.AppendAsync("task.state", taskId, new { status = "queued" });
    await store.AppendAsync("task.state", taskId, new { status = "running" });
    await store.AppendAsync("task.question_answer_available", taskId, new { answerAvailable = true, questionTargetToken = token, binding, sourceAnswerArtifact = source, questionAnswerRef = display, answeredTurnCount = 2, latestAnswerId = "answer-2", sessionRef = $"runs/{runId}/stages/question-answer/attempts/attempt-question-1/question-session.json" });
    await store.AppendAsync("task.state", taskId, new { status = "waiting_input" });
    var request = new { protocolVersion = "1.0", correlationId = "task-get-truth", messageType = "task-query", payload = new { schemaVersion = "2.4.0", queryId = "query-task-truth", type = "task.get", taskId } };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, request));
    Assert(response.RootElement.GetProperty("ok").GetBoolean(), "task.get IPC response failed.");
    var result = response.RootElement.GetProperty("payload");
    Assert(result.GetProperty("type").GetString() == "task.get" && result.GetProperty("items").GetArrayLength() == 1, "task.get did not remain task-scoped.");
    var item = result.GetProperty("items").EnumerateArray().Single();
    var session = item.GetProperty("activeQuestionSession");
    Assert(item.GetProperty("status").GetString() == "waiting_input" && item.GetProperty("attentionRequired").GetBoolean(), "task.get omitted event-derived status/attention facts.");
    Assert(session.GetProperty("targetToken").GetString() == token && session.GetProperty("answeredTurnCount").GetInt32() == 2 && session.GetProperty("binding").GetProperty("taskId").GetString() == taskId && session.GetProperty("binding").GetProperty("runId").GetString() == runId, "task.get omitted authoritative session binding facts.");
    var evidencePath = Path.Combine(Path.GetDirectoryName(jobsRoot)!, "task-get-result.v2.4.json");
    await AtomicFile.WriteJsonOnceAsync(evidencePath, result.Clone());
    return new
    {
        status = "passed", queryType = "task.get", recentQueryUsed = false, taskId, runId, eventState = "waiting_input",
        attentionRequired = true, answeredTurnCount = 2, resultRef = "evidence/task-get-result.v2.4.json",
        resultSha256 = Sha(await File.ReadAllBytesAsync(evidencePath))
    };
}

static async Task<object> RunSingleVersionProcessAsync(string dotnet, string orchestratorRuntime, string jobsRoot, string registryPath, string releaseRoot, string ipcPath, string protocolVersion, Func<Task<object>> action)
{
    using var process = StartCoreProcess(dotnet, orchestratorRuntime, jobsRoot, registryPath, releaseRoot, ipcPath);
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    try
    {
        var result = await action();
        return new { protocolVersion, startup = "passed", typedAction = "passed", result };
    }
    finally
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert(stdout.Contains("\"status\"", StringComparison.Ordinal) && stdout.Contains("\"starting\"", StringComparison.Ordinal), $"IPC {protocolVersion} final process did not report real startup.");
        Assert(string.IsNullOrWhiteSpace(stderr), $"IPC {protocolVersion} final process wrote stderr: {stderr}");
    }
}

static Process StartCoreProcess(string dotnet, string orchestratorRuntime, string jobsRoot, string registryPath, string releaseRoot, string ipcPath)
{
    var executable = Path.GetExtension(orchestratorRuntime).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? orchestratorRuntime : dotnet;
    var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (!executable.Equals(orchestratorRuntime, StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(orchestratorRuntime);
    foreach (var argument in new[] { "serve", "--jobs-root", jobsRoot, "--registry", registryPath, "--release-root", releaseRoot, "--ipc-config", ipcPath }) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("Failed to start formal Core process.");
}

static async Task<string> RoundTripAsync(string pipeName, object request)
{
    var payload = JsonSerializer.Serialize(request, IpcJson.Options) + "\n";
    Exception? last = null;
    for (var attempt = 0; attempt < 80; attempt++)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(500);
            await client.WriteAsync(new UTF8Encoding(false).GetBytes(payload));
            await client.FlushAsync();
            using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, true);
            return await reader.ReadLineAsync() ?? throw new InvalidDataException("No IPC response.");
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            last = exception;
            await Task.Delay(25);
        }
    }
    throw new InvalidOperationException("IPC connection failed.", last);
}

static async Task<ArtifactReference> PublishArtifactAsync(string jobRoot, string id, string kind, byte[] payload)
{
    var root = Path.Combine(jobRoot, "artifacts", id);
    Directory.CreateDirectory(root);
    var payloadPath = Path.Combine(root, "payload.json");
    await File.WriteAllBytesAsync(payloadPath, payload);
    var manifestPath = Path.Combine(root, "artifact.json");
    await AtomicFile.WriteJsonOnceAsync(manifestPath, new
    {
        schemaVersion = "1.0.0", artifactId = id, kind, contractVersion = "1.0.0", mediaType = "application/json",
        files = new[] { new { path = $"artifacts/{id}/payload.json", sha256 = Sha(payload), bytes = payload.LongLength, mediaType = "application/json" } },
        createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId = "desktop-settings", workerId = "desktop.settings", workerVersion = "1.0.0", attemptId = "settings-1" },
        inputRefs = Array.Empty<object>(), dataClass = "sensitive", metadata = new { entrypoint = $"artifacts/{id}/payload.json" }
    });
    return new ArtifactReference(id, kind, "1.0.0", $"artifacts/{id}/artifact.json", Sha(await File.ReadAllBytesAsync(manifestPath)));
}

static WorkerRegistry Registry(string fakeWorker) => new("1.0.0", "2.10.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[]
{
    new WorkerEntry("content.deepseek", ">=1.0.0 <2.0.0", new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", "content.deepseek" }),
        new[] { "content.understand-and-card" }, new[] { "content-input" },
        new[] { "learning-bundle", "card-bundle", "language-practice-decision", "question.answer", "ocr-proofread-bundle" }, new[] { "parity-remnote", "cards-remnote", "cards-anki" }, "local", "idempotent", true),
    new WorkerEntry("speech.edge", ">=1.0.0 <2.0.0", new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", "speech.edge" }),
        new[] { "speech.synthesize", "speech.playback" }, new[] { "text.spoken", "audio.speech" }, new[] { "audio.speech" }, new[] { "parity-remnote", "cards-remnote", "cards-anki" }, "local", "idempotent", true),
    new WorkerEntry("export.remnote", ">=1.0.0 <2.0.0", new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", "export.remnote" }),
        new[] { "export-remnote" }, new[] { "learning.bundle", "card.bundle" }, new[] { "remnote.write-receipt", "remnote.import-package" }, new[] { "parity-remnote", "cards-remnote" }, "external", "reconcilable", true),
    new WorkerEntry("export.anki", ">=1.0.0 <2.0.0", new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", "export.anki" }),
        new[] { "anki-export" }, new[] { "card-bundle", "learning-bundle", "anki-export-config" },
        new[] { "anki-export-evidence", "anki-connection-status", "anki-managed-initialization-status" }, new[] { "cards-anki" }, "external", "reconcilable", true)
});

static IpcConfiguration Ipc(string pipeName, string protocolVersion) => new(protocolVersion + ".0", "windows-named-pipe", pipeName, $"\\\\.\\pipe\\{pipeName}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query", "stage-control", "module-action" }, true, "job-event-sequence", 1048576,
    new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"),
    $"schemas/ipc-request.v{protocolVersion}.schema.json", $"schemas/ipc-response.v{protocolVersion}.schema.json", "respond-when-possible-then-close");

static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
