using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

var projectRoot = Path.GetFullPath(args.Length == 1 ? args[0] : throw new ArgumentException("Expected project root."));
var fakeWorker = "tests/FakeWorker/bin/Release/net8.0/RemNoteQuickClipper.FakeWorker.exe";
Assert(File.Exists(Path.Combine(projectRoot, fakeWorker.Replace('/', Path.DirectorySeparatorChar))), "Release FakeWorker is missing.");
var testRoot = Path.Combine(Path.GetTempPath(), "rqc-settings", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(testRoot);
try
{
    var configuration = DesktopIntentResolver.LoadConfiguration(Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json"));
    var registry = Registry(fakeWorker);
    var storeEvidence = await TestStoreAsync(testRoot, configuration);
    var routeEvidence = await TestRouteMatrixAsync(testRoot, configuration);
    var learningResolverEvidence = await TestLearningRemNoteResolutionAsync(projectRoot, testRoot, Registry(fakeWorker), configuration);
    var flowEvidence = await TestFullFlowAndImmutabilityAsync(projectRoot, testRoot, registry, configuration);
    var moduleV2Evidence = await TestModuleSettingsV2Async(projectRoot, testRoot, registry, configuration);
    var moduleV3Evidence = await TestModuleSettingsV3Async(projectRoot, testRoot, registry, configuration);
    var ankiProbeEvidence = await TestAnkiExplicitProbeAsync(projectRoot, testRoot, registry);
    var report = new
    {
        schemaVersion = "knowledge-clipper-settings-control-canary.v1",
        status = "passed",
        externalCalls = 0,
        physicalSpeakerInvocations = 0,
        storeEvidence,
        routeEvidence,
        learningResolverEvidence,
        flowEvidence,
        moduleV2Evidence,
        moduleV3Evidence,
        ankiProbeEvidence
    };
    var receiptPath = Environment.GetEnvironmentVariable("RQC_SETTINGS_CONTROL_RECEIPT");
    if (!string.IsNullOrWhiteSpace(receiptPath))
        await AtomicFile.WriteJsonOnceAsync(Path.GetFullPath(receiptPath), report);
    Console.WriteLine(JsonSerializer.Serialize(report, ContractJson.Options));
    return 0;
}
finally
{
    if (Environment.GetEnvironmentVariable("RQC_SETTINGS_KEEP_TEMP") == "1") Console.Error.WriteLine("settings-test-root=" + testRoot);
    else if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
}

static async Task<object> TestModuleSettingsV3Async(
    string projectRoot,
    string root,
    WorkerRegistry registry,
    DestinationNeutralResolverConfiguration configuration)
{
    const string taskId = "task-settings-modules-v3";
    const string revisionId = "content-settings-v11-canary";
    var jobsRoot = Path.Combine(root, "jobs");
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    var contentReference = await PublishContentConfigV11Async(jobRoot, "content-config-v11", revisionId);
    var job = await CreateLegacyJobAsync(projectRoot, jobsRoot, taskId, "read", false, "sentence", false, false, new[] { contentReference });
    var voices = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["zh-CN"] = "zh-CN-XiaoxiaoNeural", ["en-US"] = "en-US-AvaMultilingualNeural",
        ["es-ES"] = "es-ES-XimenaNeural", ["ja-JP"] = "ja-JP-NanamiNeural", ["ko-KR"] = "ko-KR-SunHiNeural"
    };
    var edgeTtsPath = Path.GetFullPath(Path.Combine(root, "deployment", "edge-tts.exe"));
    var ffplayPath = Path.GetFullPath(Path.Combine(root, "deployment", "ffplay.exe"));
    var settings = new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v3", "module-v3", new Dictionary<string, WorkflowSettingsProfile>
    {
        ["module-v3"] = new("Module v3",
            new WorkflowRouteSettings("cards", Array.Empty<string>()),
            new CaptureSettings(false, "sentence", "ocr"),
            new SpeechSettings(voices, 17, -8, 23, null, "override", "en-US", new SpeechDeploymentSettings(edgeTtsPath, ffplayPath)),
            Content: new ContentSettingsReference(contentReference.ArtifactRef, contentReference.ExpectedSha256, revisionId))
    });
    var adapter = new TaskSettingsAdapter();
    var built = await adapter.BuildAndPublishAsync(job.JobRoot, taskId, "clip.read-aloud", settings, null, configuration, job.Inputs,
        DateTimeOffset.Parse("2026-08-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    await adapter.MigrateLegacyManifestV2Async(job.JobRoot, built);
    Assert(built.Snapshot.SchemaVersion == "knowledge-clipper-task-settings-snapshot.v3", "Module settings did not create the additive v3 task snapshot.");
    Assert(built.Snapshot.Modules.Content.ConfigSchemaVersion == "content-config-snapshot/1.1.0" &&
           built.Snapshot.Modules.Content.RuntimePolicy is { LanguagePracticeDefault: "both", ProviderModel: "deepseek-reasoner", ProviderMaxRetries: 4 },
        "Content 1.1 runtime policy did not freeze into the task settings snapshot.");
    Assert(built.Snapshot.Modules.Speech is { LanguageStrategy: "override", LanguageOverride: "en-US", AutoPlay: true } &&
           built.Snapshot.Modules.Speech.VoicesByLanguage["en-US"] == "en-US-AvaMultilingualNeural",
        "Speech language override did not freeze into the task settings snapshot.");
    Assert(await ResolveAndRunAsync(projectRoot, job, registry, configuration, "run-module-v3") == "succeeded", "Module v3 settings flow failed.");
    var plan = ReadPlan(job.JobRoot, "run-module-v3");
    var contentStage = Stage(plan, "content.understand-and-card");
    var synthStage = Stage(plan, "speech-synthesize");
    var playbackStage = Stage(plan, "speech-playback");
    var language = synthStage.GetProperty("payload").GetProperty("language");
    Assert(language.GetProperty("strategy").GetString() == "override" && language.GetProperty("override").GetString() == "en-US", "Speech language override did not reach the resolved plan.");
    Assert(synthStage.GetProperty("payload").GetProperty("voiceSnapshot").GetProperty("voicesByLanguage").GetProperty("en-US").GetString() == "en-US-AvaMultilingualNeural", "Speech override voice was not selected from the frozen map.");
    Assert(synthStage.GetProperty("configSnapshot").GetProperty("edgeTtsPath").GetString() == edgeTtsPath &&
           !synthStage.GetProperty("configSnapshot").TryGetProperty("ffplayPath", out _), "Synthesis deployment config leaked playback settings.");
    Assert(playbackStage.GetProperty("configSnapshot").GetProperty("ffplayPath").GetString() == ffplayPath &&
           !playbackStage.GetProperty("configSnapshot").TryGetProperty("edgeTtsPath", out _), "Playback deployment config leaked synthesis settings.");
    Assert(contentStage.GetProperty("configSnapshot").GetProperty("contentSettingsRevisionId").GetString() == revisionId &&
           contentStage.GetProperty("payload").GetProperty("contentConfigPath").GetString()!.EndsWith("content-config.v1.1.json", StringComparison.Ordinal),
        "Content 1.1 artifact identity did not reach the resolved plan.");
    var requests = Requests(job.JobRoot, "run-module-v3");
    var synthRequest = Request(requests, "speech-synthesize");
    Assert(Same(synthStage.GetProperty("payload").GetProperty("language"), synthRequest.GetProperty("payload").GetProperty("language")) &&
           Same(synthStage.GetProperty("payload").GetProperty("voiceSnapshot"), synthRequest.GetProperty("payload").GetProperty("voiceSnapshot")) &&
           Same(synthStage.GetProperty("configSnapshot"), synthRequest.GetProperty("configSnapshot")) &&
           Same(playbackStage.GetProperty("configSnapshot"), Request(requests, "speech-playback").GetProperty("configSnapshot")),
        "Speech v3 settings did not reach WorkerRequest unchanged through the source-neutral input adapter.");
    var contentRequest = Request(requests, "content.understand-and-card");
    Assert(Same(contentStage.GetProperty("configSnapshot"), contentRequest.GetProperty("configSnapshot")) &&
           contentRequest.GetProperty("inputRefs").EnumerateArray().Any(item => item.GetProperty("artifactRef").GetString() == contentReference.ArtifactRef &&
               item.GetProperty("expectedSha256").GetString() == contentReference.ExpectedSha256),
        "Content 1.1 artifact did not reach WorkerRequest unchanged.");

    var frozenSnapshot = JsonSerializer.SerializeToUtf8Bytes(built.Snapshot, ContractJson.Options);
    var nextJob = await CreateLegacyJobAsync(projectRoot, jobsRoot, "task-settings-modules-v3-next", "read", false, "sentence", false);
    var changedSettings = settings with
    {
        Profiles = new Dictionary<string, WorkflowSettingsProfile>
        {
            ["module-v3"] = settings.Profiles["module-v3"] with
            {
                Speech = settings.Profiles["module-v3"].Speech! with { LanguageOverride = "es-ES" },
                Content = null
            }
        }
    };
    var nextBuilt = await adapter.BuildAndPublishAsync(nextJob.JobRoot, nextJob.TaskId, "clip.read-aloud", changedSettings, null, configuration, nextJob.Inputs, DateTimeOffset.UtcNow);
    Assert(nextBuilt.Snapshot.Modules.Speech?.LanguageOverride == "es-ES" &&
           frozenSnapshot.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(built.Snapshot, ContractJson.Options)),
        "A project settings change affected an already frozen task snapshot.");

    return new
    {
        status = "passed",
        snapshotSchemaVersion = built.Snapshot.SchemaVersion,
        contentConfigSchemaVersion = built.Snapshot.Modules.Content.ConfigSchemaVersion,
        contentRuntimePolicy = built.Snapshot.Modules.Content.RuntimePolicy,
        language = language.Clone(),
        selectedVoice = built.Snapshot.Modules.Speech?.VoicesByLanguage["en-US"],
        synthConfigSnapshot = synthStage.GetProperty("configSnapshot").Clone(),
        playbackConfigSnapshot = playbackStage.GetProperty("configSnapshot").Clone(),
        effectiveAt = "next_task",
        activeTaskFrozen = true,
        speakerPreview = "conditional-not-run"
    };
}

static async Task<object> TestAnkiExplicitProbeAsync(string projectRoot, string root, WorkerRegistry registry)
{
    const string taskId = "task-anki-explicit-probe";
    var jobRoot = Path.Combine(root, "jobs", taskId);
    Directory.CreateDirectory(jobRoot);
    var configBytes = await File.ReadAllBytesAsync(Path.Combine(projectRoot, "workers", "anki-exporter", "config", "anki-export.v1.example.json"));
    var config = await PublishArtifactAsync(jobRoot, "anki-config-probe", "anki-export-config", configBytes);
    var plan = ModuleSettingsActionResolver.ResolveAnkiProbe(taskId, config, registry);
    Assert(plan.Stages.Count == 1 && plan.Stages[0].Operation == "probe" && plan.Stages[0].OutputKinds.SequenceEqual(new[] { "anki-connection-status" }), "Anki probe plan is not one explicit read-only operation.");
    Assert(plan.Stages[0].ConfigSnapshot.GetProperty("ankiConfigArtifactId").GetString() == config.ArtifactRef &&
           plan.Stages[0].ConfigSnapshot.EnumerateObject().Count() == 1, "Anki probe configSnapshot copied config content instead of artifact identity.");
    var state = await new OrchestratorEngine().RunAsync(jobRoot, plan, registry, projectRoot, "run-anki-probe", CancellationToken.None);
    Assert(state == "succeeded", "Anki explicit probe FakeWorker flow failed.");
    var request = Request(Requests(jobRoot, "run-anki-probe"), "anki-probe");
    Assert(request.GetProperty("operation").GetString() == "probe" &&
           request.GetProperty("configSnapshot").EnumerateObject().Count() == 1 &&
           request.GetProperty("configSnapshot").GetProperty("ankiConfigArtifactId").GetString() == config.ArtifactRef &&
           request.GetProperty("inputRefs").EnumerateArray().Single().GetProperty("expectedSha256").GetString() == config.ExpectedSha256,
        "Anki probe artifact identity did not reach WorkerRequest unchanged.");
    var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-anki-probe"), "result.json", SearchOption.AllDirectories).Single())).RootElement;
    Assert(result.GetProperty("outputs").EnumerateArray().Single().GetProperty("kind").GetString() == "anki-connection-status", "Anki probe did not publish the admitted status output.");
    return new { status = "passed", invocation = "explicit_only", operation = "probe", effectClass = "provider-read-only", externalMutation = false, configArtifactIdentityOnly = true, outputKind = "anki-connection-status" };
}

static async Task<object> TestModuleSettingsV2Async(
    string projectRoot,
    string root,
    WorkerRegistry registry,
    DestinationNeutralResolverConfiguration configuration)
{
    const string taskId = "task-settings-modules-v2";
    const string revisionId = "content-settings-canary-v1";
    var jobsRoot = Path.Combine(root, "jobs");
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    var contentReference = await PublishContentConfigAsync(jobRoot, "content-config-canary", revisionId);
    var job = await CreateLegacyJobAsync(projectRoot, jobsRoot, taskId, "clip", false, "sentence", false, true, new[] { contentReference });
    var imageSettings = new ImageOcrSettings("image-ocr-settings.v1",
        new ImageOcrProviderSettings("pipeline", "en", false, true, 120, 3));
    var settings = SettingsV2("module-v2", "cards", Array.Empty<string>(), imageSettings,
        new ContentSettingsReference(contentReference.ArtifactRef, contentReference.ExpectedSha256, revisionId));
    var adapter = new TaskSettingsAdapter();
    var built = await adapter.BuildAndPublishAsync(job.JobRoot, taskId, "clip.card", settings, null, configuration, job.Inputs,
        DateTimeOffset.Parse("2026-08-28T07:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    Assert(built.Snapshot.SchemaVersion == "knowledge-clipper-task-settings-snapshot.v2", "Module settings did not create the additive v2 task snapshot.");
    await adapter.MigrateLegacyManifestV2Async(job.JobRoot, built);
    Assert(await ResolveAndRunAsync(projectRoot, job, registry, configuration, "run-module-v2") == "succeeded", "Module v2 settings flow failed.");
    var plan = ReadPlan(job.JobRoot, "run-module-v2");
    var imageStage = Stage(plan, "image-ocr");
    var imageProvider = imageStage.GetProperty("configSnapshot").GetProperty("providerSettings");
    Assert(imageProvider.GetProperty("modelVersion").GetString() == "pipeline" && imageProvider.GetProperty("language").GetString() == "en" &&
           imageProvider.GetProperty("enableTable").GetBoolean() == false && imageProvider.GetProperty("enableFormula").GetBoolean() &&
           imageProvider.GetProperty("timeoutSeconds").GetInt32() == 120 && imageProvider.GetProperty("pollIntervalSeconds").GetInt32() == 3,
        "Image providerSettings did not reach the resolved plan.");
    var contentStage = Stage(plan, "content.understand-and-card");
    Assert(contentStage.GetProperty("configSnapshot").GetProperty("contentSettingsRevisionId").GetString() == revisionId &&
           contentStage.GetProperty("payload").GetProperty("contentConfigPath").GetString()!.EndsWith("content-config.json", StringComparison.Ordinal),
        "Content config snapshot identity did not reach the resolved plan.");
    var requests = Requests(job.JobRoot, "run-module-v2");
    Assert(Same(imageStage.GetProperty("configSnapshot"), Request(requests, "image-ocr").GetProperty("configSnapshot")),
        "Image providerSettings did not reach WorkerRequest unchanged.");
    var contentRequest = Request(requests, "content.understand-and-card");
    Assert(Same(contentStage.GetProperty("configSnapshot"), contentRequest.GetProperty("configSnapshot")) &&
           contentRequest.GetProperty("payload").GetProperty("contentConfigPath").GetString() == contentStage.GetProperty("payload").GetProperty("contentConfigPath").GetString() &&
           contentRequest.GetProperty("inputRefs").EnumerateArray().Any(item => item.GetProperty("artifactRef").GetString() == contentReference.ArtifactRef),
        "Content settings did not reach WorkerRequest unchanged.");
    return new
    {
        status = "passed",
        snapshotSchemaVersion = built.Snapshot.SchemaVersion,
        imageProviderSettings = imageProvider.Clone(),
        contentSettingsRevisionId = revisionId,
        contentConfigArtifactRef = contentReference.ArtifactRef,
        planBound = true,
        workerRequestBound = true
    };
}

static async Task<object> TestStoreAsync(string root, DestinationNeutralResolverConfiguration configuration)
{
    var path = Path.Combine(root, "settings-store", "workflow-settings.v1.json");
    var store = new WorkflowSettingsStore();
    var first = Settings("stable", "cards", Array.Empty<string>(), rate: 5, autoPlay: true);
    Assert(await store.SaveAsync(path, first) is null, "First settings save unexpectedly created a backup.");
    var second = Settings("stable", "cards", Array.Empty<string>(), rate: 11, autoPlay: false);
    var backup = await store.SaveAsync(path, second);
    Assert(backup is not null && File.Exists(backup), "Second settings save did not preserve an automatic backup.");
    await File.WriteAllTextAsync(path, "{corrupt");
    var recovered = await store.LoadAsync(path);
    Assert(recovered.Source == "backup" && recovered.Document.Profiles["stable"].Speech?.RatePercent == 5, "Corrupt primary did not fail over to the last valid backup.");
    return new { status = "passed", backupCreated = true, corruptFallback = true, recoveredRatePercent = 5 };
}

static async Task<object> TestRouteMatrixAsync(string root, DestinationNeutralResolverConfiguration configuration)
{
    var adapter = new TaskSettingsAdapter();
    var frozenAt = DateTimeOffset.Parse("2026-08-27T16:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
    var evidence = new List<object>();
    foreach (var item in new[]
    {
        (Name: "none", Profile: "cards", Destinations: Array.Empty<string>(), Anki: false),
        (Name: "remnote-cards", Profile: "cards", Destinations: new[] { "remnote" }, Anki: false),
        (Name: "anki", Profile: "cards", Destinations: new[] { "anki" }, Anki: true),
        (Name: "dual", Profile: "cards", Destinations: new[] { "remnote", "anki" }, Anki: true),
        (Name: "remnote-learning", Profile: "learning", Destinations: new[] { "remnote" }, Anki: false)
    })
    {
        var jobRoot = Path.Combine(root, "routes", item.Name);
        Directory.CreateDirectory(jobRoot);
        var inputs = item.Anki ? new[] { await PublishArtifactAsync(jobRoot, "anki-config", "anki-export-config", Encoding.UTF8.GetBytes("{}")) } : Array.Empty<ArtifactReference>();
        var result = await adapter.BuildAndPublishAsync(jobRoot, "task-" + item.Name, "clip.card",
            Settings("matrix", item.Profile, item.Destinations, 0, true), null, configuration, inputs, frozenAt);
        var route = result.Snapshot.Modules.Remnote?.Route;
        evidence.Add(new
        {
            item.Name,
            processingProfile = result.Snapshot.Workflow.ProcessingProfile,
            destinations = result.Snapshot.Workflow.Destinations,
            route = route?.Clone(),
            ankiConfigArtifactRef = result.Snapshot.Modules.Anki?.ConfigArtifactRef
        });
        if (item.Name == "remnote-cards")
            Assert(route?.EnumerateObject().Count() == 1 && route?.GetProperty("kind").GetString() == "card-export", "Card RemNote route contains date fields.");
        if (item.Name == "remnote-learning")
            Assert(route?.GetProperty("localDate").GetString() == "2026-08-28" && route?.GetProperty("timeZone").GetString() == "Asia/Shanghai", "Learning RemNote route did not freeze the Shanghai date.");
    }
    var invalidRoot = Path.Combine(root, "routes", "invalid-learning-anki");
    Directory.CreateDirectory(invalidRoot);
    var invalidInput = await PublishArtifactAsync(invalidRoot, "anki-config", "anki-export-config", Encoding.UTF8.GetBytes("{}"));
    await ExpectCodeAsync("settings.route_unsupported", () => adapter.BuildAndPublishAsync(invalidRoot, "task-invalid", "clip.card",
        Settings("invalid", "learning", new[] { "anki" }, 0, true), null, configuration, new[] { invalidInput }, frozenAt));
    var questionRoot = Path.Combine(root, "routes", "question-remnote-export");
    Directory.CreateDirectory(questionRoot);
    var question = await adapter.BuildAndPublishAsync(questionRoot, "task-question-export", "clip.question",
        Settings("question-export", "cards", new[] { "remnote" }, 0, true), null, configuration, Array.Empty<ArtifactReference>(), frozenAt);
    Assert(question.Snapshot.Workflow.Destinations.SequenceEqual(new[] { "remnote" }) && question.Snapshot.Modules.Remnote?.Route.GetProperty("kind").GetString() == "card-export",
        "Question destinations were not frozen for deferred question.make-cards routing.");
    return new { status = "passed", routes = evidence, learningAnki = "conditional-unsupported", questionExport = "deferred-until-make-cards" };
}

static async Task<object> TestLearningRemNoteResolutionAsync(
    string projectRoot,
    string root,
    WorkerRegistry registry,
    DestinationNeutralResolverConfiguration configuration)
{
    const string taskId = "task-learning-remnote-resolver";
    var job = await CreateLegacyJobAsync(projectRoot, Path.Combine(root, "jobs"), taskId, "clip", false, "sentence", false,
        requestedOutputs: new[] { "learning-bundle" });
    var settings = new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v2", "learning", new Dictionary<string, WorkflowSettingsProfile>
    {
        ["learning"] = new("Learning",
            new WorkflowRouteSettings("learning", new[] { "remnote" }),
            new CaptureSettings(false, "sentence", "ocr"),
            Remnote: new RemNoteDestinationSettings("remnote-destination-settings.v1", true,
                new RemNoteTargetSettings("8zNzfX45XLCSKoPSJ", "KnowledgeClipper PhaseB Canary 20260828T081109Z"),
                new RemNoteAdvancedSettings("mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd")))
    });
    var adapter = new TaskSettingsAdapter();
    var built = await adapter.BuildAndPublishAsync(job.JobRoot, taskId, "clip.card", settings, null, configuration, job.Inputs,
        DateTimeOffset.Parse("2026-08-28T00:30:00Z", System.Globalization.CultureInfo.InvariantCulture));
    await adapter.MigrateLegacyManifestV2Async(job.JobRoot, built);
    Assert(await ResolveAndRunAsync(projectRoot, job, registry, configuration, "run-learning-remnote") == "succeeded", "Learning-only RemNote resolver flow failed.");
    var plan = ReadPlan(job.JobRoot, "run-learning-remnote");
    var export = Stage(plan, "export-remnote");
    var route = export.GetProperty("payload").GetProperty("route");
    Assert(route.GetProperty("kind").GetString() == "clip-daily-route" &&
           route.GetProperty("timeZone").GetString() == "Asia/Shanghai" &&
           route.GetProperty("localDate").GetString() == "2026-08-28" &&
           route.GetProperty("dateFormat").GetString() == "yyyy-MM-dd", "Learning RemNote route did not freeze the exact daily route.");
    Assert(!plan.GetProperty("stages").EnumerateArray().Any(item => item.GetProperty("stageId").GetString() == "anki-export"), "Learning RemNote plan unexpectedly contains Anki.");
    var request = Request(Requests(job.JobRoot, "run-learning-remnote"), "export-remnote");
    Assert(Same(export.GetProperty("payload"), request.GetProperty("payload")), "Learning RemNote payload did not reach WorkerRequest unchanged.");
    return new
    {
        status = "passed",
        requestedOutputs = new[] { "learning-bundle" },
        rootRemId = "8zNzfX45XLCSKoPSJ",
        route = route.Clone(),
        planBound = true,
        workerRequestBound = true,
        ankiStage = false
    };
}

static async Task<object> TestFullFlowAndImmutabilityAsync(
    string projectRoot,
    string root,
    WorkerRegistry registry,
    DestinationNeutralResolverConfiguration configuration)
{
    var settingsPath = Path.Combine(root, "full-flow", "workflow-settings.v1.json");
    var store = new WorkflowSettingsStore();
    var initial = Settings("active", "cards", new[] { "remnote", "anki" }, rate: 7, autoPlay: true,
        capture: new CaptureSettings(true, "paragraph", "ocr"));
    await store.SaveAsync(settingsPath, initial);
    var loaded = await store.LoadAsync(settingsPath);
    var job = await CreateLegacyJobAsync(projectRoot, Path.Combine(root, "jobs"), "task-settings-full", "clip", true, "paragraph", true);
    var adapter = new TaskSettingsAdapter();
    var taskOverride = new TaskSettingsOverride("knowledge-clipper-task-settings-override.v1", Speech: new SpeechSettings(RatePercent: 19));
    var built = await adapter.BuildAndPublishAsync(job.JobRoot, job.TaskId, "clip.card", loaded.Document, taskOverride, configuration, job.Inputs,
        DateTimeOffset.Parse("2026-08-27T16:30:00Z", System.Globalization.CultureInfo.InvariantCulture));
    await adapter.MigrateLegacyManifestV2Async(job.JobRoot, built);
    var firstRun = await ResolveAndRunAsync(projectRoot, job, registry, configuration, "run-first");
    Assert(firstRun == "succeeded", "Settings-bound full flow failed: " + firstRun);
    var firstPlan = ReadPlan(job.JobRoot, "run-first");
    var remnoteStage = Stage(firstPlan, "export-remnote");
    var remnoteRoute = remnoteStage.GetProperty("payload").GetProperty("route");
    Assert(remnoteStage.GetProperty("payload").GetProperty("schemaVersion").GetString() == "2.0.0" &&
           remnoteRoute.EnumerateObject().Count() == 1 && remnoteRoute.GetProperty("kind").GetString() == "card-export", "Resolved RemNote stage did not use additive v2 card routing.");
    var ankiStage = Stage(firstPlan, "anki-export");
    Assert(ankiStage.GetProperty("configSnapshot").GetProperty("ankiConfigArtifactId").GetString() == built.Snapshot.Modules.Anki?.ConfigArtifactRef, "Anki snapshot did not reach stage configSnapshot.");
    var firstRequests = Requests(job.JobRoot, "run-first");
    Assert(Same(remnoteStage.GetProperty("payload"), Request(firstRequests, "export-remnote").GetProperty("payload")), "RemNote stage payload did not reach WorkerRequest unchanged.");
    Assert(Same(ankiStage.GetProperty("configSnapshot"), Request(firstRequests, "anki-export").GetProperty("configSnapshot")), "Anki stage configSnapshot did not reach WorkerRequest unchanged.");
    var contentRequest = Request(firstRequests, "content.understand-and-card");
    var contentInputPath = contentRequest.GetProperty("payload").GetProperty("contentInputPath").GetString()!;
    var contentMaterialPath = Path.Combine(Path.GetDirectoryName(PathPolicy.ResolveJobRelative(job.JobRoot, contentInputPath))!, "material.md");
    var contentMaterial = await File.ReadAllTextAsync(contentMaterialPath);
    Assert(contentMaterial.Contains("paragraph prefix", StringComparison.Ordinal) && contentMaterial.Contains("paragraph suffix", StringComparison.Ordinal),
        "Capture context settings did not reach the content WorkerRequest input artifact.");

    var changed = Settings("active", "cards", Array.Empty<string>(), rate: -25, autoPlay: false,
        capture: new CaptureSettings(false, "sentence", "ocr"));
    await store.SaveAsync(settingsPath, changed);
    var secondRun = await ResolveAndRunAsync(projectRoot, job, registry, configuration, "run-replay");
    Assert(secondRun == "succeeded", "Immutable task replay failed after global settings changed.");
    var replayPlan = ReadPlan(job.JobRoot, "run-replay");
    Assert(Same(firstPlan.GetProperty("routing"), replayPlan.GetProperty("routing")) &&
           Same(remnoteStage.GetProperty("payload"), Stage(replayPlan, "export-remnote").GetProperty("payload")), "Active task was affected by a later settings change.");

    var speechJob = await CreateLegacyJobAsync(projectRoot, Path.Combine(root, "jobs"), "task-settings-new", "read", false, "sentence", false);
    var newSettings = await store.LoadAsync(settingsPath);
    var newOverride = new TaskSettingsOverride("knowledge-clipper-task-settings-override.v1", Speech: new SpeechSettings(RatePercent: 19));
    var newBuilt = await adapter.BuildAndPublishAsync(speechJob.JobRoot, speechJob.TaskId, "clip.read-aloud", newSettings.Document, newOverride, configuration, speechJob.Inputs, DateTimeOffset.UtcNow);
    await adapter.MigrateLegacyManifestV2Async(speechJob.JobRoot, newBuilt);
    Assert(await ResolveAndRunAsync(projectRoot, speechJob, registry, configuration, "run-new") == "succeeded", "New settings-bound task failed.");
    var newPlan = ReadPlan(speechJob.JobRoot, "run-new");
    Assert(!newPlan.GetProperty("stages").EnumerateArray().Any(item => item.GetProperty("stageId").GetString() == "speech-playback"), "autoPlay=false did not suppress the playback stage.");
    var speechStage = Stage(newPlan, "speech-synthesize");
    Assert(speechStage.GetProperty("payload").GetProperty("voiceSnapshot").GetProperty("ratePercent").GetInt32() == 19, "Per-task speech override did not take precedence in the new task plan.");
    var speechRequestPayload = Request(Requests(speechJob.JobRoot, "run-new"), "speech-synthesize").GetProperty("payload");
    Assert(Same(speechStage.GetProperty("payload").GetProperty("voiceSnapshot"), speechRequestPayload.GetProperty("voiceSnapshot")) &&
           speechRequestPayload.GetProperty("playbackPolicy").GetProperty("autoPlay").GetBoolean() == false,
        "Speech settings did not reach WorkerRequest.");

    return new
    {
        status = "passed",
        migration = "task.manifest.v1.json preserved; task.manifest.v2.json appended",
        precedence = new[] { "taskOverride", "activeProfile", "moduleDefault" },
        taskOverrideRatePercent = newBuilt.Snapshot.Modules.Speech?.RatePercent,
        stageConfigBound = true,
        stagePayloadBound = true,
        workerRequestBound = true,
        activeTaskFrozen = true,
        replayUsesOriginalSnapshot = true,
        newTaskUsesNewSnapshot = true,
        playbackSuppressedWithoutSpeaker = true
    };
}

static WorkflowSettingsDocument Settings(string id, string profile, IReadOnlyList<string> destinations, int rate, bool autoPlay, CaptureSettings? capture = null) =>
    new("knowledge-clipper-workflow-settings.v1", id, new Dictionary<string, WorkflowSettingsProfile>
    {
        [id] = new("Profile " + id,
            new WorkflowRouteSettings(profile, destinations),
            capture ?? new CaptureSettings(false, "sentence", "ocr"),
            new SpeechSettings(new Dictionary<string, string> { ["zh-CN"] = "zh-CN-XiaoxiaoNeural", ["en-US"] = "en-US-AriaNeural" }, rate, 0, 0, autoPlay),
            new RemNoteDestinationSettings("remnote-destination-settings.v1", true, new RemNoteTargetSettings("fake-clip-root", "剪藏"),
                new RemNoteAdvancedSettings("markdown-zip", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd")))
    });

static WorkflowSettingsDocument SettingsV2(
    string id,
    string profile,
    IReadOnlyList<string> destinations,
    ImageOcrSettings imageOcr,
    ContentSettingsReference content) =>
    new("knowledge-clipper-workflow-settings.v2", id, new Dictionary<string, WorkflowSettingsProfile>
    {
        [id] = new("Profile " + id,
            new WorkflowRouteSettings(profile, destinations),
            new CaptureSettings(false, "sentence", "ocr"),
            new SpeechSettings(new Dictionary<string, string> { ["zh-CN"] = "zh-CN-XiaoxiaoNeural", ["en-US"] = "en-US-AriaNeural" }, 0, 0, 0, false),
            new RemNoteDestinationSettings("remnote-destination-settings.v1", true, new RemNoteTargetSettings("fake-clip-root", "剪藏"),
                new RemNoteAdvancedSettings("markdown-zip", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd")),
            imageOcr,
            content)
    });

static async Task<Job> CreateLegacyJobAsync(
    string projectRoot,
    string jobsRoot,
    string taskId,
    string captureIntent,
    bool includeAnki,
    string granularity,
    bool contextEnabled,
    bool includeImage = false,
    IReadOnlyList<ArtifactReference>? extraInputs = null,
    IReadOnlyList<string>? requestedOutputs = null)
{
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(Path.Combine(jobRoot, "plans"));
    var text = Encoding.UTF8.GetBytes("settings flow evidence");
    var prefix = Encoding.UTF8.GetBytes("paragraph prefix");
    var suffix = Encoding.UTF8.GetBytes("paragraph suffix");
    var captureArtifacts = new List<object>
    {
        new { artifactRef = "capture.text.selection", relativePath = $"artifacts/capture-bundle/selection.txt", sha256 = Sha(text), bytes = text.LongLength, mediaType = "text/plain", encoding = "utf-8" }
    };
    var captureFiles = new List<(string Name, byte[] Bytes, string MediaType)> { ("selection.txt", text, "text/plain") };
    if (includeImage)
    {
        var image = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        captureArtifacts.Add(new { artifactRef = "capture.image.1", relativePath = "artifacts/capture-bundle/image.png", sha256 = Sha(image), bytes = image.LongLength, mediaType = "image/png", encoding = (string?)null });
        captureFiles.Add(("image.png", image, "image/png"));
    }
    if (contextEnabled)
    {
        captureArtifacts.Add(new { artifactRef = "capture.text.prefix", relativePath = $"artifacts/capture-bundle/prefix.txt", sha256 = Sha(prefix), bytes = prefix.LongLength, mediaType = "text/plain", encoding = "utf-8" });
        captureArtifacts.Add(new { artifactRef = "capture.text.suffix", relativePath = $"artifacts/capture-bundle/suffix.txt", sha256 = Sha(suffix), bytes = suffix.LongLength, mediaType = "text/plain", encoding = "utf-8" });
        captureFiles.Add(("prefix.txt", prefix, "text/plain"));
        captureFiles.Add(("suffix.txt", suffix, "text/plain"));
    }
    var capture = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = "capture-bundle.v1", captureId = "capture-" + taskId, createdAt = DateTimeOffset.UtcNow, intent = captureIntent, source = "selection",
        modeSnapshot = new { workflow = "card", contextEnabled, contextGranularity = granularity, contextApplied = contextEnabled, imageHandling = "ocr" },
        artifacts = captureArtifacts,
        selectedTextArtifactRef = "capture.text.selection",
        contextSegments = contextEnabled ? new { prefixArtifactRef = "capture.text.prefix", selectionArtifactRef = "capture.text.selection", suffixArtifactRef = "capture.text.suffix", captureMethod = "fixture" } : null,
        questionImages = includeImage ? new[] { "capture.image.1" } : Array.Empty<string>(), answerImages = Array.Empty<string>(), questionTurns = Array.Empty<object>()
    }, ContractJson.Options);
    var captureRef = await PublishArtifactAsync(jobRoot, "capture-bundle", "capture-bundle", capture, captureFiles, "capture-bundle.v1.json");
    var inputs = new List<ArtifactReference> { captureRef };
    inputs.AddRange(extraInputs ?? Array.Empty<ArtifactReference>());
    if (includeAnki)
    {
        var configBytes = await File.ReadAllBytesAsync(Path.Combine(projectRoot, "workers", "anki-exporter", "config", "anki-export.v1.example.json"));
        inputs.Add(await PublishArtifactAsync(jobRoot, "anki-config", "anki-export-config", configBytes));
    }
    var intentName = "desktop-intent.v1.json";
    var taskIntent = captureIntent == "read" ? "clip.read-aloud" : "clip.card";
    var outputs = requestedOutputs?.ToArray() ?? (taskIntent == "clip.read-aloud" ? new[] { "card-bundle", "audio.speech" } : new[] { "card-bundle" });
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "plans", intentName), new { schemaVersion = "desktop-intent.v1", taskId, intent = taskIntent, inputArtifactRef = captureRef.ArtifactRef, requestedOutputs = outputs });
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
    {
        schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "settings-test", version = "1.0.0" }, intent = taskIntent,
        executionProfile = includeAnki ? "cards-anki" : "cards-remnote",
        inputRefs = inputs.Select(ManifestRef).ToArray(), requestedOutputs = outputs, initialPlanRef = "plans/desktop-intent.v1.json", retentionClass = "ephemeral"
    });
    return new Job(taskId, jobRoot, inputs);
}

static async Task<ArtifactReference> PublishContentConfigAsync(string jobRoot, string id, string revisionId)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = "content-config-snapshot/1.0.0",
        settingsRevisionId = revisionId,
        workerCompatibility = new { workerId = "content.deepseek", minimumVersion = "0.6.0" },
        promptSet = new { id = "open-md-v2-copy", source = "user-copy", promptRootPath = "prompts", sha256 = new string('a', 64), files = Array.Empty<object>() },
        categoryRegistry = new { id = "category-registry/2.0.0", schemaVersion = "category-registry/2.0.0", packagePath = "config/categories.v2.json", sha256 = new string('b', 64), bytes = 1 },
        providerBoundary = new { provider = "deepseek", credentials = "process-injected-only" }
    }, ContractJson.Options);
    return await PublishArtifactAsync(jobRoot, id, "content-config-snapshot", payload, payloadName: "content-config.json");
}

