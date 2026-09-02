using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: <project-root> <remnote|anki|spoken> <job-root> <source> [config] <output-json>");
    return 2;
}

var projectRoot = Path.GetFullPath(args[0]);
var command = args[1];
var jobRoot = Path.GetFullPath(args[2]);
Directory.CreateDirectory(jobRoot);

try
{
    object result = command switch
    {
        "remnote" when args.Length == 5 => await ProjectRemNoteAsync(projectRoot, jobRoot, Path.GetFullPath(args[3])),
        "anki" when args.Length == 6 => await ProjectAnkiAsync(projectRoot, jobRoot, Path.GetFullPath(args[3]), Path.GetFullPath(args[4])),
        "spoken" when args.Length == 5 => await ProjectSpokenAsync(jobRoot, Path.GetFullPath(args[3])),
        _ => throw new ArgumentException("invalid command or argument count")
    };
    var output = Path.GetFullPath(args[^1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, ContractJson.Options), new UTF8Encoding(false));
    Console.WriteLine(output);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(Unwrap(exception));
    return 1;
}

static async Task<object> ProjectRemNoteAsync(string projectRoot, string jobRoot, string contentJobRoot)
{
    var sources = await ImportContentOutputsAsync(jobRoot, contentJobRoot);
    using var resolverConfiguration = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json")));
    var mapping = resolverConfiguration.RootElement.GetProperty("destinationConfigurations").GetProperty("remnote").GetProperty("projection").Clone();
    var runId = "run-remnote-projection";
    var stage = new PipelineStage(
        "export-remnote", "export.remnote", "remnote.export", new[] { "content.understand-and-card" }, Array.Empty<ArtifactReference>(),
        new[] { "remnote.write-receipt" }, 180000, new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }),
        JsonSerializer.SerializeToElement(new { }), sources.Select(source => new StageOutputBinding("content.understand-and-card", source.Kind, true)).ToArray(),
        new StageInputAdapter("content-to-remnote.v1", "card.bundle", mapping));
    var resolved = await ResolveAsync(jobRoot, runId, stage, sources);
    var projected = resolved.InputRefs.Single(reference => reference.Kind == "card.bundle");
    return await DescribeProjectionAsync(jobRoot, projected, resolved.Payload, sources);
}

static async Task<object> ProjectAnkiAsync(string projectRoot, string jobRoot, string contentJobRoot, string configPath)
{
    var contentSources = await ImportContentOutputsAsync(jobRoot, contentJobRoot);
    var config = await PublishJsonAsync(jobRoot, "anki-export-config", "anki-export-config", configPath, "quality.path-matrix", "quality.fixture");
    var sources = contentSources.Append(config).ToArray();
    using var resolverConfiguration = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json")));
    var mapping = resolverConfiguration.RootElement.GetProperty("destinationConfigurations").GetProperty("anki").GetProperty("projection").Clone();
    var runId = "run-anki-projection";
    var stage = new PipelineStage(
        "export-anki", "export.anki", "export", new[] { "content.understand-and-card" }, new[] { config },
        new[] { "anki-export-evidence" }, 180000, new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { ankiConfigArtifactId = config.ArtifactRef }),
        JsonSerializer.SerializeToElement(new { }),
        new[] { new StageOutputBinding("content.understand-and-card", "card-bundle", true), new StageOutputBinding("content.understand-and-card", "learning-bundle", true) },
        new StageInputAdapter("content-to-anki.v1", "card-bundle", mapping));
    var resolved = await ResolveAsync(jobRoot, runId, stage, sources);
    var projected = resolved.InputRefs.Single(reference => reference.Kind == "card-bundle");
    return await DescribeProjectionAsync(jobRoot, projected, resolved.Payload, sources);
}

static async Task<object> ProjectSpokenAsync(string jobRoot, string rawTextPath)
{
    var rawBytes = await File.ReadAllBytesAsync(rawTextPath);
    _ = new UTF8Encoding(false, true).GetString(rawBytes);
    const string rawRelative = "inputs/capture/raw-spoken.txt";
    var rawDestination = Path.Combine(jobRoot, rawRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(rawDestination)!);
    await File.WriteAllBytesAsync(rawDestination, rawBytes);
    var captureObject = new
    {
        schemaVersion = "capture-bundle.v1",
        bundleId = "path-matrix-spoken-capture",
        artifacts = new[] { new { artifactRef = "raw-spoken", relativePath = rawRelative, sha256 = Sha256(rawBytes), bytes = rawBytes.LongLength, mediaType = "text/plain" } },
        selectedTextArtifactRef = "raw-spoken",
        modeSnapshot = new { contextApplied = false, contextGranularity = "selection" }
    };
    var capturePath = Path.Combine(jobRoot, "inputs", "capture", "capture-bundle.json");
    await File.WriteAllTextAsync(capturePath, JsonSerializer.Serialize(captureObject, ContractJson.Options), new UTF8Encoding(false));
    var capture = await PublishJsonAsync(jobRoot, "capture-bundle", "capture-bundle", capturePath, "desktop.capture", "quality.fixture", "application/vnd.rqc.capture-bundle+json");
    var runId = "run-spoken-projection";
    var stage = new PipelineStage(
        "speech-synthesize", "speech.edge", "speech.synthesize", new[] { "desktop.capture" }, Array.Empty<ArtifactReference>(),
        new[] { "audio.speech" }, 180000, new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }),
        new[] { new StageOutputBinding("desktop.capture", "capture-bundle", true) }, new StageInputAdapter("text-spoken.v1"));
    var resolved = await ResolveAsync(jobRoot, runId, stage, new[] { capture });
    var projected = resolved.InputRefs.Single(reference => reference.Kind == "text.spoken");
    return await DescribeProjectionAsync(jobRoot, projected, resolved.Payload, new[] { capture });
}

