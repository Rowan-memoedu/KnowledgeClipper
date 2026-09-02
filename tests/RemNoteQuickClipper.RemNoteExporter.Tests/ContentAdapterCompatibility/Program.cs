using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

var projectRoot = Path.GetFullPath(args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..") : args[0]);
var stamp = DateTimeOffset.UtcNow.ToString("yyMMddHHmmss");
var canaryRoot = Path.Combine(projectRoot, "canary", "rwa", stamp);
var jobsRoot = Path.Combine(canaryRoot, "jobs");
Directory.CreateDirectory(jobsRoot);

var fixtureAttempt = Path.Combine(projectRoot, "content-worker", "canary", "jobs", "20260825T141838065Z-offline-qa-replay-28f1ec17",
    "runs", "run-9b930a5c2326de3f8b7f", "stages", "content.understand-and-card", "attempts", "attempt-9b930a5c2326de3f8b7f", "outputs");
var learningFixture = Path.Combine(fixtureAttempt, "learning-bundle.schema-v1.json");
var cardFixture = Path.Combine(fixtureAttempt, "card-bundle.schema-v1.json");
var learningSchema = Path.Combine(projectRoot, "content-worker", "schemas", "learning-bundle.v1.schema.json");
var cardSchema = Path.Combine(projectRoot, "content-worker", "schemas", "card-bundle.v1.schema.json");
var exporter = Path.Combine(projectRoot, "workers", "remnote", "KnowledgeClipper.RemNoteExporter.Worker.exe");
var realLanguageJob = Path.Combine(projectRoot, "content-worker", "canary", "jobs", "20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e");
var realLanguageAuthorityReceipt = Path.Combine(realLanguageJob, "canary-receipt.json");
var realLanguageAttempt = Path.Combine(realLanguageJob,
    "runs", "run-200a433f248b2eab5eba", "stages", "content.understand-and-card", "attempts", "attempt-200a433f248b2eab5eba", "outputs");
var realLanguageCardFixture = Path.Combine(realLanguageAttempt, "card-bundle.schema-v1.json");
var realLanguageLearningFixture = Path.Combine(realLanguageAttempt, "learning-bundle.schema-v1.json");
var extendedResolverConfiguration = Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json");
var canonicalResolverConfiguration = Path.Combine(projectRoot, "contracts", "core", "v2", "profile-resolver.v2.json");
var legacyResolverConfiguration = Path.Combine(projectRoot, "contracts", "core-adapter", "v1", "profile-resolver.v1.json");
var resolverConfiguration = File.Exists(extendedResolverConfiguration) ? extendedResolverConfiguration : File.Exists(canonicalResolverConfiguration) ? canonicalResolverConfiguration : legacyResolverConfiguration;

RequireFile(learningFixture);
RequireFile(cardFixture);
RequireFile(learningSchema);
RequireFile(cardSchema);
RequireFile(exporter);
RequireFile(realLanguageAuthorityReceipt);
RequireFile(realLanguageCardFixture);
RequireFile(realLanguageLearningFixture);
RequireFile(resolverConfiguration);
Require(Sha256(await File.ReadAllBytesAsync(realLanguageAuthorityReceipt)) == "6e7ddc2b71d7647bab24b89a28ee288cc11cf7050d012d4c49adae63a3cb9e63", "real language authority receipt SHA-256 mismatch");
using var resolverConfigurationJson = JsonDocument.Parse(await File.ReadAllBytesAsync(resolverConfiguration));
var remNoteMapping = resolverConfigurationJson.RootElement.GetProperty("schemaVersion").GetString()?.StartsWith("knowledge-clipper-profile-resolver.v2", StringComparison.Ordinal) == true
    ? resolverConfigurationJson.RootElement.GetProperty("destinationConfigurations").GetProperty("remnote").GetProperty("projection").Clone()
    : resolverConfigurationJson.RootElement.GetProperty("remNoteProjection").Clone();

var checks = new List<Check>();
var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);

await CheckAsync("boundary.upstream_bundles_are_target_neutral", async () =>
{
    foreach (var path in new[] { learningFixture, cardFixture, realLanguageLearningFixture, realLanguageCardFixture, learningSchema, cardSchema })
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        RequireTargetNeutralKeys(document.RootElement, Path.GetFileName(path));
    }
    evidence["targetNeutralInputs"] = new[] { Relative(learningFixture), Relative(cardFixture), Relative(realLanguageLearningFixture), Relative(realLanguageCardFixture), Relative(learningSchema), Relative(cardSchema) };
    evidence["realLanguageAuthorityReceipt"] = new { path = Relative(realLanguageAuthorityReceipt), sha256 = Sha256(await File.ReadAllBytesAsync(realLanguageAuthorityReceipt)) };
    evidence["resolverConfiguration"] = Relative(resolverConfiguration);
});

await CheckAsync("boundary.descriptor_is_optional_terminal_exporter", async () =>
{
    using var descriptor = await ReadDescriptorAsync(exporter);
    var root = descriptor.RootElement;
    Require(root.GetProperty("workerId").GetString() == "export.remnote", "descriptor workerId drifted");
    Require(root.GetProperty("stages").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "export-remnote" }), "descriptor exposes a non-export stage");
    Require(root.GetProperty("inputKinds").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "learning.bundle", "card.bundle" }), "descriptor inputs are not target-neutral bundles");
    Require(root.GetProperty("outputKinds").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "remnote.write-receipt", "remnote.import-package" }), "descriptor outputs escaped exporter-owned boundary");
    evidence["canonicalWorker"] = Relative(exporter);
    evidence["descriptorBoundary"] = "target-neutral bundle inputs; RemNote appears only in exporter identity, stage, operation, and outputs";
});

var languageMappingReady = remNoteMapping.GetProperty("templateMappings").TryGetProperty("language_writing", out var languageWritingPolicy)
    && languageWritingPolicy.ValueKind == JsonValueKind.Object
    && languageWritingPolicy.TryGetProperty("policy", out var languageWritingAction)
    && languageWritingAction.GetString() == "project";

