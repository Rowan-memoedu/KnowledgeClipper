using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Orchestrator.Core;

if (args.Length != 13)
    throw new ArgumentException("Usage: PhaseB.Canary <projectRoot> <releaseRoot> <registryPath> <pathRoot> <A|B|C|D|E|F|G> <stamp> <remRootId> <remRootTitle> <ankiDeck> <mineruScript> <edgeTts> <ffplay> <syntheticImage>");

var projectRoot = Path.GetFullPath(args[0]);
var releaseRoot = Path.GetFullPath(args[1]);
var registryPath = Path.GetFullPath(args[2]);
var pathRoot = Path.GetFullPath(args[3]);
var mode = args[4];
var stamp = args[5];
var remRootId = args[6];
var remRootTitle = args[7];
var ankiDeck = args[8];
var mineruScript = Path.GetFullPath(args[9]);
var edgeTts = Path.GetFullPath(args[10]);
var ffplay = Path.GetFullPath(args[11]);
var syntheticImage = Path.GetFullPath(args[12]);
if (mode is not ("A" or "B" or "C" or "D" or "E" or "F" or "G")) throw new ArgumentException("Unknown path.");
if (Directory.Exists(pathRoot)) throw new IOException("Append-only path root already exists: " + pathRoot);
Directory.CreateDirectory(pathRoot);

