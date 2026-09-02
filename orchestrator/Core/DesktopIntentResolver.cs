using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed class DesktopIntentResolver
{
    private static readonly HashSet<string> ProcessingProfiles = new(StringComparer.Ordinal)
    {
        "learning", "cards"
    };
    private static readonly HashSet<string> Destinations = new(StringComparer.Ordinal)
    {
        "remnote", "anki"
    };
    private static readonly IReadOnlyDictionary<string, LegacyProfileRoute> BuiltInLegacyAliases =
        new Dictionary<string, LegacyProfileRoute>(StringComparer.Ordinal)
        {
            ["parity-remnote"] = new("learning", new[] { "remnote" }),
            ["cards-remnote"] = new("cards", new[] { "remnote" }),
            ["cards-anki"] = new("cards", new[] { "anki" })
        };
    private static readonly HashSet<string> AnkiCategories = new(new[] { "question_answer", "language_zh", "language_en", "general_learning" }, StringComparer.Ordinal);
    private static readonly HashSet<string> BaseTemplates = new(new[] { "basic_qa", "media_qa", "language_context", "concept_transfer", "procedure" }, StringComparer.Ordinal);
    private static readonly HashSet<string> LanguageWritingTemplates = new(new[] { "basic_qa", "media_qa", "language_context", "language_writing", "concept_transfer", "procedure" }, StringComparer.Ordinal);

    public static DestinationNeutralResolverConfiguration LoadConfiguration(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var schemaVersion = RequiredText(document.RootElement, "schemaVersion");
        DestinationNeutralResolverConfiguration configuration;
        if (schemaVersion == "desktop-profile-resolver.v1")
        {
            var legacy = JsonSerializer.Deserialize<DesktopProfileResolverConfiguration>(json, ContractJson.Options)
                ?? throw new OrchestratorDispatchException("resolver.configuration_empty", "Legacy profile resolver configuration is empty.");
            if (!BuiltInLegacyAliases.ContainsKey(legacy.DefaultProfile) ||
                !BuiltInLegacyAliases.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(legacy.Profiles))
                throw new OrchestratorDispatchException("resolver.configuration_invalid", "Legacy resolver profiles do not match the frozen v1 aliases.");
            var defaultRoute = BuiltInLegacyAliases[legacy.DefaultProfile];
            configuration = new DestinationNeutralResolverConfiguration(
                legacy.SchemaVersion,
                defaultRoute.ProcessingProfile,
                ProcessingProfiles.Order(StringComparer.Ordinal).ToArray(),
                defaultRoute.Destinations,
                Destinations.Order(StringComparer.Ordinal).ToArray(),
                BuiltInLegacyAliases,
                legacy.RemNoteExport,
                legacy.RemNoteProjection,
                legacy.Speech,
                legacy.AnkiProjection);
        }
        else if (schemaVersion is "knowledge-clipper-profile-resolver.v2" or "knowledge-clipper-profile-resolver.v2.1")
        {
            var current = JsonSerializer.Deserialize<KnowledgeClipperProfileResolverV2>(json, ContractJson.Options)
                ?? throw new OrchestratorDispatchException("resolver.configuration_empty", "KnowledgeClipper profile resolver configuration is empty.");
            configuration = new DestinationNeutralResolverConfiguration(
                current.SchemaVersion,
                current.DefaultProcessingProfile,
                current.ProcessingProfiles,
                current.DefaultDestinations,
                current.Destinations,
                current.LegacyProfileAliases,
                current.DestinationConfigurations.Remnote.Export,
                current.DestinationConfigurations.Remnote.Projection,
                current.Speech,
                current.DestinationConfigurations.Anki.Projection);
        }
        else
        {
            throw new OrchestratorDispatchException("resolver.configuration_version_unknown", $"Unsupported resolver configuration: {schemaVersion}");
        }

        var expectedTemplates = schemaVersion == "knowledge-clipper-profile-resolver.v2.1" ? LanguageWritingTemplates : BaseTemplates;
        if (!ProcessingProfiles.Contains(configuration.DefaultProcessingProfile) ||
            !ProcessingProfiles.SetEquals(configuration.ProcessingProfiles) ||
            !Destinations.SetEquals(configuration.Destinations) ||
            configuration.DefaultDestinations.Distinct(StringComparer.Ordinal).Count() != configuration.DefaultDestinations.Count ||
            configuration.DefaultDestinations.Any(item => !Destinations.Contains(item)) ||
            !BuiltInLegacyAliases.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(configuration.LegacyProfileAliases.Keys) ||
            configuration.LegacyProfileAliases.Any(item =>
                !BuiltInLegacyAliases.TryGetValue(item.Key, out var expected) ||
                item.Value.ProcessingProfile != expected.ProcessingProfile ||
                !item.Value.Destinations.SequenceEqual(expected.Destinations)) ||
            configuration.Speech.VoicesByLanguage.Count == 0 ||
            configuration.RemNoteProjection.CategoryRegistryVersion != (schemaVersion == "knowledge-clipper-profile-resolver.v2.1" ? "category-registry/2.0.0" : "category-registry/1.0.0") ||
            !expectedTemplates.SetEquals(configuration.RemNoteProjection.TemplateMappings.Keys) ||
            !new HashSet<string>(configuration.RemNoteProjection.AllowedTextBlockTypes, StringComparer.Ordinal).SetEquals(new[] { "text", "markdown" }) ||
            configuration.RemNoteProjection.BlockRendering != "single-block-verbatim-inline" ||
            configuration.RemNoteProjection.AssetRefPolicy != "reject-media-deferred" ||
            configuration.RemNoteProjection.TemplateMappings.Any(item =>
                item.Value.Policy is not ("project" or "reject-media-deferred") ||
                (item.Value.Policy == "project" && (item.Value.CardType != "basic" || item.Value.Direction is not ("forward" or "reverse" or "bidirectional"))) ||
                (item.Value.Policy == "reject-media-deferred" && (item.Value.CardType is not null || item.Value.Direction is not null))) ||
            configuration.AnkiProjection.CategoryRegistryVersion != (schemaVersion == "knowledge-clipper-profile-resolver.v2.1" ? "category-registry/2.0.0" : "category-registry/1.0.0") ||
            configuration.AnkiProjection.RevisionPolicy != "initial-revision-1" ||
            configuration.AnkiProjection.ContentHashPolicy != "sha256-canonical-projection-v1" ||
            configuration.AnkiProjection.AssetRefPolicy != "reject-media-deferred" ||
            !AnkiCategories.SetEquals(configuration.AnkiProjection.CategoryToDeckRoute.Keys) ||
            !expectedTemplates.SetEquals(configuration.AnkiProjection.TemplateMappings.Keys) ||
            configuration.AnkiProjection.TemplateMappings.Any(item =>
                item.Value.Policy is not ("project" or "reject-media-deferred") ||
                (item.Value.Policy == "project" && (string.IsNullOrWhiteSpace(item.Value.NoteTypeKey) || string.IsNullOrWhiteSpace(item.Value.FrontField) || string.IsNullOrWhiteSpace(item.Value.BackField))) ||
                (item.Value.Policy == "reject-media-deferred" && (item.Value.NoteTypeKey is not null || item.Value.FrontField is not null || item.Value.BackField is not null))) ||
            (schemaVersion == "knowledge-clipper-profile-resolver.v2.1" &&
                (!configuration.RemNoteProjection.TemplateMappings.TryGetValue("language_writing", out var remNoteWriting) ||
                 remNoteWriting.Policy != "project" || remNoteWriting.CardType != "basic" || remNoteWriting.Direction != "forward" ||
                 !configuration.AnkiProjection.TemplateMappings.TryGetValue("language_writing", out var ankiWriting) ||
                 ankiWriting.Policy != "project" || ankiWriting.NoteTypeKey != "language_writing" ||
                 ankiWriting.FrontField != "front" || ankiWriting.BackField != "back")))
            throw new OrchestratorDispatchException("resolver.configuration_invalid", "Destination-neutral profile resolver configuration is invalid.");
        return configuration;
    }

    public async Task<PipelinePlan> ResolveAsync(
        string jobRoot,
        string intentPath,
        WorkerRegistry registry,
        string runId,
        DestinationNeutralResolverConfiguration configuration,
        CancellationToken cancellationToken)
    {
        jobRoot = Path.GetFullPath(jobRoot);
        var manifestPath = ResolveManifestPath(jobRoot);
        using var manifestDocument = StrictJson.Read(manifestPath, new[]
        {
            "schemaVersion", "taskId", "createdAt", "createdBy", "intent", "executionProfile", "inputRefs",
            "requestedOutputs", "initialPlanRef", "retentionClass", "metadata", "destinations"
        });
        var manifest = manifestDocument.RootElement;
        var manifestSchemaVersion = RequiredText(manifest, "schemaVersion");
        if (manifestSchemaVersion is not ("1.0.0" or "2.0.0"))
            throw new OrchestratorDispatchException("resolver.manifest_version_unknown", $"Unsupported task manifest schemaVersion: {manifestSchemaVersion}");
        var taskId = RequiredText(manifest, "taskId");
        var declaredProfile = RequiredText(manifest, "executionProfile");
        var route = ResolveRoute(manifest, manifestSchemaVersion, declaredProfile, configuration);

        using var intentDocument = StrictJson.Read(intentPath, new[] { "schemaVersion", "taskId", "intent", "inputArtifactRef", "requestedOutputs" });
        var intent = JsonSerializer.Deserialize<DesktopIntentDocument>(intentDocument.RootElement.GetRawText(), ContractJson.Options)
            ?? throw new OrchestratorDispatchException("resolver.intent_empty", "Desktop intent is empty.");
        ValidateIntent(intent, taskId, route.ProcessingProfile);

        var manifestPlanRef = RequiredText(manifest, "initialPlanRef");
        if (!Path.GetFullPath(intentPath).Equals(PathPolicy.ResolveJobRelative(jobRoot, manifestPlanRef), StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("resolver.plan_reference_mismatch", "task.manifest.initialPlanRef does not identify the submitted desktop intent.");

        var inputReferences = await ReadManifestInputsAsync(jobRoot, manifest, cancellationToken);
        var settingsReference = inputReferences.SingleOrDefault(item => item.Kind == "task-settings-snapshot");
        var taskSettings = settingsReference is null
            ? null
            : await new TaskSettingsAdapter().ReadSnapshotAsync(jobRoot, settingsReference, cancellationToken);
        if (taskSettings is not null)
        {
            if (taskSettings.TaskId != taskId || taskSettings.Workflow.ProcessingProfile != route.ProcessingProfile ||
                !taskSettings.Workflow.Destinations.SequenceEqual(route.Destinations))
                throw new OrchestratorDispatchException("settings.snapshot_route_mismatch", "Task settings snapshot does not match task.manifest.v2 routing.");
            route = route with { Source = taskSettings.SchemaVersion };
        }
        ArtifactReference? contentConfigReference = null;
        string? contentConfigPath = null;
        if (taskSettings?.Modules.Content.ConfigArtifactRef is string contentArtifactRef)
        {
            contentConfigReference = inputReferences.SingleOrDefault(item => item.Kind == "content-config-snapshot" && item.ArtifactRef == contentArtifactRef)
                ?? throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "The frozen Content settings artifact is absent from task.manifest inputRefs.");
            if (!contentConfigReference.ExpectedSha256.Equals(taskSettings.Modules.Content.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "The frozen Content settings artifact hash does not match task settings.");
            var contentConfigManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, contentConfigReference, cancellationToken);
            contentConfigPath = contentConfigManifest.Metadata.TryGetProperty("entrypoint", out var contentEntry) ? contentEntry.GetString() : null;
            if (string.IsNullOrWhiteSpace(contentConfigPath) || !contentConfigManifest.Files.Any(item => item.Path == contentConfigPath))
                throw new OrchestratorDispatchException("settings.content_invalid", "The selected Content config artifact has no declared entrypoint.");
        }
        var captureReference = inputReferences.SingleOrDefault(item => item.ArtifactRef == intent.InputArtifactRef && item.Kind == "capture-bundle")
            ?? throw new OrchestratorDispatchException("resolver.capture_reference_missing", "Desktop intent inputArtifactRef is not the manifest capture-bundle input.");
        var capture = await CaptureBundleSnapshot.ReadAsync(jobRoot, captureReference, cancellationToken);
        if (capture.TaskIntent != intent.Intent)
            throw new OrchestratorDispatchException("resolver.capture_intent_mismatch", "Capture bundle intent is incompatible with desktop intent.");
        if (taskSettings is not null &&
            (capture.ContextEnabled != taskSettings.Modules.Capture.ContextEnabled ||
             capture.ContextGranularity != taskSettings.Modules.Capture.ContextGranularity ||
             capture.ImageHandling != taskSettings.Modules.Capture.ImageHandling))
            throw new OrchestratorDispatchException("settings.capture_snapshot_mismatch", "Capture bundle modeSnapshot does not match the frozen task settings snapshot.");

        var resolvedProfile = route.ProcessingProfile;
        var resolvedDestinations = route.Destinations;
        if (intent.Intent == "clip.question")
        {
            if (capture.HasImages)
                throw new OrchestratorDispatchException("resolver.question_media_unsupported", "Question workflow v2.2 accepts text turns only; image question answering remains a separate capability.");
            var questionWorker = RequireWorker(registry, "content.deepseek", "content.understand-and-card", "content-input", "question.answer");
            var questionInputs = contentConfigReference is null ? new[] { captureReference } : new[] { captureReference, contentConfigReference! };
            var questionStage = new PipelineStage(
                "question-answer", questionWorker.WorkerId, "question.answer", Array.Empty<string>(), questionInputs,
                new[] { "learning-bundle", "question.answer" }, 300_000, new RetryPolicy(3, 1000),
                ContentConfig(taskSettings?.Modules.Content), ContentPayload(taskSettings?.Modules.Content, contentConfigPath),
                Array.Empty<StageOutputBinding>(), new StageInputAdapter("question-input.v3"));
            var questionPlan = new PipelinePlan(
                "2.0.0", "2.2.0", taskId, 1, new[] { questionStage },
                new PipelineRoutingSnapshot("cards", resolvedDestinations, route.Source));
            var questionPlanPath = PathPolicy.ResolveJobRelative(jobRoot, $"plans/resolved/{runId}/pipeline.v2.json");
            await AtomicFile.WriteJsonOnceAsync(questionPlanPath, questionPlan, cancellationToken);
            return questionPlan;
        }
        var needsOcr = capture.HasImages && capture.ImageHandling == "ocr";
        var needsOriginalPassthrough = capture.HasImages && capture.ImageHandling == "original";
        var stages = new List<PipelineStage>();
        string? imageStageId = null;
        if (needsOcr || needsOriginalPassthrough)
        {
            var requiredOutput = needsOriginalPassthrough ? "image.original" : "image-ocr.markdown";
            var image = RequireWorker(registry, "image.ocr.mineru", "image-ocr", "capture-bundle", requiredOutput);
            imageStageId = "image-ocr";
            stages.Add(new PipelineStage(
                imageStageId, image.WorkerId, "process-images", Array.Empty<string>(), new[] { captureReference },
                needsOriginalPassthrough
                    ? new[] { "image.original", "image.original.bundle", "image.processing.provenance" }
                    // OCR produces OCR text/structure plus provenance.  An immutable original-media
                    // alias belongs solely to the explicit `original` route; requiring it here turns a
                    // completed OCR result into a synthetic binding failure after an external provider call.
                    : new[] { requiredOutput, "image-ocr.structured", "image.processing.provenance" },
                600_000, new RetryPolicy(needsOriginalPassthrough ? 1 : 2, 1000),
                needsOriginalPassthrough ? Empty() : ImageOcrConfig(taskSettings?.Modules.ImageOcr), Empty(),
                Array.Empty<StageOutputBinding>(), null, null,
                needsOriginalPassthrough ? null : new PartialRetryPolicy("failed-items-only", 1)));
        }

        if (needsOriginalPassthrough)
        {
            // Original-media is a deliberately Anki-only route.  RemNote's MCP
            // surface does not expose media writes, and treating that as a
            // destination-level conditional leaves an immutable task that cannot
            // satisfy the selected destinations.  Fail before any image worker
            // starts so the caller can select/normalize to Anki explicitly.
            if (resolvedDestinations.Contains("remnote", StringComparer.Ordinal))
                throw new OrchestratorDispatchException(
                    "resolver.original_remnote_incompatible",
                    "Original-media tasks support only the Anki destination; remove RemNote and submit the Anki route.");

            var unsupportedConsumers = new List<string> { "content.deepseek" };
            if (resolvedDestinations.Contains("anki", StringComparer.Ordinal))
            {
                if (resolvedProfile != "cards")
                    throw new OrchestratorDispatchException("resolver.route_unsupported", "Original-media Anki export requires the cards processing profile.");
                var export = RequireWorker(registry, "export.anki", "anki-export", "card-bundle", "anki-export-evidence");
                var configReference = inputReferences.SingleOrDefault(item => item.Kind == "anki-export-config")
                    ?? throw new OrchestratorDispatchException("resolver.export_configuration_missing", "Original-media Anki export requires one SHA-bound anki-export-config artifact.");
                var settingsAnki = taskSettings?.Modules.Anki;
                if (taskSettings is not null && (settingsAnki is null || settingsAnki.ConfigArtifactRef != configReference.ArtifactRef ||
                    !settingsAnki.ExpectedSha256.Equals(configReference.ExpectedSha256, StringComparison.OrdinalIgnoreCase)))
                    throw new OrchestratorDispatchException("settings.anki_snapshot_mismatch", "Original-media Anki config does not match the frozen task settings snapshot.");
                stages.Add(new PipelineStage(
                    "anki-export", export.WorkerId, "export", new[] { imageStageId! }, new[] { captureReference, configReference },
                    new[] { "anki-export-evidence" }, 180_000, new RetryPolicy(1, 0),
                    JsonSerializer.SerializeToElement(new { ankiConfigArtifactId = configReference.ArtifactRef }), Empty(),
                    new[]
                    {
                        new StageOutputBinding(imageStageId!, "image.original", true),
                        new StageOutputBinding(imageStageId!, "image.original.bundle", true),
                        new StageOutputBinding(imageStageId!, "image.processing.provenance", true)
                    },
                    new StageInputAdapter("ordered-original-images.v1", "card-bundle", JsonSerializer.SerializeToElement(new
                    {
                        schemaVersion = "knowledge-clipper-ordered-original-images-adapter.v1",
                        projection = "ordered-original-images.v1", sourceMode = "original", ocrInvoked = false,
                        sourceKinds = new[] { "capture-bundle", "image.original", "image.original.bundle", "image.processing.provenance", "anki-export-config" },
                        ordering = "questionImages-then-answerImages", noteTypeKey = "original_image_qa",
                        deckRouteKey = "question", tags = configuration.AnkiProjection.Tags,
                        unsupportedDestinations = Array.Empty<object>()
                    }, ContractJson.Options))));
            }
            if (intent.Intent == "clip.read-aloud") unsupportedConsumers.Add("speech.edge");
            var originalPlan = new PipelinePlan(
                "2.1.0", "2.6.0", taskId, 2, stages,
                new PipelineRoutingSnapshot(resolvedProfile, resolvedDestinations, route.Source),
                unsupportedConsumers.Count == 0 ? null : new PipelineTerminalBoundary(
                    "conditional", "media.destination_adapter_unavailable", new[] { "image.original", "media-bundle" },
                    unsupportedConsumers.Distinct(StringComparer.Ordinal).ToArray()));
            var originalPlanPath = PathPolicy.ResolveJobRelative(jobRoot, $"plans/resolved/{runId}/pipeline.v2.json");
            await AtomicFile.WriteJsonOnceAsync(originalPlanPath, originalPlan, cancellationToken);
            return originalPlan;
        }

        var content = RequireWorker(registry, "content.deepseek", "content.understand-and-card", "content-input", "card-bundle");
        var contentDependencies = imageStageId is null ? Array.Empty<string>() : new[] { imageStageId };
        // The capture bundle remains an explicit root input even when OCR is used.  The
        // content adapter needs its questionImages/answerImages ordering to bind OCR
        // itemDiagnostics deterministically instead of trusting result output order.
        var contentStaticInputs = contentConfigReference is null ? new[] { captureReference } : new[] { captureReference, contentConfigReference! };
        var contentBindings = imageStageId is null
            ? Array.Empty<StageOutputBinding>()
            : new[] { new StageOutputBinding(imageStageId, "image-ocr.markdown", true) };
        stages.Add(new PipelineStage(
            "content.understand-and-card", content.WorkerId, "understand-and-card", contentDependencies, contentStaticInputs,
            new[] { "learning-bundle", "card-bundle" }, 300_000, new RetryPolicy(3, 1000),
            ContentConfig(taskSettings?.Modules.Content, configuration), ContentPayload(taskSettings?.Modules.Content, contentConfigPath),
            contentBindings, new StageInputAdapter("content-input.v1")));

        if (intent.Intent == "clip.read-aloud")
        {
            var effectiveSpeech = taskSettings?.Modules.Speech;
            if (taskSettings is not null && effectiveSpeech is null)
                throw new OrchestratorDispatchException("settings.speech_snapshot_missing", "Read-aloud requires a frozen speech module settings snapshot.");
            var speech = RequireWorker(registry, "speech.edge", "speech.synthesize", "text.spoken", "audio.speech");
            var speechDependencies = imageStageId is null ? Array.Empty<string>() : new[] { imageStageId };
            var speechStaticInputs = new[] { captureReference };
            var speechBindings = imageStageId is null
                ? Array.Empty<StageOutputBinding>()
                : new[] { new StageOutputBinding(imageStageId, "image-ocr.markdown", true) };
            stages.Add(new PipelineStage(
                "speech-synthesize", speech.WorkerId, "speech.synthesize", speechDependencies, speechStaticInputs,
                new[] { "audio.speech" }, 120_000, new RetryPolicy(2, 500), SpeechSynthesisConfig(configuration.Speech, effectiveSpeech),
                SpeechPayload(configuration.Speech, effectiveSpeech), speechBindings, new StageInputAdapter("text-spoken.v1")));
            if (effectiveSpeech?.AutoPlay ?? true)
            {
                stages.Add(new PipelineStage(
                    "speech-playback", speech.WorkerId, "speech.playback", new[] { "speech-synthesize" }, Array.Empty<ArtifactReference>(),
                    Array.Empty<string>(), 3_600_000, new RetryPolicy(1, 0), SpeechPlaybackConfig(configuration.Speech, effectiveSpeech), Empty(),
                    new[] { new StageOutputBinding("speech-synthesize", "audio.speech", true) }, null,
                    new StageCompletionPolicy(new[] { "user_stop" }, "succeeded-with-playback-stopped")));
            }
        }

        AppendDestinationStages(stages, "content.understand-and-card", taskId, resolvedProfile, resolvedDestinations,
            inputReferences, taskSettings, registry, configuration);

        var plan = new PipelinePlan(
            needsOcr ? "2.1.0" : "2.0.0",
            needsOcr ? "2.1.0" : "2.0.0",
            taskId,
            4,
            stages,
            new PipelineRoutingSnapshot(resolvedProfile, resolvedDestinations, route.Source));
        var resolvedPlanPath = PathPolicy.ResolveJobRelative(jobRoot, $"plans/resolved/{runId}/pipeline.v2.json");
        await AtomicFile.WriteJsonOnceAsync(resolvedPlanPath, plan, cancellationToken);
        return plan;
    }

    internal async Task<PipelinePlan> ResolveQuestionResumePlanAsync(
        string jobRoot,
        string taskId,
        string runId,
        bool answerPhase,
        int? turnIndex,
        IReadOnlyList<ArtifactReference> questionInputs,
        JsonElement? interactiveCommandRoot,
        WorkerRegistry registry,
        DestinationNeutralResolverConfiguration configuration,
        CancellationToken cancellationToken)
    {
        jobRoot = Path.GetFullPath(jobRoot);
        var manifestPath = ResolveManifestPath(jobRoot);
        using var manifestDocument = StrictJson.Read(manifestPath, new[]
        {
            "schemaVersion", "taskId", "createdAt", "createdBy", "intent", "executionProfile", "inputRefs",
            "requestedOutputs", "initialPlanRef", "retentionClass", "metadata", "destinations"
        });
        var manifest = manifestDocument.RootElement;
        var manifestSchemaVersion = RequiredText(manifest, "schemaVersion");
        if (manifestSchemaVersion is not ("1.0.0" or "2.0.0") || RequiredText(manifest, "taskId") != taskId || RequiredText(manifest, "intent") != "clip.question")
            throw new OrchestratorDispatchException("settings.question_manifest_invalid", "Question resume requires the original immutable clip.question task manifest.");
        var route = ResolveRoute(manifest, manifestSchemaVersion, RequiredText(manifest, "executionProfile"), configuration);
        if (route.ProcessingProfile != "cards")
            throw new OrchestratorDispatchException("settings.question_route_unsupported", "Question resume requires the cards processing profile.");
        var inputReferences = await ReadManifestInputsAsync(jobRoot, manifest, cancellationToken);
        var settingsReference = inputReferences.SingleOrDefault(item => item.Kind == "task-settings-snapshot");
        var taskSettings = settingsReference is null ? null : await new TaskSettingsAdapter().ReadSnapshotAsync(jobRoot, settingsReference, cancellationToken);
        if (taskSettings is not null)
        {
            if (taskSettings.TaskId != taskId || taskSettings.Workflow.ProcessingProfile != route.ProcessingProfile ||
                !taskSettings.Workflow.Destinations.SequenceEqual(route.Destinations))
                throw new OrchestratorDispatchException("settings.snapshot_route_mismatch", "Question resume route does not match the frozen task settings snapshot.");
            route = route with { Source = taskSettings.SchemaVersion };
        }
        ArtifactReference? contentConfigReference = null;
        string? contentConfigPath = null;
        if (taskSettings?.Modules.Content.ConfigArtifactRef is string contentArtifactRef)
        {
            contentConfigReference = inputReferences.SingleOrDefault(item => item.Kind == "content-config-snapshot" && item.ArtifactRef == contentArtifactRef)
                ?? throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "The frozen Content settings artifact is absent from task.manifest inputRefs.");
            if (!contentConfigReference.ExpectedSha256.Equals(taskSettings.Modules.Content.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "The frozen Content settings artifact hash does not match task settings.");
            var contentConfigManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, contentConfigReference, cancellationToken);
            contentConfigPath = contentConfigManifest.Metadata.TryGetProperty("entrypoint", out var entrypoint) ? entrypoint.GetString() : null;
            if (string.IsNullOrWhiteSpace(contentConfigPath) || !contentConfigManifest.Files.Any(item => item.Path == contentConfigPath))
                throw new OrchestratorDispatchException("settings.content_invalid", "The selected Content config artifact has no declared entrypoint.");
        }

        var contentWorker = RequireWorker(registry, "content.deepseek", "content.understand-and-card", "content-input",
            answerPhase ? "question.answer" : "card-bundle");
        var inputs = questionInputs.ToList();
        if (contentConfigReference is not null) inputs.Add(contentConfigReference);
        var adapterSnapshot = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["schemaVersion"] = "knowledge-clipper-question-input-adapter.v2",
            ["phase"] = answerPhase ? "answer" : "make-cards",
            ["turnIndex"] = answerPhase ? turnIndex : null,
            ["interactiveCommandRoot"] = answerPhase ? interactiveCommandRoot : null
        }, ContractJson.Options);
        var stageId = answerPhase ? "question-follow-up" : "question-make-cards";
        var stages = new List<PipelineStage>
        {
            new(stageId, contentWorker.WorkerId, answerPhase ? "question.answer" : "question.make-cards", Array.Empty<string>(), inputs,
                answerPhase ? new[] { "learning-bundle", "question.answer" } : new[] { "learning-bundle", "card-bundle" },
                300_000, new RetryPolicy(3, 1000), ContentConfig(taskSettings?.Modules.Content, configuration),
                ContentPayload(taskSettings?.Modules.Content, contentConfigPath), Array.Empty<StageOutputBinding>(),
                new StageInputAdapter("question-resume-input.v3", MappingSnapshot: adapterSnapshot))
        };
        if (!answerPhase)
            AppendDestinationStages(stages, stageId, taskId, route.ProcessingProfile, route.Destinations,
                inputReferences, taskSettings, registry, configuration);
        var plan = new PipelinePlan("2.1.0", "2.5.2", taskId, 4, stages,
            new PipelineRoutingSnapshot(route.ProcessingProfile, route.Destinations, route.Source));
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, $"plans/resolved/{runId}/pipeline.v2.json"), plan, cancellationToken);
        return plan;
    }

    internal async Task<PipelinePlan> ResolveOcrHumanResolutionPlanAsync(
        string jobRoot,
        string taskId,
        string runId,
        IReadOnlyList<ArtifactReference> resolutionInputs,
        JsonElement adapterSnapshot,
        WorkerRegistry registry,
        DestinationNeutralResolverConfiguration configuration,
        CancellationToken cancellationToken)
    {
        jobRoot = Path.GetFullPath(jobRoot);
        var manifestPath = ResolveManifestPath(jobRoot);
        using var manifestDocument = StrictJson.Read(manifestPath, new[]
        {
            "schemaVersion", "taskId", "createdAt", "createdBy", "intent", "executionProfile", "inputRefs",
            "requestedOutputs", "initialPlanRef", "retentionClass", "metadata", "destinations"
        });
        var manifest = manifestDocument.RootElement;
        var manifestSchemaVersion = RequiredText(manifest, "schemaVersion");
        if (manifestSchemaVersion is not ("1.0.0" or "2.0.0") || RequiredText(manifest, "taskId") != taskId)
            throw new OrchestratorDispatchException("settings.ocr_resolution_manifest_invalid", "OCR human resolution requires the original immutable task manifest.");
        var route = ResolveRoute(manifest, manifestSchemaVersion, RequiredText(manifest, "executionProfile"), configuration);
        var inputReferences = await ReadManifestInputsAsync(jobRoot, manifest, cancellationToken);
        var settingsReference = inputReferences.SingleOrDefault(item => item.Kind == "task-settings-snapshot");
        var taskSettings = settingsReference is null ? null : await new TaskSettingsAdapter().ReadSnapshotAsync(jobRoot, settingsReference, cancellationToken);
        if (taskSettings is not null)
        {
            if (taskSettings.TaskId != taskId || taskSettings.Workflow.ProcessingProfile != route.ProcessingProfile ||
                !taskSettings.Workflow.Destinations.SequenceEqual(route.Destinations))
                throw new OrchestratorDispatchException("settings.snapshot_route_mismatch", "OCR human-resolution route does not match the frozen task settings snapshot.");
            route = route with { Source = taskSettings.SchemaVersion };
        }
        ArtifactReference? contentConfigReference = null;
        string? contentConfigPath = null;
        if (taskSettings?.Modules.Content.ConfigArtifactRef is string contentArtifactRef)
        {
            contentConfigReference = inputReferences.SingleOrDefault(item => item.Kind == "content-config-snapshot" && item.ArtifactRef == contentArtifactRef)
                ?? throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "The frozen Content settings artifact is absent from task.manifest inputRefs.");
            if (!contentConfigReference.ExpectedSha256.Equals(taskSettings.Modules.Content.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "The frozen Content settings artifact hash does not match task settings.");
            var contentConfigManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, contentConfigReference, cancellationToken);
            contentConfigPath = contentConfigManifest.Metadata.TryGetProperty("entrypoint", out var entrypoint) ? entrypoint.GetString() : null;
            if (string.IsNullOrWhiteSpace(contentConfigPath) || !contentConfigManifest.Files.Any(item => item.Path == contentConfigPath))
                throw new OrchestratorDispatchException("settings.content_invalid", "The selected Content config artifact has no declared entrypoint.");
        }
        var contentWorker = RequireWorker(registry, "content.deepseek", "content.understand-and-card", "content-input", "card-bundle");
        var inputs = resolutionInputs.ToList();
        if (contentConfigReference is not null) inputs.Add(contentConfigReference);
        const string stageId = "content.ocr-human-resolution";
        var stages = new List<PipelineStage>
        {
            new(stageId, contentWorker.WorkerId, "understand-and-card", Array.Empty<string>(), inputs,
                new[] { "learning-bundle", "card-bundle" }, 300_000, new RetryPolicy(3, 1000),
                ContentConfig(taskSettings?.Modules.Content, configuration), ContentPayload(taskSettings?.Modules.Content, contentConfigPath),
                Array.Empty<StageOutputBinding>(), new StageInputAdapter("ocr-human-resolution-input.v1", MappingSnapshot: adapterSnapshot))
        };
        AppendDestinationStages(stages, stageId, taskId, route.ProcessingProfile, route.Destinations,
            inputReferences, taskSettings, registry, configuration);
        var plan = new PipelinePlan("2.1.0", "2.6.0", taskId, 4, stages,
            new PipelineRoutingSnapshot(route.ProcessingProfile, route.Destinations, route.Source));
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, $"plans/resolved/{runId}/pipeline.v2.json"), plan, cancellationToken);
        return plan;
    }

    private static void AppendDestinationStages(
        List<PipelineStage> stages,
        string contentStageId,
        string taskId,
        string resolvedProfile,
        IReadOnlyList<string> resolvedDestinations,
        IReadOnlyList<ArtifactReference> inputReferences,
        TaskSettingsSnapshot? taskSettings,
        WorkerRegistry registry,
        DestinationNeutralResolverConfiguration configuration)
    {
        if (resolvedDestinations.Contains("remnote", StringComparer.Ordinal))
        {
            var export = RequireWorker(registry, "export.remnote", "export-remnote",
                resolvedProfile == "cards" ? "card.bundle" : "learning.bundle", "remnote.write-receipt");
            var exportProfile = configuration.RemNoteExport;
            var settingsRemNote = taskSettings?.Modules.Remnote;
            if (settingsRemNote is null && exportProfile is null)
                throw new OrchestratorDispatchException("resolver.export_configuration_missing", "The remnote destination requires an exact target configuration.");
            var sourceKind = resolvedProfile == "cards" ? "card-bundle" : "learning-bundle";
            var targetKind = resolvedProfile == "cards" ? "card.bundle" : "learning.bundle";
            var exportBindings = new List<StageOutputBinding> { new(contentStageId, sourceKind, true) };
            if (resolvedProfile == "cards") exportBindings.Add(new StageOutputBinding(contentStageId, "learning-bundle", true));
            stages.Add(new PipelineStage(
                "export-remnote", export.WorkerId, "remnote.export", new[] { contentStageId }, Array.Empty<ArtifactReference>(),
                new[] { "remnote.write-receipt" }, 180_000, new RetryPolicy(1, 0), Empty(),
                settingsRemNote is null
                    ? JsonSerializer.SerializeToElement(new { schemaVersion = "1.0.0", documentTitle = $"{exportProfile!.DocumentTitlePrefix} · {taskId}", sink = exportProfile.Sink, target = new { rootRemId = exportProfile.RootRemId, rootTitle = exportProfile.RootTitle } })
                    : JsonSerializer.SerializeToElement(new { schemaVersion = "2.0.0", documentTitle = $"Knowledge Clipper · {taskId}", sink = settingsRemNote.Settings.Advanced.Sink, target = new { rootRemId = settingsRemNote.Settings.Target.RootRemId!, rootTitle = settingsRemNote.Settings.Target.RootTitle }, route = settingsRemNote.Route }, ContractJson.Options),
                exportBindings, new StageInputAdapter("content-to-remnote.v1", targetKind, JsonSerializer.SerializeToElement(configuration.RemNoteProjection, ContractJson.Options))));
        }

        if (resolvedDestinations.Contains("anki", StringComparer.Ordinal))
        {
            if (resolvedProfile != "cards")
                throw new OrchestratorDispatchException("resolver.route_unsupported", "The anki destination currently requires the cards processing profile.");
            var export = RequireWorker(registry, "export.anki", "anki-export", "card-bundle", "anki-export-evidence");
            var configReference = inputReferences.SingleOrDefault(item => item.Kind == "anki-export-config")
                ?? throw new OrchestratorDispatchException("resolver.export_configuration_missing", "The anki destination requires one anki-export-config artifact in task.manifest.inputRefs.");
            var settingsAnki = taskSettings?.Modules.Anki;
            if (taskSettings is not null && (settingsAnki is null || settingsAnki.ConfigArtifactRef != configReference.ArtifactRef ||
                !settingsAnki.ExpectedSha256.Equals(configReference.ExpectedSha256, StringComparison.OrdinalIgnoreCase)))
                throw new OrchestratorDispatchException("settings.anki_snapshot_mismatch", "Anki config artifact does not match the frozen task settings snapshot.");
            stages.Add(new PipelineStage(
                "anki-export", export.WorkerId, "export", new[] { contentStageId }, new[] { configReference },
                new[] { "anki-export-evidence" }, 180_000, new RetryPolicy(1, 0),
                JsonSerializer.SerializeToElement(new { ankiConfigArtifactId = configReference.ArtifactRef }), Empty(),
                new[] { new StageOutputBinding(contentStageId, "card-bundle", true), new StageOutputBinding(contentStageId, "learning-bundle", true) },
                new StageInputAdapter("content-to-anki.v1", "card-bundle", JsonSerializer.SerializeToElement(configuration.AnkiProjection, ContractJson.Options))));
        }
    }

    private static PipelineRoutingSnapshot ResolveRoute(
        JsonElement manifest,
        string manifestSchemaVersion,
        string declaredProfile,
        DestinationNeutralResolverConfiguration configuration)
    {
        if (manifestSchemaVersion == "1.0.0")
        {
            if (!configuration.LegacyProfileAliases.TryGetValue(declaredProfile, out var legacy))
                throw new OrchestratorDispatchException("resolver.profile_unknown", $"Unknown legacy execution profile alias: {declaredProfile}");
            return new PipelineRoutingSnapshot(
                legacy.ProcessingProfile,
                legacy.Destinations.ToArray(),
                $"legacy-profile-alias:{declaredProfile}");
        }

        if (!configuration.ProcessingProfiles.Contains(declaredProfile, StringComparer.Ordinal))
            throw new OrchestratorDispatchException("resolver.profile_unknown", $"Unknown processing profile: {declaredProfile}");
        if (!manifest.TryGetProperty("destinations", out var destinationsElement) || destinationsElement.ValueKind != JsonValueKind.Array)
            throw new OrchestratorDispatchException("resolver.destinations_missing", "Task manifest v2 requires an explicit destinations array, which may be empty.");
        var destinations = destinationsElement.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();
        if (destinations.Any(item => item is null) ||
            destinations.Distinct(StringComparer.Ordinal).Count() != destinations.Length ||
            destinations.Any(item => !configuration.Destinations.Contains(item!, StringComparer.Ordinal)))
            throw new OrchestratorDispatchException("resolver.destinations_invalid", "Task destinations must be a unique subset of the configured exporter endpoints.");
        return new PipelineRoutingSnapshot(declaredProfile, destinations.Select(item => item!).ToArray(), "task-manifest.v2");
    }

    private static string ResolveManifestPath(string jobRoot)
    {
        var canonical = Path.Combine(jobRoot, "task.manifest.v2.json");
        if (File.Exists(canonical)) return canonical;
        var legacy = Path.Combine(jobRoot, "task.manifest.v1.json");
        if (File.Exists(legacy)) return legacy;
        throw new FileNotFoundException("Task manifest is missing. Expected task.manifest.v2.json or the legacy task.manifest.v1.json.");
    }

    private static void ValidateIntent(DesktopIntentDocument intent, string taskId, string processingProfile)
    {
        if (intent.SchemaVersion is not ("desktop-intent.v1" or "knowledge-clipper-intent.v2"))
            throw new OrchestratorDispatchException("resolver.intent_version_unknown", $"Unsupported desktop intent schema: {intent.SchemaVersion}");
        if (intent.TaskId != taskId)
            throw new OrchestratorDispatchException("resolver.intent_task_mismatch", "Desktop intent taskId does not match task manifest.");
        if (intent.Intent is not ("clip.card" or "clip.read-aloud" or "clip.question"))
            throw new OrchestratorDispatchException("resolver.intent_unknown", $"Unknown desktop intent: {intent.Intent}");
        if (intent.RequestedOutputs.Count == 0 || intent.RequestedOutputs.Distinct(StringComparer.Ordinal).Count() != intent.RequestedOutputs.Count)
            throw new OrchestratorDispatchException("resolver.requested_outputs_invalid", "Desktop intent requestedOutputs must be a non-empty unique array.");
        var expected = intent.Intent switch
        {
            "clip.card" when processingProfile == "learning" => "learning-bundle",
            "clip.card" => "card-bundle",
            "clip.read-aloud" => "audio.speech",
            _ => "question.answer"
        };
        if (!intent.RequestedOutputs.Contains(expected, StringComparer.Ordinal))
            throw new OrchestratorDispatchException("resolver.requested_outputs_invalid", $"Desktop intent must request {expected}.");
    }

    private static async Task<IReadOnlyList<ArtifactReference>> ReadManifestInputsAsync(string jobRoot, JsonElement manifest, CancellationToken cancellationToken)
    {
        var output = new List<ArtifactReference>();
        foreach (var item in manifest.GetProperty("inputRefs").EnumerateArray())
        {
            var artifactRef = RequiredText(item, "artifactRef");
            var reference = new ArtifactReference(
                artifactRef,
                RequiredText(item, "kind"),
                RequiredText(item, "contractVersion"),
                $"artifacts/{artifactRef}/artifact.json",
                RequiredText(item, "expectedSha256"));
            await CoreArtifactIO.ValidateReferenceAsync(jobRoot, reference, cancellationToken);
            output.Add(reference);
        }
        return output;
    }

    private static WorkerEntry RequireWorker(WorkerRegistry registry, string workerId, string describedStage, string inputKind, string outputKind)
    {
        var worker = registry.Workers.SingleOrDefault(item => item.WorkerId == workerId && item.EnabledByDefault)
            ?? throw new OrchestratorDispatchException("resolver.worker_missing", $"Required worker is missing or disabled: {workerId}");
        if (!worker.Stages.Contains(describedStage, StringComparer.Ordinal) ||
            !worker.InputKinds.Contains(inputKind, StringComparer.Ordinal) ||
            (outputKind.Length > 0 && !worker.OutputKinds.Contains(outputKind, StringComparer.Ordinal)))
            throw new OrchestratorDispatchException("resolver.worker_capability_mismatch", $"Worker descriptor does not satisfy the pipeline capability: {workerId}");
        return worker;
    }

    private static JsonElement SpeechSynthesisConfig(SpeechProfile profile, EffectiveSpeechSettings? settings)
    {
        var values = new Dictionary<string, object?>();
        var path = settings?.Deployment?.EdgeTtsPath ?? profile.EdgeTtsPath;
        if (!string.IsNullOrWhiteSpace(path)) values["edgeTtsPath"] = path;
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }

    private static JsonElement SpeechPlaybackConfig(SpeechProfile profile, EffectiveSpeechSettings? settings)
    {
        var values = new Dictionary<string, object?>();
        var path = settings?.Deployment?.FfplayPath ?? profile.FfplayPath;
        if (!string.IsNullOrWhiteSpace(path)) values["ffplayPath"] = path;
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }

    private static JsonElement SpeechPayload(SpeechProfile profile, EffectiveSpeechSettings? settings = null) => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = "1.0.0",
        contextMode = "selection_only",
        language = new { strategy = settings?.LanguageStrategy ?? "auto", @override = settings?.LanguageOverride },
        voiceSnapshot = new
        {
            provider = "edge-tts",
            voicesByLanguage = settings?.VoicesByLanguage ?? profile.VoicesByLanguage,
            ratePercent = settings?.RatePercent ?? profile.RatePercent,
            volumePercent = settings?.VolumePercent ?? profile.VolumePercent,
            pitchHz = settings?.PitchHz ?? profile.PitchHz
        },
        playbackPolicy = new { autoPlay = settings?.AutoPlay ?? true, activePlayback = "supersede", replay = "from_start", naturalCompletion = "idle_replayable", stopTerminal = "stopped" },
        cachePolicy = new { scope = "task", reuseSuccessfulAudio = true, crossTaskReuse = false }
    }, ContractJson.Options);

    private static JsonElement ImageOcrConfig(EffectiveImageOcrSettings? settings)
    {
        if (settings is null) return Empty();
        var values = new Dictionary<string, object?>
        {
            ["providerSettings"] = settings.ProviderSettings
        };
        if (!string.IsNullOrWhiteSpace(settings.MineruScriptPath)) values["mineruScriptPath"] = settings.MineruScriptPath;
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }

    private static JsonElement ContentConfig(EffectiveContentSettings? settings, DestinationNeutralResolverConfiguration? fallback = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["promptSet"] = settings?.PromptSet ?? (fallback?.SchemaVersion == "knowledge-clipper-profile-resolver.v2.1" ? "open-md-v2" : "open-md-v1"),
            ["categoryRegistry"] = settings?.CategoryRegistry ?? fallback?.RemNoteProjection.CategoryRegistryVersion ?? "category-registry/2.0.0"
        };
        if (!string.IsNullOrWhiteSpace(settings?.SettingsRevisionId)) values["contentSettingsRevisionId"] = settings.SettingsRevisionId;
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }

    private static JsonElement ContentPayload(EffectiveContentSettings? settings, string? contentConfigPath)
    {
        var values = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "content-run/1.0.0",
            ["providerMode"] = settings?.ProviderMode ?? "live"
        };
        if (!string.IsNullOrWhiteSpace(contentConfigPath)) values["contentConfigPath"] = contentConfigPath;
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });
    private static string RequiredText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new OrchestratorDispatchException("resolver.contract_invalid", $"Required text is missing: {property}");
}

