using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
var workerId = "test.fake";
string? forcedStatus = null;
var waitForCurrentRunPlaybackCancel = false;
var describeMode = "valid";
string? describePidFile = null;
string? remNoteProbeState = null;
string? remNoteDiscoveryState = null;
var remNoteProbeExit = 0;
string? ankiInitializationMode = null;
string? ankiActionLog = null;
var optionIndex = 0;
while (optionIndex + 1 < args.Length && args[optionIndex].StartsWith("--", StringComparison.Ordinal))
{
    if (args[optionIndex] == "--worker-id") workerId = args[optionIndex + 1];
    else if (args[optionIndex] == "--force-status") forcedStatus = args[optionIndex + 1];
    else if (args[optionIndex] == "--wait-current-run-playback-cancel" && bool.TryParse(args[optionIndex + 1], out var wait)) waitForCurrentRunPlaybackCancel = wait;
    else if (args[optionIndex] == "--describe-mode") describeMode = args[optionIndex + 1];
    else if (args[optionIndex] == "--describe-pid-file") describePidFile = args[optionIndex + 1];
    else if (args[optionIndex] == "--remnote-probe-state") remNoteProbeState = args[optionIndex + 1];
    else if (args[optionIndex] == "--remnote-discovery-state") remNoteDiscoveryState = args[optionIndex + 1];
    else if (args[optionIndex] == "--remnote-probe-exit" && int.TryParse(args[optionIndex + 1], out var probeExit)) remNoteProbeExit = probeExit;
    else if (args[optionIndex] == "--anki-initialize-mode") ankiInitializationMode = args[optionIndex + 1];
    else if (args[optionIndex] == "--anki-action-log") ankiActionLog = args[optionIndex + 1];
    else break;
    optionIndex += 2;
}
var effectiveArgs = args[optionIndex..];

if (effectiveArgs.SequenceEqual(new[] { "describe", "--json" }))
{
    if (describeMode == "hang")
    {
        if (!string.IsNullOrWhiteSpace(describePidFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(describePidFile))!);
            await File.WriteAllTextAsync(describePidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 5;
    }
    if (describeMode == "invalid-json") { Console.WriteLine("{invalid-describe"); return 0; }
    if (describeMode == "nonzero") { Console.Error.WriteLine("injected describe failure"); return 4; }
    var description = JsonSerializer.SerializeToNode(Description(workerId), jsonOptions)!.AsObject();
    if (describeMode == "extra-field") description["unexpected"] = true;
    else if (describeMode == "parallel-over-limit") description["concurrency"]!["maxParallelAttempts"] = 129;
    else if (describeMode == "duplicate-stage")
    {
        var stages = description["stages"]!.AsArray();
        stages.Add(stages[0]!.GetValue<string>());
    }
    else if (describeMode != "valid") return 4;
    Console.WriteLine(description.ToJsonString(jsonOptions));
    return 0;
}
if (workerId == "content.deepseek" && effectiveArgs.SequenceEqual(new[] { "settings", "describe", "--json" }))
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = "content-settings-adapter/1.0.0",
        worker = new { id = "content.deepseek", version = "1.0.0" },
        credentialsPolicy = new { owner = "control-plane-local", persistence = "local-secret-store-or-user-environment", workerConsumption = "process-injection", taskPackages = "forbidden", logsReceiptsFixtures = "forbidden" },
        provider = new { apiKey = new { key = "provider.apiKey", status = "local-secret", valueExposed = false } },
        commands = new { describe = "settings describe --json", copy = "settings copy --target <absolute-directory> --json", validate = "settings validate --root <absolute-directory> --json", diff = "settings diff --root <absolute-directory> --json" }
    }, jsonOptions));
    return 0;
}
if (workerId == "content.deepseek" && effectiveArgs.Length == 5 && effectiveArgs[0] == "settings" && effectiveArgs[4] == "--json")
{
    var operation = effectiveArgs[1];
    var root = Path.GetFullPath(effectiveArgs[3]);
    if (operation == "copy" && effectiveArgs[2] == "--target")
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "prompts"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "prompts", "fake.md"), "Fake prompt\n");
        await File.WriteAllTextAsync(Path.Combine(root, "config", "categories.v2.json"), "{}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "content-settings.draft.json"), "{}\n");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = "content-settings-copy-result/1.0.0", targetRoot = root,
            draftFile = Path.Combine(root, "content-settings.draft.json"),
            validation = new { schemaVersion = "content-settings-validation/1.0.0", valid = true, draftRoot = root, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }
        }, jsonOptions));
        return 0;
    }
    if (operation == "validate" && effectiveArgs[2] == "--root")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "content-settings-validation/1.0.0", valid = true, draftRoot = root, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }, jsonOptions));
        return 0;
    }
    if (operation == "diff" && effectiveArgs[2] == "--root")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "content-settings-diff/1.0.0", valid = true, draftRoot = root, basedOn = new { }, current = new { }, changes = new { }, warnings = Array.Empty<object>(), errors = Array.Empty<object>() }, jsonOptions));
        return 0;
    }
}
if (workerId == "speech.edge" && effectiveArgs.Length == 6 && effectiveArgs[0] == "settings" && effectiveArgs[1] == "companion" && effectiveArgs[2] == "voices" && effectiveArgs[3] == "--input" && effectiveArgs[5] == "--json")
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = "speech-settings-voice-discovery-output.v1", companionVersion = "1.1.0", workerId = "speech.edge", workerVersion = "1.2.5",
        status = "ready", mode = "validate_only", source = "supplied_snapshot", provider = "edge-tts",
        catalogRevision = "catalog-fake-20260830-v1", catalogSha256 = new string('a', 64), updatedAtUtc = "2026-08-30T00:00:00Z",
        routes = new[] { "zh-CN" }, voices = new[] { new { language = "zh-CN", voiceId = "zh-CN-XiaoxiaoNeural" } },
        cache = new { used = false }, deployment = new { edgeTtsPath = (string?)null, ffplayPath = (string?)null },
        settingsMutationCount = 0, taskCreation = new { count = 0 }, physicalSpeakerPlayback = false,
        readOperationsPerformed = new[] { "validate_only" }, sideEffectsPerformed = Array.Empty<string>()
    }, jsonOptions));
    return 0;
}
if (workerId == "speech.edge" && effectiveArgs.Length == 6 && effectiveArgs[0] == "settings" && effectiveArgs[1] == "companion" && effectiveArgs[2] == "preview" && effectiveArgs[3] == "--input" && effectiveArgs[5] == "--json")
{
    using var previewInput = JsonDocument.Parse(await File.ReadAllBytesAsync(effectiveArgs[4]));
    var language = previewInput.RootElement.GetProperty("previewLanguage").GetString()!;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = "speech-settings-speaker-preview-output.v1", companionVersion = "1.1.0", workerId = "speech.edge", workerVersion = "1.2.5",
        status = "completed", action = "speaker.preview", scope = "project", effectiveAt = "preview_only", explicitAuthorizationConsumed = true,
        sampleSource = "worker_built_in_short_sample", sampleText = "Fake offline preview", languageTag = language, voiceId = "fake-offline-voice",
        voiceSnapshot = new { source = "fake-offline" }, naturalCompletion = true, playbackState = "idle", activePlayback = false,
        manualStopRequired = false, temporaryArtifactsRetained = false, previewAttemptCount = 1, audioPlayerInvocationCount = 1,
        taskCreation = new { count = 0 }, sideEffectsPerformed = new[] { "fake-offline-synthesis", "fake-offline-playback-contract" }
    }, jsonOptions));
    return 0;
}
if (workerId == "export.remnote" && effectiveArgs.Length == 4 && effectiveArgs[0] == "probe-public" && effectiveArgs[1] == "--request" && effectiveArgs[3] == "--json")
{
    var probeState = remNoteProbeState ?? "healthy";
    if (probeState == "malformed")
    {
        Console.WriteLine("{\"schemaVersion\":\"remnote-public-probe-result.v2\",\"externalWriteCount\":1}");
        return remNoteProbeExit;
    }
    var healthy = probeState == "healthy";
    var degraded = probeState == "degraded";
    var connected = healthy || degraded;
    var reasonCode = degraded ? "tool.timeout.15s" : "bridge.disconnected";
    var toolChecks = healthy
        ? new[] { new { operation = "status", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "search", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "list", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "read", outcome = "passed", durationMs = 1, reasonCode = "ok" } }
        : degraded
        ? new[] { new { operation = "status", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "search", outcome = "timeout", durationMs = 15000, reasonCode }, new { operation = "list", outcome = "not_run", durationMs = 0, reasonCode = "prior_check_failed" }, new { operation = "read", outcome = "not_run", durationMs = 0, reasonCode = "prior_check_failed" } }
        : new[] { new { operation = "status", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "search", outcome = "not_run", durationMs = 0, reasonCode = "prior_check_failed" }, new { operation = "list", outcome = "not_run", durationMs = 0, reasonCode = "prior_check_failed" }, new { operation = "read", outcome = "not_run", durationMs = 0, reasonCode = "prior_check_failed" } };
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = "remnote-public-probe-result.v2", probeId = "fake-probe", destination = new { type = "remnote", optional = true, peerDestinationTypes = new[] { "anki" } },
        worker = new { id = "export.remnote", version = "2.2.0", contractVersion = "1.0.0" }, state = probeState, admission = healthy ? "ready" : "blocked", timeoutMs = 15000,
        externalCall = true, externalCallCount = healthy ? 4 : 1, externalWriteCount = 0,
        connection = new { connected, acceptWriteOperations = connected, serverVersion = "fake", pluginVersion = connected ? "fake" : null, endpointClass = "loopback-local" },
        targetBinding = new { authority = "rootRemId+exact-readback", authorizationScope = "isolated-canary", rootRemId = "fake-root", expectedTitle = "Canary", observedTitle = healthy ? "Canary" : null, expectedType = "document", observedType = healthy ? "document" : null, matched = healthy, rootTitleRole = "readback-only-not-authority", changeEffectiveAt = "next-task" },
        routeCapabilities = new { clip = new { kind = "clip-daily-route", supported = true, timeZone = "Asia/Shanghai", dateFormat = "yyyy-MM-dd", preflight = healthy ? "ready" : "blocked" }, cards = new { kind = "card-export", supported = true, dateRouting = false }, originalMedia = new { contractVersion = "remnote-original-media.v2", readCapability = "media.images.v1", writeCapability = (string?)null, status = "unsupported", reasonCode = "mcp.media_write_unavailable" } },
        fixedSemantics = new { readback = "exact", idempotency = "reconcilable", unknownExternalStateRetry = "manual-only", automaticRetryAllowed = false },
        toolChecks, reasonCodes = healthy ? Array.Empty<string>() : new[] { reasonCode }, sanitized = true, generatedAt = "2026-08-30T00:00:00Z"
    }, jsonOptions));
    return remNoteProbeExit;
}
if (workerId == "export.remnote" && effectiveArgs.Length == 4 && effectiveArgs[0] == "discover-target-public" && effectiveArgs[1] == "--request" && effectiveArgs[3] == "--json")
{
    using var discoveryRequest = JsonDocument.Parse(await File.ReadAllBytesAsync(effectiveArgs[2]));
    var rootTitle = discoveryRequest.RootElement.GetProperty("selector").GetProperty("rootTitle").GetString()!;
    var discoveryId = discoveryRequest.RootElement.GetProperty("discoveryId").GetString()!;
    var state = remNoteDiscoveryState ?? "healthy";
    var healthy = state == "healthy";
    var ambiguous = state == "ambiguous";
    var notFound = state == "not_found";
    var timeout = state == "timeout";
    var reason = ambiguous ? "target.discovery_ambiguous" : notFound ? "target.discovery_not_found" : timeout ? "tool.timeout.15s" : "permission.write_operations_disabled";
    var resolution = healthy ? "unique" : ambiguous ? "ambiguous" : notFound ? "not_found" : "unavailable";
    var checks = healthy
        ? new[] { new { operation = "status", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "search", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "list", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "read", outcome = "passed", durationMs = 1, reasonCode = "ok" } }
        : new[] { new { operation = "status", outcome = "passed", durationMs = 1, reasonCode = "ok" }, new { operation = "search", outcome = timeout ? "timeout" : "passed", durationMs = timeout ? 15000 : 1, reasonCode = reason }, new { operation = "list", outcome = "not_run", durationMs = 0, reasonCode = "prior_check_failed" }, new { operation = "read", outcome = "not_run", durationMs = 0, reasonCode = "prior_check_failed" } };
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = "remnote-target-discovery-result.v1", discoveryId,
        destination = new { type = "remnote", optional = true, peerDestinationTypes = new[] { "anki" } },
        worker = new { id = "export.remnote", version = "2.3.0", contractVersion = "1.0.0" },
        state = healthy ? "healthy" : timeout ? "degraded" : "blocked", admission = healthy ? "ready" : "blocked", timeoutMs = 15000,
        externalCall = true, externalCallCount = healthy ? 4 : 2, externalWriteCount = 0,
        connection = new { connected = healthy || timeout, acceptWriteOperations = healthy, serverVersion = "fake", pluginVersion = "fake", endpointClass = "loopback-local" },
        selection = new { authorizationScope = "configured-destination", selector = new { rootTitle, expectedType = "document", match = "exact", uniqueness = "required" }, candidateCount = healthy ? 1 : ambiguous ? 2 : 0, resolution, resolvedRootRemId = healthy ? "fake-root" : null, observedTitle = healthy ? rootTitle : null, observedType = healthy ? "document" : null, exactReadback = healthy, authority = "resolved-rootRemId+exact-readback", rootTitleRole = "discovery-selector-not-authority", persistableBinding = healthy, changeEffectiveAt = "next-task" },
        fixedSemantics = new { automaticRetryAllowed = false, unknownExternalStateRetry = "manual-only" }, toolChecks = checks,
        reasonCodes = healthy ? Array.Empty<string>() : new[] { reason }, sanitized = true, generatedAt = "2026-09-01T00:00:00Z"
    }, jsonOptions));
    return 0;
}
if (effectiveArgs.Length != 3 || effectiveArgs[0] != "run" || effectiveArgs[1] != "--request" || !Path.IsPathFullyQualified(effectiveArgs[2])) return 2;

