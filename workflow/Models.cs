using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace KnowledgeClipper.Workflow;

public static class WorkflowJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("文件内容为空：" + Path.GetFileName(path));
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
}

public sealed record CaptureMaterial(string SelectedText, string? ContextText, string ContextOrigin,
    int? TargetStart, string SourceTitle, long SourceWindow, DateTimeOffset CapturedAt)
{
    public string? LookupContextText { get; init; }
    public ImageAsset? FrontImage { get; init; }
    public bool VisualQuestion { get; init; }
    public string SpokenText => ContextText ?? SelectedText;
    public bool NeedsContextChoice => ContextOrigin == "unavailable";
    public void Validate()
    {
        FrontImage?.ReadVerified();
        if (VisualQuestion && FrontImage == null) throw new InvalidDataException("视觉问答缺少原图。");
        if (string.IsNullOrWhiteSpace(SelectedText)) throw new InvalidDataException("请先选中文字。");
        if (ContextText != null && (TargetStart is not int start || start < 0 || start > ContextText.Length - SelectedText.Length ||
            !ContextText.AsSpan(start, SelectedText.Length).SequenceEqual(SelectedText)))
            throw new InvalidDataException("选中目标在原文中的位置无法确认，请补充原文或允许生成例句。");
        if (ContextText == null && TargetStart != null) throw new InvalidDataException("缺少目标所在原文。");
        if (ContextOrigin is not ("source" or "user" or "generated_allowed" or "unavailable"))
            throw new InvalidDataException("上下文来源无效。");
        if (ContextOrigin is "source" or "user" && ContextText == null) throw new InvalidDataException("缺少原文。");
    }
}

public enum PracticeChoice { Both, Reading, Writing }
public enum ImportState { Unconfirmed, Ready, Writing, Unknown, Verified, Failed }
public enum ExportDestination { Anki, RemNote }

public sealed record LanguageFields(string? Pronunciation, string? Meaning, string? GeneratedSentence, int? GeneratedTargetStart)
{
    // Absent in saved four-field drafts. A new result cannot silently reinterpret legacy content.
    public string? Format { get; init; }
    public string? Status { get; init; }
    public string[] UnresolvedReasons { get; init; } = [];
    public string? ExplanationEn { get; init; }
    public string? SentenceTranslationZh { get; init; }
    public string? Lemma { get; init; }
    public string? NormalizedSentence { get; init; }
    [JsonIgnore] public bool IsUnresolved => Status == "unresolved";
}
public sealed record BasicFields(string Front, string Back, string? Context = null);
public sealed record QuestionCardPreview(int Index, string Question, string Answer, string Context, ImportState State, bool Frozen, string? Error)
{
    public string DestinationLabel { get; init; } = "";
}
public sealed record QuestionAnswer(string AnswerState, string? AnswerText, string[] UnresolvedReasons)
{
    [JsonIgnore] public bool Answered => AnswerState == "answered";
    [JsonIgnore] public string DisplayText => Answered ? AnswerText! : "暂未解答：" + string.Join("；", UnresolvedReasons.Select(reason => reason switch
    {
        "missing_facts" => "缺少必要条件，请补充相关事实",
        "conflicting_context" => "已有信息存在冲突，请说明以哪项为准",
        "unsafe_high_risk" => "现有信息不足以可靠回答，请补充条件",
        "cannot_answer_reliably" => "暂时无法可靠回答，可以补充条件继续追问",
        _ => reason
    }));
}
public sealed record ConversationTurn(string Question, QuestionAnswer Answer);
public sealed record InputMetadata(string Source = "selection", string Intent = "auto", bool QuestionMarkRouting = false, bool ReadRequested = false,
    string? ExplicitQuestion = null);
public sealed record ModeRoute(string ModeId, string OutputKind, bool FollowUpDefault, bool RequiresContext);
public sealed record ContentResult(string ModeId, LanguageFields? Fields = null, BasicFields? Basic = null, QuestionAnswer? Answer = null);
public sealed record ContentPreview(string SelectedText, ContentResult? Content, IReadOnlyList<ConversationTurn>? Conversation = null,
    IReadOnlyList<ContentResult>? ModeContents = null);
public sealed record ModeDefinition(string Id, string Name, bool Enabled, string Description, string PromptFile, string SchemaFile,
    string OutputKind = "language", bool FollowUpDefault = false, bool RequiresContext = true, string? AnkiDeckPath = null,
    string? Language = null, PracticeChoice? Practice = null, string? ContentFormat = null);
