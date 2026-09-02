using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed record ModuleActionRequest(
    string SchemaVersion,
    string ActionId,
    string Operation,
    string PayloadRef,
    string IdempotencyKey);

public sealed record ModuleActionReceipt(
    string SchemaVersion,
    string ActionId,
    string Operation,
    bool Accepted,
    string Status,
    string InputRef,
    string InputSha256,
    string? OutputRef,
    string? OutputSha256,
    string? CatalogSnapshotRef,
    string? CatalogSnapshotSha256,
    int ExternalWriteCount,
    bool PhysicalAudio,
    bool AutomaticRetryAllowed,
    string IdempotencyKey,
    DateTimeOffset CompletedAt,
    WorkerError? Error,
    string? ExecutionStatus = null,
    string? TaskId = null,
    string? RunId = null,
    string? ArtifactRootRef = null,
    IReadOnlyList<ModuleActionOutputReference>? OutputRefs = null);

public sealed record ModuleActionOutputReference(
    string ReferenceType,
    string Kind,
    string ContractVersion,
    string RootRef,
    string Path,
    string ExpectedSha256);

public sealed class ModuleActionHandler
{
    private readonly string jobsRoot;
    private readonly string releaseRoot;
    private readonly WorkerRegistry registry;

    public ModuleActionHandler(string jobsRoot, string registryPath, string releaseRoot)
    {
        this.jobsRoot = Path.GetFullPath(jobsRoot);
        this.releaseRoot = Path.GetFullPath(releaseRoot);
        registry = new RegistryLoader().Load(Path.GetFullPath(registryPath));
    }