await CheckAsync("language_extension.deterministic_front_semantics", () =>
{
    const string sourceCardsJson = """
        {
          "cards": [{
            "card_id": "language-writing-1",
            "item_id": "item-language",
            "unit_id": "unit-language",
            "variant_group_id": "variant-language",
            "template": "language_writing",
            "front_blocks": [{ "type": "text", "text": "The plan is （/ˈtentətɪv/；暂定的）." }],
            "back_blocks": [{ "type": "text", "text": "tentative" }]
          }]
        }
        """;
    const string projectedCardsJson = """
        {
          "cards": [{
            "cardId": "language-writing-1",
            "cardType": "basic",
            "direction": "forward",
            "front": "The plan is （/ˈtentətɪv/；暂定的）.",
            "back": "tentative"
          }]
        }
        """;
    const string learningJson = """
        {
          "items": [{
            "item_id": "item-language",
            "source_segments": [{ "segment_id": "segment-language", "text": "The plan is tentative." }],
            "units": [{
              "unit_id": "unit-language",
              "source_segment_ids": ["segment-language"],
              "target_term": "tentative"
            }]
          }],
          "ai_trace": [{
            "stage": "language_writing",
            "call_key": "language_writing:item-language:unit-language",
            "response": {
              "pronunciation": { "reliability": "reliable", "value": "/ˈtentətɪv/", "evidence": "fixture" },
              "meaning": { "reliability": "reliable", "value": "暂定的", "evidence": "fixture" },
              "unresolved_reasons": []
            }
          }]
        }
        """;
    using var sourceCards = JsonDocument.Parse(sourceCardsJson);
    using var projectedCards = JsonDocument.Parse(projectedCardsJson);
    using var learning = JsonDocument.Parse(learningJson);
    var counts = ValidateLanguageWritingProjection(sourceCards.RootElement, learning.RootElement, projectedCards.RootElement);
    Require(counts.WritingCards == 1, "valid language_writing semantic fixture was not counted");

    ExpectLanguageProjectionJsonRejected(
        sourceCardsJson.Replace("The plan is （/ˈtentətɪv/；暂定的）.", "The plan is tentative（/ˈtentətɪv/；暂定的）.", StringComparison.Ordinal),
        projectedCardsJson.Replace("The plan is （/ˈtentətɪv/；暂定的）.", "The plan is tentative（/ˈtentətɪv/；暂定的）.", StringComparison.Ordinal),
        learningJson,
        "front is not the exact deterministic replacement");
    ExpectLanguageProjectionRejected(sourceCards.RootElement, projectedCards.RootElement,
        learningJson.Replace("The plan is tentative.", "The plan is final.", StringComparison.Ordinal),
        "target_term occurrence count must equal one");
    ExpectLanguageProjectionRejected(sourceCards.RootElement, projectedCards.RootElement,
        learningJson.Replace("The plan is tentative.", "tentative remains tentative.", StringComparison.Ordinal),
        "target_term occurrence count must equal one");
    ExpectLanguageProjectionRejected(sourceCards.RootElement, projectedCards.RootElement,
        learningJson.Replace("\"stage\": \"language_writing\"", "\"stage\": \"card_generation\"", StringComparison.Ordinal),
        "not losslessly available from LearningBundle");

    evidence["languageWritingSemanticGate"] = new
    {
        originalSentence = "The plan is tentative.",
        targetTerm = "tentative",
        pronunciation = "/ˈtentətɪv/",
        meaning = "暂定的",
        expectedFront = "The plan is （/ˈtentətɪv/；暂定的）.",
        back = "tentative",
        retainedTargetTermFront = "rejected",
        zeroOccurrence = "rejected",
        multipleOccurrences = "rejected",
        missingLearningEvidence = "rejected"
    };
    return Task.CompletedTask;
});

await CheckAsync("language_extension.real_content_source_semantics", async () =>
{
    using var sourceCards = JsonDocument.Parse(await File.ReadAllBytesAsync(realLanguageCardFixture));
    using var learning = JsonDocument.Parse(await File.ReadAllBytesAsync(realLanguageLearningFixture));
    var standardInputPath = FindLinkedStandardInput(realLanguageLearningFixture, learning.RootElement);
    using var standardInput = JsonDocument.Parse(await File.ReadAllBytesAsync(standardInputPath));
    var counts = ValidateLanguageWritingSourceSemantics(sourceCards.RootElement, learning.RootElement, standardInput.RootElement);
    Require(counts.WritingCards == 2, "real Content fixture must contain two language_writing cards");
    Require(counts.BothGroups == 2, "real Content fixture must contain two ordered both groups");
    RequireNoControlPlaneKeys(sourceCards.RootElement, "real Content CardBundle", new[] { "choice", "token", "languagePracticeMode" });
    evidence["realLanguageSource"] = new
    {
        cardBundle = Relative(realLanguageCardFixture),
        learningBundle = Relative(realLanguageLearningFixture),
        standardInput = Relative(standardInputPath),
        writingCards = counts.WritingCards,
        bothGroups = counts.BothGroups,
        sourceCardOrder = sourceCards.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.GetProperty("template").GetString()).ToArray(),
        writingFronts = sourceCards.RootElement.GetProperty("cards").EnumerateArray().Where(card => card.GetProperty("template").GetString() == "language_writing").Select(card => card.GetProperty("front_blocks")[0].GetProperty("text").GetString()).ToArray(),
        terminalContainerNewline = "removed by Content before card construction; no Exporter trimming"
    };
});