public sealed record ModeFile(ModeDefinition[] Modes);
public sealed record ModeSnapshot(ModeDefinition Definition, string Prompt, string Schema, string? LoadError = null);
public sealed record ContentConfiguration(string ClassificationPrompt, ModeSnapshot[] Modes);

public sealed record LanguageSettings
{
    public string ApiBaseUrl { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public int ContentTimeoutSeconds { get; init; } = 90;
    public int ContentMaxRetries { get; init; } = 1;
    public string AnkiEndpoint { get; init; } = "http://127.0.0.1:8765";
    public Dictionary<string, ExportDestination> ModeDestinations { get; init; } = new(StringComparer.Ordinal);
    public string RemNoteSdkEndpoint { get; init; } = RemNoteConnection.DefaultEndpoint;
    public string RemNoteRootTitle { get; init; } = "零碎知识剪藏";
    public string? RemNoteRootId { get; init; }
    public string AnkiDeck { get; init; } = "零碎知识剪藏::语言学习";
    // Null preserves the exact destination in drafts saved before hierarchical routing.
    public string? AnkiRootDeck { get; init; }
    public string AnkiModel { get; init; } = "零碎知识剪藏 · 语言共享字段";
    public PracticeChoice Practice { get; init; } = PracticeChoice.Both;
    public bool GlobalSelection { get; init; }
    public bool SelectionLeftExcerpt { get; init; }
    public bool ClipboardImageDotEnabled { get; init; } = true;
    public bool QuickLanguage { get; init; } = true;
    public bool QuickQuestion { get; init; }
    public bool QuickExcerpt { get; init; } = true;
    public bool QuickScreenshot { get; init; }
    public bool LanguageAutoRead { get; init; } = true;
    public string ExcerptDeckPath { get; init; } = "摘录";
    public string? OriginalExcerptDocumentId { get; init; }
    public string? OriginalExcerptDocumentTitle { get; init; }
    public string OriginalExcerptHotkey { get; init; } = "Alt+Shift+《";
    public string ReplayLastHotkey { get; init; } = "Alt+Shift+/";
    public string ExcerptModesDirectory { get; init; } = "";
    public string TypedDefaultRoute { get; init; } = MaterialRouting.Punctuation;
    public string OcrDefaultRoute { get; init; } = MaterialRouting.Ask;
    public string OriginalDefaultRoute { get; init; } = "image-answer";
    public bool? OriginalImageMode { get; init; }
    public string ImageModel { get; init; } = "deepseek-flash";
    public string MineruScriptPath { get; init; } = "";
    public string MineruLanguage { get; init; } = "ch";
    public bool MineruTable { get; init; } = true;
    public bool MineruFormula { get; init; } = true;
    public int MineruTimeoutSeconds { get; init; } = 900;
    public bool TypedInputAutoRead { get; init; } = true;
    public bool QuestionCardIncludeContext { get; init; } = true;
    public bool ShowDictionaryInPreview { get; init; }
    public bool ContextEnabled { get; init; }
    public string ContextGranularity { get; init; } = "sentence";
    public string PythonPath { get; init; } = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe";
    public string NodePath { get; init; } = @"C:\Program Files\nodejs\node.exe";
    public string FfplayPath { get; init; } = @"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe";
    public string Voice { get; init; } = "zh-CN-XiaoxiaoNeural";
    public string EnglishVoice { get; init; } = "en-US-AnaNeural";
    public bool AutoVoice { get; init; } = true;
    public bool SpeechAutoPlay { get; init; } = true;
    public int RatePercent { get; init; }
    public int VolumePercent { get; init; }
    public int PitchHz { get; init; }
    public void Validate()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var api) || api.Scheme != "https" && !api.IsLoopback)
            throw new InvalidDataException("内容 API 地址必须使用 HTTPS。");
        if (!Uri.TryCreate(AnkiEndpoint, UriKind.Absolute, out var anki) || !anki.IsLoopback)
            throw new InvalidDataException("AnkiConnect 地址必须是本机地址。");
        if (!Uri.TryCreate(RemNoteSdkEndpoint, UriKind.Absolute, out var remnote) || !remnote.IsLoopback || remnote.Scheme is not ("http" or "https") ||
            remnote.UserInfo.Length > 0 || remnote.Query.Length > 0 || remnote.Fragment.Length > 0)
            throw new InvalidDataException("RemNote SDK 地址必须是本机 HTTP 地址。");
        if (string.IsNullOrWhiteSpace(RemNoteRootTitle) || ModeDestinations == null || ModeDestinations.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !Enum.IsDefined(pair.Value)))
            throw new InvalidDataException("导出目录或模式导出对象无效。");
        if (string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(AnkiDeck) || string.IsNullOrWhiteSpace(AnkiModel))
            throw new InvalidDataException("请填写模型、Anki 牌组和笔记类型。");
        if (AnkiRootDeck != null) DeckRouting.ValidateRoot(AnkiRootDeck);
        DeckRouting.ValidateRoot(ExcerptDeckPath);
        if ((OriginalExcerptDocumentId == null) != (OriginalExcerptDocumentTitle == null) ||
            OriginalExcerptDocumentId is { Length: > 200 } ||
            OriginalExcerptDocumentTitle is { Length: > 200 } ||
            OriginalExcerptDocumentId != null && (string.IsNullOrWhiteSpace(OriginalExcerptDocumentId) || string.IsNullOrWhiteSpace(OriginalExcerptDocumentTitle)))
            throw new InvalidDataException("原文摘录的 RemNote 目录选择无效。");
        var targetHotkey = ExcerptHotkey.Parse(OriginalExcerptHotkey);
        var replayHotkey = ExcerptHotkey.Parse(ReplayLastHotkey);
        if (targetHotkey.VirtualKey != 0 && targetHotkey == replayHotkey)
            throw new InvalidDataException("RemNote 导入目标与重读上一条不能使用相同快捷键。");
        if (ExcerptModesDirectory.Length > 0 && !Path.IsPathFullyQualified(ExcerptModesDirectory))
            throw new InvalidDataException("摘录提示词目录请填写完整路径，或留空使用程序自带目录。");
        if (MineruScriptPath.Length > 0 && !Path.IsPathFullyQualified(MineruScriptPath))
            throw new InvalidDataException("MinerU 程序请填写完整路径。");
        if (MineruTimeoutSeconds is < 30 or > 3600 || string.IsNullOrWhiteSpace(MineruLanguage) || string.IsNullOrWhiteSpace(ImageModel))
            throw new InvalidDataException("请填写图片模型、OCR 语言，识别超时应为 30–3600 秒。");
        if (RatePercent is < -50 or > 100 || VolumePercent is < -100 or > 100 || PitchHz is < -50 or > 50)
            throw new InvalidDataException("朗读速度、音量或音调超出范围。");
        if (ContentTimeoutSeconds is < 1 or > 600 || ContentMaxRetries is < 0 or > 5)
            throw new InvalidDataException("内容超时应为 1–600 秒，失败重试次数应为 0–5 次。");
        if (string.IsNullOrWhiteSpace(Voice) || string.IsNullOrWhiteSpace(EnglishVoice))
            throw new InvalidDataException("请选择或填写中文与英文音色。");
        if (ContextGranularity is not ("sentence" or "paragraph") || !Enum.IsDefined(Practice))
            throw new InvalidDataException("上下文范围或练习方式无效。");
        foreach (var path in new[] { PythonPath, NodePath, FfplayPath })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("运行程序请填写完整路径。");
    }
}