internal sealed class CaptureBundleSnapshot
{
    public required string TaskIntent { get; init; }
    public required string ImageHandling { get; init; }
    public required bool ContextEnabled { get; init; }
    public required string ContextGranularity { get; init; }
    public required bool HasImages { get; init; }

    public static async Task<CaptureBundleSnapshot> ReadAsync(string jobRoot, ArtifactReference reference, CancellationToken cancellationToken)
    {
        var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, reference, cancellationToken);
        var entrypoint = manifest.Metadata.TryGetProperty("entrypoint", out var entry) ? entry.GetString() : null;
        var file = manifest.Files.SingleOrDefault(item => item.Path == entrypoint) ?? manifest.Files.Single();
        using var document = StrictJson.Read(PathPolicy.ResolveJobRelative(jobRoot, file.Path), new[]
        {
            "schemaVersion", "captureId", "createdAt", "intent", "source", "modeSnapshot", "artifacts", "selectedTextArtifactRef",
            "contextSegments", "questionImages", "answerImages", "questionTurns"
        });
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "capture-bundle.v1")
            throw new OrchestratorDispatchException("resolver.capture_version_unknown", "Capture bundle must use capture-bundle.v1.");
        var bundleIntent = root.GetProperty("intent").GetString();
        var taskIntent = bundleIntent == "read" ? "clip.read-aloud" : bundleIntent == "question" ? "clip.question" : "clip.card";
        var mode = root.GetProperty("modeSnapshot");
        var imageHandling = mode.GetProperty("imageHandling").GetString()
            ?? throw new OrchestratorDispatchException("resolver.capture_invalid", "Capture bundle imageHandling is missing.");
        var contextEnabled = mode.GetProperty("contextEnabled").GetBoolean();
        var contextGranularity = mode.GetProperty("contextGranularity").GetString()
            ?? throw new OrchestratorDispatchException("resolver.capture_invalid", "Capture bundle contextGranularity is missing.");
        var hasImages = root.GetProperty("questionImages").GetArrayLength() > 0 || root.GetProperty("answerImages").GetArrayLength() > 0 ||
            root.GetProperty("artifacts").EnumerateArray().Any(item => item.GetProperty("mediaType").GetString()?.StartsWith("image/", StringComparison.Ordinal) == true);
        return new CaptureBundleSnapshot { TaskIntent = taskIntent, ImageHandling = imageHandling, ContextEnabled = contextEnabled, ContextGranularity = contextGranularity, HasImages = hasImages };
    }
}