if (!languageMappingReady)
{
    await CheckAsync("language_extension.unfrozen_template_rejected", async () =>
    {
        var jobRoot = Path.Combine(jobsRoot, "tlw-pending");
        var fixture = await WriteFixtureAsync(canaryRoot, "language-writing-unfrozen.json", new
        {
            schema_version = "card-bundle/1.0.0", bundle_id = "language-writing-unfrozen", release_state = "ready",
            cards = new[] { new { card_id = "language-writing-1", template = "language_writing", front_blocks = new[] { new { type = "text", text = "sentence（reading；meaning）" } }, back_blocks = new[] { new { type = "text", text = "term" } } } }
        });
        await ExpectProjectionRejectedAsync(jobRoot, "rlw-pending", await PublishSourceAsync(jobRoot, "card-bundle", fixture), "card.bundle", remNoteMapping);
        evidence["languageExtension"] = "real language_writing/both Content output is present and source semantics pass; awaiting explicit Core RemNote projection mapping";
    });
}
else
{
    await CheckAsync("language_extension.real_fixture_projection", async () =>
    {
        var fixtures = await FindLanguageCardBundleFixturesAsync(Path.Combine(projectRoot, "content-worker", "fixtures"));
        fixtures.Add(realLanguageCardFixture);
        fixtures = fixtures.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        Require(fixtures.Count > 0, "language_writing mapping is enabled without a frozen real Content CardBundle fixture");
        var writingCount = 0;
        var bothGroupCount = 0;
        var fixtureEvidence = new List<object>();
        for (var fixtureIndex = 0; fixtureIndex < fixtures.Count; fixtureIndex++)
        {
            var cardPath = fixtures[fixtureIndex];
            using var cardSource = JsonDocument.Parse(await File.ReadAllBytesAsync(cardPath));
            var learningRelative = cardSource.RootElement.GetProperty("learning_bundle_path").GetString()!;
            var learningPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cardPath)!, learningRelative));
            RequireFile(learningPath);
            using var learningSourceJson = JsonDocument.Parse(await File.ReadAllBytesAsync(learningPath));
            var standardInputPath = FindLinkedStandardInput(learningPath, learningSourceJson.RootElement);
            using var standardInputJson = JsonDocument.Parse(await File.ReadAllBytesAsync(standardInputPath));

            var jobRoot = Path.Combine(jobsRoot, "tlw-" + fixtureIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var cardSourceRef = await PublishSourceAsync(jobRoot, "card-bundle", cardPath);
            var learningSourceRef = await PublishSourceAsync(jobRoot, "learning-bundle", learningPath);
            var projected = await ResolveCoreAdapterAsync(jobRoot, "rlw-" + fixtureIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), cardSourceRef, "card.bundle", remNoteMapping, new[] { learningSourceRef });
            using var projectedJson = await ReadPayloadAsync(jobRoot, projected);
            RequireNoControlPlaneKeys(projectedJson.RootElement, "Core RemNote projection", new[] { "template", "category", "choice", "token", "targetTerm", "target_term", "languagePracticeMode" });
            var counts = ValidateLanguageWritingProjection(cardSource.RootElement, learningSourceJson.RootElement, projectedJson.RootElement, standardInputJson.RootElement);
            ValidateCardProjection(cardSource.RootElement, projectedJson.RootElement);
            writingCount += counts.WritingCards;
            bothGroupCount += counts.BothGroups;

            var result = await RunExporterAsync(projectRoot, exporter, jobRoot, projected, "alw-" + fixtureIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), StableDocumentTitle(projectedJson.RootElement));
            Require(result.ExitCode == 0, "Exporter rejected frozen language projection: " + result.StandardError);
            using var resultJson = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ResultPath));
            var expectedMarkdown = string.Join("", projectedJson.RootElement.GetProperty("cards").EnumerateArray().Select(card => "- " + card.GetProperty("front").GetString() + " >> " + card.GetProperty("back").GetString() + "\n"));
            Equal(Encoding.UTF8.GetBytes(expectedMarkdown), await ReadZipMarkdownAsync(jobRoot, resultJson.RootElement), "language cards were merged, reordered, or text-normalized");
            var projectionRows = cardSource.RootElement.GetProperty("cards").EnumerateArray().Select((card, index) => new
            {
                index,
                cardId = card.GetProperty("card_id").GetString(),
                template = card.GetProperty("template").GetString(),
                inputFront = card.GetProperty("front_blocks")[0].GetProperty("text").GetString(),
                inputBack = card.GetProperty("back_blocks")[0].GetProperty("text").GetString(),
                projectedCardId = projectedJson.RootElement.GetProperty("cards")[index].GetProperty("cardId").GetString(),
                projectedCardType = projectedJson.RootElement.GetProperty("cards")[index].GetProperty("cardType").GetString(),
                projectedDirection = projectedJson.RootElement.GetProperty("cards")[index].GetProperty("direction").GetString(),
                projectedFront = projectedJson.RootElement.GetProperty("cards")[index].GetProperty("front").GetString(),
                projectedBack = projectedJson.RootElement.GetProperty("cards")[index].GetProperty("back").GetString()
            }).ToArray();
            fixtureEvidence.Add(new { cardBundle = Relative(cardPath), learningBundle = Relative(learningPath), standardInput = Relative(standardInputPath), result = Relative(result.ResultPath), cards = projectionRows });
        }
        Require(writingCount > 0, "frozen fixtures contain no language_writing card");
        Require(bothGroupCount > 0, "frozen fixtures contain no both case with two independent language cards");
        evidence["languageExtension"] = new { state = "accepted", writingCount, bothGroupCount, fixtures = fixtureEvidence };
    });
}

await CheckAsync("card.real_fixture_projection", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "tc");
    var source = await PublishSourceAsync(jobRoot, "card-bundle", cardFixture);
    var learningSource = await PublishSourceAsync(jobRoot, "learning-bundle", learningFixture);
    var sourcePayloadBefore = await PayloadBytesAsync(jobRoot, source);
    var sourceManifestBefore = await File.ReadAllBytesAsync(Path.Combine(jobRoot, source.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
    var first = await ResolveCoreAdapterAsync(jobRoot, "ra", source, "card.bundle", remNoteMapping, new[] { learningSource });
    var second = await ResolveCoreAdapterAsync(jobRoot, "rb", source, "card.bundle", remNoteMapping, new[] { learningSource });
    var sourceJson = JsonDocument.Parse(await File.ReadAllBytesAsync(cardFixture));
    var firstPayload = await ReadPayloadAsync(jobRoot, first);
    var secondPayload = await ReadPayloadAsync(jobRoot, second);
    ValidateCardProjection(sourceJson.RootElement, firstPayload.RootElement);
    Equal(await PayloadBytesAsync(jobRoot, first), await PayloadBytesAsync(jobRoot, second), "projection payload changed across run IDs");
    Equal(sourcePayloadBefore, await PayloadBytesAsync(jobRoot, source), "terminal adapter mutated the upstream CardBundle payload");
    Equal(sourceManifestBefore, await File.ReadAllBytesAsync(Path.Combine(jobRoot, source.ManifestPath.Replace('/', Path.DirectorySeparatorChar))), "terminal adapter mutated the upstream Artifact manifest");
    evidence["cardProjectionArtifact"] = first.ManifestPath;
    evidence["cardProjectionSha256"] = first.ExpectedSha256.ToLowerInvariant();

    var result = await RunExporterAsync(projectRoot, exporter, jobRoot, first, "ac", StableDocumentTitle(firstPayload.RootElement));
    Require(result.ExitCode == 0, "Exporter offline entry returned " + result.ExitCode + ": " + result.StandardError);
    using var resultJson = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ResultPath));
    Require(resultJson.RootElement.GetProperty("status").GetString() == "completed", "Exporter did not complete the adapted card bundle");
    var expectedMarkdown = "- " + firstPayload.RootElement.GetProperty("cards")[0].GetProperty("front").GetString() + " >> " +
        firstPayload.RootElement.GetProperty("cards")[0].GetProperty("back").GetString() + "\n";
    Equal(Encoding.UTF8.GetBytes(expectedMarkdown), await ReadZipMarkdownAsync(jobRoot, resultJson.RootElement), "Exporter ZIP text/order mismatch");
    evidence["cardExporterResult"] = Path.GetRelativePath(projectRoot, result.ResultPath).Replace('\\', '/');
});

