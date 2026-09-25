using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed partial class LanguageDesktopFlow : IDisposable
{
    private readonly Form owner;
    private readonly string dataRoot, baseRoot;
    private readonly Action<string> status;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DraftStore store;
    private readonly StreamingSpeech speech;
    private readonly LanguageDesktopCapture capture;
    private readonly List<LanguageDraftForm> forms = new();
    private readonly Dictionary<string, ClipDraft> quickDrafts = new();
    private readonly Dictionary<string, Task> contentTasks = new();
    private ClipDraft latest, lastSpeech;
    internal event Action<ClipDraft> DraftChanged;
    private LanguageInputForm inputForm;
    private bool capturing;
    private readonly CancellationTokenSource lifetime = new();
    internal bool IsPlaying => speech.IsPlaying;
    internal LanguageSettings Settings { get; private set; }
    internal event Action SettingsChanged;
    internal string SettingsPath => Path.Combine(dataRoot, "language-settings.json");
    internal string ModeOverrides => Path.Combine(dataRoot, "modes");
    internal string ModeDefaults => Path.Combine(baseRoot, "modes");

    public LanguageDesktopFlow(Form owner, string dataRoot, Action<string> status, UserSettings initial)
    {
        this.owner = owner; this.dataRoot = dataRoot; this.status = status;
        baseRoot = AppContext.BaseDirectory;
        Settings = File.Exists(SettingsPath) ? WorkflowJson.Read<LanguageSettings>(SettingsPath) : new LanguageSettings
        {
            AnkiRootDeck = DeckRouting.DefaultRoot, AnkiDeck = DeckRouting.DefaultRoot,
            Voice = initial.ChineseVoice, EnglishVoice = initial.EnglishVoice,
            AutoVoice = initial.LanguageOverride == "auto", RatePercent = initial.RatePercent,
            VolumePercent = initial.VolumePercent, PitchHz = initial.PitchHz,
            ContextEnabled = initial.ContextModeEnabled, ContextGranularity = initial.ContextGranularity
        };
        Settings = Settings with { OriginalImageMode = Settings.OriginalImageMode ?? initial.OriginalImageMode };
        Settings.Validate();
        store = new DraftStore(dataRoot);
        historyLookup = new LanguageHistoryLookup(dataRoot);
        speech = new StreamingSpeech(Path.Combine(baseRoot, "helpers", "stream_speech.py"), @"D:\Caches\KnowledgeClipper\audio");
        speech.Prepare(Settings);
        capture = new LanguageDesktopCapture(Path.Combine(baseRoot, "helpers", "selection.cjs"));
        lastSpeech = HistoryRecords.LastLanguageSpeech(store);
    }

    internal void SaveSettings(LanguageSettings value)
    {
        value.Validate(); WorkflowJson.Save(SettingsPath, value); Settings = value; speech.Prepare(value); SettingsChanged?.Invoke();
    }

    public Task StartAsync(bool read, SelectionSnapshot frozen = null) => StartCaptureAsync(read, frozen, false);

    public Task StartLanguageAsync(SelectionSnapshot frozen = null, bool quick = false) => StartCaptureAsync(Settings.LanguageAutoRead, frozen, false, quick);

    public Task StartQuestionAsync(SelectionSnapshot frozen = null, bool quick = false) => StartCaptureAsync(false, frozen, true, quick);

    private async Task StartCaptureAsync(bool read, SelectionSnapshot frozen, bool question, bool quick = false)
    {
        if (capturing) return;
        capturing = true;
        var requestedAt = DateTimeOffset.UtcNow;
        try
        {
            var settings = Settings with { };
            var configuration = ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides);
            status("正在取得本次选区…");
            var captureStartedAt = DateTimeOffset.UtcNow;
            var material = await capture.CaptureAsync(settings, frozen);
            capturing = false;
            var input = new InputMetadata(frozen == null ? "selection-button" : "selection-dot", Intent: question ? "question" : "language",
                ReadRequested: read && !question, ExplicitQuestion: question ? "解释：" + material.SelectedText : null);
            var draft = StartMaterial(material, input, settings, configuration, requestedAt, quick);
            if (frozen != null && !question) BeginMeaningPopup(draft, new Point(frozen.X, frozen.Y));
            if (frozen != null) draft.Times["selectionObserved"] = frozen.CapturedAt;
            draft.Times["captureStarted"] = captureStartedAt;
            store.Save(draft);
        }
        catch (Exception ex)
        {
            status("取词未完成：" + ex.Message);
            if (quick && frozen != null && !owner.IsDisposed)
            {
                var material = new CaptureMaterial(frozen.Text, null, "unavailable", null, frozen.Source, frozen.Window, frozen.CapturedAt);
                var draft = new ClipDraft { Original = material, Material = material, Settings = Settings with { },
                    Input = new("selection-dot", question ? "question" : "language", ExplicitQuestion: question ? "解释：" + frozen.Text : null),
                    Configuration = ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides), Practice = Settings.Practice, ContentError = ex.Message };
                draft.ModeId = EntryRouting.ModeId(draft.Configuration, draft.Input.Intent);
                store.Save(draft); OpenDraft(draft);
            }
        }
        finally { capturing = false; }
    }

    internal Task<bool> StartTextAsync(string text) => StartTextCoreAsync(text, "language");

    internal Task<bool> StartQuestionTextAsync(string text) => StartTextCoreAsync(text, "question");

    internal Task<bool> StartTranslationTextAsync(string text) => StartTextCoreAsync(text, "translation");

    internal string StartPreparedMaterial(CaptureMaterial material, LanguageSettings snapshot, string intent = "language", bool quick = false)
    {
        if (material.FrontImage != null && intent == "language") snapshot = snapshot with { Practice = PracticeChoice.Reading };
        return StartMaterial(material, new InputMetadata(material.VisualQuestion ? "image-visual" : "image-ocr", intent,
            ReadRequested: intent == "language" && snapshot.LanguageAutoRead,
            ExplicitQuestion: intent == "question" ? "解释：" + material.SelectedText : null), snapshot,
            ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides), DateTimeOffset.UtcNow, quick).Id;
    }
    internal void Resume(string id) => ContinueHistory(store.Find(id) ?? throw new InvalidOperationException("找不到已保存的语言草稿。"));

    private Task<bool> StartTextCoreAsync(string text, string intent)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
        try
        {
            var settings = Settings with { };
            var material = new CaptureMaterial(text, null, "generated_allowed", null, "文字框输入", 0, DateTimeOffset.UtcNow);
            StartMaterial(material, new InputMetadata("typed", intent,
                ReadRequested: intent != "question" && settings.TypedInputAutoRead && (intent != "language" || settings.LanguageAutoRead)),
                settings, ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides), DateTimeOffset.UtcNow);
            return Task.FromResult(true);
        }
        catch (Exception ex) { status("输入未提交：" + ex.Message); return Task.FromResult(false); }
    }

    internal void OpenInput()
    {
        if (inputForm == null || inputForm.IsDisposed) inputForm = new LanguageInputForm(StartQuestionTextAsync);
        if (!inputForm.Visible) inputForm.Show(owner);
        inputForm.Activate();
    }

    // Every entry stops at this boundary: material + metadata, then the same draft and modules.
    private ClipDraft StartMaterial(CaptureMaterial material, InputMetadata input, LanguageSettings settings,
        ContentConfiguration configuration, DateTimeOffset requestedAt, bool quick = false)
    {
        material.Validate();
        var draft = new ClipDraft { Original = material, Material = material, Input = input, Settings = settings,
            Configuration = configuration, Practice = settings.Practice, ModeId = EntryRouting.ModeId(configuration, input.Intent) };
        draft.Times["requested"] = requestedAt;
        draft.Times["captured"] = DateTimeOffset.UtcNow;
        latest = draft;
        if (input.Intent == "language")
        {
            var epoch = ++languageEntryEpoch;
            pendingHistoryEntries.Add(draft.Id);
            if (input.ReadRequested) { speech.Stop(); draft.Times["speechRequested"] = requestedAt; }
            if (quick) quickDrafts[draft.Id] = draft;
            var view = quick ? null : OpenDraft(draft);
            store.Save(draft);
            _ = CompleteLanguageEntryAsync(draft, quick, view, epoch);
            return draft;
        }
        // Explicit entry routing is already resolved; audio need not wait for content.
        if (input.ReadRequested) { languageEntryEpoch++; speech.Stop(); }
        Task playback = Task.CompletedTask;
        if (input.ReadRequested && input.Intent != "question" && input.Intent != "lookup" &&
            !(input.QuestionMarkRouting && ContentModule.IsQuestion(material.SelectedText))) playback = PlayAsync(draft, false);
        if (quick)
        {
            store.Save(draft);
            _ = CompleteQuickAsync(draft, playback);
            return draft;
        }
        var form = OpenDraft(draft);
        draft.Times["previewOpened"] = DateTimeOffset.UtcNow;
        store.Save(draft);
        _ = ProcessAsync(draft, form);
        return draft;
    }

    private LanguageDraftForm OpenDraft(ClipDraft draft)
    {
        var existing = forms.FirstOrDefault(f => !f.IsDisposed && f.Draft.Id == draft.Id);
        if (existing != null) { existing.Show(); existing.Activate(); return existing; }
        var form = new LanguageDraftForm(draft, store,
            async () => { try { using var request = new HttpClient { Timeout = TimeSpan.FromSeconds(180) }; await new CardImportRouter(request).ImportAsync(draft, store); } finally { Changed(draft, null); } },
            () => { languageEntryEpoch++; return PlayAsync(draft, true); }, Stop);
        forms.Add(form);
        form.RetryContent += () => _ = ProcessAsync(draft, form);
        form.RetryDictionary += () => RetryDictionaryAsync(form.SelectedLanguageDraft, form);
        form.GenerateExampleRequested += () => {
            var generated = GeneratedExampleDrafts.Ensure(draft, store);
            var generatedForm = OpenDraft(generated);
            generatedForm.Text = "生成例句卡片预览 · " + generated.Material.SelectedText.TakeText(24);
            if (generated.ImportState != ImportState.Verified && generated.ConfirmedNote == null)
                _ = ProcessAsync(generated, generatedForm);
        };
        form.SubmitFollowUp += text => FollowUpAsync(draft, form, text);
        form.ImportQuestionCards += indexes => ImportQuestionCardsAsync(draft, form, indexes);
        form.ImportLanguageModes += modeIds => ImportLanguageModesAsync(draft, form, modeIds);
        form.PlaySentence += text => { languageEntryEpoch++; return speech.SpeakTextAsync(draft, text, SpeechPurpose.Sentence, true, () => Changed(draft, form)); };
        form.RefreshStatus();
        form.FormClosed += (_, _) => { forms.Remove(form); store.Save(draft); };
        form.Show(owner);
        return form;
    }

    private async Task CompleteQuickAsync(ClipDraft draft, Task playback)
    {
        quickDrafts[draft.Id] = draft;
        var needsInput = false;
        try
        {
            await ProcessAsync(draft, null, () => needsInput = true);
            lifetime.Token.ThrowIfCancellationRequested();
            using var request = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            await FloatingQuickImport.LanguageOrQuestionAsync(draft, store, new CardImportRouter(request), () => Changed(draft, null), lifetime.Token);
            await playback;
            if (draft.SpeechError != null) throw new InvalidOperationException(draft.SpeechError);
            status("快捷导入已完成 · " + DescribeStatus(draft));
        }
        catch (AnkiPendingException)
        {
            status("内容已保存，等待 Anki 打开后自动导入。");
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested || owner.IsDisposed) return;
            if (draft.ContentError == null) draft.ImportError = ex.Message;
            store.Save(draft);
            status("快捷处理需要继续确认：" + ex.Message);
            var form = OpenDraft(draft);
            if (needsInput) _ = ProcessAsync(draft, form);
            else form.ContentReady();
        }
        finally
        {
            quickDrafts.Remove(draft.Id);
            if (!owner.IsDisposed) Changed(draft, null);
        }
    }

    private async Task ProcessAsync(ClipDraft draft, LanguageDraftForm form, Action needsInput = null)
    {
        if (form?.ContentRunning == true) return;
        if (contentTasks.TryGetValue(draft.Id, out var active))
        {
            if (form != null) form.ContentRunning = true;
            try { await active; }
            finally
            {
                if (form?.IsDisposed == false) { form.ContentRunning = false; form.ContentReady(); }
            }
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        contentTasks.Add(draft.Id, completion.Task);
        try { await ProcessCoreAsync(draft, form, needsInput); }
        finally { contentTasks.Remove(draft.Id); completion.TrySetResult(); }
    }

    private async Task RetryDictionaryAsync(ClipDraft draft, LanguageDraftForm form)
    {
        if (draft == null || draft.ConfirmedNote != null || draft.ImportState is ImportState.Writing or ImportState.Unknown or ImportState.Verified)
            throw new InvalidOperationException("已确认或导入的卡片不能重新查询词典字段。");
        var language = draft.Configuration.Modes.FirstOrDefault(mode => mode.Definition.Id == draft.ModeId)?.Definition.Language;
        if (language is not ("zh" or "en")) throw new InvalidDataException("本次草稿尚未选定语言模式。");
        draft.DictionaryDetails = await new DirectDictionaryLookup(http).LookupAsync(draft.Material.SelectedText, language!, form.Lifetime);
        Changed(draft, form);
    }

    private async Task ProcessCoreAsync(ClipDraft draft, LanguageDraftForm form, Action needsInput)
    {
        if (form?.ContentRunning == true) return;
        if (form != null) form.ContentRunning = true;
        var cancellation = form?.Lifetime ?? lifetime.Token;
        void RequireInteraction(string message) { if (form == null) { needsInput?.Invoke(); throw new InvalidOperationException(message); } }
        try
        {
            draft.ContentError = null;
            var content = new ContentModule(http, () => ContentCredentials.Resolve(DesktopCredentialSettings.Load(dataRoot).DeepSeekApiKey));
            if (draft.ModeId == null)
            {
                RequireInteraction("请选择本次草稿的模式。");
                draft.ModeId = await form.ChooseModeAsync("请选择本次草稿的模式。");
                if (draft.ModeId == null) return;
                draft.Times["classified"] = DateTimeOffset.UtcNow;
            }
            draft.Route = ModeConfigurationLoader.Route(draft.Configuration, draft.ModeId);
            if (draft.Input.ReadRequested && draft.Route.OutputKind == "language" && !draft.Times.ContainsKey("speechRequested"))
                _ = PlayAsync(draft, false);
            if (draft.Route.RequiresContext && draft.Material.NeedsContextChoice)
            {
                RequireInteraction("本次上下文需要确认。");
                var material = await form.ResolveContextAsync();
                if (material == null) return;
                draft.Material = material; store.Save(draft);
            }
            if (draft.Route.OutputKind == "language" && LanguageModes.HasConcrete(draft.Configuration) && draft.ModeParentId == null && draft.ConfirmedNote == null)
            {
                ModeDefinition[] selected;
                try { selected = LanguageModes.Select(draft.Configuration, draft.Material, draft.Practice, draft.ModeId); }
                catch (ModeChoiceRequiredException ex)
                {
                    RequireInteraction(ex.Message);
                    var chosen = await form.ChooseModeAsync(ex.Message);
                    if (chosen == null) return;
                    selected = LanguageModes.Select(draft.Configuration, draft.Material, draft.Practice, chosen);
                }
                var children = LanguageModeCards.Ensure(draft, selected, store);
                var dictionaryTasks = children.Where(child => child.ConfirmedNote == null &&
                    (child.DictionaryDetails == null || child.DictionaryDetails.Target != child.Material.SelectedText.Trim() ||
                     child.DictionaryDetails.Language != child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language))
                    .Select(child => child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!)
                    .Distinct(StringComparer.Ordinal).ToDictionary(language => language,
                        language => new DirectDictionaryLookup(http).LookupAsync(draft.Material.SelectedText, language, cancellation));
                var exampleTasks = children.Where(child => child.ConfirmedNote == null && child.NoteId == null &&
                    child.Material.ContextText == null).Select(child =>
                    child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!)
                    .Distinct(StringComparer.Ordinal).ToDictionary(language => language, async language =>
                    {
                        var details = dictionaryTasks.TryGetValue(language, out var pending) ? await pending :
                            children.First(child => child.DictionaryDetails?.Language == language).DictionaryDetails;
                        return await new AuthenticExampleLookup(http).FindWithFallbackAsync(draft.Material.SelectedText,
                            language, details, cancellation);
                    });
                form?.ContentReady();
                await Task.WhenAll(children.Select(async child =>
                {
                    if (child.Content != null && child.Content.Fields?.IsUnresolved != true || child.ConfirmedNote != null || child.NoteId != null) return;
                    try
                    {
                        RefreshMissingModeConfiguration(child);
                        child.ContentError = null;
                        var dictionaryLanguage = child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!;
                        if (dictionaryTasks.TryGetValue(dictionaryLanguage, out var dictionaryTask))
                            child.DictionaryDetails = await dictionaryTask;
                        if (child.Material.ContextText == null)
                        {
                            var example = await exampleTasks[dictionaryLanguage];
                            if (example == null) throw new InvalidDataException("开放语料没有找到包含原样词块的真实例句；请补充上下文后重试。");
                            child.Material = AuthenticExampleLookup.Apply(child.Material, example.Value);
                            child.DictionaryDetails = child.DictionaryDetails with
                            { ExampleSentence = example.Value.Sentence, ExampleSource = example.Value.Source };
                            store.Save(child);
                        }
                        child.Content = await content.ProcessAsync(child.Material, child.ModeId, child.Configuration, child.Settings, cancellation);
                        child.EditedFields = null;
                        child.Times["previewReady"] = DateTimeOffset.UtcNow;
                    }
                    catch (OperationCanceledException) { child.ContentError = "已停止本模式处理，原材料已保留。"; }
                    catch (Exception ex) { child.ContentError = ex.Message; }
                    finally { store.Save(child); if (form?.IsDisposed == false) form.ContentReady(); }
                }));
                foreach (var child in children.Where(child => child.ConfirmedNote == null &&
                    (child.DictionaryDetails == null || child.DictionaryDetails.Target != child.Material.SelectedText.Trim() ||
                     child.DictionaryDetails.Language != child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language)))
                {
                    var dictionaryLanguage = child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!;
                    if (dictionaryTasks.TryGetValue(dictionaryLanguage, out var dictionaryTask))
                        child.DictionaryDetails = await dictionaryTask;
                    store.Save(child);
                }
                if (children.Length > 0) draft.DictionaryDetails = children[0].DictionaryDetails;
                store.Save(draft);
                if (form?.IsDisposed == false) form.ContentReady();
                draft.Times["previewReady"] = DateTimeOffset.UtcNow;
                return;
            }
            if (draft.Route.OutputKind == "language")
            {
                RefreshMissingModeConfiguration(draft);
                if (draft.ConfirmedNote == null && draft.NoteId == null &&
                    draft.Configuration.Modes.FirstOrDefault(mode => mode.Definition.Id == draft.ModeId)?.Definition.Language is { } draftLanguage)
                {
                    draft.DictionaryDetails ??= await new DirectDictionaryLookup(http).LookupAsync(draft.Material.SelectedText, draftLanguage, cancellation);
                    if (draft.Material.ContextText == null)
                    {
                        var example = await new AuthenticExampleLookup(http).FindWithFallbackAsync(draft.Material.SelectedText,
                            draftLanguage, draft.DictionaryDetails, cancellation)
                            ?? throw new InvalidDataException("开放语料没有找到包含原样词块的真实例句；请补充上下文后重试。");
                        draft.Material = AuthenticExampleLookup.Apply(draft.Material, example);
                        draft.DictionaryDetails = draft.DictionaryDetails with
                        { ExampleSentence = example.Sentence, ExampleSource = example.Source };
                        store.Save(draft);
                    }
                }
            }
            draft.Content = await content.ProcessAsync(draft.Material, draft.ModeId, draft.Configuration, draft.Settings, cancellation,
                draft.Input.Intent == "question" ? draft.QuestionText : null);
            if (draft.Route.OutputKind == "language" && draft.DictionaryDetails == null && draft.ConfirmedNote == null &&
                draft.Configuration.Modes.FirstOrDefault(mode => mode.Definition.Id == draft.ModeId)?.Definition.Language is { } language)
                draft.DictionaryDetails = await new DirectDictionaryLookup(http).LookupAsync(draft.Material.SelectedText, language, cancellation);
            if (draft.Content.Answer != null && draft.Conversation.Count == 0)
                draft.Conversation.Add(new ConversationTurn(draft.QuestionText, draft.Content.Answer));
            if (draft.Input.ReadRequested && draft.Route.OutputKind == "translation" && draft.Content.Basic != null && !draft.Times.ContainsKey("speechRequested"))
            {
                draft.Times["speechRequested"] = DateTimeOffset.UtcNow;
                _ = speech.SpeakTextAsync(draft, draft.Content.Basic.Back, SpeechPurpose.Translation, false, () => Changed(draft, null));
            }
            draft.Times["previewReady"] = DateTimeOffset.UtcNow;
            if (form?.IsDisposed == false) form.ContentReady();
        }
        catch (OperationCanceledException) { draft.ContentError = "已停止内容处理，原材料已保留。"; }
        catch (Exception ex) { draft.ContentError = ex.Message; }
        finally { if (form != null) form.ContentRunning = false; Changed(draft, form); }
    }

    private void RefreshMissingModeConfiguration(ClipDraft child)
    {
        var missing = child.Configuration.Modes.FirstOrDefault(m => m.Definition.Id == child.ModeId);
        if (missing == null || child.ConfirmedNote != null || child.NoteId != null ||
            missing.LoadError == null && child.Content != null && child.Content.Fields?.IsUnresolved != true) return;
        var available = ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides).Modes.FirstOrDefault(m => m.Definition.Id == child.ModeId);
        if (available == null || available.LoadError != null) return;
        // Retry refreshes unavailable or unresolved content; captured routing and successful siblings stay frozen.
        available = available with { Definition = missing.Definition with { PromptFile = available.Definition.PromptFile, SchemaFile = available.Definition.SchemaFile,
            ContentFormat = available.Definition.ContentFormat } };
        child.Configuration = child.Configuration with { Modes = child.Configuration.Modes.Select(m => m.Definition.Id == child.ModeId ? available : m).ToArray() };
    }

    private async Task ImportLanguageModesAsync(ClipDraft draft, LanguageDraftForm form, string[] modeIds)
    {
        if (form.LanguageImportRunning) return;
        form.LanguageImportRunning = true;
        try
        {
            draft.ImportError = null;
            using var request = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            await LanguageModeCards.ImportAsync(draft, modeIds, store, new CardImportRouter(request), () => Changed(draft, null));
        }
        catch (Exception ex) { draft.ImportError = ex.Message; }
        finally { form.LanguageImportRunning = false; Changed(draft, form); }
    }

    private async Task FollowUpAsync(ClipDraft draft, LanguageDraftForm form, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        draft.PendingFollowUp = text; draft.ConversationError = null; store.Save(draft);
        try
        {
            var content = new ContentModule(http, () => ContentCredentials.Resolve(DesktopCredentialSettings.Load(dataRoot).DeepSeekApiKey));
            var conversation = QuestionCards.EffectiveConversation(draft, store);
            var answer = await content.AnswerAsync(draft.ConversationMaterial ?? draft.Material, draft.ConversationContent ?? draft.PreviewContent, conversation, text,
                draft.Configuration, draft.Settings, form.Lifetime, currentPreview: new(draft.Original.SelectedText, draft.SupportsSpeech ? draft.PreviewContent : null, conversation,
                    draft.ModeCards.Count == 0 ? null : LanguageModeCards.Children(draft, store).Where(d => d.PreviewContent != null).Select(d => d.PreviewContent).ToArray()));
            draft.Conversation.Add(new ConversationTurn(text, answer));
            draft.PendingFollowUp = null;
            draft.Times["followUpAnswered"] = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { draft.ConversationError = "追问已停止，输入已保留。"; }
        catch (Exception ex) { draft.ConversationError = ex.Message; }
        finally { store.Save(draft); if (!form.IsDisposed) form.RefreshConversation(); }
    }

    private async Task ImportQuestionCardsAsync(ClipDraft draft, LanguageDraftForm form, int[] indexes)
    {
        if (form.QuestionImportRunning) return;
        form.QuestionImportRunning = true;
        try
        {
            using var request = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            await QuestionCards.ImportAsync(draft, indexes, store, new CardImportRouter(request), () => {
                if (!form.IsDisposed) form.RefreshConversation();
                Changed(draft, form);
            });
        }
        catch (Exception ex) { draft.ConversationImportError ??= ex.Message; }
        finally { form.QuestionImportRunning = false; Changed(draft, form); }
    }

    private void Changed(ClipDraft draft, LanguageDraftForm form)
    {
        if (owner.IsDisposed) return;
        if (owner.InvokeRequired) { owner.BeginInvoke((Action)(() => Changed(draft, form))); return; }
        RefreshCardLinks(draft);
        store.Save(draft);
        historyLookup.Remember(draft);
        foreach (var childId in draft.ModeCards.Values.Distinct())
            if (store.Find(childId) is { } child) historyLookup.Remember(child);
        DraftChanged?.Invoke(draft);
        foreach (var view in forms.ToArray())
            if (!view.IsDisposed)
            {
                RefreshCardLinks(view.Draft); view.RefreshStatus();
                DraftChanged?.Invoke(view.Draft);
            }
        if (latest != null) RefreshCardLinks(latest);
        if (lastSpeech != null) RefreshCardLinks(lastSpeech);
        if (ReferenceEquals(latest, draft)) status(DescribeStatus(draft));
    }

    internal string DescribeStatus(ClipDraft draft)
    {
        string state;
        if (draft.ModeCards.Count > 0)
        {
            var children = LanguageModeCards.Children(draft, store);
            var ready = children.Count(child => child.PreviewContent != null && child.PreviewContent.Fields?.IsUnresolved != true);
            var imported = children.Count(child => child.ImportState == ImportState.Verified);
            state = $"语言模式 {ready}/{children.Length} 就绪 · 已导入 {imported} 张";
            if (children.Any(child => child.AwaitingAnki)) state += " · 等待 Anki 打开后自动导入";
            if (children.Any(child => child.ContentError != null || child.ImportError != null || child.Content?.Fields?.IsUnresolved == true)) state += " · 部分待处理";
        }
        else if (draft.Content == null) state = draft.HistoryReplayOnly ? "历史词块 · 可继续制卡" : draft.ContentError != null ? "内容待重试" : "内容处理中";
        else state = draft.AwaitingAnki ? "等待 Anki 打开后自动导入" : draft.Content.Fields?.IsUnresolved == true ? PublishedLanguage.PendingReason(draft.Content.Fields) : draft.ImportState == ImportState.Verified ? ExportRouting.ResultSummary(draft) : "预览已就绪";
        if (draft.Conversation.Count > 0)
        {
            var verified = QuestionCards.Preview(draft, store).Count(card => card.State == ImportState.Verified);
            var questions = "问答 " + draft.Conversation.Count + " 轮 · 已导入 " + verified + " 张";
            state = draft.SupportsSpeech ? state + " · " + questions : questions;
        }
        if (draft.ConversationImportError != null || draft.ImportError != null) state += " · 导入待核对";
        return draft.SupportsSpeech ? state + " · " + draft.SpeechStatus : state;
    }

    internal void Stop() { languageEntryEpoch++; speech.Stop(); }
    private void RefreshCardLinks(ClipDraft draft)
    {
        var saved = store.Find(draft.Id);
        if (saved == null) return;
        foreach (var link in saved.FollowUpCards) draft.FollowUpCards.TryAdd(link.Key, link.Value);
        foreach (var link in saved.ModeCards) draft.ModeCards.TryAdd(link.Key, link.Value);
        if (saved.ConfirmedNote != null)
        {
            draft.ConfirmedNote = saved.ConfirmedNote; draft.ImportState = saved.ImportState;
            draft.NoteId = saved.NoteId; draft.CardIds = saved.CardIds; draft.ImportError = saved.ImportError;
            draft.AwaitingAnki = saved.AwaitingAnki;
            draft.ConfirmedDestination = saved.ConfirmedDestination; draft.ConfirmedRemNote = saved.ConfirmedRemNote; draft.RemNoteResult = saved.RemNoteResult;
        }
    }
    internal ClipDraft LiveDraft(ClipDraft draft)
    {
        var live = quickDrafts.TryGetValue(draft.Id, out var quick) ? quick : forms.FirstOrDefault(f => !f.IsDisposed && f.Draft.Id == draft.Id)?.Draft
            ?? (latest?.Id == draft.Id ? latest : lastSpeech?.Id == draft.Id ? lastSpeech : draft);
        RefreshCardLinks(live);
        return live;
    }

    internal void PendingImportChanged(ClipDraft draft)
    {
        if (owner.IsDisposed) return;
        Changed(LiveDraft(draft), null);
    }

    private Task PlayAsync(ClipDraft draft, bool replay)
    {
        draft = LiveDraft(draft);
        if (!draft.SupportsSpeech) return Task.CompletedTask;
        var translation = (draft.Route?.OutputKind ?? draft.Content?.ModeId) == "translation";
        var spokenText = translation ? draft.PreviewContent?.Basic?.Back : null;
        if (translation && string.IsNullOrWhiteSpace(spokenText)) return Task.CompletedTask;
        latest = draft;
        if (HistoryRecords.IsLanguage(draft)) lastSpeech = draft;
        draft.Times["speechRequested"] = DateTimeOffset.UtcNow;
        store.Save(draft);
        if (!translation && draft.ReusedSpeech is { } recorded)
        {
            try { return speech.SpeakRecordedAsync(draft, recorded.Text, recorded.Settings, recorded.AudioPath, () => Changed(draft, null)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            { draft.SpeechError = ex.Message; draft.SpeechStatus = "历史录音不可用"; Changed(draft, null); return Task.CompletedTask; }
        }
        return translation
            ? speech.SpeakTextAsync(draft, spokenText, SpeechPurpose.Translation, replay, () => Changed(draft, null))
            : speech.SpeakAsync(draft, replay, () => Changed(draft, null));
    }

    internal void ReplayLastLanguage()
    {
        languageEntryEpoch++;
        var target = lastSpeech ?? HistoryRecords.LastLanguageSpeech(store);
        if (target != null) _ = PlayAsync(target, true);
        else status("还没有可重播的语言记录；双击可查看历史记录");
    }

    internal void PlayHistory(ClipDraft draft)
    {
        languageEntryEpoch++;
        if (HistoryRecords.IsLanguage(draft)) _ = PlayAsync(draft, true);
    }

    internal void ContinueHistory(ClipDraft draft)
    {
        var form = OpenDraft(LiveDraft(draft)); latest = form.Draft;
        form.Draft.HistoryReplayOnly = false;
        if (form.Draft.Content != null) form.ContentReady();
        else _ = ProcessAsync(form.Draft, form);
    }

    public void Dispose() { lifetime.Cancel(); meaningPopup?.Dispose(); inputForm?.Close(); foreach (var f in forms.ToArray()) f.Close(); speech.Dispose(); http.Dispose(); lifetime.Dispose(); }
}