internal static class StrictJson
{
    public static JsonDocument Read(string path, IReadOnlyCollection<string> allowedProperties)
    {
        var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new OrchestratorDispatchException("resolver.contract_invalid", $"JSON root must be an object: {path}");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                document.Dispose();
                throw new OrchestratorDispatchException("resolver.contract_invalid", $"Unknown or duplicate property {property.Name}: {path}");
            }
        }
        return document;
    }
}

internal sealed record CoreArtifactFile(string Path, string Sha256, long Bytes, string MediaType);
internal sealed record CoreArtifactPayload(string Name, byte[] Bytes, string MediaType);
internal sealed record CoreArtifactManifest(
    string ArtifactId,
    string Kind,
    string ContractVersion,
    string MediaType,
    IReadOnlyList<CoreArtifactFile> Files,
    IReadOnlyList<ArtifactReference> InputRefs,
    JsonElement Metadata);

internal static class CoreArtifactIO
{
    public static async Task ValidateReferenceAsync(string jobRoot, ArtifactReference reference, CancellationToken cancellationToken) =>
        _ = await ReadManifestAsync(jobRoot, reference, cancellationToken);

    public static async Task<bool> HasAncestorAsync(
        string jobRoot,
        ArtifactReference root,
        ArtifactReference expectedAncestor,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return await HasAncestorAsync(jobRoot, root, expectedAncestor, visited, 0, cancellationToken);
    }