await CheckAsync("target.same_bundle_across_task_ids", async () =>
{
    var zipEntries = new List<ZipMarkdown>();
    string? bundleId = null;
    foreach (var taskId in new[] { "tsa", "tsb" })
    {
        var jobRoot = Path.Combine(jobsRoot, taskId);
        var source = await PublishSourceAsync(jobRoot, "card-bundle", cardFixture);
        var learningSource = await PublishSourceAsync(jobRoot, "learning-bundle", learningFixture);
        var projected = await ResolveCoreAdapterAsync(jobRoot, "adapter-" + taskId, source, "card.bundle", remNoteMapping, new[] { learningSource });
        using var payload = await ReadPayloadAsync(jobRoot, projected);
        bundleId ??= payload.RootElement.GetProperty("bundleId").GetString();
        Require(payload.RootElement.GetProperty("bundleId").GetString() == bundleId, "same source fixture produced different bundle IDs");

        var result = await RunExporterAsync(projectRoot, exporter, jobRoot, projected, "attempt-" + taskId, "剪藏 · " + taskId);
        Require(result.ExitCode == 0, "Exporter target-strategy run returned " + result.ExitCode + ": " + result.StandardError);
        using var resultJson = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ResultPath));
        Require(resultJson.RootElement.GetProperty("status").GetString() == "completed", "Exporter target-strategy run did not complete");
        zipEntries.Add(await ReadZipMarkdownEntryAsync(jobRoot, resultJson.RootElement));
    }

    Require(zipEntries[0].Name == zipEntries[1].Name, "same bundle routed to different ZIP document names across task IDs");
    Require(zipEntries[0].Name == $"剪藏/剪藏 · {bundleId}.md", "ZIP document name is not rooted at the stable bundle document");
    Equal(zipEntries[0].Content, zipEntries[1].Content, "same bundle produced different Markdown across task IDs");
    Require(!zipEntries[0].Name.Contains("tsa", StringComparison.Ordinal) && !zipEntries[0].Name.Contains("tsb", StringComparison.Ordinal),
        "transient task ID leaked into stable document name");
    evidence["stableTargetZipEntry"] = zipEntries[0].Name;
    evidence["stableTargetMarkdown"] = Encoding.UTF8.GetString(zipEntries[0].Content);
});

await CheckAsync("learning.blank_line_remains_fail_closed", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "tl");
    var source = await PublishSourceAsync(jobRoot, "learning-bundle", learningFixture);
    using var sourceJson = JsonDocument.Parse(await File.ReadAllBytesAsync(learningFixture));
    var sourceText = sourceJson.RootElement.GetProperty("items")[0].GetProperty("cleaning").GetProperty("cleaned_text").GetString()!;
    Require(sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Contains("\n\n", StringComparison.Ordinal), "real fixture no longer exercises the blank-line boundary");
    var rejected = false;
    try { await ResolveCoreAdapterAsync(jobRoot, "rl", source, "learning.bundle", remNoteMapping); }
    catch (TargetInvocationException exception) when (exception.InnerException is OrchestratorDispatchException) { rejected = true; }
    catch (OrchestratorDispatchException) { rejected = true; }
    Require(rejected, "Core adapter silently normalized an unrepresentable blank line");
});

await CheckAsync("learning.multiline_text_projects_to_ordered_v2_entries", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "tml");
    const string sourceText = "first exact line\nsecond exact line";
    var fixture = await WriteFixtureAsync(canaryRoot, "learning-multiline-no-blank.json", new
    {
        schema_version = "learning-bundle/1.0.0", bundle_id = "learning-multiline-no-blank", release_state = "ready",
        items = new[] { new { item_id = "item-multiline", state = "ready", cleaning = new { cleaned_text = sourceText } } }
    });
    var source = await PublishSourceAsync(jobRoot, "learning-bundle", fixture);
    var projected = await ResolveCoreAdapterAsync(jobRoot, "rl", source, "learning.bundle", remNoteMapping);
    using var targetJson = await ReadPayloadAsync(jobRoot, projected);
    var entries = targetJson.RootElement.GetProperty("entries").EnumerateArray().ToArray();
    var projectedText = string.Join("\n", entries.Select(entry => entry.GetProperty("text").GetString()));
    Require(string.Equals(sourceText.Replace("\r\n", "\n", StringComparison.Ordinal), projectedText, StringComparison.Ordinal),
        "Core adapter did not preserve multiline LearningBundle line text and order");
    Require(entries.Select(entry => entry.GetProperty("itemId").GetString()).Distinct(StringComparer.Ordinal).Count() == entries.Length,
        "Core adapter did not derive stable unique item IDs for ordered multiline entries");
});

await CheckAsync("learning.inline_whitespace_preserved_or_rejected", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "twl");
    const string exact = "  meaningful leading and trailing space  ";
    var fixture = await WriteFixtureAsync(canaryRoot, "learning-inline-whitespace.json", new
    {
        schema_version = "learning-bundle/1.0.0", bundle_id = "learning-inline-whitespace", release_state = "ready",
        items = new[] { new { item_id = "item-inline-whitespace", state = "ready", cleaning = new { cleaned_text = exact } } }
    });
    var source = await PublishSourceAsync(jobRoot, "learning-bundle", fixture);
    try
    {
        var projected = await ResolveCoreAdapterAsync(jobRoot, "rwl", source, "learning.bundle", remNoteMapping);
        using var target = await ReadPayloadAsync(jobRoot, projected);
        Require(target.RootElement.GetProperty("entries")[0].GetProperty("text").GetString() == exact,
            "Core adapter silently trimmed representable LearningBundle text");
    }
    catch (TargetInvocationException exception) when (exception.InnerException is OrchestratorDispatchException)
    {
        // An explicit lossless-mapping rejection is acceptable.
    }
    catch (OrchestratorDispatchException)
    {
        // An explicit lossless-mapping rejection is acceptable.
    }
});

await CheckAsync("card.inline_whitespace_preserved_or_rejected", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "twc");
    const string front = "  exact front  ";
    const string back = "  exact back  ";
    var fixture = await WriteFixtureAsync(canaryRoot, "card-inline-whitespace.json", new
    {
        schema_version = "card-bundle/1.0.0", bundle_id = "cards-inline-whitespace", release_state = "ready",
        cards = new[] { new { card_id = "card-inline-whitespace", front_blocks = new[] { new { type = "text", text = front } }, back_blocks = new[] { new { type = "text", text = back } } } }
    });
    var source = await PublishSourceAsync(jobRoot, "card-bundle", fixture);
    try
    {
        var projected = await ResolveCoreAdapterAsync(jobRoot, "rwc", source, "card.bundle", remNoteMapping);
        using var target = await ReadPayloadAsync(jobRoot, projected);
        var card = target.RootElement.GetProperty("cards")[0];
        Require(card.GetProperty("front").GetString() == front && card.GetProperty("back").GetString() == back,
            "Core adapter silently trimmed representable CardBundle text");
    }
    catch (TargetInvocationException exception) when (exception.InnerException is OrchestratorDispatchException)
    {
        // An explicit lossless-mapping rejection is acceptable.
    }
    catch (OrchestratorDispatchException)
    {
        // An explicit lossless-mapping rejection is acceptable.
    }
});

await CheckAsync("card.multiple_blocks_rejected", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "tmb");
    var fixture = await WriteFixtureAsync(canaryRoot, "card-multiple-blocks.json", new
    {
        schema_version = "card-bundle/1.0.0", bundle_id = "cards-multiple-blocks", release_state = "ready",
        cards = new[] { new { card_id = "card-multiple-blocks", front_blocks = new[] { new { type = "text", text = "part one" }, new { type = "text", text = "part two" } }, back_blocks = new[] { new { type = "text", text = "answer" } } } }
    });
    await ExpectProjectionRejectedAsync(jobRoot, "rmb", await PublishSourceAsync(jobRoot, "card-bundle", fixture), "card.bundle", remNoteMapping);
});

