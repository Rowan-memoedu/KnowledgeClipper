using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KnowledgeClipper.Orchestrator.Core;

if (args.Length == 6 && args[1] == "--project-only")
    return await RunProjectionOnlyAsync(args[0], args[2], args[3], args[4], args[5]);
if (args.Length != 1) throw new ArgumentException("Expected only the project root. The immutable Content language authority is fixed by this acceptance version.");
var projectRoot = Path.GetFullPath(args[0]);
var stamp = DateTimeOffset.UtcNow.ToString("yyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];
var canaryRoot = Path.Combine(projectRoot, "workers", "anki-exporter", "canary", "ca", stamp);
var jobsRoot = Path.Combine(canaryRoot, "jobs");
Directory.CreateDirectory(jobsRoot);

var fixtureRoot = Path.Combine(projectRoot, "content-worker", "fixtures", "projection-v1");
var learningFixture = Path.Combine(fixtureRoot, "learning-bundle.schema-v1.json");
var cardFixture = Path.Combine(fixtureRoot, "card-bundle.schema-v1.json");
const string contentAuthorityCanaryId = "20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e";
const string expectedContentAuthorityReceiptSha256 = "6e7ddc2b71d7647bab24b89a28ee288cc11cf7050d012d4c49adae63a3cb9e63";
const string expectedLearningManifestSha256 = "f5a0ef3e4cb57db8db8bf40f1e92941c483a0a8b748e46021567f7567be2670b";
const string expectedLearningBundleSha256 = "80167822a932961696462fcf0cc009eef429d6152e9f0cb1b658b73c3ae2615b";
const string expectedCardManifestSha256 = "f8f36baf66bb68f1392982b907e4b62701dadd22edf58f15e5214543a3324a9e";
const string expectedCardBundleSha256 = "249ec0213c5bfc4e7253dc2fa5aeb6727ad52db1449f73d74bb47045bc4e4016";
var contentAuthorityRoot = Path.Combine(projectRoot, "content-worker", "canary", "jobs", contentAuthorityCanaryId);
var contentAuthorityReceiptPath = Path.Combine(contentAuthorityRoot, "canary-receipt.json");
RequireFile(contentAuthorityReceiptPath);
var contentAuthorityReceiptBytes = await File.ReadAllBytesAsync(contentAuthorityReceiptPath);
Require(Sha256(contentAuthorityReceiptBytes) == expectedContentAuthorityReceiptSha256,
    "Content authority receipt SHA-256 mismatch");
using var contentAuthorityReceiptDocument = JsonDocument.Parse(contentAuthorityReceiptBytes);
var contentAuthorityReceipt = contentAuthorityReceiptDocument.RootElement;
Require(contentAuthorityReceipt.GetProperty("canaryId").GetString() == contentAuthorityCanaryId &&
        contentAuthorityReceipt.GetProperty("status").GetString() == "completed",
    "Content authority receipt identity/status mismatch");
var contentAuthorityLearning = VerifyContentAuthorityArtifact(
    contentAuthorityRoot, contentAuthorityReceipt, "learning-bundle", expectedLearningManifestSha256,
    expectedLearningBundleSha256, "learning-bundle.schema-v1.json");
var contentAuthorityCard = VerifyContentAuthorityArtifact(
    contentAuthorityRoot, contentAuthorityReceipt, "card-bundle", expectedCardManifestSha256,
    expectedCardBundleSha256, "card-bundle.schema-v1.json");
var languageLearningFixture = contentAuthorityLearning.PayloadPath;
var languageCardFixture = contentAuthorityCard.PayloadPath;
var profilePath = Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json");
var configTemplate = Path.Combine(projectRoot, "workers", "anki-exporter", "config", "anki-export.v1.example.json");
var exporter = Path.Combine(projectRoot, "workers", "anki", "KnowledgeClipper.AnkiExporter.Worker.exe");
var coreSource = Path.Combine(projectRoot, "orchestrator", "Core", "StageInputResolver.cs");
foreach (var path in new[] { learningFixture, cardFixture, profilePath, configTemplate, exporter, coreSource }) RequireFile(path);
RequireFile(languageLearningFixture);
RequireFile(languageCardFixture);

var checks = new List<Check>();
var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);
AcceptanceJob? successfulJob = null;
ArtifactReference? successfulProjection = null;
string? successfulResultPath = null;
string? mediaQaRejectionCode = null;
string? assetRefRejectionCode = null;
string? languageWritingSamplesPath = null;
var learningBytes = await File.ReadAllBytesAsync(learningFixture);
var cardBytes = await File.ReadAllBytesAsync(cardFixture);
var languageLearningBytes = await File.ReadAllBytesAsync(languageLearningFixture);
var languageCardBytes = await File.ReadAllBytesAsync(languageCardFixture);
using var cardDocument = JsonDocument.Parse(cardBytes);
using var languageCardDocument = JsonDocument.Parse(languageCardBytes);
Require(cardDocument.RootElement.GetProperty("learning_bundle_sha256").GetString() == Sha256(learningBytes),
    "Real CardBundle does not pin the real LearningBundle payload");
Require(languageCardDocument.RootElement.GetProperty("learning_bundle_sha256").GetString() == Sha256(languageLearningBytes),
    "Authoritative language both Canary CardBundle does not pin its LearningBundle payload");

var nonMediaFixture = await WriteCardViewAsync(canaryRoot, cardBytes, "card-bundle.non-media.json", card =>
    card["template"]?.GetValue<string>() != "media_qa");
var assetOnlyFixture = await WriteCardViewAsync(canaryRoot, cardBytes, "card-bundle.asset-ref.json", card =>
{
    if (card["template"]?.GetValue<string>() == "media_qa") card["template"] = "basic_qa";
    return true;
});

using var profile = JsonDocument.Parse(await File.ReadAllBytesAsync(profilePath));
var mapping = profile.RootElement.GetProperty("destinationConfigurations").GetProperty("anki").GetProperty("projection").Clone();
using var configDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(configTemplate));
var languageWritingReadiness = InspectLanguageWritingReadiness(languageCardDocument.RootElement, mapping, configDocument.RootElement);

await CheckAsync("projection.real_nonmedia_to_exporter_validate_only", async () =>
{
    var job = await BuildJobAsync(Path.Combine(jobsRoot, "ok"), "rok", nonMediaFixture, learningFixture, configTemplate);
    var adapted = await ResolveCoreAdapterAsync(job.Root, job.RunId, job.Card, job.Learning, job.Config, mapping);
    using var source = JsonDocument.Parse(await File.ReadAllBytesAsync(nonMediaFixture));
    using var projected = await ReadPayloadAsync(job.Root, adapted);
    ValidateProjection(source.RootElement, projected.RootElement, mapping);

    var result = await RunExporterValidateOnlyAsync(exporter, job, adapted);
    Require(result.ExitCode == 0, "Exporter validate-only exited " + result.ExitCode + ": " + result.StandardError);
    using var resultJson = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ResultPath));
    var root = resultJson.RootElement;
    Require(root.GetProperty("status").GetString() == "completed", "Exporter rejected the real Core projection");
    Require(root.GetProperty("worker").GetProperty("id").GetString() == "export.anki" &&
            root.GetProperty("worker").GetProperty("version").GetString() == "1.1.4", "Exporter identity/version mismatch");
    Require(root.GetProperty("payload").GetProperty("validateOnly").GetBoolean(), "Exporter result is not validate-only");
    Require(root.GetProperty("outputs").GetArrayLength() == 1, "Successful validate-only did not publish one evidence artifact");
    successfulJob = job;
    successfulProjection = adapted;
    successfulResultPath = result.ResultPath;
    evidence["success"] = new
    {
        projectedManifest = adapted.ManifestPath,
        projectedManifestSha256 = adapted.ExpectedSha256.ToLowerInvariant(),
        exporterResult = Relative(result.ResultPath, projectRoot),
        exporterResultSha256 = Sha256(await File.ReadAllBytesAsync(result.ResultPath)),
        cards = projected.RootElement.GetProperty("cards").GetArrayLength(),
        contentHashesValidated = true,
        fieldRouteNoteTypeMappingValidated = true,
        derivedAncestryValidatedByExporter = true,
        validateOnly = true
    };
});