    private static async Task<bool> HasAncestorAsync(
        string jobRoot,
        ArtifactReference current,
        ArtifactReference expectedAncestor,
        HashSet<string> visited,
        int depth,
        CancellationToken cancellationToken)
    {
        if (SameReference(current, expectedAncestor)) return true;
        if (depth >= 16 || !visited.Add(current.ManifestPath)) return false;
        var manifest = await ReadManifestAsync(jobRoot, current, cancellationToken);
        foreach (var input in manifest.InputRefs)
            if (await HasAncestorAsync(jobRoot, input, expectedAncestor, visited, depth + 1, cancellationToken)) return true;
        return false;
    }

    private static bool SameReference(ArtifactReference left, ArtifactReference right) =>
        left.ArtifactRef == right.ArtifactRef && left.Kind == right.Kind && left.ContractVersion == right.ContractVersion &&
        left.ManifestPath == right.ManifestPath && left.ExpectedSha256.Equals(right.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

    public static async Task<CoreArtifactManifest> ReadManifestAsync(string jobRoot, ArtifactReference reference, CancellationToken cancellationToken)
    {
        var path = PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!Integrity.Sha256Hex(bytes).Equals(reference.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("binding.manifest_hash_mismatch", $"Artifact manifest hash mismatch: {reference.ManifestPath}");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("artifactId").GetString() != reference.ArtifactRef || root.GetProperty("kind").GetString() != reference.Kind ||
            root.GetProperty("contractVersion").GetString() != reference.ContractVersion)
            throw new OrchestratorDispatchException("binding.manifest_identity_mismatch", $"Artifact manifest identity mismatch: {reference.ManifestPath}");
        var files = new List<CoreArtifactFile>();
        var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            var file = new CoreArtifactFile(item.GetProperty("path").GetString()!, item.GetProperty("sha256").GetString()!, item.GetProperty("bytes").GetInt64(), item.GetProperty("mediaType").GetString()!);
            VerifiedArtifactEntrypointResolver.EnsureCanonicalJobRelativePath(jobRoot, file.Path, "Artifact file path");
            if (!declaredPaths.Add(file.Path))
                throw new OrchestratorDispatchException("binding.manifest_file_duplicate", $"Artifact declares a duplicate file path: {file.Path}");
            await Integrity.VerifyFileAsync(jobRoot, file.Path, file.Bytes, file.Sha256, cancellationToken);
            files.Add(file);
        }
        if (files.Count == 0)
            throw new OrchestratorDispatchException("binding.manifest_empty", $"Artifact has no files: {reference.ManifestPath}");
        var inputRefs = root.TryGetProperty("inputRefs", out var inputRefsElement) && inputRefsElement.ValueKind == JsonValueKind.Array
            ? inputRefsElement.EnumerateArray().Select(item => item.Deserialize<ArtifactReference>(ContractJson.Options)
                ?? throw new OrchestratorDispatchException("binding.manifest_ancestry_invalid", $"Artifact inputRef is invalid: {reference.ManifestPath}")).ToArray()
            : Array.Empty<ArtifactReference>();
        return new CoreArtifactManifest(reference.ArtifactRef, reference.Kind, reference.ContractVersion,
            root.GetProperty("mediaType").GetString()!, files, inputRefs,
            root.TryGetProperty("metadata", out var metadata) ? metadata.Clone() : JsonSerializer.SerializeToElement(new { }));
    }