var requestPath = effectiveArgs[2];
using var requestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
var request = requestDocument.RootElement;
var attemptDirectory = Path.GetDirectoryName(requestPath)!;
var cancelPath = Path.Combine(attemptDirectory, "cancel.json");
var resultPath = Path.Combine(attemptDirectory, "result.json");
var mode = request.GetProperty("payload").TryGetProperty("mode", out var modeValue) ? modeValue.GetString() : "pipeline";
Console.WriteLine("{\"schemaVersion\":\"1.0.0\",\"type\":\"worker.started\"}");

if (workerId == "export.anki" && request.GetProperty("operation").GetString() == "initialize-managed")
{
    var initializeMode = ankiInitializationMode ?? "initialized";
    var created = initializeMode == "initialized";
    if (!string.IsNullOrWhiteSpace(ankiActionLog))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ankiActionLog))!);
        await File.AppendAllTextAsync(ankiActionLog, JsonSerializer.Serialize(new
        {
            operation = "initialize-managed",
            mode = initializeMode,
            provider = "fake-anki",
            realAnkiConnectCalls = 0,
            createModelCalls = created ? 1 : 0,
            createDeckCalls = created ? 1 : 0,
            exactReadback = initializeMode != "unknown_external_state"
        }, jsonOptions) + Environment.NewLine);
    }
    if (initializeMode == "unknown_external_state")
    {
        var unknown = CreateResult(request, workerId, "unknown_external_state", Array.Empty<object>(),
            new { code = "ANKI_RESPONSE_LOSS", category = "effect", retryable = false, detailsRef = request.GetProperty("attemptPath").GetString() + "/request.json" },
            new { operation = "initialize-managed", initializationStatus = "unknown_external_state" }, null, "1.4.0");
        var unknownMap = (Dictionary<string, object?>)unknown;
        unknownMap["sideEffects"] = new[] { new { kind = "anki.initialize-managed", idempotencyKey = request.GetProperty("idempotencyKey").GetString(), state = "unknown", receiptRef = (object?)null } };
        await WriteOnceAsync(resultPath, unknownMap, jsonOptions);
        return 17;
    }
    var initializationStatus = new
    {
        schemaVersion = "anki-managed-initialization-status.v1",
        status = created ? "initialized" : "already_current",
        checkedAt = DateTimeOffset.UtcNow,
        operation = "initialize-managed",
        effectClass = "provider-write",
        networkScope = "loopback-only",
        externalMutation = true,
        permission = "granted",
        apiVersion = 6,
        activeProfileSha256 = new string('a', 64),
        authorization = new { mode = "explicit-user-action", scope = "managed-models-and-decks" },
        models = new[] { new { modelNameSha256 = new string('b', 64), noteTypeKeys = new[] { "image_qa" }, status = created ? "created" : "already_current", readback = "exact" } },
        decks = new[] { new { deckNameSha256 = new string('c', 64), status = created ? "created" : "already_current", readback = "exact" } },
        writes = new { modelsCreated = created ? 1 : 0, decksCreated = created ? 1 : 0 },
        actionsObserved = created ? new[] { "requestPermission", "version", "getActiveProfile", "modelNames", "deckNames", "createModel", "createDeck", "modelNames", "deckNames" } : new[] { "requestPermission", "version", "getActiveProfile", "modelNames", "deckNames" },
        mutatingActionsObserved = created ? new[] { "createModel", "createDeck" } : Array.Empty<string>(),
        forbiddenActionsObserved = Array.Empty<string>(),
        automaticRetryAllowed = false,
        replayPolicy = "explicit-reconcile-query-then-create-missing",
        notesMediaAndExistingResourcesModified = false
    };
    var statusBytes = JsonSerializer.SerializeToUtf8Bytes(initializationStatus, jsonOptions);
    var output = await PublishArtifactAsync(request, attemptDirectory, workerId, "anki-managed-initialization-status", "anki-managed-initialization-status", "initialization-status.json", statusBytes,
        "application/vnd.knowledge-clipper.anki-managed-initialization-status+json", jsonOptions);
    var completed = CreateResult(request, workerId, "completed", new[] { output }, null,
        new { operation = "initialize-managed", initializationStatus = created ? "initialized" : "already_current" }, null, "1.4.0");
    var completedMap = (Dictionary<string, object?>)completed;
    completedMap["sideEffects"] = new[] { new { kind = "anki.initialize-managed", idempotencyKey = request.GetProperty("idempotencyKey").GetString(), state = "verified", receiptRef = output } };
    await WriteOnceAsync(resultPath, completedMap, jsonOptions);
    return 0;
}

