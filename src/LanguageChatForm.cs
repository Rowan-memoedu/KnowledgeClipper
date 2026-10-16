using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KnowledgeClipper;

internal sealed class LanguageChatForm : Form
{
    internal ClipDraft Draft { get; }
    private readonly DraftStore store;
    private readonly string dataRoot, baseRoot;
    private readonly ChatConfigurationLoader config;
    private ChatCoordinator coordinator;
    private readonly Dictionary<ChatSession, ChatCoordinator> conversations = new();
    private readonly Dictionary<string, int> conversationPages = new();
    private ChatCoordinator auxiliaryCoordinator, activeRequest;
    private readonly Func<string> apiKey;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Fill, Text = "正在加载本机问答界面…", TextAlign = ContentAlignment.MiddleCenter };
    private readonly System.Windows.Forms.Timer updates = new() { Interval = 80 };
    private readonly Action newConversation;
    private ChatModel[] models = [];
    private ChatModel[] auxiliaryModels = [];
    private int auxiliaryModelGeneration;
    private bool auxiliaryModelsLoading;
    private string auxiliaryError;
    private bool Busy => activeRequest != null || conversations.Values.Any(c => c.Busy) || auxiliaryCoordinator.Busy;
    private int visibleCount = 20;
    private string error;
    private bool dirty, ready, closeRequested;
    private int modelGeneration;
    private bool initialSendPending;
    internal void RequestInitialSend() => initialSendPending = Draft.Chat.Prompts.Count == 0;
    private object cardPreview;
    private readonly Dictionary<string, (string Raw, string Display, ChatCopyMath[] CopyMath)> displayCache = new();
    private readonly Dictionary<string, (long Length, DateTime Modified, string Status)> exportStatusCache = new();
    private static readonly JsonSerializerOptions ViewJson = new(WorkflowJson.Options) { WriteIndented = false };
    internal Action<string> WriteClipboardText { get; set; } = text => Clipboard.SetText(text, TextDataFormat.UnicodeText);
    private CancellationTokenSource requestCancellation;
    internal event Action<ClipDraft> Saved;
    internal ChatCoordinator Coordinator => coordinator;

    internal LanguageChatForm(ClipDraft draft, DraftStore store, string dataRoot, string baseRoot, Func<string> apiKey, Action newConversation)
    {
        Draft = draft; this.store = store; this.dataRoot = dataRoot; this.baseRoot = baseRoot; this.newConversation = newConversation; this.apiKey = apiKey;
        config = new(Path.Combine(baseRoot, "modes", "chat"), Path.Combine(dataRoot, "modes", "chat"));
        draft.Chat ??= new();
        if (draft.Chat.Auxiliary == null)
        {
            var path = Path.Combine(dataRoot, "chat-auxiliary-preferences.json");
            var preferences = File.Exists(path) ? WorkflowJson.Read<ChatAuxiliaryPreferences>(path) : new();
            draft.Chat.Auxiliary = new() { Provider = preferences.Provider };
            draft.Chat.Auxiliary.Current.Model = preferences.Model;
            draft.Chat.Auxiliary.Current.Effort = preferences.Effort;
        }
        if (draft.Chat.Compositions.Count == 0) draft.Chat.Compositions.Add(new() { TypeId = draft.Chat.TypeId });
        BindConversation(ChatWorkspace.Active(draft.Chat));
        BindAuxiliary();
        Text = "问答与制卡 · 零碎知识剪藏"; Name = "knowledge-clipper.chat";
        Size = new Size(1240, 860); MinimumSize = new Size(680, 520); StartPosition = FormStartPosition.CenterParent;
        Controls.Add(web); Controls.Add(status);
        updates.Tick += (_, _) => { if (dirty && ready) { dirty = false; Publish(); } }; updates.Start();
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += async (_, e) =>
        {
            store.Save(Draft);
            if (Busy) { e.Cancel = true; closeRequested = true; await StopAsync(); }
        };
        FormClosed += (_, _) => { modelGeneration++; auxiliaryModelGeneration++; updates.Dispose(); store.Save(Draft); Saved?.Invoke(Draft); http.Dispose(); web.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            config.Load();
            var directory = Path.Combine(baseRoot, "chat-view");
            if (!File.Exists(Path.Combine(directory, "index.html"))) throw new FileNotFoundException("本机聊天资源未构建，请使用 build.ps1 生成完整候选。");
            var env = await WarmChatWebViewEnvironment.GetAsync(Path.Combine(dataRoot, "webview-chat"));
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("clipper-chat.local", directory, CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://clipper-chat.local/", StringComparison.Ordinal)) e.Cancel = true; };
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                if (uri.Host == "clipper-chat.local") return;
                if (uri.Host == "clipper-media.local" && e.ResourceContext == CoreWebView2WebResourceContext.Image)
                {
                    var sha = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                    var image = Draft.Chat.Assets.Concat(Draft.Chat.Attachments).FirstOrDefault(a => a.Sha256 == sha);
                    if (image != null)
                    {
                        try { e.Response = env.CreateWebResourceResponse(new MemoryStream(image.ReadVerified()), 200, "OK", "Content-Type: image/png\r\nCache-Control: private, max-age=3600"); return; }
                        catch (Exception ex) { error = ex.Message; dirty = true; }
                    }
                }
                e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            web.CoreWebView2.WebMessageReceived += async (_, e) =>
            {
                if (!e.Source.StartsWith("https://clipper-chat.local/", StringComparison.Ordinal)) return;
                string requestId = null;
                try {
                    using var json = JsonDocument.Parse(e.WebMessageAsJson);
                    if (json.RootElement.TryGetProperty("clientCommandId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString().Length <= 100)
                        requestId = id.GetString();
                    await CommandAsync(json.RootElement);
                }
                catch (Exception ex)
                {
                    if (ex.Data["KnowledgeClipper.ChatSession"] is not ChatSession affected || affected == ChatWorkspace.Active(Draft.Chat)) error = ex.Message;
                    Publish();
                }
                finally {
                    if (requestId != null && !IsDisposed && ready)
                        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "commandAcknowledged", requestId }));
                }
            };
            web.CoreWebView2.Navigate("https://clipper-chat.local/index.html");
            status.Visible = false;
        }
        catch (Exception ex) { status.Text = "问答界面未就绪，草稿已保存。\n" + ex.Message; error = ex.Message; }
    }

    private void Save() { coordinator.SaveEdits(); Saved?.Invoke(Draft); }
    private void BindConversation(ChatSession session)
    {
        if (!conversations.TryGetValue(session, out var bound))
        {
            bound = new(Draft, store, config, new CodexChatProcess(Path.Combine(baseRoot, "helpers", "codex_chat.py"), dataRoot), http, apiKey, dataRoot, session);
            bound.Changed += () => { if (IsDisposed) return; if (InvokeRequired) BeginInvoke((Action)(() => dirty = true)); else dirty = true; };
            conversations.Add(session, bound);
        }
        coordinator = bound;
    }
    private async Task SwitchConversationAsync(string id)
    {
        var root = Draft.Chat;
        var previousId = coordinator.Session == root ? "main" : root.Branches.Single(b => b.Session == coordinator.Session).Id;
        conversationPages[previousId] = visibleCount;
        var oldProvider = coordinator.Session.Binding.Provider;
        ChatWorkspace.Switch(root, id); BindConversation(ChatWorkspace.Active(root));
        visibleCount = conversationPages.GetValueOrDefault(id, 20);
        modelGeneration++; error = null; Save(); Publish();
        if (oldProvider != coordinator.Session.Binding.Provider || models.Length == 0) await LoadModelsAsync();
    }
    private string Display(string key, string raw)
    {
        if (raw == null) return null;
        if (!displayCache.TryGetValue(key, out var cached) || cached.Raw != raw)
            displayCache[key] = cached = (raw, ChatMarkdown.ForDisplay(raw), ChatMarkdown.CopyMath(raw));
        return cached.Display;
    }
    private ChatCopyMath[] CopyMath(string key, string raw)
    { if (raw == null) return []; Display(key, raw); return displayCache[key].CopyMath; }
    private string ExportStatus(CardComposition card)
    {
        if (card.ExportDraftId == null) return "尚未导出";
        if (!Guid.TryParseExact(card.ExportDraftId, "N", out _)) return "冻结草稿缺失";
        var file = new FileInfo(store.Identity(card.ExportDraftId));
        if (!file.Exists) { exportStatusCache.Remove(card.ExportDraftId); return "冻结草稿缺失"; }
        if (exportStatusCache.TryGetValue(card.ExportDraftId, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc)
            return cached.Status;
        var child = store.Find(card.ExportDraftId);
        var status = child == null ? "冻结草稿缺失" : child.ImportState == ImportState.Verified ? ExportRouting.ResultSummary(child) : child.ImportError ?? "已确认 · 待导入或核对";
        exportStatusCache[card.ExportDraftId] = (file.Length, file.LastWriteTimeUtc, status);
        return status;
    }
    private void Publish()
    {
        if (!ready || IsDisposed) return;
        var root = Draft.Chat; var chat = ChatWorkspace.Active(root);
        ChatConfiguration current;
        try { current = config.Load(); } catch (Exception ex) { error = ex.Message; current = new([]); }
        var exportStatus = root.Compositions.ToDictionary(c => c.Id, ExportStatus);
        var sourceIds = root.Compositions.SelectMany(c => c.FrontSources.Concat(c.BackSources)).ToHashSet();
        object Tab(string id, string title, ChatSession session) => new { id, title, busy = conversations.TryGetValue(session, out var c) && c.Busy,
            forking = session.Fork is { Stage: not "verified" },
            needsReconciliation = session.Binding.NeedsReconciliation || session.Fork is { Stage: not "verified" } };
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {
            draftId = Draft.Id, conversationId = root.ActiveConversationId,
            tabs = ChatWorkspace.OpenIds(root).Select(id => Tab(id, id == "main" ? "原对话" : root.Branches.Single(b => b.Id == id).Title, ChatWorkspace.Session(root, id))),
            canUndoClose = root.ClosedTabs.Count > 0,
            busy = Busy, conversationBusy = coordinator.Busy && chat.Fork is not { Stage: not "verified" }, error = error ?? chat.Binding.Error,
            status = auxiliaryCoordinator.Busy ? "正在处理卡片…" : coordinator.Busy && chat.Fork is { Stage: not "verified" } ? "正在建立或核对分叉…" : coordinator.Busy ? "正在回复…" : Busy ? "其他对话正在处理…" : chat.Binding.NeedsReconciliation ? "结果待核对" : "草稿已保存",
            provider = chat.Binding.Provider, chat.Model, chat.Effort, chat.TypeId, chat.Input, chat.IncludeExamples, chat.UseQuestionTypeDefaults,
            messages = chat.Messages.TakeLast(visibleCount).Select(m => new { m.Id, m.Role, m.Status, m.TypeId, m.Action, m.CreatedAt, canFork = ChatWorkspace.CanFork(chat, m),
                parts = m.Parts.Select((p, i) => new { p.Kind, p.Text, displayText = Display(m.Id + ":" + i, p.Text), copyMath = CopyMath(m.Id + ":" + i, p.Text), image = p.Image == null ? null : new { p.Image.Sha256 } }) }),
            total = chat.Messages.Count, hasSent = chat.Prompts.Count > 0, models, types = current.Types, attachments = chat.Attachments.Select(a => new { a.Sha256 }),
            sourceMessages = ChatWorkspace.Messages(root).Where(m => sourceIds.Contains(m.Id)).Select(m => new { m.Id, title = m.Text.Length > 32 ? m.Text[..32] + "…" : m.Text,
                conversation = root.Messages.Any(x => x.Id == m.Id) ? "原对话" : root.Branches.First(b => b.Session.Messages.Any(x => x.Id == m.Id)).Title }),
            cards = root.Compositions.Select(c => new { c.Id, c.TypeId, c.FrontSources, c.BackSources, c.Front, c.Back, c.Annotation, c.AnnotationEnabled, c.FrontAnnotation, c.FrontAnnotationEnabled, c.Revision,
                c.ManuallyEdited, c.SourcesChanged, c.ExportDraftId, canUndo = c.ExportDraftId == null && c.UndoHistory.Count > 0,
                canRedo = c.ExportDraftId == null && c.RedoHistory.Count > 0 }),
            proposals = root.Proposals, exportStatus, cardPreview, needsReconciliation = chat.Binding.NeedsReconciliation || chat.Fork is { Stage: not "verified" },
            auxiliary = new { provider = root.Auxiliary.Provider, model = root.Auxiliary.Current.Model, effort = root.Auxiliary.Current.Effort,
                models = auxiliaryModels, loading = auxiliaryModelsLoading, busy = auxiliaryCoordinator.Busy,
                error = auxiliaryError ?? root.Auxiliary.Current.Binding.Error, needsReconciliation = root.Auxiliary.Current.Binding.NeedsReconciliation }
        }, ViewJson));
    }

    internal async Task CommandAsync(JsonElement e)
    {
        string Text(string key) => e.GetProperty(key).GetString() ?? "";
        var root = Draft.Chat; var chat = ChatWorkspace.Active(root); var command = Text("command"); error = null;
        if (e.TryGetProperty("conversationId", out var owner) && owner.GetString() is { Length: > 0 } ownerId && ownerId != root.ActiveConversationId)
        {
            if (command == "input") { ChatWorkspace.Session(root, ownerId).Input = Text("text"); Save(); return; }
            if (command is "send" or "settings" or "attach" or "pasteImage" or "removeImage" or "fork" or "roundCard" or "snapshot" or "older" or "recover" or "openRules" || command == "pasteImageData" && !e.TryGetProperty("cardId", out _))
                throw new InvalidOperationException("对话已切换；原操作未应用到其他 Tab。");
        }
        CardComposition Card() => root.Compositions.Single(c => c.Id == Text("cardId"));
        switch (command)
        {
            case "ready":
                var initialCoordinator = coordinator;
                var sendInitially = initialSendPending;
                initialSendPending = false;
                ready = true; Publish();
                if (!coordinator.Busy && chat.Binding.Provider == "codex" && chat.Binding.ThreadId != null &&
                    (chat.Binding.NeedsReconciliation && chat.Binding.TurnId != null || chat.Fork is { Stage: not "verified" }))
                    await RunAsync(() => coordinator.RecoverAsync(requestCancellation.Token));
                await Task.WhenAll(LoadModelsAsync(), LoadAuxiliaryModelsAsync());
                if (sendInitially && !IsDisposed && coordinator == initialCoordinator)
                {
                    // The ready event claims the initial send before catalog loading or any request awaits.
                    if (chat.Prompts.Count == 0) await RunAsync(() => coordinator.SendAsync(models, ct: requestCancellation.Token));
                }
                break;
            case "models": await LoadModelsAsync(); break;
            case "auxiliaryModels": await LoadAuxiliaryModelsAsync(); break;
            case "auxiliarySettings":
                if (Busy) throw new InvalidOperationException("当前请求的设置已冻结。");
                var auxiliary = root.Auxiliary;
                if (e.TryGetProperty("provider", out var auxiliaryProvider))
                {
                    if (auxiliaryProvider.GetString() is not ("api" or "codex")) throw new InvalidDataException("辅助服务无效。");
                    auxiliary.Provider = auxiliaryProvider.GetString(); BindAuxiliary(); Save(); await LoadAuxiliaryModelsAsync();
                }
                if (e.TryGetProperty("model", out var auxiliaryModel)) { auxiliary.Current.Model = auxiliaryModel.GetString(); auxiliary.Current.Effort = null; }
                if (e.TryGetProperty("effort", out var auxiliaryEffort)) auxiliary.Current.Effort = string.IsNullOrWhiteSpace(auxiliaryEffort.GetString()) ? null : auxiliaryEffort.GetString();
                SaveAuxiliaryPreferences(); Save(); break;
            case "recoverAuxiliary":
                await RunAsync(async () => { await auxiliaryCoordinator.RecoverAsync(requestCancellation.Token); FinishAuxiliaryProposal(); }, true); break;
            case "reloadView": store.Save(Draft); web.CoreWebView2.Reload(); break;
            case "uiError":
                var logRoot = Path.Combine(dataRoot, "logs"); Directory.CreateDirectory(logRoot);
                File.AppendAllText(Path.Combine(logRoot, "chat-ui.jsonl"), JsonSerializer.Serialize(new {
                    at = DateTimeOffset.UtcNow, draftId = Draft.Id, message = Text("message"),
                    stack = e.TryGetProperty("stack", out var stack) && stack.ValueKind == JsonValueKind.String ? stack.GetString() : null,
                    stage = chat.Binding.Stage, messageCount = chat.Messages.Count
                }) + Environment.NewLine); break;
            case "older": visibleCount += 20; break;
            case "input": if (!Busy) { chat.Input = Text("text"); Save(); } break;
            case "settings":
                if (Busy) throw new InvalidOperationException("当前回合的设置已冻结。");
                if (e.TryGetProperty("provider", out var provider))
                {
                    if (chat.Prompts.Count > 0) throw new InvalidOperationException("切换服务请新建对话，当前会话绑定保留。");
                    if (provider.GetString() is not ("api" or "codex")) throw new InvalidDataException("服务无效。");
                    chat.Binding.Provider = provider.GetString(); chat.Model = ""; chat.Effort = null; Save(); await LoadModelsAsync();
                }
                if (e.TryGetProperty("model", out var model)) { chat.Model = model.GetString(); chat.Effort = null; }
                if (e.TryGetProperty("effort", out var effort)) chat.Effort = string.IsNullOrWhiteSpace(effort.GetString()) ? null : effort.GetString();
                if (e.TryGetProperty("typeId", out var type)) { config.Target(type.GetString()); chat.TypeId = type.GetString(); }
                ChatTypeDefaults.Apply(chat);
                if (e.TryGetProperty("includeExamples", out var examples)) chat.IncludeExamples = examples.GetBoolean();
                WorkflowJson.Save(Path.Combine(dataRoot, "chat-preferences.json"), new ChatPreferences(chat.Binding.Provider, chat.Model, chat.Effort, chat.TypeId)); Save(); break;
            case "send":
                if (e.TryGetProperty("input", out var input)) { chat.Input = input.GetString(); Save(); }
                await RunAsync(() => coordinator.SendAsync(models, ct: requestCancellation.Token)); break;
            case "stop": await StopAsync(); break;
            case "recover": await RunAsync(() => coordinator.RecoverAsync(requestCancellation.Token)); break;
            case "new":
                Save(); var blank = ChatWorkspace.New(root); await SwitchConversationAsync(blank.Id); break;
            case "switchConversation": await SwitchConversationAsync(Text("id")); break;
            case "closeConversation":
                Save(); ChatWorkspace.Close(root, Text("id")); await SwitchConversationAsync(root.ActiveConversationId); break;
            case "undoCloseConversation":
                Save(); if (ChatWorkspace.UndoClose(root)) await SwitchConversationAsync(root.ActiveConversationId); break;
            case "moveConversation":
                ChatWorkspace.Move(root, Text("id"), Text("targetId"), e.GetProperty("after").GetBoolean()); Save(); break;
            case "fork":
                if (Busy) throw new InvalidOperationException("请等待当前回合完成或停止后分叉。");
                Save(); var branch = ChatWorkspace.Fork(root, Text("messageId")); await SwitchConversationAsync(branch.Id);
                if (branch.Session.Fork != null)
                {
                    var forkCoordinator = conversations[branch.Session];
                    await RunAsync(() => forkCoordinator.ForkAsync(requestCancellation.Token), owner: forkCoordinator);
                }
                break;
            case "attach": ChooseImages(chat.Attachments); Save(); break;
            case "pasteImage":
                if (!Clipboard.ContainsImage()) throw new InvalidDataException("剪贴板没有图片。");
                using (var image = Clipboard.GetImage()) { using var bytes = new MemoryStream(); image.Save(bytes, ImageFormat.Png); chat.Attachments.Add(StoreImage(bytes.ToArray())); }
                Save(); break;
            case "removeImage": chat.Attachments.RemoveAll(a => a.Sha256 == Text("sha256")); Save(); break;
            case "copyMessage": WriteClipboardText(ChatComposition.Material(ChatWorkspace.Messages(root).Single(m => m.Id == Text("messageId")))); break;
            case "copySelectionError": throw new InvalidDataException(Text("message"));
            case "pasteImageData":
                if (Busy && !e.TryGetProperty("cardId", out _)) throw new InvalidOperationException("当前回合的输入已冻结。");
                var targetCard = e.TryGetProperty("cardId", out _) ? Card() : null;
                if (targetCard?.ExportDraftId != null) throw new InvalidOperationException("已确认卡片已冻结，请编辑为新草稿。");
                var side = targetCard == null ? "input" : Text("side");
                if (side is not ("input" or "front" or "back" or "annotation" or "frontAnnotation")) throw new InvalidDataException("粘贴目标无效。");
                var encodedImages = e.GetProperty("images").EnumerateArray().Select(p => p.GetString()).ToArray();
                if (encodedImages.Length is < 1 or > 10) throw new InvalidDataException("单次粘贴支持 1 至 10 张图片。");
                var pngs = encodedImages.Select(encoded =>
                {
                    var bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length > 33554432) throw new InvalidDataException("图片超过 32 MiB。");
                    using var stream = new MemoryStream(bytes); using var image = Image.FromStream(stream);
                    using var png = new MemoryStream(); image.Save(png, ImageFormat.Png); return png.ToArray();
                }).ToArray();
                var pasted = pngs.Select(StoreImage).ToArray();
                var plain = Text("clipboardText");
                var insertion = targetCard == null ? plain : string.Join("\n", new[] { plain }.Where(t => !string.IsNullOrEmpty(t)).Concat(pasted.Select(a => "![图片](asset:" + a.Sha256 + ")")));
                var current = side == "front" ? targetCard.Front : side == "back" ? targetCard.Back : side == "annotation" ? targetCard.Annotation : side == "frontAnnotation" ? targetCard.FrontAnnotation : chat.Input;
                string Insert()
                {
                    if (insertion.Length == 0) return current;
                    var at = e.GetProperty("start").GetInt32(); var end = e.GetProperty("end").GetInt32();
                    if (current == Text("expectedText") && at >= 0 && end >= at && end <= current.Length)
                        return current[..at] + insertion + current[end..];
                    return string.IsNullOrEmpty(current) ? insertion : current + "\n" + insertion;
                }
                if (targetCard == null) { chat.Input = Insert(); chat.Attachments.AddRange(pasted); }
                else ChatComposition.Edit(targetCard, side == "front" ? Insert() : targetCard.Front,
                    side == "back" ? Insert() : targetCard.Back, targetCard.TypeId,
                    side == "annotation" ? Insert() : null, side == "annotation" ? true : null,
                    side == "frontAnnotation" ? Insert() : null, side == "frontAnnotation" ? true : null);
                cardPreview = null; Save(); break;
            case "addCard": root.Compositions.Add(new() { TypeId = chat.TypeId }); Save(); break;
            case "copyCard": ChatComposition.Editable(root, Card()); Save(); break;
            case "select": ChatComposition.Select(root, Card(), Text("messageId"), Text("side"), e.GetProperty("selected").GetBoolean()); Save(); break;
            case "editCard":
                var editedCard = Card();
                ChatComposition.Edit(editedCard,
                    e.TryGetProperty("front", out var frontEdit) ? ChatMarkdown.NormalizeMediaReferences(frontEdit.GetString(), root.Assets) : editedCard.Front,
                    e.TryGetProperty("back", out var backEdit) ? ChatMarkdown.NormalizeMediaReferences(backEdit.GetString(), root.Assets) : editedCard.Back,
                    e.TryGetProperty("typeId", out var typeEdit) ? typeEdit.GetString() : editedCard.TypeId,
                    e.TryGetProperty("annotation", out var annotationEdit) ? ChatMarkdown.NormalizeMediaReferences(annotationEdit.GetString(), root.Assets) : null,
                    e.TryGetProperty("annotationEnabled", out var annotationEnabled) ? annotationEnabled.GetBoolean() : null,
                    e.TryGetProperty("frontAnnotation", out var frontAnnotationEdit) ? ChatMarkdown.NormalizeMediaReferences(frontAnnotationEdit.GetString(), root.Assets) : null,
                    e.TryGetProperty("frontAnnotationEnabled", out var frontAnnotationEnabled) ? frontAnnotationEnabled.GetBoolean() : null);
                cardPreview = null; Save(); break;
            case "move": ChatComposition.Move(root, Card(), Text("side"), e.GetProperty("index").GetInt32(), e.GetProperty("offset").GetInt32()); Save(); break;
            case "rebuild": ChatComposition.Rebuild(root, Card()); Save(); break;
            case "undo": ChatComposition.Undo(Card(), root); cardPreview = null; Save(); break;
            case "redo": ChatComposition.Redo(Card(), root); cardPreview = null; Save(); break;
            case "roundCard":
                var selected = chat.Messages.Single(m => m.Id == Text("messageId")); var index = chat.Messages.IndexOf(selected);
                var user = selected.Role == "user" ? selected : chat.Messages.Take(index).LastOrDefault(m => m.Role == "user" && m.Action == null);
                if (user == null) throw new InvalidDataException("没有对应问题。");
                var next = chat.Messages.Skip(chat.Messages.IndexOf(user) + 1).TakeWhile(m => m.Role != "user").Where(m => m.Role == "assistant" && m.Action == null).ToArray();
                var newCard = new CardComposition { TypeId = user.TypeId, FrontSources = [user.Id], BackSources = next.Select(m => m.Id).ToList() };
                ChatComposition.Rebuild(root, newCard); root.Compositions.Add(newCard); Save(); break;
            case "cardImage":
                var card = Card(); if (card.ExportDraftId != null) throw new InvalidOperationException("请先编辑为新草稿。");
                var added = new List<ImageAsset>(); ChooseImages(added);
                var markdown = string.Join("\n", added.Select(a => "![图片](asset:" + a.Sha256 + ")"));
                ChatComposition.Edit(card, Text("side") == "front" ? card.Front + "\n" + markdown : card.Front,
                    Text("side") == "back" ? card.Back + "\n" + markdown : card.Back, card.TypeId); Save(); break;
            case "compose":
            {
                if (Busy || auxiliaryModelsLoading) throw new InvalidOperationException("请等待当前请求或辅助模型目录加载完成。");
                auxiliaryError = null;
                try
                {
                    var sourceCard = Card();
                    if (sourceCard.ExportDraftId != null) throw new InvalidOperationException("请先编辑为新草稿。");
                    if (root.Auxiliary.Current.Binding.NeedsReconciliation && root.Auxiliary.Provider != "api")
                        throw new InvalidOperationException("上次辅助操作待核对，请先核对原辅助回合。");
                    var proposal = ChatComposition.Begin(root, sourceCard, Text("action"), Text("requirements"), config.Load().Types.Select(t => t.Id).ToArray(), e.TryGetProperty("side", out var targetSide) ? targetSide.GetString() : null, chat);
                    if (root.Auxiliary.PrepareExplicitRequest()) BindAuxiliary();
                    Save();
                    await RunAsync(async () => {
                        var auxiliaryChat = root.Auxiliary.Current;
                        auxiliaryChat.TypeId = sourceCard.TypeId; auxiliaryChat.IncludeExamples = chat.IncludeExamples;
                        var images = ChatComposition.ProposalImages(proposal, root);
                        try { await auxiliaryCoordinator.SendAsync(auxiliaryModels, proposal.Action, proposal.InputSnapshot, images, requestCancellation.Token); }
                        finally { FinishAuxiliaryProposal(proposal); }
                    }, true);
                }
                catch (Exception ex) { auxiliaryError = ex is OperationCanceledException ? root.Auxiliary.Current.Binding.Error ?? "已停止处理，原输入已保留。" : ex.Message; throw; }
                break;
            }
            case "editProposal":
                var candidate = root.Proposals.Single(p => p.Id == Text("proposalId"));
                if (candidate.Status != "preview") throw new InvalidOperationException("候选已处理。");
                var c = candidate.Cards[e.GetProperty("index").GetInt32()];
                if (candidate.Action != "transcribe" || candidate.TargetSide == "front") c.Front = Text("front");
                if (candidate.Action != "formalize" && (candidate.Action != "transcribe" || candidate.TargetSide == "back")) c.Back = Text("back"); Save(); break;
            case "adopt": ChatComposition.Adopt(root, root.Proposals.Single(p => p.Id == Text("proposalId"))); Save(); break;
            case "discard": root.Proposals.Single(p => p.Id == Text("proposalId")).Status = "discarded"; Save(); break;
            case "export":
                var destination = Text("destination") switch { "RemNote" => ExportDestination.RemNote, "Anki" => ExportDestination.Anki, _ => throw new InvalidDataException("导出对象无效。") };
                await ChatComposition.ExportAsync(Draft, Card(), store, config, new CardImportRouter(http), destination); Save(); break;
            case "preview":
                var previewCard = Card(); ChatMarkdown.Render(previewCard.Front, previewCard.Back, root.Assets, previewCard.Annotation, previewCard.FrontAnnotation);
                cardPreview = new { cardId = previewCard.Id, revision = previewCard.Revision,
                    front = ChatMarkdown.ForDisplay(ChatMarkdown.BackWithAnnotation(previewCard.Front, previewCard.FrontAnnotation), root.Assets), back = ChatMarkdown.ForDisplay(ChatMarkdown.BackWithAnnotation(previewCard.Back, previewCard.Annotation), root.Assets) }; break;
            case "openRules":
                var relative = Text("kind") switch { "type" => config.Load().Types.Single(t => t.Id == chat.TypeId).PromptFile,
                    "profile" => "profiles/common.md", "registry" => "question-types.json", _ => throw new InvalidDataException("配置入口无效。") };
                var path = config.Editable(relative);
                if (relative == "question-types.json") EditRegistry(path); else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); break;
            case "snapshot":
                var snapshot = chat.Prompts.LastOrDefault();
                var summary = snapshot == null ? "尚未发送。本次发送前会重新读取 MD。" :
                    $"服务：{snapshot.Provider}　模型：{snapshot.Model}　推理：{snapshot.Effort ?? "默认"}\n类型：{snapshot.TypeId}　状态：{snapshot.Stage}\n\n" +
                    (snapshot.CompactedItemIds.Count == 0 ? "" : snapshot.RulesReinjectedAfterTurn
                        ? "本回合发生自动压缩。规则已在回合结束后补入；已生成的答案没有自动重答。\n\n"
                        : "本回合发生自动压缩，尚无回合结束后的规则重注入回执；请先核对原回合。\n\n") +
                    string.Join("\n", snapshot.Files.Select(f => f.RelativePath + " · " + f.Sha256)) + "\n\n" + snapshot.Instructions;
                ShowText("本次生效规则", summary); break;
            default: throw new InvalidDataException("未允许的界面动作。");
        }
        Publish();
    }

    private async Task LoadModelsAsync()
    {
        var generation = ++modelGeneration;
        var session = coordinator.Session;
        var provider = session.Binding.Provider;
        models = []; Publish();
        ChatModel[] fetched;
        try { fetched = await coordinator.ModelsAsync(provider); }
        catch when (IsDisposed || generation != modelGeneration || session != coordinator.Session || provider != session.Binding.Provider) { return; }
        if (IsDisposed || generation != modelGeneration || session != coordinator.Session || provider != session.Binding.Provider) return;
        models = fetched;
        ChatTypeDefaults.Apply(session);
        // First use may select the user's existing API model, but never silently replace an invalid saved selection.
        if (string.IsNullOrWhiteSpace(session.Model) && provider == "api" && models.Any(m => m.Id == Draft.Settings.Model))
            session.Model = Draft.Settings.Model;
        Save(); Publish();
    }
    private void BindAuxiliary()
    {
        auxiliaryCoordinator = new(Draft, store, config, new CodexChatProcess(Path.Combine(baseRoot, "helpers", "codex_chat.py"), dataRoot), http, apiKey, dataRoot, Draft.Chat.Auxiliary.Current);
        auxiliaryCoordinator.Changed += () => { if (IsDisposed) return; if (InvokeRequired) BeginInvoke((Action)(() => dirty = true)); else dirty = true; };
    }
    private void SaveAuxiliaryPreferences()
    {
        var auxiliary = Draft.Chat.Auxiliary;
        WorkflowJson.Save(Path.Combine(dataRoot, "chat-auxiliary-preferences.json"), new ChatAuxiliaryPreferences(auxiliary.Provider, auxiliary.Current.Model, auxiliary.Current.Effort));
    }
    private async Task LoadAuxiliaryModelsAsync()
    {
        var generation = ++auxiliaryModelGeneration;
        var auxiliary = Draft.Chat.Auxiliary; var provider = auxiliary.Provider;
        auxiliaryModels = []; auxiliaryModelsLoading = true; auxiliaryError = null; Publish();
        try
        {
            var fetched = await auxiliaryCoordinator.ModelsAsync(provider);
            if (IsDisposed || generation != auxiliaryModelGeneration || provider != auxiliary.Provider) return;
            auxiliaryModels = fetched;
            if (string.IsNullOrWhiteSpace(auxiliary.Current.Model))
            {
                var preferred = provider == "api" ? Draft.Settings.ImageModel : ChatTypeDefaults.Model;
                auxiliary.Current.Model = fetched.FirstOrDefault(m => m.Id == preferred)?.Id ?? fetched.FirstOrDefault()?.Id ?? "";
            }
            Save();
        }
        catch (Exception ex) { if (!IsDisposed && generation == auxiliaryModelGeneration && provider == auxiliary.Provider) auxiliaryError = ex.Message; }
        finally { if (!IsDisposed && generation == auxiliaryModelGeneration) { auxiliaryModelsLoading = false; Publish(); } }
    }
    private void FinishAuxiliaryProposal(ComposeProposal proposal = null)
    {
        var auxiliary = Draft.Chat.Auxiliary.Current;
        var snapshot = auxiliary.Prompts.LastOrDefault();
        proposal ??= Draft.Chat.Proposals.LastOrDefault(p => p.InputSnapshot == snapshot?.Input && p.Status is "pending" or "incomplete");
        if (proposal == null || proposal.Status is "discarded" or "adopted" or "stale") return;
        var matches = snapshot?.Input == proposal.InputSnapshot;
        var output = !matches ? "" : string.Join("\n", auxiliary.Messages.Where(m => m.Role == "assistant" && m.CreatedAt >= snapshot.CreatedAt).Select(m => m.Text));
        proposal.RawOutput = output;
        if (proposal.Status != "discarded")
        {
            if (auxiliary.Binding.Stage == "completed" && matches)
            {
                ChatComposition.Parse(proposal, output, Draft.Chat, config.Load().Types.Select(t => t.Id).ToArray());
                if (proposal.Status == "preview")
                    try { ChatComposition.Adopt(Draft.Chat, proposal); cardPreview = null; }
                    catch (InvalidOperationException ex) { proposal.Status = "stale"; proposal.Error = ex.Message; }
            }
            else { proposal.Status = matches ? "incomplete" : "failed"; proposal.Error = matches ? "辅助回合未完成；输入和部分回复已保留，不能自动采用。" : "辅助调用尚未开始；原输入已保留，请修正设置或提示词后再生成。"; }
        }
        Save();
        if (proposal.Status is "unparsed" or "stale" or "failed")
            throw new InvalidOperationException(proposal.Error);
    }
    private async Task RunAsync(Func<Task> run, bool auxiliary = false, ChatCoordinator owner = null)
    {
        if (Busy) throw new InvalidOperationException("已有请求在运行。");
        activeRequest = owner ?? (auxiliary ? auxiliaryCoordinator : coordinator);
        requestCancellation = new();
        try { await run(); }
        catch (Exception ex) { ex.Data["KnowledgeClipper.ChatSession"] = activeRequest.Session; throw; }
        finally { requestCancellation.Dispose(); requestCancellation = null; activeRequest = null; Publish(); if (closeRequested) Close(); }
    }
    private async Task StopAsync()
    { var running = activeRequest ?? coordinator; if (running.Session.Binding.Provider == "api") requestCancellation?.Cancel(); await running.StopAsync(); }
    private void ChooseImages(List<ImageAsset> destination)
    {
        using var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in dialog.FileNames)
        {
            var original = File.ReadAllBytes(path); if (original.Length > 33554432) throw new InvalidDataException("图片超过 32 MiB。");
            var originals = Path.Combine(dataRoot, "chat-media", "originals"); Directory.CreateDirectory(originals);
            var saved = Path.Combine(originals, ImageAsset.Hash(original) + Path.GetExtension(path).ToLowerInvariant());
            if (!File.Exists(saved)) File.WriteAllBytes(saved, original);
            using var stream = new MemoryStream(original); using var image = Image.FromStream(stream);
            using var png = new MemoryStream(); image.Save(png, ImageFormat.Png); destination.Add(StoreImage(png.ToArray()));
        }
    }
    private ImageAsset StoreImage(byte[] bytes)
    {
        var sha = ImageAsset.Hash(bytes); var root = Path.Combine(dataRoot, "chat-media"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, sha + ".png"); if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        var asset = new ImageAsset(path, sha, "question", 1); asset.ReadVerified();
        if (!Draft.Chat.Assets.Any(a => a.Sha256 == sha)) Draft.Chat.Assets.Add(asset); return asset;
    }
    private void EditRegistry(string path)
    {
        using var dialog = new Form { Text = "问题类型设置 · JSON", Size = new Size(820, 680), StartPosition = FormStartPosition.CenterParent };
        var editor = new TextBox { Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = File.ReadAllText(path) };
        var save = new Button { Text = "保存类型设置", Dock = DockStyle.Bottom, Height = 38 };
        save.Click += (_, _) => { try { var value = JsonSerializer.Deserialize<ChatConfiguration>(editor.Text, WorkflowJson.Options);
            if (value?.Types == null || value.Types.Length == 0 || value.Types.Select(t => t.Id).Distinct().Count() != value.Types.Length)
                throw new InvalidDataException("类型标识为空或重复。"); WorkflowJson.Save(path, value); dialog.Close(); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message); } };
        dialog.Controls.Add(editor); dialog.Controls.Add(save); dialog.ShowDialog(this);
    }
    private void ShowText(string title, string text)
    {
        var dialog = new Form { Text = title, Size = new Size(850, 700), StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Text = text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill }); dialog.Show(this);
    }
}

