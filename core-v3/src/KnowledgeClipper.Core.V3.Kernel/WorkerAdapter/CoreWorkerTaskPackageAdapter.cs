using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed partial class CoreWorkerTaskPackageAdapter
{
    private sealed record PreparedInputs(
        IReadOnlyList<LegacyArtifactReference> RequestInputs,
        IReadOnlyList<LegacyArtifactReference> InitialInputs);

    private readonly string _root;
    private readonly AuthorityRuntimeTools? _runtimeTools;

    public CoreWorkerTaskPackageAdapter(string root, AuthorityRuntimeTools? runtimeTools = null)
    {
        _root = Path.GetFullPath(root);
        _runtimeTools = runtimeTools;
        Directory.CreateDirectory(_root);
    }

    public async Task<LegacyAttemptPackage> PrepareAsync(
        WorkerInvocation invocation,
        AdmittedAuthorityWorker worker,
        CancellationToken cancellationToken)
    {
        // Each legacy worker owns an immutable jobs/<taskId> package.  Speech
        // synth/playback share their package for the audio handoff; independent
        // Content and Anki packages avoid cross-worker manifest mutation.
        // Legacy worker layouts place task/run/stage/attempt in every physical
        // request path.  Keep those package identifiers short and content-bound:
        // the full Core operation identity remains in WorkerRequest.operationId
        // and the immutable Core event stream, while these path segments are
        // stable across a retry of the same operation/worker pair.
        var phase = invocation.Request.GetProperty("phase").GetString()!;
        var isLanguageResume = worker.WorkerId == "content.deepseek" && invocation.Request.TryGetProperty("languageDecision", out _);
        if (isLanguageResume && (!Version.TryParse(worker.WorkerVersion, out var contentVersion) || contentVersion < new Version(0, 12, 0)))
            throw new InvalidDataException("Decision-bound Content resume requires Content 0.12+; independent reclassification is forbidden.");
        // A question operation invokes Content once for the answer and again
        // for its destination-neutral draft.  Each immutable legacy package
        // owns a single standard-input path, so bind Content package identity
        // to phase while retaining the public Core operation identity in every
        // request/event. Speech intentionally keeps one shared package for its
        // synth -> playback artifact handoff.
        var packageIdentity = worker.WorkerId == "content.deepseek" ? invocation.OperationId + "\n" + phase + "\n" + (invocation.Request.TryGetProperty("inputVersion", out var inputVersionIdentity) ? inputVersionIdentity.GetString() : "1") : invocation.OperationId;
        if (phase == "export" && invocation.Request.TryGetProperty("destination", out var destinationIdentity))
            packageIdentity += "\n" + destinationIdentity.GetString() + "\n" + GetString(invocation.Request, "cardBundleSha256");
        var initialPackageIdentity = isLanguageResume
            ? invocation.OperationId + "\nreview\n" + GetString(invocation.Request.GetProperty("languageDecision"), "inputVersion")
            : packageIdentity;
        var taskId = "task-" + Hash(initialPackageIdentity)[..24];
        var runId = "run-" + Hash(packageIdentity)[..24];
        var attemptId = "attempt-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worker.WorkerId + "\n" + invocation.Request.GetProperty("phase").GetString() + "\n" + invocation.Request.GetProperty("idempotencyKey").GetString())))[..20].ToLowerInvariant();
        var stageId = ResolveStage(worker.WorkerId, invocation.Request);
        var legacyOperation = ResolveOperation(worker.WorkerId, invocation.Request);
        var jobRoot = ResolveWithin(_root, "jobs/" + taskId);
        Directory.CreateDirectory(jobRoot);
        var operationRoots = invocation.Request.TryGetProperty("originalText", out _) && HasAnkiConfiguration(invocation.Request)
            ? await EnsureOperationRootsAsync(jobRoot, invocation, cancellationToken).ConfigureAwait(false)
            : null;

        var preparedInputs = worker.WorkerId switch
        {
            "export.remnote" when phase == "export" => await PublishRemNoteInputsAsync(jobRoot, runId, attemptId, invocation.Request, cancellationToken).ConfigureAwait(false),
            "export.anki" when legacyOperation == "probe" => SingleInitial(await PublishJsonArtifactAsync(
                jobRoot, "anki-config-" + attemptId[8..], "anki-export-config", "anki-export.v1.json",
                GetConfiguration(invocation.Request), cancellationToken).ConfigureAwait(false)),
            "export.anki" when legacyOperation == "export" => await PublishAnkiExportInputsAsync(
                jobRoot, runId, attemptId, invocation.Request,
                operationRoots is { Count: 2 } ? operationRoots[0] : throw new InvalidDataException("Read-to-Anki export is missing its shared operation capture root."),
                operationRoots is { Count: 2 } ? operationRoots[1] : throw new InvalidDataException("Read-to-Anki export is missing its shared operation configuration root."), cancellationToken).ConfigureAwait(false),
            "content.deepseek" when isLanguageResume => SingleInitial(await PublishLanguageResumeInputAsync(jobRoot, runId, attemptId, invocation, worker, cancellationToken).ConfigureAwait(false)),
            "content.deepseek" when GetString(invocation.Request, "phase") == "review" => SingleInitial(await PublishContentCardInputAsync(
                jobRoot, taskId, attemptId, GetString(invocation.Request, "originalText"),
                invocation.Request.TryGetProperty("languageChoice", out var languageChoice) ? languageChoice.GetString() : null,
                invocation.Request.TryGetProperty("languageSelection", out var languageSelection) ? languageSelection : (JsonElement?)null,
                invocation.Request.TryGetProperty("humanResolution", out var humanResolution) ? humanResolution : (JsonElement?)null,
                invocation.Request.TryGetProperty("inputVersion", out var inputVersion) ? inputVersion.GetString() ?? "1" : "1",
                operationRoots is { Count: 2 } ? operationRoots[0] : null,
                cancellationToken).ConfigureAwait(false)),
            "content.deepseek" => SingleInitial(await PublishJsonArtifactAsync(
                jobRoot, "content-input-" + attemptId[8..], "content-input", "standard-input.v3.json",
                BuildContentInput(invocation), cancellationToken).ConfigureAwait(false)),
            "speech.edge" when legacyOperation == "speech.synthesize" => SingleInitial(await PublishTextArtifactAsync(
                jobRoot, "spoken-text-" + attemptId[8..], "text.spoken", "content.txt", GetString(invocation.Request, "spokenText"), cancellationToken).ConfigureAwait(false)),
            "speech.edge" when legacyOperation == "speech.playback" => SingleInitial(ReadLegacyArtifact(invocation.Request, "audioArtifact")),
            _ => throw new InvalidDataException($"Unsupported Core worker adapter route '{worker.WorkerId}:{legacyOperation}'.")
        };
        if (operationRoots is not null) preparedInputs = new PreparedInputs(preparedInputs.RequestInputs, operationRoots);
        var inputs = preparedInputs.RequestInputs;

        if (File.Exists(ResolveWithin(jobRoot, "task.manifest.v1.json")))
        {
            // Immutable shared operation package: every stage observes the same
            // manifest and only appends its own run/stage/attempt evidence.
        }
        else
        {
            await PublishTaskManifestAsync(
                jobRoot,
                taskId,
                worker.WorkerId == "export.remnote" ? "cards-remnote" : "cards-anki",
                preparedInputs.InitialInputs,
                worker.WorkerId == "export.remnote" ? new[] { "remnote.write-receipt" } : new[] { "audio.speech", "learning-bundle", "card-bundle", "anki-export-evidence" },
                cancellationToken).ConfigureAwait(false);
        }

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
            worker.WorkerId == "export.remnote" ? StrictJson.CloneElement(new { }) : BuildConfigSnapshot(worker.WorkerId, legacyOperation, inputs),
            isLanguageResume
                ? StrictJson.CloneElement(new { schemaVersion = "content-run/1.0.0", contentInputPath = LanguageResumeInputPath(runId, attemptId), providerMode = "live" })
                : BuildPayload(worker.WorkerId, invocation.Request));
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
        => workerId == "content.deepseek" ? "content.understand-and-card"
            : workerId == "export.remnote" ? "export-remnote"
            : workerId == "speech.edge" ? GetString(request, "phase") == "synthesize" ? "speech-synthesize" : "speech-playback"
            : GetString(request, "phase") == "probe" ? "anki-probe" : "anki-export";

    private static string ResolveOperation(string workerId, JsonElement request)
        => workerId == "speech.edge"
            ? GetString(request, "phase") == "synthesize" ? "speech.synthesize" : "speech.playback"
            : workerId == "content.deepseek"
            ? GetString(request, "phase") switch
            {
                "review" => "understand-and-card",
                "make-cards" => "question.make-cards",
                _ => "question.answer"
            }
            : workerId == "export.remnote" ? "remnote.export"
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

    private static bool HasAnkiConfiguration(JsonElement request)
    {
        if (!request.TryGetProperty("configurationJson", out var json) || json.ValueKind != JsonValueKind.String) return false;
        using var parsed = StrictJson.Parse(Encoding.UTF8.GetBytes(json.GetString()!), "operation configuration");
        return parsed.RootElement.TryGetProperty("schema_version", out var schema) && schema.GetString() == "anki-export-config.v1";
    }

    private static PreparedInputs SingleInitial(LegacyArtifactReference artifact)
        => new(new[] { artifact }, new[] { artifact });

    private static async Task<IReadOnlyList<LegacyArtifactReference>> EnsureOperationRootsAsync(string jobRoot, WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        var originalText = GetString(invocation.Request, "originalText");
        var configuration = GetConfiguration(invocation.Request);
        var operationHash = Hash(invocation.OperationId)[..24];
        var capture = await PublishTextArtifactAsync(jobRoot, "capture-" + operationHash, "capture-text", "content.txt", originalText, cancellationToken).ConfigureAwait(false);
        var config = await PublishJsonArtifactAsync(jobRoot, "anki-config-" + operationHash, "anki-export-config", "anki-export.v1.json", configuration, cancellationToken).ConfigureAwait(false);
        return new[] { capture, config };
    }

    private static async Task<PreparedInputs> PublishAnkiExportInputsAsync(
        string jobRoot,
        string runId,
        string attemptId,
        JsonElement request,
        LegacyArtifactReference capture,
        LegacyArtifactReference config,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("sideEffectAuthorization", out var authorization) || authorization.ValueKind != JsonValueKind.Object ||
            authorization.GetProperty("mode").GetString() != "explicit-user-action" || authorization.GetProperty("scope").GetString() != "isolated-canary-deck")
        {
            throw new InvalidDataException("Real Anki export requires explicit isolated-canary-deck authorization.");
        }
        var cards = request.TryGetProperty("cards", out var requestedCards)
            ? requestedCards.EnumerateArray().ToArray()
            : new[] { request.GetProperty("card") };
        var routeKey = request.TryGetProperty("selectedRoute", out var selectedRoute) && selectedRoute.ValueKind == JsonValueKind.String ? selectedRoute.GetString()! : "canary";
        if (cards.Length is < 1 or > 2) throw new InvalidDataException("Read-to-Anki export requires one or two cards.");
        var projectedCards = cards.Select(card =>
        {
            var front = GetString(card, "front");
            var back = GetString(card, "back");
            var template = card.TryGetProperty("template", out var templateValue) ? templateValue.GetString() ?? "basic_qa" : "basic_qa";
            if (template is not ("basic_qa" or "language_context" or "language_writing")) throw new InvalidDataException("Unsupported Content card template.");
            var cardId = card.TryGetProperty("cardId", out var requestedCardId) && requestedCardId.ValueKind == JsonValueKind.String
                ? Token(requestedCardId.GetString()!)
                : "card-" + Hash(template + "\n" + front + "\n" + back)[..24];
            var noteType = template == "language_context" ? "language_item" : template;
            object fields = template switch
            {
                "language_context" => new { context_sentence = front, reading_and_meaning = back },
                "language_writing" => new { front, back },
                _ => new { question = front, answer = back, source = "Core v3 isolated Canary" }
            };
            return new
            {
                card_id = cardId, revision = 1,
                content_hash = ComputeAnkiContentHash(cardId, 1, routeKey, noteType, front, back, "Core v3 isolated Canary", new[] { "core_v3_canary" }),
                status = "ready", deck_route_key = routeKey, note_type_key = noteType,
                fields,
                tags = new[] { "core_v3_canary" }, media_refs = Array.Empty<object>()
            };
        }).ToArray();
        var bundle = StrictJson.CloneElement(new
        {
            bundle_kind = "CardBundle",
            schema_version = "1.0",
            bundle_id = "bundle-" + attemptId[8..],
            cards = projectedCards,
            media = Array.Empty<object>()
        });
        var cardReference = await PublishDerivedCardArtifactAsync(
            jobRoot,
            runId,
            "card-bundle-" + attemptId[8..],
            bundle,
            capture,
            cancellationToken).ConfigureAwait(false);
        return new PreparedInputs(new[] { cardReference, config }, new[] { capture, config });
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
        string executionProfile,
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
            executionProfile,
            inputRefs = initialInputs.Select(item => new { item.ArtifactRef, item.Kind, item.ContractVersion, item.ExpectedSha256 }).ToArray(),
            requestedOutputs,
            initialPlanRef = "plans/core-v3.v1.json",
            retentionClass = "evidence",
            metadata = new { compatibilityBoundary = "core-v3", externalMutation = true }
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

    private static async Task<LegacyArtifactReference> PublishContentCardInputAsync(string jobRoot, string taskId, string attemptId, string originalText, string? languageChoice, JsonElement? languageSelection, JsonElement? humanResolution, string inputVersion, LegacyArtifactReference? captureRoot, CancellationToken cancellationToken)
    {
        var inputPath = "inputs/content/standard-input.v1.json";
        var materialPath = "inputs/content/material.txt";
        var isLanguage = languageChoice is "meaning-reading" or "writing" or "both";
        var requestedTemplates = languageChoice switch
        {
            "meaning-reading" => new[] { "language_context" },
            "writing" => new[] { "language_writing" },
            "both" => new[] { "language_context", "language_writing" },
            _ => new[] { "basic_qa" }
        };
        var extraFiles = new List<object>();
        JsonElement input;
        if ((isLanguage || languageChoice == "ask") && languageSelection is { ValueKind: JsonValueKind.Object } selection)
        {
            var context = new List<object>();
            var selected = GetString(selection, "selectedText");
            var target = GetString(selection, "targetText");
            if (selected != originalText || Hash(selected) != GetString(selection, "selectedTextSha256") ||
                Hash(target) != GetString(selection, "targetTextSha256") || !selected.Contains(target, StringComparison.Ordinal))
                throw new InvalidDataException("Content selection source or target SHA drifted before package publication.");
            var prefix = new StringBuilder();
            var suffix = new StringBuilder();
            var suffixStarted = false;
            var expectedOrder = 0;
            var segmentIds = new HashSet<string>(StringComparer.Ordinal);
            var contextApplied = selection.GetProperty("contextApplied").GetBoolean();
            foreach (var segment in selection.GetProperty("contextSegments").EnumerateArray())
            {
                var order = segment.GetProperty("order").GetInt32();
                var role = GetString(segment, "role");
                if (!contextApplied || order != expectedOrder++ || !segmentIds.Add(GetString(segment, "segmentId")) ||
                    role is not ("prefix" or "suffix") || (suffixStarted && role == "prefix"))
                    throw new InvalidDataException("Content context role/order is invalid before package publication.");
                suffixStarted |= role == "suffix";
                (role == "prefix" ? prefix : suffix).Append(GetString(segment, "text"));
                var path = $"inputs/content/context/{order:D4}.txt";
                var bytes = Encoding.UTF8.GetBytes(GetString(segment, "text"));
                await WriteCreateOnceAsync(ResolveWithin(jobRoot, path), bytes, cancellationToken).ConfigureAwait(false);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (sha != GetString(segment, "sha256")) throw new InvalidDataException("Context segment SHA drifted before Content package publication.");
                extraFiles.Add(new { path, sha256 = sha, bytes = bytes.LongLength, mediaType = "text/plain" });
            }
            var completeSource = prefix.ToString() + selected + suffix.ToString();
            var completeSha = Hash(completeSource);
            if (completeSha != GetString(selection, "spokenTextSha256") || (contextApplied && expectedOrder == 0))
                throw new InvalidDataException("Content complete context does not match the frozen source composition.");
            // Content v6 context_segments are complete source passages, not the
            // Core prefix/suffix roles. A single passage preserves source bytes
            // without the worker's inter-segment paragraph separator.
            if (contextApplied)
            {
                const string completePath = "inputs/content/context/complete.txt";
                var completeBytes = Encoding.UTF8.GetBytes(completeSource);
                await WriteCreateOnceAsync(ResolveWithin(jobRoot, completePath), completeBytes, cancellationToken).ConfigureAwait(false);
                context.Add(new { segment_id = "context-" + completeSha[..24], order = 0, material_path = completePath, sha256 = completeSha });
                extraFiles.Add(new { path = completePath, sha256 = completeSha, bytes = completeBytes.LongLength, mediaType = "text/plain" });
            }
            const string provenancePath = "inputs/content/context/source-provenance.json";
            var provenanceBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                captureRoot,
                sourceSelection = selection,
                composition = "prefix+selected+suffix",
                completeSourceSha256 = completeSha,
                selectedStartUtf16 = prefix.Length,
                selectedEndUtf16 = prefix.Length + selected.Length
            }, KernelJson.Options);
            await WriteCreateOnceAsync(ResolveWithin(jobRoot, provenancePath), provenanceBytes, cancellationToken).ConfigureAwait(false);
            extraFiles.Add(new { path = provenancePath, sha256 = Convert.ToHexString(SHA256.HashData(provenanceBytes)).ToLowerInvariant(), bytes = provenanceBytes.LongLength, mediaType = "application/json" });
            var itemOverrides = humanResolution is { ValueKind: JsonValueKind.Object } resolution ? new { languageTargetResolution = resolution } : null;
            var resolutionSchema = humanResolution is { ValueKind: JsonValueKind.Object } resolutionValue &&
                GetString(resolutionValue, "schema_version") == "language-target-resolution/2.0.0"
                    ? "standard-input/7.0.0"
                    : "standard-input/6.0.0";
            // A language preference is not a semantic category or a template
            // override. Content resolves it only after classifying the source.
            // In ask mode, omit the preference so its real decision owns the menu.
            var item = new Dictionary<string, object?>
            {
                ["item_id"] = "item-source", ["source"] = new { kind = "text", material_path = materialPath },
                ["language_selection"] = new { schema_version = "language-selection/1.0.0", selected_text = selected,
                    selected_text_sha256 = Hash(selected), target_text = target, target_text_sha256 = Hash(target),
                    context_enabled = contextApplied, context_segments = context }
            };
            if (itemOverrides is not null) item["human_overrides"] = itemOverrides;
            var document = new Dictionary<string, object?>
            {
                ["schema_version"] = resolutionSchema, ["package_id"] = "core-v3-read-aloud-" + taskId[5..],
                ["task_id"] = taskId, ["input_version"] = inputVersion, ["mode"] = "card", ["items"] = new[] { item }
            };
            if (isLanguage)
            {
                document["languagePracticeMode"] = languageChoice;
                document["human_overrides"] = new { languagePracticeMode = languageChoice };
            }
            input = StrictJson.CloneElement(document);
        }
        else input = isLanguage
            ? StrictJson.CloneElement(new
            {
                schema_version = "standard-input/2.0.0", package_id = "core-v3-read-aloud-" + taskId[5..], task_id = taskId, input_version = "1", mode = "card",
                human_overrides = new { languagePracticeMode = languageChoice },
                items = new[] { new { item_id = "item-source", source = new { kind = "text", material_path = materialPath }, hints = new { requested_templates = requestedTemplates } } }
            })
            : StrictJson.CloneElement(new
            {
                schema_version = "standard-input/1.0.0", package_id = "core-v3-read-aloud-" + taskId[5..], task_id = taskId, input_version = "1", mode = "card",
                items = new[] { new { item_id = "item-source", source = new { kind = "text", material_path = materialPath }, hints = new { requested_templates = requestedTemplates } } }
            });
        var inputBytes = JsonSerializer.SerializeToUtf8Bytes(input, KernelJson.Options);
        var materialBytes = Encoding.UTF8.GetBytes(originalText);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, inputPath), inputBytes, cancellationToken).ConfigureAwait(false);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, materialPath), materialBytes, cancellationToken).ConfigureAwait(false);
        var files = new List<object> {
            new { path = inputPath, sha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant(), bytes = inputBytes.LongLength, mediaType = "application/json" },
            new { path = materialPath, sha256 = Convert.ToHexString(SHA256.HashData(materialBytes)).ToLowerInvariant(), bytes = materialBytes.LongLength, mediaType = "text/plain" }
        }; files.AddRange(extraFiles);
        var manifest = new { schemaVersion = "1.0.0", artifactId = "content-input-" + attemptId[8..], kind = "content-input", contractVersion = "1.0.0", mediaType = "application/json", files,
            createdAt = DateTimeOffset.UnixEpoch, producer = new { stageId = "core-v3-adapter", workerId = "core-v3", workerVersion = "3.2.0", attemptId = "adapter-" + attemptId }, inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = inputPath } };
        var manifestRelative = "inputs/content/artifact.json";
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, manifestRelative), manifestBytes, cancellationToken).ConfigureAwait(false);
        return new LegacyArtifactReference("content-input-" + attemptId[8..], "content-input", "1.0.0", manifestRelative, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
    }

    private static string LanguageResumeInputPath(string runId, string attemptId)
        => $"runs/{runId}/adapters/content-input/content-input-{attemptId[8..]}/v0001/standard-input.v8.json";

    private static async Task<LegacyArtifactReference> PublishLanguageResumeInputAsync(
        string jobRoot, string runId, string attemptId, WorkerInvocation invocation, AdmittedAuthorityWorker worker, CancellationToken cancellationToken)
    {
        static void Check(bool condition, string reason) { if (!condition) throw new InvalidDataException("Language resume " + reason); }
        var proof = invocation.Request.GetProperty("languageDecision");
        Check(GetString(proof, "sourceOperationId") == invocation.OperationId &&
            Path.GetFullPath(GetString(proof, "sourceJobRoot")).Equals(Path.GetFullPath(jobRoot), StringComparison.OrdinalIgnoreCase), "cross-operation/job source is forbidden.");
        async Task<JsonElement> ReadBound(string relative, string expected)
        {
            var bytes = await File.ReadAllBytesAsync(ResolveWithin(jobRoot, relative), cancellationToken).ConfigureAwait(false);
            Check(Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase), "SHA mismatch: " + relative);
            using var document = StrictJson.Parse(bytes, relative);
            return document.RootElement.Clone();
        }
        var previousRequestJson = await ReadBound(GetString(proof, "sourceRequestPath"), GetString(proof, "sourceRequestSha256")).ConfigureAwait(false);
        var previous = previousRequestJson.Deserialize<LegacyWorkerRequest>(KernelJson.Options) ?? throw new InvalidDataException("Language resume request missing.");
        var previousResultJson = await ReadBound(GetString(proof, "sourceResultPath"), GetString(proof, "sourceResultSha256")).ConfigureAwait(false);
        var previousResult = previousResultJson.Deserialize<LegacyWorkerResult>(KernelJson.Options) ?? throw new InvalidDataException("Language resume result missing.");
        var canonical = $"runs/{previous.RunId}/stages/{previous.StageId}/attempts/{previous.AttemptId}";
        Check(previous.OperationId == invocation.OperationId && previous.TaskId == Path.GetFileName(jobRoot) && previous.WorkerId == worker.WorkerId &&
            previous.AttemptPath == canonical && GetString(proof, "sourceRequestPath") == canonical + "/request.json" && previous.ResultPath == canonical + "/result.json" &&
            GetString(proof, "sourceResultPath") == previous.ResultPath && previousResult.Status == "completed" && previousResult.Error is null &&
            previousResult.RequestId == previous.RequestId && previousResult.OperationId == previous.OperationId && previousResult.TaskId == previous.TaskId &&
            previousResult.RunId == previous.RunId && previousResult.StageId == previous.StageId && previousResult.AttemptId == previous.AttemptId &&
            previousResult.IdempotencyKey.Equals(previous.IdempotencyKey, StringComparison.OrdinalIgnoreCase) &&
            previousResult.Worker.Id == worker.WorkerId && previousResult.Worker.Version == worker.WorkerVersion &&
            previousResult.Payload is { ValueKind: JsonValueKind.Object } payload && GetString(payload, "moduleReleaseState") == "unresolved",
            "producer request/result/terminal identity drifted.");
        var originalRef = ReadLegacyArtifact(proof, "inputReference");
        var learningRef = ReadLegacyArtifact(proof, "learningReference");
        var decisionRef = ReadLegacyArtifact(proof, "decisionReference");
        Check(previous.InputRefs.Contains(originalRef) && previousResult.Outputs.Contains(learningRef) && previousResult.Outputs.Contains(decisionRef), "references are not owned by the prior attempt.");
        var allFiles = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        async Task<(JsonElement Document, object ExtendedRef)> ReadArtifact(LegacyArtifactReference reference, string kind, bool produced)
        {
            Check(reference.Kind == kind, "artifact kind drifted.");
            var manifest = await ReadBound(reference.ManifestPath, reference.ExpectedSha256).ConfigureAwait(false);
            Check(GetString(manifest, "artifactId") == reference.ArtifactRef && GetString(manifest, "kind") == kind && GetString(manifest, "contractVersion") == reference.ContractVersion, "manifest identity drifted.");
            if (produced)
            {
                var producer = manifest.GetProperty("producer");
                Check(GetString(producer, "workerId") == worker.WorkerId && GetString(producer, "workerVersion") == worker.WorkerVersion &&
                    GetString(producer, "stageId") == previous.StageId && GetString(producer, "attemptId") == previous.AttemptId && reference.ManifestPath.StartsWith(canonical + "/outputs/", StringComparison.Ordinal), "artifact producer drifted.");
            }
            var files = manifest.GetProperty("files").EnumerateArray().ToArray();
            var entrypoint = manifest.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("entrypoint", out var entry)
                ? entry.GetString()! : files.Length == 1 ? GetString(files[0], "path") : throw new InvalidDataException("Language resume ambiguous entrypoint.");
            foreach (var file in files)
            {
                var filePath = GetString(file, "path");
                var bytes = await File.ReadAllBytesAsync(ResolveWithin(jobRoot, filePath), cancellationToken).ConfigureAwait(false);
                Check(bytes.LongLength == file.GetProperty("bytes").GetInt64() && Convert.ToHexString(SHA256.HashData(bytes)).Equals(GetString(file, "sha256"), StringComparison.OrdinalIgnoreCase), "artifact payload drifted.");
                if (allFiles.TryGetValue(filePath, out var existing)) Check(existing.GetRawText() == file.GetRawText(), "conflicting payload declarations.");
                else allFiles.Add(filePath, file.Clone());
            }
            var payloadFile = files.Single(file => GetString(file, "path") == entrypoint);
            var document = await ReadBound(entrypoint, GetString(payloadFile, "sha256")).ConfigureAwait(false);
            return (document, new { artifactRef = reference.ArtifactRef, kind = reference.Kind, contractVersion = reference.ContractVersion,
                manifestPath = reference.ManifestPath, expectedSha256 = reference.ExpectedSha256, payload_path = entrypoint, payload_sha256 = GetString(payloadFile, "sha256") });
        }
        var original = await ReadArtifact(originalRef, "content-input", false).ConfigureAwait(false);
        var learning = await ReadArtifact(learningRef, "learning-bundle", true).ConfigureAwait(false);
        var decision = await ReadArtifact(decisionRef, "language-practice-decision", true).ConfigureAwait(false);
        Check(decision.Document.GetProperty("decisions").GetArrayLength() == 1 &&
            GetString(decision.Document.GetProperty("decisions")[0], "category_id") == GetString(proof, "categoryId") &&
            JsonSerializer.Serialize(decision.Document.GetProperty("decisions")[0], KernelJson.Options) == JsonSerializer.Serialize(proof.GetProperty("decision"), KernelJson.Options),
            "durable decision differs from its producer artifact.");
        Check(GetString(original.Document, "task_id") == previous.TaskId && GetString(original.Document, "input_version") == GetString(proof, "inputVersion") &&
            GetString(invocation.Request, "inputVersion") != GetString(proof, "inputVersion") && GetString(learning.Document, "release_state") == "unresolved" &&
            GetString(decision.Document, "state") == "requires_human", "input version/release state drifted.");
        var sourceSelection = original.Document.GetProperty("items")[0].GetProperty("language_selection");
        var currentSelection = invocation.Request.GetProperty("languageSelection");
        const string provenancePath = "inputs/content/context/source-provenance.json";
        Check(allFiles.TryGetValue(provenancePath, out var provenanceFile), "source provenance is missing.");
        var provenance = await ReadBound(provenancePath, GetString(provenanceFile, "sha256")).ConfigureAwait(false);
        Check(JsonSerializer.Serialize(provenance.GetProperty("sourceSelection"), KernelJson.Options) == JsonSerializer.Serialize(currentSelection, KernelJson.Options),
            "ordered source context differs from the frozen decision input.");
        Check(GetString(sourceSelection, "selected_text") == GetString(invocation.Request, "originalText") &&
            GetString(sourceSelection, "selected_text_sha256") == GetString(currentSelection, "selectedTextSha256") &&
            GetString(sourceSelection, "target_text_sha256") == GetString(currentSelection, "targetTextSha256") &&
            GetString(proof, "selectedTextSha256") == GetString(currentSelection, "selectedTextSha256") &&
            GetString(proof, "targetTextSha256") == GetString(currentSelection, "targetTextSha256"), "selected/target source drifted.");
        var mode = GetString(invocation.Request, "languageChoice");
        Check(mode is "meaning-reading" or "writing" or "both", "mode is invalid.");
        var inputPath = LanguageResumeInputPath(runId, attemptId);
        var value = System.Text.Json.Nodes.JsonNode.Parse(original.Document.GetRawText())!.AsObject();
        value["schema_version"] = "standard-input/8.0.0";
        value["input_version"] = GetString(invocation.Request, "inputVersion");
        value["languagePracticeMode"] = mode;
        value["human_overrides"] = JsonSerializer.SerializeToNode(new { languagePracticeMode = mode }, KernelJson.Options);
        value["language_practice_resume"] = JsonSerializer.SerializeToNode(new { schema_version = "language-practice-resume/1.0.0", input_ref = original.ExtendedRef,
            learning_ref = learning.ExtendedRef, decision_ref = decision.ExtendedRef }, KernelJson.Options);
        var inputBytes = JsonSerializer.SerializeToUtf8Bytes(value, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, inputPath), inputBytes, cancellationToken).ConfigureAwait(false);
        allFiles.Add(inputPath, StrictJson.CloneElement(new { path = inputPath, sha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant(), bytes = inputBytes.LongLength, mediaType = "application/json" }));
        var artifactId = "content-input-" + attemptId[8..];
        var manifestPath = inputPath[..inputPath.LastIndexOf('/')] + "/artifact.json";
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "1.0.0", artifactId, kind = "content-input", contractVersion = "1.0.0", mediaType = "application/json",
            files = allFiles.Values.ToArray(), createdAt = DateTimeOffset.UnixEpoch,
            producer = new { stageId = "core-v3-adapter", workerId = "core-v3", workerVersion = "3.2.0", attemptId = "adapter-" + attemptId },
            inputRefs = new[] { originalRef, learningRef, decisionRef }, dataClass = "personal", metadata = new { entrypoint = inputPath } }, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, manifestPath), manifestBytes, cancellationToken).ConfigureAwait(false);
        return new LegacyArtifactReference(artifactId, "content-input", "1.0.0", manifestPath, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
    }

    private JsonElement BuildConfigSnapshot(string workerId, string operation, IReadOnlyList<LegacyArtifactReference> inputs)
        => workerId == "speech.edge"
            ? operation == "speech.synthesize"
                ? StrictJson.CloneElement(new { edgeTtsPath = RequireRuntimeTools().EdgeTtsPath })
                : StrictJson.CloneElement(new { ffplayPath = RequireRuntimeTools().FfplayPath })
            : workerId == "export.anki"
            ? StrictJson.CloneElement(new { ankiConfigArtifactId = inputs.Single(item => item.Kind == "anki-export-config").ArtifactRef })
            : StrictJson.CloneElement(new
            {
                productId = "knowledge-clipper", productDisplayName = "Knowledge Clipper", checkpointId = "core-v3-worker-process",
                promptSet = "open-md-v3", categoryRegistry = "category-registry/2.0.0", destinationNeutral = true
            });

    private AuthorityRuntimeTools RequireRuntimeTools()
    {
        return _runtimeTools ?? throw new InvalidDataException("Read-to-Anki speech runtime tools were not authority-admitted at startup.");
    }

    private static JsonElement BuildPayload(string workerId, JsonElement request)
    {
        if (workerId == "export.remnote") return BuildRemNotePayload(request);
        if (workerId == "speech.edge")
        {
            if (GetString(request, "phase") == "playback") return StrictJson.CloneElement(new { });
            var text = GetString(request, "spokenText");
            var spokenTextSha256 = GetString(request, "spokenTextSha256");
            if (!string.Equals(Hash(text), spokenTextSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Speech spokenText does not match its frozen SHA-256.");
            var contextMode = GetString(request, "contextMode");
            if (contextMode is not ("selection_only" or "selection_plus_sentence" or "selection_plus_paragraph"))
                throw new InvalidDataException("Speech contextMode is unsupported.");
            var artifactId = "spoken-text-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workerId + "\n" + GetString(request, "phase") + "\n" + GetString(request, "idempotencyKey"))))[..20].ToLowerInvariant();
            return StrictJson.CloneElement(new
            {
                schemaVersion = "1.0.0",
                requestId = "request-" + GetString(request, "idempotencyKey")[..24].ToLowerInvariant(),
                operationId = GetString(request, "operationId"),
                spokenText = new { artifactRef = artifactId, path = "artifacts/" + artifactId + "/content.txt", sha256 = Hash(text), bytes = Encoding.UTF8.GetByteCount(text), encoding = "utf-8", mediaType = "text/plain" },
                contextMode,
                language = new { strategy = "auto", @override = (string?)null },
                voiceSnapshot = new { provider = "edge-tts", voicesByLanguage = new Dictionary<string, string> { ["zh-CN"] = "zh-CN-XiaoxiaoNeural", ["en-US"] = "en-US-JennyNeural" }, ratePercent = 0, volumePercent = 100, pitchHz = 0 },
                // This is frozen common v1 semantics.  Playback itself remains
                // an explicit Core stage, but the synthesis payload must retain
                // autoPlay=true so the admitted Speech worker accepts it.
                playbackPolicy = new { autoPlay = true, activePlayback = "supersede", replay = "from_start", naturalCompletion = "idle_replayable", stopTerminal = "stopped" },
                cachePolicy = new { scope = "task", reuseSuccessfulAudio = true, crossTaskReuse = false }
            });
        }
        if (workerId == "content.deepseek")
        {
            return StrictJson.CloneElement(new { schemaVersion = "content-run/1.0.0", contentInputPath = GetString(request, "phase") == "review" ? "inputs/content/standard-input.v1.json" : "inputs/content/standard-input.v3.json", providerMode = "live" });
        }
        return request.TryGetProperty("sideEffectAuthorization", out var authorization)
            ? StrictJson.CloneElement(new
            {
                sideEffectAuthorization = authorization,
                expectedCardCount = request.TryGetProperty("cards", out var requestedCards) ? requestedCards.GetArrayLength() : 1,
                expectedCards = (request.TryGetProperty("cards", out requestedCards) ? requestedCards.EnumerateArray().ToArray() : new[] { request.GetProperty("card") })
                    .Select(card => new
                    {
                        cardId = card.TryGetProperty("cardId", out var id) && id.ValueKind == JsonValueKind.String
                            ? Token(id.GetString()!)
                            : "card-" + Hash((card.TryGetProperty("template", out var template) ? template.GetString() ?? "basic_qa" : "basic_qa") + "\n" + GetString(card, "front") + "\n" + GetString(card, "back"))[..24],
                        front = GetString(card, "front"), back = GetString(card, "back")
                    }).ToArray(),
                expectedDeck = GetConfiguration(request).GetProperty("deck_routes").GetProperty(request.TryGetProperty("selectedRoute", out var route) && route.ValueKind == JsonValueKind.String ? route.GetString()! : "canary").GetString()
            })
            : StrictJson.CloneElement(new { });
    }

    private static LegacyArtifactReference ReadLegacyArtifact(JsonElement request, string name)
        => request.GetProperty(name).Deserialize<LegacyArtifactReference>(KernelJson.Options)
           ?? throw new InvalidDataException($"Speech playback requires legacy artifact '{name}'.");

    private static async Task<LegacyArtifactReference> PublishTextArtifactAsync(
        string jobRoot,
        string artifactId,
        string kind,
        string fileName,
        string text,
        CancellationToken cancellationToken)
    {
        var relativeRoot = "artifacts/" + artifactId;
        var payloadRelative = relativeRoot + "/" + fileName;
        var manifestRelative = relativeRoot + "/artifact.json";
        var bytes = Encoding.UTF8.GetBytes(text);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, payloadRelative), bytes, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifest = new
        {
            schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "text/plain",
            files = new[] { new { path = payloadRelative, sha256 = digest, bytes = bytes.LongLength, mediaType = "text/plain" } },
            createdAt = DateTimeOffset.UnixEpoch,
            producer = new { stageId = "core-v3-adapter", workerId = "core-v3", workerVersion = "3.2.0", attemptId = "adapter-" + artifactId },
            inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = payloadRelative }
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, manifestRelative), manifestBytes, cancellationToken).ConfigureAwait(false);
        return new LegacyArtifactReference(artifactId, kind, "1.0.0", manifestRelative, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
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
            // Parallel, independent stages may materialize the same immutable
            // operation root.  A creator holds the file exclusively until its
            // write-through flush completes; wait briefly, then compare bytes.
            byte[]? existing = null;
            for (var attempt = 0; attempt < 20 && existing is null; attempt++)
            {
                try { existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
                catch (IOException) when (attempt < 19) { await Task.Delay(10, cancellationToken).ConfigureAwait(false); }
            }
            if (existing is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
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

    // Mirrors export.anki's documented canonical JSON hash: sorted object keys,
    // UTF-8 characters left unescaped, and media_refs empty for this text-only
    // route.  Keeping this here makes the adapter's CardBundle self-validating
    // before an explicit destination action reaches AnkiConnect.
    private static string ComputeAnkiContentHash(string cardId, int revision, string route, string noteType, string question, string answer, string source, IReadOnlyList<string> tags)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("card_id", cardId);
            writer.WriteString("deck_route_key", route);
            writer.WritePropertyName("fields"); writer.WriteStartObject();
            if (noteType == "language_item") { writer.WriteString("context_sentence", question); writer.WriteString("reading_and_meaning", answer); }
            else if (noteType == "language_writing") { writer.WriteString("back", answer); writer.WriteString("front", question); }
            else { writer.WriteString("answer", answer); writer.WriteString("question", question); writer.WriteString("source", source); }
            writer.WriteEndObject();
            writer.WritePropertyName("media_refs"); writer.WriteStartArray(); writer.WriteEndArray();
            writer.WriteString("note_type_key", noteType);
            writer.WriteNumber("revision", revision);
            writer.WritePropertyName("tags"); writer.WriteStartArray(); foreach (var tag in tags.OrderBy(item => item, StringComparer.Ordinal)) writer.WriteStringValue(tag); writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