if (mode is "success" or "pipeline" || workerId != "test.fake")
{
    if (workerId == "speech.edge" && request.GetProperty("operation").GetString() == "speech.playback" &&
        waitForCurrentRunPlaybackCancel && PlaybackConsumesCurrentRunAudio(request))
    {
        while (!File.Exists(cancelPath)) await Task.Delay(25);
        using var cancelDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cancelPath));
        var reason = cancelDocument.RootElement.GetProperty("reason").GetString()!;
        var playbackState = reason switch
        {
            "user_stop" => "stopped",
            "superseded" => "superseded",
            "task_cancel" => "cancelled",
            "timeout" => "timed_out",
            _ => "failed"
        };
        var playbackStatus = reason == "timeout" ? "failed" : "cancelled";
        var playbackError = new { code = $"cancel.{reason}", category = reason == "timeout" ? "transient" : "cancelled", retryable = reason == "timeout", detailsRef = request.GetProperty("cancelSignal").GetProperty("path").GetString() };
        var payload = new
        {
            operationId = request.GetProperty("operationId").GetString(),
            languageTag = "zh-CN",
            voiceId = "zh-CN-XiaoxiaoNeural",
            cacheHit = false,
            playbackState
        };
        await WriteOnceAsync(resultPath, CreateResult(request, workerId, playbackStatus, Array.Empty<object>(), playbackError, payload, null), jsonOptions);
        return 0;
    }
    if (workerId != "test.fake") await Task.Delay(100);
    var outputs = new List<object>();
    var isImageContinuation = workerId == "image.ocr.mineru" && request.GetProperty("payload").TryGetProperty("retryModuleResultPath", out _);
    var status = forcedStatus == "partial-once"
        ? isImageContinuation ? "completed" : "partial"
        : forcedStatus == "missing-output"
        ? "completed"
        : forcedStatus ?? (request.GetProperty("payload").TryGetProperty("fakeStatus", out var fakeStatus) ? fakeStatus.GetString() ?? "completed" : "completed");
    object[]? diagnostics = null;
    string? contentModuleReleaseState = null;
    if (workerId == "image.ocr.mineru")
    {
        var itemIds = await ReadCaptureImageIdsAsync(request, attemptDirectory);
        if (itemIds.Count == 0) itemIds.Add("capture.image.question.0001");
        if (status == "unknown_external_state")
        {
            var unknownError = new { code = "fake.provider_submission_unknown", category = "effect", retryable = false, detailsRef = request.GetProperty("attemptPath").GetString() + "/request.json" };
            await WriteOnceAsync(resultPath, CreateResult(request, workerId, status, Array.Empty<object>(), unknownError, new { partialBlocksDownstream = true }, null), jsonOptions);
            return 0;
        }
        var itemDiagnostics = new List<object>();
        var originalMode = await ReadCaptureImageHandlingAsync(request, attemptDirectory) == "original";
        var priorCompleted = isImageContinuation
            ? await ReadPriorCompletedDiagnosticsAsync(request, attemptDirectory)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var providerCallItemIds = new List<string>();
        var originalItems = new List<(string ItemId, byte[] Bytes, string MediaType)>();
        var markdownItems = new List<(string ItemId, byte[] Bytes)>();
        for (var index = 0; index < itemIds.Count; index++)
        {
            if (priorCompleted.TryGetValue(itemIds[index], out var reused))
            {
                var reusedOutputs = reused.GetProperty("outputs").EnumerateArray().Select(item => item.Clone()).ToArray();
                outputs.AddRange(reusedOutputs.Cast<object>());
                itemDiagnostics.Add(new { itemId = itemIds[index], status = "completed", outputs = reusedOutputs, error = (object?)null });
                continue;
            }
            if (status == "partial" && index == itemIds.Count - 1)
            {
                itemDiagnostics.Add(new { itemId = itemIds[index], status = "failed", outputs = Array.Empty<object>(), error = new { code = "fake.item_failed", category = "worker", retryable = true, detailsRef = request.GetProperty("attemptPath").GetString() + "/request.json" } });
                continue;
            }
            providerCallItemIds.Add(itemIds[index]);
            object output;
            object[] diagnosticOutputs;
            if (originalMode)
            {
                var sourceImage = await ReadCaptureImageAsync(request, attemptDirectory, itemIds[index]);
                output = await PublishArtifactAsync(request, attemptDirectory, workerId, itemIds[index], "image.original", "original.bin", sourceImage.Bytes, sourceImage.MediaType, jsonOptions);
                originalItems.Add((itemIds[index], sourceImage.Bytes, sourceImage.MediaType));
                diagnosticOutputs = new[] { output };
            }
            else
            {
                var sourceImage = await ReadCaptureImageAsync(request, attemptDirectory, itemIds[index]);
                originalItems.Add((itemIds[index], sourceImage.Bytes, sourceImage.MediaType));
                var originalOutput = await PublishArtifactAsync(request, attemptDirectory, workerId, itemIds[index], "image.original", "original.bin", sourceImage.Bytes, sourceImage.MediaType, jsonOptions);
                var sourceMarker = Encoding.UTF8.GetString(sourceImage.Bytes);
                var ocrText = sourceMarker.Contains("OCR_UNRESOLVED_FIXTURE", StringComparison.Ordinal)
                    ? $"OCR fixture ambiguous CANAR for {itemIds[index]}"
                    : sourceMarker.StartsWith("OCR_PROOFREAD_SEGMENT_", StringComparison.Ordinal)
                        ? sourceMarker
                        : $"OCR fixture text for {itemIds[index]}";
                var markdownBytes = Encoding.UTF8.GetBytes(ocrText);
                output = await PublishArtifactAsync(request, attemptDirectory, workerId, $"ocr.{itemIds[index]}.markdown", "image-ocr.markdown", "ocr.md", markdownBytes, "text/markdown", jsonOptions);
                var structuredBytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = "image-ocr-structured.v1",
                    itemId = itemIds[index],
                    text = ocrText
                }, jsonOptions);
                var structuredOutput = await PublishArtifactAsync(request, attemptDirectory, workerId, $"ocr.{itemIds[index]}.structured", "image-ocr.structured", "ocr.json", structuredBytes, "application/json", jsonOptions);
                markdownItems.Add((itemIds[index], markdownBytes));
                outputs.Add(originalOutput);
                outputs.Add(structuredOutput);
                diagnosticOutputs = new[] { originalOutput, output, structuredOutput };
            }
            outputs.Add(output);
            itemDiagnostics.Add(new { itemId = itemIds[index], status = "completed", outputs = diagnosticOutputs, error = (object?)null });
        }
        if (originalMode && status == "completed")
        {
            var questionItems = originalItems.Where(item => item.ItemId.Contains(".question.", StringComparison.Ordinal)).ToArray();
            var answerItems = originalItems.Where(item => item.ItemId.Contains(".answer.", StringComparison.Ordinal)).ToArray();
            object Row((string ItemId, byte[] Bytes, string MediaType) item, string role, int imageIndex, int groupIndex) => new
            {
                artifactRef = item.ItemId, bytes = item.Bytes.LongLength, groupIndex, height = 1, imageIndex,
                mediaType = item.MediaType, role, sha256 = Sha256(item.Bytes), width = 1
            };
            var bundleBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "image-original-media-bundle.v1", captureId = $"capture-{request.GetProperty("taskId").GetString()}",
                mode = "original_passthrough", providerCalls = 0,
                questionImages = questionItems.Select((item, index) => Row(item, "question", index, index)).ToArray(),
                answerImages = answerItems.Select((item, index) => Row(item, "answer", index, questionItems.Length + index)).ToArray()
            }, jsonOptions);
            outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "image.original.bundle", "image.original.bundle", "media-bundle.json", bundleBytes,
                "application/vnd.knowledge-clipper.image-original-media-bundle+json", jsonOptions));
            var provenanceBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "image-processing-provenance.v1", attemptId = request.GetProperty("attemptId").GetString(),
                captureId = $"capture-{request.GetProperty("taskId").GetString()}", mode = "original_passthrough",
                operationId = request.GetProperty("operationId").GetString(), terminalStatus = "completed",
                provider = new { calls = 0, cloudUploadAuthorized = false, id = (string?)null },
                sourceOrder = new { questionImages = questionItems.Select(item => item.ItemId).ToArray(), answerImages = answerItems.Select(item => item.ItemId).ToArray() },
                items = originalItems.Select((item, index) => new
                {
                    artifactRef = item.ItemId, error = (object?)null, groupIndex = index,
                    imageIndex = item.ItemId.Contains(".question.", StringComparison.Ordinal)
                        ? Array.IndexOf(questionItems, item) : Array.IndexOf(answerItems, item),
                    representations = new { original = new { artifactRef = item.ItemId, bytes = item.Bytes.LongLength, mediaType = item.MediaType, sha256 = Sha256(item.Bytes) } },
                    reusedFromAttempt = (string?)null, role = item.ItemId.Contains(".question.", StringComparison.Ordinal) ? "question" : "answer", status = "completed"
                }).ToArray(),
                continuation = new
                {
                    automaticDownstreamAllowed = false, defaultOcrRerun = false, failedArtifactRefs = Array.Empty<string>(),
                    humanActions = new[] { "accept", "correct" }, humanJudgmentAvailable = true, partialBlocksDownstream = true,
                    requiresOriginalArtifactHashMatch = true, requiresSuccessfulOutputHashMatch = true, retryScope = "none",
                    reusableCompletedArtifactRefs = originalItems.Select(item => item.ItemId).ToArray()
                }
            }, jsonOptions);
            outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "image.processing.provenance", "image.processing.provenance", "provenance.json", provenanceBytes,
                "application/vnd.knowledge-clipper.image-processing-provenance+json", jsonOptions));
        }
        else if (!originalMode && status == "completed")
        {
            var provenanceBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "image-processing-provenance.v1", attemptId = request.GetProperty("attemptId").GetString(),
                captureId = $"capture-{request.GetProperty("taskId").GetString()}", mode = "ocr_mineru",
                operationId = request.GetProperty("operationId").GetString(), terminalStatus = "completed",
                provider = new { calls = 1, cloudUploadAuthorized = true, id = "fake-mineru" },
                sourceOrder = new
                {
                    questionImages = originalItems.Where(item => item.ItemId.Contains(".question.", StringComparison.Ordinal)).Select(item => item.ItemId).ToArray(),
                    answerImages = originalItems.Where(item => item.ItemId.Contains(".answer.", StringComparison.Ordinal)).Select(item => item.ItemId).ToArray()
                },
                items = originalItems.Select((item, index) =>
                {
                    var markdown = markdownItems.Single(entry => entry.ItemId == item.ItemId);
                    return new
                    {
                        artifactRef = item.ItemId, error = (object?)null, groupIndex = index,
                        imageIndex = index, role = item.ItemId.Contains(".question.", StringComparison.Ordinal) ? "question" : "answer", status = "completed",
                        reusedFromAttempt = (string?)null,
                        representations = new
                        {
                            original = new { artifactRef = item.ItemId, bytes = item.Bytes.LongLength, mediaType = item.MediaType, sha256 = Sha256(item.Bytes) },
                            markdown = new { artifactRef = $"ocr.{item.ItemId}.markdown", bytes = markdown.Bytes.LongLength, mediaType = "text/markdown", sha256 = Sha256(markdown.Bytes) }
                        }
                    };
                }).ToArray(),
                continuation = new
                {
                    automaticDownstreamAllowed = true, defaultOcrRerun = false, failedArtifactRefs = Array.Empty<string>(),
                    humanActions = new[] { "accept", "correct" }, humanJudgmentAvailable = true, partialBlocksDownstream = true,
                    requiresOriginalArtifactHashMatch = true, requiresSuccessfulOutputHashMatch = true, retryScope = "none",
                    reusableCompletedArtifactRefs = originalItems.Select(item => item.ItemId).ToArray()
                }
            }, jsonOptions);
            outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "image.processing.provenance", "image.processing.provenance", "provenance.json", provenanceBytes,
                "application/vnd.knowledge-clipper.image-processing-provenance+json", jsonOptions));
        }
        diagnostics = itemDiagnostics.ToArray();
        outputs.Reverse(); // Deliberately differ from capture order; consumers must use itemDiagnostics/itemId.
        var moduleResultPath = request.GetProperty("attemptPath").GetString() + "/module-result.json";
        var moduleResultAbsolute = Path.Combine(attemptDirectory, "module-result.json");
        await WriteOnceAsync(moduleResultAbsolute, new
        {
            schemaVersion = "fake-image-module-result.v1",
            mode = originalMode ? "original_passthrough" : "ocr_mineru",
            itemDiagnostics,
            providerCallItemIds = originalMode ? Array.Empty<string>() : providerCallItemIds.ToArray()
        }, jsonOptions);
        var moduleResultSha = Sha256(await File.ReadAllBytesAsync(moduleResultAbsolute));
        var imagePayload = new
        {
            mode = originalMode ? "original_passthrough" : "ocr_mineru",
            moduleResultPath,
            moduleResultSha256 = moduleResultSha,
            partialBlocksDownstream = true,
            providerCallItemIds = originalMode ? Array.Empty<string>() : providerCallItemIds.ToArray()
        };
        await WriteOnceAsync(resultPath, CreateResult(request, workerId, status, outputs.ToArray(), null, imagePayload, diagnostics), jsonOptions);
        return 0;
    }
    else if (workerId == "content.deepseek")
    {
        var operation = request.GetProperty("operation").GetString();
        if (operation == "question.answer")
        {
            var questionInput = await ReadQuestionInputAsync(request, attemptDirectory);
            var workflow = questionInput.RootElement.GetProperty("questionWorkflow");
            var questionTurns = workflow.GetProperty("questionTurns").EnumerateArray().ToArray();
            var ancestry = workflow.GetProperty("answerAncestry").EnumerateArray().ToArray();
            var latestQuestion = questionTurns[^1].GetProperty("question").GetString()!;
            var tamperLearningAncestry = latestQuestion.Contains("TAMPER_LEARNING_ANCESTRY", StringComparison.Ordinal);
            var tamperAnswerPayload = latestQuestion.Contains("TAMPER_ANSWER_PAYLOAD", StringComparison.Ordinal);
            var tamperAnswerManifest = latestQuestion.Contains("TAMPER_ANSWER_MANIFEST", StringComparison.Ordinal);
            var tamperAnswerChain = latestQuestion.Contains("TAMPER_ANSWER_CHAIN", StringComparison.Ordinal);
            var latestTurnId = questionTurns[^1].GetProperty("turnId").GetString()!;
            var generatedAnswer = $"Fake answer {questionTurns.Length}: {latestQuestion}";
            var answerId = $"answer-{Sha256(Encoding.UTF8.GetBytes(latestTurnId + "\0" + generatedAnswer))[..24]}";
            var learning = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = "learning-bundle/1.0.0", bundle_id = $"learning-question-{questionTurns.Length}", package_id = "package-question",
                task_id = request.GetProperty("taskId").GetString(), input_version = "3.0.0", attempt_id = request.GetProperty("attemptId").GetString(),
                created_at = DateTimeOffset.UtcNow, input = new { }, worker = new { name = "fake", version = "1.0.0" }, items = Array.Empty<object>(),
                ai_trace = Array.Empty<object>(), release_state = "ready", unresolved_items = Array.Empty<object>(), gates = Array.Empty<object>()
            }, jsonOptions);
            var learningOutput = await PublishArtifactAsync(request, attemptDirectory, workerId, "learning-bundle", "learning-bundle", "learning.json", learning, "application/json", jsonOptions);
            outputs.Add(learningOutput);
            var persisted = ancestry.Select((item, index) => new Dictionary<string, object?>
            {
                ["answer_id"] = item.GetProperty("answerId").GetString(), ["artifact_ref"] = item.GetProperty("artifactRef").GetString(),
                ["turn_id"] = item.GetProperty("turnId").GetString(), ["turn_index"] = item.GetProperty("turnIndex").GetInt32(),
                ["question"] = questionTurns[index].GetProperty("question").GetString(), ["question_sha256"] = item.GetProperty("questionSha256").GetString(),
                ["answer_text"] = item.GetProperty("answerText").GetString(), ["answer_text_sha256"] = item.GetProperty("answerTextSha256").GetString(),
                ["parent_answer_id"] = item.TryGetProperty("parentAnswerId", out var parent) ? parent.GetString() : null,
                ["provenance"] = "persisted"
            }).ToList();
            persisted.Add(new Dictionary<string, object?>
            {
                ["answer_id"] = answerId, ["artifact_ref"] = "pending", ["turn_id"] = latestTurnId, ["turn_index"] = questionTurns.Length - 1,
                ["question"] = latestQuestion, ["question_sha256"] = tamperAnswerChain ? new string('0', 64) : Sha256(Encoding.UTF8.GetBytes(latestQuestion)),
                ["answer_text"] = generatedAnswer, ["answer_text_sha256"] = Sha256(Encoding.UTF8.GetBytes(generatedAnswer)),
                ["parent_answer_id"] = ancestry.Length == 0 ? null : ancestry[^1].GetProperty("answerId").GetString(), ["provenance"] = "generated"
            });
            foreach (var record in persisted)
                if (record["parent_answer_id"] is null) record.Remove("parent_answer_id");
            var learningPath = request.GetProperty("attemptPath").GetString() + "/outputs/learning-bundle/v0001/learning.json";
            var answer = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = "question-answer/1.0.0", answer_bundle_id = $"question-answer-{questionTurns.Length}", package_id = "package-question",
                task_id = request.GetProperty("taskId").GetString(), input_version = "3.0.0", attempt_id = request.GetProperty("attemptId").GetString(),
                created_at = DateTimeOffset.UtcNow, phase = "answer", learning_bundle_path = learningPath, learning_bundle_sha256 = Sha256(learning),
                question_chain_sha256 = Sha256(JsonSerializer.SerializeToUtf8Bytes(persisted, jsonOptions)), new_answer_id = answerId,
                turns = persisted, gates = new[] { new { gate_id = "fake.question", status = "pass", evidence = "deterministic fake answer" } }
            }, jsonOptions);
            outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "question-answer", "question.answer", "question-answer.json", answer, "application/json", jsonOptions,
                tamperLearningAncestry ? null : JsonSerializer.SerializeToElement(learningOutput, jsonOptions)));
            if (tamperAnswerPayload)
                await File.AppendAllTextAsync(Path.Combine(attemptDirectory, "outputs", "question-answer", "v0001", "question-answer.json"), " ");
            if (tamperAnswerManifest)
                await File.AppendAllTextAsync(Path.Combine(attemptDirectory, "outputs", "question-answer", "v0001", "artifact.json"), " ");
            questionInput.Dispose();
        }
        else
        {
        var decisionMode = request.GetProperty("payload").TryGetProperty("fakeLanguagePracticeDecision", out var decisionFlag) && decisionFlag.GetBoolean();
        var decisionTamper = request.GetProperty("payload").TryGetProperty("fakeLanguagePracticeTamper", out var tamperFlag)
            ? tamperFlag.GetString()
            : null;
        var hasLanguageChoice = decisionMode && await ContentInputHasLanguageChoiceAsync(request, attemptDirectory);
        if (decisionMode && !hasLanguageChoice)
        {
            var learning = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = "learning-bundle/1.0.0",
                bundle_id = "learning-language-decision",
                package_id = "package-language-decision",
                task_id = request.GetProperty("taskId").GetString(),
                input_version = "2.0.0",
                attempt_id = request.GetProperty("attemptId").GetString(),
                created_at = DateTimeOffset.UtcNow,
                input = new { }, worker = new { name = "fake", version = "1.0.0" }, items = Array.Empty<object>(), ai_trace = Array.Empty<object>(),
                release_state = "requires_human", unresolved_items = Array.Empty<object>(), gates = Array.Empty<object>()
            }, jsonOptions);
            outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "learning-bundle", "learning-bundle", "learning.json", learning, "application/json", jsonOptions));
            var learningPath = request.GetProperty("attemptPath").GetString() + "/outputs/learning-bundle/v0001/learning.json";
            var decision = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = "language-practice-decision/1.0.0",
                decision_bundle_id = "language-decision-mixed-fake",
                package_id = "package-language-decision",
                task_id = request.GetProperty("taskId").GetString(),
                input_version = "2.0.0",
                attempt_id = request.GetProperty("attemptId").GetString(),
                created_at = DateTimeOffset.UtcNow,
                learning_bundle_path = learningPath,
                learning_bundle_sha256 = decisionTamper == "learning-ancestry" ? new string('0', 64) : Sha256(learning).ToLowerInvariant(),
                state = "requires_human",
                decisions = new object[]
                {
                    new { decision_id = "decision-zh", item_id = "item-zh", unit_id = "unit-zh", category_id = "language_zh", state = "requires_human", reasons = new[] { "language_practice_mode_required" }, allowed_modes = new[] { "meaning-reading", "writing", "both" }, requested_override_paths = new[] { "human_overrides.languagePracticeMode" }, source_segment_ids = new[] { "segment-zh" } },
                    new { decision_id = "decision-en", item_id = "item-en", unit_id = "unit-en", category_id = "language_en", state = "requires_human", reasons = new[] { "language_practice_mode_required" }, allowed_modes = new[] { "meaning-reading", "writing", "both" }, requested_override_paths = new[] { "human_overrides.languagePracticeMode" }, source_segment_ids = new[] { "segment-en" } }
                }
            }, jsonOptions);
            outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "language-practice-decision", "language-practice-decision", "decision.json", decision, "application/json", jsonOptions));
            if (decisionTamper == "decision-payload")
                await File.AppendAllTextAsync(Path.Combine(attemptDirectory, "outputs", "language-practice-decision", "v0001", "decision.json"), " ");
            else if (decisionTamper == "decision-manifest")
                await File.AppendAllTextAsync(Path.Combine(attemptDirectory, "outputs", "language-practice-decision", "v0001", "artifact.json"), " ");
        }
        else
        {
        var materialText = await ReadContentMaterialAsync(request, attemptDirectory);
        var contentInputVersion = await ReadContentInputSchemaVersionAsync(request, attemptDirectory);
        var cleanedText = materialText.Contains("MULTILINE_FIXTURE", StringComparison.Ordinal) ? "Line one\nLine two" : "Fake cleaned learning text";
        var frontText = materialText.Contains("RESERVED_FIXTURE", StringComparison.Ordinal) ? "Question >> answer" : "Fake question";
        if (contentInputVersion == "standard-input/5.0.0")
        {
        using var resolutionInput = await ReadContentInputAsync(request, attemptDirectory);
        var resolutionItem = resolutionInput.RootElement.GetProperty("items")[0];
        var resolution = resolutionItem.GetProperty("ocr_human_resolution");
        var resolvedItemId = resolutionItem.GetProperty("item_id").GetString()!;
        var resolvedAction = resolution.GetProperty("action").GetString()!;
        var resolvedText = resolvedAction == "user_correction"
            ? resolution.GetProperty("correction").GetProperty("corrected_text").GetString()!
            : materialText;
        var frozenSourcePath = resolutionItem.GetProperty("source").GetProperty("material_path").GetString()!;
        var humanBundle = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "ocr-human-resolution-bundle/1.0.0", bundle_id = "ocr-human-resolution-fake", package_id = "package-fake",
            task_id = request.GetProperty("taskId").GetString(), input_version = "5.0.0", attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow,
            records = new[] { new { resolution_id = resolution.GetProperty("resolution_id").GetString(), item_id = resolvedItemId, state = "resolved", action = resolvedAction, actor = "user", provenance = "human_submitted", reason = resolution.GetProperty("reason").GetString(), resolved_text = resolvedText, resolved_text_sha256 = Sha256(Encoding.UTF8.GetBytes(resolvedText)) } },
            release_state = "ready", gates = Array.Empty<object>()
        }, jsonOptions);
        var humanOutput = await PublishArtifactAsync(request, attemptDirectory, workerId, "ocr-human-resolution-bundle", "ocr-human-resolution-bundle", "resolution.json", humanBundle, "application/json", jsonOptions);
        outputs.Add(humanOutput);
        var humanReference = JsonSerializer.SerializeToElement(humanOutput, jsonOptions);
        var humanPath = request.GetProperty("attemptPath").GetString() + "/outputs/ocr-human-resolution-bundle/v0001/resolution.json";
        var learning = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "learning-bundle/3.0.0", bundle_id = "learning-ocr-human-fake", package_id = "package-fake",
            task_id = request.GetProperty("taskId").GetString(), input_version = "5.0.0", attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow,
            input = new { schema_version = "standard-input/5.0.0" }, ocr_human_resolution_bundle_path = humanPath, ocr_human_resolution_bundle_sha256 = Sha256(humanBundle),
            worker = new { name = "fake", version = "1.0.0" },
            items = new[] { new { item_id = resolvedItemId, state = "ready", cleaning = new { cleaned_text = resolvedText, format_anomalies = Array.Empty<string>(), removals = Array.Empty<object>(), state = "ready" }, proofreading = new { state = "unresolved", proofread_segments = new[] { new { segment_index = 0, segment_id = "proofread-ocr", source_kind = "ocr_text", source_path = frozenSourcePath, start = 0, end = materialText.Length, text = materialText } } }, human_resolution = new { state = "resolved", actor = "user", action = resolvedAction }, units = new[] { new { unit_id = "unit-ocr-human", source_segment_ids = new[] { "segment-ocr-human" }, category_id = "question_answer", subtype = "basic_qa", answer_state = "missing", context_state = "provided", risk_state = "ordinary", generated_content = new[] { new { kind = "answer", text = "Fake human-resolved answer" } }, state = "ready", unresolved_reasons = Array.Empty<string>(), gates = Array.Empty<object>() } }, gates = Array.Empty<object>(), source_kind = "ocr_text", source_refs = Array.Empty<object>(), source_segments = new[] { new { segment_index = 0, segment_id = "segment-ocr-human", source_kind = "ocr_text", source_path = "resolved", start = 0, end = resolvedText.Length, text = resolvedText } }, unresolved_reasons = Array.Empty<string>() } },
            ai_trace = Array.Empty<object>(), release_state = "ready", unresolved_items = Array.Empty<object>(), gates = Array.Empty<object>()
        }, jsonOptions);
        outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "learning-bundle", "learning-bundle", "learning.json", learning, "application/json", jsonOptions, humanReference, "3.0.0"));
        var cards = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "card-bundle/1.0.0", bundle_id = "cards-ocr-human-fake", package_id = "package-fake", task_id = request.GetProperty("taskId").GetString(),
            attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow, learning_bundle_path = "learning.json", learning_bundle_sha256 = Sha256(learning),
            cards = new[] { new { answer_provenance = "generated", back_blocks = new[] { new { block_id = "back-ocr-human", provenance = "generated", text = "Fake human-resolved answer", type = "text" } }, card_id = "card-ocr-human", category_id = "question_answer", front_blocks = new[] { new { block_id = "front-ocr-human", provenance = "human_submitted", text = resolvedText, type = "text" } }, item_id = resolvedItemId, source_segment_ids = new[] { "segment-ocr-human" }, subtype = "basic_qa", template = "basic_qa", unit_id = "unit-ocr-human", variant_group_id = "variant-ocr-human" } },
            withheld_units = Array.Empty<object>(), release_state = "ready", gates = Array.Empty<object>()
        }, jsonOptions);
        outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "card-bundle", "card-bundle", "cards.json", cards, "application/json", jsonOptions));
        contentModuleReleaseState = "ready";
        }
        else if (contentInputVersion == "standard-input/4.0.0")
        {
        using var ocrInput = await ReadContentInputAsync(request, attemptDirectory);
        var ocrInputItem = ocrInput.RootElement.GetProperty("items")[0];
        var ocrItemId = ocrInputItem.GetProperty("item_id").GetString()!;
        var ocrSource = ocrInputItem.GetProperty("source");
        var ocrMaterialPath = ocrSource.GetProperty("material_path").GetString()!;
        var ocrProvenance = ocrSource.GetProperty("provenance").Clone();
        contentModuleReleaseState = materialText.Contains("CANAR", StringComparison.Ordinal) ? "unresolved" : "ready";
        var textSha = Sha256(Encoding.UTF8.GetBytes(materialText));
        object ProofreadSegment(string text) => new { segment_index = 0, segment_id = "proofread-ocr", source_kind = "ocr_text", source_path = ocrMaterialPath, start = 0, end = materialText.Length, text };
        var proofreadSegments = new List<object> { ProofreadSegment(materialText) };
        if (materialText.Contains("OCR_PROOFREAD_SEGMENT_DUPLICATE", StringComparison.Ordinal))
            proofreadSegments.Add(ProofreadSegment(materialText));
        object[] learningProofreadSegments = materialText.Contains("OCR_PROOFREAD_SEGMENT_MISSING", StringComparison.Ordinal)
            ? Array.Empty<object>()
            : materialText.Contains("OCR_PROOFREAD_SEGMENT_TAMPER", StringComparison.Ordinal)
                ? new[] { ProofreadSegment(materialText + " tampered") }
                : proofreadSegments.ToArray();
        var proofread = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "ocr-proofread-bundle/1.0.0", bundle_id = "ocr-proofread-fake", package_id = "package-fake",
            task_id = request.GetProperty("taskId").GetString(), input_version = "4.0.0", attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow,
            items = new[]
            {
                new
                {
                    item_id = ocrItemId, state = contentModuleReleaseState,
                    provenance = ocrProvenance, source_ref = new { path = ocrMaterialPath, sha256 = textSha },
                    original_text = materialText, original_text_sha256 = textSha, proofread_text = materialText, proofread_text_sha256 = textSha,
                    source_segments = new[] { new { segment_index = 0, segment_id = "segment-ocr", source_kind = "ocr_text", source_path = ocrMaterialPath, start = 0, end = materialText.Length, text = materialText } },
                    proofread_segments = proofreadSegments.ToArray(), corrections = Array.Empty<object>(),
                    unresolved = contentModuleReleaseState == "unresolved" ? new object[] { new { segment_index = 0, start = materialText.IndexOf("CANAR", StringComparison.Ordinal), end = materialText.IndexOf("CANAR", StringComparison.Ordinal) + 5, exact_text = "CANAR", reason = "insufficient_context", evidence = "CANAR has multiple plausible expansions." } } : Array.Empty<object>(),
                    gates = Array.Empty<object>()
                }
            },
            release_state = contentModuleReleaseState, ai_trace_keys = Array.Empty<string>(), gates = Array.Empty<object>()
        }, jsonOptions);
        var proofreadOutput = await PublishArtifactAsync(request, attemptDirectory, workerId, "ocr-proofread-bundle", "ocr-proofread-bundle", "ocr-proofread.json", proofread, "application/json", jsonOptions);
        outputs.Add(proofreadOutput);
        var proofreadReference = JsonSerializer.SerializeToElement(proofreadOutput, jsonOptions);
        var proofreadPath = request.GetProperty("attemptPath").GetString() + "/outputs/ocr-proofread-bundle/v0001/ocr-proofread.json";
        var learning = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "learning-bundle/2.0.0", bundle_id = "learning-ocr-fake", package_id = "package-fake",
            task_id = request.GetProperty("taskId").GetString(), input_version = "4.0.0", attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow,
            input = new { schema_version = "standard-input/4.0.0" }, ocr_proofread_bundle_path = proofreadPath, ocr_proofread_bundle_sha256 = Sha256(proofread),
            worker = new { name = "fake", version = "1.0.0" },
            items = new[]
            {
                new
                {
                    item_id = ocrItemId, state = contentModuleReleaseState,
                    cleaning = new { cleaned_text = materialText, format_anomalies = Array.Empty<string>(), removals = Array.Empty<object>(), state = contentModuleReleaseState },
                    proofreading = new { state = contentModuleReleaseState, proofread_text = materialText, proofread_text_sha256 = textSha,
                        proofread_segments = learningProofreadSegments,
                        corrections = Array.Empty<object>(), unresolved = contentModuleReleaseState == "unresolved" ? new object[] { new { segment_index = 0, start = materialText.IndexOf("CANAR", StringComparison.Ordinal), end = materialText.IndexOf("CANAR", StringComparison.Ordinal) + 5, exact_text = "CANAR", reason = "insufficient_context" } } : Array.Empty<object>() },
                    units = new[] { new { unit_id = "unit-ocr", source_segment_ids = new[] { "proofread-ocr" }, category_id = "question_answer", subtype = "basic_qa", answer_state = "missing", context_state = "provided", risk_state = "ordinary", generated_content = new[] { new { kind = "answer", text = "Fake OCR answer" } }, state = contentModuleReleaseState, unresolved_reasons = contentModuleReleaseState == "unresolved" ? new[] { "ambiguous_ocr_token" } : Array.Empty<string>(), gates = Array.Empty<object>() } },
                    gates = Array.Empty<object>(), source_kind = "ocr_text", source_refs = new[] { new { path = ocrMaterialPath, sha256 = textSha } },
                    source_segments = new[] { new { segment_index = 0, segment_id = "segment-ocr", source_kind = "ocr_text", source_path = ocrMaterialPath, start = 0, end = materialText.Length, text = materialText } },
                    unresolved_reasons = contentModuleReleaseState == "unresolved" ? new[] { "ambiguous_ocr_token" } : Array.Empty<string>()
                }
            },
            ai_trace = Array.Empty<object>(), release_state = contentModuleReleaseState,
            unresolved_items = contentModuleReleaseState == "unresolved" ? new object[] { new { item_id = "ocr-0001", reason = "ambiguous_ocr_token" } } : Array.Empty<object>(),
            gates = Array.Empty<object>()
        }, jsonOptions);
        outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "learning-bundle", "learning-bundle", "learning.json", learning, "application/json", jsonOptions, proofreadReference, "2.0.0"));
        if (contentModuleReleaseState == "ready")
        {
            var cards = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = "card-bundle/1.0.0", bundle_id = "cards-ocr-fake", package_id = "package-fake", task_id = request.GetProperty("taskId").GetString(),
                attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow,
                learning_bundle_path = "learning.json", learning_bundle_sha256 = Sha256(learning),
                cards = new[] { new { answer_provenance = "generated", back_blocks = new[] { new { block_id = "back-ocr", provenance = "generated", text = "Fake OCR answer", type = "text" } }, card_id = "card-ocr", category_id = "question_answer", front_blocks = new[] { new { block_id = "front-ocr", provenance = "source", text = materialText, type = "text" } }, item_id = "ocr-0001", source_segment_ids = new[] { "proofread-ocr" }, subtype = "basic_qa", template = "basic_qa", unit_id = "unit-ocr", variant_group_id = "variant-ocr" } },
                withheld_units = Array.Empty<object>(), release_state = "ready", gates = Array.Empty<object>()
            }, jsonOptions);
            outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "card-bundle", "card-bundle", "cards.json", cards, "application/json", jsonOptions));
        }
        }
        else
        {
        var learning = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "learning-bundle/1.0.0", bundle_id = "learning-fake", package_id = "package-fake", task_id = request.GetProperty("taskId").GetString(),
            input_version = "1", attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow,
            input = new { }, worker = new { name = "fake", version = "1.0.0" },
            items = new[] { new { item_id = "item-fake", state = "ready", cleaning = new { cleaned_text = cleanedText, format_anomalies = Array.Empty<string>(), removals = Array.Empty<object>(), state = "ready" },
                units = new[] { new { unit_id = "unit-fake", source_segment_ids = new[] { "segment-fake" }, category_id = "question_answer", subtype = "basic_qa", answer_state = "missing", context_state = "provided", risk_state = "ordinary", generated_content = new[] { new { kind = "answer", text = "Fake answer" } }, state = "ready", unresolved_reasons = Array.Empty<string>(), gates = Array.Empty<object>() } },
                gates = Array.Empty<object>(), source_kind = "text", source_refs = Array.Empty<object>(), source_segments = new[] { new { segment_index = 0, segment_id = "segment-fake", source_kind = "text", source_path = "material.md", start = 0, end = frontText.Length, text = frontText } }, unresolved_reasons = Array.Empty<string>() } },
            ai_trace = Array.Empty<object>(), release_state = "ready", unresolved_items = Array.Empty<object>(), gates = Array.Empty<object>()
        }, jsonOptions);
        var cards = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "card-bundle/1.0.0", bundle_id = "cards-fake", package_id = "package-fake", task_id = request.GetProperty("taskId").GetString(),
            attempt_id = request.GetProperty("attemptId").GetString(), created_at = DateTimeOffset.UtcNow,
            learning_bundle_path = "learning.json", learning_bundle_sha256 = Sha256(learning),
            cards = new[] { new { answer_provenance = "generated", back_blocks = new[] { new { block_id = "back-fake", provenance = "generated", text = "Fake answer", type = "text" } }, card_id = "card-fake", category_id = "question_answer", front_blocks = new[] { new { block_id = "front-fake", provenance = "source", text = frontText, type = "text" } }, item_id = "item-fake", source_segment_ids = new[] { "segment-fake" }, subtype = "basic_qa", template = "basic_qa", unit_id = "unit-fake", variant_group_id = "variant-fake" } },
            withheld_units = Array.Empty<object>(), release_state = "ready", gates = Array.Empty<object>()
        }, jsonOptions);
        outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "learning-bundle", "learning-bundle", "learning.json", learning, "application/json", jsonOptions));
        outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "card-bundle", "card-bundle", "cards.json", cards, "application/json", jsonOptions));
        }
        }
        }
    }
    else if (workerId == "speech.edge" && request.GetProperty("operation").GetString() == "speech.synthesize")
        outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "audio-speech", "audio.speech", "speech.mp3", new byte[] { 0x49, 0x44, 0x33, 1 }, "audio/mpeg", jsonOptions));
    else if (workerId == "export.remnote")
        outputs.Add(await PublishArtifactAsync(request, attemptDirectory, workerId, "remnote-write-receipt", "remnote.write-receipt", "receipt.json", Encoding.UTF8.GetBytes("{\"outcome\":\"fake\"}"), "application/json", jsonOptions));
    else if (workerId == "export.anki")
    {
        var isProbe = request.GetProperty("operation").GetString() == "probe";
        outputs.Add(isProbe
            ? await PublishMultiFileAnkiProbeArtifactAsync(request, attemptDirectory, workerId, jsonOptions)
            : await PublishArtifactAsync(request, attemptDirectory, workerId,
                "anki-export-evidence", "anki-export-evidence", "receipt.json",
                Encoding.UTF8.GetBytes("{\"outcome\":\"fake\"}"), "application/json", jsonOptions));
    }

    if (forcedStatus == "missing-output") outputs.Clear();

    diagnostics ??= status == "partial" ? new object[]
    {
        new { itemId = "item-ok", status = "completed", outputs = outputs.ToArray(), error = (object?)null },
        new { itemId = "item-failed", status = "failed", outputs = Array.Empty<object>(), error = new { code = "fake.item_failed", category = "worker", retryable = true, detailsRef = request.GetProperty("attemptPath").GetString() + "/request.json" } }
    } : null;
    var error = status == "partial" ? new { code = "fake.partial", category = "worker", retryable = true, detailsRef = request.GetProperty("attemptPath").GetString() + "/request.json" } : null;
    object resultPayload = workerId == "speech.edge"
        ? new
        {
            operationId = request.GetProperty("operationId").GetString(),
            languageTag = "zh-CN",
            voiceId = "zh-CN-XiaoxiaoNeural",
            cacheHit = false,
            playbackState = "idle_replayable"
        }
        : workerId == "content.deepseek" && contentModuleReleaseState is not null
        ? new { mode = "pipeline", moduleReleaseState = contentModuleReleaseState }
        : new { mode = "pipeline" };
    await WriteOnceAsync(resultPath, CreateResult(request, workerId, status, outputs.ToArray(), error, resultPayload, diagnostics), jsonOptions);
    return 0;
}

