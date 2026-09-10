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
    public string SpokenText => ContextText ?? SelectedText;
    public bool NeedsContextChoice => ContextOrigin == "unavailable";
    public void Validate()
    {
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

public sealed record LanguageFields(string Pronunciation, string Meaning, string? GeneratedSentence, int? GeneratedTargetStart);
public sealed record BasicFields(string Front, string Back);
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
public sealed record InputMetadata(string Source = "selection", string Intent = "auto", bool QuestionMarkRouting = false, bool ReadRequested = false);
public sealed record ModeRoute(string ModeId, string OutputKind, bool FollowUpDefault, bool RequiresContext);
public sealed record ContentResult(string ModeId, LanguageFields? Fields = null, BasicFields? Basic = null, QuestionAnswer? Answer = null);
public sealed record ContentPreview(string SelectedText, ContentResult? Content);
public sealed record ModeDefinition(string Id, string Name, bool Enabled, string Description, string PromptFile, string SchemaFile,
    string OutputKind = "language", bool FollowUpDefault = false, bool RequiresContext = true);
public sealed record ModeFile(ModeDefinition[] Modes);
public sealed record ModeSnapshot(ModeDefinition Definition, string Prompt, string Schema);
public sealed record ContentConfiguration(string ClassificationPrompt, ModeSnapshot[] Modes);

public sealed record LanguageSettings
{
    public string ApiBaseUrl { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public string AnkiEndpoint { get; init; } = "http://127.0.0.1:8765";
    public string AnkiDeck { get; init; } = "零碎知识剪藏::语言学习";
    public string AnkiModel { get; init; } = "零碎知识剪藏 · 语言共享字段";
    public PracticeChoice Practice { get; init; } = PracticeChoice.Both;
    public bool GlobalSelection { get; init; }
    public bool TypedInputAutoRead { get; init; } = true;
    public bool ContextEnabled { get; init; }
    public string ContextGranularity { get; init; } = "sentence";
    public string PythonPath { get; init; } = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe";
    public string NodePath { get; init; } = @"C:\Program Files\nodejs\node.exe";
    public string FfplayPath { get; init; } = @"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe";
    public string Voice { get; init; } = "zh-CN-XiaoxiaoNeural";
    public string EnglishVoice { get; init; } = "en-US-AvaMultilingualNeural";
    public bool AutoVoice { get; init; } = true;
    public int RatePercent { get; init; }
    public int VolumePercent { get; init; }
    public int PitchHz { get; init; }
    public void Validate()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var api) || api.Scheme != "https" && !api.IsLoopback)
            throw new InvalidDataException("内容 API 地址必须使用 HTTPS。");
        if (!Uri.TryCreate(AnkiEndpoint, UriKind.Absolute, out var anki) || !anki.IsLoopback)
            throw new InvalidDataException("AnkiConnect 地址必须是本机地址。");
        if (string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(AnkiDeck) || string.IsNullOrWhiteSpace(AnkiModel))
            throw new InvalidDataException("请填写模型、Anki 牌组和笔记类型。");
        if (RatePercent is < -50 or > 100 || VolumePercent is < -100 or > 100 || PitchHz is < -50 or > 50)
            throw new InvalidDataException("朗读速度、音量或音调超出范围。");
    }
}

public sealed class ClipDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required CaptureMaterial Original { get; init; }
    public required CaptureMaterial Material { get; set; }
    public required LanguageSettings Settings { get; init; }
    public required ContentConfiguration Configuration { get; init; }
    public string? ModeId { get; set; }
    public InputMetadata Input { get; init; } = new();
    public ModeRoute? Route { get; set; }
    public ContentResult? Content { get; set; }
    public LanguageFields? EditedFields { get; set; }
    public BasicFields? EditedBasic { get; set; }
    public List<ConversationTurn> Conversation { get; set; } = [];
    public Dictionary<int, string> FollowUpCards { get; set; } = new();
    public CaptureMaterial? ConversationMaterial { get; init; }
    public ContentResult? ConversationContent { get; init; }
    public string? PendingFollowUp { get; set; }
    public string? ConversationError { get; set; }
    public string? ParentDraftId { get; init; }
    public string? ContentError { get; set; }
    public string? SpeechError { get; set; }
    public string? AudioPath { get; set; }
    public string SpeechStatus { get; set; } = "未朗读";
    public PracticeChoice Practice { get; set; }
    public AnkiNoteDraft? ConfirmedNote { get; set; }
    public ImportState ImportState { get; set; }
    public long? NoteId { get; set; }
    public long[] CardIds { get; set; } = [];
    public string? ImportError { get; set; }
    public ConcurrentDictionary<string, DateTimeOffset> Times { get; set; } = new();
    [JsonIgnore] public SemaphoreSlim ImportGate { get; } = new(1, 1);
    [JsonIgnore] public ContentResult? PreviewContent => Content == null ? null : Content with
        { Fields = EditedFields ?? Content.Fields, Basic = EditedBasic ?? Content.Basic };
    [JsonIgnore] public DateTimeOffset? LastSpeechAt => Times.TryGetValue("speechRequested", out var requested) ? requested :
        Times.TryGetValue("speechStarted", out var started) ? started : AudioPath != null ? Original.CapturedAt : null;
}

public sealed class DraftStore(string root)
{
    public void Save(ClipDraft draft) => WorkflowJson.Save(Path.Combine(root, "language-drafts", draft.Id + ".json"), draft);
    public ClipDraft? Find(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        var path = Path.Combine(root, "language-drafts", id + ".json");
        if (!File.Exists(path)) return null;
        var draft = WorkflowJson.Read<ClipDraft>(path);
        if (draft.ImportState == ImportState.Writing) draft.ImportState = ImportState.Unknown;
        return draft;
    }
    public IEnumerable<ClipDraft> Recent() => ReadDrafts().Take(20);
    public IEnumerable<ClipDraft> RecentSpeech() => ReadDrafts().Where(d => d.LastSpeechAt != null)
        .OrderByDescending(d => d.LastSpeechAt).Take(20);
    private IEnumerable<ClipDraft> ReadDrafts()
    {
        var path = Path.Combine(root, "language-drafts");
        if (!Directory.Exists(path)) yield break;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            ClipDraft? draft = null;
            try { draft = WorkflowJson.Read<ClipDraft>(file.FullName); } catch (JsonException) { } catch (IOException) { }
            if (draft != null) { if (draft.ImportState == ImportState.Writing) draft.ImportState = ImportState.Unknown; yield return draft; }
        }
    }
}
