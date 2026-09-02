using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

if (args.Length == 3 && args[0] == "--authority-content-canary")
{
    await AuthorityContentCanary.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    return;
}

var projectRoot = Path.GetFullPath(args.Length == 1 ? args[0] : throw new ArgumentException("Expected project root."));
var fakeWorker = Path.Combine(projectRoot, "tests", "FakeWorker", "bin", "Release", "net8.0", "RemNoteQuickClipper.FakeWorker.exe");
Assert(File.Exists(fakeWorker), "Fake content worker is missing.");
var root = Path.Combine(Path.GetTempPath(), "KnowledgeClipper.QuestionWorkflow", Guid.NewGuid().ToString("N"));
var jobsRoot = Path.Combine(root, "jobs");
Directory.CreateDirectory(jobsRoot);
var pipe = "KnowledgeClipper.Question." + Guid.NewGuid().ToString("N");
using var shutdown = new CancellationTokenSource();
Task? host = null;
try
{
    var registryPath = Path.Combine(root, "worker-registry.v1.json");
    await AtomicFile.WriteJsonOnceAsync(registryPath, Registry(projectRoot, fakeWorker));
    host = new OrchestratorHost(jobsRoot, registryPath, projectRoot, 4).ServeAsync(Ipc(pipe), shutdown.Token);

    const string taskId = "task-question-workflow-main";
    var jobRoot = await WriteQuestionTaskAsync(jobsRoot, taskId, "What is an immutable task package?");
    await SendCommandAsync(pipe, "task.submit", null, taskId + "/task.manifest.v2.json", "submit-main", null);
    var first = await WaitStatusAsync(pipe, taskId, "waiting_input");
    Assert(first.GetProperty("answerAvailable").GetBoolean(), "Initial answer was not projected as available.");
    Assert(first.GetProperty("questionTurnCount").GetInt32() == 1, "Initial question turn count is wrong.");
    var firstToken = first.GetProperty("questionTargetToken").GetString()!;
    var firstDisplay = first.GetProperty("questionAnswerRef").Deserialize<ArtifactReference>(ContractJson.Options)!;
    Assert(firstDisplay.Kind == "question.answer.text", "Query exposed the structured ancestry bundle instead of derived text.");
    var firstText = await ReadSingleTextArtifactAsync(jobRoot, firstDisplay);
    Assert(firstText == "Fake answer 1: What is an immutable task package?", "Derived display answer text is not the generated current answer.");
    Assert(!JsonSerializer.Serialize(first).Contains("answer_text", StringComparison.Ordinal), "IPC leaked answer content.");

    var firstStructured = ReadResults(jobRoot).Single(result => result.Outputs.Any(output => output.Kind == "question.answer")).Outputs.Single(output => output.Kind == "question.answer");
    await AssertTextAncestryAsync(jobRoot, firstDisplay, firstStructured);

    var followUpRef = await PublishTextArtifactAsync(jobRoot, "commands/command-follow-up/question-turn", "question.turn.0002", "question.turn", "How does retry preserve it?");
    const string followUpPayloadRef = "commands/command-follow-up/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, followUpPayloadRef), new
    {
        schemaVersion = "knowledge-clipper-question-action.v2", action = "question.follow-up", targetToken = firstToken,
        turnIndex = 1, questionTurnRef = followUpRef
    });
    await SendCommandAsync(pipe, "task.provide_input", taskId, followUpPayloadRef, "follow-up", "waiting_input");
    await WaitForAsync(() => ReadEvents(jobRoot).Any(item => item.CommandId == "command-follow-up" && item.Type == "command.execution_completed"), "follow-up terminal event");
    var second = await WaitStatusAsync(pipe, taskId, "waiting_input");
    Assert(second.GetProperty("questionTurnCount").GetInt32() == 2, "Follow-up did not preserve and append the ordered conversation.");
    var secondToken = second.GetProperty("questionTargetToken").GetString()!;
    Assert(secondToken != firstToken, "Follow-up did not rotate the opaque active token.");
    var secondDisplay = second.GetProperty("questionAnswerRef").Deserialize<ArtifactReference>(ContractJson.Options)!;
    Assert(await ReadSingleTextArtifactAsync(jobRoot, secondDisplay) == "Fake answer 2: How does retry preserve it?", "Follow-up display text is not the newest answer.");
    var structuredResults = ReadResults(jobRoot).Where(result => result.Outputs.Any(output => output.Kind == "question.answer")).ToArray();
    Assert(structuredResults.Length == 2, "Follow-up did not create exactly one new question.answer attempt.");
    var secondStructured = structuredResults.Single(result => result.AttemptId != structuredResults[0].AttemptId || result.Outputs[0].ArtifactRef != structuredResults[0].Outputs[0].ArtifactRef).Outputs.Single(output => output.Kind == "question.answer");
    using (var answer = await ReadJsonArtifactAsync(jobRoot, secondStructured))
    {
        var turns = answer.RootElement.GetProperty("turns");
        Assert(turns.GetArrayLength() == 2 && turns[0].GetProperty("provenance").GetString() == "persisted" && turns[1].GetProperty("provenance").GetString() == "generated", "Structured follow-up ancestry was not persisted-then-generated.");
        Assert(turns[0].GetProperty("artifact_ref").GetString() == firstStructured.ArtifactRef, "Core did not bind the prior pending answer to its immutable structured artifact.");
    }

    await SendCommandAsync(pipe, "task.provide_input", taskId, followUpPayloadRef, "repeat-old", null);
    await WaitErrorAsync(jobRoot, "repeat-old", "question.session_already_resolved");

    var outOfOrderTurn = await PublishTextArtifactAsync(jobRoot, "commands/command-out-of-order/question-turn", "question.turn.0003.bad", "question.turn", "Out of order");
    const string outOfOrderPayload = "commands/command-out-of-order/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, outOfOrderPayload), new { schemaVersion = "knowledge-clipper-question-action.v2", action = "question.follow-up", targetToken = secondToken, turnIndex = 3, questionTurnRef = outOfOrderTurn });
    await SendCommandAsync(pipe, "task.provide_input", taskId, outOfOrderPayload, "out-of-order", null);
    await WaitErrorAsync(jobRoot, "out-of-order", "question.turn_order_invalid");

    const string otherTask = "task-question-cross-target";
    var otherRoot = await WriteQuestionTaskAsync(jobsRoot, otherTask, "Cross task source");
    const string crossPayload = "commands/command-cross-task/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(otherRoot, crossPayload), new { schemaVersion = "knowledge-clipper-question-action.v2", action = "question.make-cards", targetToken = secondToken });
    await SendCommandAsync(pipe, "task.provide_input", otherTask, crossPayload, "cross-task", null);
    await WaitErrorAsync(otherRoot, "cross-task", "question.session_not_found");

    const string cardsPayload = "commands/command-make-cards/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, cardsPayload), new { schemaVersion = "knowledge-clipper-question-action.v2", action = "question.make-cards", targetToken = secondToken });
    await SendCommandAsync(pipe, "task.provide_input", taskId, cardsPayload, "make-cards", "waiting_input");
    await WaitStatusAsync(pipe, taskId, "succeeded");
    var cardRun = ReadResults(jobRoot).Single(result => result.StageId == "question-make-cards");
    Assert(cardRun.Outputs.Any(output => output.Kind == "learning-bundle") && cardRun.Outputs.Any(output => output.Kind == "card-bundle") && !cardRun.Outputs.Any(output => output.Kind == "question.answer"),
        "make-cards did not produce neutral bundles only after the full answer chain.");
    Assert(ReadResults(jobRoot).All(result => result.Worker.Id == "content.deepseek"), "Question workflow invoked an exporter before an explicit destination step.");
    await SendCommandAsync(pipe, "task.provide_input", taskId, cardsPayload, "repeat-cards", null);
    await WaitErrorAsync(jobRoot, "repeat-cards", "question.session_already_resolved");

    const string ankiTask = "task-question-anki-route";
    var ankiRoot = await WriteQuestionTaskAsync(jobsRoot, ankiTask, "Why should active recall be expressed as an executable workflow?", projectRoot, anki: true);
    await SendCommandAsync(pipe, "task.submit", null, ankiTask + "/task.manifest.v2.json", "anki-submit", null);
    var ankiInitial = await WaitStatusAsync(pipe, ankiTask, "waiting_input");
    Assert(ReadResults(ankiRoot).Count(result => result.Worker.Id == "export.anki") == 0, "Initial question wrote to Anki before make_cards.");
    var ankiFirstToken = ankiInitial.GetProperty("questionTargetToken").GetString()!;
    var ankiFollowRef = await PublishTextArtifactAsync(ankiRoot, "commands/command-anki-follow/question-turn", "question.turn.0002", "question.turn", "What are the three concrete steps?");
    const string ankiFollowPayload = "commands/command-anki-follow/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(ankiRoot, ankiFollowPayload), new
    {
        schemaVersion = "knowledge-clipper-question-action.v2", action = "question.follow-up", targetToken = ankiFirstToken,
        turnIndex = 1, questionTurnRef = ankiFollowRef
    });
    await SendCommandAsync(pipe, "task.provide_input", ankiTask, ankiFollowPayload, "anki-follow", "waiting_input");
    await WaitForAsync(() => ReadEvents(ankiRoot).Any(item => item.CommandId == "command-anki-follow" && item.Type == "command.execution_completed"), "Anki follow-up completion");
    var ankiFollowed = await WaitStatusAsync(pipe, ankiTask, "waiting_input");
    Assert(ankiFollowed.GetProperty("questionTurnCount").GetInt32() == 2, "Anki follow-up did not append exactly one turn.");
    Assert(ReadResults(ankiRoot).Count(result => result.Worker.Id == "export.anki") == 0, "Follow-up wrote to Anki before make_cards.");
    var ankiSecondToken = ankiFollowed.GetProperty("questionTargetToken").GetString()!;
    const string ankiCardsPayload = "commands/command-anki-make-cards/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(ankiRoot, ankiCardsPayload), new
    {
        schemaVersion = "knowledge-clipper-question-action.v2", action = "question.make-cards", targetToken = ankiSecondToken
    });
    await SendCommandAsync(pipe, "task.provide_input", ankiTask, ankiCardsPayload, "anki-make-cards", "waiting_input");
    await WaitStatusAsync(pipe, ankiTask, "succeeded");
    var ankiResults = ReadResults(ankiRoot);
    Assert(ankiResults.Count(result => result.Worker.Id == "content.deepseek") == 3, "Anki question route did not execute initial, follow-up, and make-cards Content attempts exactly once.");
    Assert(ankiResults.Count(result => result.Worker.Id == "export.anki") == 1, "make_cards did not append exactly one Anki export attempt.");
    var ankiPlan = ReadPlans(ankiRoot).Single(plan => plan.Stages.Any(stage => stage.StageId == "question-make-cards"));
    Assert(ankiPlan.Routing is not null && ankiPlan.Routing.ProcessingProfile == "cards" && ankiPlan.Routing.Destinations.SequenceEqual(new[] { "anki" }), "make_cards did not inherit the frozen route.");
    var ankiStage = ankiPlan.Stages.Single(stage => stage.StageId == "anki-export");
    Assert(ankiStage.DependsOn.SequenceEqual(new[] { "question-make-cards" }) && ankiStage.RetryPolicy.MaxAttempts == 1,
        "Anki export was not appended after make_cards with fail-closed retry semantics.");
    Assert(ankiStage.InputRefs.Count == 1 && ankiStage.InputRefs[0].Kind == "anki-export-config" &&
           ankiStage.ConfigSnapshot.GetProperty("ankiConfigArtifactId").GetString() == ankiStage.InputRefs[0].ArtifactRef,
        "Anki export did not bind the frozen SHA-bound configuration artifact.");
    await SendCommandAsync(pipe, "task.provide_input", ankiTask, ankiCardsPayload, "anki-repeat-cards", null);
    await WaitErrorAsync(ankiRoot, "anki-repeat-cards", "question.session_already_resolved");
    Assert(ReadResults(ankiRoot).Count(result => result.Worker.Id == "export.anki") == 1, "Replayed make_cards caused a duplicate Anki attempt.");

    const string missingConfigTask = "task-question-anki-config-missing";
    var missingConfigRoot = await WriteQuestionTaskAsync(jobsRoot, missingConfigTask, "Why must config be frozen?", projectRoot, anki: true, omitAnkiConfig: true);
    await SendCommandAsync(pipe, "task.submit", null, missingConfigTask + "/task.manifest.v2.json", "missing-config-submit", null);
    var missingConfigStatus = await WaitStatusAsync(pipe, missingConfigTask, "waiting_input");
    const string missingConfigPayload = "commands/command-missing-config-cards/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(missingConfigRoot, missingConfigPayload), new
    {
        schemaVersion = "knowledge-clipper-question-action.v2", action = "question.make-cards",
        targetToken = missingConfigStatus.GetProperty("questionTargetToken").GetString()
    });
    await SendCommandAsync(pipe, "task.provide_input", missingConfigTask, missingConfigPayload, "missing-config-cards", "waiting_input");
    await WaitErrorAsync(missingConfigRoot, "missing-config-cards", "resolver.export_configuration_missing");
    Assert(ReadResults(missingConfigRoot).All(result => result.Worker.Id != "export.anki"), "Missing Anki config started an exporter.");

    const string driftConfigTask = "task-question-anki-config-drift";
    var driftConfigRoot = await WriteQuestionTaskAsync(jobsRoot, driftConfigTask, "Why must config drift fail closed?", projectRoot, anki: true);
    await SendCommandAsync(pipe, "task.submit", null, driftConfigTask + "/task.manifest.v2.json", "drift-config-submit", null);
    var driftConfigStatus = await WaitStatusAsync(pipe, driftConfigTask, "waiting_input");
    var driftManifestPath = Path.Combine(driftConfigRoot, "artifacts", "anki-config-question", "artifact.json");
    await File.AppendAllTextAsync(driftManifestPath, Environment.NewLine, Encoding.UTF8);
    const string driftConfigPayload = "commands/command-drift-config-cards/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(driftConfigRoot, driftConfigPayload), new
    {
        schemaVersion = "knowledge-clipper-question-action.v2", action = "question.make-cards",
        targetToken = driftConfigStatus.GetProperty("questionTargetToken").GetString()
    });
    await SendCommandAsync(pipe, "task.provide_input", driftConfigTask, driftConfigPayload, "drift-config-cards", "waiting_input");
    await WaitErrorAsync(driftConfigRoot, "drift-config-cards", "binding.manifest_hash_mismatch");
    Assert(ReadResults(driftConfigRoot).All(result => result.Worker.Id != "export.anki"), "Drifted Anki config started an exporter.");

    var emptyTask = "task-question-empty-input";
    await WriteQuestionTaskAsync(jobsRoot, emptyTask, "   \t");
    await SendCommandAsync(pipe, "task.submit", null, emptyTask + "/task.manifest.v2.json", emptyTask, null);
    var emptyFailed = await WaitStatusAsync(pipe, emptyTask, "failed");
    Assert(!emptyFailed.GetProperty("answerAvailable").GetBoolean(), "Whitespace-only question did not fail closed.");

    var tamperCases = new[] { "TAMPER_ANSWER_PAYLOAD", "TAMPER_ANSWER_MANIFEST", "TAMPER_LEARNING_ANCESTRY", "TAMPER_ANSWER_CHAIN" };
    foreach (var marker in tamperCases)
    {
        var tamperTask = "task-question-" + marker.ToLowerInvariant().Replace('_', '-');
        await WriteQuestionTaskAsync(jobsRoot, tamperTask, marker);
        await SendCommandAsync(pipe, "task.submit", null, tamperTask + "/task.manifest.v2.json", tamperTask, null);
        var failed = await WaitStatusAsync(pipe, tamperTask, "failed");
        Assert(!failed.GetProperty("answerAvailable").GetBoolean() && failed.GetProperty("questionAnswerRef").ValueKind == JsonValueKind.Null,
            $"{marker} exposed an unverified answer.");
    }

    const string commandRootDriftTask = "task-question-command-root-drift";
    var commandRootDriftRoot = await WriteQuestionTaskAsync(jobsRoot, commandRootDriftTask, "Why must interactive inputs remain command-owned?");
    await SendCommandAsync(pipe, "task.submit", null, commandRootDriftTask + "/task.manifest.v2.json", "command-root-drift-submit", null);
    var commandRootDriftStatus = await WaitStatusAsync(pipe, commandRootDriftTask, "waiting_input");
    var commandRootDriftTurn = await PublishTextArtifactAsync(
        commandRootDriftRoot,
        "commands/foreign-command/question-turn",
        "question.turn.0002",
        "question.turn",
        "This payload is not owned by the accepted command.");
    const string commandRootDriftPayload = "commands/command-root-drift/question-action.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(commandRootDriftRoot, commandRootDriftPayload), new
    {
        schemaVersion = "knowledge-clipper-question-action.v2",
        action = "question.follow-up",
        targetToken = commandRootDriftStatus.GetProperty("questionTargetToken").GetString(),
        turnIndex = 1,
        questionTurnRef = commandRootDriftTurn
    });
    await SendCommandAsync(pipe, "task.provide_input", commandRootDriftTask, commandRootDriftPayload, "root-drift", "waiting_input");
    await WaitStatusAsync(pipe, commandRootDriftTask, "failed");
    var commandRootFailure = ReadResults(commandRootDriftRoot).Single(result => result.StageId == "question-follow-up");
    Assert(commandRootFailure.Status == "failed" && commandRootFailure.Error?.Code == "binding.question_command_root_invalid",
        "A question turn outside commands/<commandId>/question-turn was not rejected before worker launch.");
    Assert(!ReadEvents(commandRootDriftRoot).Any(item => item.Type == "stage.started" && item.StageId == "question-follow-up"),
        "The Content worker started before interactive command provenance validation completed.");

    var receipt = new
    {
        schemaVersion = "knowledge-clipper-question-workflow-test-receipt.v2",
        status = "passed",
        observed = new
        {
            initialQuestion = "What is an immutable task package?",
            initialAnswer = firstText,
            followUpQuestion = "How does retry preserve it?",
            followUpAnswer = await ReadSingleTextArtifactAsync(jobRoot, secondDisplay),
            orderedTurns = 2,
            makeCardsOutputs = cardRun.Outputs.Select(output => output.Kind).ToArray(),
            destinationRouting = new
            {
                initialWrites = 0,
                followUpWrites = 0,
                makeCardsWrites = 1,
                replayWrites = 0,
                destinations = ankiPlan.Routing!.Destinations,
                ankiConfigArtifactRef = ankiStage.InputRefs[0].ArtifactRef,
                ankiConfigExpectedSha256 = ankiStage.InputRefs[0].ExpectedSha256,
                unknownExternalStateMaxAttempts = ankiStage.RetryPolicy.MaxAttempts
            },
            exportersStartedInDestinationNeutralRoute = 0,
            failClosed = new[] { "empty-input", "stale", "repeat", "cross-task", "out-of-order", "interactive-command-root", "interactive-command-owner-drift", "payload-bytes-hash", "manifest-hash", "learning-ancestry", "answer-chain", "anki-config-missing", "anki-config-drift" }
        }
    };
    var receiptPath = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_QUESTION_TEST_RECEIPT");
    if (!string.IsNullOrWhiteSpace(receiptPath)) await AtomicFile.WriteJsonOnceAsync(Path.GetFullPath(receiptPath), receipt);
    Console.WriteLine(JsonSerializer.Serialize(receipt, ContractJson.Options));
}
finally
{
    shutdown.Cancel();
    if (host is not null) try { await host; } catch (OperationCanceledException) { }
    if (Directory.Exists(root) && Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_KEEP_QUESTION_TEST") != "1") Directory.Delete(root, true);
}

static async Task<string> WriteQuestionTaskAsync(string jobsRoot, string taskId, string question, string? projectRoot = null, bool anki = false, bool omitAnkiConfig = false)
{
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    var textBytes = Encoding.UTF8.GetBytes(question);
    const string textPath = "inputs/question-turn-0001.txt";
    Directory.CreateDirectory(Path.GetDirectoryName(PathPolicy.ResolveJobRelative(jobRoot, textPath))!);
    await File.WriteAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, textPath), textBytes);
    var captureArtifactRef = "capture.question.user.0001";
    var bundle = new
    {
        schemaVersion = "capture-bundle.v1", captureId = "capture-question-0001", createdAt = DateTimeOffset.UtcNow,
        intent = "question", source = "question",
        modeSnapshot = new { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "ocr" },
        artifacts = new[] { new { artifactRef = captureArtifactRef, relativePath = textPath, sha256 = Hash(textBytes), bytes = textBytes.LongLength, mediaType = "text/plain", encoding = "utf-8" } },
        selectedTextArtifactRef = (string?)null, contextSegments = (object?)null, questionImages = Array.Empty<string>(), answerImages = Array.Empty<string>(),
        questionTurns = new[] { new { turnIndex = 1, role = "user", artifactRef = captureArtifactRef } }
    };
    const string bundlePath = "inputs/capture-bundle.v1.json";
    var bundleBytes = JsonSerializer.SerializeToUtf8Bytes(bundle, ContractJson.Options);
    await File.WriteAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, bundlePath), bundleBytes);
    const string bundleRef = "capture.bundle.question";
    var manifestRef = $"artifacts/{bundleRef}/artifact.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef), new
    {
        schemaVersion = "1.0.0", artifactId = bundleRef, kind = "capture-bundle", contractVersion = "1.0.0", mediaType = "application/vnd.rqc.capture-bundle+json",
        files = new[] { new { path = bundlePath, sha256 = Hash(bundleBytes), bytes = bundleBytes.LongLength, mediaType = "application/vnd.rqc.capture-bundle+json" } },
        createdAt = DateTimeOffset.UtcNow, producer = new { stageId = "capture", workerId = "desktop-capture", workerVersion = "1.0.0", attemptId = "capture-attempt-1" },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = bundlePath }
    });
    var manifestHash = Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef)));
    const string intentRef = "plans/desktop-intent.v1.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, intentRef), new { schemaVersion = "desktop-intent.v1", taskId, intent = "clip.question", inputArtifactRef = bundleRef, requestedOutputs = new[] { "question.answer" } });
    var captureReference = new ArtifactReference(bundleRef, "capture-bundle", "1.0.0", manifestRef, manifestHash);
    if (anki && !omitAnkiConfig)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) throw new ArgumentException("projectRoot is required for the Anki question fixture.");
        var configBytes = await File.ReadAllBytesAsync(Path.Combine(projectRoot, "workers", "anki-exporter", "config", "anki-export.v1.example.json"));
        var ankiReference = await PublishJsonArtifactAsync(jobRoot, "anki-config-question", "anki-export-config", "anki-export.v1.json", configBytes);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "KnowledgeClipper.QuestionTests", version = "2.5.2" },
            intent = "clip.question", executionProfile = "cards-anki",
            inputRefs = new[] { captureReference, ankiReference }.Select(item => new { item.ArtifactRef, item.Kind, item.ContractVersion, item.ExpectedSha256 }).ToArray(),
            requestedOutputs = new[] { "question.answer" }, initialPlanRef = intentRef, retentionClass = "evidence"
        });
        var settings = new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v3", "question-anki", new Dictionary<string, WorkflowSettingsProfile>
        {
            ["question-anki"] = new("Question Anki", new WorkflowRouteSettings("cards", new[] { "anki" }), new CaptureSettings(false, "sentence", "ocr"))
        });
        var configuration = DesktopIntentResolver.LoadConfiguration(Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json"));
        var adapter = new TaskSettingsAdapter();
        var built = await adapter.BuildAndPublishAsync(jobRoot, taskId, "clip.question", settings, null, configuration,
            new[] { captureReference, ankiReference }, DateTimeOffset.Parse("2026-08-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await adapter.MigrateLegacyManifestV2Async(jobRoot, built);
    }
    else
    {
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v2.json"), new
        {
            schemaVersion = "2.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "KnowledgeClipper.QuestionTests", version = "2.5.2" },
            intent = "clip.question", executionProfile = "cards", destinations = omitAnkiConfig ? new[] { "anki" } : Array.Empty<string>(),
            inputRefs = new[] { new { artifactRef = bundleRef, kind = "capture-bundle", contractVersion = "1.0.0", expectedSha256 = manifestHash } },
            requestedOutputs = new[] { "question.answer" }, initialPlanRef = intentRef, retentionClass = "ephemeral"
        });
    }
    return jobRoot;
}