if (mode == "grace-result")
{
    while (!File.Exists(cancelPath)) await Task.Delay(25);
    using var cancelDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cancelPath));
    var reason = cancelDocument.RootElement.GetProperty("reason").GetString()!;
    var status = reason == "timeout" ? "failed" : "cancelled";
    var category = reason == "timeout" ? "transient" : "cancelled";
    var error = new { code = $"cancel.{reason}", category, retryable = reason == "timeout", detailsRef = request.GetProperty("cancelSignal").GetProperty("path").GetString() };
    await WriteOnceAsync(resultPath, CreateResult(request, workerId, status, Array.Empty<object>(), error, new { terminalDetail = reason }, null), jsonOptions);
    return 0;
}
if (mode == "nonzero-exit") { Console.Error.WriteLine("injected worker failure"); return 17; }
if (mode == "external-crash") { Console.Error.WriteLine("injected crash after external side effect may have started"); return 23; }
if (mode == "invalid-json-result") { await File.WriteAllTextAsync(resultPath, "{not-json}"); return 0; }
if (mode == "partial-write-crash") { await File.WriteAllTextAsync(resultPath, "{\"schemaVersion\":\"1.0.0\","); return 19; }
await Task.Delay(TimeSpan.FromSeconds(30));
return 3;

static object Description(string workerId) => workerId switch
{
    "image.ocr.mineru" => Describe(workerId, new[] { "image-ocr" }, new[] { "capture-bundle" }, new[] { "image.original", "image.original.bundle", "image.processing.provenance", "image-ocr.markdown", "image-ocr.structured", "image-ocr.image" }, "parallel", 4, "external", "reconcilable"),
    "content.deepseek" => Describe(workerId, new[] { "content.understand-and-card" }, new[] { "content-input" }, new[] { "learning-bundle", "card-bundle", "language-practice-decision", "question.answer", "ocr-proofread-bundle" }, "parallel", 4, "local", "idempotent"),
    "speech.edge" => Describe(workerId, new[] { "speech.synthesize", "speech.playback" }, new[] { "text.spoken", "audio.speech" }, new[] { "audio.speech" }, "serial", 1, "local", "idempotent"),
    "export.remnote" => Describe(workerId, new[] { "export-remnote" }, new[] { "learning.bundle", "card.bundle" }, new[] { "remnote.write-receipt", "remnote.import-package" }, "serial", 1, "external", "reconcilable"),
    "export.anki" => Describe(workerId, new[] { "anki-export" }, new[] { "card-bundle", "learning-bundle", "anki-export-config" }, new[] { "anki-export-evidence", "anki-connection-status", "anki-managed-initialization-status" }, "serial", 1, "external", "reconcilable"),
    _ => Describe("test.fake", new[] { "test.grace" }, Array.Empty<string>(), Array.Empty<string>(), "parallel", 8, "local", "idempotent")
};