await CheckAsync("projection.real_media_qa_rejected_without_output", async () =>
{
    var job = await BuildJobAsync(Path.Combine(jobsRoot, "mq"), "rmq", cardFixture, learningFixture, configTemplate);
    var code = await ExpectCoreRejectedWithoutOutputAsync(job, mapping);
    Require(code == "binding.anki_media_deferred", "Unexpected media_qa rejection code: " + code);
    mediaQaRejectionCode = code;
    evidence["mediaQaRejection"] = new { code, adapterOutputs = 0, source = Relative(cardFixture, projectRoot) };
});

await CheckAsync("projection.real_asset_ref_rejected_without_output", async () =>
{
    var job = await BuildJobAsync(Path.Combine(jobsRoot, "ar"), "rar", assetOnlyFixture, learningFixture, configTemplate);
    var code = await ExpectCoreRejectedWithoutOutputAsync(job, mapping);
    Require(code == "binding.anki_media_deferred", "Unexpected asset_ref rejection code: " + code);
    assetRefRejectionCode = code;
    evidence["assetRefRejection"] = new { code, adapterOutputs = 0, source = Relative(assetOnlyFixture, projectRoot) };
});

if (languageWritingReadiness.Ready)
{
    await CheckAsync("projection.language_writing_both_order_and_semantics_validate_only", async () =>
    {
        var languageJob = await BuildJobAsync(
            Path.Combine(jobsRoot, "language-both"), "rlanguage", languageCardFixture!, languageLearningFixture!, configTemplate);
        var languageProjection = await ResolveCoreAdapterAsync(
            languageJob.Root, languageJob.RunId, languageJob.Card, languageJob.Learning, languageJob.Config, mapping);
        using var source = JsonDocument.Parse(languageCardBytes!);
        using var learning = JsonDocument.Parse(languageLearningBytes!);
        using var projected = await ReadPayloadAsync(languageJob.Root, languageProjection);
        ValidateProjection(source.RootElement, projected.RootElement, mapping);
        var languageResult = await RunExporterValidateOnlyAsync(exporter, languageJob, languageProjection);
        Require(languageResult.ExitCode == 0,
            "Exporter language both validate-only exited " + languageResult.ExitCode + ": " + languageResult.StandardError);
        ValidateLanguageWritingExtension(source.RootElement, learning.RootElement, projected.RootElement, mapping, configDocument.RootElement);
        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(languageResult.ResultPath));
        Require(result.RootElement.GetProperty("status").GetString() == "completed" &&
                result.RootElement.GetProperty("payload").GetProperty("validateOnly").GetBoolean(),
            "language_writing projection did not finish validate-only");
        languageWritingSamplesPath = await WriteLanguageWritingSamplesAsync(
            canaryRoot, projectRoot, languageCardFixture!, languageLearningFixture!, source.RootElement,
            projected.RootElement, configDocument.RootElement, languageResult.ResultPath);
        evidence["languageWritingExtension"] = new
        {
            status = "passed", cards = languageWritingReadiness.WritingCards,
            bothGroups = languageWritingReadiness.BothGroups, inputOrderPreserved = true,
            totalCards = source.RootElement.GetProperty("cards").GetArrayLength(),
            inputCardOrder = source.RootElement.GetProperty("cards").EnumerateArray()
                .Select(card => card.GetProperty("card_id").GetString()).ToArray(),
            choiceAndTokenLeakage = false, externalAnkiCalls = 0,
            sourceCardBundle = Relative(languageCardFixture!, projectRoot),
            sourceCardBundleSha256 = Sha256(languageCardBytes),
            sourceLearningBundle = Relative(languageLearningFixture!, projectRoot),
            sourceLearningBundleSha256 = Sha256(languageLearningBytes),
            projectedManifest = languageProjection.ManifestPath,
            exporterResult = Relative(languageResult.ResultPath, projectRoot),
            exporterResultSha256 = Sha256(await File.ReadAllBytesAsync(languageResult.ResultPath))
        };
    });
}
else
{
    evidence["languageWritingExtension"] = new
    {
        status = "pending_upstream_schema", missing = languageWritingReadiness.Missing,
        acceptancePrepared = true, externalAnkiCalls = 0
    };
}

string? readableSamplesPath = null;
if (checks.All(check => check.Status == "passed") && successfulJob is not null && successfulProjection is not null && successfulResultPath is not null)
{
    readableSamplesPath = await WriteReadableSamplesAsync(
        canaryRoot, projectRoot, nonMediaFixture, cardFixture, configTemplate, successfulJob, successfulProjection,
        successfulResultPath, mapping, mediaQaRejectionCode!, assetRefRejectionCode!);
}

var suiteStatus = checks.Any(check => check.Status == "failed")
    ? "failed"
    : languageWritingReadiness.Ready ? "passed" : "pending_upstream_schema";