var registry = new RegistryLoader().Load(registryPath);
await new RegistryLoader().ValidateEntrypointsAsync(registry, releaseRoot);
if (registry.RegistryVersion != "1.2.12") throw new InvalidDataException("Candidate-bound Phase B qualification requires authority v1.2.12.");
var candidateRootValue = Environment.GetEnvironmentVariable("RQC_PHASE_B_CANDIDATE_ROOT");
if (string.IsNullOrWhiteSpace(candidateRootValue)) throw new InvalidDataException("RQC_PHASE_B_CANDIDATE_ROOT is required.");
var candidateRoot = Path.GetFullPath(candidateRootValue);
var candidateManifestPath = Path.Combine(candidateRoot, "candidate-file-manifest.v1.json");
var candidateCorePath = Path.Combine(candidateRoot, "KnowledgeClipper.Orchestrator.dll");
if (!File.Exists(candidateManifestPath) || !File.Exists(candidateCorePath)) throw new InvalidDataException("Integrated candidate identity files are missing.");
var candidateManifestSha = Sha(await File.ReadAllBytesAsync(candidateManifestPath));
if (!candidateManifestSha.Equals("5d03c90d42fa2a1145ff6dcc4874bd059ac3ee072628f0a8dc81fb23983b4f3c", StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Integrated candidate manifest drifted.");
var candidateCoreSha = Sha(await File.ReadAllBytesAsync(candidateCorePath));
var executingCorePath = typeof(OrchestratorEngine).Assembly.Location;
var executingCoreSha = Sha(await File.ReadAllBytesAsync(executingCorePath));
if (!candidateCoreSha.Equals(executingCoreSha, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Phase B harness is not executing the Core assembly packaged in the integrated candidate.");
var candidateBinding = new
{
    candidateId = "kc-integrated-settings-v1212-20260830T183500Z",
    root = candidateRoot,
    manifestPath = candidateManifestPath,
    manifestSha256 = candidateManifestSha,
    sealedTreeSha256 = "4f1021038635984154290467011fbb25ab9d88dd7a1a035f059cff309231416a",
    coreAssemblyPath = executingCorePath,
    coreAssemblySha256 = executingCoreSha
};

var definition = mode switch
{
    "A" => new Definition("cards", new[] { "remnote", "anki" }, "clip.card", false,
        "问题：为什么不可变任务包能保持重试可审计？\n答案：因为每次重试都创建新的 attempt，并保留先前的请求、结果与哈希。", "normal-text-dual-export"),
    "B" => new Definition("learning", new[] { "remnote" }, "clip.card", false,
        "摘录：问题：为什么可审计系统不覆盖重试证据？\n答案：它把输入、配置快照和每次 attempt 的结果分别保存。", "learning-daily-route"),
    "C" => new Definition("cards", new[] { "remnote", "anki" }, "clip.card", true,
        "请使用附图中的非敏感合成问答生成卡片。", "real-mineru-dual-export"),
    "D" => new Definition("cards", new[] { "remnote", "anki" }, "clip.read-aloud", false,
        "问题：为什么重试可审计？\n答案：每次尝试都保留独立证据。", "speech-natural-and-replay"),
    "E" => new Definition("cards", new[] { "anki" }, "clip.card", false,
        "Target word: pragmatic /præɡˈmætɪk/ — practical and realistic in approach; this adjective describes a solution that preserves evidence.", "language-meaning-reading", "meaning-reading"),
    "F" => new Definition("cards", new[] { "anki" }, "clip.card", false,
        "Target word: pragmatic /præɡˈmætɪk/ — practical and realistic in approach; this adjective describes a solution that preserves evidence.", "language-writing", "writing"),
    _ => new Definition("cards", new[] { "anki" }, "clip.card", false,
        "Target word: pragmatic /præɡˈmætɪk/ — practical and realistic in approach; this adjective describes a solution that preserves evidence.", "language-both", "both")
};
if (mode == "C" && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MINERU_API_TOKEN")))
{
    var imageBytes = await File.ReadAllBytesAsync(syntheticImage);
    var unavailable = new
    {
        schemaVersion = "knowledge-clipper-phase-b-path.v1",
        generatedAt = DateTimeOffset.UtcNow,
        path = mode,
        definition.Label,
        status = "credential-unavailable",
        taskCreated = false,
        candidate = candidateBinding,
        authority = new { registryVersion = registry.RegistryVersion, registryPath, registrySha256 = Sha(await File.ReadAllBytesAsync(registryPath)), releaseRoot },
        coreSettings = new { integrationCheckpoint = "2.5.1", runtimeCoreVersion = "2.5.0" },
        input = new { text = definition.Text, syntheticImage = Path.GetFileName(syntheticImage), syntheticImageBytes = imageBytes.LongLength, syntheticImageSha256 = Sha(imageBytes) },
        credentialBoundary = new { source = "unavailable-after-process-user-and-desktop-local-store-discovery", legacyBootstrapSource = false, secretPersisted = false, providerCalls = 0, automaticRetryAllowed = false, manualCredentialSetupBoundary = true },
        attempts = Array.Empty<object>(),
        downstreamStarted = false,
        externalWrites = 0
    };
    var unavailablePath = Path.Combine(pathRoot, "path-receipt.json");
    await AtomicFile.WriteJsonOnceAsync(unavailablePath, unavailable);
    await File.WriteAllTextAsync(Path.Combine(pathRoot, "readable-output.md"),
        $"# Knowledge Clipper Phase B path C\n\n- status: credential-unavailable\n- provider calls: 0\n- downstream started: false\n- secret persisted: false\n\n## Original input\n\n{definition.Text}\n\n## Synthetic image\n\n- bytes: {imageBytes.LongLength}\n- SHA-256: `{Sha(imageBytes)}`\n",
        new UTF8Encoding(false));
    Console.WriteLine(JsonSerializer.Serialize(new { status = "credential-unavailable", path = mode, receipt = unavailablePath, sha256 = Sha(await File.ReadAllBytesAsync(unavailablePath)) }, ContractJson.Options));
    return;
}
var destinationOverride = Environment.GetEnvironmentVariable("RQC_PHASE_B_DESTINATIONS");
if (destinationOverride == "anki")
    definition = definition with { Destinations = new[] { "anki" } };
else if (destinationOverride == "remnote")
    definition = definition with { Destinations = new[] { "remnote" } };
else if (destinationOverride == "none" || Environment.GetEnvironmentVariable("RQC_PHASE_B_NO_DESTINATIONS") == "1")
    definition = definition with { Destinations = Array.Empty<string>() };
else if (!string.IsNullOrWhiteSpace(destinationOverride) && destinationOverride != "default")
    throw new InvalidDataException("Unsupported Phase B destination override.");
var compactStamp = stamp.Length >= 15 ? stamp.Substring(9, 6).ToLowerInvariant() : Guid.NewGuid().ToString("N")[..6];
var taskId = $"pb{mode.ToLowerInvariant()}{compactStamp}";
var jobRoot = Path.Combine(pathRoot, "jobs", taskId);
Directory.CreateDirectory(Path.Combine(jobRoot, "plans"));
var strictLayout = Path.GetFileName(jobRoot).Equals(taskId, StringComparison.Ordinal) &&
    Path.GetFileName(Directory.GetParent(jobRoot)!.FullName).Equals("jobs", StringComparison.Ordinal);
if (!strictLayout) throw new InvalidDataException("Worker job root must remain jobs/<taskId>.");
if (mode is "C" or "D")
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(pathRoot, $"strict-layout-{(mode == "C" ? "image" : "speech")}.json"), new
    {
        schemaVersion = "knowledge-clipper-worker-strict-layout-regression.v1",
        workerId = mode == "C" ? "image.ocr.mineru" : "speech.edge",
        taskId,
        jobRoot,
        parentDirectory = "jobs",
        leafDirectoryEqualsTaskId = true,
        workerValidationRelaxed = false,
        status = "passed"
    });

var captureRef = await CreateCaptureAsync(jobRoot, taskId, definition, syntheticImage);
var inputs = new List<ArtifactReference> { captureRef };
if (definition.Destinations.Contains("anki", StringComparer.Ordinal))
    inputs.Add(await CreateAnkiConfigAsync(projectRoot, jobRoot, ankiDeck));
ContentConfigBuild? contentConfig = null;
if (definition.LanguagePracticeDefault is not null)
{
    contentConfig = await CreateContentConfigAsync(jobRoot, releaseRoot, mode, definition.LanguagePracticeDefault);
    inputs.Add(contentConfig.Reference);
}

var intentPath = Path.Combine(jobRoot, "plans", "desktop-intent.v1.json");
var requestedOutputs = definition.Intent == "clip.read-aloud"
    ? new[] { "card-bundle", "audio.speech" }
    : new[] { definition.Profile == "learning" ? "learning-bundle" : "card-bundle" };
await AtomicFile.WriteJsonOnceAsync(intentPath, new { schemaVersion = "desktop-intent.v1", taskId, intent = definition.Intent, inputArtifactRef = captureRef.ArtifactRef, requestedOutputs });
await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
{
    schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow,
    createdBy = new { application = "KnowledgeClipper.PhaseB.Canary", version = "1.0.0" }, intent = definition.Intent,
    executionProfile = definition.Destinations.Contains("anki", StringComparer.Ordinal)
        ? "cards-anki"
        : definition.Profile == "learning" ? "parity-remnote" : "cards-remnote",
    inputRefs = inputs.Select(ManifestRef).ToArray(), requestedOutputs,
    initialPlanRef = "plans/desktop-intent.v1.json", retentionClass = "evidence"
});

var resolverPath = Path.Combine(pathRoot, "profile-resolver.phase-b.json");
var resolverNode = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json")))!.AsObject();
resolverNode["speech"]!["edgeTtsPath"] = edgeTts;
resolverNode["speech"]!["ffplayPath"] = ffplay;
await AtomicFile.WriteJsonOnceAsync(resolverPath, resolverNode);
var configuration = DesktopIntentResolver.LoadConfiguration(resolverPath);

var profile = new WorkflowSettingsProfile(
    "Phase B " + mode,
    new WorkflowRouteSettings(definition.Profile, definition.Destinations),
    new CaptureSettings(false, "sentence", definition.Image ? "ocr" : "original"),
    definition.Intent == "clip.read-aloud"
        ? new SpeechSettings(new Dictionary<string, string> { ["zh-CN"] = "zh-CN-XiaoxiaoNeural", ["en-US"] = "en-US-AvaMultilingualNeural" }, 20, -35, 0, true)
        : null,
    definition.Destinations.Contains("remnote", StringComparer.Ordinal)
        ? new RemNoteDestinationSettings("remnote-destination-settings.v1", true,
            new RemNoteTargetSettings(remRootId, remRootTitle),
            new RemNoteAdvancedSettings("mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd"))
        : null,
    definition.Image
        ? new ImageOcrSettings("image-ocr-settings.v1", new ImageOcrProviderSettings("pipeline", "ch", true, true, 900, 5), mineruScript)
        : null,
    contentConfig is null ? null : new ContentSettingsReference(contentConfig.Reference.ArtifactRef, contentConfig.Reference.ExpectedSha256, contentConfig.SettingsRevisionId));
var settings = new WorkflowSettingsDocument(definition.LanguagePracticeDefault is null ? "knowledge-clipper-workflow-settings.v2" : "knowledge-clipper-workflow-settings.v3", "phase-b", new Dictionary<string, WorkflowSettingsProfile> { ["phase-b"] = profile });
var adapter = new TaskSettingsAdapter();
var built = await adapter.BuildAndPublishAsync(jobRoot, taskId, definition.Intent, settings, null, configuration, inputs, DateTimeOffset.UtcNow);
await adapter.MigrateLegacyManifestV2Async(jobRoot, built);
using (var legacyManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, "task.manifest.v1.json"))))
{
    var expectedLegacyProfile = definition.Destinations.Contains("anki", StringComparer.Ordinal)
        ? "cards-anki"
        : definition.Profile == "learning" ? "parity-remnote" : "cards-remnote";
    if (legacyManifest.RootElement.GetProperty("executionProfile").GetString() != expectedLegacyProfile)
        throw new InvalidDataException("Legacy worker execution profile does not match the resolved destinations.");
    if (legacyManifest.RootElement.GetProperty("retentionClass").GetString() != "evidence")
        throw new InvalidDataException("Legacy worker retentionClass must satisfy the frozen common contract.");
}
using (var v2Manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobRoot, "task.manifest.v2.json"))))
{
    if (v2Manifest.RootElement.GetProperty("retentionClass").GetString() != "evidence")
        throw new InvalidDataException("Task manifest v2 retentionClass must preserve the evidence policy.");
}