public sealed record SpeechAudioReference(string SourceDraftId, string Text, LanguageSettings Settings, string AudioPath);
public sealed record LanguageLookupResult(string MeaningZh, string? Pronunciation, string? ContextText);
public sealed record DictionarySense(string Definition, string[] Examples);
public sealed record DictionaryEntry(string PartOfSpeech, DictionarySense[] Senses);
public sealed record DirectDictionaryDetails(string Target, string Language, string? Translation, string? PartOfSpeech,
    string? Definition, string? Example, string Status, string? Error = null)
{
    public DictionaryEntry[] Entries { get; init; } = [];
    public string? Etymology { get; init; }
    public string? ExampleSentence { get; init; }
    public string? ExampleSource { get; init; }
    public string DisplayText
    {
        get
        {
            var lines = new List<string> { "划词词典 · " + Target };
            if (!string.IsNullOrWhiteSpace(Translation)) lines.Add("旧版翻译参考（未核实）：" + Translation);
            if (Entries.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(PartOfSpeech)) lines.Add("词性：" + PartOfSpeech);
                if (!string.IsNullOrWhiteSpace(Definition)) lines.Add("释义：" + Definition);
                if (!string.IsNullOrWhiteSpace(Example)) lines.Add("例句：" + Example);
            }
            else
            {
                foreach (var entry in Entries)
                {
                    lines.Add("词性：" + entry.PartOfSpeech);
                    for (var i = 0; i < entry.Senses.Length; i++)
                    {
                        lines.Add("释义 " + (i + 1) + "：" + entry.Senses[i].Definition);
                        foreach (var example in entry.Senses[i].Examples) lines.Add("例句：" + example);
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(Etymology)) lines.Add("词源：" + Etymology);
            if (Status == "unavailable") lines.Add("开放词典暂不可用，可在草稿中重试。");
            lines.Add("来源：Wiktionary（词性、释义、例句、词源）。词义未按当前语境消歧。");
            return string.Join("\n", lines);
        }
    }
}

public sealed class ClipDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required CaptureMaterial Original { get; init; }
    public required CaptureMaterial Material { get; set; }
    public required LanguageSettings Settings { get; init; }
    public required ContentConfiguration Configuration { get; set; }
    public string? ModeId { get; set; }
    public InputMetadata Input { get; init; } = new();
    public ModeRoute? Route { get; set; }
    public ContentResult? Content { get; set; }
    public LanguageFields? EditedFields { get; set; }
    public BasicFields? EditedBasic { get; set; }
    public ExcerptFields? Excerpt { get; set; }
    public bool IsOriginalExcerpt { get; set; }
    public List<ConversationTurn> Conversation { get; set; } = [];
    // Optional: legacy histories and frozen cards are never migrated on read.
    public ChatSession? Chat { get; set; }
    public QuestionTypeTarget? QuestionTarget { get; set; }
    public Dictionary<int, string> FollowUpCards { get; set; } = new();
    public Dictionary<string, string> ModeCards { get; set; } = new();
    public string[] SelectedModeIds { get; set; } = [];
    public string? ModeParentId { get; init; }
    public string? GeneratedExampleDraftId { get; set; }
    public Dictionary<int, string> EditedAnswers { get; set; } = new();
    public string? ConversationImportError { get; set; }
    public CaptureMaterial? ConversationMaterial { get; init; }
    public ContentResult? ConversationContent { get; init; }
    public string? PendingFollowUp { get; set; }
    public string? ConversationError { get; set; }
    public string? ParentDraftId { get; init; }
    public string? ContentError { get; set; }
    public string? SpeechError { get; set; }
    public string? AudioPath { get; set; }
    public SpeechAudioReference? ReusedSpeech { get; set; }
    public LanguageLookupResult? Lookup { get; set; }
    public string? LookupError { get; set; }
    public DirectDictionaryDetails? DictionaryDetails { get; set; }
    public string? MatchedHistoryId { get; set; }
    public bool HistoryReplayOnly { get; set; }
    public string SpeechStatus { get; set; } = "未朗读";
    public PracticeChoice Practice { get; set; }
    public AnkiNoteDraft? ConfirmedNote { get; set; }
    public ExportDestination? ConfirmedDestination { get; set; }
    public RemNoteExportPlan? ConfirmedRemNote { get; set; }
    public RemNoteImportReceipt? RemNoteResult { get; set; }
    public string? RemNoteRevealError { get; set; }
    public ImportState ImportState { get; set; }
    public long? NoteId { get; set; }
    public long[] CardIds { get; set; } = [];
    public string? ImportError { get; set; }
    public bool AwaitingAnki { get; set; }
    public ConcurrentDictionary<string, DateTimeOffset> Times { get; set; } = new();
    [JsonIgnore] public SemaphoreSlim ImportGate { get; } = new(1, 1);
    [JsonIgnore] public SemaphoreSlim ConversationImportGate { get; } = new(1, 1);
    [JsonIgnore] public SemaphoreSlim ModeImportGate { get; } = new(1, 1);
    [JsonIgnore] public string QuestionText => Input.ExplicitQuestion ?? Original.SelectedText;
    [JsonIgnore] public ContentResult? PreviewContent => Content == null ? null : Content with
        { Fields = EditedFields ?? Content.Fields, Basic = EditedBasic ?? Content.Basic };
    [JsonIgnore] public bool SupportsSpeech
    {
        get
        {
            var kind = Route?.OutputKind ?? Configuration.Modes.FirstOrDefault(m => m.Definition.Id == ModeId)?.Definition.OutputKind
                ?? Content?.ModeId ?? ModeId;
            return Input.Intent != "question" && (kind == null ? !(Input.QuestionMarkRouting && ContentModule.IsQuestion(Original.SelectedText)) : kind is "language" or "translation");
        }
    }
    [JsonIgnore] public DateTimeOffset? LastSpeechAt => Times.TryGetValue("speechRequested", out var requested) ? requested :
        Times.TryGetValue("speechStarted", out var started) ? started : AudioPath != null ? Original.CapturedAt : null;
}