    public static async Task<ArtifactReference> PublishAsync(
        string jobRoot,
        string runId,
        string stageId,
        string artifactId,
        string kind,
        string mediaType,
        string payloadName,
        byte[] payload,
        IReadOnlyList<ArtifactReference> inputRefs,
        JsonElement metadata,
        CancellationToken cancellationToken) =>
        await PublishFilesAsync(jobRoot, runId, stageId, artifactId, kind, mediaType,
            new[] { new CoreArtifactPayload(payloadName, payload, mediaType) }, inputRefs, metadata, cancellationToken);

    public static async Task<ArtifactReference> PublishDerivedJsonAsync(
        string jobRoot,
        string runId,
        string stageId,
        string artifactId,
        string payloadName,
        byte[] payload,
        ArtifactReference source,
        IReadOnlyList<CoreArtifactFile> inheritedFiles,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        var versionRoot = $"runs/{runId}/adapters/{stageId}/{artifactId}/v0001";
        var payloadRelative = $"{versionRoot}/{payloadName}";
        await WriteBytesOnceOrVerifyAsync(PathPolicy.ResolveJobRelative(jobRoot, payloadRelative), payload, cancellationToken);
        var files = new List<object>
        {
            new { path = payloadRelative, sha256 = Integrity.Sha256Hex(payload).ToLowerInvariant(), bytes = payload.LongLength, mediaType = "application/json" }
        };
        files.AddRange(inheritedFiles.Select(file => (object)new { path = file.Path, sha256 = file.Sha256, bytes = file.Bytes, mediaType = file.MediaType }));
        var manifestRelative = $"{versionRoot}/artifact.json";
        var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, manifestRelative);
        if (!File.Exists(manifestPath))
        {
            await AtomicFile.WriteJsonOnceAsync(manifestPath, new
            {
                schemaVersion = "1.0.0", artifactId, kind = "content-input", contractVersion = "1.0.0", mediaType = "application/vnd.knowledge-clipper.content-input+json",
                files,
                createdAt = DateTimeOffset.UtcNow,
                producer = new { stageId = "core-adapter", workerId = "knowledge-clipper.orchestrator", workerVersion = "2.1.0", attemptId = $"adapter-{artifactId}" },
                inputRefs = new[] { source }, dataClass = "sensitive", metadata
            }, cancellationToken);
        }
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var reference = new ArtifactReference(artifactId, "content-input", "1.0.0", manifestRelative, Integrity.Sha256Hex(manifestBytes).ToLowerInvariant());
        await ValidateReferenceAsync(jobRoot, reference, cancellationToken);
        return reference;
    }

    public static async Task<ArtifactReference> PublishFilesAsync(
        string jobRoot,
        string runId,
        string stageId,
        string artifactId,
        string kind,
        string mediaType,
        IReadOnlyList<CoreArtifactPayload> payloads,
        IReadOnlyList<ArtifactReference> inputRefs,
        JsonElement metadata,
        CancellationToken cancellationToken) =>
        await PublishFilesWithInheritedAsync(
            jobRoot, runId, stageId, artifactId, kind, mediaType, payloads,
            Array.Empty<CoreArtifactFile>(), inputRefs, metadata, cancellationToken);

    public static async Task<ArtifactReference> PublishFilesWithInheritedAsync(
        string jobRoot,
        string runId,
        string stageId,
        string artifactId,
        string kind,
        string mediaType,
        IReadOnlyList<CoreArtifactPayload> payloads,
        IReadOnlyList<CoreArtifactFile> inheritedFiles,
        IReadOnlyList<ArtifactReference> inputRefs,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        var versionRoot = $"runs/{runId}/adapters/{stageId}/{artifactId}/v0001";
        var files = new List<object>();
        foreach (var payload in payloads)
        {
            if (Path.GetFileName(payload.Name) != payload.Name)
                throw new OrchestratorDispatchException("binding.adapter_filename_invalid", $"Adapter payload filename is unsafe: {payload.Name}");
            var payloadRelative = $"{versionRoot}/{payload.Name}";
            var payloadPath = PathPolicy.ResolveJobRelative(jobRoot, payloadRelative);
            await WriteBytesOnceOrVerifyAsync(payloadPath, payload.Bytes, cancellationToken);
            files.Add(new { path = payloadRelative, sha256 = Integrity.Sha256Hex(payload.Bytes).ToLowerInvariant(), bytes = payload.Bytes.LongLength, mediaType = payload.MediaType });
        }
        var declaredPaths = payloads.Select(payload => $"{versionRoot}/{payload.Name}").ToHashSet(StringComparer.Ordinal);
        foreach (var inherited in inheritedFiles)
        {
            if (!declaredPaths.Add(inherited.Path))
                throw new OrchestratorDispatchException("binding.adapter_file_duplicate", $"Adapter file path is duplicated: {inherited.Path}");
            await Integrity.VerifyFileAsync(jobRoot, inherited.Path, inherited.Bytes, inherited.Sha256, cancellationToken);
            files.Add(new { path = inherited.Path, sha256 = inherited.Sha256, bytes = inherited.Bytes, mediaType = inherited.MediaType });
        }
        var manifestRelative = $"{versionRoot}/artifact.json";
        var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, manifestRelative);
        if (!File.Exists(manifestPath))
        {
            await AtomicFile.WriteJsonOnceAsync(manifestPath, new
            {
                schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType,
                files,
                createdAt = DateTimeOffset.UtcNow,
                producer = new { stageId = "core-adapter", workerId = "orchestrator.core-adapter", workerVersion = "1.0.0", attemptId = $"adapter-{artifactId}" },
                inputRefs, dataClass = "sensitive", metadata
            }, cancellationToken);
        }
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var reference = new ArtifactReference(artifactId, kind, "1.0.0", manifestRelative, Integrity.Sha256Hex(manifestBytes).ToLowerInvariant());
        await ValidateReferenceAsync(jobRoot, reference, cancellationToken);
        return reference;
    }

    public static async Task<ArtifactReference> PublishAliasAsync(
        string jobRoot,
        string runId,
        string stageId,
        ArtifactReference source,
        string targetKind,
        CancellationToken cancellationToken)
    {
        var sourceManifest = await ReadManifestAsync(jobRoot, source, cancellationToken);
        var digest = Integrity.Sha256Hex(Encoding.UTF8.GetBytes($"{source.ExpectedSha256}\0{targetKind}"))[..24].ToLowerInvariant();
        var artifactId = $"alias-{targetKind.Replace('.', '-')}-{digest}";
        var versionRoot = $"runs/{runId}/adapters/{stageId}/{artifactId}/v0001";
        var manifestRelative = $"{versionRoot}/artifact.json";
        var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, manifestRelative);
        if (!File.Exists(manifestPath))
        {
            await AtomicFile.WriteJsonOnceAsync(manifestPath, new
            {
                schemaVersion = "1.0.0", artifactId, kind = targetKind, contractVersion = "1.0.0", mediaType = sourceManifest.MediaType,
                files = sourceManifest.Files.Select(file => new { path = file.Path, sha256 = file.Sha256, bytes = file.Bytes, mediaType = file.MediaType }).ToArray(),
                createdAt = DateTimeOffset.UtcNow,
                producer = new { stageId = "core-adapter", workerId = "orchestrator.core-adapter", workerVersion = "1.0.0", attemptId = $"adapter-{artifactId}" },
                inputRefs = new[] { source }, dataClass = "sensitive",
                metadata = JsonSerializer.SerializeToElement(new { aliasOf = source.ArtifactRef, sourceKind = source.Kind })
            }, cancellationToken);
        }
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var reference = new ArtifactReference(artifactId, targetKind, "1.0.0", manifestRelative, Integrity.Sha256Hex(manifestBytes).ToLowerInvariant());
        await ValidateReferenceAsync(jobRoot, reference, cancellationToken);
        return reference;
    }

    private static async Task WriteBytesOnceOrVerifyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!existing.AsSpan().SequenceEqual(bytes))
                throw new IOException($"Append-only adapter payload conflict: {path}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