static async Task<ArtifactReference> PublishContentConfigV11Async(string jobRoot, string id, string revisionId)
{
    var promptBytes = Encoding.UTF8.GetBytes("System prompt for source-neutral settings binding.\n");
    var categoryBytes = Encoding.UTF8.GetBytes("{\"schema_version\":\"category-registry/2.0.0\",\"categories\":[]}");
    var payload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = "content-config-snapshot/1.1.0",
        settingsRevisionId = revisionId,
        workerCompatibility = new { workerId = "content.deepseek", minimumVersion = "0.7.0" },
        promptSet = new
        {
            id = "open-md-v2-user-copy",
            source = "user-copy",
            promptRootPath = "prompts",
            sha256 = Sha(promptBytes),
            files = new[] { new { path = "shared/system.md", packagePath = "prompts/shared/system.md", sha256 = Sha(promptBytes), bytes = promptBytes.LongLength } }
        },
        categoryRegistry = new
        {
            id = "category-registry/2.0.0",
            schemaVersion = "category-registry/2.0.0",
            packagePath = "config/categories.v2.json",
            sha256 = Sha(categoryBytes),
            bytes = categoryBytes.LongLength
        },
        providerBoundary = new { provider = "deepseek", credentials = "process-injected-only" },
        runtimePolicy = new
        {
            profileId = "content-v11-canary",
            profileVersion = "1.1.0",
            languagePracticeDefault = "both",
            provider = new { model = "deepseek-reasoner", maxRetries = 4 }
        }
    }, ContractJson.Options);
    return await PublishArtifactAsync(jobRoot, id, "content-config-snapshot", payload,
        new[]
        {
            ("prompts/shared/system.md", promptBytes, "text/markdown"),
            ("config/categories.v2.json", categoryBytes, "application/json")
        }, "content-config.v1.1.json");
}