// WebView2 shuts down its browser after the final controller closes. Reopening
// during that shutdown can wait several seconds on the same user-data folder.
// Retain one blank, invisible controller (no page, messages, models or draft).
// Each conversation still gets a fresh controller and is disposed/saved normally.
internal static class WarmChatWebViewEnvironment
{
    private static Entry current;
    static WarmChatWebViewEnvironment() => Application.ApplicationExit += (_, _) => { current?.Dispose(); current = null; };
    internal static Task<CoreWebView2Environment> GetAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (current == null || !string.Equals(current.Folder, folder, StringComparison.OrdinalIgnoreCase) || current.Ready.IsFaulted)
        {
            current?.Dispose();
            current = new Entry(folder);
        }
        return current.Ready;
    }
    private sealed class Entry : IDisposable
    {
        internal readonly string Folder;
        internal readonly Task<CoreWebView2Environment> Ready;
        private WebView2 keeper;
        private bool disposed;
        internal Entry(string folder) { Folder = folder; Ready = CreateAsync(); }
        private async Task<CoreWebView2Environment> CreateAsync()
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Folder);
            if (disposed) return environment;
            var view = keeper = new WebView2 { Visible = false, Size = new Size(1, 1) };
            _ = view.Handle;
            try { await view.EnsureCoreWebView2Async(environment); }
            catch { view.Dispose(); keeper = null; throw; }
            if (disposed) return environment;
            environment.BrowserProcessExited += (_, _) =>
            {
                if (ReferenceEquals(current, this)) { current = null; Dispose(); }
            };
            return environment;
        }
        public void Dispose() { disposed = true; keeper?.Dispose(); keeper = null; }
    }
}