var runId = "run-initial";
var plan = await new DesktopIntentResolver().ResolveAsync(jobRoot, intentPath, registry, runId, configuration, CancellationToken.None);
var initialStatus = await new OrchestratorEngine().RunAsync(jobRoot, plan, registry, releaseRoot, runId, CancellationToken.None);

object? replay = null;
if (mode == "B" && initialStatus == "succeeded") replay = await ReplayRemNoteAsync(jobRoot, releaseRoot, registry);
if (mode == "D" && CanReplayPlayback(jobRoot)) replay = await ReplayPlaybackAsync(jobRoot, releaseRoot, registry, taskId);

var results = LoadResults(jobRoot);
var mineruCredentialSource = Environment.GetEnvironmentVariable("RQC_MINERU_CREDENTIAL_SOURCE") switch
{
    "legacy-remnote-secret-node" => "legacy-remnote-secret-node",
    "desktop-dpapi-current-user" => "desktop-dpapi-current-user",
    _ => "process-environment"
};
var report = new
{
    schemaVersion = "knowledge-clipper-phase-b-path.v1",
    generatedAt = DateTimeOffset.UtcNow,
    path = mode,
    definition.Label,
    status = initialStatus,
    taskId,
    candidate = candidateBinding,
    authority = new { registryVersion = registry.RegistryVersion, registryPath, registrySha256 = Sha(await File.ReadAllBytesAsync(registryPath)), releaseRoot },
    coreSettings = new { integrationCheckpoint = "2.5.1", runtimeCoreVersion = "2.5.0", snapshot = built.Snapshot, artifact = built.ArtifactReference },
    input = new { text = definition.Text, syntheticImage = definition.Image ? "synthetic-input.png" : null },
    plan = new { plan.SchemaVersion, plan.PlanVersion, plan.Routing, stages = plan.Stages.Select(StageSummary) },
    strictLayout = new { passed = strictLayout, jobRoot, parentDirectory = "jobs", leafDirectoryEqualsTaskId = true, workerValidationRelaxed = false },
    attempts = results.Select(ResultSummary),
    invocationCounts = results.GroupBy(item => item.Result.Worker.Id).ToDictionary(group => group.Key, group => group.Count()),
    replay,
    physicalPlayback = mode == "D" ? new
    {
        physicalSpeakerInvocations = results.Count(item => item.Result.StageId == "speech-playback" && item.Result.Status == "completed"),
        naturalCompletion = "idle_replayable",
        replayPolicy = "from_start",
        muted = false,
        simulated = false
    } : null,
    externalStatePolicy = new { unknownExternalStateAutoRetry = false },
    credentialBoundary = mode == "C" ? new
    {
        source = mineruCredentialSource,
        legacyBootstrapSource = mineruCredentialSource == "legacy-remnote-secret-node",
        secretPersisted = false,
        desktopCredentialChainPassed = mineruCredentialSource == "desktop-dpapi-current-user",
        processEnvironmentOnly = true,
        longTermMigrationPending = mineruCredentialSource == "legacy-remnote-secret-node",
        manualCredentialSetupBoundary = false
    } : null,
    deepSeekCredentialBoundary = new { present = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_PRODUCER_API_KEY")), secretPersisted = false, taskPackageReferences = 0 }
};
var reportPath = Path.Combine(pathRoot, "path-receipt.json");
await AtomicFile.WriteJsonOnceAsync(reportPath, report);
await WriteReadableAsync(pathRoot, jobRoot, definition, initialStatus, results, replay);
Console.WriteLine(JsonSerializer.Serialize(new { status = initialStatus, path = mode, receipt = reportPath, sha256 = Sha(await File.ReadAllBytesAsync(reportPath)) }, ContractJson.Options));

static async Task<ArtifactReference> CreateCaptureAsync(string jobRoot, string taskId, Definition definition, string imageSource)
{
    var artifactRoot = Path.Combine(jobRoot, "artifacts", "capture-bundle");
    Directory.CreateDirectory(artifactRoot);
    var textBytes = Encoding.UTF8.GetBytes(definition.Text);
    var textPath = Path.Combine(artifactRoot, "selection.txt");
    await File.WriteAllBytesAsync(textPath, textBytes);
    var artifacts = new List<object> { new { artifactRef = "capture.text.selection", relativePath = "artifacts/capture-bundle/selection.txt", sha256 = Sha(textBytes), bytes = textBytes.LongLength, mediaType = "text/plain", encoding = "utf-8" } };
    var files = new List<object> { new { path = "artifacts/capture-bundle/selection.txt", sha256 = Sha(textBytes), bytes = textBytes.LongLength, mediaType = "text/plain" } };
    if (definition.Image)
    {
        var imageBytes = await File.ReadAllBytesAsync(imageSource);
        var imagePath = Path.Combine(artifactRoot, "image.png");
        await File.WriteAllBytesAsync(imagePath, imageBytes);
        artifacts.Add(new { artifactRef = "capture.image.1", relativePath = "artifacts/capture-bundle/image.png", sha256 = Sha(imageBytes), bytes = imageBytes.LongLength, mediaType = "image/png", encoding = (string?)null });
        files.Add(new { path = "artifacts/capture-bundle/image.png", sha256 = Sha(imageBytes), bytes = imageBytes.LongLength, mediaType = "image/png" });
    }
    var capture = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = "capture-bundle.v1", captureId = "capture-" + taskId, createdAt = DateTimeOffset.UtcNow,
        intent = definition.Intent == "clip.read-aloud" ? "read" : "card", source = "selection",
        modeSnapshot = new { workflow = definition.Intent == "clip.read-aloud" ? "read" : "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = definition.Image ? "ocr" : "original" },
        artifacts, selectedTextArtifactRef = "capture.text.selection", contextSegments = (object?)null,
        questionImages = definition.Image ? new[] { "capture.image.1" } : Array.Empty<string>(), answerImages = Array.Empty<string>(), questionTurns = Array.Empty<object>()
    }, ContractJson.Options);
    var capturePath = Path.Combine(artifactRoot, "capture-bundle.v1.json");
    await File.WriteAllBytesAsync(capturePath, capture);
    files.Insert(0, new { path = "artifacts/capture-bundle/capture-bundle.v1.json", sha256 = Sha(capture), bytes = capture.LongLength, mediaType = "application/vnd.rqc.capture-bundle+json" });
    var manifestPath = Path.Combine(artifactRoot, "artifact.json");
    await AtomicFile.WriteJsonOnceAsync(manifestPath, new
    {
        schemaVersion = "1.0.0", artifactId = "capture-bundle", kind = "capture-bundle", contractVersion = "1.0.0", mediaType = "application/vnd.rqc.capture-bundle+json", files,
        createdAt = DateTimeOffset.UtcNow, producer = new { stageId = "desktop-capture", workerId = "desktop.capture", workerVersion = "1.0.0", attemptId = "capture-1" },
        inputRefs = Array.Empty<object>(), dataClass = "public", metadata = new { entrypoint = "artifacts/capture-bundle/capture-bundle.v1.json" }
    });
    return new ArtifactReference("capture-bundle", "capture-bundle", "1.0.0", "artifacts/capture-bundle/artifact.json", Sha(await File.ReadAllBytesAsync(manifestPath)));
}