await CheckAsync("card.asset_block_rejected", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "tab");
    var fixture = await WriteFixtureAsync(canaryRoot, "card-asset-block.json", new
    {
        schema_version = "card-bundle/1.0.0", bundle_id = "cards-asset-block", release_state = "ready",
        cards = new[] { new { card_id = "card-asset-block", front_blocks = new[] { new { type = "asset_ref", material_path = "images/question.png" } }, back_blocks = new[] { new { type = "text", text = "answer" } } } }
    });
    await ExpectProjectionRejectedAsync(jobRoot, "rab", await PublishSourceAsync(jobRoot, "card-bundle", fixture), "card.bundle", remNoteMapping);
});

await CheckAsync("kind_only_alias_rejected", async () =>
{
    var jobRoot = Path.Combine(jobsRoot, "tk");
    var source = await PublishSourceAsync(jobRoot, "card.bundle", cardFixture);
    var result = await RunExporterAsync(projectRoot, exporter, jobRoot, source, "ak", "Kind-only must fail");
    Require(result.ExitCode != 0, "kind-only payload was accepted");
    using var resultJson = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ResultPath));
    Require(resultJson.RootElement.GetProperty("status").GetString() == "failed", "kind-only result was not failed");
    Require(resultJson.RootElement.GetProperty("outputs").GetArrayLength() == 0, "kind-only failure published outputs");
    evidence["kindOnlyResult"] = Path.GetRelativePath(projectRoot, result.ResultPath).Replace('\\', '/');
});

await CheckAsync("card.lossless_constraints", async () =>
{
    using var source = JsonDocument.Parse(await File.ReadAllBytesAsync(cardFixture));
    ValidateContentCardSource(source.RootElement);
});

var receipt = new
{
    schemaVersion = "1.0.0",
    suite = "export.remnote-content-to-remnote.v1-consumer-acceptance",
    status = checks.All(check => check.Status == "passed") ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    sourceFixtures = new[]
    {
        Relative(learningFixture), Relative(cardFixture), Relative(realLanguageLearningFixture), Relative(realLanguageCardFixture)
    },
    rules = new
    {
        learning = new { minimum = new[] { "schema_version", "bundle_id", "release_state", "items[].item_id", "items[].state", "items[].cleaning.cleaned_text" }, order = "items array preserved", ids = "bundle_id/item_id preserved", text = "exact; no trimming or newline flattening" },
        card = new { minimum = new[] { "schema_version", "bundle_id", "release_state", "cards[].card_id", "cards[].front_blocks", "cards[].back_blocks" }, order = "cards array preserved", ids = "bundle_id/card_id preserved", semantics = "basic/forward; exactly one text or markdown block per side; exact text" },
        languageWriting = new { source = "LearningBundle source_segments + unit.target_term + reliable language_writing ai_trace or SHA-linked human-confirmed standard input pronunciation/meaning", occurrence = "target_term must occur exactly once or projection is rejected", front = "original sentence with the one target_term replaced by full-width （pronunciation；meaning）; exact text including trailing newline; target_term absent", back = "exact original target_term", both = "exactly language_context then language_writing; distinct card IDs; source order preserved", leakage = "Core projection contains no template/category/choice/token/target-term/mode control fields" },
        unrepresentable = "adapter must fail before publishing projected artifact",
        kindOnly = "real Exporter must fail with zero outputs",
        boundary = "LearningBundle/CardBundle keys are target-neutral; adapter is a terminal projection and source artifacts remain immutable"
    },
    checks,
    evidence
};
var receiptPath = Path.Combine(canaryRoot, "receipt.json");
await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, ContractJson.Options), new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(new { receiptPath, receiptSha256 = Sha256(await File.ReadAllBytesAsync(receiptPath)), receipt.status, checks }, ContractJson.Options));
return receipt.status == "passed" ? 0 : 1;

async Task CheckAsync(string name, Func<Task> action)
{
    try
    {
        await action();
        checks.Add(new Check(name, "passed", null));
    }
    catch (Exception exception)
    {
        var actual = exception is TargetInvocationException { InnerException: not null } target ? target.InnerException! : exception;
        checks.Add(new Check(name, "failed", actual.GetType().Name + ": " + actual.Message));
    }
}

string Relative(string path) => Path.GetRelativePath(projectRoot, path).Replace('\\', '/');

static void RequireFile(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException("Required acceptance input is missing", path);
}

static void Require(bool value, string message)
{
    if (!value) throw new InvalidDataException(message);
}

static void Equal(byte[] expected, byte[] actual, string message)
{
    if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidDataException(message);
}

static async Task<ArtifactReference> PublishSourceAsync(string jobRoot, string kind, string fixturePath)
{
    Directory.CreateDirectory(jobRoot);
    var bytes = await File.ReadAllBytesAsync(fixturePath);
    var artifactId = "content-fixture-" + kind.Replace('.', '-');
    var root = Path.Combine(jobRoot, "inputs", artifactId, "v0001");
    Directory.CreateDirectory(root);
    var payloadPath = Path.Combine(root, "bundle.json");
    await File.WriteAllBytesAsync(payloadPath, bytes);
    var payloadRelative = Path.GetRelativePath(jobRoot, payloadPath).Replace('\\', '/');
    var manifestPath = Path.Combine(root, "artifact.json");
    var manifest = new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "application/json",
        files = new[] { new { path = payloadRelative, sha256 = Sha256(bytes), bytes = bytes.LongLength, mediaType = "application/json" } },
        createdAt = DateTimeOffset.Parse("2026-08-25T14:18:38.334Z"),
        producer = new { stageId = "content.understand-and-card", workerId = "content.deepseek", workerVersion = "0.2.0", attemptId = "attempt-9b930a5c2326de3f8b7f" },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = payloadRelative }
    };
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ContractJson.Options), new UTF8Encoding(false));
    var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
    return new ArtifactReference(artifactId, kind, "1.0.0", Path.GetRelativePath(jobRoot, manifestPath).Replace('\\', '/'), Sha256(manifestBytes));
}

static async Task<string> WriteFixtureAsync(string canaryRoot, string name, object value)
{
    var directory = Path.Combine(canaryRoot, "fixtures");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, name);
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, ContractJson.Options), new UTF8Encoding(false));
    return path;
}