    public async Task<ModuleActionReceipt> HandleAsync(ModuleActionRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var actionRoot = PathPolicy.ResolveJobRelative(jobsRoot, $"module-actions/{request.ActionId}");
        Directory.CreateDirectory(actionRoot);
        var expectedPayload = $"module-actions/{request.ActionId}/input.json";
        if (!request.PayloadRef.Equals(expectedPayload, StringComparison.Ordinal))
            throw new InvalidDataException("module-action payloadRef must be the canonical action-local input.json path.");
        var inputPath = PathPolicy.ResolveJobRelative(jobsRoot, request.PayloadRef);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("module-action input is missing.", inputPath);
        var inputSha = Integrity.Sha256Hex(await File.ReadAllBytesAsync(inputPath, cancellationToken)).ToLowerInvariant();
        var receiptPath = Path.Combine(actionRoot, "receipt.json");
        if (File.Exists(receiptPath))
        {
            var prior = JsonSerializer.Deserialize<ModuleActionReceipt>(await File.ReadAllTextAsync(receiptPath, cancellationToken), ContractJson.Options)
                ?? throw new InvalidDataException("Existing module-action receipt is invalid.");
            if (prior.IdempotencyKey != request.IdempotencyKey || prior.InputSha256 != inputSha || prior.Operation != request.Operation)
                throw new InvalidDataException("module-action replay identity drifted.");
            return prior;
        }

        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(inputPath, cancellationToken));
        var resolution = await ValidateInputAndResolveAsync(request, input.RootElement, inputPath, actionRoot, cancellationToken);
        if (resolution.AnkiProbe is not null)
            return await RunAnkiProbeAsync(request, inputSha, receiptPath, resolution.AnkiProbe, cancellationToken);
        var (workerId, arguments, physicalAudio) = (resolution.WorkerId, resolution.Arguments, resolution.PhysicalAudio);
        var worker = registry.Workers.SingleOrDefault(item => item.WorkerId == workerId && item.EnabledByDefault)
            ?? throw new InvalidDataException($"Admitted module is missing: {workerId}");
        var executable = PathPolicy.ResolveJobRelative(releaseRoot, worker.Entrypoint.Path);
        if (!File.Exists(executable)) throw new FileNotFoundException("Admitted module entrypoint is missing.", executable);

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in worker.Entrypoint.Args) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Failed to start admitted module action.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Operation is "remnote.probe" or "remnote.discover-target" ? 75 : 60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(true);
            return await PublishFailureAsync(request, inputSha, receiptPath, physicalAudio, "module.action_timeout", resolution.AnkiInitialization is not null || physicalAudio || resolution.ContentSettings?.LocalMutation == true, cancellationToken);
        }
        var stdout = await stdoutTask;
        _ = await stderrTask; // Never persist raw stderr: module output may contain deployment-local details.
        JsonElement output;
        if (resolution.AnkiInitialization is not null)
        {
            var workerResult = await ReadAnkiWorkerResultAsync(resolution.AnkiInitialization, cancellationToken);
            if (workerResult is null)
                return await PublishFailureAsync(request, inputSha, receiptPath, false, "anki.initialize_managed_result_missing", true, cancellationToken);
            var workerStatus = workerResult.Value.GetProperty("status").GetString();
            if (workerStatus != "completed")
            {
                var unknown = workerStatus == "unknown_external_state" ||
                    workerResult.Value.TryGetProperty("sideEffects", out var sideEffects) &&
                    sideEffects.ValueKind == JsonValueKind.Array &&
                    sideEffects.EnumerateArray().Any(item => item.TryGetProperty("state", out var state) && state.GetString() == "unknown");
                return await PublishFailureAsync(request, inputSha, receiptPath, false,
                    unknown ? "anki.initialize_managed_unknown_external_state" : "anki.initialize_managed_failed", unknown, cancellationToken);
            }
            if (process.ExitCode != 0)
                return await PublishFailureAsync(request, inputSha, receiptPath, false, "anki.initialize_managed_process_failed", true, cancellationToken);
            output = await ReadAndValidateAnkiInitializationOutputAsync(resolution.AnkiInitialization, workerResult.Value, cancellationToken);
        }
        else
        {
        try
        {
            using var outputDocument = JsonDocument.Parse(stdout);
            output = outputDocument.RootElement.Clone();
            ValidateOutputSafely(request.Operation, output, resolution.ContentSettings, request.SchemaVersion == "knowledge-clipper-module-action.v6");
        }
        catch (Exception exception) when (process.ExitCode != 0 && exception is JsonException or InvalidDataException)
        {
            return await PublishFailureAsync(request, inputSha, receiptPath, physicalAudio, "module.action_failed", physicalAudio || resolution.ContentSettings?.LocalMutation == true, cancellationToken);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Admitted module action did not return one JSON result.");
        }
        if (process.ExitCode != 0 && !IsAdmittedTypedNonzeroResult(request.Operation, output))
            return await PublishFailureAsync(request, inputSha, receiptPath, physicalAudio, "module.action_failed", physicalAudio || resolution.ContentSettings?.LocalMutation == true, cancellationToken);
        }
        var outputPath = Path.Combine(actionRoot, "result.json");
        await AtomicFile.WriteJsonOnceAsync(outputPath, output, cancellationToken);
        var outputRef = PathPolicy.ToJobRelative(jobsRoot, outputPath);
        var outputSha = Integrity.Sha256Hex(await File.ReadAllBytesAsync(outputPath, cancellationToken)).ToLowerInvariant();
        string? catalogRef = null;
        string? catalogSha = null;
        if (request.Operation == "speech.voices")
        {
            catalogSha = output.GetProperty("catalogSha256").GetString()!;
            var catalogPath = PathPolicy.ResolveJobRelative(jobsRoot, $"module-state/speech/voice-catalog/{catalogSha}.json");
            if (!File.Exists(catalogPath)) await AtomicFile.WriteJsonOnceAsync(catalogPath, output, cancellationToken);
            catalogRef = PathPolicy.ToJobRelative(jobsRoot, catalogPath);
        }
        var externalWrites = request.Operation == "anki.initialize-managed"
            ? output.GetProperty("writes").GetProperty("modelsCreated").GetInt32() + output.GetProperty("writes").GetProperty("decksCreated").GetInt32()
            : request.Operation is "remnote.probe" or "remnote.discover-target" ? output.GetProperty("externalWriteCount").GetInt32() : 0;
        var actionStatus = request.SchemaVersion == "knowledge-clipper-module-action.v6" && request.Operation == "remnote.probe"
            ? output.GetProperty("admission").GetString()!
            : request.Operation == "remnote.discover-target"
            ? output.GetProperty("admission").GetString()!
            : output.TryGetProperty("status", out var status) ? status.GetString()! : "completed";
        IReadOnlyList<ModuleActionOutputReference>? outputRefs = null;
        if (resolution.ContentSettings is not null)
        {
            var (kind, contractVersion) = ContentSettingsOutputIdentity(request.Operation);
            outputRefs = new[] { new ModuleActionOutputReference("file", kind, contractVersion, "jobs-root", outputRef, outputSha) };
        }
        else if (request.SchemaVersion == "knowledge-clipper-module-action.v6")
        {
            var (kind, contractVersion) = TypedPublicOutputIdentity(request.Operation);
            outputRefs = new[] { new ModuleActionOutputReference("file", kind, contractVersion, "jobs-root", outputRef, outputSha) };
        }
        var receipt = new ModuleActionReceipt(
            ReceiptSchemaVersion(request), request.ActionId, request.Operation, true,
            actionStatus,
            request.PayloadRef, inputSha, outputRef, outputSha, catalogRef, catalogSha,
            externalWrites, physicalAudio, false, request.IdempotencyKey, DateTimeOffset.UtcNow, null,
            resolution.ContentSettings is not null || request.SchemaVersion == "knowledge-clipper-module-action.v6" ? "completed" : null,
            null, null, null, outputRefs);
        await AtomicFile.WriteJsonOnceAsync(receiptPath, receipt, cancellationToken);
        return receipt;
    }

    private static void ValidateRequest(ModuleActionRequest request)
    {
        var v1 = request.SchemaVersion == "knowledge-clipper-module-action.v1" &&
                 (request.Operation is "speech.voices" or "speech.preview" or "remnote.probe");
        var v2 = request.SchemaVersion == "knowledge-clipper-module-action.v2" &&
                 (request.Operation is "speech.voices" or "speech.preview" or "remnote.probe" or "remnote.discover-target");
        var v3 = request.SchemaVersion == "knowledge-clipper-module-action.v3" &&
                 (request.Operation is "speech.voices" or "speech.preview" or "remnote.probe" or "remnote.discover-target" or "anki.initialize-managed");
        var v4 = request.SchemaVersion == "knowledge-clipper-module-action.v4" &&
                 request.Operation == "anki.probe";
        var v5 = request.SchemaVersion == "knowledge-clipper-module-action.v5" &&
                 request.Operation is "content.settings.describe" or "content.settings.copy" or "content.settings.validate" or "content.settings.diff";
        var v6 = request.SchemaVersion == "knowledge-clipper-module-action.v6" &&
                 request.Operation is "remnote.probe" or "speech.voices" or "speech.preview";
        if ((!v1 && !v2 && !v3 && !v4 && !v5 && !v6) ||
            request.ActionId.Length is < 1 or > 128 || request.ActionId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')) ||
            request.IdempotencyKey.Length != 64 || request.IdempotencyKey.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("module-action request identity is invalid.");
    }

    private async Task<ModuleActionResolution> ValidateInputAndResolveAsync(
        ModuleActionRequest request,
        JsonElement input,
        string inputPath,
        string actionRoot,
        CancellationToken cancellationToken)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new InvalidDataException("module-action input must be an object.");
        var schema = input.TryGetProperty("schemaVersion", out var value) ? value.GetString() : null;
        if (request.Operation == "anki.initialize-managed" && schema == "anki-managed-initialization-action.v1")
        {
            var context = await ValidateAnkiInitializationPackageAsync(request, input, actionRoot, cancellationToken);
            return new ModuleActionResolution("export.anki", new[] { "run", "--request", context.RequestPath }, false, context, null);
        }
        if (request.Operation == "anki.probe" && schema == "anki-readonly-probe-action.v1")
        {
            var context = await ValidateAnkiProbePackageAsync(request, input, actionRoot, cancellationToken);
            return new ModuleActionResolution("export.anki", Array.Empty<string>(), false, null, context);
        }
        if (schema == "content-settings-action.v1" && request.Operation.StartsWith("content.settings.", StringComparison.Ordinal))
        {
            var context = ValidateContentSettingsAction(request, input);
            return new ModuleActionResolution("content.deepseek", context.Arguments, false, null, null, context);
        }
        if (request.SchemaVersion == "knowledge-clipper-module-action.v6")
            return await ValidateTypedPublicActionAsync(request, input, actionRoot, cancellationToken);
        var route = request.Operation switch
        {
            "speech.voices" when schema == "speech-settings-voice-discovery-input.v1" =>
                ("speech.edge", new[] { "settings", "companion", "voices", "--input", inputPath, "--json" }, false),
            "speech.preview" when schema == "speech-settings-speaker-preview-input.v1" &&
                input.TryGetProperty("action", out var action) && action.GetString() == "speaker.preview" &&
                input.TryGetProperty("explicitSideEffectAuthorization", out var authorization) && authorization.ValueKind == JsonValueKind.True =>
                ("speech.edge", new[] { "settings", "companion", "preview", "--input", inputPath, "--json" }, true),
            "remnote.probe" when schema == "remnote-public-probe-request.v2" =>
                ("export.remnote", new[] { "probe-public", "--request", inputPath, "--json" }, false),
            "remnote.discover-target" when schema == "remnote-target-discovery-request.v1" =>
                ("export.remnote", new[] { "discover-target-public", "--request", inputPath, "--json" }, false),
            _ => throw new InvalidDataException("module-action input schema/action/authorization does not match the requested operation.")
        };
        return new ModuleActionResolution(route.Item1, route.Item2, route.Item3, null, null, null);
    }

    private static void ValidateOutput(string operation, JsonElement output, ContentSettingsContext? contentSettings, bool typedPublic)
    {
        var schema = output.TryGetProperty("schemaVersion", out var schemaElement) ? schemaElement.GetString() : null;
        if (operation == "speech.voices")
        {
            if (schema != "speech-settings-voice-discovery-output.v1" || output.GetProperty("workerId").GetString() != "speech.edge" ||
                output.GetProperty("physicalSpeakerPlayback").GetBoolean() || output.GetProperty("settingsMutationCount").GetInt32() != 0 ||
                output.GetProperty("catalogSha256").GetString() is not string digest || digest.Length != 64 || digest.Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException("Speech voice discovery result violates the read-only catalog contract.");
        }
        else if (operation == "speech.preview")
        {
            if (schema != "speech-settings-speaker-preview-output.v1" || !output.GetProperty("explicitAuthorizationConsumed").GetBoolean() ||
                !output.GetProperty("naturalCompletion").GetBoolean() || output.GetProperty("previewAttemptCount").GetInt32() != 1 ||
                output.GetProperty("audioPlayerInvocationCount").GetInt32() != 1)
                throw new InvalidDataException("Speech preview result violates the explicit single-attempt contract.");
        }
        else if (operation == "remnote.probe")
        {
            if (schema != "remnote-public-probe-result.v2" || output.GetProperty("worker").GetProperty("id").GetString() != "export.remnote" ||
                output.GetProperty("externalWriteCount").GetInt32() != 0 || !output.GetProperty("sanitized").GetBoolean())
                throw new InvalidDataException("RemNote public probe result violates the read-only contract.");
            var state = output.GetProperty("state").GetString();
            var admission = output.GetProperty("admission").GetString();
            if (state is not ("healthy" or "degraded" or "blocked") || admission is not ("ready" or "blocked") ||
                (state == "healthy" && admission != "ready") || (state is "degraded" or "blocked" && admission != "blocked") ||
                output.GetProperty("reasonCodes").ValueKind != JsonValueKind.Array ||
                output.GetProperty("reasonCodes").EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) ||
                output.GetProperty("fixedSemantics").GetProperty("automaticRetryAllowed").GetBoolean())
                throw new InvalidDataException("RemNote public probe state/admission/retry semantics are invalid.");
            if (state == "healthy" &&
                (!output.GetProperty("connection").GetProperty("connected").GetBoolean() ||
                 !output.GetProperty("connection").GetProperty("acceptWriteOperations").GetBoolean() ||
                 !output.GetProperty("targetBinding").GetProperty("matched").GetBoolean() ||
                 output.GetProperty("targetBinding").GetProperty("observedType").GetString() != "document" ||
                 output.GetProperty("reasonCodes").GetArrayLength() != 0 ||
                 output.GetProperty("toolChecks").EnumerateArray().Any(item => item.GetProperty("outcome").GetString() != "passed")))
                throw new InvalidDataException("Healthy RemNote public probe result is not fully ready and exact-matched.");
        }
        else if (operation == "remnote.discover-target")
        {
            if (schema != "remnote-target-discovery-result.v1" || output.GetProperty("worker").GetProperty("id").GetString() != "export.remnote" ||
                output.GetProperty("externalWriteCount").GetInt32() != 0 || !output.GetProperty("sanitized").GetBoolean() ||
                output.GetProperty("fixedSemantics").GetProperty("automaticRetryAllowed").GetBoolean())
                throw new InvalidDataException("RemNote target discovery violates the read-only contract.");
            var state = output.GetProperty("state").GetString();
            var admission = output.GetProperty("admission").GetString();
            var selection = output.GetProperty("selection");
            if (state is not ("healthy" or "degraded" or "blocked") || admission is not ("ready" or "blocked") ||
                (state == "healthy" && admission != "ready") || (state is "degraded" or "blocked" && admission != "blocked") ||
                selection.GetProperty("selector").GetProperty("expectedType").GetString() != "document" ||
                selection.GetProperty("selector").GetProperty("match").GetString() != "exact" ||
                selection.GetProperty("selector").GetProperty("uniqueness").GetString() != "required" ||
                output.GetProperty("fixedSemantics").GetProperty("automaticRetryAllowed").GetBoolean())
                throw new InvalidDataException("RemNote target discovery state/admission semantics are invalid.");
            var ready = state == "healthy" && admission == "ready";
            if (ready && (!selection.GetProperty("persistableBinding").GetBoolean() ||
                          selection.GetProperty("resolution").GetString() != "unique" ||
                          !selection.GetProperty("exactReadback").GetBoolean() ||
                          string.IsNullOrWhiteSpace(selection.GetProperty("resolvedRootRemId").GetString())))
                throw new InvalidDataException("Ready RemNote discovery is not a unique exact persistable binding.");
            if (!ready && selection.GetProperty("persistableBinding").GetBoolean())
                throw new InvalidDataException("Blocked RemNote discovery must never be persistable.");
        }
        else if (operation == "anki.initialize-managed")
        {
            ValidateAnkiInitializationStatus(output);
        }
        else if (operation == "anki.probe")
        {
            ValidateAnkiProbeStatus(output);
        }
        else if (contentSettings is not null)
        {
            ValidateContentSettingsOutput(operation, output, contentSettings);
        }
        else throw new InvalidDataException("Unknown module-action output contract.");
        if (typedPublic) ValidateTypedPublicOutput(operation, output);
    }

    private static void ValidateOutputSafely(string operation, JsonElement output, ContentSettingsContext? contentSettings, bool typedPublic)
    {
        try { ValidateOutput(operation, output, contentSettings, typedPublic); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Admitted module action returned an incomplete or ill-typed contract result.", exception);
        }
    }

    private static bool IsAdmittedTypedNonzeroResult(string operation, JsonElement output) =>
        (operation is "remnote.probe" or "remnote.discover-target") &&
        output.GetProperty("state").GetString() is "degraded" or "blocked" &&
        output.GetProperty("admission").GetString() == "blocked" &&
        output.GetProperty("externalWriteCount").GetInt32() == 0 &&
        output.GetProperty("sanitized").GetBoolean();

    private async Task<ModuleActionReceipt> PublishFailureAsync(
        ModuleActionRequest request, string inputSha, string receiptPath, bool physicalAudio, string code, bool unknownState, CancellationToken cancellationToken)
    {
        var receipt = new ModuleActionReceipt(
            ReceiptSchemaVersion(request), request.ActionId, request.Operation, false,
            unknownState ? "unknown_external_state" : "failed", request.PayloadRef, inputSha,
            null, null, null, null, unknownState && request.Operation == "anki.initialize-managed" ? -1 : 0, physicalAudio, false, request.IdempotencyKey, DateTimeOffset.UtcNow,
            new WorkerError(code, "worker", false, request.PayloadRef, "Admitted module action did not complete.", "worker"),
            request.SchemaVersion is "knowledge-clipper-module-action.v5" or "knowledge-clipper-module-action.v6" ? unknownState ? "unknown_external_state" : "failed" : null,
            null, null, null,
            request.SchemaVersion is "knowledge-clipper-module-action.v5" or "knowledge-clipper-module-action.v6" ? Array.Empty<ModuleActionOutputReference>() : null);
        await AtomicFile.WriteJsonOnceAsync(receiptPath, receipt, cancellationToken);
        return receipt;
    }

    private static string ReceiptSchemaVersion(ModuleActionRequest request) =>
        request.SchemaVersion switch
        {
            "knowledge-clipper-module-action.v3" => "knowledge-clipper-module-action-receipt.v3",
            "knowledge-clipper-module-action.v4" => "knowledge-clipper-module-action-receipt.v4",
            "knowledge-clipper-module-action.v5" => "knowledge-clipper-module-action-receipt.v5",
            "knowledge-clipper-module-action.v6" => "knowledge-clipper-module-action-receipt.v6",
            "knowledge-clipper-module-action.v2" => "knowledge-clipper-module-action-receipt.v2",
            _ => "knowledge-clipper-module-action-receipt.v1"
        };

    private async Task<ModuleActionResolution> ValidateTypedPublicActionAsync(
        ModuleActionRequest request, JsonElement input, string actionRoot, CancellationToken cancellationToken)
    {
        if (request.Operation == "remnote.probe")
        {
            RequireExactProperties(input, "typed RemNote public probe", new[] { "schemaVersion", "probeId", "authorizationScope", "timeoutMs", "operations", "target" });
            if (input.GetProperty("schemaVersion").GetString() != "remnote-public-probe-request.v2" ||
                string.IsNullOrWhiteSpace(input.GetProperty("probeId").GetString()) || input.GetProperty("timeoutMs").GetInt32() != 15000)
                throw new InvalidDataException("Typed RemNote probe identity or timeout drifted.");
            var scope = input.GetProperty("authorizationScope").GetString();
            if (scope is not ("configured-destination" or "isolated-canary")) throw new InvalidDataException("Typed RemNote probe authorization scope is invalid.");
            var operations = input.GetProperty("operations").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (!operations.SequenceEqual(new[] { "status", "search", "list", "read" }, StringComparer.Ordinal))
                throw new InvalidDataException("Typed RemNote probe permits only status/search/list/read in fixed order.");
            var target = input.GetProperty("target");
            RequireExactProperties(target, "typed RemNote probe target", new[] { "rootRemId", "rootTitle" });
            if (string.IsNullOrWhiteSpace(target.GetProperty("rootRemId").GetString()) || string.IsNullOrWhiteSpace(target.GetProperty("rootTitle").GetString()) ||
                scope == "isolated-canary" && target.GetProperty("rootTitle").GetString() == "剪藏")
                throw new InvalidDataException("Typed RemNote probe target violates its authorization scope.");
            return new ModuleActionResolution("export.remnote", new[] { "probe-public", "--request", Path.Combine(actionRoot, "input.json"), "--json" }, false, null, null, null);
        }

        if (request.Operation == "speech.voices")
        {
            var names = input.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.IsSubsetOf(new[] { "schemaVersion", "mode", "requestedLanguages", "cacheMaxAgeSeconds", "deployment", "catalogSnapshot" }) ||
                !new[] { "schemaVersion", "mode", "requestedLanguages", "cacheMaxAgeSeconds", "deployment" }.All(names.Contains) ||
                input.GetProperty("schemaVersion").GetString() != "speech-settings-voice-discovery-input.v1" ||
                input.GetProperty("mode").GetString() is not ("discover" or "cache_only" or "validate_only") ||
                input.GetProperty("cacheMaxAgeSeconds").GetInt32() is < 60 or > 604800)
                throw new InvalidDataException("Typed Speech voice discovery input drifted.");
            var languages = input.GetProperty("requestedLanguages").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (languages.Length == 0 || languages.Any(string.IsNullOrWhiteSpace) || languages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != languages.Length ||
                input.GetProperty("deployment").ValueKind != JsonValueKind.Object ||
                input.GetProperty("mode").GetString() == "validate_only" && !input.TryGetProperty("catalogSnapshot", out _))
                throw new InvalidDataException("Typed Speech voice discovery is not a complete read-only request.");
            return new ModuleActionResolution("speech.edge", new[] { "settings", "companion", "voices", "--input", Path.Combine(actionRoot, "input.json"), "--json" }, false, null, null, null);
        }

        RequireExactProperties(input, "typed Speech preview action", new[] { "schemaVersion", "workerInput", "authorization", "effectClass", "automaticRetryAllowed" });
        if (input.GetProperty("schemaVersion").GetString() != "speech-settings-speaker-preview-action.v2" ||
            input.GetProperty("effectClass").GetString() != "physical-audio-single-attempt" || input.GetProperty("automaticRetryAllowed").GetBoolean())
            throw new InvalidDataException("Typed Speech preview action identity/effect policy drifted.");
        var workerInput = input.GetProperty("workerInput");
        RequireExactProperties(workerInput, "typed Speech preview worker input", new[] { "schemaVersion", "scope", "effectiveAt", "action", "explicitSideEffectAuthorization", "previewLanguage", "adapterInput" });
        if (workerInput.GetProperty("schemaVersion").GetString() != "speech-settings-speaker-preview-input.v1" ||
            workerInput.GetProperty("scope").GetString() != "project" || workerInput.GetProperty("effectiveAt").GetString() != "preview_only" ||
            workerInput.GetProperty("action").GetString() != "speaker.preview" || !workerInput.GetProperty("explicitSideEffectAuthorization").GetBoolean() ||
            string.IsNullOrWhiteSpace(workerInput.GetProperty("previewLanguage").GetString()) || workerInput.GetProperty("adapterInput").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Typed Speech preview worker input is incomplete or unauthorized.");
        var authorization = input.GetProperty("authorization");
        RequireExactProperties(authorization, "typed Speech preview authorization", new[] { "mode", "operation", "actionId", "previewLanguage", "authorizedAt" });
        if (authorization.GetProperty("mode").GetString() != "explicit-user-action" || authorization.GetProperty("operation").GetString() != "speaker.preview" ||
            authorization.GetProperty("actionId").GetString() != request.ActionId ||
            authorization.GetProperty("previewLanguage").GetString() != workerInput.GetProperty("previewLanguage").GetString() ||
            !DateTimeOffset.TryParse(authorization.GetProperty("authorizedAt").GetString(), out _))
            throw new InvalidDataException("Typed Speech preview requires an exact explicit-user-action authorization binding.");
        var workerInputPath = Path.Combine(actionRoot, "worker-input.json");
        await AtomicFile.WriteJsonOnceAsync(workerInputPath, workerInput, cancellationToken);
        return new ModuleActionResolution("speech.edge", new[] { "settings", "companion", "preview", "--input", workerInputPath, "--json" }, true, null, null, null);
    }

    private static void ValidateTypedPublicOutput(string operation, JsonElement output)
    {
        if (operation == "speech.voices" &&
            (output.GetProperty("settingsMutationCount").GetInt32() != 0 || output.GetProperty("physicalSpeakerPlayback").GetBoolean() ||
             output.GetProperty("sideEffectsPerformed").GetArrayLength() != 0))
            throw new InvalidDataException("Typed Speech voice discovery produced a mutation or physical playback.");
        if (operation == "speech.preview" &&
            (output.GetProperty("workerId").GetString() != "speech.edge" || output.GetProperty("action").GetString() != "speaker.preview" ||
             output.GetProperty("scope").GetString() != "project" || output.GetProperty("effectiveAt").GetString() != "preview_only" ||
             output.GetProperty("playbackState").GetString() != "idle" || output.GetProperty("activePlayback").GetBoolean() ||
             output.GetProperty("temporaryArtifactsRetained").GetBoolean()))
            throw new InvalidDataException("Typed Speech preview did not complete its exact single-attempt boundary.");
        if (operation == "remnote.probe")
        {
            var operations = output.GetProperty("toolChecks").EnumerateArray().Select(item => item.GetProperty("operation").GetString()).ToArray();
            if (!operations.SequenceEqual(new[] { "status", "search", "list", "read" }, StringComparer.Ordinal) ||
                output.GetProperty("externalWriteCount").GetInt32() != 0 || !output.GetProperty("sanitized").GetBoolean())
                throw new InvalidDataException("Typed RemNote probe exceeded status/search/list/read or leaked an unsanitized result.");
        }
    }

    private static (string Kind, string ContractVersion) TypedPublicOutputIdentity(string operation) => operation switch
    {
        "remnote.probe" => ("remnote-public-probe-result", "2.0.0"),
        "speech.voices" => ("speech-voice-discovery-result", "1.0.0"),
        "speech.preview" => ("speech-speaker-preview-result", "1.0.0"),
        _ => throw new InvalidDataException("Unknown typed public settings operation.")
    };

    private ContentSettingsContext ValidateContentSettingsAction(ModuleActionRequest request, JsonElement input)
    {
        var action = input.GetProperty("action").GetString();
        if (action != request.Operation) throw new InvalidDataException("Content settings action does not match the module-action operation.");
        if (request.Operation == "content.settings.describe")
        {
            RequireExactProperties(input, "content settings describe action", new[] { "schemaVersion", "action", "effectClass", "externalMutation" });
            if (input.GetProperty("effectClass").GetString() != "local-filesystem-read-only" || input.GetProperty("externalMutation").GetBoolean())
                throw new InvalidDataException("Content settings describe must remain read-only.");
            return new ContentSettingsContext(new[] { "settings", "describe", "--json" }, null, false);
        }

        var pathProperty = request.Operation == "content.settings.copy" ? "targetRoot" : "root";
        var expected = request.Operation == "content.settings.copy"
            ? new[] { "schemaVersion", "action", pathProperty, "effectClass", "externalMutation", "authorization" }
            : new[] { "schemaVersion", "action", pathProperty, "effectClass", "externalMutation" };
        RequireExactProperties(input, $"{request.Operation} action", expected);
        var suppliedPath = input.GetProperty(pathProperty).GetString();
        if (string.IsNullOrWhiteSpace(suppliedPath) || !Path.IsPathFullyQualified(suppliedPath))
            throw new InvalidDataException($"Content settings {pathProperty} must be an absolute path.");
        var canonicalPath = Path.GetFullPath(suppliedPath);
        if (Path.GetPathRoot(canonicalPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(canonicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("Content settings action may not target a filesystem root.");

        if (request.Operation == "content.settings.copy")
        {
            if (input.GetProperty("effectClass").GetString() != "local-filesystem-create-once" || !input.GetProperty("externalMutation").GetBoolean())
                throw new InvalidDataException("Content settings copy must declare its create-once local mutation.");
            if (Directory.Exists(canonicalPath) || File.Exists(canonicalPath))
                throw new InvalidDataException("Content settings copy target must not already exist.");
            var parent = Directory.GetParent(canonicalPath) ?? throw new InvalidDataException("Content settings copy target has no parent directory.");
            if (!parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Content settings copy parent must be an existing non-reparse directory.");
            if (IsWithin(canonicalPath, jobsRoot) || IsWithin(canonicalPath, releaseRoot))
                throw new InvalidDataException("Content settings copy may not mutate the Core jobs root or admitted release root.");
            var authorization = input.GetProperty("authorization");
            RequireExactProperties(authorization, "content settings copy authorization", new[] { "mode", "operation", "targetRoot", "createOnly" });
            var authorizedPath = authorization.GetProperty("targetRoot").GetString();
            if (authorization.GetProperty("mode").GetString() != "explicit-user-action" || authorization.GetProperty("operation").GetString() != "copy" ||
                !authorization.GetProperty("createOnly").GetBoolean() || string.IsNullOrWhiteSpace(authorizedPath) ||
                !Path.GetFullPath(authorizedPath).Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Content settings copy requires exact explicit target authorization.");
            return new ContentSettingsContext(new[] { "settings", "copy", "--target", canonicalPath, "--json" }, canonicalPath, true);
        }

        if (input.GetProperty("effectClass").GetString() != "local-filesystem-read-only" || input.GetProperty("externalMutation").GetBoolean() ||
            !Directory.Exists(canonicalPath) || new DirectoryInfo(canonicalPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Content settings validate/diff requires an existing non-reparse read-only root.");
        var command = request.Operation == "content.settings.validate" ? "validate" : "diff";
        return new ContentSettingsContext(new[] { "settings", command, "--root", canonicalPath, "--json" }, canonicalPath, false);
    }

    private static bool IsWithin(string candidate, string boundary)
    {
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedBoundary = Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedBoundary, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateContentSettingsOutput(string operation, JsonElement output, ContentSettingsContext context)
    {
        var (kind, _) = ContentSettingsOutputIdentity(operation);
        var expectedSchema = kind switch
        {
            "content-settings-description" => "content-settings-adapter/1.0.0",
            "content-settings-copy-result" => "content-settings-copy-result/1.0.0",
            "content-settings-validation" => "content-settings-validation/1.0.0",
            _ => "content-settings-diff/1.0.0"
        };
        if (output.ValueKind != JsonValueKind.Object || output.GetProperty("schemaVersion").GetString() != expectedSchema)
            throw new InvalidDataException("Content settings worker returned the wrong typed result.");
        if (operation == "content.settings.describe")
        {
            if (output.GetProperty("worker").GetProperty("id").GetString() != "content.deepseek" ||
                output.GetProperty("provider").GetProperty("apiKey").GetProperty("valueExposed").GetBoolean() ||
                output.GetProperty("credentialsPolicy").GetProperty("taskPackages").GetString() != "forbidden")
                throw new InvalidDataException("Content settings description violates credential boundary facts.");
            return;
        }
        var rootProperty = operation == "content.settings.copy" ? "targetRoot" : "draftRoot";
        var observedRoot = output.GetProperty(rootProperty).GetString();
        if (string.IsNullOrWhiteSpace(observedRoot) || !Path.GetFullPath(observedRoot).Equals(context.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Content settings result root drifted from the authorized input.");
        if (operation == "content.settings.copy")
        {
            var validation = output.GetProperty("validation");
            if (!validation.GetProperty("valid").GetBoolean() || validation.GetProperty("draftRoot").GetString() is not string validationRoot ||
                !Path.GetFullPath(validationRoot).Equals(context.Root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(context.Root))
                throw new InvalidDataException("Content settings copy did not produce a valid exact target draft.");
        }
        else
        {
            if (output.GetProperty("valid").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                output.GetProperty("warnings").ValueKind != JsonValueKind.Array || output.GetProperty("errors").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Content settings validation/diff result omitted typed validity evidence.");
        }
    }

    private static (string Kind, string ContractVersion) ContentSettingsOutputIdentity(string operation) => operation switch
    {
        "content.settings.describe" => ("content-settings-description", "1.0.0"),
        "content.settings.copy" => ("content-settings-copy-result", "1.0.0"),
        "content.settings.validate" => ("content-settings-validation", "1.0.0"),
        "content.settings.diff" => ("content-settings-diff", "1.0.0"),
        _ => throw new InvalidDataException("Unknown content settings operation.")
    };

    private async Task<AnkiProbeContext> ValidateAnkiProbePackageAsync(
        ModuleActionRequest moduleRequest,
        JsonElement input,
        string actionRoot,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(input, "Anki read-only probe action", new[]
        {
            "schemaVersion", "taskId", "packageRootRef", "configRef", "effectClass", "externalMutation"
        });
        if (input.GetProperty("effectClass").GetString() != "provider-read-only" || input.GetProperty("externalMutation").GetBoolean())
            throw new InvalidDataException("Anki probe action must remain provider-read-only with externalMutation=false.");
        var taskId = input.GetProperty("taskId").GetString();
        if (string.IsNullOrWhiteSpace(taskId)) throw new InvalidDataException("Anki probe taskId is missing.");
        var canonicalPackageRef = $"module-actions/{moduleRequest.ActionId}/package";
        if (input.GetProperty("packageRootRef").GetString() != canonicalPackageRef)
            throw new InvalidDataException("Anki probe packageRootRef is not canonical.");
        var packageRoot = Path.Combine(actionRoot, "package");
        if (!Directory.Exists(packageRoot)) throw new DirectoryNotFoundException("Anki probe package is missing.");
        var configReference = input.GetProperty("configRef").Deserialize<ArtifactReference>(ContractJson.Options)
            ?? throw new InvalidDataException("Anki probe configRef is invalid.");
        if (configReference.Kind != "anki-export-config" || configReference.ContractVersion != "1.0.0")
            throw new InvalidDataException("Anki probe configRef must be an anki-export-config@1.0.0 artifact.");
        await CoreArtifactIO.ValidateReferenceAsync(packageRoot, configReference, cancellationToken);
        var manifestPath = Path.Combine(packageRoot, "task.manifest.v1.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Anki probe task manifest is missing.", manifestPath);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
        var root = manifest.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "1.0.0" || root.GetProperty("taskId").GetString() != taskId ||
            !root.GetProperty("requestedOutputs").EnumerateArray().Any(item => item.GetString() == "anki-connection-status"))
            throw new InvalidDataException("Anki probe task manifest does not bind the task and requested output.");
        var manifestInput = root.GetProperty("inputRefs").EnumerateArray().Single();
        if (manifestInput.GetProperty("artifactRef").GetString() != configReference.ArtifactRef ||
            !manifestInput.GetProperty("expectedSha256").GetString()!.Equals(configReference.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Anki probe task manifest does not bind the config artifact SHA.");
        return new AnkiProbeContext(packageRoot, canonicalPackageRef, taskId, configReference);
    }

    private async Task<ModuleActionReceipt> RunAnkiProbeAsync(
        ModuleActionRequest request,
        string inputSha,
        string receiptPath,
        AnkiProbeContext context,
        CancellationToken cancellationToken)
    {
        var runId = $"run-{request.ActionId}";
        var plan = ModuleSettingsActionResolver.ResolveAnkiProbe(context.TaskId, context.ConfigReference, registry);
        var taskState = await new OrchestratorEngine().RunAsync(context.PackageRoot, plan, registry, releaseRoot, runId, cancellationToken);
        var resultPaths = Directory.GetFiles(Path.Combine(context.PackageRoot, "runs", runId), "result.json", SearchOption.AllDirectories);
        if (resultPaths.Length != 1) throw new InvalidDataException("Anki probe must publish exactly one worker result.");
        var resultPath = resultPaths[0];
        var resultBytes = await File.ReadAllBytesAsync(resultPath, cancellationToken);
        var result = JsonSerializer.Deserialize<WorkerResult>(resultBytes, ContractJson.Options)
            ?? throw new InvalidDataException("Anki probe worker result is empty.");
        if (result.TaskId != context.TaskId || result.RunId != runId || result.Worker.Id != "export.anki" || result.OperationId.Length == 0)
            throw new InvalidDataException("Anki probe worker result identity is not bound to the action task/run.");
        var resultRef = PathPolicy.ToJobRelative(jobsRoot, resultPath);
        var resultSha = Integrity.Sha256Hex(resultBytes).ToLowerInvariant();
        if (taskState != "succeeded" || result.Status != "completed" || result.Error is not null)
        {
            if (result.Outputs.Count != 0)
                throw new InvalidDataException("Failed Anki probe must not expose an unvalidated output artifact.");
            var failed = new ModuleActionReceipt(
                ReceiptSchemaVersion(request), request.ActionId, request.Operation, false, "failed",
                request.PayloadRef, inputSha, resultRef, resultSha, null, null, 0, false, false,
                request.IdempotencyKey, DateTimeOffset.UtcNow,
                result.Error ?? new WorkerError("anki.probe_failed", "worker", false, resultRef, "Read-only probe did not complete.", "worker"),
                result.Status, context.TaskId, runId, context.PackageRootRef, Array.Empty<ModuleActionOutputReference>());
            await AtomicFile.WriteJsonOnceAsync(receiptPath, failed, cancellationToken);
            return failed;
        }
        if (result.Outputs.Count != 1 || result.Outputs[0].Kind != "anki-connection-status")
            throw new InvalidDataException("Completed Anki probe omitted its typed status artifact.");
        var output = result.Outputs[0];
        await CoreArtifactIO.ValidateReferenceAsync(context.PackageRoot, output, cancellationToken);
        var manifest = await CoreArtifactIO.ReadManifestAsync(context.PackageRoot, output, cancellationToken);
        var entrypoint = VerifiedArtifactEntrypointResolver.Resolve(context.PackageRoot, manifest, "Anki probe status artifact");
        using var statusDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(context.PackageRoot, entrypoint.Path), cancellationToken));
        ValidateAnkiProbeStatus(statusDocument.RootElement);
        var businessStatus = statusDocument.RootElement.GetProperty("status").GetString()!;
        var outputRefs = new[]
        {
            new ModuleActionOutputReference("artifact", output.Kind, output.ContractVersion, context.PackageRootRef,
                output.ManifestPath, output.ExpectedSha256.ToLowerInvariant())
        };
        var receipt = new ModuleActionReceipt(
            ReceiptSchemaVersion(request), request.ActionId, request.Operation, true, businessStatus,
            request.PayloadRef, inputSha, resultRef, resultSha, null, null, 0, false, false,
            request.IdempotencyKey, DateTimeOffset.UtcNow, null, result.Status, context.TaskId, runId,
            context.PackageRootRef, outputRefs);
        await AtomicFile.WriteJsonOnceAsync(receiptPath, receipt, cancellationToken);
        return receipt;
    }

    private static void ValidateAnkiProbeStatus(JsonElement output)
    {
        if (output.GetProperty("schemaVersion").GetString() != "anki-connection-status.v1" ||
            output.GetProperty("operation").GetString() != "probe" ||
            output.GetProperty("status").GetString() is not ("ready" or "needs_initialization" or "migration_required") ||
            output.GetProperty("externalMutation").GetBoolean())
            throw new InvalidDataException("Anki probe status violates the completed read-only output contract.");
    }

    private async Task<AnkiInitializationContext> ValidateAnkiInitializationPackageAsync(
        ModuleActionRequest moduleRequest,
        JsonElement input,
        string actionRoot,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(input, "anki initialize-managed action", new[]
        {
            "schemaVersion", "workerId", "workerVersion", "packageRootRef", "taskManifestRef", "taskManifestSha256",
            "requestRef", "requestSha256", "configManifestRef", "configManifestSha256", "configPayloadRef", "configPayloadSha256", "authorization"
        });
        if (input.GetProperty("workerId").GetString() != "export.anki" || input.GetProperty("workerVersion").GetString() != "1.4.0")
            throw new InvalidDataException("Anki initialize-managed action must bind export.anki@1.4.0.");
        var canonicalPackageRef = $"module-actions/{moduleRequest.ActionId}/package";
        if (input.GetProperty("packageRootRef").GetString() != canonicalPackageRef)
            throw new InvalidDataException("Anki initialize-managed packageRootRef is not canonical.");
        var packageRoot = PathPolicy.ResolveJobRelative(jobsRoot, canonicalPackageRef);
        if (!Directory.Exists(packageRoot)) throw new DirectoryNotFoundException("Anki initialize-managed package is missing.");

        string ResolveBoundFile(string property, string shaProperty, string requiredSuffix)
        {
            var relative = input.GetProperty(property).GetString() ?? throw new InvalidDataException($"{property} is missing.");
            if (!relative.StartsWith(canonicalPackageRef + "/", StringComparison.Ordinal) || !relative.EndsWith(requiredSuffix, StringComparison.Ordinal))
                throw new InvalidDataException($"{property} is outside the immutable action package.");
            var path = PathPolicy.ResolveJobRelative(jobsRoot, relative);
            if (!File.Exists(path)) throw new FileNotFoundException($"{property} is missing.", path);
            var expected = input.GetProperty(shaProperty).GetString();
            var actual = Integrity.Sha256Hex(File.ReadAllBytes(path)).ToLowerInvariant();
            if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{property} SHA-256 drifted.");
            return path;
        }

        var taskManifestPath = ResolveBoundFile("taskManifestRef", "taskManifestSha256", "/task.manifest.v1.json");
        var requestPath = ResolveBoundFile("requestRef", "requestSha256", "/request.json");
        var configManifestPath = ResolveBoundFile("configManifestRef", "configManifestSha256", "/artifact.json");
        var configPayloadPath = ResolveBoundFile("configPayloadRef", "configPayloadSha256", ".json");
        using var taskManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(taskManifestPath, cancellationToken));
        using var workerRequest = JsonDocument.Parse(await File.ReadAllBytesAsync(requestPath, cancellationToken));
        using var configManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(configManifestPath, cancellationToken));
        using var configPayload = JsonDocument.Parse(await File.ReadAllBytesAsync(configPayloadPath, cancellationToken));
        var requestRoot = workerRequest.RootElement;
        var taskId = requestRoot.GetProperty("taskId").GetString();
        var configArtifactId = requestRoot.GetProperty("configSnapshot").GetProperty("ankiConfigArtifactId").GetString();
        if (requestRoot.GetProperty("schemaVersion").GetString() != "1.0.0" || requestRoot.GetProperty("workerId").GetString() != "export.anki" ||
            requestRoot.GetProperty("operation").GetString() != "initialize-managed" || requestRoot.GetProperty("idempotencyKey").GetString() != moduleRequest.IdempotencyKey)
            throw new InvalidDataException("Anki worker request identity is not bound to the public action.");
        var attemptPath = requestRoot.GetProperty("attemptPath").GetString() ?? "";
        var packageRequestRelative = Path.GetRelativePath(packageRoot, requestPath).Replace('\\', '/');
        if (packageRequestRelative != attemptPath + "/request.json")
            throw new InvalidDataException("Anki worker request path does not match attemptPath.");
        var resultRelative = requestRoot.GetProperty("resultPath").GetString() ?? "";
        if (resultRelative != attemptPath + "/result.json") throw new InvalidDataException("Anki worker resultPath is not canonical.");
        var resultPath = PathPolicy.ResolveJobRelative(packageRoot, resultRelative);
        if (File.Exists(resultPath)) throw new InvalidDataException("Anki action package already contains a worker result without a public receipt.");

        var authorization = input.GetProperty("authorization");
        var workerAuthorization = requestRoot.GetProperty("payload").GetProperty("sideEffectAuthorization");
        RequireExactProperties(authorization, "Anki public authorization", new[] { "schemaVersion", "authorizationId", "mode", "operation", "scope", "taskId", "configArtifactId", "authorizedAt" });
        if (authorization.EnumerateObject().Any(item => !workerAuthorization.TryGetProperty(item.Name, out var workerValue) || item.Value.ToString() != workerValue.ToString()) ||
            workerAuthorization.EnumerateObject().Count() != authorization.EnumerateObject().Count() || authorization.GetProperty("schemaVersion").GetString() != "1.0.0" ||
            authorization.GetProperty("mode").GetString() != "explicit-user-action" || authorization.GetProperty("operation").GetString() != "initialize-managed" ||
            authorization.GetProperty("scope").GetString() != "managed-models-and-decks" || authorization.GetProperty("taskId").GetString() != taskId ||
            authorization.GetProperty("configArtifactId").GetString() != configArtifactId ||
            !DateTimeOffset.TryParse(authorization.GetProperty("authorizedAt").GetString(), out _))
            throw new InvalidDataException("Anki initialize-managed explicit authorization is missing or drifted.");

        var configManifestSha = input.GetProperty("configManifestSha256").GetString()!;
        var requestInput = requestRoot.GetProperty("inputRefs").EnumerateArray().Single();
        var packageManifestRelative = Path.GetRelativePath(packageRoot, configManifestPath).Replace('\\', '/');
        if (requestInput.GetProperty("artifactRef").GetString() != configArtifactId || requestInput.GetProperty("kind").GetString() != "anki-export-config" ||
            requestInput.GetProperty("manifestPath").GetString() != packageManifestRelative || !requestInput.GetProperty("expectedSha256").GetString()!.Equals(configManifestSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Anki worker request is not SHA-bound to the selected config manifest.");
        var manifestRoot = configManifest.RootElement;
        var packagePayloadRelative = Path.GetRelativePath(packageRoot, configPayloadPath).Replace('\\', '/');
        var payloadEntry = manifestRoot.GetProperty("files").EnumerateArray().Single();
        if (manifestRoot.GetProperty("artifactId").GetString() != configArtifactId || manifestRoot.GetProperty("kind").GetString() != "anki-export-config" ||
            payloadEntry.GetProperty("path").GetString() != packagePayloadRelative ||
            !payloadEntry.GetProperty("sha256").GetString()!.Equals(input.GetProperty("configPayloadSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            manifestRoot.GetProperty("metadata").GetProperty("entrypoint").GetString() != packagePayloadRelative ||
            configPayload.RootElement.GetProperty("provision_managed").ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Anki config artifact is not an exact provision_managed=true SHA-bound snapshot.");
        var taskRoot = taskManifest.RootElement;
        var taskInput = taskRoot.GetProperty("inputRefs").EnumerateArray().Single();
        if (taskRoot.GetProperty("schemaVersion").GetString() != "1.0.0" || taskRoot.GetProperty("taskId").GetString() != taskId ||
            !taskRoot.GetProperty("requestedOutputs").EnumerateArray().Any(item => item.GetString() == "anki-managed-initialization-status") ||
            taskInput.GetProperty("artifactRef").GetString() != configArtifactId || !taskInput.GetProperty("expectedSha256").GetString()!.Equals(configManifestSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Anki task manifest is not bound to the initialization config/output contract.");
        return new AnkiInitializationContext(packageRoot, requestPath, resultPath);
    }

    private static async Task<JsonElement?> ReadAnkiWorkerResultAsync(AnkiInitializationContext context, CancellationToken cancellationToken)
    {
        if (!File.Exists(context.ResultPath)) return null;
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(context.ResultPath, cancellationToken));
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadAndValidateAnkiInitializationOutputAsync(
        AnkiInitializationContext context,
        JsonElement workerResult,
        CancellationToken cancellationToken)
    {
        if (workerResult.GetProperty("schemaVersion").GetString() != "1.0.0" || workerResult.GetProperty("worker").GetProperty("id").GetString() != "export.anki" ||
            workerResult.GetProperty("worker").GetProperty("version").GetString() != "1.4.0" || workerResult.GetProperty("status").GetString() != "completed" ||
            workerResult.GetProperty("error").ValueKind != JsonValueKind.Null || workerResult.GetProperty("sideEffects").EnumerateArray().Single().GetProperty("state").GetString() != "verified")
            throw new InvalidDataException("Anki initialize-managed worker result is not a verified export.anki@1.4.0 completion.");
        var outputRefElement = workerResult.GetProperty("outputs").EnumerateArray().Single();
        if (outputRefElement.GetProperty("kind").GetString() != "anki-managed-initialization-status")
            throw new InvalidDataException("Anki initialize-managed worker result omitted the typed status artifact.");
        var outputRef = outputRefElement.Deserialize<ArtifactReference>(ContractJson.Options)
            ?? throw new InvalidDataException("Anki initialize-managed output reference is invalid.");
        await CoreArtifactIO.ValidateReferenceAsync(context.PackageRoot, outputRef, cancellationToken);
        var manifest = await CoreArtifactIO.ReadManifestAsync(context.PackageRoot, outputRef, cancellationToken);
        var entrypoint = VerifiedArtifactEntrypointResolver.Resolve(context.PackageRoot, manifest, "Anki initialization status artifact");
        var statusBytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(context.PackageRoot, entrypoint.Path), cancellationToken);
        using var status = JsonDocument.Parse(statusBytes);
        ValidateAnkiInitializationStatus(status.RootElement);
        return status.RootElement.Clone();
    }

    private static void ValidateAnkiInitializationStatus(JsonElement output)
    {
        if (output.GetProperty("schemaVersion").GetString() != "anki-managed-initialization-status.v1" ||
            output.GetProperty("operation").GetString() != "initialize-managed" || output.GetProperty("effectClass").GetString() != "provider-write" ||
            output.GetProperty("networkScope").GetString() != "loopback-only" || !output.GetProperty("externalMutation").GetBoolean() ||
            output.GetProperty("authorization").GetProperty("mode").GetString() != "explicit-user-action" ||
            output.GetProperty("authorization").GetProperty("scope").GetString() != "managed-models-and-decks" ||
            output.GetProperty("status").GetString() is not ("initialized" or "already_current") ||
            output.GetProperty("automaticRetryAllowed").GetBoolean() || output.GetProperty("notesMediaAndExistingResourcesModified").GetBoolean() ||
            output.GetProperty("forbiddenActionsObserved").GetArrayLength() != 0 ||
            output.GetProperty("models").EnumerateArray().Any(item => item.GetProperty("readback").GetString() != "exact" || item.GetProperty("status").GetString() is not ("created" or "already_current")) ||
            output.GetProperty("decks").EnumerateArray().Any(item => item.GetProperty("readback").GetString() != "exact" || item.GetProperty("status").GetString() is not ("created" or "already_current")) ||
            output.GetProperty("mutatingActionsObserved").EnumerateArray().Any(item => item.GetString() is not ("createModel" or "createDeck")))
            throw new InvalidDataException("Anki initialize-managed status violates the explicit missing-only exact-readback contract.");
        var writes = output.GetProperty("writes");
        var createdModels = output.GetProperty("models").EnumerateArray().Count(item => item.GetProperty("status").GetString() == "created");
        var createdDecks = output.GetProperty("decks").EnumerateArray().Count(item => item.GetProperty("status").GetString() == "created");
        if (writes.GetProperty("modelsCreated").GetInt32() != createdModels || writes.GetProperty("decksCreated").GetInt32() != createdDecks ||
            (output.GetProperty("status").GetString() == "already_current" && (createdModels != 0 || createdDecks != 0)))
            throw new InvalidDataException("Anki initialize-managed write counts do not match exact readback.");
    }

    private static void RequireExactProperties(JsonElement element, string label, IEnumerable<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{label} must be an object.");
        var actual = element.EnumerateObject().Select(item => item.Name).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var required = expected.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(required, StringComparer.Ordinal)) throw new InvalidDataException($"{label} fields drifted.");
    }

    private sealed record ModuleActionResolution(string WorkerId, string[] Arguments, bool PhysicalAudio, AnkiInitializationContext? AnkiInitialization, AnkiProbeContext? AnkiProbe, ContentSettingsContext? ContentSettings = null);
    private sealed record AnkiInitializationContext(string PackageRoot, string RequestPath, string ResultPath);
    private sealed record AnkiProbeContext(string PackageRoot, string PackageRootRef, string TaskId, ArtifactReference ConfigReference);
    private sealed record ContentSettingsContext(string[] Arguments, string? Root, bool LocalMutation);
}