public sealed class DraftStore(string root, string folder = "language-drafts")
{
    private Dictionary<string, ClipDraft>? readSnapshot;
    public string DirectoryPath => Path.GetFullPath(Path.Combine(root, folder));
    public string Identity(string id) => Path.Combine(DirectoryPath, id + ".json");
    // A presentation load owns this short-lived snapshot. Live/resumed drafts still
    // use Find on the writable store, with their original gates and identities.
    public DraftStore CreateReadSnapshot() => new(root, folder) { readSnapshot = ReadDrafts().ToDictionary(d => d.Id, StringComparer.Ordinal) };
    public void Save(ClipDraft draft)
    {
        if (readSnapshot != null) throw new InvalidOperationException("历史快照仅用于读取。");
        WorkflowJson.Save(Path.Combine(root, folder, draft.Id + ".json"), draft);
    }
    public ClipDraft? Find(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        if (readSnapshot != null && readSnapshot.TryGetValue(id, out var saved)) return saved;
        var path = Path.Combine(root, folder, id + ".json");
        if (!File.Exists(path)) return null;
        var draft = WorkflowJson.Read<ClipDraft>(path);
        if (draft.ImportState == ImportState.Writing) draft.ImportState = ImportState.Unknown;
        return draft;
    }
    public IEnumerable<ClipDraft> Recent() => ReadDrafts().Take(20);
    public IEnumerable<ClipDraft> AllSessions()
    {
        var snapshot = ReadDrafts().ToArray();
        var byId = snapshot.ToDictionary(draft => draft.Id, StringComparer.Ordinal);
        return snapshot.Where(draft => IsSession(draft, id => byId.GetValueOrDefault(id))).Select(draft =>
        {
            HistoryRecords.RestoreChatCards(draft, id => byId.GetValueOrDefault(id));
            return draft;
        });
    }
    public IEnumerable<ClipDraft> RecentSessions() => AllSessions().Take(20);
    public IEnumerable<ClipDraft> RecentSpeech() => ReadDrafts().Where(d => d.LastSpeechAt != null && d.SupportsSpeech)
        .OrderByDescending(d => d.LastSpeechAt).Take(20);
    private bool IsSession(ClipDraft draft, Func<string, ClipDraft?> find)
    {
        if (draft.ModeParentId != null)
        {
            try { if (find(draft.ModeParentId)?.ModeCards.Values.Contains(draft.Id) == true) return false; }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        if (draft.ParentDraftId == null || draft.PendingFollowUp != null || draft.ConversationError != null || draft.ConversationImportError != null) return true;
        ClipDraft? parent;
        try { parent = find(draft.ParentDraftId); }
        catch (Exception ex) when (ex is JsonException or IOException) { return true; }
        if (HistoryRecords.IsChatExportChild(draft, parent) && HistoryRecords.ResolveChatSession(draft, find).Id != draft.Id) return false;
        if (parent == null || !parent.FollowUpCards.Values.Contains(draft.Id) || draft.Conversation.Count > parent.Conversation.Count) return true;
        // Old single-card previews could grow their own conversation; keep those branches accessible.
        return draft.Conversation.Where((turn, index) => {
            var previous = parent.Conversation[index];
            return turn.Question != previous.Question || turn.Answer.AnswerState != previous.Answer.AnswerState ||
                turn.Answer.AnswerText != previous.Answer.AnswerText || !turn.Answer.UnresolvedReasons.SequenceEqual(previous.Answer.UnresolvedReasons);
        }).Any();
    }
    private IEnumerable<ClipDraft> ReadDrafts()
    {
        if (readSnapshot != null)
        {
            foreach (var saved in readSnapshot.Values) yield return saved;
            yield break;
        }
        var path = Path.Combine(root, folder);
        if (!Directory.Exists(path)) yield break;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            ClipDraft? draft = null;
            try { draft = WorkflowJson.Read<ClipDraft>(file.FullName); } catch (JsonException) { } catch (IOException) { }
            if (draft != null) { if (draft.ImportState == ImportState.Writing) draft.ImportState = ImportState.Unknown; yield return draft; }
        }
    }
}