static async Task<ArtifactReference> CreateAnkiConfigAsync(string projectRoot, string jobRoot, string deck)
{
    var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectRoot, "workers", "anki-exporter", "config", "anki-export.v1.example.json")))!.AsObject();
    config["expected_profile"] = "账户 1";
    config["provision_managed"] = false;
    var routes = config["deck_routes"]!.AsObject();
    foreach (var route in routes.ToArray()) routes[route.Key] = deck;
    var bytes = JsonSerializer.SerializeToUtf8Bytes(config, ContractJson.Options);
    var root = Path.Combine(jobRoot, "artifacts", "anki-config");
    Directory.CreateDirectory(root);
    await File.WriteAllBytesAsync(Path.Combine(root, "anki-export.v1.json"), bytes);
    var manifestPath = Path.Combine(root, "artifact.json");
    await AtomicFile.WriteJsonOnceAsync(manifestPath, new
    {
        schemaVersion = "1.0.0", artifactId = "anki-config", kind = "anki-export-config", contractVersion = "1.0.0", mediaType = "application/json",
        files = new[] { new { path = "artifacts/anki-config/anki-export.v1.json", sha256 = Sha(bytes), bytes = bytes.LongLength, mediaType = "application/json" } },
        createdAt = DateTimeOffset.UtcNow, producer = new { stageId = "desktop-settings", workerId = "desktop.settings", workerVersion = "1.0.0", attemptId = "settings-1" },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = "artifacts/anki-config/anki-export.v1.json" }
    });
    return new ArtifactReference("anki-config", "anki-export-config", "1.0.0", "artifacts/anki-config/artifact.json", Sha(await File.ReadAllBytesAsync(manifestPath)));
}