static async Task<ArtifactReference[]> ImportContentOutputsAsync(string jobRoot, string contentJobRoot)
{
    var resultPath = Directory.GetFiles(contentJobRoot, "result.json", SearchOption.AllDirectories).Single();
    using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath));
    if (result.RootElement.GetProperty("status").GetString() != "completed") throw new InvalidDataException("Content result is not completed");
    var output = new List<ArtifactReference>();
    foreach (var item in result.RootElement.GetProperty("outputs").EnumerateArray())
    {
        var kind = item.GetProperty("kind").GetString()!;
        if (kind is not ("learning-bundle" or "card-bundle")) continue;
        var manifestPath = Path.Combine(contentJobRoot, item.GetProperty("manifestPath").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
        var metadata = manifest.RootElement.GetProperty("metadata");
        var entrypoint = metadata.TryGetProperty("entrypoint", out var declaredEntrypoint) && declaredEntrypoint.ValueKind == JsonValueKind.String
            ? declaredEntrypoint.GetString()!
            : manifest.RootElement.GetProperty("files").EnumerateArray().Single().GetProperty("path").GetString()!;
        var payloadPath = Path.Combine(contentJobRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar));
        output.Add(await PublishJsonAsync(jobRoot, "source-" + kind, kind, payloadPath, "content.understand-and-card", "content.deepseek"));
    }
    if (output.Count != 2) throw new InvalidDataException("Expected paired Content learning/card outputs");
    return output.ToArray();
}

static async Task<ArtifactReference> PublishJsonAsync(
    string jobRoot, string artifactId, string kind, string sourcePath, string stageId, string workerId, string mediaType = "application/json")
{
    var bytes = await File.ReadAllBytesAsync(sourcePath);
    var root = Path.Combine(jobRoot, "inputs", artifactId, "v0001");
    Directory.CreateDirectory(root);
    var payloadPath = Path.Combine(root, Path.GetFileName(sourcePath));
    await File.WriteAllBytesAsync(payloadPath, bytes);
    var payloadRelative = Relative(jobRoot, payloadPath);
    var manifestPath = Path.Combine(root, "artifact.json");
    var manifest = new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType,
        files = new[] { new { path = payloadRelative, sha256 = Sha256(bytes), bytes = bytes.LongLength, mediaType } },
        createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId, workerId, workerVersion = workerId == "content.deepseek" ? "0.5.0" : "1.0.0", attemptId = "source-import" },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = payloadRelative, sourceSha256 = Sha256(bytes) }
    };
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ContractJson.Options), new UTF8Encoding(false));
    return new ArtifactReference(artifactId, kind, "1.0.0", Relative(jobRoot, manifestPath), Sha256(await File.ReadAllBytesAsync(manifestPath)));
}

static async Task<(IReadOnlyList<ArtifactReference> InputRefs, JsonElement Payload)> ResolveAsync(
    string jobRoot, string runId, PipelineStage stage, IReadOnlyList<ArtifactReference> sourceRefs)
{
    var sourceResult = new WorkerResult(
        "1.0.0", "request-source", "operation-source", "task-path-matrix", runId, "content.understand-and-card", "attempt-source",
        new WorkerIdentity("content.deepseek", "0.5.0"), "1.0.0", new string('1', 64), "completed", sourceRefs, null,
        DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, Array.Empty<string>());
    var completed = new Dictionary<string, WorkerResult>(StringComparer.Ordinal)
    {
        ["content.understand-and-card"] = sourceResult,
        ["desktop.capture"] = sourceResult
    };
    var resolverType = typeof(PipelineStage).Assembly.GetType("KnowledgeClipper.Orchestrator.Core.StageInputResolver", true)!;
    var resolver = Activator.CreateInstance(resolverType, true)!;
    var method = resolverType.GetMethod("ResolveAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(resolverType.FullName, "ResolveAsync");
    var invoked = method.Invoke(resolver, new object[] { jobRoot, runId, "task-path-matrix", stage, completed, CancellationToken.None })
        ?? throw new InvalidOperationException("Core adapter returned null Task");
    var task = (Task)invoked;
    await task;
    var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
    return ((IReadOnlyList<ArtifactReference>)result.GetType().GetProperty("InputRefs")!.GetValue(result)!,
        ((JsonElement)result.GetType().GetProperty("Payload")!.GetValue(result)!));
}

static async Task<object> DescribeProjectionAsync(
    string jobRoot, ArtifactReference projected, JsonElement payload, IReadOnlyList<ArtifactReference> ancestry)
{
    var manifestPath = Path.Combine(jobRoot, projected.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
    var entrypoint = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    var payloadPath = Path.Combine(jobRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar));
    var bytes = await File.ReadAllBytesAsync(payloadPath);
    return new
    {
        status = "passed", projected, entrypoint, payloadSha256 = Sha256(bytes), payloadBytes = bytes.LongLength,
        requestPayload = payload, ancestry = ancestry.Select(item => new { item.ArtifactRef, item.Kind, item.ExpectedSha256 }).ToArray()
    };
}

static Exception Unwrap(Exception value)
{
    while (value is TargetInvocationException { InnerException: not null }) value = value.InnerException;
    return value;
}

static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