var sourceFixtures = new List<object>
{
    new { path = Relative(learningFixture, projectRoot), sha256 = Sha256(learningBytes), role = "real-learning-bundle" },
    new { path = Relative(cardFixture, projectRoot), sha256 = Sha256(cardBytes), role = "real-full-card-bundle" }
};
sourceFixtures.Add(new { path = Relative(languageLearningFixture, projectRoot), sha256 = Sha256(languageLearningBytes), role = "authoritative-language-both-learning-bundle" });
sourceFixtures.Add(new { path = Relative(languageCardFixture, projectRoot), sha256 = Sha256(languageCardBytes), role = "authoritative-language-both-card-bundle" });
sourceFixtures.Add(new { path = Relative(nonMediaFixture, projectRoot), sha256 = Sha256(await File.ReadAllBytesAsync(nonMediaFixture)), role = "generated-non-media-view" });
sourceFixtures.Add(new { path = Relative(assetOnlyFixture, projectRoot), sha256 = Sha256(await File.ReadAllBytesAsync(assetOnlyFixture)), role = "generated-asset-ref-policy-view" });
var receipt = new
{
    schemaVersion = "1.0.0",
    suite = "export.anki-content-to-anki.v1-consumer-acceptance",
    status = suiteStatus,
    generatedAt = DateTimeOffset.UtcNow,
    product = new { displayName = "零碎知识剪藏", asciiRoot = "KnowledgeClipper" },
    worker = new { workerId = "export.anki", workerVersion = "1.1.4", role = "peer-pluggable-terminal-exporter", path = Relative(exporter, projectRoot), sha256 = Sha256(await File.ReadAllBytesAsync(exporter)) },
    core = new { adapter = "content-to-anki.v1", source = Relative(coreSource, projectRoot), sha256 = Sha256(await File.ReadAllBytesAsync(coreSource)), profile = Relative(profilePath, projectRoot), profileSha256 = Sha256(await File.ReadAllBytesAsync(profilePath)) },
    contentAuthority = new
    {
        canaryId = contentAuthorityCanaryId,
        receipt = Relative(contentAuthorityReceiptPath, projectRoot),
        receiptSha256 = expectedContentAuthorityReceiptSha256,
        learningManifest = Relative(contentAuthorityLearning.ManifestPath, projectRoot),
        learningManifestSha256 = contentAuthorityLearning.ManifestSha256,
        learningBundleSha256 = contentAuthorityLearning.PayloadSha256,
        cardManifest = Relative(contentAuthorityCard.ManifestPath, projectRoot),
        cardManifestSha256 = contentAuthorityCard.ManifestSha256,
        cardBundleSha256 = contentAuthorityCard.PayloadSha256,
        verified = true
    },
    sourceFixtures,
    exporterBoundary = new { dependsOnOtherTerminalExporters = false, consumedProfileMember = "ankiProjection" },
    languageWritingExtension = new
    {
        status = languageWritingReadiness.Ready
            ? checks.Single(check => check.Name == "projection.language_writing_both_order_and_semantics_validate_only").Status
            : "pending_upstream_schema",
        acceptancePrepared = true,
        writingCards = languageWritingReadiness.WritingCards,
        bothGroups = languageWritingReadiness.BothGroups,
        missing = languageWritingReadiness.Missing,
        choiceOwnedByExporter = false,
        inputOrderRequired = true,
        externalAnki = "not contacted"
    },
    policy = new
    {
        success = "real fixture cards except the single deferred-media card; exact fields/routes/note types/content hashes; full derived ancestry; Exporter validate-only",
        mediaQa = "unmodified real full bundle must fail binding.anki_media_deferred before Core publishes an artifact",
        assetRef = "real media card routed through a text template must still fail binding.anki_media_deferred before Core publishes an artifact",
        externalAnki = "not contacted; request operation=validate and fixture endpoint=127.0.0.1:65534"
    },
    external_api_calls = 0,
    readableSamples = readableSamplesPath is null ? null : new
    {
        path = Relative(readableSamplesPath, projectRoot),
        sha256 = Sha256(await File.ReadAllBytesAsync(readableSamplesPath)),
        sampleCount = 4,
        generatedFromThisRun = true
    },
    languageWritingReadableSamples = languageWritingSamplesPath is null ? null : new
    {
        path = Relative(languageWritingSamplesPath, projectRoot),
        sha256 = Sha256(await File.ReadAllBytesAsync(languageWritingSamplesPath)),
        generatedFromAuthoritativeContentCanary = true
    },
    checks,
    evidence
};
var receiptPath = Path.Combine(canaryRoot, "receipt.json");
await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, ContractJson.Options), new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(new { receiptPath, receiptSha256 = Sha256(await File.ReadAllBytesAsync(receiptPath)), readableSamplesPath, receipt.status, checks }, ContractJson.Options));
return receipt.status == "failed" ? 1 : 0;

static async Task<int> RunProjectionOnlyAsync(
    string projectRootArgument,
    string cardBundlePathArgument,
    string learningBundlePathArgument,
    string configPathArgument,
    string jobRootArgument)
{
    var projectRoot = Path.GetFullPath(projectRootArgument);
    var cardBundlePath = Path.GetFullPath(cardBundlePathArgument);
    var learningBundlePath = Path.GetFullPath(learningBundlePathArgument);
    var configPath = Path.GetFullPath(configPathArgument);
    var jobRoot = Path.GetFullPath(jobRootArgument);
    foreach (var path in new[] { cardBundlePath, learningBundlePath, configPath }) RequireFile(path);
    Require(!Directory.Exists(jobRoot) && !File.Exists(jobRoot), "Projection-only job root must be new and immutable");

    var profilePath = Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json");
    RequireFile(profilePath);
    using var profile = JsonDocument.Parse(await File.ReadAllBytesAsync(profilePath));
    var mapping = profile.RootElement.GetProperty("destinationConfigurations").GetProperty("anki").GetProperty("projection").Clone();
    using var config = JsonDocument.Parse(await File.ReadAllBytesAsync(configPath));
    using var source = JsonDocument.Parse(await File.ReadAllBytesAsync(cardBundlePath));
    using var learning = JsonDocument.Parse(await File.ReadAllBytesAsync(learningBundlePath));
    Require(source.RootElement.GetProperty("learning_bundle_sha256").GetString() == Sha256(await File.ReadAllBytesAsync(learningBundlePath)),
        "Live Content CardBundle does not pin the supplied LearningBundle");

    var job = await BuildJobAsync(jobRoot, "rlive", cardBundlePath, learningBundlePath, configPath, useConfigVerbatim: true);
    var projected = await ResolveCoreAdapterAsync(job.Root, job.RunId, job.Card, job.Learning, job.Config, mapping);
    using var projectedPayload = await ReadPayloadAsync(job.Root, projected);
    ValidateProjection(source.RootElement, projectedPayload.RootElement, mapping);
    ValidateLanguageWritingExtension(source.RootElement, learning.RootElement, projectedPayload.RootElement, mapping, config.RootElement);
    Require(source.RootElement.GetProperty("cards").GetArrayLength() == 4, "Live destination Canary requires exactly four language cards");

    var projectionRecord = new
    {
        schemaVersion = "1.0.0",
        status = "projected",
        taskId = job.TaskId,
        runId = job.RunId,
        projected = RefView(projected),
        config = RefView(job.Config),
        source = new
        {
            cardBundlePath,
            cardBundleSha256 = Sha256(await File.ReadAllBytesAsync(cardBundlePath)),
            learningBundlePath,
            learningBundleSha256 = Sha256(await File.ReadAllBytesAsync(learningBundlePath))
        },
        core = new { adapter = "content-to-anki.v1", profilePath, profileSha256 = Sha256(await File.ReadAllBytesAsync(profilePath)) },
        cards = projectedPayload.RootElement.GetProperty("cards").GetArrayLength(),
        order = projectedPayload.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.GetProperty("card_id").GetString()).ToArray(),
        externalApiCalls = 0
    };
    var recordPath = Path.Combine(jobRoot, "projection-stage.json");
    await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(projectionRecord, ContractJson.Options), new UTF8Encoding(false));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "projected",
        recordPath,
        recordSha256 = Sha256(await File.ReadAllBytesAsync(recordPath)),
        cards = projectionRecord.cards,
        externalApiCalls = 0
    }, ContractJson.Options));
    return 0;
}

static object RefView(ArtifactReference value) => new
{
    value.ArtifactRef,
    value.Kind,
    value.ContractVersion,
    value.ManifestPath,
    value.ExpectedSha256
};

async Task CheckAsync(string name, Func<Task> action)
{
    try
    {
        await action();
        checks.Add(new Check(name, "passed", null));
    }
    catch (Exception exception)
    {
        var actual = Unwrap(exception);
        checks.Add(new Check(name, "failed", actual.GetType().Name + ": " + actual.Message));
    }
}

static Exception Unwrap(Exception exception) => exception is TargetInvocationException { InnerException: not null } target ? target.InnerException! : exception;