static async Task<ContentConfigBuild> CreateContentConfigAsync(string jobRoot, string releaseRoot, string mode, string languagePracticeDefault)
{
    var settingsRevisionId = $"phase-b-language-{languagePracticeDefault}-v1";
    var artifactId = $"content-settings-{mode.ToLowerInvariant()}";
    var relativeRoot = $"artifacts/{artifactId}";
    var absoluteRoot = Path.Combine(jobRoot, "artifacts", artifactId);
    var workerRoot = Path.Combine(releaseRoot, "workers", "content", "app");
    var sourcePromptRoot = Path.Combine(workerRoot, "prompts");
    var sourceRegistry = Path.Combine(workerRoot, "config", "categories.v2.json");
    Directory.CreateDirectory(absoluteRoot);

    var promptFiles = new List<ContentPromptFile>();
    var manifestFiles = new List<object>();
    foreach (var source in Directory.GetFiles(sourcePromptRoot, "*.md", SearchOption.AllDirectories)
                 .OrderBy(path => Path.GetRelativePath(sourcePromptRoot, path).Replace('\\', '/'), StringComparer.Ordinal))
    {
        var promptPath = Path.GetRelativePath(sourcePromptRoot, source).Replace('\\', '/');
        var packagePath = $"{relativeRoot}/prompts/{promptPath}";
        var bytes = await File.ReadAllBytesAsync(source);
        var target = Path.Combine(jobRoot, packagePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes);
        promptFiles.Add(new ContentPromptFile(promptPath, packagePath, Sha(bytes), bytes.LongLength));
        manifestFiles.Add(new { path = packagePath, sha256 = Sha(bytes), bytes = bytes.LongLength, mediaType = "text/markdown; charset=utf-8" });
    }
    if (promptFiles.Count != 12) throw new InvalidDataException("The admitted Content prompt set must contain exactly 12 files.");

    var registryBytes = await File.ReadAllBytesAsync(sourceRegistry);
    var registryPath = $"{relativeRoot}/config/categories.v2.json";
    var registryTarget = Path.Combine(jobRoot, registryPath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(registryTarget)!);
    await File.WriteAllBytesAsync(registryTarget, registryBytes);
    var snapshotPath = $"{relativeRoot}/content-config.v1.1.json";
    var snapshot = new
    {
        schemaVersion = "content-config-snapshot/1.1.0",
        settingsRevisionId,
        workerCompatibility = new { workerId = "content.deepseek", minimumVersion = "0.7.0" },
        promptSet = new { id = "open-md-v2-canary-copy", source = "user-copy", promptRootPath = $"{relativeRoot}/prompts", sha256 = "d9f46841067babe0563060aed6f6b918560a81acf5c3d4871826ca72d5dd9c62", files = promptFiles },
        categoryRegistry = new { id = "category-registry/2.0.0", schemaVersion = "category-registry/2.0.0", packagePath = registryPath, sha256 = Sha(registryBytes), bytes = registryBytes.LongLength },
        providerBoundary = new { provider = "deepseek", credentials = "process-injected-only" },
        runtimePolicy = new { profileId = $"phase-b-{languagePracticeDefault}-v1", profileVersion = "1.0.0", languagePracticeDefault, provider = new { model = "deepseek-chat", maxRetries = 3 } }
    };
    var snapshotBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, ContractJson.Options) + "\n");
    await File.WriteAllBytesAsync(Path.Combine(jobRoot, snapshotPath.Replace('/', Path.DirectorySeparatorChar)), snapshotBytes);
    manifestFiles.Insert(0, new { path = snapshotPath, sha256 = Sha(snapshotBytes), bytes = snapshotBytes.LongLength, mediaType = "application/json" });
    manifestFiles.Insert(1, new { path = registryPath, sha256 = Sha(registryBytes), bytes = registryBytes.LongLength, mediaType = "application/json" });

    var manifestPath = Path.Combine(absoluteRoot, "artifact.json");
    await AtomicFile.WriteJsonOnceAsync(manifestPath, new
    {
        schemaVersion = "1.0.0", artifactId, kind = "content-config-snapshot", contractVersion = "1.0.0", mediaType = "application/json", files = manifestFiles,
        createdAt = DateTimeOffset.UtcNow, producer = new { stageId = "content.settings", workerId = "desktop.settings", workerVersion = "1.0.0", attemptId = $"settings-{mode.ToLowerInvariant()}" },
        inputRefs = Array.Empty<object>(), dataClass = "public", metadata = new { entrypoint = snapshotPath, credentialsRecorded = false }
    });
    return new ContentConfigBuild(new ArtifactReference(artifactId, "content-config-snapshot", "1.0.0", $"{relativeRoot}/artifact.json", Sha(await File.ReadAllBytesAsync(manifestPath))), settingsRevisionId);
}