static async Task<ArtifactReference> ResolveCoreAdapterAsync(
    string jobRoot,
    string runId,
    ArtifactReference source,
    string targetKind,
    JsonElement remNoteMapping,
    IReadOnlyList<ArtifactReference>? supplemental = null)
{
    var stageId = "export-remnote";
    var outputs = new[] { source }.Concat(supplemental ?? Array.Empty<ArtifactReference>()).ToArray();
    var sourceResult = new WorkerResult("1.0.0", "request-content", "operation-content", "task-source", runId, "content.understand-and-card", "attempt-content",
        new WorkerIdentity("content.deepseek", "0.2.0"), "1.0.0", new string('1', 64), "completed", outputs, null,
        DateTimeOffset.Parse("2026-08-25T14:18:38Z"), DateTimeOffset.Parse("2026-08-25T14:18:39Z"), Array.Empty<string>());
    var completed = new Dictionary<string, WorkerResult>(StringComparer.Ordinal) { ["content.understand-and-card"] = sourceResult };
    var bindings = outputs.Select(item => new StageOutputBinding("content.understand-and-card", item.Kind, true)).ToArray();
    var stage = new PipelineStage(stageId, "export.remnote", "remnote.export", new[] { "content.understand-and-card" }, Array.Empty<ArtifactReference>(),
        new[] { "remnote.import-package" }, 180000, new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }),
        JsonSerializer.SerializeToElement(new { schemaVersion = "1.0.0", documentTitle = "adapter fixture", sink = "markdown-zip", target = new { rootRemId = "offline-root", rootTitle = "剪藏" } }),
        bindings, new StageInputAdapter("content-to-remnote.v1", targetKind, remNoteMapping));

    var resolverType = typeof(PipelineStage).Assembly.GetType(typeof(PipelineStage).Namespace + ".StageInputResolver", true)!;
    var resolver = Activator.CreateInstance(resolverType, true)!;
    var method = resolverType.GetMethod("ResolveAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(resolverType.FullName, "ResolveAsync");
    var invoked = method.Invoke(resolver, new object[] { jobRoot, runId, "task-adapter-fixture", stage, completed, CancellationToken.None })
        ?? throw new InvalidOperationException("Core adapter returned null Task");
    var task = (Task)invoked;
    await task;
    var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
    var refs = (IReadOnlyList<ArtifactReference>)result.GetType().GetProperty("InputRefs")!.GetValue(result)!;
    Require(refs.Count == 1 && refs[0].Kind == targetKind, "Core adapter did not publish exactly one target artifact");
    return refs[0];
}

static async Task ExpectProjectionRejectedAsync(string jobRoot, string runId, ArtifactReference source, string targetKind, JsonElement remNoteMapping)
{
    try
    {
        await ResolveCoreAdapterAsync(jobRoot, runId, source, targetKind, remNoteMapping);
    }
    catch (TargetInvocationException exception) when (exception.InnerException is OrchestratorDispatchException)
    {
        return;
    }
    catch (OrchestratorDispatchException)
    {
        return;
    }
    throw new InvalidDataException("Core adapter published an artifact for an unrepresentable content bundle");
}

static async Task<JsonDocument> ReadPayloadAsync(string jobRoot, ArtifactReference reference)
{
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(jobRoot, reference.ManifestPath.Replace('/', Path.DirectorySeparatorChar))));
    var entrypoint = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    return JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(jobRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar))));
}

static async Task<byte[]> PayloadBytesAsync(string jobRoot, ArtifactReference reference)
{
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(jobRoot, reference.ManifestPath.Replace('/', Path.DirectorySeparatorChar))));
    var entrypoint = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    return await File.ReadAllBytesAsync(Path.Combine(jobRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar)));
}

static void ValidateCardProjection(JsonElement source, JsonElement target)
{
    ValidateContentCardSource(source);
    Require(target.GetProperty("schemaVersion").GetString() == "card-bundle.v1", "target schemaVersion mismatch");
    Require(target.GetProperty("bundleId").GetString() == source.GetProperty("bundle_id").GetString(), "bundle ID not preserved");
    var sourceCards = source.GetProperty("cards").EnumerateArray().ToArray();
    var targetCards = target.GetProperty("cards").EnumerateArray().ToArray();
    Require(sourceCards.Length == targetCards.Length, "card count/order mismatch");
    for (var index = 0; index < sourceCards.Length; index++)
    {
        var expected = sourceCards[index];
        var actual = targetCards[index];
        Require(actual.GetProperty("cardId").GetString() == expected.GetProperty("card_id").GetString(), "card ID/order mismatch");
        Require(actual.GetProperty("cardType").GetString() == "basic", "cardType is not deterministic basic");
        Require(actual.GetProperty("direction").GetString() == "forward", "direction is not deterministic forward");
        Require(actual.GetProperty("front").GetString() == expected.GetProperty("front_blocks")[0].GetProperty("text").GetString(), "front text changed");
        Require(actual.GetProperty("back").GetString() == expected.GetProperty("back_blocks")[0].GetProperty("text").GetString(), "back text changed");
    }
}

static void ValidateContentCardSource(JsonElement source)
{
    Require(source.GetProperty("schema_version").GetString() == "card-bundle/1.0.0", "source card schema mismatch");
    Require(source.GetProperty("release_state").GetString() == "ready", "source card bundle not ready");
    SafeId(source.GetProperty("bundle_id").GetString()!, "bundle_id");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var card in source.GetProperty("cards").EnumerateArray())
    {
        var cardId = card.GetProperty("card_id").GetString()!;
        SafeId(cardId, "card_id");
        Require(seen.Add(cardId), "duplicate card_id");
        ValidateSide(card.GetProperty("front_blocks"), "front_blocks");
        ValidateSide(card.GetProperty("back_blocks"), "back_blocks");
    }
}

static LanguageProjectionCounts ValidateLanguageWritingSourceSemantics(JsonElement sourceCardsRoot, JsonElement learningRoot, JsonElement? standardInputRoot = null)
{
    var sourceCards = sourceCardsRoot.GetProperty("cards").EnumerateArray().ToArray();
    var writingCount = 0;
    foreach (var sourceCard in sourceCards)
    {
        if (sourceCard.GetProperty("template").GetString() != "language_writing") continue;
        writingCount++;
        var itemId = sourceCard.GetProperty("item_id").GetString();
        var unitId = sourceCard.GetProperty("unit_id").GetString();
        var item = learningRoot.GetProperty("items").EnumerateArray().Single(candidate => candidate.GetProperty("item_id").GetString() == itemId);
        var unit = item.GetProperty("units").EnumerateArray().Single(candidate => candidate.GetProperty("unit_id").GetString() == unitId);
        var targetTerm = unit.GetProperty("target_term").GetString()!;
        var originalSentence = ResolveUnitSourceText(item, unit);
        Require(CountOccurrencesOrdinal(originalSentence, targetTerm) == 1, "language_writing target_term occurrence count must equal one");
        var details = ResolveLanguageWritingDetails(learningRoot, itemId!, unitId!, standardInputRoot);
        var targetIndex = originalSentence.IndexOf(targetTerm, StringComparison.Ordinal);
        var expectedFront = originalSentence[..targetIndex] + "（" + details.Pronunciation + "；" + details.Meaning + "）" + originalSentence[(targetIndex + targetTerm.Length)..];
        var front = sourceCard.GetProperty("front_blocks")[0].GetProperty("text").GetString()!;
        var back = sourceCard.GetProperty("back_blocks")[0].GetProperty("text").GetString()!;
        Require(back == targetTerm, "language_writing back is not exact target_term source text");
        Require(front == expectedFront, "language_writing front is not the exact deterministic replacement of the source sentence");
        Require(!front.Contains(targetTerm, StringComparison.Ordinal), "language_writing front still contains target_term after replacement");
    }

    var bothGroups = sourceCards
        .GroupBy(card => card.GetProperty("variant_group_id").GetString(), StringComparer.Ordinal)
        .Count(group =>
        {
            var templates = group.Select(card => card.GetProperty("template").GetString()).ToArray();
            if (!templates.Contains("language_writing", StringComparer.Ordinal) || !templates.Contains("language_context", StringComparer.Ordinal)) return false;
            Require(group.Count() == 2, "both must emit exactly two independent cards");
            Require(templates.SequenceEqual(new[] { "language_context", "language_writing" }, StringComparer.Ordinal), "both card order must be language_context then language_writing");
            Require(group.Select(card => card.GetProperty("card_id").GetString()).Distinct(StringComparer.Ordinal).Count() == 2, "both cards must have distinct card IDs");
            return true;
        });
    return new LanguageProjectionCounts(writingCount, bothGroups);
}

