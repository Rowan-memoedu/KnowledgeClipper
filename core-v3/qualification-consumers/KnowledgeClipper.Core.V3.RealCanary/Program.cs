using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;

const string ExpectedCheckpointSha256 = "ACACC82EE64CCD3011D252A4258BA6029E32000D210F120875B9AF7B80C768AC";
const string ExpectedPacketSha256 = "B441AB5C6E3A790809CF30BF5D8192E1248179FBCFFD126529E3686AE75BB9D9";
const string ExpectedInventorySha256 = "0C876D875075EF0F63196EE1D07A356E9239DB544C2530D18A750C37BF6BC470";
const string ExpectedQualificationSha256 = "8EE8150FD11955B78E179628A1F50844DF419714383FB6AFF72D9F81882A079F";

var arguments = ParseArguments(args);
var releaseRoot = RequiredPath(arguments, "--release-root", directory: true);
var configurationPath = RequiredPath(arguments, "--configuration", directory: false);
var receiptPath = Path.GetFullPath(Required(arguments, "--receipt"));
var authorizationId = Required(arguments, "--authorization-id");
var startedAt = DateTimeOffset.UtcNow;
var dataRoot = Path.Combine(Path.GetDirectoryName(receiptPath)!, "isolated-data");
Process? host = null;