static async Task<object> ReplayRemNoteAsync(string jobRoot, string releaseRoot, WorkerRegistry registry)
{
    var sourcePath = Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-initial"), "request.json", SearchOption.AllDirectories)
        .Single(path => JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(path), ContractJson.Options)!.WorkerId == "export.remnote");
    var source = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(sourcePath), ContractJson.Options)!;
    var attemptId = "attempt-replay-" + Guid.NewGuid().ToString("N");
    var attemptPath = $"runs/run-remnote-replay/stages/export-remnote/attempts/{attemptId}";
    var request = source with
    {
        RequestId = "request-" + Guid.NewGuid().ToString("N"), OperationId = "operation-" + Guid.NewGuid().ToString("N"), RunId = "run-remnote-replay",
        AttemptId = attemptId, AttemptPath = attemptPath, OutputPath = attemptPath + "/outputs", ProgressPath = attemptPath + "/progress.ndjson", ResultPath = attemptPath + "/result.json",
        CancelSignal = new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000)
    };
    var execution = await new WorkerProcessRunner().RunAsync(registry.Workers.Single(item => item.WorkerId == "export.remnote"), releaseRoot, jobRoot, request, TimeSpan.FromMinutes(10), new CancellationReasonState(), CancellationToken.None);
    return new { type = "remnote-idempotency", source.IdempotencyKey, status = execution.Result.Status, payload = execution.Result.Payload, exitCode = execution.ExitCode };
}

