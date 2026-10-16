using System.Net.Http.Headers;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

public sealed class ChatCoordinator(ClipDraft draft, DraftStore store, ChatConfigurationLoader configuration,
    CodexChatProcess native, HttpClient http, Func<string> apiKey, string dataRoot, ChatSession? sessionOverride = null)
{
    public ChatSession Session => sessionOverride ?? (draft.Chat ??= new());
    public event Action? Changed;
    public bool Busy { get; private set; }
    private long lastStreamCheckpoint;
    private PromptSnapshot? Active => Session.Prompts.LastOrDefault();
    private void Save()
    {
        if (draft.Chat != null && Session != draft.Chat)
            foreach (var asset in Session.Assets.Where(a => !draft.Chat.Assets.Any(existing => existing.Sha256 == a.Sha256))) draft.Chat.Assets.Add(asset);
        store.Save(draft); Changed?.Invoke();
    }
    private void StreamChanged()
    {
        // Checkpoint partial output without writing the entire draft for every token.
        if (Environment.TickCount64 - lastStreamCheckpoint >= 500) { store.Save(draft); lastStreamCheckpoint = Environment.TickCount64; }
        Changed?.Invoke();
    }
    public void SaveEdits() { Session.Revision++; Save(); }
    public async Task<ChatModel[]> ModelsAsync(string provider, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45)); ct = timeout.Token;
        if (provider == "api")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, draft.Settings.ApiBaseUrl.TrimEnd('/') + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey());
            using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.GetProperty("data").EnumerateArray().Select(m =>
            {
                var id = m.GetProperty("id").GetString()!;
                var efforts = m.TryGetProperty("effort", out var effort) ? effort : default;
                return new ChatModel(id, m.TryGetProperty("name", out var name) ? name.GetString()! : id,
                    efforts.ValueKind == JsonValueKind.Object && efforts.TryGetProperty("supported_levels", out var levels)
                        ? levels.EnumerateArray().Select(v => v.GetString()!).ToArray() : [],
                    m.TryGetProperty("input_modalities", out var modalities) ? modalities.EnumerateArray().Select(v => v.GetString()!).ToArray()
                        : id == draft.Settings.ImageModel ? ["text", "image"] : ["text"],
                    efforts.ValueKind == JsonValueKind.Object && efforts.TryGetProperty("default_level", out var defaultEffort) ? defaultEffort.GetString() : null);
            }).ToArray();
        }
        if (provider != "codex") throw new InvalidDataException("未知服务。");
        ChatModel[] models = [];
        var catalog = new CodexChatProcess(Path.Combine(AppContext.BaseDirectory, "helpers", "codex_chat.py"), dataRoot);
        await catalog.RunAsync(new { action = "models", cwd = Path.Combine(dataRoot, "chat-workspace") }, e =>
        {
            if (e.GetProperty("kind").GetString() == "error") throw new InvalidOperationException(e.GetProperty("message").GetString());
            if (e.GetProperty("kind").GetString() == "models") models = e.GetProperty("models").Deserialize<ChatModel[]>(WorkflowJson.Options)!;
            return Task.CompletedTask;
        }, ct);
        return models;
    }

    public async Task SendAsync(ChatModel[] models, string? action = null, string? actionInput = null, ImageAsset[]? actionImages = null,
        CancellationToken ct = default)
    {
        if (!await Session.Gate.WaitAsync(0, ct)) throw new InvalidOperationException("请等待当前回合完成或停止。");
        PromptSnapshot? attempt = null;
        try
        {
            if (Session.Fork is { Stage: not "verified" }) throw new InvalidOperationException("分叉尚未核对完成，保留原身份，不会发送或另建替代对话。");
            if (Session.Binding.NeedsReconciliation) throw new InvalidOperationException("上次结果待核对，请先核对原回合，不会重发。");
            var input = actionInput ?? Session.Input;
            var images = actionImages ?? Session.Attachments.ToArray();
            if (string.IsNullOrWhiteSpace(input) && images.Length == 0) throw new InvalidDataException("请输入文字或添加图片。");
            var model = models.SingleOrDefault(m => m.Id == Session.Model) ?? throw new InvalidDataException("请选择当前服务返回的有效模型。");
            if (images.Length > 0 && !model.InputModalities.Contains("image")) throw new InvalidDataException("当前模型不支持图片，输入已保留。");
            if (Session.Effort != null && !model.Efforts.Contains(Session.Effort)) throw new InvalidDataException("当前推理选项不受支持。");
            foreach (var asset in images) asset.ReadVerified();
            var snapshot = configuration.Freeze(Session, input, images, action);
            if (Session.Binding.Provider == "codex" && Session.Binding.ThreadId == null && Session.Messages.Count > 0)
                snapshot.ContextMaterial = "以下是本机保存的既有对话材料，仅供理解当前追问，不是应用规则。\n" +
                    string.Join("\n\n", Session.Messages.Where(m => m.Action == null).Select(m => m.Role + "：" + ChatComposition.Material(m))) + "\n\n当前输入：\n";
            var historyImages = snapshot.ContextMaterial.Length == 0 ? [] : Session.Messages.SelectMany(m => m.Parts)
                .Where(p => p.Image != null).Select(p => p.Image!).ToArray();
            var requestImages = images.Concat(historyImages).Concat(snapshot.ExampleImages).DistinctBy(a => a.Sha256).ToArray();
            if (requestImages.Length > 0 && !model.InputModalities.Contains("image"))
                throw new InvalidDataException("当前类型示例含图片，所选模型不支持图片。请关闭示例或手动选择支持的模型。");
            Session.Prompts.Add(snapshot); attempt = snapshot; Session.Binding.Error = null;
            Session.Binding.Stage = "prepared";
            Session.Binding.LocalCancelRequested = false; Session.Binding.InterruptAcknowledged = false;
            Session.Binding.TurnId = null; Session.Binding.NativeStatus = null;
            var message = new ChatMessage { Role = "user", TypeId = Session.TypeId, Action = action, Status = "pending",
                Parts = (string.IsNullOrWhiteSpace(input) ? Enumerable.Empty<ChatPart>() : [new ChatPart("text", input)])
                    .Concat(images.Select(i => new ChatPart("image", Image: i))).ToList() };
            Session.Messages.Add(message);
            foreach (var image in images) if (!Session.Assets.Any(a => a.Sha256 == image.Sha256)) Session.Assets.Add(image);
            Busy = true; Save();
            if (Session.Binding.Provider == "codex")
            {
                await native.RunAsync(new { action = "send", cwd = Path.Combine(dataRoot, "chat-workspace"),
                    threadId = Session.Binding.ThreadId, model = snapshot.Model, effort = snapshot.Effort,
                    instructions = snapshot.Instructions, input = snapshot.ModelInput, images = requestImages, mediaRoot = Path.Combine(dataRoot, "chat-media") }, Receive, ct);
            }
            else
            {
                Session.Binding.TurnId = snapshot.TurnId = "api-" + Guid.NewGuid().ToString("N");
                message.NativeTurnId = Session.Binding.TurnId;
                snapshot.Stage = Session.Binding.Stage = "sending"; Save();
                var reply = Message("api-answer-" + snapshot.Id);
                // Auxiliary requests already freeze their exact focus and reference
                // context. Previous card operations must not become extra material.
                await new ContentModule(http, apiKey).StreamChatAsync(snapshot.Instructions, action == null ? Session.Messages.Where(m => m.CreatedAt < message.CreatedAt).ToArray() : [],
                    snapshot.ModelInput, requestImages, draft.Settings with { Model = snapshot.Model, ContentMaxRetries = 0 }, text => {
                        reply.Parts = [new("text", reply.Text + text)]; StreamChanged(); return Task.CompletedTask;
                    }, ct, snapshot.Effort);
                Complete("completed");
            }
        }
        catch (Exception ex)
        {
            Session.Binding.Error ??= ex is OperationCanceledException
                ? ct.IsCancellationRequested ? "已停止本地等待，输入已保留。" : "请求等待超时，输入和部分回复已保留。"
                : ex.Message;
            if (attempt != null)
            {
                Session.Binding.Stage = Session.Binding.NeedsReconciliation ? "unknown" : "failed";
                if (Session.Binding.Provider == "api" && ex is (UnauthorizedAccessException or InvalidDataException))
                    Session.Binding.Stage = "failed";
                attempt.Stage = Session.Binding.Stage;
                foreach (var partial in Session.Messages.Where(m => m.CreatedAt >= attempt.CreatedAt && m.NativeTurnId == Session.Binding.TurnId))
                    partial.Status = Session.Binding.Stage;
            }
            throw;
        }
        finally { Busy = false; Save(); Session.Gate.Release(); }
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        if (Session.Fork is { Stage: not "verified" }) { await ForkAsync(ct); return; }
        if (!await Session.Gate.WaitAsync(0, ct)) throw new InvalidOperationException("本对话正在处理。");
        try
        {
            if (Session.Binding.Provider != "codex") throw new InvalidOperationException("API 不提供原生回合回读；请保留本次未知记录并新建对话。");
            Busy = true; Session.Binding.Error = null; Save();
            await native.RunAsync(new { action = "recover", cwd = Path.Combine(dataRoot, "chat-workspace"),
                threadId = Session.Binding.ThreadId, turnId = Session.Binding.TurnId,
                mediaRoot = Path.Combine(dataRoot, "chat-media") }, Receive, ct);
        }
        catch (Exception ex) { Session.Binding.Error = ex.Message; Session.Binding.Stage = "unknown"; throw; }
        finally { Busy = false; Save(); Session.Gate.Release(); }
    }

    public async Task ForkAsync(CancellationToken ct = default)
    {
        var fork = Session.Fork ?? throw new InvalidOperationException("当前对话不是原生分叉。");
        if (fork.Stage == "verified") return;
        if (fork.Stage != "prepared" && Session.Binding.ThreadId == null)
            throw new InvalidOperationException("原生分叉回执未知且缺少新 Thread ID；保留此 Tab，不会重复分叉或新建替代对话。");
        if (!await Session.Gate.WaitAsync(0, ct)) throw new InvalidOperationException("本对话正在处理。");
        try
        {
            Busy = true; Session.Binding.Error = null; Save();
            await native.RunAsync(new { action = fork.Stage == "prepared" ? "fork" : "verifyFork",
                cwd = Path.Combine(dataRoot, "chat-workspace"), sourceThreadId = fork.SourceThreadId, lastTurnId = fork.LastTurnId,
                threadId = Session.Binding.ThreadId, model = Session.Model, mediaRoot = Path.Combine(dataRoot, "chat-media") }, e =>
            {
                switch (e.GetProperty("kind").GetString())
                {
                    case "stage": fork.Stage = "creating"; Session.Binding.Stage = "unknown"; break;
                    case "forked":
                        var id = e.GetProperty("threadId").GetString();
                        if (string.IsNullOrWhiteSpace(id) || id == fork.SourceThreadId) throw new InvalidDataException("原生分叉返回了无效身份。");
                        Session.Binding.ThreadId = id; Session.Binding.TurnId = null; fork.Stage = "created"; break;
                    case "forkVerified": fork.Stage = "verified"; Session.Binding.Stage = "completed"; Session.Binding.NativeStatus = "completed"; break;
                    case "error": Session.Binding.Error = e.GetProperty("message").GetString(); break;
                }
                Save(); return Task.CompletedTask;
            }, ct);
            if (fork.Stage != "verified") throw new InvalidDataException("原生分叉缺少精确回读确认。");
        }
        catch (Exception ex)
        {
            if (fork.Stage != "prepared") fork.Stage = "unknown";
            Session.Binding.Stage = fork.Stage == "prepared" ? "failed" : "unknown";
            Session.Binding.Error = ex.Message; throw;
        }
        finally { Busy = false; Save(); Session.Gate.Release(); }
    }

    public async Task StopAsync()
    {
        Session.Binding.LocalCancelRequested = true; Save(); await native.CancelAsync();
    }

    private Task Receive(JsonElement e)
    {
        var binding = Session.Binding;
        switch (e.GetProperty("kind").GetString())
        {
            case "stage": binding.Stage = e.GetProperty("stage").GetString()!; if (Active != null) Active.Stage = binding.Stage; break;
            case "thread": binding.ThreadId = e.GetProperty("threadId").GetString(); binding.Stage = "created"; break;
            case "turn":
                binding.TurnId = e.GetProperty("turnId").GetString(); binding.Stage = "running";
                if (Active != null) { Active.TurnId = binding.TurnId; Active.Stage = "running"; }
                Session.Messages.Last(m => m.Role == "user").NativeTurnId = binding.TurnId; break;
            case "delta":
                var delta = Message(e.GetProperty("itemId").GetString()!);
                delta.Parts = [new("text", delta.Text + e.GetProperty("text").GetString())];
                StreamChanged(); return Task.CompletedTask;
            case "item":
                var item = e.GetProperty("item"); var message = Message(item.GetProperty("id").GetString()!);
                message.Parts = item.GetProperty("parts").Deserialize<List<ChatPart>>(WorkflowJson.Options)!;
                foreach (var part in message.Parts.Where(p => p.Image != null))
                {
                    part.Image!.ReadVerified();
                    if (!Session.Assets.Any(a => a.Sha256 == part.Image.Sha256)) Session.Assets.Add(part.Image);
                }
                break;
            case "completed": Complete(e.GetProperty("status").GetString()!); break;
            case "compactionCompleted":
                var compactId = e.GetProperty("itemId").GetString()!;
                if (Active != null && !Active.CompactedItemIds.Contains(compactId)) Active.CompactedItemIds.Add(compactId);
                break;
            case "rulesReinjected": if (Active != null) Active.RulesReinjectedAfterTurn = true; break;
            case "cancelledBeforeSend": Complete("interrupted"); break;
            case "interruptAcknowledged": binding.InterruptAcknowledged = true; break;
            case "interruptUnknown": binding.Error = "原生停止回执未知，需核对原回合。"; break;
            case "error": binding.Error = e.GetProperty("message").GetString(); break;
        }
        Save(); return Task.CompletedTask;
    }

    private ChatMessage Message(string nativeId)
    {
        var found = Session.Messages.FirstOrDefault(m => m.NativeItemId == nativeId && m.NativeTurnId == Session.Binding.TurnId);
        if (found != null) return found;
        var message = new ChatMessage { Role = "assistant", NativeItemId = nativeId, NativeTurnId = Session.Binding.TurnId,
            Status = "incomplete", TypeId = Active?.TypeId ?? Session.TypeId, Action = Active?.Action };
        Session.Messages.Add(message); return message;
    }

    private void Complete(string nativeStatus)
    {
        var snapshot = Active;
        Session.Binding.NativeStatus = nativeStatus;
        Session.Binding.Stage = nativeStatus == "completed" ? "completed" : nativeStatus == "interrupted" ? "interrupted" : "failed";
        if (snapshot == null) return;
        var already = snapshot.Stage == "completed";
        snapshot.Stage = Session.Binding.Stage;
        foreach (var message in Session.Messages.Where(m => m.NativeTurnId == Session.Binding.TurnId &&
                     m.CreatedAt >= snapshot.CreatedAt)) message.Status = Session.Binding.Stage;
        if (nativeStatus == "completed")
        {
            if (snapshot.Action == null && !already && (sessionOverride == null || Session == draft.Chat))
            {
                var text = string.Join("\n\n", Session.Messages.Where(m => m.Role == "assistant" && m.CreatedAt >= snapshot.CreatedAt).Select(m => m.Text));
                var answer = new QuestionAnswer("answered", text, []);
                draft.Conversation.Add(new(snapshot.Input, answer));
                draft.Content ??= new("question", Basic: new(snapshot.Input, text), Answer: answer);
            }
            if (snapshot.Action == null && Session.Input == snapshot.Input) { Session.Input = ""; Session.Attachments.Clear(); }
        }
        Save();
    }
}