static object Describe(string workerId, string[] stages, string[] inputKinds, string[] outputKinds, string model, int maxParallelAttempts, string sideEffectLevel, string idempotencyLevel) => new
{
    schemaVersion = "1.0.0", workerId, workerVersion = "1.0.0", contractVersion = "1.0.0", stages, inputKinds, outputKinds,
    concurrency = new { model, maxParallelAttempts }, sideEffectLevel, idempotencyLevel,
    cancellation = new { protocol = "attempt-signal-file-v1", supportedReasons = new[] { "user_stop", "superseded", "task_cancel", "timeout" } }
};

static async Task<object> PublishArtifactAsync(JsonElement request, string attemptDirectory, string workerId, string artifactId, string kind, string fileName, byte[] payload, string mediaType, JsonSerializerOptions options, JsonElement? additionalInputRef = null, string contractVersion = "1.0.0")
{
    var versionDirectory = Path.Combine(attemptDirectory, "outputs", artifactId, "v0001");
    Directory.CreateDirectory(versionDirectory);
    var payloadPath = Path.Combine(versionDirectory, fileName);
    await File.WriteAllBytesAsync(payloadPath, payload);
    var jobRoot = attemptDirectory;
    foreach (var _ in request.GetProperty("attemptPath").GetString()!.Split('/')) jobRoot = Path.GetDirectoryName(jobRoot)!;
    var payloadRelative = Path.GetRelativePath(jobRoot, payloadPath).Replace('\\', '/');
    var manifestPath = Path.Combine(versionDirectory, "artifact.json");
    var manifest = new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion, mediaType,
        files = new[] { new { path = payloadRelative, sha256 = Sha256(payload), bytes = payload.LongLength, mediaType } },
        createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId = request.GetProperty("stageId").GetString(), workerId, workerVersion = "1.0.0", attemptId = request.GetProperty("attemptId").GetString() },
        inputRefs = request.GetProperty("inputRefs").EnumerateArray().Select(item => item.Clone())
            .Concat(additionalInputRef is JsonElement additional ? new[] { additional.Clone() } : Array.Empty<JsonElement>()).ToArray(),
        dataClass = "sensitive", metadata = new { entrypoint = payloadRelative }
    };
    await WriteOnceAsync(manifestPath, manifest, options);
    var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
    return new { artifactRef = artifactId, kind, contractVersion, manifestPath = Path.GetRelativePath(jobRoot, manifestPath).Replace('\\', '/'), expectedSha256 = Sha256(manifestBytes) };
}