static async Task<ArtifactReference> PublishJsonArtifactAsync(string jobRoot, string artifactId, string kind, string fileName, byte[] payload)
{
    var root = $"artifacts/{artifactId}";
    var payloadRef = $"{root}/{fileName}";
    Directory.CreateDirectory(Path.GetDirectoryName(PathPolicy.ResolveJobRelative(jobRoot, payloadRef))!);
    await File.WriteAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, payloadRef), payload);
    var manifestRef = $"{root}/artifact.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef), new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "application/json",
        files = new[] { new { path = payloadRef, sha256 = Hash(payload), bytes = payload.LongLength, mediaType = "application/json" } },
        createdAt = DateTimeOffset.Parse("2026-08-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        producer = new { stageId = "desktop-settings", workerId = "desktop.settings", workerVersion = "1.0.0", attemptId = "settings-" + artifactId },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = payloadRef }
    });
    return new ArtifactReference(artifactId, kind, "1.0.0", manifestRef, Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef))));
}

static async Task<ArtifactReference> PublishTextArtifactAsync(string jobRoot, string root, string artifactId, string kind, string text)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    var payloadRef = root + "/payload.txt";
    Directory.CreateDirectory(Path.GetDirectoryName(PathPolicy.ResolveJobRelative(jobRoot, payloadRef))!);
    await File.WriteAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, payloadRef), bytes);
    var manifestRef = $"artifacts/{artifactId}/artifact.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef), new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "text/plain",
        files = new[] { new { path = payloadRef, sha256 = Hash(bytes), bytes = bytes.LongLength, mediaType = "text/plain" } },
        createdAt = DateTimeOffset.UtcNow, producer = new { stageId = "desktop-question", workerId = "desktop-capture", workerVersion = "1.0.0", attemptId = "question-turn-attempt" },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = payloadRef, encoding = "utf-8" }
    });
    return new ArtifactReference(artifactId, kind, "1.0.0", manifestRef, Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef))));
}