static LanguageProjectionCounts ValidateLanguageWritingProjection(JsonElement sourceCardsRoot, JsonElement learningRoot, JsonElement projectedRoot, JsonElement? standardInputRoot = null)
{
    var counts = ValidateLanguageWritingSourceSemantics(sourceCardsRoot, learningRoot, standardInputRoot);
    var sourceCards = sourceCardsRoot.GetProperty("cards").EnumerateArray().ToArray();
    var projectedCards = projectedRoot.GetProperty("cards").EnumerateArray().ToArray();
    foreach (var sourceCard in sourceCards)
    {
        if (sourceCard.GetProperty("template").GetString() != "language_writing") continue;
        var index = Array.FindIndex(sourceCards, candidate => candidate.GetProperty("card_id").GetString() == sourceCard.GetProperty("card_id").GetString());
        var expectedFront = sourceCard.GetProperty("front_blocks")[0].GetProperty("text").GetString();
        var expectedBack = sourceCard.GetProperty("back_blocks")[0].GetProperty("text").GetString();
        Require(projectedCards[index].GetProperty("cardType").GetString() == "basic" && projectedCards[index].GetProperty("direction").GetString() == "forward", "language_writing is not a normal forward basic card");
        Require(projectedCards[index].GetProperty("front").GetString() == expectedFront && projectedCards[index].GetProperty("back").GetString() == expectedBack, "projected language_writing text differs from the validated source card");
    }
    return counts;
}

static string ResolveUnitSourceText(JsonElement item, JsonElement unit)
{
    var segments = item.GetProperty("source_segments").EnumerateArray()
        .ToDictionary(segment => segment.GetProperty("segment_id").GetString()!, segment => segment.GetProperty("text").GetString()!, StringComparer.Ordinal);
    var segmentSetText = string.Join("\n\n", unit.GetProperty("source_segment_ids").EnumerateArray().Select(id =>
    {
        var segmentId = id.GetString()!;
        Require(segments.TryGetValue(segmentId, out _), "language_writing unit references a missing source segment");
        return segments[segmentId];
    }));
    return segmentSetText.TrimEnd('\r', '\n');
}

static LanguageWritingDetails ResolveLanguageWritingDetails(JsonElement learningRoot, string itemId, string unitId, JsonElement? standardInputRoot)
{
    var callKey = "language_writing:" + itemId + ":" + unitId;
    var traces = learningRoot.GetProperty("ai_trace").EnumerateArray()
        .Where(trace => trace.GetProperty("stage").GetString() == "language_writing" && trace.GetProperty("call_key").GetString() == callKey)
        .ToArray();
    Require(traces.Length <= 1, "multiple language_writing traces match one unit");
    if (traces.Length == 1)
    {
        var response = traces[0].GetProperty("response");
        return new LanguageWritingDetails(
            ReadReliableLanguageWritingField(response, "pronunciation"),
            ReadReliableLanguageWritingField(response, "meaning"));
    }

    if (!standardInputRoot.HasValue) throw new InvalidDataException("language_writing pronunciation/meaning are not losslessly available from LearningBundle or its linked standard input");
    var root = standardInputRoot.GetValueOrDefault();
    if (!root.TryGetProperty("human_overrides", out var overrides)
        || !overrides.TryGetProperty("languageWriting", out var writing)
        || !writing.TryGetProperty("units", out var units)
        || !units.TryGetProperty(unitId, out var details)
        || !details.GetProperty("confirmed").GetBoolean())
        throw new InvalidDataException("language_writing human-confirmed evidence is missing from linked standard input");
    var pronunciation = details.GetProperty("pronunciation").GetString();
    var meaning = details.GetProperty("meaning").GetString();
    Require(!string.IsNullOrWhiteSpace(pronunciation) && !string.IsNullOrWhiteSpace(meaning), "language_writing human-confirmed pronunciation/meaning is empty");
    return new LanguageWritingDetails(pronunciation!, meaning!);
}

static string FindLinkedStandardInput(string learningPath, JsonElement learningRoot)
{
    var input = learningRoot.GetProperty("input");
    var relative = input.GetProperty("standard_input_path").GetString()!.Replace('/', Path.DirectorySeparatorChar);
    var expectedSha256 = input.GetProperty("standard_input_sha256").GetString()!;
    for (var directory = Directory.GetParent(learningPath); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, relative);
        if (!File.Exists(candidate)) continue;
        Require(Sha256(File.ReadAllBytes(candidate)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "linked standard input SHA-256 mismatch");
        return candidate;
    }
    throw new FileNotFoundException("LearningBundle linked standard input is missing", relative);
}

static void RequireNoControlPlaneKeys(JsonElement value, string context, IReadOnlyCollection<string> forbidden)
{
    if (value.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in value.EnumerateObject())
        {
            Require(!forbidden.Contains(property.Name, StringComparer.OrdinalIgnoreCase), context + " leaks control-plane key: " + property.Name);
            RequireNoControlPlaneKeys(property.Value, context, forbidden);
        }
    }
    else if (value.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in value.EnumerateArray()) RequireNoControlPlaneKeys(item, context, forbidden);
    }
}

static string ReadReliableLanguageWritingField(JsonElement response, string name)
{
    var field = response.GetProperty(name);
    Require(field.GetProperty("reliability").GetString() == "reliable", "language_writing " + name + " is not reliable");
    var value = field.GetProperty("value").GetString();
    Require(!string.IsNullOrWhiteSpace(value), "language_writing " + name + " is missing");
    return value!;
}

static int CountOccurrencesOrdinal(string source, string target)
{
    if (target.Length == 0) return 0;
    var count = 0;
    for (var cursor = 0; ;)
    {
        var index = source.IndexOf(target, cursor, StringComparison.Ordinal);
        if (index < 0) return count;
        count++;
        cursor = index + target.Length;
    }
}

static void ExpectLanguageProjectionRejected(JsonElement sourceCards, JsonElement projectedCards, string learningJson, string expectedMessage)
{
    try
    {
        using var learning = JsonDocument.Parse(learningJson);
        ValidateLanguageWritingProjection(sourceCards, learning.RootElement, projectedCards);
        throw new InvalidDataException("invalid language_writing fixture was accepted");
    }
    catch (InvalidDataException exception)
    {
        Require(exception.Message.Contains(expectedMessage, StringComparison.Ordinal), "language_writing rejection reason drifted: " + exception.Message);
    }
}