static async Task<object> PublishMultiFileAnkiProbeArtifactAsync(JsonElement request, string attemptDirectory, string workerId, JsonSerializerOptions options)
{
    const string artifactId = "anki-connection-status";
    var versionDirectory = Path.Combine(attemptDirectory, "outputs", artifactId, "v0001");
    Directory.CreateDirectory(versionDirectory);
    var payloads = new[]
    {
        (Name: "diagnostics.json", Bytes: Encoding.UTF8.GetBytes("{\"sanitized\":true}")),
        (Name: "connection-status.json", Bytes: Encoding.UTF8.GetBytes("{\"schemaVersion\":\"anki-connection-status.v1\",\"operation\":\"probe\",\"status\":\"ready\",\"externalMutation\":false}")),
        (Name: "provider-actions.json", Bytes: Encoding.UTF8.GetBytes("{\"readOnlyOperations\":18,\"mutatingActionsObserved\":[]}")),
        (Name: "readback.json", Bytes: Encoding.UTF8.GetBytes("{\"networkScope\":\"loopback-only\",\"externalMutation\":false}"))
    };
    var jobRoot = attemptDirectory;
    foreach (var _ in request.GetProperty("attemptPath").GetString()!.Split('/')) jobRoot = Path.GetDirectoryName(jobRoot)!;
    var files = new List<object>();
    string? entrypoint = null;
    foreach (var payload in payloads)
    {
        var path = Path.Combine(versionDirectory, payload.Name);
        await File.WriteAllBytesAsync(path, payload.Bytes);
        var relative = Path.GetRelativePath(jobRoot, path).Replace('\\', '/');
        if (payload.Name == "connection-status.json") entrypoint = relative;
        files.Add(new { path = relative, sha256 = Sha256(payload.Bytes), bytes = payload.Bytes.LongLength, mediaType = "application/json" });
    }
    var manifestPath = Path.Combine(versionDirectory, "artifact.json");
    var manifest = new
    {
        schemaVersion = "1.0.0", artifactId, kind = artifactId, contractVersion = "1.0.0", mediaType = "application/json", files,
        createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId = request.GetProperty("stageId").GetString(), workerId, workerVersion = "1.0.0", attemptId = request.GetProperty("attemptId").GetString() },
        inputRefs = request.GetProperty("inputRefs").EnumerateArray().Select(item => item.Clone()).ToArray(),
        dataClass = "sensitive", metadata = new { entrypoint }
    };
    await WriteOnceAsync(manifestPath, manifest, options);
    var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
    return new { artifactRef = artifactId, kind = artifactId, contractVersion = "1.0.0", manifestPath = Path.GetRelativePath(jobRoot, manifestPath).Replace('\\', '/'), expectedSha256 = Sha256(manifestBytes) };
}