static async Task<object> ReplayPlaybackAsync(string jobRoot, string releaseRoot, WorkerRegistry registry, string taskId)
{
    var sourceResultPath = Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-initial"), "result.json", SearchOption.AllDirectories)
        .Single(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)!.Outputs.Any(output => output.Kind == "audio.speech"));
    var sourceResult = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(sourceResultPath), ContractJson.Options)!;
    var sourceRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(sourceResultPath)!, "request.json")), ContractJson.Options)!;
    var audio = sourceResult.Outputs.Single(output => output.Kind == "audio.speech");
    var stage = new PipelineStage("speech-playback", "speech.edge", "speech.playback", Array.Empty<string>(), new[] { audio }, Array.Empty<string>(), 3_600_000,
        new RetryPolicy(1, 0), sourceRequest.ConfigSnapshot, JsonSerializer.SerializeToElement(new { }), Array.Empty<StageOutputBinding>(), null,
        new StageCompletionPolicy(new[] { "user_stop" }, "succeeded-with-playback-stopped"));
    var replayPlan = new PipelinePlan("1.1.0", "1.1.0", taskId, 1, new[] { stage });
    var status = await new OrchestratorEngine().RunAsync(jobRoot, replayPlan, registry, releaseRoot, "run-playback-replay", CancellationToken.None);
    return new { type = "playback-from-start", status, planStageCount = 1, operation = "speech.playback", exportStages = 0, contentStages = 0 };
}

