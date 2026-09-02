using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed class CoreWorkerTaskPackageAdapter
{
    private sealed record PreparedInputs(
        IReadOnlyList<LegacyArtifactReference> RequestInputs,
        IReadOnlyList<LegacyArtifactReference> InitialInputs);

    private readonly string _root;

    public CoreWorkerTaskPackageAdapter(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<LegacyAttemptPackage> PrepareAsync(
        WorkerInvocation invocation,
        AdmittedAuthorityWorker worker,
        CancellationToken cancellationToken)
    {
        var taskId = Token("task-" + invocation.OperationId);
        var runId = Token("run-" + invocation.OperationId);
        var attemptId = "attempt-" + invocation.Request.GetProperty("idempotencyKey").GetString()![..20].ToLowerInvariant();
        var stageId = ResolveStage(worker.WorkerId, invocation.Request);
        var legacyOperation = ResolveOperation(worker.WorkerId, invocation.Request);
        var packageId = Token(invocation.OperationId + "-" + invocation.Request.GetProperty("idempotencyKey").GetString()![..16].ToLowerInvariant());
        var jobRoot = ResolveWithin(_root, packageId);
        Directory.CreateDirectory(jobRoot);

        var preparedInputs = worker.WorkerId switch
        {
            "export.anki" when legacyOperation == "probe" => SingleInitial(await PublishJsonArtifactAsync(
                jobRoot, "anki-config-" + attemptId[8..], "anki-export-config", "anki-export.v1.json",
                GetConfiguration(invocation.Request), cancellationToken).ConfigureAwait(false)),
            "export.anki" when legacyOperation == "export" => await PublishAnkiExportInputsAsync(
                jobRoot, runId, attemptId, invocation.Request, cancellationToken).ConfigureAwait(false),
            "content.deepseek" => SingleInitial(await PublishJsonArtifactAsync(
                jobRoot, "content-input-" + attemptId[8..], "content-input", "standard-input.v3.json",
                BuildContentInput(invocation), cancellationToken).ConfigureAwait(false)),
            _ => throw new InvalidDataException($"Unsupported Core worker adapter route '{worker.WorkerId}:{legacyOperation}'.")
        };
        var inputs = preparedInputs.RequestInputs;

        await PublishTaskManifestAsync(
            jobRoot,
            taskId,
            legacyOperation,
            preparedInputs.InitialInputs,
            worker.WorkerId == "export.anki" && legacyOperation == "probe"
                ? new[] { "anki-connection-status" }
                : worker.WorkerId == "export.anki"
                    ? new[] { "anki-export-evidence" }
                    : new[] { "learning-bundle" },
            cancellationToken).ConfigureAwait(false);

        var attemptPath = $"runs/{runId}/stages/{stageId}/attempts/{attemptId}";
        var request = new LegacyWorkerRequest(
            "1.0.0",
            "request-" + invocation.Request.GetProperty("idempotencyKey").GetString()![..24].ToLowerInvariant(),
            invocation.OperationId,
            taskId,
            runId,
            stageId,
            attemptId,
            worker.WorkerId,
            legacyOperation,
            invocation.Request.GetProperty("idempotencyKey").GetString()!,
            attemptPath,
            attemptPath + "/outputs",
            attemptPath + "/progress.ndjson",
            attemptPath + "/result.json",
            new LegacyCancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000),
            inputs,
            BuildConfigSnapshot(worker.WorkerId, legacyOperation, inputs),
            BuildPayload(worker.WorkerId, invocation.Request));
        var requestPath = ResolveWithin(jobRoot, attemptPath + "/request.json");
        await WriteCreateOnceAsync(requestPath, JsonSerializer.SerializeToUtf8Bytes(request, KernelJson.Options), cancellationToken).ConfigureAwait(false);
        return new LegacyAttemptPackage(
            jobRoot,
            requestPath,
            ResolveWithin(jobRoot, request.ResultPath),
            ResolveWithin(jobRoot, request.CancelSignal.Path),
            request);
    }

    private static string ResolveStage(string workerId, JsonElement request)
        => workerId == "content.deepseek" ? "content.understand-and-card" : GetString(request, "phase") == "probe" ? "anki-probe" : "anki-export";

    private static string ResolveOperation(string workerId, JsonElement request)
        => workerId == "content.deepseek"
            ? GetString(request, "phase") == "make-cards" ? "question.make-cards" : "question.answer"
            : GetString(request, "phase") == "probe" ? "probe" : "export";

    private static JsonElement GetConfiguration(JsonElement request)
    {
        var json = GetString(request, "configurationJson");
        using var document = StrictJson.Parse(Encoding.UTF8.GetBytes(json), "Core Anki configuration");
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetString() != "anki-export-config.v1" || root.GetProperty("provision_managed").GetBoolean())
        {
            throw new InvalidDataException("Core real-worker Anki configuration must be anki-export-config.v1 with provision_managed=false.");
        }
        return root.Clone();
    }

    private static PreparedInputs SingleInitial(LegacyArtifactReference artifact)
        => new(new[] { artifact }, new[] { artifact });

    private static async Task<PreparedInputs> PublishAnkiExportInputsAsync(
        string jobRoot,
        string runId,
        string attemptId,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("sideEffectAuthorization", out var authorization) || authorization.ValueKind != JsonValueKind.Object ||
            authorization.GetProperty("mode").GetString() != "explicit-user-action" || authorization.GetProperty("scope").GetString() != "isolated-canary-deck")
        {
            throw new InvalidDataException("Real Anki export requires explicit isolated-canary-deck authorization.");
        }
        var config = await PublishJsonArtifactAsync(jobRoot, "anki-config-" + attemptId[8..], "anki-export-config", "anki-export.v1.json", GetConfiguration(request), cancellationToken).ConfigureAwait(false);
        var card = request.GetProperty("card");
        var front = GetString(card, "front");
        var back = GetString(card, "back");
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(front + "\n" + back))).ToLowerInvariant();
        var bundle = StrictJson.CloneElement(new
        {
            bundle_kind = "CardBundle",
            schema_version = "1.0",
            bundle_id = "bundle-" + attemptId[8..],
            cards = new[]
            {
                new
                {
                    card_id = "card-" + contentHash[..24], revision = 1, content_hash = contentHash, status = "ready",
                    deck_route_key = "canary", note_type_key = "basic_qa",
                    fields = new { question = front, answer = back, source = "Core v3 isolated Canary" },
                    tags = new[] { "core_v3_canary" }, media_refs = Array.Empty<object>()
                }
            },
            media = Array.Empty<object>()
        });
        var source = await PublishJsonArtifactAsync(
            jobRoot,
            "core-question-source-" + attemptId[8..],
            "core-question-source",
            "source.v1.json",
            StrictJson.CloneElement(new { schema_version = "core-question-source/1.0.0", front, back }),
            cancellationToken).ConfigureAwait(false);
        var cardReference = await PublishDerivedCardArtifactAsync(
            jobRoot,
            runId,
            "card-bundle-" + attemptId[8..],
            bundle,
            source,
            cancellationToken).ConfigureAwait(false);
        return new PreparedInputs(new[] { cardReference, config }, new[] { source, config });
    }

    private static async Task<LegacyArtifactReference> PublishDerivedCardArtifactAsync(
        string jobRoot,
        string runId,
        string artifactId,
        JsonElement value,
        LegacyArtifactReference source,
        CancellationToken cancellationToken)
    {
        var relativeRoot = $"runs/{runId}/adapters/anki-export/{artifactId}/v0001";
        var payloadRelative = relativeRoot + "/card-bundle.v1.json";
        var manifestRelative = relativeRoot + "/artifact.json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, payloadRelative), bytes, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifest = new
        {
            schemaVersion = "1.0.0", artifactId, kind = "card-bundle", contractVersion = "1.0.0", mediaType = "application/json",
            files = new[] { new { path = payloadRelative, sha256 = digest, bytes = bytes.LongLength, mediaType = "application/json" } },
            createdAt = DateTimeOffset.UnixEpoch,
            producer = new { stageId = "core-adapter", workerId = "orchestrator.core-adapter", workerVersion = "1.0.0", attemptId = "adapter-" + artifactId },
            inputRefs = new[] { source }, dataClass = "personal", metadata = new { entrypoint = payloadRelative, compatibilityBoundary = "core-v3" }
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, manifestRelative), manifestBytes, cancellationToken).ConfigureAwait(false);
        return new LegacyArtifactReference(artifactId, "card-bundle", "1.0.0", manifestRelative, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
    }

    private static Task PublishTaskManifestAsync(
        string jobRoot,
        string taskId,
        string operation,
        IReadOnlyList<LegacyArtifactReference> initialInputs,
        IReadOnlyList<string> requestedOutputs,
        CancellationToken cancellationToken)
    {
        var manifest = new
        {
            schemaVersion = "1.0.0",
            taskId,
            createdAt = DateTimeOffset.UnixEpoch,
            createdBy = new { application = "KnowledgeClipper.Core.V3", version = "3.0.0" },
            intent = "clip.card",
            executionProfile = "cards-anki",
            inputRefs = initialInputs.Select(item => new { item.ArtifactRef, item.Kind, item.ContractVersion, item.ExpectedSha256 }).ToArray(),
            requestedOutputs,
            initialPlanRef = "plans/core-v3.v1.json",
            retentionClass = "evidence",
            metadata = new { compatibilityBoundary = "core-v3", externalMutation = operation == "export" }
        };
        return WriteCreateOnceAsync(
            ResolveWithin(jobRoot, "task.manifest.v1.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, KernelJson.Options),
            cancellationToken);
    }

    private static JsonElement BuildContentInput(WorkerInvocation invocation)
    {
        var request = invocation.Request;
        var phase = GetString(request, "phase");
        JsonElement questionTurns;
        JsonElement answerAncestry;
        if (request.TryGetProperty("legacyQuestionWorkflow", out var workflow))
        {
            questionTurns = workflow.GetProperty("questionTurns").Clone();
            answerAncestry = workflow.GetProperty("answerAncestry").Clone();
        }
        else
        {
            var question = phase == "make-cards" ? GetString(request, "question") : GetString(request, "prompt");
            questionTurns = JsonSerializer.SerializeToElement(new[] { new { turnId = "turn-0", turnIndex = 0, question } }, KernelJson.Options);
            answerAncestry = phase == "make-cards"
                ? JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        answerId = "answer-0", artifactRef = "core-answer", turnId = "turn-0", turnIndex = 0,
                        questionSha256 = Hash(question), answerText = GetString(request, "answer"), answerTextSha256 = Hash(GetString(request, "answer"))
                    }
                }, KernelJson.Options)
                : JsonSerializer.SerializeToElement(Array.Empty<object>(), KernelJson.Options);
        }
        return StrictJson.CloneElement(new
        {
            schema_version = "standard-input/3.0.0",
            package_id = "core-v3-question",
            task_id = Token("question-" + invocation.OperationId),
            input_version = "1",
            mode = "question",
            questionWorkflow = new { schemaVersion = "question-workflow-input/1.0.0", phase = phase == "make-cards" ? "make-cards" : "answer", questionTurns, answerAncestry },
            items = new[] { new { item_id = "item-question", source = new { kind = "question_workflow" }, hints = new { requested_templates = new[] { "basic_qa" } } } }
        });
    }

    private static JsonElement BuildConfigSnapshot(string workerId, string operation, IReadOnlyList<LegacyArtifactReference> inputs)
        => workerId == "export.anki"
            ? StrictJson.CloneElement(new { ankiConfigArtifactId = inputs.Single(item => item.Kind == "anki-export-config").ArtifactRef })
            : StrictJson.CloneElement(new
            {
                productId = "knowledge-clipper", productDisplayName = "Knowledge Clipper", checkpointId = "core-v3-worker-process",
                promptSet = "open-md-v3", categoryRegistry = "category-registry/2.0.0", destinationNeutral = true
            });

    private static JsonElement BuildPayload(string workerId, JsonElement request)
    {
        if (workerId == "content.deepseek")
        {
            return StrictJson.CloneElement(new { schemaVersion = "content-run/1.0.0", contentInputPath = "inputs/content/standard-input.v3.json", providerMode = "live" });
        }
        return request.TryGetProperty("sideEffectAuthorization", out var authorization)
            ? StrictJson.CloneElement(new { sideEffectAuthorization = authorization })
            : StrictJson.CloneElement(new { });
    }

    private static async Task<LegacyArtifactReference> PublishJsonArtifactAsync(
        string jobRoot,
        string artifactId,
        string kind,
        string fileName,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        var relativeRoot = kind == "content-input" ? "inputs/content" : "artifacts/" + artifactId;
        var payloadRelative = relativeRoot + "/" + fileName;
        var manifestRelative = relativeRoot + "/artifact.json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, payloadRelative), bytes, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifest = new
        {
            schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "application/json",
            files = new[] { new { path = payloadRelative, sha256 = digest, bytes = bytes.LongLength, mediaType = "application/json" } },
            createdAt = DateTimeOffset.UnixEpoch,
            producer = new { stageId = "core-v3-adapter", workerId = "core-v3", workerVersion = "3.2.0", attemptId = "adapter-" + artifactId },
            inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = payloadRelative }
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, manifestRelative), manifestBytes, cancellationToken).ConfigureAwait(false);
        return new LegacyArtifactReference(artifactId, kind, "1.0.0", manifestRelative, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
    }

    private static async Task WriteCreateOnceAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
            {
                throw new InvalidDataException($"Legacy task-package create-once file drifted: {path}");
            }
        }
    }

    internal static string ResolveWithin(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(item => item is "" or "." or ".."))
        {
            throw new InvalidDataException("Legacy task-package path is not canonical.");
        }
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var boundary = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Legacy task-package path escapes its root.");
        return full;
    }

    private static string Token(string value)
    {
        var normalized = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-').ToArray());
        return normalized.Length <= 120 ? normalized : normalized[..95] + "-" + Hash(value)[..24];
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Core worker invocation requires string '{name}'.");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