static async Task WriteOnceAsync(string path, object value, JsonSerializerOptions options)
{
    var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, options));
    File.Move(temporary, path, false);
}

static object CreateResult(JsonElement request, string workerId, string status, object[] outputs, object? error, object? payload, object[]? itemDiagnostics, string workerVersion = "1.0.0")
{
    var result = new Dictionary<string, object?>
    {
        ["schemaVersion"] = "1.0.0",
        ["requestId"] = request.GetProperty("requestId").GetString(),
        ["operationId"] = request.GetProperty("operationId").GetString(),
        ["taskId"] = request.GetProperty("taskId").GetString(),
        ["runId"] = request.GetProperty("runId").GetString(),
        ["stageId"] = request.GetProperty("stageId").GetString(),
        ["attemptId"] = request.GetProperty("attemptId").GetString(),
        ["worker"] = new { id = workerId, version = workerVersion },
        ["contractVersion"] = "1.0.0",
        ["idempotencyKey"] = request.GetProperty("idempotencyKey").GetString(),
        ["status"] = status,
        ["outputs"] = outputs,
        ["error"] = error,
        ["startedAt"] = DateTimeOffset.UtcNow.AddMilliseconds(-25),
        ["finishedAt"] = DateTimeOffset.UtcNow,
        ["warnings"] = Array.Empty<string>()
    };
    if (itemDiagnostics is not null)
        result["itemDiagnostics"] = itemDiagnostics;
    if (payload is not null)
        result["payload"] = payload;
    return result;
}