static LanguageWritingReadiness InspectLanguageWritingReadiness(JsonElement cardBundle, JsonElement mapping, JsonElement config)
{
    var cards = cardBundle.GetProperty("cards").EnumerateArray().ToArray();
    var writingCards = cards.Where(card => card.GetProperty("template").GetString() == "language_writing").ToArray();
    var writingGroups = writingCards.Select(card => card.GetProperty("variant_group_id").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    var bothGroups = writingGroups.Count(group => cards.Any(card =>
        card.GetProperty("variant_group_id").GetString() == group && card.GetProperty("template").GetString() == "language_context"));
    var missing = new List<string>();
    if (writingCards.Length == 0) missing.Add("authoritative Content CardBundle has no language_writing card");
    if (writingCards.Length > 0 && bothGroups == 0) missing.Add("authoritative Content CardBundle has no language_context + language_writing variant group");
    var mappings = mapping.GetProperty("templateMappings");
    string? noteTypeKey = null;
    if (!mappings.TryGetProperty("language_writing", out var writingMapping))
    {
        missing.Add("Core ankiProjection.templateMappings has no language_writing entry");
    }
    else if (writingMapping.GetProperty("policy").GetString() != "project" ||
             string.IsNullOrWhiteSpace(noteTypeKey = writingMapping.GetProperty("noteTypeKey").GetString()))
    {
        missing.Add("Core language_writing mapping is not a projectable independent note type");
    }
    if (noteTypeKey is not null && !config.GetProperty("note_types").TryGetProperty(noteTypeKey, out _))
        missing.Add("Anki config has no note type selected by Core language_writing mapping");
    return new LanguageWritingReadiness(missing.Count == 0, missing.ToArray(), writingCards.Length, bothGroups);
}

static void ValidateLanguageWritingExtension(
    JsonElement sourceBundle,
    JsonElement learningBundle,
    JsonElement projectedBundle,
    JsonElement mapping,
    JsonElement config)
{
    Require(!ContainsDecisionLeak(sourceBundle), "choice/token leaked into authoritative Content CardBundle");
    Require(!ContainsDecisionLeak(projectedBundle), "choice/token leaked into Core projected Anki CardBundle");
    var sourceCards = sourceBundle.GetProperty("cards").EnumerateArray().ToArray();
    var projectedCards = projectedBundle.GetProperty("cards").EnumerateArray()
        .ToDictionary(card => card.GetProperty("card_id").GetString()!, card => card.Clone(), StringComparer.Ordinal);
    var writingMapping = mapping.GetProperty("templateMappings").GetProperty("language_writing");
    var noteTypeKey = writingMapping.GetProperty("noteTypeKey").GetString()!;
    var frontField = writingMapping.GetProperty("frontField").GetString()!;
    var backField = writingMapping.GetProperty("backField").GetString()!;
    var noteType = config.GetProperty("note_types").GetProperty(noteTypeKey);
    Require(noteType.GetProperty("field_map").GetProperty(frontField).GetString() == "Front" &&
            noteType.GetProperty("field_map").GetProperty(backField).GetString() == "Back",
        "language_writing must use an explicit ordinary forward Front/Back field mapping");

    foreach (var writing in sourceCards.Where(card => card.GetProperty("template").GetString() == "language_writing"))
    {
        Require(!writing.TryGetProperty("choice", out _), "choice leaked into finalized CardBundle");
        var cardId = writing.GetProperty("card_id").GetString()!;
        var itemId = writing.GetProperty("item_id").GetString()!;
        var unitId = writing.GetProperty("unit_id").GetString()!;
        var groupId = writing.GetProperty("variant_group_id").GetString()!;
        var peers = sourceCards.Where(card => card.GetProperty("variant_group_id").GetString() == groupId).ToArray();
        Require(peers.Length == 2 &&
                peers[0].GetProperty("template").GetString() == "language_context" &&
                peers[1].GetProperty("template").GetString() == "language_writing" &&
                peers.Select(card => card.GetProperty("card_id").GetString()).Distinct(StringComparer.Ordinal).Count() == 2,
            "both must produce independent language_context then language_writing cards in source order");

        var item = learningBundle.GetProperty("items").EnumerateArray().Single(value => value.GetProperty("item_id").GetString() == itemId);
        var unit = item.GetProperty("units").EnumerateArray().Single(value => value.GetProperty("unit_id").GetString() == unitId);
        var targetTerm = unit.GetProperty("target_term").GetString()!;
        var frontBlocks = writing.GetProperty("front_blocks").EnumerateArray().ToArray();
        var backBlocks = writing.GetProperty("back_blocks").EnumerateArray().ToArray();
        Require(frontBlocks.Length == 1 && backBlocks.Length == 1 &&
                frontBlocks[0].TryGetProperty("text", out _) &&
                backBlocks[0].TryGetProperty("text", out _),
            "language_writing must contain one text front and one text back");
        var frontText = frontBlocks[0].GetProperty("text").GetString()!;
        var backText = backBlocks[0].GetProperty("text").GetString()!;
        Require(backText == targetTerm, "language_writing back must preserve target_term verbatim");
        var sourceSegmentIds = unit.GetProperty("source_segment_ids").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var sourceContainerText = string.Concat(item.GetProperty("source_segments").EnumerateArray()
            .Where(segment => sourceSegmentIds.Contains(segment.GetProperty("segment_id").GetString()!))
            .Select(segment => segment.GetProperty("text").GetString()));
        var originalSentence = SourceSentenceWithoutContainerLineEnding(sourceContainerText);
        Require(CountOrdinalOccurrences(originalSentence, targetTerm) == 1,
            "language_writing source sentence must contain target_term exactly once");
        Require(!frontText.Contains(targetTerm, StringComparison.Ordinal),
            "language_writing front must replace target_term rather than retain it");
        var annotations = Regex.Matches(frontText, @"（[^（）；]+；[^（）]+）", RegexOptions.CultureInvariant);
        Require(annotations.Count == 1,
            "language_writing front must contain exactly one full-width reading；meaning annotation");
        var expectedFront = ReplaceOnlyOrdinalOccurrence(originalSentence, targetTerm, annotations[0].Value);
        Require(frontText == expectedFront,
            "language_writing front must equal the original sentence with its sole target_term replaced by the annotation");

        Require(projectedCards.TryGetValue(cardId, out var projected), "Core projection dropped language_writing card");
        Require(projected.GetProperty("note_type_key").GetString() == noteTypeKey, "Core projected the wrong language_writing note type");
        var fields = projected.GetProperty("fields");
        Require(fields.GetProperty(frontField).GetString() == frontText && fields.GetProperty(backField).GetString() == targetTerm,
            "Core changed language_writing front/back text");
    }
}

static bool ContainsDecisionLeak(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals("choice", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                ContainsDecisionLeak(property.Value))
                return true;
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var child in element.EnumerateArray())
            if (ContainsDecisionLeak(child)) return true;
    }
    return false;
}

