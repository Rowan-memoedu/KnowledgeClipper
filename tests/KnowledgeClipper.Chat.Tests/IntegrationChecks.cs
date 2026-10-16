using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class IntegrationChecks
{
    internal static async Task Run(string repo, string output, Action<bool, string> check)
    {
        var root = new ChatSession { Messages = [
            new() { Id = "rectangle-question", Role = "user", Parts = [new("text", "矩形长7、宽4，求面积。\n另说明面积单位。")] },
            new() { Id = "rectangle-answer", Role = "assistant", Parts = [new("text", "面积为 $7\\times4=28$ 平方厘米。")] }
        ] };
        var branch = ChatWorkspace.New(root);
        branch.Session.Messages.Add(new() { Id = "branch-follow-up", Role = "assistant", Parts = [new("text", "长度单位改变时，面积单位也需平方。")] });
        var empty = new CardComposition(); root.Compositions.Add(empty);
        var proposal = ChatComposition.Begin(root, empty, "integrate", "", ["fast-answer"], contextSession: branch.Session);
        using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
        check(snapshot.RootElement.GetProperty("context").GetArrayLength() == 3,
            "空白卡面整合默认读取全部 Tab 的真实对话，而非要求先分配正反面");
        check(snapshot.RootElement.GetProperty("contextScope").GetString() == "all-conversations" &&
            snapshot.RootElement.GetProperty("requestedCardCount").ValueKind == JsonValueKind.Null,
            "未指定数量时冻结全部对话模式，交给模型按知识目标判断张数");
        var fork = ChatWorkspace.Fork(root, "branch-follow-up");
        fork.Session.Messages.Add(new() { Id = "independent-goal", Parts = [new("text", "另一个目标：说明勾股定理的条件。")] });
        var blankTab = ChatWorkspace.New(root);
        var all = ChatComposition.Begin(root, empty, "integrate", "", ["fast-answer"], contextSession: blankTab.Session);
        using (var input = JsonDocument.Parse(all.InputSnapshot))
            check(input.RootElement.GetProperty("context").GetArrayLength() == 4 && input.RootElement.GetProperty("conversations").GetArrayLength() == 4,
                "当前 Tab 为空也可整合其他对话，分叉共享前缀按原消息 ID 去重");
        ChatComposition.Select(root, empty, "rectangle-question", "front", true);
        ChatComposition.Select(root, empty, "branch-follow-up", "back", true);
        ChatComposition.Edit(empty, empty.Front + "\n手工补充：保留单位条件。", empty.Back, "fast-answer");
        var focused = ChatComposition.Begin(root, empty, "integrate", "请生成三张卡片", ["fast-answer"]);
        using (var input = JsonDocument.Parse(focused.InputSnapshot))
            check(input.RootElement.GetProperty("contextScope").GetString() == "selected-and-draft" &&
                input.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("id").GetString()).SequenceEqual(["rectangle-question", "branch-follow-up"]) &&
                input.RootElement.GetProperty("context").GetArrayLength() == 4 && input.RootElement.GetProperty("front").GetString()!.Contains("手工补充"),
                "跨 Tab 选材和手工输入作为重点，保留完整参考对话及选取顺序");
        foreach (var faces in new[] { ("仅手写问题", ""), ("", "仅手写答案"), ("手写问题", "手写答案") })
        {
            var manual = ChatComposition.Begin(root, new() { Front = faces.Item1, Back = faces.Item2 }, "integrate", "", ["fast-answer"]);
            using var input = JsonDocument.Parse(manual.InputSnapshot);
            check(input.RootElement.GetProperty("contextScope").GetString() == "selected-and-draft" && input.RootElement.GetProperty("context").GetArrayLength() == 4,
                "单面或双面手工稿均可结合参考对话整合：" + faces);
        }
        var imagePath = Path.Combine(output, "integration-public.png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
        var image = new ImageAsset(imagePath, ImageAsset.Hash(File.ReadAllBytes(imagePath)), "question", 1); root.Assets.Add(image);
        root.Messages[0].Parts.Add(new("image", Image: image)); fork.Session.Messages.Last().Parts.Add(new("image", Image: image));
        var illustrated = ChatComposition.Begin(root, new(), "integrate", "", ["fast-answer"]);
        check(ChatComposition.ProposalImages(illustrated, root).Single().Sha256 == image.Sha256,
            "跨 Tab 参考对话中的真实图片按哈希去重并作为实际附件传入");
        foreach (var (text, expected) in new (string, int?)[] {
            ("生成3张卡片",3),("请整合成两张卡",2),("生成 ３ 张卡片",3),("Generate exactly 4 flashcards",4),
            ("20张卡片",20),("最多3张卡片",null),("不要按每条消息生成一张卡",null),
            ("第二张卡片保留单位",null),("生成2张卡，而不是一张卡",2),("按情况决定数量",null),
            ("不限定只生成一张卡",null),("请至少生成三张卡",null),("请生成三张",3),("生成2张。",2) })
        {
            using var input = JsonDocument.Parse(ChatComposition.Begin(root, new() { Front = "材料" }, "integrate", text, ["fast-answer"]).InputSnapshot);
            var value = input.RootElement.GetProperty("requestedCardCount");
            check((value.ValueKind == JsonValueKind.Null ? null : value.GetInt32()) == expected, "显式数量、中文/全角及非精确要求区分：" + text);
        }
        void Reject(Action run, string label) { try { run(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { check(true, label); return; } throw new Exception(label); }
        foreach (var requirement in new[] { "生成0张卡", "生成二十一张卡", "生成2张卡，再生成3张卡" })
            Reject(() => ChatComposition.Begin(root, empty, "integrate", requirement, ["fast-answer"]), "无效或冲突数量发送前拒绝并保留原稿：" + requirement);

        var frozen = new CardComposition { Front = "已导入原卡", ExportDraftId = "retained-export" };
        var tail = new CardComposition { Front = "已有后续卡" };
        root.Compositions = [frozen, empty, tail]; empty.Annotation = "保留批注"; empty.AnnotationEnabled = true;
        var original = JsonSerializer.Serialize(empty, WorkflowJson.Options);
        ChatComposition.Parse(focused, Cards(2), root, ["fast-answer"]);
        check(focused.Status == "unparsed" && focused.Error!.Contains("指定 3 张") && focused.RawOutput == Cards(2) &&
            JsonSerializer.Serialize(empty, WorkflowJson.Options) == original, "返回数量不符拒绝采用，显示具体错误并完整保留稿件和回复");
        foreach (var invalidOutput in new[] { "{\"cards\":[{\"front\":\"问题\",\"back\":\"\",\"typeId\":\"fast-answer\"}]}",
            "{\"cards\":[{\"front\":\"重复问题\",\"back\":\"重复答案\",\"typeId\":\"fast-answer\"},{\"front\":\"重复问题\",\"back\":\"重复答案\",\"typeId\":\"fast-answer\"}]}" })
        {
            var incomplete = ChatComposition.Begin(root, empty, "integrate", "", ["fast-answer"]);
            ChatComposition.Parse(incomplete, invalidOutput, root, ["fast-answer"]);
            check(incomplete.Status == "unparsed" && JsonSerializer.Serialize(empty, WorkflowJson.Options) == original,
                "空答案或重复卡片不能冒充完整多卡整合，原稿和失败回复保留");
        }
        var valid = ChatComposition.Begin(root, empty, "integrate", "请生成三张卡片", ["fast-answer"]);
        ChatComposition.Parse(valid, Cards(3), root, ["fast-answer"]); ChatComposition.Adopt(root, valid);
        check(root.Compositions.Count == 5 && root.Compositions[0] == frozen && root.Compositions[1] == empty && root.Compositions[4] == tail &&
            root.Compositions.Skip(1).Take(3).Select(c => c.Front).SequenceEqual(["问题 1", "问题 2", "问题 3"]),
            "生成多卡紧接原卡按模型顺序插入，不改其他卡或冻结导出身份");
        check(root.Compositions.Skip(1).Take(3).Select(c => c.Id).Distinct().Count() == 3 &&
            root.Compositions.Skip(2).Take(2).All(c => c.ExportDraftId == null && c.TypeId == "fast-answer" && c.AnnotationEnabled && c.FrontSources.SequenceEqual(empty.FrontSources)) &&
            !ReferenceEquals(root.Compositions[2], valid.Cards[1]),
            "新增卡拥有独立可导出身份，并兼容原类型、批注开关和跨 Tab 来源");
        check(empty.Annotation == "保留批注", "整合多卡保留原卡双面批注合同");
        var addedId = root.Compositions[2].Id;
        ChatComposition.Undo(empty, root);
        check(root.Compositions.SequenceEqual([frozen, empty, tail]) && empty.Front.Contains("手工补充"), "一次撤销完整恢复原稿及原卡片顺序");
        var restored = JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(root, WorkflowJson.Options), WorkflowJson.Options)!;
        var restoredOriginal = restored.Compositions[1]; ChatComposition.Redo(restoredOriginal, restored);
        check(restored.Compositions.Count == 5 && restored.Compositions[2].Id == addedId && restored.Compositions[4].Id == tail.Id,
            "重开和重做恢复原多卡身份、位置、设置与内容");
        ChatComposition.Edit(restored.Compositions.Single(c => c.Id == addedId), "用户后续编辑", "答案", "fast-answer");
        check(valid.Cards[1].Front == "问题 2", "用户编辑新增卡不反向篡改原模型处理记录");
        Reject(() => ChatComposition.Undo(restoredOriginal, restored), "新增卡被编辑后拒绝整批撤销，保护后续内容");

        await RetryChecks(repo, output, check);
    }

    internal static string Cards(int count) => JsonSerializer.Serialize(new { cards = Enumerable.Range(1, count)
        .Select(i => new { front = "问题 " + i, back = "答案 " + i, typeId = "fast-answer" }) });

    private static async Task RetryChecks(string repo, string output, Action<bool, string> check)
    {
        var root = new ChatSession { Messages = [new() { Parts = [new("text", "主对话保持原样")] }], Auxiliary = new() };
        var session = root.Auxiliary.Current; session.Model = "fixture"; session.Effort = "high";
        var material = new CaptureMaterial("公开 API 辅助重试", null, "generated_allowed", null, "public", 0, DateTimeOffset.UtcNow);
        var data = Path.Combine(output, "integration-retry"); var store = new DraftStore(data);
        var draft = new ClipDraft { Original = material, Material = material, Settings = new(), Chat = root,
            Configuration = ModeConfigurationLoader.Load(Path.Combine(repo, "modes")) };
        var config = new ChatConfigurationLoader(Path.Combine(repo, "modes", "chat"), Path.Combine(data, "modes"));
        using var handler = new CancelledThenCompleted(); using var http = new HttpClient(handler);
        var catalog = new[] { new ChatModel("fixture", "Fixture", ["high"], ["text"]) };
        var first = new ChatCoordinator(draft, store, config, new("unused", data), http, () => "fixture", data, session);
        using var cancellation = new CancellationTokenSource();
        var sending = first.SendAsync(catalog, "integrate", "原始整合材料", ct: cancellation.Token);
        await handler.Entered.Task; cancellation.Cancel();
        try { await sending; } catch (OperationCanceledException) { }
        check(!first.Busy && session.Binding.NeedsReconciliation && session.Prompts.Single().Stage == "unknown", "API 辅助取消保存 Unknown 并释放实际运行状态");
        var turnId = session.Binding.TurnId; var promptId = session.Prompts.Single().Id;
        var reloaded = store.Find(draft.Id)!.Chat!;
        check(reloaded.Auxiliary!.Current.Binding.TurnId == turnId && handler.Requests == 1 && reloaded.Auxiliary.PreviousAttempts.Count == 0,
            "重开未知辅助请求只恢复原身份，没有自动重发");
        check(root.Auxiliary.PrepareExplicitRequest() && handler.Requests == 1, "用户明确再次生成时准备新辅助请求，准备本身不调用模型");
        var retry = root.Auxiliary.Current;
        check(root.Auxiliary.PreviousAttempts.Single() == session && session.Prompts.Single().Id == promptId && session.Binding.TurnId == turnId && session.Binding.Stage == "unknown" &&
            retry.Model == "fixture" && retry.Effort == "high", "旧不确定身份和输入完整保留，新的请求复用服务模型与推理设置");
        var next = new ChatCoordinator(draft, store, config, new("unused", data), http, () => "fixture", data, retry);
        await next.SendAsync(catalog, "integrate", "当前整合材料");
        check(retry.Binding.Stage == "completed" && retry.Binding.TurnId != turnId && handler.Requests == 2 && root.Messages.Single().Text == "主对话保持原样",
            "明确重试可成功完成新辅助操作，主对话及旧失败状态不变");
        check(!root.Auxiliary.PrepareExplicitRequest() && root.Auxiliary.PreviousAttempts.Count == 1, "完成的辅助会话不会被误归档或触发请求");
        await next.SendAsync(catalog, "integrate", "不应混入历史的最新材料");
        check(handler.LastBody.GetProperty("messages").GetArrayLength() == 2 && !handler.LastBody.ToString().Contains("当前整合材料"),
            "每次 API 整合只使用当前冻结快照，不把旧辅助操作的材料和答案混入上下文");
        store.Save(draft); reloaded = store.Find(draft.Id)!.Chat!;
        check(reloaded.Auxiliary!.PreviousAttempts.Single().Binding.TurnId == turnId && reloaded.Auxiliary.Current.Binding.Stage == "completed", "关闭重开后同时保留失败历史和成功重试状态");
        root.Auxiliary.Provider = "codex"; var native = root.Auxiliary.Current; native.Binding.ThreadId = "original-native-thread"; native.Binding.Stage = "unknown";
        try { root.Auxiliary.PrepareExplicitRequest(); throw new Exception("Native identity replaced"); }
        catch (InvalidOperationException) { check(native.Binding.ThreadId == "original-native-thread" && root.Auxiliary.PreviousAttempts.Count == 1, "Codex 未知回合继续要求精确核对，不借重试重建原生身份"); }
    }

    private sealed class CancelledThenCompleted : HttpMessageHandler
    {
        internal int Requests; internal JsonElement LastBody;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            if (++Requests == 1) { Entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var stream = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Cards(2) }, finish_reason = "stop" } } }) + "\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