try
{
    RequireHash(Path.Combine(releaseRoot, "checkpoint.json"), ExpectedCheckpointSha256);
    RequireHash(Path.Combine(releaseRoot, "typed-sdk-slice-packet.json"), ExpectedPacketSha256);
    RequireHash(Path.Combine(releaseRoot, "inventory.json"), ExpectedInventorySha256);
    RequireHash(Path.Combine(releaseRoot, "evidence", "qualification.receipt.json"), ExpectedQualificationSha256);
    if (Directory.Exists(dataRoot) && Directory.EnumerateFileSystemEntries(dataRoot).Any())
    {
        throw new InvalidOperationException("Canary data root is not create-once empty; refusing a retry.");
    }
    Directory.CreateDirectory(dataRoot);

    var repositoryRoot = Path.Combine(releaseRoot, "self-contained-repository");
    var hostDll = Path.Combine(releaseRoot, "artifacts", "net8.0", "KnowledgeClipper.Core.V3.Host.dll");
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var dotnet = ResolveDotnetSdk();
    var pipeName = "KnowledgeClipper.Core.V3.RealWriteCanary." + Guid.NewGuid().ToString("N");
    host = StartHost(dotnet, hostDll, repositoryRoot, binding, dataRoot, pipeName);
    await WaitReadyAsync(host, pipeName);

    var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(150)));
    var configurationJson = await File.ReadAllTextAsync(configurationPath);
    using (var configurationDocument = JsonDocument.Parse(configurationJson))
    {
        var root = configurationDocument.RootElement;
        Require(root.GetProperty("schema_version").GetString() == "anki-export-config.v1", "Anki config schema is not v1.");
        Require(root.GetProperty("provision_managed").ValueKind == JsonValueKind.False, "Canary cannot provision Anki resources.");
        Require(root.GetProperty("deck_routes").TryGetProperty("canary", out var canaryRoute) && canaryRoute.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(canaryRoute.GetString()), "Anki config has no isolated canary route.");
    }

    var runToken = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
    var configuration = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(
        "real-write-config-" + runToken,
        ArtifactPurpose.AnkiConfiguration,
        configurationJson,
        "sensitive"));

    var probeSubmission = await client.AnkiProbeAsync(new AnkiProbeRequest("real-write-preflight-probe-" + runToken, configuration, "canary"));
    var probe = await client.WaitOperationProjectionAsync(probeSubmission.OperationId);
    Require(probe.State == OperationProjectionState.Succeeded, $"Anki preflight probe failed: {probe.State}; {probe.Notice}");
    Require(probe.AnkiReadiness is { ReadyForRequestedAction: true, SelectedRouteReady: true }, "Anki canary route is not ready for the requested action.");

    var questionText = $"Core v3 isolated Canary {runToken}: What is 2 + 2? Answer in one short sentence.";
    var question = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(
        "real-write-question-" + runToken,
        ArtifactPurpose.Question,
        questionText));
    var initialSubmission = await client.QuestionInitialAsync(new QuestionInitialRequest("real-write-session-" + runToken, question, Array.Empty<TypedArtifactHandle>()));
    var initial = await client.WaitQuestionProjectionAsync(initialSubmission.OperationId);
    Require(initial.State == QuestionProjectionState.WaitingInput && initial.AnswerArtifact is not null, $"Real Content answer did not reach waiting input: {initial.State}; {initial.Notice}");

    var makeRequest = new QuestionMakeCardsRequest(
        "real-write-make-cards-" + runToken,
        initialSubmission.SessionId,
        initial.AnswerArtifact!,
        configuration,
        new AnkiWriteAuthorization(authorizationId, "isolated-canary-deck"));
    var makeSubmission = await client.QuestionMakeCardsAsync(makeRequest);
    var make = await client.WaitQuestionProjectionAsync(makeSubmission.OperationId);
    Require(make.State == QuestionProjectionState.Succeeded && make.CardBundleArtifact is not null && make.DestinationEvidenceArtifact is not null,
        $"Real Content-to-Anki operation did not succeed: {make.State}; {make.Notice}");
    using var destinationEvidence = JsonDocument.Parse((await client.ReadArtifactAsync(make.DestinationEvidenceArtifact!)).Content);
    Require(destinationEvidence.RootElement.GetProperty("exactReadback").GetBoolean(), "Core projection did not preserve exact Anki readback.");
    Require(destinationEvidence.RootElement.GetProperty("externalWriteCount").GetInt32() == 1, "Canary did not perform exactly one bounded external write.");

    var operationEvidence = ReadWorkerEvidence(dataRoot);
    Require(operationEvidence.MutatingActions.SequenceEqual(new[] { "addNote" }, StringComparer.Ordinal) || operationEvidence.MutatingActions.SequenceEqual(new[] { "addNotes" }, StringComparer.Ordinal),
        "Canary mutation boundary was not exactly one add operation.");
    Require(operationEvidence.Actions.Contains("findNotes") && operationEvidence.Actions.Contains("notesInfo") && operationEvidence.Actions.Contains("cardsInfo"),
        "Worker evidence lacks findNotes/notesInfo/cardsInfo exact readback.");
    Require(!operationEvidence.Actions.Any(action => action is "updateNoteFields" or "deleteNotes" or "changeDeck"), "Canary observed an update, delete, or deck-change mutation.");
    Require(operationEvidence.ItemStatus == "CREATED", $"Canary item was not newly created: {operationEvidence.ItemStatus}");

    var attemptsBeforeReplay = CountAttempts(dataRoot);
    var operationsHashBeforeReplay = Sha256(operationEvidence.OperationsPath);
    var itemsHashBeforeReplay = Sha256(operationEvidence.ItemsPath);
    var replaySubmission = await client.QuestionMakeCardsAsync(makeRequest);
    var replay = await client.WaitQuestionProjectionAsync(replaySubmission.OperationId);
    Require(replay.State == QuestionProjectionState.Succeeded && replay.DestinationEvidenceArtifact?.Token == make.DestinationEvidenceArtifact!.Token,
        "Exact command replay changed the terminal projection or evidence identity.");
    var attemptsAfterReplay = CountAttempts(dataRoot);
    Require(attemptsAfterReplay == attemptsBeforeReplay, "Exact command replay created a new worker attempt.");
    Require(Sha256(operationEvidence.OperationsPath) == operationsHashBeforeReplay && Sha256(operationEvidence.ItemsPath) == itemsHashBeforeReplay,
        "Exact command replay changed Anki evidence files.");

    var receipt = new
    {
        schema = "kc.core-v3-real-question-anki-canary-receipt/1",
        startedAt,
        completedAt = DateTimeOffset.UtcNow,
        status = "PASS",
        release = new { root = releaseRoot, checkpointSha256 = ExpectedCheckpointSha256, packetSha256 = ExpectedPacketSha256, inventorySha256 = ExpectedInventorySha256, qualificationSha256 = ExpectedQualificationSha256 },
        flow = new { questionInitial = "real-content", questionMakeCards = "real-content", destination = "real-anki", isolatedRoute = "canary" },
        anki = new { externalWriteCount = 1, operationEvidence.MutatingActions, operationEvidence.NoteId, operationEvidence.CardId, operationEvidence.ItemStatus, exactReadback = true, readbackActions = new[] { "findNotes", "notesInfo", "cardsInfo" } },
        replay = new { newAttempts = attemptsAfterReplay - attemptsBeforeReplay, newWrites = 0, evidenceFilesChanged = false },
        forbidden = new { update = false, delete = false, changeDeck = false, remNote = false, minerU = false, ocr = false, speech = false, speaker = false },
        retainedDataRoot = dataRoot
    };
    await WriteReceiptAsync(receiptPath, receipt);
    Console.WriteLine($"RECEIPT {receiptPath}");
    Console.WriteLine("RESULT PASS REAL_QUESTION_ANKI_CANARY");
    return 0;
}
catch (Exception ex)
{
    var observed = Directory.Exists(dataRoot) ? TryReadWorkerEvidence(dataRoot) : null;
    var failure = new
    {
        schema = "kc.core-v3-real-question-anki-canary-receipt/1",
        startedAt,
        completedAt = DateTimeOffset.UtcNow,
        status = "FAIL",
        errorType = ex.GetType().Name,
        error = ex.Message,
        externalState = observed is null ? "unknown" : "evidence-observed",
        observedMutatingActions = observed?.MutatingActions ?? Array.Empty<string>(),
        observedNoteId = observed?.NoteId,
        observedCardId = observed?.CardId,
        retryAllowed = false,
        retainedDataRoot = dataRoot
    };
    await WriteReceiptAsync(receiptPath, failure);
    Console.WriteLine($"RECEIPT {receiptPath}");
    Console.WriteLine($"RESULT FAIL REAL_QUESTION_ANKI_CANARY {ex.Message}");
    return 1;
}
finally
{
    if (host is not null)
    {
        if (!host.HasExited) host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync();
        host.Dispose();
    }
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    if (values.Length % 2 != 0) throw new ArgumentException("Every Canary argument requires a value.");
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(values[index], values[index + 1])) throw new ArgumentException("Canary arguments are malformed or duplicated.");
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> values, string name)
    => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing required argument {name}.");