static int CountOrdinalOccurrences(string value, string needle)
{
    if (needle.Length == 0) return 0;
    var count = 0;
    for (var index = 0; (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0; index += needle.Length)
        count++;
    return count;
}

static string SourceSentenceWithoutContainerLineEnding(string value)
{
    var sentence = value.EndsWith("\r\n", StringComparison.Ordinal)
        ? value[..^2]
        : value.EndsWith('\r') || value.EndsWith('\n') ? value[..^1] : value;
    Require(!sentence.EndsWith('\r') && !sentence.EndsWith('\n'),
        "source contains multiple trailing container line endings");
    return sentence;
}

static string ReplaceOnlyOrdinalOccurrence(string value, string needle, string replacement)
{
    var index = value.IndexOf(needle, StringComparison.Ordinal);
    Require(index >= 0 && value.IndexOf(needle, index + needle.Length, StringComparison.Ordinal) < 0,
        "replacement requires exactly one target_term occurrence");
    return value[..index] + replacement + value[(index + needle.Length)..];
}

static async Task<string> WriteLanguageWritingSamplesAsync(
    string canaryRoot,
    string projectRoot,
    string cardBundlePath,
    string learningBundlePath,
    JsonElement sourceBundle,
    JsonElement projectedBundle,
    JsonElement config,
    string resultPath)
{
    var projected = projectedBundle.GetProperty("cards").EnumerateArray()
        .ToDictionary(card => card.GetProperty("card_id").GetString()!, card => card.Clone(), StringComparer.Ordinal);
    var text = new StringBuilder();
    text.AppendLine("# export.anki 真实 language_writing / both validate-only 样例").AppendLine();
    text.AppendLine("本文件由权威 Content both Canary、当前 Core `content-to-anki.v1` 派生产物和 planned EXE Result 自动生成。").AppendLine();
    text.AppendLine($"- Content CardBundle：`{Relative(cardBundlePath, projectRoot)}`");
    text.AppendLine($"- Content LearningBundle：`{Relative(learningBundlePath, projectRoot)}`");
    text.AppendLine("- choice/token：未进入 Content CardBundle、Core 派生产物或 Exporter 输入");
    text.AppendLine("- validate-only：`true`；AnkiConnect 调用：0").AppendLine();

    var cards = sourceBundle.GetProperty("cards").EnumerateArray().ToArray();
    foreach (var group in cards.GroupBy(card => card.GetProperty("variant_group_id").GetString()!, StringComparer.Ordinal))
    {
        text.AppendLine($"## both 组 `{group.Key}`").AppendLine();
        foreach (var source in group)
        {
            var cardId = source.GetProperty("card_id").GetString()!;
            var template = source.GetProperty("template").GetString()!;
            var front = source.GetProperty("front_blocks")[0].GetProperty("text").GetString()!;
            var back = source.GetProperty("back_blocks")[0].GetProperty("text").GetString()!;
            var target = projected[cardId];
            var noteTypeKey = target.GetProperty("note_type_key").GetString()!;
            var fieldMap = config.GetProperty("note_types").GetProperty(noteTypeKey).GetProperty("field_map");
            text.AppendLine($"### {template}").AppendLine();
            text.AppendLine($"- card_id：`{cardId}`");
            text.AppendLine($"- Content Front 输入：{ReadableLiteral(front)}");
            text.AppendLine($"- Content Back 输入：{ReadableLiteral(back)}");
            text.AppendLine($"- Deck route：`{target.GetProperty("deck_route_key").GetString()}`");
            text.AppendLine($"- Note type：`{noteTypeKey}`");
            foreach (var field in target.GetProperty("fields").EnumerateObject())
                text.AppendLine($"- 字段投影：`{field.Name}` → `{fieldMap.GetProperty(field.Name).GetString()}` → {ReadableLiteral(field.Value.GetString()!)}");
            text.AppendLine($"- Content hash：`{target.GetProperty("content_hash").GetString()}`").AppendLine();
        }
    }
    using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath));
    text.AppendLine("## Worker 最终结果").AppendLine();
    text.AppendLine($"- status：`{result.RootElement.GetProperty("status").GetString()}`");
    text.AppendLine($"- worker：`{result.RootElement.GetProperty("worker").GetProperty("id").GetString()}` " +
                    $"`{result.RootElement.GetProperty("worker").GetProperty("version").GetString()}`");
    text.AppendLine("- 外部 Anki 调用：0").AppendLine();
    var path = Path.Combine(canaryRoot, "language-writing-both-samples.md");
    await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false));
    return path;
}

static string ReadableLiteral(string value) => "\"" + value
    .Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal)
    .Replace("\r", "\\r", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

static async Task<string> WriteCardViewAsync(string root, byte[] source, string name, Func<JsonObject, bool> keep)
{
    var document = JsonNode.Parse(source)?.AsObject() ?? throw new InvalidDataException("Card fixture is not a JSON object");
    var cards = document["cards"]?.AsArray() ?? throw new InvalidDataException("Card fixture has no cards array");
    for (var index = cards.Count - 1; index >= 0; index--)
    {
        var card = cards[index]?.AsObject() ?? throw new InvalidDataException("Card fixture contains a non-object card");
        if (!keep(card)) cards.RemoveAt(index);
    }
    var directory = Path.Combine(root, "fixtures");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, name);
    await File.WriteAllTextAsync(path, document.ToJsonString(ContractJson.Options), new UTF8Encoding(false));
    return path;
}