static async Task<string> ReadSingleTextArtifactAsync(string jobRoot, ArtifactReference reference)
{
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath)));
    Assert(Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath))) == reference.ExpectedSha256, "ArtifactReference manifest hash mismatch.");
    var file = manifest.RootElement.GetProperty("files")[0];
    Assert(manifest.RootElement.GetProperty("files").GetArrayLength() == 1 && file.GetProperty("mediaType").GetString() == "text/plain", "Display answer is not exactly one text/plain file.");
    var bytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, file.GetProperty("path").GetString()!));
    Assert(bytes.LongLength == file.GetProperty("bytes").GetInt64() && Hash(bytes) == file.GetProperty("sha256").GetString(), "Display answer payload bytes/hash mismatch.");
    return new UTF8Encoding(false, true).GetString(bytes);
}

static async Task AssertTextAncestryAsync(string jobRoot, ArtifactReference display, ArtifactReference structured)
{
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, display.ManifestPath)));
    var input = manifest.RootElement.GetProperty("inputRefs");
    Assert(input.GetArrayLength() == 1 && input[0].GetProperty("artifactRef").GetString() == structured.ArtifactRef && input[0].GetProperty("kind").GetString() == "question.answer", "Display answer does not point to the structured ancestry bundle.");
}

static async Task<JsonDocument> ReadJsonArtifactAsync(string jobRoot, ArtifactReference reference)
{
    using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath)));
    return JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, manifest.RootElement.GetProperty("files")[0].GetProperty("path").GetString()!)));
}