static async Task<ArtifactReference> PublishArtifactAsync(string jobRoot, string id, string kind, byte[] payload, IReadOnlyList<(string Name, byte[] Bytes, string MediaType)>? extras = null, string payloadName = "payload.json")
{
    var root = Path.Combine(jobRoot, "artifacts", id);
    Directory.CreateDirectory(root);
    var files = new List<object>();
    async Task Add(string name, byte[] bytes, string media)
    {
        var full = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes);
        files.Add(new { path = $"artifacts/{id}/{name}", sha256 = Sha(bytes), bytes = bytes.LongLength, mediaType = media });
    }
    await Add(payloadName, payload, "application/json");
    foreach (var extra in extras ?? Array.Empty<(string, byte[], string)>()) await Add(extra.Name, extra.Bytes, extra.MediaType);
    var manifestPath = Path.Combine(root, "artifact.json");
    await AtomicFile.WriteJsonOnceAsync(manifestPath, new
    {
        schemaVersion = "1.0.0", artifactId = id, kind, contractVersion = "1.0.0", mediaType = "application/json", files, createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId = "desktop-capture", workerId = "desktop.capture", workerVersion = "1.0.0", attemptId = "capture-1" }, inputRefs = Array.Empty<object>(), dataClass = "sensitive", metadata = new { entrypoint = $"artifacts/{id}/{payloadName}" }
    });
    return new ArtifactReference(id, kind, "1.0.0", $"artifacts/{id}/artifact.json", Sha(await File.ReadAllBytesAsync(manifestPath)));
}