static async Task<string> WriteReadableSamplesAsync(
    string canaryRoot,
    string projectRoot,
    string nonMediaFixture,
    string fullCardFixture,
    string configTemplate,
    AcceptanceJob job,
    ArtifactReference projection,
    string resultPath,
    JsonElement mapping,
    string mediaQaCode,
    string assetRefCode)
{
    using var source = JsonDocument.Parse(await File.ReadAllBytesAsync(nonMediaFixture));
    using var fullSource = JsonDocument.Parse(await File.ReadAllBytesAsync(fullCardFixture));
    using var projected = await ReadPayloadAsync(job.Root, projection);
    using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath));
    using var config = JsonDocument.Parse(await File.ReadAllBytesAsync(configTemplate));
    var sourceCards = source.RootElement.GetProperty("cards").EnumerateArray()
        .ToDictionary(card => card.GetProperty("card_id").GetString()!, card => card.Clone(), StringComparer.Ordinal);
    var projectedCards = projected.RootElement.GetProperty("cards").EnumerateArray()
        .ToDictionary(card => card.GetProperty("card_id").GetString()!, card => card.Clone(), StringComparer.Ordinal);
    var examples = new[]
    {
        (Title: "普通问答", Id: "card-qa-basic"),
        (Title: "中文语境词汇", Id: "card-zh-language-context"),
        (Title: "概念迁移", Id: "card-concept-transfer")
    };
    var text = new StringBuilder();
    text.AppendLine("# export.anki 1.1.4 自动生成可读样例").AppendLine();
    text.AppendLine("本文件由本轮模块级 consumer acceptance 从真实 projection-v1 输入、当前 Core 派生产物和 validate-only Worker Result 自动生成；不是手写样例。").AppendLine();
    text.AppendLine($"- Worker Result：`{result.RootElement.GetProperty("status").GetString()}`")
        .AppendLine($"- validate-only：`{result.RootElement.GetProperty("payload").GetProperty("validateOnly").GetBoolean().ToString().ToLowerInvariant()}`")
        .AppendLine("- 外部 Anki 调用：0")
        .AppendLine($"- 派生 CardBundle：`{projection.ManifestPath}`").AppendLine();

    var index = 0;
    foreach (var example in examples)
    {
        index++;
        if (!sourceCards.TryGetValue(example.Id, out var original) || !projectedCards.TryGetValue(example.Id, out var card))
            throw new InvalidDataException("Readable sample card is missing: " + example.Id);
        var template = original.GetProperty("template").GetString()!;
        var policy = mapping.GetProperty("templateMappings").GetProperty(template);
        var route = card.GetProperty("deck_route_key").GetString()!;
        var noteType = card.GetProperty("note_type_key").GetString()!;
        var frontField = policy.GetProperty("frontField").GetString()!;
        var backField = policy.GetProperty("backField").GetString()!;
        var fields = card.GetProperty("fields");
        var model = config.RootElement.GetProperty("note_types").GetProperty(noteType).GetProperty("model_name").GetString();
        var deck = config.RootElement.GetProperty("deck_routes").GetProperty(route).GetString();
        var frontTarget = config.RootElement.GetProperty("note_types").GetProperty(noteType).GetProperty("field_map").GetProperty(frontField).GetString();
        var backTarget = config.RootElement.GetProperty("note_types").GetProperty(noteType).GetProperty("field_map").GetProperty(backField).GetString();
        var frontBlock = original.GetProperty("front_blocks")[0];
        var backBlock = original.GetProperty("back_blocks")[0];
        var tags = card.GetProperty("tags").GetArrayLength() == 0
            ? "[]"
            : "[" + string.Join(", ", card.GetProperty("tags").EnumerateArray().Select(value => value.GetString())) + "]";
        text.AppendLine($"## 样例 {index}：{example.Title}").AppendLine()
            .AppendLine("### 原始文本/结构化输入").AppendLine()
            .AppendLine($"- `category_id/template`：`{original.GetProperty("category_id").GetString()}/{template}`")
            .AppendLine($"- 正面：`{frontBlock.GetProperty("type").GetString()} / {frontBlock.GetProperty("provenance").GetString()}` → “{frontBlock.GetProperty("text").GetString()}”")
            .AppendLine($"- 背面：`{backBlock.GetProperty("type").GetString()} / {backBlock.GetProperty("provenance").GetString()}` → “{backBlock.GetProperty("text").GetString()}”").AppendLine()
            .AppendLine("### 映射后的卡片").AppendLine()
            .AppendLine($"- 卡片类型：`{noteType}` → `{model}`")
            .AppendLine($"- Deck route：`{route}` → `{deck}`")
            .AppendLine($"- 正面：`{frontField}` → `{frontTarget}` → “{fields.GetProperty(frontField).GetString()}”")
            .AppendLine($"- 背面：`{backField}` → `{backTarget}` → “{fields.GetProperty(backField).GetString()}”")
            .AppendLine($"- 标签：`{tags}`；validate-only 不写入 `rqc_managed`")
            .AppendLine($"- 媒体：`media_refs={card.GetProperty("media_refs").GetRawText()}`")
            .AppendLine($"- Content hash：`{card.GetProperty("content_hash").GetString()}`").AppendLine()
            .AppendLine("### validate-only 最终结果").AppendLine()
            .AppendLine("- `export.anki` 1.1.4：`completed`")
            .AppendLine("- 字段、route、note type、content hash、derived ancestry：通过")
            .AppendLine("- Anki note/media 写入：0").AppendLine();
    }

    var media = fullSource.RootElement.GetProperty("cards").EnumerateArray()
        .Single(card => card.GetProperty("template").GetString() == "media_qa");
    var mediaFront = media.GetProperty("front_blocks")[0];
    var mediaBack = media.GetProperty("back_blocks")[0];
    text.AppendLine("## 样例 4：unsupported media").AppendLine()
        .AppendLine("### 原始文本/结构化输入").AppendLine()
        .AppendLine($"- `category_id/template`：`{media.GetProperty("category_id").GetString()}/media_qa`")
        .AppendLine($"- 正面：`asset_ref` → `{mediaFront.GetProperty("material_path").GetString()}`")
        .AppendLine($"- 背面：`asset_ref` → `{mediaBack.GetProperty("material_path").GetString()}`").AppendLine()
        .AppendLine("### 当前 policy 的处理").AppendLine()
        .AppendLine("- 卡片类型：`media_qa → reject-media-deferred`")
        .AppendLine("- 字段/标签：不生成")
        .AppendLine("- 媒体：不复制、不导入、不上传")
        .AppendLine($"- 原始 media_qa 拒绝：`{mediaQaCode}`，Core adapter Artifact 0")
        .AppendLine($"- 保留 asset_ref、改走文字 template 的策略变体：`{assetRefCode}`，Core adapter Artifact 0").AppendLine()
        .AppendLine("### validate-only 最终结果").AppendLine()
        .AppendLine("- Worker 未启动；Worker Result 不存在")
        .AppendLine("- 原因：Core 在派生 CardBundle 发布前显式拒绝 unsupported media")
        .AppendLine("- Anki note/media 写入：0").AppendLine()
        .AppendLine("## 真实边界").AppendLine()
        .AppendLine("这是 Anki 模块 consumer acceptance，不是桌面端全链 Canary；未访问真实 Anki、AnkiConnect profile 或同步状态。");

    var path = Path.Combine(canaryRoot, "readable-samples.md");
    await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false));
    return path;
}

static async Task<AcceptanceJob> BuildJobAsync(
    string root,
    string runId,
    string cardPath,
    string learningPath,
    string configTemplate,
    bool useConfigVerbatim = false)
{
    Directory.CreateDirectory(root);
    var source = await PublishInitialAsync(root, "source-input", "source.input", "source.txt", Encoding.UTF8.GetBytes("projection-v1 consumer acceptance"), "text/plain");

    var configNode = JsonNode.Parse(await File.ReadAllBytesAsync(configTemplate))?.AsObject() ?? throw new InvalidDataException("Anki config template is invalid");
    if (!useConfigVerbatim)
    {
        configNode["endpoint"] = "http://127.0.0.1:65534";
        configNode["expected_profile"] = "offline-never-used";
    }
    var config = await PublishInitialAsync(root, "anki-config", "anki-export-config", "anki-export.v1.json",
        Encoding.UTF8.GetBytes(configNode.ToJsonString(ContractJson.Options)), "application/json");

    var card = await PublishContentAsync(root, runId, "content-card", "card-bundle", cardPath, source);
    var learning = await PublishContentAsync(root, runId, "content-learning", "learning-bundle", learningPath, source);
    var taskId = "task-" + Path.GetFileName(root);
    Directory.CreateDirectory(Path.Combine(root, "plans"));
    await File.WriteAllTextAsync(Path.Combine(root, "plans", "initial-plan.json"), "{}\n", new UTF8Encoding(false));
    var task = new
    {
        schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.Parse("2026-08-25T15:00:00Z"),
        createdBy = new { application = "anki-consumer-acceptance", version = "1.0.0" }, intent = "clip.card", executionProfile = "cards-anki",
        inputRefs = new[] { TaskRef(source), TaskRef(config) }, requestedOutputs = new[] { "anki-export-evidence" },
        initialPlanRef = "plans/initial-plan.json", retentionClass = "evidence"
    };
    await File.WriteAllTextAsync(Path.Combine(root, "task.manifest.v1.json"), JsonSerializer.Serialize(task, ContractJson.Options), new UTF8Encoding(false));
    return new AcceptanceJob(root, runId, taskId, source, card, learning, config);
}

static object TaskRef(ArtifactReference value) => new { value.ArtifactRef, value.Kind, value.ContractVersion, value.ExpectedSha256 };