static WorkerRegistry Registry(string projectRoot, string workerPath) => new("1.0.0", "2.2.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[]
{
    new WorkerEntry("content.deepseek", ">=1.0.0 <2.0.0", new WorkerEntrypoint(Path.GetRelativePath(projectRoot, workerPath).Replace('\\', '/'), new[] { "--worker-id", "content.deepseek" }),
        new[] { "content.understand-and-card" }, new[] { "content-input" }, new[] { "learning-bundle", "card-bundle", "language-practice-decision", "question.answer", "ocr-proofread-bundle" },
        new[] { "parity-remnote", "cards-remnote", "cards-anki" }, "local", "idempotent", true),
    new WorkerEntry("export.anki", ">=1.0.0 <2.0.0", new WorkerEntrypoint(Path.GetRelativePath(projectRoot, workerPath).Replace('\\', '/'), new[] { "--worker-id", "export.anki" }),
        new[] { "anki-export" }, new[] { "card-bundle", "learning-bundle", "anki-export-config" }, new[] { "anki-export-evidence", "anki-connection-status", "anki-managed-initialization-status" },
        new[] { "cards-anki" }, "external", "reconcilable", true)
});

static IReadOnlyList<WorkerResult> ReadResults(string jobRoot) => Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories)
    .Select(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)!).ToArray();