static object ManifestRef(ArtifactReference item) => new { item.ArtifactRef, item.Kind, item.ContractVersion, expectedSha256 = item.ExpectedSha256 };
static async Task<string> ResolveAndRunAsync(string projectRoot, Job job, WorkerRegistry registry, DestinationNeutralResolverConfiguration configuration, string runId)
{
    var plan = await new DesktopIntentResolver().ResolveAsync(job.JobRoot, Path.Combine(job.JobRoot, "plans", "desktop-intent.v1.json"), registry, runId, configuration, CancellationToken.None);
    return await new OrchestratorEngine().RunAsync(job.JobRoot, plan, registry, projectRoot, runId, CancellationToken.None);
}
static JsonElement ReadPlan(string jobRoot, string runId) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(jobRoot, "plans", "resolved", runId, "pipeline.v2.json"))).RootElement.Clone();
static JsonElement Stage(JsonElement plan, string id) => plan.GetProperty("stages").EnumerateArray().Single(item => item.GetProperty("stageId").GetString() == id);
static JsonElement[] Requests(string jobRoot, string runId) => Directory.GetFiles(Path.Combine(jobRoot, "runs", runId), "request.json", SearchOption.AllDirectories).Select(path => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.Clone()).ToArray();
static JsonElement Request(JsonElement[] requests, string stageId) => requests.Single(item => item.GetProperty("stageId").GetString() == stageId);
static bool Same(JsonElement left, JsonElement right) => JsonNodeDeepEquals(left, right);
static bool JsonNodeDeepEquals(JsonElement left, JsonElement right) => JsonSerializer.Serialize(left, IpcJson.Options) == JsonSerializer.Serialize(right, IpcJson.Options);