static void ExpectLanguageProjectionJsonRejected(string sourceCardsJson, string projectedCardsJson, string learningJson, string expectedMessage)
{
    using var sourceCards = JsonDocument.Parse(sourceCardsJson);
    using var projectedCards = JsonDocument.Parse(projectedCardsJson);
    ExpectLanguageProjectionRejected(sourceCards.RootElement, projectedCards.RootElement, learningJson, expectedMessage);
}

static async Task<List<string>> FindLanguageCardBundleFixturesAsync(string fixturesRoot)
{
    var result = new List<string>();
    if (!Directory.Exists(fixturesRoot)) return result;
    foreach (var path in Directory.EnumerateFiles(fixturesRoot, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema_version", out var schema) || schema.GetString()?.StartsWith("card-bundle/", StringComparison.Ordinal) != true || !root.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array) continue;
            if (cards.EnumerateArray().Any(card => card.TryGetProperty("template", out var template) && template.GetString() == "language_writing")) result.Add(path);
        }
        catch (JsonException)
        {
            // Non-JSON or partial provider fixtures are outside this consumer acceptance route.
        }
    }
    return result;
}

static void ValidateSide(JsonElement blocks, string name)
{
    Require(blocks.ValueKind == JsonValueKind.Array && blocks.GetArrayLength() == 1, name + " must contain exactly one block");
    var block = blocks[0];
    var type = block.GetProperty("type").GetString();
    Require(type is "text" or "markdown", name + " contains an image/unsupported block");
    var text = block.GetProperty("text").GetString()!;
    Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 20000, name + " text is empty/too long");
    Require(text.IndexOfAny(new[] { '\r', '\n' }) < 0 && !text.Any(char.IsControl), name + " cannot be represented as Exporter v1 inline text");
    foreach (var delimiter in new[] { ">>", "==", "<<", "<>", "><" })
        Require(!text.Contains(delimiter, StringComparison.Ordinal), name + " contains reserved RemNote delimiter");
}

static void SafeId(string value, string field)
{
    Require(value.Length is >= 1 and <= 128 && char.IsLetterOrDigit(value[0]) && value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'), field + " is not Exporter-safe");
}

static void RequireTargetNeutralKeys(JsonElement value, string context)
{
    if (value.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in value.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            Require(!key.Contains("remnote", StringComparison.Ordinal) && !key.Contains("anki", StringComparison.Ordinal), context + " contains target-specific key: " + property.Name);
            Require(key is not "rootremid" and not "roottitle" and not "documenttitle" and not "sink" and not "remnotemcpurl" and not "remnoteclijspath" and not "ankiconnecturl" and not "deck" and not "modelname",
                context + " contains exporter routing key: " + property.Name);
            RequireTargetNeutralKeys(property.Value, context);
        }
    }
    else if (value.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in value.EnumerateArray()) RequireTargetNeutralKeys(item, context);
    }
}

static string StableDocumentTitle(JsonElement target) => "剪藏 · " + target.GetProperty("bundleId").GetString();

static async Task<ExporterRun> RunExporterAsync(string projectRoot, string exporter, string jobRoot, ArtifactReference input, string attemptId, string documentTitle)
{
    var taskId = Path.GetFileName(jobRoot);
    const string runId = "r1";
    var attemptRelative = $"runs/{runId}/stages/export-remnote/attempts/{attemptId}";
    var attemptPath = Path.Combine(jobRoot, attemptRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(attemptPath);
    var requestPath = Path.Combine(attemptPath, "request.json");
    var resultRelative = attemptRelative + "/result.json";
    var request = new
    {
        schemaVersion = "1.0.0", requestId = "request-" + attemptId, operationId = "operation-" + attemptId, taskId, runId,
        stageId = "export-remnote", attemptId, workerId = "export.remnote", operation = "remnote.export", idempotencyKey = Sha256(Encoding.UTF8.GetBytes(attemptId)),
        attemptPath = attemptRelative, outputPath = attemptRelative + "/outputs", progressPath = attemptRelative + "/progress.ndjson", resultPath = resultRelative,
        cancelSignal = new { schemaVersion = "1.0.0", path = attemptRelative + "/cancel.json", gracePeriodMs = 2000 },
        inputRefs = new[] { input }, configSnapshot = new { }, payload = new { schemaVersion = "1.0.0", documentTitle, sink = "markdown-zip", target = new { rootRemId = "offline-root", rootTitle = "剪藏" } }
    };
    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, ContractJson.Options), new UTF8Encoding(false));
    var start = new ProcessStartInfo(exporter)
    {
        WorkingDirectory = Path.GetDirectoryName(exporter)!, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("run"); start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Exporter");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return new ExporterRun(process.ExitCode, await stdout, await stderr, Path.Combine(jobRoot, resultRelative.Replace('/', Path.DirectorySeparatorChar)));
}

static async Task<JsonDocument> ReadDescriptorAsync(string exporter)
{
    var start = new ProcessStartInfo(exporter)
    {
        WorkingDirectory = Path.GetDirectoryName(exporter)!, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("describe"); start.ArgumentList.Add("--json");
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Exporter descriptor audit");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Require(process.ExitCode == 0, "Exporter describe failed: " + await stderr);
    return JsonDocument.Parse(await stdout);
}

static async Task<byte[]> ReadZipMarkdownAsync(string jobRoot, JsonElement result)
    => (await ReadZipMarkdownEntryAsync(jobRoot, result)).Content;

static async Task<ZipMarkdown> ReadZipMarkdownEntryAsync(string jobRoot, JsonElement result)
{
    var output = result.GetProperty("outputs")[0];
    var manifestPath = Path.Combine(jobRoot, output.GetProperty("manifestPath").GetString()!.Replace('/', Path.DirectorySeparatorChar));
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
    var zipRelative = manifest.RootElement.GetProperty("files")[0].GetProperty("path").GetString()!;
    using var archive = new System.IO.Compression.ZipArchive(File.OpenRead(Path.Combine(jobRoot, zipRelative.Replace('/', Path.DirectorySeparatorChar))), System.IO.Compression.ZipArchiveMode.Read);
    var entry = archive.Entries.Single();
    await using var stream = entry.Open();
    using var memory = new MemoryStream();
    await stream.CopyToAsync(memory);
    return new ZipMarkdown(entry.FullName, memory.ToArray());
}

static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

internal sealed record Check(string Name, string Status, string? Detail);
internal sealed record ExporterRun(int ExitCode, string StandardOutput, string StandardError, string ResultPath);
internal sealed record ZipMarkdown(string Name, byte[] Content);
internal sealed record LanguageProjectionCounts(int WritingCards, int BothGroups);
internal sealed record LanguageWritingDetails(string Pronunciation, string Meaning);