static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static bool PlaybackConsumesCurrentRunAudio(JsonElement request)
{
    var runPrefix = $"runs/{request.GetProperty("runId").GetString()}/";
    return request.GetProperty("inputRefs").EnumerateArray().Any(item =>
        item.GetProperty("kind").GetString() == "audio.speech" &&
        item.GetProperty("manifestPath").GetString()!.Replace('\\', '/').StartsWith(runPrefix, StringComparison.Ordinal));
}

static async Task<List<string>> ReadCaptureImageIdsAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = attemptDirectory;
    foreach (var _ in request.GetProperty("attemptPath").GetString()!.Split('/')) jobRoot = Path.GetDirectoryName(jobRoot)!;
    var captureRef = request.GetProperty("inputRefs").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "capture-bundle");
    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, captureRef.GetProperty("manifestPath").GetString()!.Replace('/', Path.DirectorySeparatorChar))));
    var entrypoint = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar))));
    return bundle.RootElement.GetProperty("questionImages").EnumerateArray().Concat(bundle.RootElement.GetProperty("answerImages").EnumerateArray()).Select(item => item.GetString()!).ToList();
}

static async Task<string> ReadCaptureImageHandlingAsync(JsonElement request, string attemptDirectory)
{
    using var bundle = await ReadCaptureBundleAsync(request, attemptDirectory);
    return bundle.RootElement.GetProperty("modeSnapshot").GetProperty("imageHandling").GetString()!;
}

static async Task<(byte[] Bytes, string MediaType)> ReadCaptureImageAsync(JsonElement request, string attemptDirectory, string itemId)
{
    var jobRoot = ResolveJobRoot(request, attemptDirectory);
    using var bundle = await ReadCaptureBundleAsync(request, attemptDirectory);
    var artifact = bundle.RootElement.GetProperty("artifacts").EnumerateArray()
        .Single(item => item.GetProperty("artifactRef").GetString() == itemId);
    var bytes = await File.ReadAllBytesAsync(Path.Combine(jobRoot, artifact.GetProperty("relativePath").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
    return (bytes, artifact.GetProperty("mediaType").GetString()!);
}

static async Task<JsonDocument> ReadCaptureBundleAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = ResolveJobRoot(request, attemptDirectory);
    var captureRef = request.GetProperty("inputRefs").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "capture-bundle");
    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, captureRef.GetProperty("manifestPath").GetString()!.Replace('/', Path.DirectorySeparatorChar))));
    var entrypoint = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar))));
}

static async Task<Dictionary<string, JsonElement>> ReadPriorCompletedDiagnosticsAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = ResolveJobRoot(request, attemptDirectory);
    var relative = request.GetProperty("payload").GetProperty("retryModuleResultPath").GetString()!;
    using var prior = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, relative.Replace('/', Path.DirectorySeparatorChar))));
    return prior.RootElement.GetProperty("itemDiagnostics").EnumerateArray()
        .Where(item => item.GetProperty("status").GetString() == "completed")
        .ToDictionary(item => item.GetProperty("itemId").GetString()!, item => item.Clone(), StringComparer.Ordinal);
}

static string ResolveJobRoot(JsonElement request, string attemptDirectory)
{
    var jobRoot = attemptDirectory;
    foreach (var _ in request.GetProperty("attemptPath").GetString()!.Split('/')) jobRoot = Path.GetDirectoryName(jobRoot)!;
    return jobRoot;
}

static async Task<string> ReadContentMaterialAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = attemptDirectory;
    foreach (var _ in request.GetProperty("attemptPath").GetString()!.Split('/')) jobRoot = Path.GetDirectoryName(jobRoot)!;
    var inputPath = request.GetProperty("payload").GetProperty("contentInputPath").GetString()!;
    using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, inputPath.Replace('/', Path.DirectorySeparatorChar))));
    var source = input.RootElement.GetProperty("items")[0].GetProperty("source");
    if (source.GetProperty("kind").GetString() == "question_workflow")
        return input.RootElement.GetProperty("questionWorkflow").GetProperty("questionTurns").EnumerateArray().Last().GetProperty("question").GetString()!;
    var material = source.GetProperty("material_path").GetString()!;
    return await File.ReadAllTextAsync(Path.Combine(jobRoot, material.Replace('/', Path.DirectorySeparatorChar)));
}

static async Task<string> ReadContentInputSchemaVersionAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = ResolveJobRoot(request, attemptDirectory);
    var inputPath = request.GetProperty("payload").GetProperty("contentInputPath").GetString()!;
    using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, inputPath.Replace('/', Path.DirectorySeparatorChar))));
    return input.RootElement.GetProperty("schema_version").GetString()!;
}

static async Task<JsonDocument> ReadContentInputAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = ResolveJobRoot(request, attemptDirectory);
    var inputPath = request.GetProperty("payload").GetProperty("contentInputPath").GetString()!;
    return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, inputPath.Replace('/', Path.DirectorySeparatorChar))));
}

static async Task<JsonDocument> ReadQuestionInputAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = attemptDirectory;
    foreach (var _ in request.GetProperty("attemptPath").GetString()!.Split('/')) jobRoot = Path.GetDirectoryName(jobRoot)!;
    var inputPath = request.GetProperty("payload").GetProperty("contentInputPath").GetString()!;
    var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, inputPath.Replace('/', Path.DirectorySeparatorChar))));
    if (document.RootElement.GetProperty("schema_version").GetString() != "standard-input/3.0.0")
    {
        document.Dispose();
        throw new InvalidDataException("Fake question operation requires standard-input/3.0.0.");
    }
    return document;
}

static async Task<bool> ContentInputHasLanguageChoiceAsync(JsonElement request, string attemptDirectory)
{
    var jobRoot = attemptDirectory;
    foreach (var _ in request.GetProperty("attemptPath").GetString()!.Split('/')) jobRoot = Path.GetDirectoryName(jobRoot)!;
    var inputPath = request.GetProperty("payload").GetProperty("contentInputPath").GetString()!;
    using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, inputPath.Replace('/', Path.DirectorySeparatorChar))));
    return input.RootElement.TryGetProperty("schema_version", out var version) && version.GetString() == "standard-input/2.0.0" &&
        input.RootElement.TryGetProperty("human_overrides", out var overrides) &&
        overrides.TryGetProperty("languagePracticeMode", out var choice) &&
        choice.GetString() is "meaning-reading" or "writing" or "both";
}
