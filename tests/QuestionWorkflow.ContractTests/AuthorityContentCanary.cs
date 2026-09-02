using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

internal static class AuthorityContentCanary
{
    private const string InitialQuestion = "为什么不可变任务包能让失败重试更安全？";
    private const string FollowUpQuestion = "它怎样避免追问覆盖上一轮答案？";
    private const string RegistrySha256 = "b695939d797ddf7924d048b3380885ecf06ab926a807e9381d531855363f885b";
    private const string WorkerSha256 = "f6ec35798f8948727b8664517fdbb096db8a94c8dd079f612b05bac4ea72d3b8";

    public static async Task RunAsync(string projectRoot, string canaryRoot)
    {
        Directory.CreateDirectory(canaryRoot);
        var authorityContractRoot = Path.Combine(projectRoot, "contracts", "authority", "worker-registry", "v1.2.3-20260828T021300Z");
        var registryPath = Path.Combine(authorityContractRoot, "worker-registry.v1.json");
        var checkpointPath = Path.Combine(authorityContractRoot, "checkpoint.json");
        var releaseRoot = Path.Combine(projectRoot, "releases", "knowledge-clipper-authority-v1.2.3-20260828T021300Z", "win-x64");
        var workerPath = Path.Combine(releaseRoot, "workers", "content", "KnowledgeClipper.Content.Worker.exe");
        Assert(File.Exists(registryPath) && File.Exists(checkpointPath) && File.Exists(workerPath), "Authority v1.2.3 binding is incomplete.");
        Assert(Hash(await File.ReadAllBytesAsync(registryPath)) == RegistrySha256, "Authority registry SHA-256 drifted.");
        Assert(Hash(await File.ReadAllBytesAsync(workerPath)) == WorkerSha256, "content.deepseek authority EXE SHA-256 drifted.");

        var jobsRoot = Path.Combine(canaryRoot, "jobs");
        Directory.CreateDirectory(jobsRoot);
        var pipe = "KnowledgeClipper.AuthorityQuestion." + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource();
        var host = new OrchestratorHost(jobsRoot, registryPath, releaseRoot, 1).ServeAsync(Ipc(pipe), shutdown.Token);
        try
        {
            var taskId = "task-authority-question-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ").ToLowerInvariant();
            var jobRoot = await WriteQuestionTaskAsync(jobsRoot, taskId, InitialQuestion);
            await SendCommandAsync(pipe, "task.submit", null, taskId + "/task.manifest.v1.json", "submit", null);

            var firstQuery = await WaitStatusAsync(pipe, taskId, "waiting_input");
            Assert(firstQuery.GetProperty("answerAvailable").GetBoolean(), "Initial answer was not available at waiting_input.");
            Assert(firstQuery.GetProperty("questionTurnCount").GetInt32() == 1, "Initial waiting_input turn count is not one.");
            var firstToken = firstQuery.GetProperty("questionTargetToken").GetString() ?? throw new InvalidDataException("Missing first question target token.");
            var firstTextRef = firstQuery.GetProperty("questionAnswerRef").Deserialize<ArtifactReference>(ContractJson.Options)
                ?? throw new InvalidDataException("Missing first question answer text reference.");
            Assert(firstTextRef.Kind == "question.answer.text", "Initial query did not expose the derived text artifact.");
            var firstAnswer = await ReadTextArtifactAsync(jobRoot, firstTextRef);
            var firstResult = ReadResults(jobRoot).Single(result => result.Outputs.Any(output => output.Kind == "question.answer"));
            var firstStructuredRef = firstResult.Outputs.Single(output => output.Kind == "question.answer");
            using var firstStructured = await ReadJsonArtifactAsync(jobRoot, firstStructuredRef);
            Assert(firstStructured.RootElement.GetProperty("turns").GetArrayLength() == 1, "Initial structured answer does not contain exactly one turn.");
            Assert(firstStructured.RootElement.GetProperty("turns")[0].GetProperty("provenance").GetString() == "generated", "Initial answer provenance is not generated.");

            var followUpTurnRef = await PublishTextArtifactAsync(jobRoot, "commands/follow-up/question-turn", "question.turn.0002", FollowUpQuestion);
            const string followUpActionRef = "commands/follow-up/action.json";
            await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, followUpActionRef), new
            {
                schemaVersion = "knowledge-clipper-question-action.v2",
                action = "question.follow-up",
                targetToken = firstToken,
                turnIndex = 1,
                questionTurnRef = followUpTurnRef
            });
            await SendCommandAsync(pipe, "task.provide_input", taskId, followUpActionRef, "follow-up", "waiting_input");

            var secondQuery = await WaitStatusAsync(pipe, taskId, "waiting_input", firstToken);
            Assert(secondQuery.GetProperty("answerAvailable").GetBoolean(), "Follow-up answer was not available at waiting_input.");
            Assert(secondQuery.GetProperty("questionTurnCount").GetInt32() == 2, "Follow-up waiting_input did not preserve two ordered turns.");
            var secondToken = secondQuery.GetProperty("questionTargetToken").GetString() ?? throw new InvalidDataException("Missing second question target token.");
            Assert(secondToken != firstToken, "Follow-up did not rotate the opaque question token.");
            var secondTextRef = secondQuery.GetProperty("questionAnswerRef").Deserialize<ArtifactReference>(ContractJson.Options)
                ?? throw new InvalidDataException("Missing second question answer text reference.");
            var secondAnswer = await ReadTextArtifactAsync(jobRoot, secondTextRef);
            var answerResults = ReadResults(jobRoot).Where(result => result.Outputs.Any(output => output.Kind == "question.answer")).OrderBy(result => result.FinishedAt).ToArray();
            Assert(answerResults.Length == 2, "Follow-up did not append exactly one answer attempt.");
            var secondStructuredRef = answerResults[1].Outputs.Single(output => output.Kind == "question.answer");
            using var secondStructured = await ReadJsonArtifactAsync(jobRoot, secondStructuredRef);
            var turns = secondStructured.RootElement.GetProperty("turns");
            Assert(turns.GetArrayLength() == 2, "Follow-up structured answer does not contain two turns.");
            Assert(turns[0].GetProperty("provenance").GetString() == "persisted" && turns[1].GetProperty("provenance").GetString() == "generated", "Follow-up ancestry is not persisted then generated.");
            var firstBundleId = firstStructured.RootElement.GetProperty("answer_bundle_id").GetString();
            Assert(turns[0].GetProperty("artifact_ref").GetString() == firstBundleId, "Follow-up did not preserve the first module-local answer bundle identity.");
            Assert(turns[0].GetProperty("answer_text").GetString() == firstAnswer, "Follow-up changed the first answer text.");
            Assert(turns[1].GetProperty("question").GetString() == FollowUpQuestion, "Follow-up question was not preserved exactly.");
            var secondRequest = Directory.GetFiles(Path.Combine(jobRoot, "runs"), "request.json", SearchOption.AllDirectories)
                .Select(path => JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(path), ContractJson.Options)!)
                .Single(request => request.AttemptId == answerResults[1].AttemptId);
            Assert(secondRequest.InputRefs.Count == 1 && secondRequest.InputRefs[0].Kind == "content-input", "Core follow-up request did not consume exactly one derived content-input artifact.");
            using var followUpInputManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, secondRequest.InputRefs[0].ManifestPath)));
            var adapterAncestry = followUpInputManifest.RootElement.GetProperty("inputRefs").EnumerateArray().ToArray();
            Assert(adapterAncestry.Any(reference => reference.GetProperty("artifactRef").GetString() == firstStructuredRef.ArtifactRef && reference.GetProperty("expectedSha256").GetString() == firstStructuredRef.ExpectedSha256),
                "Core follow-up adapter did not bind the first immutable common artifact reference.");

            const string makeCardsActionRef = "commands/make-cards/action.json";
            await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, makeCardsActionRef), new
            {
                schemaVersion = "knowledge-clipper-question-action.v2",
                action = "question.make-cards",
                targetToken = secondToken
            });
            await SendCommandAsync(pipe, "task.provide_input", taskId, makeCardsActionRef, "make-cards", "waiting_input");
            var finalQuery = await WaitStatusAsync(pipe, taskId, "succeeded");
            var cardResult = ReadResults(jobRoot).Single(result => result.StageId == "question-make-cards");
            Assert(cardResult.Outputs.Any(output => output.Kind == "learning-bundle") && cardResult.Outputs.Any(output => output.Kind == "card-bundle"), "make-cards did not publish both neutral bundles.");
            Assert(!cardResult.Outputs.Any(output => output.Kind == "question.answer"), "make-cards incorrectly published a new answer artifact.");
            Assert(ReadResults(jobRoot).All(result => result.Worker.Id == "content.deepseek"), "A non-content worker was invoked.");
            var cardBundleRef = cardResult.Outputs.Single(output => output.Kind == "card-bundle");
            using var cardBundle = await ReadJsonArtifactAsync(jobRoot, cardBundleRef);
            var cards = cardBundle.RootElement.GetProperty("cards").EnumerateArray().Select(card => new
            {
                template = card.GetProperty("template").GetString(),
                front = JoinBlockText(card.GetProperty("front_blocks")),
                back = JoinBlockText(card.GetProperty("back_blocks"))
            }).ToArray();
            Assert(cards.Length > 0 && cards.All(card => !string.IsNullOrWhiteSpace(card.front) && !string.IsNullOrWhiteSpace(card.back)), "Final card bundle has an empty front/back.");

            var resultFiles = Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var attemptEvidence = new List<object>();
            foreach (var resultPath in resultFiles)
            {
                var requestPath = Path.Combine(Path.GetDirectoryName(resultPath)!, "request.json");
                var result = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(resultPath), ContractJson.Options)!;
                var learningRef = result.Outputs.Single(output => output.Kind == "learning-bundle");
                using var learning = await ReadJsonArtifactAsync(jobRoot, learningRef);
                var traces = learning.RootElement.GetProperty("ai_trace").EnumerateArray().Select(trace => new
                {
                    mode = trace.GetProperty("mode").GetString(),
                    provider = trace.GetProperty("provider").GetString(),
                    model = trace.GetProperty("model").GetString(),
                    attempts = trace.GetProperty("attempts").GetInt32(),
                    promptSha256 = trace.GetProperty("prompt_sha256").GetString(),
                    responseSha256 = trace.GetProperty("response_sha256").GetString()
                }).ToArray();
                Assert(traces.Length > 0 && traces.All(trace => trace.mode == "live" && trace.provider == "deepseek"), "Question attempt was not an actual live DeepSeek attempt.");
                attemptEvidence.Add(new
                {
                    result.AttemptId,
                    result.StageId,
                    operation = JsonDocument.Parse(await File.ReadAllBytesAsync(requestPath)).RootElement.GetProperty("operation").GetString(),
                    request = Evidence(projectRoot, requestPath),
                    result = Evidence(projectRoot, resultPath),
                    outputs = result.Outputs.Select(output => new { output.Kind, output.ManifestPath, output.ExpectedSha256 }).ToArray(),
                    providerTrace = traces
                });
            }

            var readablePath = Path.Combine(canaryRoot, "readable-question-workflow.md");
            var readable = new StringBuilder()
                .AppendLine("# authority v1.2.3 内容问题链：真实输入 → 最终卡片")
                .AppendLine()
                .AppendLine("- Core 状态序列：`waiting_input` → `waiting_input` → `succeeded`")
                .AppendLine("- provider：`live / deepseek`（合成非私密文本）")
                .AppendLine("- Exporter 调用：0")
                .AppendLine()
                .AppendLine("## 第 1 轮")
                .AppendLine()
                .AppendLine($"用户问题：{InitialQuestion}")
                .AppendLine()
                .AppendLine($"AI 回答：{firstAnswer}")
                .AppendLine()
                .AppendLine("## 第 2 轮")
                .AppendLine()
                .AppendLine($"用户追问：{FollowUpQuestion}")
                .AppendLine()
                .AppendLine($"AI 回答：{secondAnswer}")
                .AppendLine()
                .AppendLine("## 最终卡片");
            for (var index = 0; index < cards.Length; index++)
            {
                readable.AppendLine().AppendLine($"### 卡片 {index + 1} · `{cards[index].template}`")
                    .AppendLine().AppendLine($"Front：{cards[index].front}")
                    .AppendLine().AppendLine($"Back：{cards[index].back}");
            }
            await WriteOnceAsync(readablePath, readable.ToString());

            var receiptPath = Path.Combine(canaryRoot, "question-workflow-receipt.json");
            var receipt = new
            {
                schemaVersion = "knowledge-clipper-authority-content-question-canary.v1",
                status = "passed",
                completedAt = DateTimeOffset.UtcNow,
                authority = new
                {
                    checkpointId = "knowledge-clipper-authority-1.2.3-20260828T021600Z",
                    registry = Evidence(projectRoot, registryPath),
                    checkpoint = Evidence(projectRoot, checkpointPath),
                    releaseRoot,
                    worker = new { workerId = "content.deepseek", workerVersion = "0.5.2", executable = Evidence(projectRoot, workerPath) }
                },
                providerMode = "live",
                externalProvider = new { provider = "deepseek", transport = "https", endpoint = "https://api.deepseek.com/chat/completions", credentialRecorded = false },
                task = new { taskId, jobRoot, manifest = Evidence(projectRoot, Path.Combine(jobRoot, "task.manifest.v1.json")) },
                observed = new
                {
                    states = new[] { "waiting_input", "waiting_input", "succeeded" },
                    firstQuery,
                    secondQuery,
                    finalQuery,
                    initialQuestion = InitialQuestion,
                    initialAnswer = firstAnswer,
                    followUpQuestion = FollowUpQuestion,
                    followUpAnswer = secondAnswer,
                    orderedTurns = 2,
                    firstAnswerPreserved = true,
                    targetTokenRotated = true,
                    cards,
                    exporterResults = 0
                },
                attempts = attemptEvidence,
                readable = Evidence(projectRoot, readablePath)
            };
            await AtomicFile.WriteJsonOnceAsync(receiptPath, receipt);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", receiptPath, receiptSha256 = Hash(await File.ReadAllBytesAsync(receiptPath)), readablePath }, ContractJson.Options));
        }
        finally
        {
            shutdown.Cancel();
            try { await host; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> WriteQuestionTaskAsync(string jobsRoot, string taskId, string question)
    {
        var jobRoot = Path.Combine(jobsRoot, taskId);
        Directory.CreateDirectory(jobRoot);
        var questionBytes = Encoding.UTF8.GetBytes(question);
        const string questionPath = "inputs/question-turn-0001.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(PathPolicy.ResolveJobRelative(jobRoot, questionPath))!);
        await File.WriteAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, questionPath), questionBytes);
        const string captureArtifactRef = "capture.question.user.0001";
        var bundle = new
        {
            schemaVersion = "capture-bundle.v1",
            captureId = "capture-question-0001",
            createdAt = DateTimeOffset.UtcNow,
            intent = "question",
            source = "question",
            modeSnapshot = new { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "ocr" },
            artifacts = new[] { new { artifactRef = captureArtifactRef, relativePath = questionPath, sha256 = Hash(questionBytes), bytes = questionBytes.LongLength, mediaType = "text/plain", encoding = "utf-8" } },
            selectedTextArtifactRef = (string?)null,
            contextSegments = (object?)null,
            questionImages = Array.Empty<string>(),
            answerImages = Array.Empty<string>(),
            questionTurns = new[] { new { turnIndex = 1, role = "user", artifactRef = captureArtifactRef } }
        };
        const string bundlePath = "inputs/capture-bundle.v1.json";
        var bundleBytes = JsonSerializer.SerializeToUtf8Bytes(bundle, ContractJson.Options);
        await File.WriteAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, bundlePath), bundleBytes);
        const string bundleRef = "capture.bundle.question";
        var manifestRef = $"artifacts/{bundleRef}/artifact.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef), new
        {
            schemaVersion = "1.0.0",
            artifactId = bundleRef,
            kind = "capture-bundle",
            contractVersion = "1.0.0",
            mediaType = "application/vnd.rqc.capture-bundle+json",
            files = new[] { new { path = bundlePath, sha256 = Hash(bundleBytes), bytes = bundleBytes.LongLength, mediaType = "application/vnd.rqc.capture-bundle+json" } },
            createdAt = DateTimeOffset.UtcNow,
            producer = new { stageId = "capture", workerId = "desktop-capture", workerVersion = "1.0.0", attemptId = "capture-attempt-1" },
            inputRefs = Array.Empty<object>(),
            dataClass = "public",
            metadata = new { entrypoint = bundlePath, synthetic = true }
        });
        var manifestHash = Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef)));
        const string intentRef = "plans/desktop-intent.v1.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, intentRef), new { schemaVersion = "desktop-intent.v1", taskId, intent = "clip.question", inputArtifactRef = bundleRef, requestedOutputs = new[] { "question.answer" } });
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0",
            taskId,
            createdAt = DateTimeOffset.UtcNow,
            createdBy = new { application = "KnowledgeClipper.AuthorityContentCanary", version = "1.0.0" },
            intent = "clip.question",
            executionProfile = "cards-remnote",
            inputRefs = new[] { new { artifactRef = bundleRef, kind = "capture-bundle", contractVersion = "1.0.0", expectedSha256 = manifestHash } },
            requestedOutputs = new[] { "question.answer" },
            initialPlanRef = intentRef,
            retentionClass = "ephemeral"
        });
        return jobRoot;
    }

    private static async Task<ArtifactReference> PublishTextArtifactAsync(string jobRoot, string root, string artifactId, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var payloadRef = root + "/payload.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(PathPolicy.ResolveJobRelative(jobRoot, payloadRef))!);
        await File.WriteAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, payloadRef), bytes);
        var manifestRef = root + "/artifact.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef), new
        {
            schemaVersion = "1.0.0",
            artifactId,
            kind = "question.turn",
            contractVersion = "1.0.0",
            mediaType = "text/plain",
            files = new[] { new { path = payloadRef, sha256 = Hash(bytes), bytes = bytes.LongLength, mediaType = "text/plain" } },
            createdAt = DateTimeOffset.UtcNow,
            producer = new { stageId = "desktop-question", workerId = "desktop-capture", workerVersion = "1.0.0", attemptId = "question-turn-attempt-2" },
            inputRefs = Array.Empty<object>(),
            dataClass = "public",
            metadata = new { entrypoint = payloadRef, encoding = "utf-8", synthetic = true }
        });
        return new ArtifactReference(artifactId, "question.turn", "1.0.0", manifestRef, Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef))));
    }

    private static IReadOnlyList<WorkerResult> ReadResults(string jobRoot) => Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories)
        .Select(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)!).ToArray();

    private static async Task<string> ReadTextArtifactAsync(string jobRoot, ArtifactReference reference)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath)));
        Assert(Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath))) == reference.ExpectedSha256, "Text artifact manifest hash mismatch.");
        var file = manifest.RootElement.GetProperty("files")[0];
        var bytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, file.GetProperty("path").GetString()!));
        Assert(bytes.LongLength == file.GetProperty("bytes").GetInt64() && Hash(bytes) == file.GetProperty("sha256").GetString(), "Text artifact payload hash mismatch.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static async Task<JsonDocument> ReadJsonArtifactAsync(string jobRoot, ArtifactReference reference)
    {
        var manifestBytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath));
        Assert(Hash(manifestBytes) == reference.ExpectedSha256, $"Artifact manifest hash mismatch for {reference.ArtifactRef}.");
        using var manifest = JsonDocument.Parse(manifestBytes);
        var file = manifest.RootElement.GetProperty("files")[0];
        var payload = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, file.GetProperty("path").GetString()!));
        Assert(payload.LongLength == file.GetProperty("bytes").GetInt64() && Hash(payload) == file.GetProperty("sha256").GetString(), $"Artifact payload hash mismatch for {reference.ArtifactRef}.");
        return JsonDocument.Parse(payload);
    }

    private static async Task<JsonElement> SendCommandAsync(string pipe, string type, string? taskId, string payloadRef, string suffix, string? expectedState)
    {
        var payload = new
        {
            schemaVersion = "1.0.0",
            commandId = "command-authority-" + suffix,
            operationId = "operation-authority-" + suffix,
            taskId,
            type,
            issuedAt = DateTimeOffset.UtcNow,
            idempotencyKey = Hash(Encoding.UTF8.GetBytes("authority-content-" + suffix)),
            payloadRef,
            expectedState
        };
        using var response = JsonDocument.Parse(await RoundTripAsync(pipe, new { protocolVersion = "1.0", correlationId = "correlation-authority-" + suffix, messageType = "task-command", payload }));
        Assert(response.RootElement.GetProperty("ok").GetBoolean(), $"Command {suffix} returned a protocol error.");
        return response.RootElement.GetProperty("payload").Clone();
    }

    private static async Task<JsonElement> WaitStatusAsync(string pipe, string taskId, string expected, string? excludedToken = null)
    {
        for (var index = 0; index < 1200; index++)
        {
            using var response = JsonDocument.Parse(await RoundTripAsync(pipe, new
            {
                protocolVersion = "1.0",
                correlationId = $"query-authority-{index}",
                messageType = "task-query",
                payload = new { schemaVersion = "2.2.0", queryId = $"query-authority-{index}", type = "task.get", taskId }
            }));
            var items = response.RootElement.GetProperty("payload").GetProperty("items");
            if (items.GetArrayLength() == 1 && items[0].GetProperty("status").GetString() == expected)
            {
                var item = items[0];
                if (excludedToken is null || item.GetProperty("questionTargetToken").GetString() != excludedToken) return item.Clone();
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Task {taskId} did not reach {expected}.");
    }

    private static async Task<string> RoundTripAsync(string pipe, object value)
    {
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(value, IpcJson.Options) + "\n");
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, true);
        return await reader.ReadLineAsync() ?? throw new InvalidDataException("No IPC response.");
    }

    private static object Evidence(string projectRoot, string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new { path = Path.GetRelativePath(projectRoot, path).Replace('\\', '/'), absolutePath = path, bytes = bytes.LongLength, sha256 = Hash(bytes) };
    }

    private static string JoinBlockText(JsonElement blocks) => string.Join("\n", blocks.EnumerateArray().Select(block => block.GetProperty("text").GetString()));

    private static async Task WriteOnceAsync(string path, string content)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
    }

    private static IpcConfiguration Ipc(string pipe) => new("1.0.0", "windows-named-pipe", pipe, $"\\\\.\\pipe\\{pipe}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query" }, true, "job-event-sequence", 1_048_576, new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"), "../schemas/common/ipc-request.schema.json", "../schemas/common/ipc-response.schema.json", "respond-when-possible-then-close");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