static async Task<ArtifactReference> PublishInitialAsync(string jobRoot, string artifactId, string kind, string fileName, byte[] payload, string mediaType)
{
    var directory = Path.Combine(jobRoot, "artifacts", artifactId);
    Directory.CreateDirectory(directory);
    var payloadPath = Path.Combine(directory, fileName);
    await File.WriteAllBytesAsync(payloadPath, payload);
    var payloadRelative = Relative(payloadPath, jobRoot);
    var manifestPath = Path.Combine(directory, "artifact.json");
    var manifest = new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType,
        files = new[] { new { path = payloadRelative, sha256 = Sha256(payload), bytes = payload.LongLength, mediaType } },
        createdAt = DateTimeOffset.Parse("2026-08-25T15:00:00Z"),
        producer = new { stageId = "fixture-build", workerId = "fixture-builder", workerVersion = "1.0.0", attemptId = "fixture-" + artifactId },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = payloadRelative }
    };
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ContractJson.Options), new UTF8Encoding(false));
    return new ArtifactReference(artifactId, kind, "1.0.0", Relative(manifestPath, jobRoot), Sha256(await File.ReadAllBytesAsync(manifestPath)));
}

static async Task<ArtifactReference> PublishContentAsync(string jobRoot, string runId, string artifactId, string kind, string fixturePath, ArtifactReference source)
{
    var directory = Path.Combine(jobRoot, "runs", runId, "stages", "content.understand-and-card", "attempts", "attempt-content", "outputs", artifactId, "v0001");
    Directory.CreateDirectory(directory);
    var payload = await File.ReadAllBytesAsync(fixturePath);
    var payloadPath = Path.Combine(directory, Path.GetFileName(fixturePath));
    await File.WriteAllBytesAsync(payloadPath, payload);
    var payloadRelative = Relative(payloadPath, jobRoot);
    var manifestPath = Path.Combine(directory, "artifact.json");
    var manifest = new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "application/json",
        files = new[] { new { path = payloadRelative, sha256 = Sha256(payload), bytes = payload.LongLength, mediaType = "application/json" } },
        createdAt = DateTimeOffset.Parse("2026-08-25T15:00:01Z"),
        producer = new { stageId = "content.understand-and-card", workerId = "content.deepseek", workerVersion = "0.2.0", attemptId = "attempt-content" },
        inputRefs = new[] { source }, dataClass = "personal", metadata = new { entrypoint = payloadRelative }
    };
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ContractJson.Options), new UTF8Encoding(false));
    return new ArtifactReference(artifactId, kind, "1.0.0", Relative(manifestPath, jobRoot), Sha256(await File.ReadAllBytesAsync(manifestPath)));
}

static async Task<ArtifactReference> ResolveCoreAdapterAsync(string jobRoot, string runId, ArtifactReference card, ArtifactReference learning, ArtifactReference config, JsonElement mapping)
{
    var result = new WorkerResult("1.0.0", "request-content", "operation-content", "task-" + Path.GetFileName(jobRoot), runId,
        "content.understand-and-card", "attempt-content", new WorkerIdentity("content.deepseek", "0.2.0"), "1.0.0", new string('1', 64),
        "completed", new[] { card, learning }, null, DateTimeOffset.Parse("2026-08-25T15:00:00Z"), DateTimeOffset.Parse("2026-08-25T15:00:01Z"), Array.Empty<string>());
    var completed = new Dictionary<string, WorkerResult>(StringComparer.Ordinal) { ["content.understand-and-card"] = result };
    var stage = new PipelineStage("anki-export", "export.anki", "export", new[] { "content.understand-and-card" }, new[] { config },
        new[] { "anki-export-evidence" }, 180000, new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { ankiConfigArtifactId = config.ArtifactRef }),
        JsonSerializer.SerializeToElement(new { }),
        new[] { new StageOutputBinding("content.understand-and-card", "card-bundle", true), new StageOutputBinding("content.understand-and-card", "learning-bundle", true) },
        new StageInputAdapter("content-to-anki.v1", "card-bundle", mapping));

    var resolverType = typeof(PipelineStage).Assembly.GetType("KnowledgeClipper.Orchestrator.Core.StageInputResolver", true)!;
    var resolver = Activator.CreateInstance(resolverType, true)!;
    var method = resolverType.GetMethod("ResolveAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(resolverType.FullName, "ResolveAsync");
    var invoked = method.Invoke(resolver, new object[] { jobRoot, runId, "task-" + Path.GetFileName(jobRoot), stage, completed, CancellationToken.None })
        ?? throw new InvalidOperationException("Core adapter returned null Task");
    var task = (Task)invoked;
    await task;
    var resolved = task.GetType().GetProperty("Result")!.GetValue(task)!;
    var refs = (IReadOnlyList<ArtifactReference>)resolved.GetType().GetProperty("InputRefs")!.GetValue(resolved)!;
    Require(refs.Count == 2 && refs.Count(value => value.Kind == "card-bundle") == 1 && refs.Count(value => value.Kind == "anki-export-config") == 1,
        "Core adapter did not return one derived CardBundle plus the initial Anki config");
    return refs.Single(value => value.Kind == "card-bundle");
}

static async Task<string> ExpectCoreRejectedWithoutOutputAsync(AcceptanceJob job, JsonElement mapping)
{
    string code;
    try
    {
        await ResolveCoreAdapterAsync(job.Root, job.RunId, job.Card, job.Learning, job.Config, mapping);
        throw new InvalidDataException("Core adapter accepted deferred media");
    }
    catch (Exception exception)
    {
        var actual = Unwrap(exception);
        if (actual is not OrchestratorDispatchException dispatch) throw;
        code = dispatch.Code;
    }
    var adapterRoot = Path.Combine(job.Root, "runs", job.RunId, "adapters", "anki-export");
    var manifests = Directory.Exists(adapterRoot) ? Directory.GetFiles(adapterRoot, "artifact.json", SearchOption.AllDirectories) : Array.Empty<string>();
    Require(manifests.Length == 0, "Rejected Core projection published an adapter artifact");
    return code;
}

static async Task<ExporterRun> RunExporterValidateOnlyAsync(string exporter, AcceptanceJob job, ArtifactReference projected)
{
    const string attemptId = "a1";
    var attemptRelative = $"runs/{job.RunId}/stages/anki-export/attempts/{attemptId}";
    var attemptPath = Path.Combine(job.Root, attemptRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(attemptPath);
    var requestPath = Path.Combine(attemptPath, "request.json");
    var request = new
    {
        schemaVersion = "1.0.0", requestId = "request-" + job.RunId, operationId = "operation-" + job.RunId, taskId = job.TaskId,
        runId = job.RunId, stageId = "anki-export", attemptId, workerId = "export.anki", operation = "validate",
        idempotencyKey = Sha256(Encoding.UTF8.GetBytes(projected.ExpectedSha256 + job.Config.ExpectedSha256)),
        attemptPath = attemptRelative, outputPath = attemptRelative + "/outputs", progressPath = attemptRelative + "/progress.ndjson", resultPath = attemptRelative + "/result.json",
        cancelSignal = new { schemaVersion = "1.0.0", path = attemptRelative + "/cancel.json", gracePeriodMs = 2000 },
        inputRefs = new[] { projected, job.Config }, configSnapshot = new { ankiConfigArtifactId = job.Config.ArtifactRef }, payload = new { validateOnly = true }
    };
    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, ContractJson.Options), new UTF8Encoding(false));
    var start = new ProcessStartInfo(exporter)
    {
        WorkingDirectory = Path.GetDirectoryName(exporter)!, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("run"); start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Anki Exporter");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    await File.WriteAllTextAsync(Path.Combine(attemptPath, "worker.stdout.ndjson"), stdout, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(attemptPath, "worker.stderr.log"), stderr, new UTF8Encoding(false));
    return new ExporterRun(process.ExitCode, stdout, stderr, Path.Combine(attemptPath, "result.json"));
}