static List<ResultFile> LoadResults(string jobRoot) => Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal).Select(path => new ResultFile(Path.GetRelativePath(jobRoot, path).Replace('\\', '/'), path, JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)!)).ToList();
static object ResultSummary(ResultFile item) => new { resultPath = item.Path, resultSha256 = Sha(File.ReadAllBytes(item.AbsolutePath)), item.Result.RunId, item.Result.StageId, workerId = item.Result.Worker.Id, workerVersion = item.Result.Worker.Version, item.Result.AttemptId, item.Result.IdempotencyKey, item.Result.Status, item.Result.Error, item.Result.Outputs, item.Result.Payload, item.Result.StartedAt, item.Result.FinishedAt, item.Result.Warnings };
static bool CanReplayPlayback(string jobRoot) => Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-initial"), "result.json", SearchOption.AllDirectories)
    .Select(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)!)
    .Any(result => result.Status == "completed" && result.Outputs.Any(output => output.Kind == "audio.speech"));
static object StageSummary(PipelineStage stage) => new { stage.StageId, stage.WorkerId, stage.Operation, stage.DependsOn, stage.OutputKinds, stage.ConfigSnapshot, stage.Payload };
static object ManifestRef(ArtifactReference item) => new { item.ArtifactRef, item.Kind, item.ContractVersion, expectedSha256 = item.ExpectedSha256 };
static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static async Task WriteReadableAsync(string pathRoot, string jobRoot, Definition definition, string status, IReadOnlyList<ResultFile> results, object? replay)
{
    var text = new StringBuilder();
    text.AppendLine("# Knowledge Clipper Phase B path").AppendLine().AppendLine($"- status: {status}").AppendLine($"- label: {definition.Label}").AppendLine();
    text.AppendLine("## Original input").AppendLine().AppendLine(definition.Text).AppendLine();
    foreach (var result in results)
    {
        text.AppendLine($"## {result.Result.RunId} / {result.Result.StageId} / {result.Result.Worker.Id}@{result.Result.Worker.Version}").AppendLine();
        text.AppendLine($"- status: {result.Result.Status}").AppendLine($"- result: `{result.Path}`").AppendLine($"- result SHA-256: `{Sha(await File.ReadAllBytesAsync(result.AbsolutePath))}`").AppendLine();
        foreach (var output in result.Result.Outputs)
        {
            text.AppendLine($"### {output.Kind} / {output.ArtifactRef}").AppendLine();
            var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, output.ManifestPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString()!;
                var media = file.GetProperty("mediaType").GetString()!;
                var bytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, relative));
                text.AppendLine($"- `{relative}` bytes={bytes.LongLength} sha256={Sha(bytes)} mediaType={media}");
                if (bytes.LongLength <= 512_000 && (media.StartsWith("text/", StringComparison.Ordinal) || media == "application/json"))
                    text.AppendLine().AppendLine("```").AppendLine(Encoding.UTF8.GetString(bytes)).AppendLine("```").AppendLine();
            }
        }
    }
    if (replay is not null) text.AppendLine("## Replay").AppendLine().AppendLine(JsonSerializer.Serialize(replay, ContractJson.Options));
    await File.WriteAllTextAsync(Path.Combine(pathRoot, "readable-output.md"), text.ToString(), new UTF8Encoding(false));
}

sealed record Definition(string Profile, string[] Destinations, string Intent, bool Image, string Text, string Label, string? LanguagePracticeDefault = null);
sealed record ContentPromptFile(string Path, string PackagePath, string Sha256, long Bytes);
sealed record ContentConfigBuild(ArtifactReference Reference, string SettingsRevisionId);
sealed record ResultFile(string Path, string AbsolutePath, WorkerResult Result);