static string RequiredPath(IReadOnlyDictionary<string, string> values, string name, bool directory)
{
    var path = Path.GetFullPath(Required(values, name));
    if (directory ? !Directory.Exists(path) : !File.Exists(path)) throw new FileNotFoundException($"Required path does not exist: {path}", path);
    return path;
}

static Process StartHost(string dotnet, string hostDll, string repositoryRoot, string binding, string dataRoot, string pipeName)
{
    var process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
    foreach (var argument in new[] { hostDll, "--repository-root", repositoryRoot, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName }) process.StartInfo.ArgumentList.Add(argument);
    Require(process.Start(), "Immutable production Host did not start.");
    return process;
}

static async Task WaitReadyAsync(Process process, string pipeName)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
    if (ready != $"CORE_V3_READY {pipeName}")
    {
        var error = process.HasExited ? await process.StandardError.ReadToEndAsync() : "Host did not emit its readiness line.";
        throw new InvalidOperationException($"Immutable production Host was not ready. Actual='{ready}'. {error}");
    }
}

static WorkerEvidence ReadWorkerEvidence(string dataRoot)
    => TryReadWorkerEvidence(dataRoot) ?? throw new InvalidDataException("Anki Worker did not publish operations/items evidence.");

static WorkerEvidence? TryReadWorkerEvidence(string dataRoot)
{
    var operations = Directory.GetFiles(dataRoot, "operations.ndjson", SearchOption.AllDirectories)
        .Where(path => path.Contains($"{Path.DirectorySeparatorChar}anki-export{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)).ToArray();
    var items = Directory.GetFiles(dataRoot, "items.ndjson", SearchOption.AllDirectories)
        .Where(path => path.Contains($"{Path.DirectorySeparatorChar}anki-export{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (operations.Length != 1 || items.Length != 1) return null;
    var actions = File.ReadLines(operations[0]).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("action").GetString()!).ToArray();
    var itemLines = File.ReadLines(items[0]).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
    if (itemLines.Length != 1) return null;
    using var item = JsonDocument.Parse(itemLines[0]);
    var root = item.RootElement;
    var mutations = actions.Where(action => action is "addNote" or "addNotes" or "updateNoteFields" or "deleteNotes" or "changeDeck").ToArray();
    return new WorkerEvidence(operations[0], items[0], actions, mutations, root.GetProperty("note_id").GetInt64(), root.GetProperty("card_id").GetString()!, root.GetProperty("status").GetString()!);
}

static int CountAttempts(string dataRoot) => Directory.GetDirectories(dataRoot, "attempt-*", SearchOption.AllDirectories).Length;
static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void RequireHash(string path, string expected) { if (!File.Exists(path) || !Sha256(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Immutable release hash mismatch: {path}"); }
static string ResolveDotnetSdk() => File.Exists(@"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe") ? @"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe" : throw new FileNotFoundException("Pinned .NET SDK is unavailable.");
static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static async Task WriteReceiptAsync(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); if (File.Exists(path)) throw new IOException("Refusing to overwrite create-once Canary receipt."); await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })); }
sealed record WorkerEvidence(string OperationsPath, string ItemsPath, string[] Actions, string[] MutatingActions, long NoteId, string CardId, string ItemStatus);