static void ValidateProjection(JsonElement source, JsonElement target, JsonElement mapping)
{
    Require(target.GetProperty("bundle_kind").GetString() == "CardBundle" && target.GetProperty("schema_version").GetString() == "1.0", "Projected bundle identity mismatch");
    Require(target.GetProperty("bundle_id").GetString() == source.GetProperty("bundle_id").GetString(), "Projected bundle_id changed");
    Require(target.GetProperty("media").GetArrayLength() == 0, "Text-only projection published media");
    var sourceCardArray = source.GetProperty("cards").EnumerateArray().ToArray();
    var sourceCards = sourceCardArray.ToDictionary(value => value.GetProperty("card_id").GetString()!, value => value.Clone(), StringComparer.Ordinal);
    var targetCards = target.GetProperty("cards").EnumerateArray().ToArray();
    Require(targetCards.Length == sourceCards.Count, "Non-media projection card count mismatch");
    Require(sourceCardArray.Select(card => card.GetProperty("card_id").GetString())
            .SequenceEqual(targetCards.Select(card => card.GetProperty("card_id").GetString()), StringComparer.Ordinal),
        "Core adapter changed CardBundle input order");
    foreach (var card in targetCards)
    {
        var cardId = card.GetProperty("card_id").GetString()!;
        Require(sourceCards.TryGetValue(cardId, out var original), "Projected unknown card: " + cardId);
        var category = original.GetProperty("category_id").GetString()!;
        var template = original.GetProperty("template").GetString()!;
        var expectedRoute = mapping.GetProperty("categoryToDeckRoute").GetProperty(category).GetString();
        var templatePolicy = mapping.GetProperty("templateMappings").GetProperty(template);
        var expectedNoteType = templatePolicy.GetProperty("noteTypeKey").GetString();
        Require(card.GetProperty("deck_route_key").GetString() == expectedRoute, "Deck route mismatch for " + cardId);
        Require(card.GetProperty("note_type_key").GetString() == expectedNoteType, "Note type mismatch for " + cardId);
        Require(card.GetProperty("revision").GetInt32() == 1 && card.GetProperty("status").GetString() == "ready", "Revision/status mismatch for " + cardId);
        Require(card.GetProperty("media_refs").GetArrayLength() == 0, "Unexpected media refs for " + cardId);
        var fields = card.GetProperty("fields");
        Require(fields.EnumerateObject().Count() == 2, "Projected extra/missing fields for " + cardId);
        var frontField = templatePolicy.GetProperty("frontField").GetString()!;
        var backField = templatePolicy.GetProperty("backField").GetString()!;
        Require(fields.GetProperty(frontField).GetString() == original.GetProperty("front_blocks")[0].GetProperty("text").GetString(), "Front field changed for " + cardId);
        Require(fields.GetProperty(backField).GetString() == original.GetProperty("back_blocks")[0].GetProperty("text").GetString(), "Back field changed for " + cardId);
        Require(card.GetProperty("content_hash").GetString() == CanonicalContentHash(card), "content_hash is not worker-compatible for " + cardId);
    }
}

static string CanonicalContentHash(JsonElement card)
{
    var value = new
    {
        card_id = card.GetProperty("card_id").GetString(), revision = card.GetProperty("revision").GetInt32(),
        deck_route_key = card.GetProperty("deck_route_key").GetString(), note_type_key = card.GetProperty("note_type_key").GetString(),
        fields = card.GetProperty("fields").Deserialize<Dictionary<string, string>>()!,
        tags = card.GetProperty("tags").EnumerateArray().Select(item => item.GetString()!).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
        media_refs = Array.Empty<object>()
    };
    var element = JsonSerializer.SerializeToElement(value, ContractJson.Options);
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) WriteCanonical(writer, element);
    return Sha256(stream.ToArray());
}

static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
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

static async Task<JsonDocument> ReadPayloadAsync(string jobRoot, ArtifactReference reference)
{
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(jobRoot, reference.ManifestPath.Replace('/', Path.DirectorySeparatorChar))));
    var entrypoint = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    return JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(jobRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar))));
}

static void RequireFile(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException("Required acceptance input is missing", path);
}

static AuthorityArtifact VerifyContentAuthorityArtifact(
    string authorityRoot,
    JsonElement receipt,
    string kind,
    string expectedManifestSha256,
    string expectedPayloadSha256,
    string expectedPayloadFileName)
{
    var output = receipt.GetProperty("outputs").EnumerateArray()
        .Single(value => value.GetProperty("kind").GetString() == kind);
    Require(output.GetProperty("manifestSha256").GetString() == expectedManifestSha256,
        $"Content authority {kind} receipt manifest SHA-256 mismatch");
    var manifestPath = ResolveContainedAuthorityPath(authorityRoot, output.GetProperty("manifestPath").GetString()!);
    RequireFile(manifestPath);
    var manifestBytes = File.ReadAllBytes(manifestPath);
    Require(Sha256(manifestBytes) == expectedManifestSha256,
        $"Content authority {kind} manifest payload SHA-256 mismatch");
    using var manifestDocument = JsonDocument.Parse(manifestBytes);
    var manifest = manifestDocument.RootElement;
    Require(manifest.GetProperty("kind").GetString() == kind &&
            manifest.GetProperty("producer").GetProperty("workerId").GetString() == "content.deepseek",
        $"Content authority {kind} manifest identity/producer mismatch");
    var file = manifest.GetProperty("files").EnumerateArray().Single();
    Require(file.GetProperty("sha256").GetString() == expectedPayloadSha256,
        $"Content authority {kind} artifact payload SHA-256 mismatch");
    var payloadPath = ResolveContainedAuthorityPath(authorityRoot, file.GetProperty("path").GetString()!);
    Require(Path.GetFileName(payloadPath) == expectedPayloadFileName,
        $"Content authority {kind} payload filename mismatch");
    RequireFile(payloadPath);
    Require(Sha256(File.ReadAllBytes(payloadPath)) == expectedPayloadSha256,
        $"Content authority {kind} payload bytes SHA-256 mismatch");
    return new AuthorityArtifact(payloadPath, manifestPath, expectedManifestSha256, expectedPayloadSha256);
}

static string ResolveContainedAuthorityPath(string authorityRoot, string relativePath)
{
    Require(!Path.IsPathRooted(relativePath) && relativePath == relativePath.Replace('\\', '/'),
        "Content authority contains a non-canonical or absolute path");
    var normalizedRoot = Path.GetFullPath(authorityRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    var resolved = Path.GetFullPath(Path.Combine(authorityRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    Require(resolved.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase),
        "Content authority path escapes its immutable job root");
    return resolved;
}

static void Require(bool value, string message)
{
    if (!value) throw new InvalidDataException(message);
}

static string Relative(string path, string root) => Path.GetRelativePath(root, path).Replace('\\', '/');
static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

internal sealed record AcceptanceJob(string Root, string RunId, string TaskId, ArtifactReference Source, ArtifactReference Card, ArtifactReference Learning, ArtifactReference Config);
internal sealed record Check(string Name, string Status, string? Detail);
internal sealed record ExporterRun(int ExitCode, string StandardOutput, string StandardError, string ResultPath);
internal sealed record LanguageWritingReadiness(bool Ready, string[] Missing, int WritingCards, int BothGroups);
internal sealed record AuthorityArtifact(string PayloadPath, string ManifestPath, string ManifestSha256, string PayloadSha256);
