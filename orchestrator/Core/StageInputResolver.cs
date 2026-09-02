using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeClipper.Orchestrator.Core;

internal sealed record ResolvedStageInput(IReadOnlyList<ArtifactReference> InputRefs, JsonElement Payload, bool AddRequestIdentity);
internal sealed record ExtractedText(string Text, string ContextMode);

internal sealed class StageInputResolver
{
    public async Task<ResolvedStageInput> ResolveAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationToken cancellationToken)
    {
        var inputs = new List<ArtifactReference>(stage.InputRefs);
        foreach (var binding in stage.InputBindings ?? Array.Empty<StageOutputBinding>())
        {
            if (!completed.TryGetValue(binding.FromStageId, out var source))
                throw new OrchestratorDispatchException("binding.source_missing", $"Binding source stage is not complete: {binding.FromStageId}");
            if (source.Status != "completed")
                throw new OrchestratorDispatchException("binding.source_not_consumable", $"Binding source status is not consumable: {binding.FromStageId}/{source.Status}");
            var matching = source.Outputs.Where(item => item.Kind == binding.Kind).ToArray();
            if (matching.Length == 0 && binding.Required)
                throw new OrchestratorDispatchException("binding.required_output_missing", $"Required output {binding.Kind} is absent from {binding.FromStageId} ({source.Status}).");
            foreach (var reference in matching)
            {
                await CoreArtifactIO.ValidateReferenceAsync(jobRoot, reference, cancellationToken);
                inputs.Add(reference);
            }
        }

        foreach (var reference in inputs)
            await CoreArtifactIO.ValidateReferenceAsync(jobRoot, reference, cancellationToken);

        if (stage.InputAdapter is null)
            return new ResolvedStageInput(inputs, stage.Payload, false);

        return stage.InputAdapter.Type switch
        {
            "content-input.v1" => await BuildContentInputAsync(jobRoot, runId, taskId, stage, inputs, completed, cancellationToken),
            "content-input-language-choice.v2" => await BuildLanguagePracticeContentInputAsync(jobRoot, runId, taskId, stage, inputs, cancellationToken),
            "question-input.v3" => await BuildInitialQuestionInputAsync(jobRoot, runId, taskId, stage, inputs, cancellationToken),
            "question-resume-input.v3" => await BuildQuestionResumeInputAsync(jobRoot, runId, taskId, stage, inputs, cancellationToken),
            "ocr-human-resolution-input.v1" => await BuildOcrHumanResolutionInputAsync(jobRoot, runId, taskId, stage, inputs, cancellationToken),
            "text-spoken.v1" => await BuildSpokenTextAsync(jobRoot, runId, stage, inputs, completed, cancellationToken),
            "content-to-remnote.v1" => await BuildRemNoteProjectionAsync(jobRoot, runId, stage, inputs, cancellationToken),
            "content-to-anki.v1" => await BuildAnkiProjectionAsync(jobRoot, runId, stage, inputs, cancellationToken),
            "ordered-original-images.v1" => await BuildOrderedOriginalImagesAsync(jobRoot, runId, taskId, stage, inputs, completed, cancellationToken),
            "kind-alias.v1" => await BuildAliasAsync(jobRoot, runId, stage, inputs, cancellationToken),
            _ => throw new OrchestratorDispatchException("binding.adapter_unknown", $"Unknown stage input adapter: {stage.InputAdapter.Type}")
        };
    }

    private static async Task<ResolvedStageInput> BuildOcrHumanResolutionInputAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        CancellationToken cancellationToken)
    {
        if (stage.InputAdapter?.MappingSnapshot is not JsonElement snapshot || snapshot.ValueKind != JsonValueKind.Object ||
            snapshot.GetProperty("schemaVersion").GetString() != "knowledge-clipper-ocr-human-resolution-input-adapter.v1")
            throw new OrchestratorDispatchException("binding.ocr_resolution_mapping_invalid", "OCR human-resolution adapter snapshot is invalid.");
        var proofreadReference = sources.SingleOrDefault(item => item.Kind == "ocr-proofread-bundle")
            ?? throw new OrchestratorDispatchException("binding.ocr_resolution_source_missing", "OCR human resolution requires one proofread bundle.");
        var imageProvenanceReference = sources.SingleOrDefault(item => item.Kind == "image.processing.provenance")
            ?? throw new OrchestratorDispatchException("binding.ocr_resolution_image_provenance_missing", "OCR human resolution requires hash-bound image processing provenance.");
        var originalImageReference = sources.SingleOrDefault(item => item.Kind == "image.original")
            ?? throw new OrchestratorDispatchException("binding.ocr_resolution_original_missing", "OCR human resolution requires its immutable original image artifact.");
        var proofreadManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, proofreadReference, cancellationToken);
        var proofreadFile = proofreadManifest.Files.SingleOrDefault(file => file.MediaType == "application/json")
            ?? throw new OrchestratorDispatchException("binding.ocr_resolution_source_invalid", "OCR proofread bundle must contain one JSON payload.");
        using var proofreadDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, proofreadFile.Path), cancellationToken));
        var proofreadRoot = proofreadDocument.RootElement;
        if (proofreadRoot.GetProperty("schema_version").GetString() != "ocr-proofread-bundle/1.0.0" || proofreadRoot.GetProperty("release_state").GetString() != "unresolved")
            throw new OrchestratorDispatchException("binding.ocr_resolution_source_invalid", "OCR human resolution requires a frozen unresolved proofread bundle.");
        var itemId = snapshot.GetProperty("sourceItemId").GetString()!;
        var item = proofreadRoot.GetProperty("items").EnumerateArray().SingleOrDefault(entry => entry.GetProperty("item_id").GetString() == itemId);
        if (item.ValueKind != JsonValueKind.Object || item.GetProperty("state").GetString() != "unresolved")
            throw new OrchestratorDispatchException("binding.ocr_resolution_item_invalid", "OCR resolution item is absent or no longer unresolved.");
        var provenance = item.GetProperty("provenance");
        var markdown = provenance.GetProperty("markdown_artifact");
        var markdownReference = new ArtifactReference(
            markdown.GetProperty("artifactRef").GetString()!, markdown.GetProperty("kind").GetString()!, markdown.GetProperty("contractVersion").GetString()!,
            markdown.GetProperty("manifestPath").GetString()!, markdown.GetProperty("expectedSha256").GetString()!);
        if (!await CoreArtifactIO.HasAncestorAsync(jobRoot, proofreadReference, markdownReference, cancellationToken))
            throw new OrchestratorDispatchException("binding.ocr_resolution_ancestry_invalid", "OCR markdown is not an immutable ancestor of the proofread bundle.");
        var markdownManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, markdownReference, cancellationToken);
        var markdownPath = markdown.GetProperty("payload_path").GetString()!;
        var markdownFile = markdownManifest.Files.SingleOrDefault(file => file.Path == markdownPath)
            ?? throw new OrchestratorDispatchException("binding.ocr_resolution_ancestry_invalid", "OCR markdown payload is not declared by its artifact.");
        if (!markdownFile.Sha256.Equals(markdown.GetProperty("payload_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("binding.ocr_resolution_ancestry_invalid", "OCR markdown payload hash drifted.");
        using var imageProvenanceDocument = await ReadEntrypointJsonAsync(jobRoot, imageProvenanceReference, cancellationToken);
        var imageProvenance = imageProvenanceDocument.RootElement;
        var provenanceItem = imageProvenance.GetProperty("items").EnumerateArray().SingleOrDefault(entry =>
            entry.GetProperty("status").GetString() == "completed" && entry.GetProperty("representations").TryGetProperty("markdown", out var representation) &&
            representation.GetProperty("artifactRef").GetString() == markdownReference.ArtifactRef);
        if (provenanceItem.ValueKind != JsonValueKind.Object || imageProvenance.GetProperty("continuation").GetProperty("defaultOcrRerun").GetBoolean())
            throw new OrchestratorDispatchException("binding.ocr_resolution_image_provenance_invalid", "OCR human-resolution input is not bound to a successful no-rerun image provenance item.");
        var originalRepresentation = provenanceItem.GetProperty("representations").GetProperty("original");
        if (originalRepresentation.GetProperty("artifactRef").GetString() != originalImageReference.ArtifactRef)
            throw new OrchestratorDispatchException("binding.ocr_resolution_original_mismatch", "OCR human-resolution original image identity drifted.");
        var originalManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, originalImageReference, cancellationToken);
        if (originalManifest.Files.Count != 1 || !originalManifest.Files[0].Sha256.Equals(originalRepresentation.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            originalManifest.Files[0].Bytes != originalRepresentation.GetProperty("bytes").GetInt64() || originalManifest.Files[0].MediaType != originalRepresentation.GetProperty("mediaType").GetString())
            throw new OrchestratorDispatchException("binding.ocr_resolution_original_mismatch", "OCR human-resolution original image bytes drifted.");

        var action = snapshot.GetProperty("action").GetString();
        object? correction = null;
        if (action == "submit_correction")
        {
            if (!snapshot.TryGetProperty("interactiveCommandRoot", out var ocrCommandRoot))
                throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "submit_correction requires an immutable Core-validated command proof.");
            ocrCommandRoot = await ValidateOcrInteractiveCommandRootAsync(jobRoot, runId, taskId, ocrCommandRoot, cancellationToken);
            var correctionReference = ocrCommandRoot.GetProperty("correctionRef").Deserialize<ArtifactReference>(ContractJson.Options)!;
            var correctionManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, correctionReference, cancellationToken);
            var correctionFile = correctionManifest.Files.Single();
            var correctionPayload = ocrCommandRoot.GetProperty("correctionPayload");
            if (correctionManifest.Files.Count != 1 || correctionFile.Path != RequiredString(correctionPayload, "path") ||
                !correctionFile.Sha256.Equals(RequiredString(correctionPayload, "sha256"), StringComparison.OrdinalIgnoreCase) ||
                correctionFile.Bytes != correctionPayload.GetProperty("bytes").GetInt64() ||
                correctionFile.MediaType != RequiredString(correctionPayload, "mediaType"))
                throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction payload drifted from the immutable command proof.");
            var correctedText = await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, correctionFile.Path), new UTF8Encoding(false, true), cancellationToken);
            correction = new
            {
                corrected_text = correctedText,
                corrected_text_sha256 = correctionFile.Sha256.ToLowerInvariant(),
                diffs = snapshot.GetProperty("diffs").EnumerateArray().Select(diff => new
                {
                    segment_index = diff.GetProperty("segmentIndex").GetInt32(), operation = diff.GetProperty("operation").GetString(),
                    start = diff.GetProperty("start").GetInt32(), end = diff.GetProperty("end").GetInt32(),
                    before = diff.GetProperty("before").GetString(), after = diff.GetProperty("after").GetString()
                }).ToArray()
            };
        }
        else if (action != "accept_original")
        {
            throw new OrchestratorDispatchException("binding.ocr_resolution_action_invalid", "Only accepted OCR actions may produce a Content resume input.");
        }

        var extendedProofreadReference = new
        {
            proofreadReference.ArtifactRef, proofreadReference.Kind, proofreadReference.ContractVersion,
            proofreadReference.ManifestPath, expectedSha256 = proofreadReference.ExpectedSha256.ToLowerInvariant(),
            payload_path = proofreadFile.Path, payload_sha256 = proofreadFile.Sha256.ToLowerInvariant()
        };
        var artifactId = $"content-input-ocr-resolution-{snapshot.GetProperty("resolutionId").GetString()}";
        const string inputName = "content-input.v5.json";
        var root = $"runs/{runId}/adapters/{stage.StageId}/{artifactId}/v0001";
        var input = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "standard-input/5.0.0", package_id = artifactId, task_id = taskId, input_version = "5.0.0", mode = "card",
            items = new[]
            {
                new
                {
                    item_id = itemId,
                    source = new { kind = "ocr_text", material_path = markdownPath, provenance = provenance.Clone() },
                    ocr_human_resolution = new
                    {
                        schema_version = "ocr-human-resolution-input/1.0.0", resolution_id = snapshot.GetProperty("resolutionId").GetString(),
                        action = action == "submit_correction" ? "user_correction" : "accept_original",
                        source_proofread_artifact = extendedProofreadReference, source_item_id = itemId,
                        original_text_sha256 = item.GetProperty("original_text_sha256").GetString(),
                        proofread_text_sha256 = item.GetProperty("proofread_text_sha256").GetString(),
                        unresolved_candidates = item.GetProperty("unresolved").Clone(), reason = snapshot.GetProperty("reason").GetString(), correction
                    }
                }
            }
        }, ContractJson.Options);
        // The Content worker validates OCR provenance against the adapter artifact's
        // direct inputRefs.  The proofread bundle proves transitive ancestry, while
        // the original OCR artifact is the exact source named by the v5 input. Keep
        // both bindings so a resume cannot silently substitute another OCR payload.
        var inheritedSources = sources
            .Append(markdownReference)
            .GroupBy(source => source.ArtifactRef, StringComparer.Ordinal)
            .Select(group => group.Single())
            .ToArray();
        var reference = await CoreArtifactIO.PublishFilesWithInheritedAsync(
            jobRoot, runId, stage.StageId, artifactId, "content-input", "application/vnd.rqc.content-input+json",
            new[] { new CoreArtifactPayload(inputName, input, "application/vnd.rqc.content-input+json") },
            new[] { markdownFile }, inheritedSources,
            JsonSerializer.SerializeToElement(new
            {
                entrypoint = $"{root}/{inputName}",
                adapter = "ocr-human-resolution-input.v1",
                humanProvenance = true,
                interactiveCommandRoot = snapshot.TryGetProperty("interactiveCommandRoot", out var commandRoot)
                    ? commandRoot.Clone()
                    : (JsonElement?)null
            }),
            cancellationToken);
        var requestInputs = new List<ArtifactReference> { reference };
        requestInputs.AddRange(sources.Where(source => source.Kind == "content-config-snapshot"));
        return new ResolvedStageInput(requestInputs, Merge(stage.Payload, new Dictionary<string, object?> { ["contentInputPath"] = $"{root}/{inputName}" }), false);
    }

    private static async Task<ResolvedStageInput> BuildInitialQuestionInputAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        CancellationToken cancellationToken)
    {
        var contentConfigSources = sources.Where(item => item.Kind == "content-config-snapshot").ToArray();
        var questionSources = sources.Where(item => item.Kind != "content-config-snapshot").ToArray();
        var capture = questionSources.SingleOrDefault(item => item.Kind == "capture-bundle")
            ?? throw new OrchestratorDispatchException("binding.question_capture_missing", "Initial question answering requires exactly one capture-bundle.");
        if (questionSources.Length != 1 || contentConfigSources.Length > 1)
            throw new OrchestratorDispatchException("binding.question_capture_invalid", "Initial question answering accepts only the submitted capture-bundle.");
        using var bundle = await ReadEntrypointJsonAsync(jobRoot, capture, cancellationToken);
        var root = bundle.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "capture-bundle.v1" || root.GetProperty("intent").GetString() != "question")
            throw new OrchestratorDispatchException("binding.question_capture_invalid", "Question capture identity is invalid.");
        var turns = root.GetProperty("questionTurns").EnumerateArray().ToArray();
        if (turns.Length != 1 || turns[0].GetProperty("turnIndex").GetInt32() != 1 || turns[0].GetProperty("role").GetString() != "user")
            throw new OrchestratorDispatchException("binding.question_turn_order_invalid", "Initial question capture must contain exactly user turnIndex=1.");
        var artifactRef = turns[0].GetProperty("artifactRef").GetString()!;
        var artifact = root.GetProperty("artifacts").EnumerateArray().SingleOrDefault(item => item.GetProperty("artifactRef").GetString() == artifactRef);
        if (artifact.ValueKind != JsonValueKind.Object || artifact.GetProperty("mediaType").GetString() != "text/plain" || artifact.GetProperty("encoding").GetString() != "utf-8")
            throw new OrchestratorDispatchException("binding.question_turn_invalid", "Initial question turn must reference one UTF-8 text/plain capture artifact.");
        var relativePath = artifact.GetProperty("relativePath").GetString()!;
        await Integrity.VerifyFileAsync(jobRoot, relativePath, artifact.GetProperty("bytes").GetInt64(), artifact.GetProperty("sha256").GetString()!, cancellationToken);
        var question = await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, relativePath), new UTF8Encoding(false, true), cancellationToken);
        ValidateQuestionText(question);
        return await PublishQuestionInputAsync(
            jobRoot, runId, taskId, stage, sources,
            new[] { new QuestionTurnInput(artifactRef, 0, question, null) },
            Array.Empty<QuestionAnswerAncestry>(), "answer", null, null, cancellationToken);
    }

    private static async Task<ResolvedStageInput> BuildQuestionResumeInputAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        CancellationToken cancellationToken)
    {
        if (stage.InputAdapter?.MappingSnapshot is not JsonElement snapshot || snapshot.ValueKind != JsonValueKind.Object ||
            snapshot.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "phase", "turnIndex", "interactiveCommandRoot" }) == false ||
            snapshot.GetProperty("schemaVersion").GetString() != "knowledge-clipper-question-input-adapter.v2")
            throw new OrchestratorDispatchException("binding.question_mapping_invalid", "Question resume adapter snapshot is invalid.");
        var phase = snapshot.GetProperty("phase").GetString();
        if (phase is not ("answer" or "make-cards"))
            throw new OrchestratorDispatchException("binding.question_phase_invalid", "Question resume phase is invalid.");
        var answerRef = sources.SingleOrDefault(item => item.Kind == "question.answer")
            ?? throw new OrchestratorDispatchException("binding.question_answer_missing", "Question resume requires exactly one persisted answer artifact.");
        using var answerDocument = await ReadEntrypointJsonAsync(jobRoot, answerRef, cancellationToken);
        var answer = answerDocument.RootElement;
        ValidateQuestionAnswerPayload(answer, taskId);
        var records = answer.GetProperty("turns").EnumerateArray().ToArray();
        var turns = records.Select(item => new QuestionTurnInput(
            item.GetProperty("turn_id").GetString()!, item.GetProperty("turn_index").GetInt32(), item.GetProperty("question").GetString()!,
            item.TryGetProperty("parent_answer_id", out var parent) ? parent.GetString() : null)).ToList();
        var ancestry = records.Select((item, index) => new QuestionAnswerAncestry(
            item.GetProperty("answer_id").GetString()!,
            index == records.Length - 1 && item.GetProperty("artifact_ref").GetString() == "pending" ? answerRef.ArtifactRef : item.GetProperty("artifact_ref").GetString()!,
            item.GetProperty("turn_id").GetString()!, item.GetProperty("turn_index").GetInt32(),
            item.GetProperty("question_sha256").GetString()!, item.GetProperty("answer_text").GetString()!, item.GetProperty("answer_text_sha256").GetString()!,
            item.TryGetProperty("parent_answer_id", out var parent) ? parent.GetString() : null)).ToList();
        JsonElement? interactiveCommandRoot = null;

        if (phase == "answer")
        {
            var questionRef = sources.SingleOrDefault(item => item.Kind == "question.turn")
                ?? throw new OrchestratorDispatchException("binding.question_turn_missing", "Follow-up answering requires exactly one question.turn artifact.");
            var expectedIndex = records.Length;
            if (snapshot.GetProperty("turnIndex").GetInt32() != expectedIndex)
                throw new OrchestratorDispatchException("binding.question_turn_order_invalid", "Follow-up turnIndex is stale or out of order.");
            var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, questionRef, cancellationToken);
            if (manifest.Files.Count != 1 || manifest.Files[0].MediaType != "text/plain")
                throw new OrchestratorDispatchException("binding.question_turn_invalid", "question.turn must contain exactly one text/plain file.");
            var question = await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, manifest.Files[0].Path), new UTF8Encoding(false, true), cancellationToken);
            ValidateQuestionText(question);
            interactiveCommandRoot = await ValidateInteractiveCommandRootAsync(
                jobRoot, runId, taskId, snapshot.GetProperty("interactiveCommandRoot"), questionRef, manifest, cancellationToken);
            turns.Add(new QuestionTurnInput(questionRef.ArtifactRef, expectedIndex, question, ancestry[^1].AnswerId));
        }
        else if (snapshot.GetProperty("turnIndex").ValueKind != JsonValueKind.Null || snapshot.GetProperty("interactiveCommandRoot").ValueKind != JsonValueKind.Null)
        {
            throw new OrchestratorDispatchException("binding.question_turn_order_invalid", "make-cards must not add a question turn.");
        }

        string? cardStructurePath = null;
        var cardStructure = sources.SingleOrDefault(item => item.Kind == "question.card-structure");
        if (cardStructure is not null)
        {
            var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, cardStructure, cancellationToken);
            if (manifest.Files.Count != 1)
                throw new OrchestratorDispatchException("binding.question_card_structure_invalid", "question.card-structure must contain exactly one file.");
            cardStructurePath = manifest.Files[0].Path;
        }
        return await PublishQuestionInputAsync(jobRoot, runId, taskId, stage, sources, turns, ancestry, phase, cardStructurePath, interactiveCommandRoot, cancellationToken);
    }

    private static async Task<ResolvedStageInput> PublishQuestionInputAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        IReadOnlyList<QuestionTurnInput> turns,
        IReadOnlyList<QuestionAnswerAncestry> ancestry,
        string phase,
        string? cardStructurePath,
        JsonElement? interactiveCommandRoot,
        CancellationToken cancellationToken)
    {
        var inputObject = new
        {
            schema_version = "standard-input/3.0.0",
            package_id = $"question-{taskId}-{runId}",
            task_id = taskId,
            input_version = "3.0.0",
            mode = "question",
            questionWorkflow = new
            {
                schemaVersion = "question-workflow-input/1.0.0",
                phase,
                questionTurns = turns.Select(QuestionTurnObject).ToArray(),
                answerAncestry = ancestry.Select(QuestionAncestryObject).ToArray(),
                cardStructureRef = cardStructurePath
            },
            items = new[] { new { item_id = "item-question-workflow", source = new { kind = "question_workflow" } } }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(inputObject, ContractJson.Options);
        var digest = Integrity.Sha256Hex(bytes).ToLowerInvariant();
        var artifactId = $"question-input-{digest[..24]}";
        const string inputName = "content-input.v3.json";
        var root = $"runs/{runId}/adapters/{stage.StageId}/{artifactId}/v0001";
        var ancestrySources = interactiveCommandRoot is null
            ? sources
            : sources.Where(item => item.Kind != "question.turn").ToArray();
        var reference = await CoreArtifactIO.PublishFilesAsync(
            jobRoot, runId, stage.StageId, artifactId, "content-input", "application/vnd.rqc.content-input+json",
            new[] { new CoreArtifactPayload(inputName, bytes, "application/vnd.rqc.content-input+json") }, ancestrySources,
            JsonSerializer.SerializeToElement(new
            {
                entrypoint = $"{root}/{inputName}",
                adapter = phase == "answer" ? "question-answer-input.v3" : "question-make-cards-input.v3",
                interactiveCommandRoot
            }),
            cancellationToken);
        var requestInputs = new List<ArtifactReference> { reference };
        requestInputs.AddRange(sources.Where(item => item.Kind == "content-config-snapshot"));
        return new ResolvedStageInput(requestInputs, Merge(stage.Payload, new Dictionary<string, object?> { ["contentInputPath"] = $"{root}/{inputName}" }), false);
    }

    internal static void ValidateQuestionAnswerPayload(JsonElement root, string taskId)
    {
        if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "schema_version") != "question-answer/1.0.0" ||
            RequiredString(root, "task_id") != taskId || RequiredString(root, "phase") != "answer")
            throw new OrchestratorDispatchException("binding.question_answer_invalid", "question.answer payload identity is invalid.");
        var turns = root.GetProperty("turns").EnumerateArray().ToArray();
        if (turns.Length == 0 || RequiredString(root, "new_answer_id") != RequiredString(turns[^1], "answer_id"))
            throw new OrchestratorDispatchException("binding.question_answer_invalid", "question.answer contains no valid appended answer.");
        for (var index = 0; index < turns.Length; index++)
        {
            var item = turns[index];
            var question = RequiredString(item, "question");
            var answer = RequiredString(item, "answer_text");
            ValidateQuestionText(question);
            if (string.IsNullOrWhiteSpace(answer) || Integrity.Sha256Hex(Encoding.UTF8.GetBytes(question)).ToLowerInvariant() != RequiredString(item, "question_sha256") ||
                Integrity.Sha256Hex(Encoding.UTF8.GetBytes(answer)).ToLowerInvariant() != RequiredString(item, "answer_text_sha256") || item.GetProperty("turn_index").GetInt32() != index)
                throw new OrchestratorDispatchException("binding.question_answer_invalid", $"question.answer turn {index} failed hash or order validation.");
            if (index == 0 && item.TryGetProperty("parent_answer_id", out _))
                throw new OrchestratorDispatchException("binding.question_answer_invalid", "First answer cannot have a parent.");
            if (index > 0 && (!item.TryGetProperty("parent_answer_id", out var parent) || parent.GetString() != turns[index - 1].GetProperty("answer_id").GetString()))
                throw new OrchestratorDispatchException("binding.question_answer_invalid", $"question.answer ancestry is broken at turn {index}.");
        }
    }

    private static void ValidateQuestionText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character is < ' ' and not ('\t' or '\r' or '\n') || character == '\u007f'))
            throw new OrchestratorDispatchException("binding.question_text_invalid", "Question text is empty or contains a forbidden control character.");
    }

    // A user correction is a durable interactive input root, not an initial task
    // input and not an adapter inputRef.  The proof is carried as metadata only;
    // this validation makes that exception narrow, task-local, and replay-safe.
    private static async Task<JsonElement> ValidateOcrInteractiveCommandRootAsync(
        string jobRoot,
        string runId,
        string taskId,
        JsonElement proof,
        CancellationToken cancellationToken)
    {
        var expectedFields = new[]
        {
            "schemaVersion", "taskId", "commandId", "operationId", "idempotencyKey", "acceptanceRunId", "resolutionRunId",
            "payloadRef", "payloadSha256", "acceptedEventRef", "resolutionRef", "action", "targetToken", "correctionRef",
            "correctionPayload", "diffsSha256"
        };
        if (proof.ValueKind != JsonValueKind.Object ||
            !proof.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expectedFields) ||
            RequiredString(proof, "schemaVersion") != "knowledge-clipper-ocr-interactive-command-root.v1" ||
            RequiredString(proof, "taskId") != taskId || RequiredString(proof, "resolutionRunId") != runId ||
            RequiredString(proof, "action") != "submit_correction")
            throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction command provenance snapshot is invalid.");

        var commandId = RequiredString(proof, "commandId");
        var operationId = RequiredString(proof, "operationId");
        var idempotencyKey = RequiredString(proof, "idempotencyKey");
        var acceptanceRunId = RequiredString(proof, "acceptanceRunId");
        var targetToken = RequiredString(proof, "targetToken");
        var correctionReference = proof.GetProperty("correctionRef").Deserialize<ArtifactReference>(ContractJson.Options)
            ?? throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction proof has no artifact reference.");
        if (correctionReference.Kind != "ocr.human-correction.text" || correctionReference.ContractVersion != "1.0.0" ||
            correctionReference.ManifestPath.Replace('\\', '/') != $"artifacts/{correctionReference.ArtifactRef}/artifact.json")
            throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction reference is not canonical.");
        await CoreArtifactIO.ValidateReferenceAsync(jobRoot, correctionReference, cancellationToken);

        var payloadRef = RequiredString(proof, "payloadRef");
        if (payloadRef.Replace('\\', '/') != $"commands/{commandId}/ocr-human-resolution-action.v1.json")
            throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction action path is not canonical.");
        var actionBytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, payloadRef), cancellationToken);
        if (!Integrity.Sha256Hex(actionBytes).Equals(RequiredString(proof, "payloadSha256"), StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction action hash drifted after acceptance.");
        using (var actionDocument = JsonDocument.Parse(actionBytes))
        {
            var action = actionDocument.RootElement;
            if (RequiredString(action, "schemaVersion") != "knowledge-clipper-ocr-human-resolution-action.v1" ||
                RequiredString(action, "decisionKind") != "ocr-human-resolution" || RequiredString(action, "action") != "submit_correction" ||
                RequiredString(action, "targetToken") != targetToken)
                throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction action identity does not match its provenance snapshot.");
            var actionReference = action.GetProperty("correctionRef").Deserialize<ArtifactReference>(ContractJson.Options);
            if (actionReference is null || !SameReference(actionReference, correctionReference) ||
                !Integrity.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(action.GetProperty("diffs"), ContractJson.Options)).Equals(RequiredString(proof, "diffsSha256"), StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction action reference or diff evidence drifted.");
        }

        var correctionManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, correctionReference, cancellationToken);
        var correctionPayload = proof.GetProperty("correctionPayload");
        var expectedCorrectionPath = $"commands/{commandId}/ocr-correction/payload.txt";
        if (correctionManifest.Files.Count != 1 || correctionManifest.Files[0].Path != expectedCorrectionPath || correctionManifest.Files[0].MediaType != "text/plain" ||
            RequiredString(correctionPayload, "path") != expectedCorrectionPath ||
            !correctionManifest.Files[0].Sha256.Equals(RequiredString(correctionPayload, "sha256"), StringComparison.OrdinalIgnoreCase) ||
            correctionManifest.Files[0].Bytes != correctionPayload.GetProperty("bytes").GetInt64() || correctionManifest.Files[0].MediaType != RequiredString(correctionPayload, "mediaType"))
            throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction payload is not the exact canonical command-owned file.");
        using (var manifestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, correctionReference.ManifestPath), cancellationToken)))
        {
            var producer = manifestDocument.RootElement.GetProperty("producer");
            if (RequiredString(producer, "stageId") != "desktop-ocr-human-resolution" || RequiredString(producer, "workerId") != "desktop-capture")
                throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction artifact owner is not the trusted Desktop human-resolution producer.");
        }

        var acceptedEventRef = RequiredString(proof, "acceptedEventRef");
        var acceptedEvent = JsonSerializer.Deserialize<TaskEvent>(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, acceptedEventRef), cancellationToken), ContractJson.Options);
        if (acceptedEvent is null || acceptedEvent.Type != "command.accepted" || acceptedEvent.TaskId != taskId || acceptedEvent.RunId != acceptanceRunId ||
            acceptedEvent.CommandId != commandId || acceptedEvent.IdempotencyKey != idempotencyKey ||
            RequiredString(acceptedEvent.Payload, "operationId") != operationId || RequiredString(acceptedEvent.Payload, "type") != "task.provide_input" ||
            RequiredString(acceptedEvent.Payload, "payloadRef") != payloadRef)
            throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction command acceptance evidence is missing or mismatched.");

        var resolutionRef = RequiredString(proof, "resolutionRef");
        using (var resolutionDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, resolutionRef), cancellationToken)))
        {
            var resolution = resolutionDocument.RootElement;
            if (RequiredString(resolution, "schemaVersion") != "knowledge-clipper-ocr-human-resolution.v1" ||
                RequiredString(resolution, "action") != "submit_correction" || RequiredString(resolution, "targetToken") != targetToken ||
                RequiredString(resolution, "commandId") != commandId || RequiredString(resolution, "resolutionRunId") != runId)
                throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction resolution evidence is missing or mismatched.");
            var resolutionReference = resolution.GetProperty("correctionRef").Deserialize<ArtifactReference>(ContractJson.Options);
            if (resolutionReference is null || !SameReference(resolutionReference, correctionReference) ||
                !Integrity.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(resolution.GetProperty("diffs"), ContractJson.Options)).Equals(RequiredString(proof, "diffsSha256"), StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction resolution does not bind the exact correction evidence.");
        }

        var resolved = new EventStore(jobRoot, runId).ReadAll().Where(item => item.Type == "task.ocr_human_resolution_resolved" && item.TaskId == taskId && item.CommandId == commandId).ToArray();
        if (resolved.Length != 1 || resolved[0].IdempotencyKey != idempotencyKey || RequiredString(resolved[0].Payload, "action") != "submit_correction" ||
            RequiredString(resolved[0].Payload, "targetToken") != targetToken)
            throw new OrchestratorDispatchException("binding.ocr_command_root_invalid", "OCR correction command has no unique durable resolved event.");
        return proof.Clone();
    }

    private static async Task<JsonElement> ValidateInteractiveCommandRootAsync(
        string jobRoot,
        string runId,
        string taskId,
        JsonElement proof,
        ArtifactReference questionTurn,
        CoreArtifactManifest questionManifest,
        CancellationToken cancellationToken)
    {
        if (proof.ValueKind != JsonValueKind.Object ||
            !proof.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(new[]
            {
                "schemaVersion", "taskId", "commandId", "operationId", "idempotencyKey", "acceptanceRunId", "resolutionRunId",
                "payloadRef", "payloadSha256", "acceptedEventRef", "resolutionRef", "action", "targetToken", "turnIndex", "questionTurnRef"
            }) || RequiredString(proof, "schemaVersion") != "knowledge-clipper-interactive-command-root.v1" ||
            RequiredString(proof, "taskId") != taskId || RequiredString(proof, "resolutionRunId") != runId ||
            RequiredString(proof, "action") != "question.follow-up")
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question command provenance snapshot is invalid.");

        var commandId = RequiredString(proof, "commandId");
        var operationId = RequiredString(proof, "operationId");
        var idempotencyKey = RequiredString(proof, "idempotencyKey");
        var acceptanceRunId = RequiredString(proof, "acceptanceRunId");
        var targetToken = RequiredString(proof, "targetToken");
        var turnIndex = proof.GetProperty("turnIndex").GetInt32();
        var expectedArtifactId = $"question.turn.{turnIndex + 1:D4}";
        if (questionTurn.ArtifactRef != expectedArtifactId || questionTurn.Kind != "question.turn" || questionTurn.ContractVersion != "1.0.0" ||
            questionTurn.ManifestPath.Replace('\\', '/') != $"artifacts/{expectedArtifactId}/artifact.json" || questionManifest.Files.Count != 1)
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question artifact path or identity is not canonical.");
        var expectedPayloadPath = $"commands/{commandId}/question-turn/payload.txt";
        if (questionManifest.Files[0].Path != expectedPayloadPath || questionManifest.Files[0].MediaType != "text/plain")
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question payload is not owned by its canonical command directory.");
        var proofReference = proof.GetProperty("questionTurnRef").Deserialize<ArtifactReference>(ContractJson.Options);
        if (proofReference is null || !SameReference(proofReference, questionTurn))
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question reference drifted from its provenance snapshot.");

        var payloadRef = RequiredString(proof, "payloadRef");
        if (payloadRef.Replace('\\', '/') != $"commands/{commandId}/question-action.v2.json")
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question action path is not canonical.");
        var actionBytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, payloadRef), cancellationToken);
        if (!Integrity.Sha256Hex(actionBytes).Equals(RequiredString(proof, "payloadSha256"), StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question action hash drifted after acceptance.");
        using (var actionDocument = JsonDocument.Parse(actionBytes))
        {
            var action = actionDocument.RootElement;
            if (RequiredString(action, "schemaVersion") != "knowledge-clipper-question-action.v2" ||
                RequiredString(action, "action") != "question.follow-up" || RequiredString(action, "targetToken") != targetToken ||
                action.GetProperty("turnIndex").GetInt32() != turnIndex)
                throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question action identity does not match its provenance snapshot.");
            var actionReference = action.GetProperty("questionTurnRef").Deserialize<ArtifactReference>(ContractJson.Options);
            if (actionReference is null || !SameReference(actionReference, questionTurn))
                throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question action does not bind the exact question artifact.");
        }

        var acceptedEventRef = RequiredString(proof, "acceptedEventRef");
        var acceptedEvent = JsonSerializer.Deserialize<TaskEvent>(
            await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, acceptedEventRef), cancellationToken), ContractJson.Options);
        if (acceptedEvent is null || acceptedEvent.Type != "command.accepted" || acceptedEvent.TaskId != taskId || acceptedEvent.RunId != acceptanceRunId ||
            acceptedEvent.CommandId != commandId || acceptedEvent.IdempotencyKey != idempotencyKey ||
            RequiredString(acceptedEvent.Payload, "operationId") != operationId || RequiredString(acceptedEvent.Payload, "type") != "task.provide_input" ||
            RequiredString(acceptedEvent.Payload, "payloadRef") != payloadRef)
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question command acceptance evidence is missing or mismatched.");

        var resolutionRef = RequiredString(proof, "resolutionRef");
        using (var resolutionDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, resolutionRef), cancellationToken)))
        {
            var resolution = resolutionDocument.RootElement;
            if (RequiredString(resolution, "schemaVersion") != "knowledge-clipper-question-resolution.v2" ||
                RequiredString(resolution, "action") != "question.follow-up" || RequiredString(resolution, "targetToken") != targetToken ||
                RequiredString(resolution, "commandId") != commandId || resolution.GetProperty("turnIndex").GetInt32() != turnIndex)
                throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question resolution evidence is missing or mismatched.");
            var resolutionReference = resolution.GetProperty("questionTurnRef").Deserialize<ArtifactReference>(ContractJson.Options);
            if (resolutionReference is null || !SameReference(resolutionReference, questionTurn))
                throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question resolution does not bind the exact question artifact.");
        }
        var resolved = new EventStore(jobRoot, runId).ReadAll().Where(item => item.Type == "task.question_action_resolved" && item.TaskId == taskId && item.CommandId == commandId).ToArray();
        if (resolved.Length != 1 || resolved[0].IdempotencyKey != idempotencyKey || RequiredString(resolved[0].Payload, "action") != "question.follow-up" ||
            RequiredString(resolved[0].Payload, "targetToken") != targetToken)
            throw new OrchestratorDispatchException("binding.question_command_root_invalid", "Interactive question command has no unique durable resolved event.");
        return proof.Clone();
    }

    private static bool SameReference(ArtifactReference left, ArtifactReference right) =>
        left.ArtifactRef == right.ArtifactRef && left.Kind == right.Kind && left.ContractVersion == right.ContractVersion &&
        left.ManifestPath.Replace('\\', '/') == right.ManifestPath.Replace('\\', '/') &&
        left.ExpectedSha256.Equals(right.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object> QuestionTurnObject(QuestionTurnInput item)
    {
        var value = new Dictionary<string, object>
        {
            ["turnId"] = item.TurnId, ["turnIndex"] = item.TurnIndex, ["question"] = item.Question
        };
        if (item.ParentAnswerId is not null) value["parentAnswerId"] = item.ParentAnswerId;
        return value;
    }

    private static Dictionary<string, object> QuestionAncestryObject(QuestionAnswerAncestry item)
    {
        var value = new Dictionary<string, object>
        {
            ["answerId"] = item.AnswerId, ["artifactRef"] = item.ArtifactRef, ["turnId"] = item.TurnId,
            ["turnIndex"] = item.TurnIndex, ["questionSha256"] = item.QuestionSha256,
            ["answerText"] = item.AnswerText, ["answerTextSha256"] = item.AnswerTextSha256
        };
        if (item.ParentAnswerId is not null) value["parentAnswerId"] = item.ParentAnswerId;
        return value;
    }

    private sealed record QuestionTurnInput(string TurnId, int TurnIndex, string Question, string? ParentAnswerId);
    private sealed record QuestionAnswerAncestry(string AnswerId, string ArtifactRef, string TurnId, int TurnIndex, string QuestionSha256, string AnswerText, string AnswerTextSha256, string? ParentAnswerId);

    public static JsonElement AddRequestIdentity(JsonElement payload, string requestId, string operationId)
    {
        var values = payload.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        values["requestId"] = JsonSerializer.SerializeToElement(requestId);
        values["operationId"] = JsonSerializer.SerializeToElement(operationId);
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }

    private static async Task<ResolvedStageInput> BuildContentInputAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationToken cancellationToken)
    {
        var contentConfigSources = sources.Where(item => item.Kind == "content-config-snapshot").ToArray();
        if (contentConfigSources.Length > 1)
            throw new OrchestratorDispatchException("binding.content_config_invalid", "Content input accepts at most one content-config-snapshot.");
        var materialSources = sources.Where(item => item.Kind != "content-config-snapshot").ToArray();
        if (materialSources.Any(item => item.Kind == "image-ocr.markdown"))
            return await BuildOcrContentInputAsync(jobRoot, runId, taskId, stage, sources, materialSources, contentConfigSources, completed, cancellationToken);
        var extracted = await ExtractTextAsync(jobRoot, stage, materialSources, completed, cancellationToken);
        var material = new UTF8Encoding(false).GetBytes(extracted.Text);
        var digest = Integrity.Sha256Hex(material)[..24].ToLowerInvariant();
        var artifactId = $"content-input-{digest}";
        var root = $"runs/{runId}/adapters/{stage.StageId}/{artifactId}/v0001";
        var materialName = "material.md";
        var inputName = "content-input.v1.json";
        var input = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "standard-input/1.0.0",
            package_id = artifactId,
            task_id = taskId,
            input_version = "1.0.0",
            mode = "card",
            items = new[] { new { item_id = "item-0001", source = new { kind = "text", material_path = $"{root}/{materialName}" } } }
        }, ContractJson.Options);
        var reference = await CoreArtifactIO.PublishFilesAsync(
            jobRoot, runId, stage.StageId, artifactId, "content-input", "application/vnd.rqc.content-input+json",
            new[]
            {
                new CoreArtifactPayload(inputName, input, "application/vnd.rqc.content-input+json"),
                new CoreArtifactPayload(materialName, material, "text/markdown")
            },
            sources,
            JsonSerializer.SerializeToElement(new { entrypoint = $"{root}/{inputName}", sourceKinds = sources.Select(item => item.Kind).ToArray() }),
            cancellationToken);
        var requestInputs = new List<ArtifactReference> { reference };
        requestInputs.AddRange(contentConfigSources);
        return new ResolvedStageInput(requestInputs, Merge(stage.Payload, new Dictionary<string, object?>
        {
            ["contentInputPath"] = $"{root}/{inputName}"
        }), false);
    }

    private static async Task<ResolvedStageInput> BuildOcrContentInputAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        IReadOnlyList<ArtifactReference> materialSources,
        IReadOnlyList<ArtifactReference> contentConfigSources,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationToken cancellationToken)
    {
        var capture = materialSources.SingleOrDefault(item => item.Kind == "capture-bundle")
            ?? throw new OrchestratorDispatchException("binding.ocr_capture_order_missing", "OCR-aware Content input requires its immutable capture-bundle for item ordering.");
        if (materialSources.Any(item => item.Kind is not ("capture-bundle" or "image-ocr.markdown")))
            throw new OrchestratorDispatchException("binding.ocr_source_unsupported", "OCR-aware Content input accepts only the capture-bundle and image-ocr.markdown artifacts.");
        var ordered = await OrderOcrOutputsAsync(jobRoot, capture, stage, completed, cancellationToken);
        if (ordered.Count != materialSources.Count(item => item.Kind == "image-ocr.markdown"))
            throw new OrchestratorDispatchException("binding.ocr_item_output_invalid", "OCR-aware Content input contains unbound or duplicate markdown artifacts.");

        var inherited = new List<CoreArtifactFile>();
        var items = new List<object>();
        var identity = new StringBuilder(taskId);
        for (var index = 0; index < ordered.Count; index++)
        {
            var orderedItem = ordered[index];
            var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, orderedItem.Reference, cancellationToken);
            var markdownFiles = manifest.Files.Where(file => file.MediaType is "text/markdown" or "text/plain").ToArray();
            if (markdownFiles.Length != 1)
                throw new OrchestratorDispatchException("binding.ocr_markdown_missing", $"OCR item must declare exactly one markdown payload: {orderedItem.Reference.ArtifactRef}");
            var file = markdownFiles[0];
            inherited.Add(file);
            identity.Append('\0').Append(orderedItem.Reference.ArtifactRef).Append('\0').Append(orderedItem.Reference.ExpectedSha256).Append('\0').Append(file.Sha256);
            items.Add(new
            {
                item_id = $"ocr-{index + 1:D4}",
                source = new
                {
                    kind = "ocr_text",
                    material_path = file.Path,
                    provenance = new
                    {
                        schema_version = "ocr-provenance/1.0.0",
                        producer_worker_id = "image.ocr.mineru",
                        markdown_artifact = new
                        {
                            artifactRef = orderedItem.Reference.ArtifactRef,
                            kind = orderedItem.Reference.Kind,
                            contractVersion = orderedItem.Reference.ContractVersion,
                            manifestPath = orderedItem.Reference.ManifestPath,
                            expectedSha256 = orderedItem.Reference.ExpectedSha256.ToLowerInvariant(),
                            payload_path = file.Path,
                            payload_sha256 = file.Sha256.ToLowerInvariant()
                        }
                    }
                },
                hints = new { role = orderedItem.Role, index = orderedItem.Index }
            });
        }

        var digest = Integrity.Sha256Hex(Encoding.UTF8.GetBytes(identity.ToString()))[..24].ToLowerInvariant();
        var artifactId = $"content-input-ocr-{digest}";
        const string inputName = "content-input.v4.json";
        var root = $"runs/{runId}/adapters/{stage.StageId}/{artifactId}/v0001";
        var input = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "standard-input/4.0.0",
            package_id = artifactId,
            task_id = taskId,
            input_version = "4.0.0",
            mode = "card",
            items
        }, ContractJson.Options);
        var reference = await CoreArtifactIO.PublishFilesWithInheritedAsync(
            jobRoot, runId, stage.StageId, artifactId, "content-input", "application/vnd.rqc.content-input+json",
            new[] { new CoreArtifactPayload(inputName, input, "application/vnd.rqc.content-input+json") },
            inherited, sources,
            JsonSerializer.SerializeToElement(new
            {
                entrypoint = $"{root}/{inputName}",
                adapter = "content-input-ocr.v4",
                sourceKinds = sources.Select(item => item.Kind).ToArray()
            }),
            cancellationToken);
        var requestInputs = new List<ArtifactReference> { reference };
        requestInputs.AddRange(contentConfigSources);
        return new ResolvedStageInput(requestInputs, Merge(stage.Payload, new Dictionary<string, object?>
        {
            ["contentInputPath"] = $"{root}/{inputName}"
        }), false);
    }

    private static async Task<ResolvedStageInput> BuildLanguagePracticeContentInputAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        CancellationToken cancellationToken)
    {
        var contentSources = sources.Where(source => source.Kind == "content-input").ToArray();
        if (contentSources.Length != 1)
            throw new OrchestratorDispatchException("binding.language_choice_input_invalid", "Language-practice resume requires exactly one immutable content-input source.");
        if (stage.InputAdapter?.MappingSnapshot is not JsonElement snapshot || snapshot.ValueKind != JsonValueKind.Object ||
            snapshot.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "schemaVersion", "choice" }) == false ||
            snapshot.GetProperty("schemaVersion").GetString() != "knowledge-clipper-language-practice-input-adapter.v2")
            throw new OrchestratorDispatchException("binding.language_choice_mapping_invalid", "Language-practice resume adapter snapshot is invalid.");
        var choice = snapshot.GetProperty("choice").GetString();
        if (choice is not ("meaning-reading" or "writing" or "both"))
            throw new OrchestratorDispatchException("binding.language_choice_invalid", "Language-practice choice is unsupported.");
        if (stage.Payload.ValueKind != JsonValueKind.Object || !stage.Payload.TryGetProperty("contentInputPath", out var inputPathElement))
            throw new OrchestratorDispatchException("binding.language_choice_source_invalid", "Original content request does not identify its immutable content input.");
        var originalInputPath = inputPathElement.GetString()!;
        var sourceManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, contentSources[0], cancellationToken);
        var sourceInput = sourceManifest.Files.SingleOrDefault(file => file.Path == originalInputPath)
            ?? throw new OrchestratorDispatchException("binding.language_choice_source_invalid", "Original content input path is not declared by its artifact manifest.");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, sourceInput.Path), new UTF8Encoding(false, true), cancellationToken)) as JsonObject
            ?? throw new OrchestratorDispatchException("binding.language_choice_source_invalid", "Original content input is not a JSON object.");
        if (root["task_id"]?.GetValue<string>() != taskId || root["schema_version"]?.GetValue<string>() is not ("standard-input/1.0.0" or "standard-input/2.0.0"))
            throw new OrchestratorDispatchException("binding.language_choice_source_invalid", "Original content input identity is invalid.");
        root["schema_version"] = "standard-input/2.0.0";
        JsonObject humanOverrides;
        if (root["human_overrides"] is null)
        {
            humanOverrides = new JsonObject();
            root["human_overrides"] = humanOverrides;
        }
        else if (root["human_overrides"] is JsonObject existingOverrides)
        {
            humanOverrides = existingOverrides;
        }
        else
        {
            throw new OrchestratorDispatchException("binding.language_choice_source_invalid", "Original content input human_overrides is not an object.");
        }
        humanOverrides["languagePracticeMode"] = choice;
        var bytes = new UTF8Encoding(false).GetBytes(root.ToJsonString(ContractJson.Options) + "\n");
        var digest = Integrity.Sha256Hex(Encoding.UTF8.GetBytes($"{contentSources[0].ExpectedSha256}\0{choice}"))[..24].ToLowerInvariant();
        var artifactId = $"content-input-language-choice-{digest}";
        var reference = await CoreArtifactIO.PublishDerivedJsonAsync(
            jobRoot,
            runId,
            stage.StageId,
            artifactId,
            "content-input.v2.json",
            bytes,
            contentSources[0],
            sourceManifest.Files.Where(file => file.Path != originalInputPath).ToArray(),
            JsonSerializer.SerializeToElement(new
            {
                adapter = "content-input-language-choice.v2",
                inputSchemaVersion = "standard-input/2.0.0",
                choiceAppliedAt = "human_overrides.languagePracticeMode"
            }, ContractJson.Options),
            cancellationToken);
        var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, reference, cancellationToken);
        var newInputPath = manifest.Files.Single(file => file.Path.EndsWith("/content-input.v2.json", StringComparison.Ordinal)).Path;
        var passthrough = sources.Where(source => source.Kind != "content-input").Append(reference).ToArray();
        return new ResolvedStageInput(passthrough, Merge(stage.Payload, new Dictionary<string, object?>
        {
            ["contentInputPath"] = newInputPath
        }), false);
    }

    private static async Task<ResolvedStageInput> BuildSpokenTextAsync(
        string jobRoot,
        string runId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationToken cancellationToken)
    {
        var extracted = await ExtractTextAsync(jobRoot, stage, sources, completed, cancellationToken);
        var bytes = new UTF8Encoding(false).GetBytes(extracted.Text);
        var digest = Integrity.Sha256Hex(bytes).ToLowerInvariant();
        var artifactId = $"spoken-text-{digest[..24]}";
        const string payloadName = "spoken.txt";
        var reference = await CoreArtifactIO.PublishAsync(
            jobRoot, runId, stage.StageId, artifactId, "text.spoken", "text/plain", payloadName, bytes, sources,
            JsonSerializer.SerializeToElement(new { contextMode = extracted.ContextMode, encoding = "utf-8" }), cancellationToken);
        var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, reference, cancellationToken);
        var file = manifest.Files.Single();
        return new ResolvedStageInput(new[] { reference }, Merge(stage.Payload, new Dictionary<string, object?>
        {
            ["spokenText"] = new { artifactRef = reference.ArtifactRef, path = file.Path, sha256 = file.Sha256.ToLowerInvariant(), bytes = file.Bytes, encoding = "utf-8", mediaType = "text/plain" },
            ["contextMode"] = extracted.ContextMode
        }), true);
    }

    private static async Task<ResolvedStageInput> BuildAliasAsync(
        string jobRoot,
        string runId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count != 1 || string.IsNullOrWhiteSpace(stage.InputAdapter?.TargetKind))
            throw new OrchestratorDispatchException("binding.alias_invalid", "kind-alias.v1 requires exactly one source and one targetKind.");
        var alias = await CoreArtifactIO.PublishAliasAsync(jobRoot, runId, stage.StageId, sources[0], stage.InputAdapter.TargetKind, cancellationToken);
        return new ResolvedStageInput(new[] { alias }, stage.Payload, false);
    }

    private static async Task<ResolvedStageInput> BuildRemNoteProjectionAsync(
        string jobRoot,
        string runId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        CancellationToken cancellationToken)
    {
        if (stage.InputAdapter?.TargetKind is not ("learning.bundle" or "card.bundle"))
            throw new OrchestratorDispatchException("binding.remnote_projection_invalid", "content-to-remnote.v1 requires an explicit RemNote target kind.");
        if (stage.InputAdapter.MappingSnapshot is not JsonElement remNoteMapping || remNoteMapping.ValueKind != JsonValueKind.Object ||
            RequiredString(remNoteMapping, "categoryRegistryVersion") is not ("category-registry/1.0.0" or "category-registry/2.0.0") ||
            RequiredString(remNoteMapping, "blockRendering") != "single-block-verbatim-inline" ||
            RequiredString(remNoteMapping, "assetRefPolicy") != "reject-media-deferred")
            throw new OrchestratorDispatchException("binding.remnote_mapping_missing", "content-to-remnote.v1 requires an explicit RemNote projection snapshot.");

        var source = sources.SingleOrDefault(item => item.Kind == (stage.InputAdapter.TargetKind == "learning.bundle" ? "learning-bundle" : "card-bundle"))
            ?? throw new OrchestratorDispatchException("binding.remnote_projection_invalid", "Required content bundle is absent from RemNote projection inputs.");
        using var document = await ReadEntrypointJsonAsync(jobRoot, source, cancellationToken);
        var root = document.RootElement;
        object projection;
        if (stage.InputAdapter.TargetKind == "learning.bundle")
        {
            await ValidateLearningBundleAsync(jobRoot, source, root, cancellationToken);
            if (RequiredString(root, "release_state") != "ready")
                throw new OrchestratorDispatchException("binding.content_bundle_not_ready", "Only a ready content LearningBundle can be projected to RemNote.");
            var entries = root.GetProperty("items").EnumerateArray().SelectMany(item =>
            {
                if (RequiredString(item, "state") != "ready")
                    throw new OrchestratorDispatchException("binding.content_item_not_ready", $"Content item is not ready: {RequiredString(item, "item_id")}");
                var itemId = RequiredString(item, "item_id");
                var text = RequiredString(item.GetProperty("cleaning"), "cleaned_text");
                if (text.Length == 0)
                    throw new OrchestratorDispatchException("binding.content_item_empty", "Content learning item has empty cleaned_text.");
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                if (lines.Any(string.IsNullOrEmpty))
                    throw new OrchestratorDispatchException("binding.remnote_blank_line_unsupported", "LearningBundle.text contains a blank line that cannot be represented by ordered RemNote v2 learning entries.");
                return lines.Select((line, index) => new
                {
                    itemId = lines.Length == 1 ? itemId : $"{itemId}-line-{index + 1:D4}",
                    text = RequireRemNoteInline(line, $"LearningBundle.text[{index}]")
                });
            }).ToArray();
            if (entries.Length == 0)
                throw new OrchestratorDispatchException("binding.content_bundle_empty", "Content LearningBundle contains no ready entries.");
            projection = new { schemaVersion = "learning-bundle.v1", bundleId = RequiredString(root, "bundle_id"), entries };
        }
        else
        {
            RequireBundle(root, source, "card-bundle", "card-bundle/1.0.0");
            var learningReference = sources.SingleOrDefault(item => item.Kind == "learning-bundle");
            if (learningReference is not null)
                await ValidateContentPairAsync(jobRoot, source, learningReference, root, cancellationToken);
            if (RequiredString(root, "release_state") != "ready")
                throw new OrchestratorDispatchException("binding.content_bundle_not_ready", "Only a ready content CardBundle can be projected to RemNote.");
            var templateMappings = remNoteMapping.GetProperty("templateMappings");
            var allowedBlockTypes = remNoteMapping.GetProperty("allowedTextBlockTypes").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
            var cards = root.GetProperty("cards").EnumerateArray().Select(card =>
            {
                var template = RequiredString(card, "template");
                if (!templateMappings.TryGetProperty(template, out var policy) || policy.ValueKind != JsonValueKind.Object)
                    throw new OrchestratorDispatchException("binding.remnote_template_unknown", $"RemNote projection snapshot does not map template: {template}");
                var action = RequiredString(policy, "policy");
                if (action == "reject-media-deferred")
                    throw new OrchestratorDispatchException("binding.remnote_media_deferred", $"Template {template} requires deferred media support and cannot be auto-exported.");
                if (action != "project")
                    throw new OrchestratorDispatchException("binding.remnote_template_policy_unknown", $"Unsupported RemNote template policy: {action}");
                return new
                {
                    cardId = RequiredString(card, "card_id"),
                    cardType = RequiredString(policy, "cardType"),
                    direction = RequiredString(policy, "direction"),
                    front = RenderRemNoteBlock(card, "front_blocks", allowedBlockTypes, "CardBundle.front"),
                    back = RenderRemNoteBlock(card, "back_blocks", allowedBlockTypes, "CardBundle.back")
                };
            }).ToArray();
            if (cards.Length == 0)
                throw new OrchestratorDispatchException("binding.content_bundle_empty", "Content CardBundle contains no cards.");
            projection = new { schemaVersion = "card-bundle.v1", bundleId = RequiredString(root, "bundle_id"), cards };
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(projection, ContractJson.Options);
        var digest = Integrity.Sha256Hex(payload).ToLowerInvariant();
        var artifactId = $"remnote-{stage.InputAdapter.TargetKind.Replace('.', '-')}-{digest[..24]}";
        const string payloadName = "bundle.json";
        var reference = await CoreArtifactIO.PublishAsync(
            jobRoot, runId, stage.StageId, artifactId, stage.InputAdapter.TargetKind, "application/json", payloadName, payload, sources,
            JsonSerializer.SerializeToElement(new
            {
                entrypoint = $"runs/{runId}/adapters/{stage.StageId}/{artifactId}/v0001/{payloadName}",
                adapter = "content-to-remnote.v1",
                sourceSchemaVersion = RequiredString(root, "schema_version")
            }), cancellationToken);
        return new ResolvedStageInput(new[] { reference }, stage.Payload, false);
    }

    private static async Task<ResolvedStageInput> BuildAnkiProjectionAsync(
        string jobRoot,
        string runId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        CancellationToken cancellationToken)
    {
        var source = sources.SingleOrDefault(item => item.Kind == "card-bundle");
        var learningRef = sources.SingleOrDefault(item => item.Kind == "learning-bundle");
        var configRef = sources.SingleOrDefault(item => item.Kind == "anki-export-config");
        if (source is null || learningRef is null || configRef is null || sources.Count != 3 || stage.InputAdapter?.TargetKind != "card-bundle")
            throw new OrchestratorDispatchException("binding.anki_projection_invalid", "content-to-anki.v1 requires a paired content CardBundle/LearningBundle and one initial anki-export-config.");

        using var contentDocument = await ReadEntrypointJsonAsync(jobRoot, source, cancellationToken);
        using var configDocument = await ReadEntrypointJsonAsync(jobRoot, configRef, cancellationToken);
        var content = contentDocument.RootElement;
        var config = configDocument.RootElement;
        await ValidateContentPairAsync(jobRoot, source, learningRef, content, cancellationToken);
        RequireBundle(content, source, "card-bundle", "card-bundle/1.0.0");
        if (RequiredString(content, "release_state") != "ready")
            throw new OrchestratorDispatchException("binding.content_bundle_not_ready", "Only a ready content CardBundle can be projected to Anki.");
        if (RequiredString(config, "schema_version") != "anki-export-config.v1")
            throw new OrchestratorDispatchException("binding.anki_config_version_unknown", "Anki projection requires anki-export-config.v1.");
        if (stage.InputAdapter?.MappingSnapshot is not JsonElement mapping || mapping.ValueKind != JsonValueKind.Object ||
            RequiredString(mapping, "categoryRegistryVersion") is not ("category-registry/1.0.0" or "category-registry/2.0.0"))
            throw new OrchestratorDispatchException("binding.anki_mapping_missing", "content-to-anki.v1 requires a supported category registry mapping snapshot.");
        var categoryMappings = mapping.GetProperty("categoryToDeckRoute");
        if (RequiredString(mapping, "revisionPolicy") != "initial-revision-1" ||
            RequiredString(mapping, "contentHashPolicy") != "sha256-canonical-projection-v1" ||
            RequiredString(mapping, "assetRefPolicy") != "reject-media-deferred")
            throw new OrchestratorDispatchException("binding.anki_mapping_missing", "Anki projection policy snapshot is incomplete.");
        var configuredTags = mapping.GetProperty("tags").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var templateMappings = mapping.GetProperty("templateMappings");
        var routes = config.GetProperty("deck_routes");
        var noteTypes = config.GetProperty("note_types");

        var projectedCards = new List<object>();
        foreach (var card in content.GetProperty("cards").EnumerateArray())
        {
            var category = RequiredString(card, "category_id");
            var template = RequiredString(card, "template");
            if (!categoryMappings.TryGetProperty(category, out var routeValue) || routeValue.ValueKind != JsonValueKind.String)
                throw new OrchestratorDispatchException("binding.anki_category_unknown", $"Category is not mapped by the frozen registry snapshot: {category}");
            if (!templateMappings.TryGetProperty(template, out var templatePolicy) || templatePolicy.ValueKind != JsonValueKind.Object)
                throw new OrchestratorDispatchException("binding.anki_note_type_unknown", $"Template is not mapped by the frozen registry snapshot: {template}");
            var route = routeValue.GetString()!;
            var policy = RequiredString(templatePolicy, "policy");
            if (policy == "reject-media-deferred")
                throw new OrchestratorDispatchException("binding.anki_media_deferred", $"Template {template} requires deferred media support and cannot be auto-exported.");
            if (policy != "project")
                throw new OrchestratorDispatchException("binding.anki_template_policy_unknown", $"Unsupported Anki template policy: {policy}");
            var noteType = RequiredString(templatePolicy, "noteTypeKey");
            if (!routes.TryGetProperty(route, out _) || !noteTypes.TryGetProperty(noteType, out _))
                throw new OrchestratorDispatchException("binding.anki_route_missing", $"Anki config does not define route/note type: {route}/{noteType}");

            var front = RenderConfiguredTextBlock(card, "front_blocks", "Anki front");
            var back = RenderConfiguredTextBlock(card, "back_blocks", "Anki back");
            IReadOnlyDictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RequiredString(templatePolicy, "frontField")] = front,
                [RequiredString(templatePolicy, "backField")] = back
            };
            var revision = 1;
            var mediaRefs = Array.Empty<object>();
            var canonical = CanonicalJsonBytes(new
            {
                card_id = RequiredString(card, "card_id"),
                revision,
                deck_route_key = route,
                note_type_key = noteType,
                fields,
                tags = configuredTags.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                media_refs = mediaRefs
            });
            projectedCards.Add(new
            {
                card_id = RequiredString(card, "card_id"),
                revision,
                content_hash = Integrity.Sha256Hex(canonical).ToLowerInvariant(),
                status = "ready",
                deck_route_key = route,
                note_type_key = noteType,
                fields,
                tags = configuredTags,
                media_refs = mediaRefs
            });
        }
        if (projectedCards.Count == 0)
            throw new OrchestratorDispatchException("binding.content_bundle_empty", "Content CardBundle contains no cards.");

        var projection = new
        {
            bundle_kind = "CardBundle",
            schema_version = "1.0",
            bundle_id = RequiredString(content, "bundle_id"),
            cards = projectedCards,
            media = Array.Empty<object>()
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(projection, ContractJson.Options);
        var digest = Integrity.Sha256Hex(payload).ToLowerInvariant();
        var artifactId = $"anki-card-bundle-{digest[..24]}";
        const string payloadName = "card-bundle.v1.json";
        var projectedRef = await CoreArtifactIO.PublishAsync(
            jobRoot, runId, stage.StageId, artifactId, "card-bundle", "application/json", payloadName, payload, sources,
            JsonSerializer.SerializeToElement(new
            {
                entrypoint = $"runs/{runId}/adapters/{stage.StageId}/{artifactId}/v0001/{payloadName}",
                adapter = "content-to-anki.v1",
                sourceSchemaVersion = RequiredString(content, "schema_version"),
                ancestry = sources
            }), cancellationToken);
        return new ResolvedStageInput(new[] { projectedRef, configRef }, stage.Payload, false);
    }

    private static async Task<ResolvedStageInput> BuildOrderedOriginalImagesAsync(
        string jobRoot,
        string runId,
        string taskId,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationToken cancellationToken)
    {
        var captureReference = sources.SingleOrDefault(item => item.Kind == "capture-bundle")
            ?? throw new OrchestratorDispatchException("binding.original_media_capture_missing", "ordered-original-images.v1 requires one authoritative CaptureBundle.");
        var configReference = sources.SingleOrDefault(item => item.Kind == "anki-export-config")
            ?? throw new OrchestratorDispatchException("binding.original_media_config_missing", "ordered-original-images.v1 requires one SHA-bound Anki config artifact.");
        var mediaBundleReference = sources.SingleOrDefault(item => item.Kind == "image.original.bundle")
            ?? throw new OrchestratorDispatchException("binding.original_media_bundle_missing", "ordered-original-images.v1 requires the module-owned ordered media bundle.");
        var provenanceReference = sources.SingleOrDefault(item => item.Kind == "image.processing.provenance")
            ?? throw new OrchestratorDispatchException("binding.original_media_provenance_missing", "ordered-original-images.v1 requires the module-owned processing provenance.");
        var originalReferences = sources.Where(item => item.Kind == "image.original").ToArray();
        if (sources.Count != originalReferences.Length + 4 || originalReferences.Length == 0 || stage.InputAdapter?.TargetKind != "card-bundle")
            throw new OrchestratorDispatchException("binding.original_media_sources_invalid", "ordered-original-images.v1 accepts only CaptureBundle, Anki config, image.original artifacts, their ordered bundle, and processing provenance.");
        if (stage.InputAdapter.MappingSnapshot is not JsonElement mapping || mapping.ValueKind != JsonValueKind.Object ||
            RequiredString(mapping, "schemaVersion") != "knowledge-clipper-ordered-original-images-adapter.v1" ||
            RequiredString(mapping, "projection") != "ordered-original-images.v1" ||
            RequiredString(mapping, "noteTypeKey") != "original_image_qa")
            throw new OrchestratorDispatchException("binding.original_media_mapping_invalid", "Ordered original-media adapter snapshot is invalid.");

        using var captureDocument = await ReadEntrypointJsonAsync(jobRoot, captureReference, cancellationToken);
        using var configDocument = await ReadEntrypointJsonAsync(jobRoot, configReference, cancellationToken);
        using var mediaBundleDocument = await ReadEntrypointJsonAsync(jobRoot, mediaBundleReference, cancellationToken);
        using var provenanceDocument = await ReadEntrypointJsonAsync(jobRoot, provenanceReference, cancellationToken);
        var capture = captureDocument.RootElement;
        var config = configDocument.RootElement;
        var moduleBundle = mediaBundleDocument.RootElement;
        var provenance = provenanceDocument.RootElement;
        if (RequiredString(capture, "schemaVersion") != "capture-bundle.v1" ||
            capture.GetProperty("modeSnapshot").GetProperty("imageHandling").GetString() != "original")
            throw new OrchestratorDispatchException("binding.original_media_capture_invalid", "Original-media projection requires a frozen CaptureBundle original-mode snapshot.");
        if (RequiredString(config, "schema_version") != "anki-export-config.v2")
            throw new OrchestratorDispatchException("binding.anki_config_version_unknown", "Ordered original-media projection requires anki-export-config.v2.");
        if (RequiredString(moduleBundle, "schemaVersion") != "image-original-media-bundle.v1" ||
            RequiredString(moduleBundle, "mode") != "original_passthrough" || moduleBundle.GetProperty("providerCalls").GetInt32() != 0)
            throw new OrchestratorDispatchException("binding.original_media_bundle_invalid", "Module original-media bundle must prove original passthrough with zero provider calls.");
        if (RequiredString(provenance, "schemaVersion") != "image-processing-provenance.v1" ||
            RequiredString(provenance, "mode") != "original_passthrough" ||
            RequiredString(provenance, "terminalStatus") != "completed" ||
            provenance.GetProperty("provider").GetProperty("calls").GetInt32() != 0 ||
            provenance.GetProperty("continuation").GetProperty("defaultOcrRerun").GetBoolean())
            throw new OrchestratorDispatchException("binding.original_media_provenance_invalid", "Module processing provenance does not prove completed zero-provider original passthrough.");

        var route = RequiredString(mapping, "deckRouteKey");
        var noteType = RequiredString(mapping, "noteTypeKey");
        if (!config.GetProperty("deck_routes").TryGetProperty(route, out _) ||
            !config.GetProperty("note_types").TryGetProperty(noteType, out var noteTypeConfiguration) ||
            RequiredString(noteTypeConfiguration, "media_projection") != "ordered-original-images.v1")
            throw new OrchestratorDispatchException("binding.anki_route_missing", $"Anki config does not define the ordered original-media route/note type: {route}/{noteType}");

        if (!completed.TryGetValue("image-ocr", out var imageResult) || imageResult.Status != "completed" || imageResult.ItemDiagnostics is null)
            throw new OrchestratorDispatchException("binding.original_media_result_missing", "Original-media projection requires one completed image passthrough stage.");
        var diagnostics = imageResult.ItemDiagnostics.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var captureManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, captureReference, cancellationToken);
        var captureArtifacts = capture.GetProperty("artifacts").EnumerateArray().ToDictionary(item => RequiredString(item, "artifactRef"), item => item.Clone(), StringComparer.Ordinal);
        var ordered = new List<(string Role, int Ordinal, string ItemId)>();
        var ordinal = 0;
        foreach (var item in capture.GetProperty("questionImages").EnumerateArray())
            ordered.Add(("questionImages", ordinal++, item.GetString()!));
        ordinal = 0;
        foreach (var item in capture.GetProperty("answerImages").EnumerateArray())
            ordered.Add(("answerImages", ordinal++, item.GetString()!));
        if (ordered.Count == 0 || ordered.All(item => item.Role != "questionImages") || ordered.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != ordered.Count)
            throw new OrchestratorDispatchException("binding.original_media_order_invalid", "CaptureBundle original-media roles must be disjoint and include at least one question image.");
        var moduleOrder = moduleBundle.GetProperty("questionImages").EnumerateArray().Select(item => (Role: "questionImages", Item: item.Clone()))
            .Concat(moduleBundle.GetProperty("answerImages").EnumerateArray().Select(item => (Role: "answerImages", Item: item.Clone()))).ToArray();
        if (moduleOrder.Length != ordered.Count || !moduleOrder.Select((item, index) =>
                item.Role == ordered[index].Role && RequiredString(item.Item, "artifactRef") == ordered[index].ItemId).All(value => value))
            throw new OrchestratorDispatchException("binding.original_media_order_drift", "Module media bundle order differs from the authoritative CaptureBundle roles.");
        var provenanceOrder = provenance.GetProperty("sourceOrder");
        if (!provenanceOrder.GetProperty("questionImages").EnumerateArray().Select(item => item.GetString()).SequenceEqual(
                ordered.Where(item => item.Role == "questionImages").Select(item => item.ItemId)) ||
            !provenanceOrder.GetProperty("answerImages").EnumerateArray().Select(item => item.GetString()).SequenceEqual(
                ordered.Where(item => item.Role == "answerImages").Select(item => item.ItemId)))
            throw new OrchestratorDispatchException("binding.original_media_provenance_drift", "Module provenance order differs from the authoritative CaptureBundle roles.");

        var payloads = new List<CoreArtifactPayload>();
        var media = new List<object>();
        var mediaRefs = new List<object>();
        var verifiedOriginals = new List<ArtifactReference>();
        foreach (var item in ordered)
        {
            if (!captureArtifacts.TryGetValue(item.ItemId, out var captured) || RequiredString(captured, "mediaType") is not string capturedMediaType || !capturedMediaType.StartsWith("image/", StringComparison.Ordinal))
                throw new OrchestratorDispatchException("binding.original_media_capture_item_invalid", $"CaptureBundle image metadata is missing: {item.ItemId}");
            var capturedPath = RequiredString(captured, "relativePath");
            var capturedFile = captureManifest.Files.SingleOrDefault(file => file.Path == capturedPath)
                ?? throw new OrchestratorDispatchException("binding.original_media_capture_item_invalid", $"CaptureBundle does not own the declared image payload: {item.ItemId}");
            if (!capturedFile.Sha256.Equals(RequiredString(captured, "sha256"), StringComparison.OrdinalIgnoreCase) ||
                capturedFile.Bytes != captured.GetProperty("bytes").GetInt64() || capturedFile.MediaType != capturedMediaType)
                throw new OrchestratorDispatchException("binding.original_media_capture_item_drift", $"CaptureBundle image metadata drifted: {item.ItemId}");
            if (!diagnostics.TryGetValue(item.ItemId, out var diagnostic) || diagnostic.Status != "completed")
                throw new OrchestratorDispatchException("binding.original_media_item_incomplete", $"Original passthrough did not complete the ordered capture item: {item.ItemId}");
            var output = diagnostic.Outputs.Where(reference => reference.Kind == "image.original").ToArray();
            if (output.Length != 1 || !originalReferences.Any(reference => SameReference(reference, output[0])))
                throw new OrchestratorDispatchException("binding.original_media_output_invalid", $"Original passthrough must publish exactly one bound image.original output: {item.ItemId}");
            var originalManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, output[0], cancellationToken);
            if (originalManifest.Files.Count != 1 || !originalManifest.Files[0].MediaType.StartsWith("image/", StringComparison.Ordinal))
                throw new OrchestratorDispatchException("binding.original_media_output_invalid", $"image.original must contain exactly one image payload: {item.ItemId}");
            var originalFile = originalManifest.Files[0];
            if (!originalFile.Sha256.Equals(capturedFile.Sha256, StringComparison.OrdinalIgnoreCase) || originalFile.Bytes != capturedFile.Bytes || originalFile.MediaType != capturedFile.MediaType)
                throw new OrchestratorDispatchException("binding.original_media_bytes_drift", $"image.original differs from the authoritative captured bytes: {item.ItemId}");
            var moduleItem = moduleOrder.Single(entry => RequiredString(entry.Item, "artifactRef") == item.ItemId).Item;
            if (!RequiredString(moduleItem, "sha256").Equals(originalFile.Sha256, StringComparison.OrdinalIgnoreCase) ||
                moduleItem.GetProperty("bytes").GetInt64() != originalFile.Bytes || RequiredString(moduleItem, "mediaType") != originalFile.MediaType)
                throw new OrchestratorDispatchException("binding.original_media_bundle_drift", $"Module media bundle bytes differ from image.original: {item.ItemId}");
            var bytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, originalFile.Path), cancellationToken);
            var extension = OriginalMediaExtension(originalFile.MediaType);
            var fileName = $"{(item.Role == "questionImages" ? "q" : "a")}{item.Ordinal:D4}-{originalFile.Sha256.ToLowerInvariant()}{extension}";
            payloads.Add(new CoreArtifactPayload(fileName, bytes, originalFile.MediaType));
            var projectedPath = $"runs/{runId}/adapters/{stage.StageId}/original-media-{captureReference.ExpectedSha256.ToLowerInvariant()[..24]}/v0001/{fileName}";
            media.Add(new { media_id = item.ItemId, kind = "image", relative_path = projectedPath, sha256 = originalFile.Sha256.ToLowerInvariant(), size = originalFile.Bytes, mime_type = originalFile.MediaType });
            mediaRefs.Add(new { media_id = item.ItemId, field = item.Role == "questionImages" ? "question" : "answer", role = item.Role, ordinal = item.Ordinal, placement = "append" });
            verifiedOriginals.Add(output[0]);
        }

        var tags = mapping.GetProperty("tags").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["question"] = "Original question images",
            ["answer"] = string.Empty,
            ["source"] = "capture-bundle.v1"
        };
        var cardId = $"original-media-{captureReference.ExpectedSha256.ToLowerInvariant()[..24]}";
        var mediaById = media.Select(item => JsonSerializer.SerializeToElement(item, ContractJson.Options)).ToDictionary(item => RequiredString(item, "media_id"), StringComparer.Ordinal);
        var canonicalRefs = mediaRefs.Select(reference =>
        {
            var element = JsonSerializer.SerializeToElement(reference, ContractJson.Options);
            var mediaItem = mediaById[RequiredString(element, "media_id")];
            return new
            {
                media_id = RequiredString(element, "media_id"), field = RequiredString(element, "field"), placement = RequiredString(element, "placement"),
                kind = RequiredString(mediaItem, "kind"), sha256 = RequiredString(mediaItem, "sha256"),
                role = RequiredString(element, "role"), ordinal = element.GetProperty("ordinal").GetInt32()
            };
        }).ToArray();
        var contentHash = Integrity.Sha256Hex(CanonicalJsonBytes(new
        {
            card_id = cardId, revision = 1, deck_route_key = route, note_type_key = noteType,
            fields, tags = tags.OrderBy(value => value, StringComparer.Ordinal).ToArray(), media_refs = canonicalRefs
        })).ToLowerInvariant();
        var projection = new
        {
            bundle_kind = "CardBundle", schema_version = "1.1", bundle_id = $"original-media-{taskId}",
            cards = new[] { new { card_id = cardId, revision = 1, content_hash = contentHash, status = "ready", deck_route_key = route, note_type_key = noteType, fields, tags, media_refs = mediaRefs } },
            media
        };
        var bundleBytes = JsonSerializer.SerializeToUtf8Bytes(projection, ContractJson.Options);
        const string payloadName = "card-bundle.v1.1.json";
        payloads.Insert(0, new CoreArtifactPayload(payloadName, bundleBytes, "application/json"));
        var artifactId = $"original-media-{captureReference.ExpectedSha256.ToLowerInvariant()[..24]}";
        // The derived CardBundle owns content-addressed copies of every verified byte. Preserve
        // each image.original zero-copy alias as recursive provenance as well: Anki 1.3.1 admits
        // such an alias only when its descriptor is an exact transparent view of a canonical,
        // SHA-bound initial Capture artifact. Arbitrary job-root paths remain rejected.
        var ancestry = new[] { captureReference, configReference, mediaBundleReference, provenanceReference }.Concat(verifiedOriginals).ToArray();
        var projectedReference = await CoreArtifactIO.PublishFilesAsync(
            jobRoot, runId, stage.StageId, artifactId, "card-bundle", "application/json", payloads, ancestry,
            JsonSerializer.SerializeToElement(new
            {
                entrypoint = $"runs/{runId}/adapters/{stage.StageId}/{artifactId}/v0001/{payloadName}",
                adapter = "ordered-original-images.v1", sourceMode = "original", ocrInvoked = false,
                ordering = "questionImages-then-answerImages", ancestry,
                validatedOriginals = verifiedOriginals
            }, ContractJson.Options), cancellationToken);
        return new ResolvedStageInput(new[] { projectedReference, configReference }, stage.Payload, false);
    }

    private static string OriginalMediaExtension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => throw new OrchestratorDispatchException("binding.original_media_type_unsupported", $"Unsupported original image media type: {mediaType}")
    };

    private static async Task<ExtractedText> ExtractTextAsync(
        string jobRoot,
        PipelineStage stage,
        IReadOnlyList<ArtifactReference> sources,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
            throw new OrchestratorDispatchException("binding.text_source_missing", "Text adapter has no source artifact.");
        var pieces = new List<string>();
        var contextMode = "selection_only";
        var captureSource = sources.SingleOrDefault(item => item.Kind == "capture-bundle");
        var ocrSources = sources.Where(item => item.Kind == "image-ocr.markdown").ToArray();
        if (captureSource is not null && ocrSources.Length > 0)
        {
            var orderedOcr = await OrderOcrOutputsAsync(jobRoot, captureSource, stage, completed, cancellationToken);
            foreach (var item in orderedOcr)
            {
                var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, item.Reference, cancellationToken);
                var content = new List<string>();
                foreach (var file in manifest.Files.Where(file => file.MediaType is "text/markdown" or "text/plain"))
                    content.Add(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, file.Path), new UTF8Encoding(false, true), cancellationToken));
                if (content.Count == 0)
                    throw new OrchestratorDispatchException("binding.ocr_markdown_missing", $"OCR item has no markdown payload: {item.Reference.ArtifactRef}");
                pieces.Add($"## {item.Role} {item.Index}\n\n{string.Join("\n", content).Trim()}");
            }
        }
        foreach (var source in sources)
        {
            if (source.Kind == "capture-bundle")
            {
                var capture = await ExtractCaptureTextAsync(jobRoot, source, cancellationToken);
                pieces.Add(capture.Text);
                contextMode = capture.ContextMode;
                continue;
            }
            if (source.Kind == "image-ocr.markdown")
            {
                if (captureSource is not null) continue;
                var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, source, cancellationToken);
                foreach (var file in manifest.Files.Where(item => item.MediaType is "text/markdown" or "text/plain" or "application/json"))
                    pieces.Add(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, file.Path), new UTF8Encoding(false, true), cancellationToken));
                continue;
            }
            throw new OrchestratorDispatchException("binding.text_source_unsupported", $"Text adapter cannot consume {source.Kind}.");
        }
        var text = string.Join("\n\n", pieces.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()));
        if (string.IsNullOrWhiteSpace(text))
            throw new OrchestratorDispatchException("binding.text_empty", "Text adapter produced empty text.");
        return new ExtractedText(text, contextMode);
    }

    private sealed record OrderedOcr(string Role, int Index, ArtifactReference Reference);

    private static async Task<IReadOnlyList<OrderedOcr>> OrderOcrOutputsAsync(
        string jobRoot,
        ArtifactReference captureReference,
        PipelineStage stage,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationToken cancellationToken)
    {
        using var captureDocument = await ReadEntrypointJsonAsync(jobRoot, captureReference, cancellationToken);
        var root = captureDocument.RootElement;
        var requested = new List<(string Role, int Index, string ItemId)>();
        var index = 0;
        foreach (var item in root.GetProperty("questionImages").EnumerateArray())
            requested.Add(("Question image", ++index, item.GetString()!));
        index = 0;
        foreach (var item in root.GetProperty("answerImages").EnumerateArray())
            requested.Add(("Answer image", ++index, item.GetString()!));
        if (requested.Count == 0)
            throw new OrchestratorDispatchException("binding.ocr_capture_order_missing", "OCR binding has no ordered capture image references.");

        var diagnostics = new Dictionary<string, ItemDiagnostic>(StringComparer.Ordinal);
        foreach (var binding in stage.InputBindings ?? Array.Empty<StageOutputBinding>())
        {
            if (!completed.TryGetValue(binding.FromStageId, out var result) || result.Status != "completed")
                throw new OrchestratorDispatchException("binding.source_not_consumable", $"OCR source is not completed: {binding.FromStageId}");
            foreach (var diagnostic in result.ItemDiagnostics ?? Array.Empty<ItemDiagnostic>())
                if (!diagnostics.TryAdd(diagnostic.ItemId, diagnostic))
                    throw new OrchestratorDispatchException("binding.ocr_item_duplicate", $"OCR item diagnostic is duplicated: {diagnostic.ItemId}");
        }

        var output = new List<OrderedOcr>();
        foreach (var item in requested)
        {
            if (!diagnostics.TryGetValue(item.ItemId, out var diagnostic) || diagnostic.Status != "completed")
                throw new OrchestratorDispatchException("binding.ocr_item_incomplete", $"OCR did not complete the ordered capture item: {item.ItemId}");
            var markdown = diagnostic.Outputs.Where(reference => reference.Kind == "image-ocr.markdown").ToArray();
            if (markdown.Length != 1)
                throw new OrchestratorDispatchException("binding.ocr_item_output_invalid", $"OCR item must publish exactly one markdown output: {item.ItemId}");
            await CoreArtifactIO.ValidateReferenceAsync(jobRoot, markdown[0], cancellationToken);
            output.Add(new OrderedOcr(item.Role, item.Index, markdown[0]));
        }
        return output;
    }

    private static async Task<JsonDocument> ReadEntrypointJsonAsync(string jobRoot, ArtifactReference source, CancellationToken cancellationToken)
    {
        var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, source, cancellationToken);
        var file = ResolveJsonEntrypoint(manifest, source);
        return JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, file.Path), cancellationToken));
    }

    private static CoreArtifactFile ResolveJsonEntrypoint(CoreArtifactManifest manifest, ArtifactReference source)
    {
        var entrypoint = manifest.Metadata.TryGetProperty("entrypoint", out var entry) ? entry.GetString() : null;
        CoreArtifactFile? file;
        if (!string.IsNullOrWhiteSpace(entrypoint))
        {
            file = manifest.Files.SingleOrDefault(item => item.Path == entrypoint)
                ?? throw new OrchestratorDispatchException("binding.entrypoint_missing", $"Artifact declares a missing JSON entrypoint: {source.ArtifactRef}");
        }
        else
        {
            var jsonFiles = manifest.Files
                .Where(item => IsJsonMediaType(item.MediaType))
                .ToArray();
            file = jsonFiles.Length == 1
                ? jsonFiles[0]
                : throw new OrchestratorDispatchException("binding.entrypoint_missing", $"Artifact without an entrypoint must contain exactly one JSON file: {source.ArtifactRef}");
        }
        if (!IsJsonMediaType(file.MediaType))
            throw new OrchestratorDispatchException("binding.entrypoint_media_invalid", $"Artifact JSON entrypoint has invalid media type: {source.ArtifactRef}/{file.MediaType}");
        return file;
    }

    private static bool IsJsonMediaType(string mediaType) =>
        mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private static void RequireBundle(JsonElement root, ArtifactReference source, string sourceKind, string schemaVersion)
    {
        if (source.Kind != sourceKind || RequiredString(root, "schema_version") != schemaVersion)
            throw new OrchestratorDispatchException("binding.content_bundle_version_unknown", $"Expected {sourceKind}/{schemaVersion}.");
    }

    internal static async Task<string> ReadValidatedLearningReleaseStateAsync(
        string jobRoot,
        ArtifactReference source,
        CancellationToken cancellationToken)
    {
        using var document = await ReadEntrypointJsonAsync(jobRoot, source, cancellationToken);
        var root = document.RootElement;
        await ValidateLearningBundleAsync(jobRoot, source, root, cancellationToken);
        var releaseState = RequiredString(root, "release_state");
        if (releaseState is not ("ready" or "partial" or "unresolved" or "failed"))
            throw new OrchestratorDispatchException("binding.content_release_state_unknown", $"Unsupported LearningBundle release_state: {releaseState}.");
        if (!root.TryGetProperty("unresolved_items", out var unresolved) || unresolved.ValueKind != JsonValueKind.Array)
            throw new OrchestratorDispatchException("binding.projection_contract_invalid", "LearningBundle unresolved_items must be an array.");
        if (releaseState == "ready" && unresolved.GetArrayLength() != 0)
            throw new OrchestratorDispatchException("binding.content_unresolved", "A ready LearningBundle cannot contain unresolved items.");
        return releaseState;
    }

    private static async Task ValidateLearningBundleAsync(
        string jobRoot,
        ArtifactReference source,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (source.Kind != "learning-bundle")
            throw new OrchestratorDispatchException("binding.content_bundle_version_unknown", "Expected learning-bundle.");
        var schemaVersion = RequiredString(root, "schema_version");
        if (schemaVersion == "learning-bundle/1.0.0") return;
        if (schemaVersion == "learning-bundle/3.0.0" && source.ContractVersion == "3.0.0")
        {
            var learningManifestV3 = await CoreArtifactIO.ReadManifestAsync(jobRoot, source, cancellationToken);
            var resolutionReferences = learningManifestV3.InputRefs.Where(item => item.Kind == "ocr-human-resolution-bundle").ToArray();
            if (resolutionReferences.Length != 1 || resolutionReferences[0].ContractVersion != "1.0.0")
                throw new OrchestratorDispatchException("binding.ocr_human_resolution_ancestry_invalid", "LearningBundle v3 requires exactly one immutable OCR human-resolution bundle.");
            var resolutionManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, resolutionReferences[0], cancellationToken);
            var resolutionFile = ResolveJsonEntrypoint(resolutionManifest, resolutionReferences[0]);
            using var resolutionDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, resolutionFile.Path), cancellationToken));
            var resolution = resolutionDocument.RootElement;
            if (RequiredString(resolution, "schema_version") != "ocr-human-resolution-bundle/1.0.0" || RequiredString(resolution, "release_state") != "ready")
                throw new OrchestratorDispatchException("binding.ocr_human_resolution_release_invalid", "LearningBundle v3 requires a ready OCR human-resolution bundle.");
            var learningFileV3 = ResolveJsonEntrypoint(learningManifestV3, source);
            var declaredPathV3 = RequiredString(root, "ocr_human_resolution_bundle_path");
            var normalizedPathV3 = Path.GetFileName(declaredPathV3) == declaredPathV3
                ? Path.Combine(Path.GetDirectoryName(learningFileV3.Path.Replace('/', Path.DirectorySeparatorChar))!, declaredPathV3).Replace('\\', '/')
                : PathPolicy.ToJobRelative(jobRoot, PathPolicy.ResolveJobRelative(jobRoot, declaredPathV3));
            if (normalizedPathV3 != resolutionFile.Path || !RequiredString(root, "ocr_human_resolution_bundle_sha256").Equals(resolutionFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorDispatchException("binding.ocr_human_resolution_ancestry_invalid", "LearningBundle v3 human-resolution path/hash ancestry is invalid.");
            return;
        }
        if (schemaVersion != "learning-bundle/2.0.0" || source.ContractVersion != "2.0.0")
            throw new OrchestratorDispatchException("binding.content_bundle_version_unknown", $"Unsupported learning-bundle version: {schemaVersion}/{source.ContractVersion}.");

        var learningManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, source, cancellationToken);
        var proofreadReferences = learningManifest.InputRefs.Where(item => item.Kind == "ocr-proofread-bundle").ToArray();
        if (proofreadReferences.Length != 1)
            throw new OrchestratorDispatchException("binding.ocr_proofread_ancestry_invalid", "LearningBundle v2 requires exactly one immutable OCR proofreading ancestry artifact.");
        var proofreadReference = proofreadReferences[0];
        if (proofreadReference.ContractVersion != "1.0.0")
            throw new OrchestratorDispatchException("binding.ocr_proofread_ancestry_invalid", "OCR proofreading ancestry uses an unsupported contract version.");
        var proofreadManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, proofreadReference, cancellationToken);
        var proofreadFile = ResolveJsonEntrypoint(proofreadManifest, proofreadReference);
        using var proofreadDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, proofreadFile.Path), cancellationToken));
        var proofread = proofreadDocument.RootElement;
        if (RequiredString(proofread, "schema_version") != "ocr-proofread-bundle/1.0.0")
            throw new OrchestratorDispatchException("binding.ocr_proofread_ancestry_invalid", "OCR proofreading ancestry payload uses an unsupported schema.");

        var learningFile = ResolveJsonEntrypoint(learningManifest, source);
        var declaredPath = RequiredString(root, "ocr_proofread_bundle_path");
        var normalizedPath = Path.GetFileName(declaredPath) == declaredPath
            ? Path.Combine(Path.GetDirectoryName(learningFile.Path.Replace('/', Path.DirectorySeparatorChar))!, declaredPath).Replace('\\', '/')
            : PathPolicy.ToJobRelative(jobRoot, PathPolicy.ResolveJobRelative(jobRoot, declaredPath));
        if (normalizedPath != proofreadFile.Path || !RequiredString(root, "ocr_proofread_bundle_sha256").Equals(proofreadFile.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("binding.ocr_proofread_ancestry_invalid", "LearningBundle v2 OCR proofreading path/hash ancestry is invalid.");
        if (RequiredString(root, "release_state") == "ready" && RequiredString(proofread, "release_state") != "ready")
            throw new OrchestratorDispatchException("binding.ocr_proofread_release_mismatch", "A ready LearningBundle v2 cannot reference unresolved OCR proofreading.");
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new OrchestratorDispatchException("binding.projection_contract_invalid", $"Projection input is missing required text: {property}");
        return value.GetString()!;
    }

    private static string RenderRemNoteBlock(JsonElement card, string property, IReadOnlySet<string> allowedTypes, string field)
    {
        if (!card.TryGetProperty(property, out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new OrchestratorDispatchException("binding.projection_contract_invalid", $"Content card is missing {property}.");
        if (blocks.GetArrayLength() != 1)
            throw new OrchestratorDispatchException("binding.remnote_block_structure_unsupported", $"{field} requires exactly one block under single-block-verbatim-inline.");
        var block = blocks[0];
        var type = RequiredString(block, "type");
        if (type == "asset_ref")
            throw new OrchestratorDispatchException("binding.remnote_media_deferred", $"{field} contains asset_ref and media export is deferred.");
        if (!allowedTypes.Contains(type))
            throw new OrchestratorDispatchException("binding.remnote_block_type_unsupported", $"{field} block type is not allowed by the projection snapshot: {type}");
        return RequireRemNoteInline(RequiredString(block, "text"), field);
    }

    private static string RenderConfiguredTextBlock(JsonElement card, string property, string field)
    {
        if (!card.TryGetProperty(property, out var blocks) || blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() != 1)
            throw new OrchestratorDispatchException("binding.anki_block_structure_unsupported", $"{field} requires exactly one configured block.");
        var block = blocks[0];
        var type = RequiredString(block, "type");
        if (type == "asset_ref")
            throw new OrchestratorDispatchException("binding.anki_media_deferred", $"{field} contains asset_ref and media export is deferred.");
        if (type is not ("text" or "markdown"))
            throw new OrchestratorDispatchException("binding.anki_block_type_unsupported", $"{field} block type is unsupported: {type}");
        return RequiredString(block, "text");
    }

    private static string RequireRemNoteInline(string value, string field)
    {
        if (value.Length > 20_000 || value.IndexOfAny(new[] { '\r', '\n' }) >= 0 || value.Any(char.IsControl))
            throw new OrchestratorDispatchException("binding.remnote_inline_unsupported", $"{field} cannot be represented losslessly by RemNote exporter v1 single-line text.");
        foreach (var delimiter in new[] { ">>", "==", "<<", "<>", "><" })
            if (value.Contains(delimiter, StringComparison.Ordinal))
                throw new OrchestratorDispatchException("binding.remnote_reserved_delimiter", $"{field} contains a RemNote v1 reserved card delimiter: {delimiter}");
        return value;
    }

    private static byte[] CanonicalJsonBytes(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, ContractJson.Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, element);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static async Task ValidateContentPairAsync(
        string jobRoot,
        ArtifactReference cardReference,
        ArtifactReference learningReference,
        JsonElement cards,
        CancellationToken cancellationToken)
    {
        using var learningDocument = await ReadEntrypointJsonAsync(jobRoot, learningReference, cancellationToken);
        var learning = learningDocument.RootElement;
        await ValidateLearningBundleAsync(jobRoot, learningReference, learning, cancellationToken);
        var learningSchemaVersion = RequiredString(learning, "schema_version");
        var permitsHumanSubmitted = learningSchemaVersion == "learning-bundle/3.0.0" && learningReference.ContractVersion == "3.0.0";
        if (RequiredString(cards, "release_state") != "ready" || RequiredString(learning, "release_state") != "ready")
            throw new OrchestratorDispatchException("binding.content_bundle_not_ready", "Content CardBundle/LearningBundle pair must both be ready.");
        foreach (var field in new[] { "package_id", "task_id", "attempt_id" })
            if (RequiredString(cards, field) != RequiredString(learning, field))
                throw new OrchestratorDispatchException("binding.content_lineage_mismatch", $"Content bundle lineage differs at {field}.");
        var learningManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, learningReference, cancellationToken);
        var learningFile = ResolveJsonEntrypoint(learningManifest, learningReference);
        if (!learningFile.Sha256.Equals(RequiredString(cards, "learning_bundle_sha256"), StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("binding.content_lineage_hash_mismatch", "CardBundle learning_bundle_sha256 does not match the paired LearningBundle payload.");
        if (cards.GetProperty("withheld_units").GetArrayLength() != 0 || learning.GetProperty("unresolved_items").GetArrayLength() != 0)
            throw new OrchestratorDispatchException("binding.content_unresolved", "Unresolved or withheld content cannot be projected as ready.");

        // LearningBundle v2 may legitimately use the immutable proofreading segment ids
        // (pseg-*) rather than the raw OCR source ids (seg-*).  Do not treat the
        // LearningBundle payload itself as sufficient authority for those ids: the
        // segments must be byte-for-byte represented by the one hash-bound
        // ocr-proofread-bundle that ValidateLearningBundleAsync already established.
        var proofreadSegmentsByItem = await ReadVerifiedProofreadSegmentsAsync(jobRoot, learningReference, learning, cancellationToken);

        var items = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in learning.GetProperty("items").EnumerateArray())
            if (!items.TryAdd(RequiredString(item, "item_id"), item.Clone()))
                throw new OrchestratorDispatchException("binding.content_duplicate_id", "LearningBundle contains duplicate item_id.");
        var cardIds = new HashSet<string>(StringComparer.Ordinal);
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards.GetProperty("cards").EnumerateArray())
        {
            var cardId = RequiredString(card, "card_id");
            var template = RequiredString(card, "template");
            if (!cardIds.Add(cardId)) throw new OrchestratorDispatchException("binding.content_duplicate_id", $"Duplicate card_id: {cardId}");
            var itemId = RequiredString(card, "item_id");
            if (!items.TryGetValue(itemId, out var item) || RequiredString(item, "state") != "ready")
                throw new OrchestratorDispatchException("binding.content_reference_missing", $"Card references a missing/non-ready item: {itemId}");
            var unitId = RequiredString(card, "unit_id");
            var units = item.GetProperty("units").EnumerateArray().Where(unit => RequiredString(unit, "unit_id") == unitId).ToArray();
            if (units.Length != 1 || RequiredString(units[0], "state") != "ready")
                throw new OrchestratorDispatchException("binding.content_reference_missing", $"Card references a missing/non-ready unit: {unitId}");
            var unit = units[0];
            if (RequiredString(card, "category_id") != RequiredString(unit, "category_id") || RequiredString(card, "subtype") != RequiredString(unit, "subtype"))
                throw new OrchestratorDispatchException("binding.content_semantics_mismatch", $"Card category/subtype differs from its unit: {cardId}");
            var segments = DistinctSegments(item.GetProperty("source_segments"), "source");
            if (proofreadSegmentsByItem.TryGetValue(itemId, out var proofreadSegments))
            {
                foreach (var (segmentId, segment) in proofreadSegments)
                    if (!segments.TryAdd(segmentId, segment))
                        throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", $"OCR proofread segment collides with a source segment: {segmentId}");
            }
            foreach (var segmentId in card.GetProperty("source_segment_ids").EnumerateArray().Select(value => value.GetString()!))
                if (!segments.ContainsKey(segmentId)) throw new OrchestratorDispatchException("binding.content_reference_missing", $"Card references a missing source segment: {segmentId}");
            var generated = unit.GetProperty("generated_content").EnumerateArray().Select(value => RequiredString(value, "text")).ToHashSet(StringComparer.Ordinal);
            var sourcePaths = item.GetProperty("source_refs").EnumerateArray().Select(value => RequiredString(value, "path")).ToHashSet(StringComparer.Ordinal);
            foreach (var block in card.GetProperty("front_blocks").EnumerateArray().Concat(card.GetProperty("back_blocks").EnumerateArray()))
            {
                var blockId = RequiredString(block, "block_id");
                if (!blockIds.Add(blockId)) throw new OrchestratorDispatchException("binding.content_duplicate_id", $"Duplicate block_id: {blockId}");
                var type = RequiredString(block, "type");
                var provenance = RequiredString(block, "provenance");
                if (type is not ("text" or "markdown" or "asset_ref") ||
                    provenance is not ("source" or "generated" or "human_confirmed" or "human_submitted") ||
                    (provenance == "human_submitted" && !permitsHumanSubmitted))
                    throw new OrchestratorDispatchException("binding.content_enum_unknown", $"Unknown content block type/provenance: {type}/{provenance}");
                if (provenance == "human_confirmed" && template != "language_writing")
                    throw new OrchestratorDispatchException("binding.content_provenance_mismatch", $"human_confirmed blocks are only valid for language_writing: {blockId}");
                if (type == "asset_ref")
                {
                    var materialPath = RequiredString(block, "material_path");
                    if (!sourcePaths.Contains(materialPath)) throw new OrchestratorDispatchException("binding.content_reference_missing", $"Asset block is not present in item source_refs: {materialPath}");
                }
                else
                {
                    var text = RequiredString(block, "text");
                    if (provenance == "generated" && !generated.Contains(text))
                        throw new OrchestratorDispatchException("binding.content_provenance_mismatch", $"Generated block text is absent from unit generated_content: {blockId}");
                    if (provenance is "source" or "human_submitted")
                    {
                        var normalized = text.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal);
                        if (!segments.Values.Any(segment => RequiredString(segment, "text").Contains(normalized, StringComparison.Ordinal)))
                            throw new OrchestratorDispatchException("binding.content_provenance_mismatch", $"Source/human-submitted block cannot be traced to a source segment: {blockId}");
                    }
                }
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>> ReadVerifiedProofreadSegmentsAsync(
        string jobRoot,
        ArtifactReference learningReference,
        JsonElement learning,
        CancellationToken cancellationToken)
    {
        var learningSchema = RequiredString(learning, "schema_version");
        if (learningSchema != "learning-bundle/2.0.0")
            return new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);

        var learningManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, learningReference, cancellationToken);
        var proofreadReferences = learningManifest.InputRefs.Where(item => item.Kind == "ocr-proofread-bundle" && item.ContractVersion == "1.0.0").ToArray();
        if (proofreadReferences.Length != 1)
            throw new OrchestratorDispatchException("binding.ocr_proofread_ancestry_invalid", "LearningBundle must bind exactly one OCR proofreading artifact.");
        var proofreadManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, proofreadReferences[0], cancellationToken);
        var proofreadFile = ResolveJsonEntrypoint(proofreadManifest, proofreadReferences[0]);
        using var proofreadDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, proofreadFile.Path), cancellationToken));
        var proofread = proofreadDocument.RootElement;
        var proofreadState = RequiredString(proofread, "release_state");
        if (RequiredString(proofread, "schema_version") != "ocr-proofread-bundle/1.0.0" || proofreadState != "ready")
            throw new OrchestratorDispatchException("binding.ocr_proofread_release_mismatch", "LearningBundle proofreading release state is incompatible with its immutable continuation ancestry.");

        var proofreadItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in proofread.GetProperty("items").EnumerateArray())
            if (!proofreadItems.TryAdd(RequiredString(item, "item_id"), item.Clone()))
                throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", "OCR proofreading artifact contains duplicate item_id.");

        var result = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var learningItem in learning.GetProperty("items").EnumerateArray())
        {
            var itemId = RequiredString(learningItem, "item_id");
            if (!proofreadItems.Remove(itemId, out var proofreadItem) || RequiredString(proofreadItem, "state") != proofreadState)
                throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", $"LearningBundle item is not matched by its immutable proofreading item: {itemId}");
            if (!learningItem.TryGetProperty("proofreading", out var learningProofreading) || learningProofreading.ValueKind != JsonValueKind.Object ||
                RequiredString(learningProofreading, "state") != proofreadState)
                throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", $"LearningBundle item lacks immutable proofreading state: {itemId}");
            var declared = DistinctSegments(learningProofreading.GetProperty("proofread_segments"), "learning proofreading");
            var authoritative = DistinctSegments(proofreadItem.GetProperty("proofread_segments"), "proofread artifact");
            if (declared.Count != authoritative.Count || declared.Any(pair => !authoritative.TryGetValue(pair.Key, out var segment) ||
                    !Integrity.Sha256Hex(CanonicalJsonBytes(pair.Value)).Equals(Integrity.Sha256Hex(CanonicalJsonBytes(segment)), StringComparison.OrdinalIgnoreCase)))
                throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", $"LearningBundle proofread segments drift from immutable proofreading artifact: {itemId}");
            result.Add(itemId, new Dictionary<string, JsonElement>(declared, StringComparer.Ordinal));
        }
        if (proofreadItems.Count != 0)
            throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", "OCR proofreading artifact has items not represented by LearningBundle.");
        return result;
    }

    private static Dictionary<string, JsonElement> DistinctSegments(JsonElement segments, string scope)
    {
        if (segments.ValueKind != JsonValueKind.Array)
            throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", $"{scope} segments must be an array.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var segment in segments.EnumerateArray())
            if (!result.TryAdd(RequiredString(segment, "segment_id"), segment.Clone()))
                throw new OrchestratorDispatchException("binding.ocr_proofread_segment_invalid", $"{scope} contains duplicate segment_id.");
        return result;
    }

    private static async Task<ExtractedText> ExtractCaptureTextAsync(string jobRoot, ArtifactReference source, CancellationToken cancellationToken)
    {
        var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, source, cancellationToken);
        var entrypoint = manifest.Metadata.TryGetProperty("entrypoint", out var entry) ? entry.GetString() : null;
        var bundleFile = manifest.Files.SingleOrDefault(item => item.Path == entrypoint) ?? manifest.Files.Single();
        using var bundle = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, bundleFile.Path), cancellationToken));
        var root = bundle.RootElement;
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToDictionary(item => item.GetProperty("artifactRef").GetString()!, item => item.Clone(), StringComparer.Ordinal);
        var ordered = new List<string>();
        var contextMode = "selection_only";
        if (root.TryGetProperty("contextSegments", out var context) && context.ValueKind == JsonValueKind.Object &&
            root.GetProperty("modeSnapshot").GetProperty("contextApplied").GetBoolean())
        {
            foreach (var name in new[] { "prefixArtifactRef", "selectionArtifactRef", "suffixArtifactRef" })
                if (context.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) ordered.Add(value.GetString()!);
            contextMode = root.GetProperty("modeSnapshot").GetProperty("contextGranularity").GetString() == "paragraph"
                ? "selection_plus_paragraph" : "selection_plus_sentence";
        }
        else if (root.TryGetProperty("selectedTextArtifactRef", out var selected) && selected.ValueKind == JsonValueKind.String)
        {
            ordered.Add(selected.GetString()!);
        }
        else if (root.TryGetProperty("questionTurns", out var turns))
        {
            ordered.AddRange(turns.EnumerateArray().OrderBy(item => item.GetProperty("turnIndex").GetInt32()).Select(item => item.GetProperty("artifactRef").GetString()!));
        }
        if (ordered.Count == 0)
            ordered.AddRange(artifacts.Where(item => item.Value.GetProperty("mediaType").GetString()?.StartsWith("text/", StringComparison.Ordinal) == true).Select(item => item.Key));

        var pieces = new List<string>();
        foreach (var artifactRef in ordered.Distinct(StringComparer.Ordinal))
        {
            if (!artifacts.TryGetValue(artifactRef, out var artifact))
                throw new OrchestratorDispatchException("binding.capture_reference_missing", $"Capture text reference is absent: {artifactRef}");
            var relativePath = artifact.GetProperty("relativePath").GetString()!;
            var expectedBytes = artifact.GetProperty("bytes").GetInt64();
            var expectedHash = artifact.GetProperty("sha256").GetString()!;
            await Integrity.VerifyFileAsync(jobRoot, relativePath, expectedBytes, expectedHash, cancellationToken);
            pieces.Add(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, relativePath), new UTF8Encoding(false, true), cancellationToken));
        }
        return new ExtractedText(string.Join("\n", pieces), contextMode);
    }

    private static JsonElement Merge(JsonElement original, IReadOnlyDictionary<string, object?> overrides)
    {
        var values = original.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value.Clone(), StringComparer.Ordinal);
        foreach (var item in overrides) values[item.Key] = item.Value;
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }
}