static IReadOnlyList<PipelinePlan> ReadPlans(string jobRoot) => Directory.GetFiles(Path.Combine(jobRoot, "plans", "resolved"), "pipeline.v2.json", SearchOption.AllDirectories)
    .Select(path => JsonSerializer.Deserialize<PipelinePlan>(File.ReadAllText(path), ContractJson.Options)!).ToArray();

static IReadOnlyList<TaskEvent> ReadEvents(string jobRoot) => Directory.Exists(Path.Combine(jobRoot, "runs"))
    ? Directory.GetFiles(Path.Combine(jobRoot, "runs"), "*.json", SearchOption.AllDirectories).Where(path => path.Contains($"{Path.DirectorySeparatorChar}events{Path.DirectorySeparatorChar}"))
        .Select(path => JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options)!).OrderBy(item => item.Timestamp).ToArray()
    : Array.Empty<TaskEvent>();

static async Task<JsonElement> SendCommandAsync(string pipe, string type, string? taskId, string payloadRef, string suffix, string? expectedState)
{
    var payload = new { schemaVersion = "1.0.0", commandId = "command-" + suffix, operationId = "operation-" + suffix, taskId, type, issuedAt = DateTimeOffset.UtcNow, idempotencyKey = Hash(Encoding.UTF8.GetBytes("idempotency-" + suffix)), payloadRef, expectedState };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipe, new { protocolVersion = "1.0", correlationId = "correlation-" + suffix, messageType = "task-command", payload }));
    Assert(response.RootElement.GetProperty("ok").GetBoolean(), $"Command {suffix} returned protocol error.");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<JsonElement> WaitStatusAsync(string pipe, string taskId, string expected)
{
    for (var index = 0; index < 300; index++)
    {
        using var response = JsonDocument.Parse(await RoundTripAsync(pipe, new { protocolVersion = "1.0", correlationId = "query-" + taskId + "-" + index, messageType = "task-query", payload = new { schemaVersion = "2.2.0", queryId = "query-" + index, type = "task.get", taskId } }));
        var items = response.RootElement.GetProperty("payload").GetProperty("items");
        if (items.GetArrayLength() == 1 && items[0].GetProperty("status").GetString() == expected) return items[0].Clone();
        await Task.Delay(50);
    }
    throw new TimeoutException($"Task {taskId} did not reach {expected}.");
}

static async Task WaitErrorAsync(string jobRoot, string suffix, string code) => await WaitForAsync(
    () => ReadEvents(jobRoot).Any(item => item.CommandId == "command-" + suffix && item.Type == "command.execution_failed" && item.Payload.GetProperty("errorCode").GetString() == code), suffix + "/" + code);

static async Task WaitForAsync(Func<bool> predicate, string label)
{
    for (var index = 0; index < 300; index++) { if (predicate()) return; await Task.Delay(50); }
    throw new TimeoutException("Timed out waiting for " + label + ".");
}

static async Task<string> RoundTripAsync(string pipe, object value)
{
    await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(5000);
    var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(value, IpcJson.Options) + "\n");
    await client.WriteAsync(bytes); await client.FlushAsync();
    using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, true);
    return await reader.ReadLineAsync() ?? throw new InvalidDataException("No IPC response.");
}

static IpcConfiguration Ipc(string pipe) => new("1.0.0", "windows-named-pipe", pipe, $"\\\\.\\pipe\\{pipe}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query" }, true, "job-event-sequence", 1_048_576, new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"), "../schemas/common/ipc-request.schema.json", "../schemas/common/ipc-response.schema.json", "respond-when-possible-then-close");
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