static WorkerRegistry Registry(string fakeWorker)
{
    WorkerEntry Worker(string id, string[] stages, string[] inputs, string[] outputs, string sideEffect, string idempotency) =>
        new(id, ">=1.0.0 <3.0.0", new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", id }), stages, inputs, outputs, new[] { "learning", "cards" }, sideEffect, idempotency, true);
    return new WorkerRegistry("1.0.0", "settings-canary.1", new[] { "learning", "cards" }, new[]
    {
        Worker("image.ocr.mineru", new[] { "image-ocr" }, new[] { "capture-bundle" }, new[] { "image-ocr.markdown" }, "external", "reconcilable"),
        Worker("content.deepseek", new[] { "content.understand-and-card" }, new[] { "content-input" }, new[] { "learning-bundle", "card-bundle", "question.answer" }, "local", "idempotent"),
        Worker("speech.edge", new[] { "speech.synthesize", "speech.playback" }, new[] { "text.spoken", "audio.speech" }, new[] { "audio.speech" }, "local", "idempotent"),
        Worker("export.remnote", new[] { "export-remnote" }, new[] { "learning.bundle", "card.bundle" }, new[] { "remnote.write-receipt" }, "external", "reconcilable"),
        Worker("export.anki", new[] { "anki-export", "anki-probe" }, new[] { "card-bundle", "learning-bundle", "anki-export-config" }, new[] { "anki-export-evidence", "anki-connection-status" }, "external", "reconcilable")
    });
}

static async Task ExpectCodeAsync(string code, Func<Task> action)
{
    try { await action(); }
    catch (OrchestratorDispatchException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException("Expected dispatch code was not thrown: " + code);
}
static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
sealed record Job(string TaskId, string JobRoot, IReadOnlyList<ArtifactReference> Inputs);
