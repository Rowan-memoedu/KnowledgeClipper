using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public sealed record AnkiNoteDraft(string Deck, string Model, Dictionary<string, string> Fields, string[] Tags, int[] Ordinals,
    Dictionary<int, string>? CardDecks = null)
{
    public ImageAsset[] Media { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageQuestionContext { get; init; }
    public const string BasicModelName = "零碎知识剪藏 · 问答与翻译";
    public const string DictionaryImageModelName = "零碎知识剪藏 · 原图语言词典20260923";
    public const string QuestionModelName = "零碎知识剪藏 · 多轮问答";
    public const string ExcerptModelName = "零碎知识剪藏 · 摘录";
    public const string ExcerptClozeModelName = "零碎知识剪藏 · 摘录挖空";
    [JsonIgnore] public bool IsExcerpt => Model is ExcerptModelName or ExcerptClozeModelName;
    [JsonIgnore] public bool IsCloze => Model == ExcerptClozeModelName;
    [JsonIgnore] public bool IsBasic => Fields.ContainsKey("Front") && Fields.ContainsKey("Back");
    [JsonIgnore] public bool IsDictionaryImage => Model == DictionaryImageModelName;
    [JsonIgnore] public bool HasQuestionContext => IsBasic && Fields.ContainsKey("Context");
    [JsonIgnore] public bool IsPublishedLanguage => Fields.GetValueOrDefault("LanguageFormat") == PublishedLanguage.Format;
    [JsonIgnore] public bool HasDictionaryDetails => Fields.ContainsKey("DictionaryDetails");
    public string DeckFor(int ordinal) => CardDecks == null ? Deck :
        CardDecks.TryGetValue(ordinal, out var deck) && !string.IsNullOrWhiteSpace(deck) ? deck :
        throw new InvalidDataException("已确认卡片缺少对应模板的目标牌组。");
    public static string Html(string value) => WebUtility.HtmlEncode(value).Replace("\r\n", "\n").Replace("\n", "<br>");
    public (string Front, string Back) PublishedFaces(int ordinal)
    {
        if (!IsPublishedLanguage || ordinal is not (0 or 1)) throw new InvalidDataException("不是正式语言卡片。");
        string Value(string name) => WebUtility.HtmlDecode(Fields[name].Replace("<br>", "\n"));
        var english = Fields["EnglishMode"] == "1";
        var target = Value("Target"); var pronunciation = Value("Pronunciation");
        var before = Value("SentenceBefore"); var after = Value("SentenceAfter");
        var dictionary = HasDictionaryDetails ? "\n\n" + Value("DictionaryDetails") : "";
        if (ordinal == 0) return (before + target + after, (english
            ? string.Join("\n", pronunciation, Value("ExplanationEn"), Value("SentenceTranslationZh"))
            : string.Join("\n", target, pronunciation, Value("Meaning"))) + dictionary);
        return (before + (english ? pronunciation : "（" + pronunciation + "；" + Value("Meaning") + "）") + after +
            (english ? "\n" + Value("ExplanationEn") : ""), target + (english ? "\n" + Value("SentenceTranslationZh") : "") + dictionary);
    }
    public static AnkiNoteDraft From(ClipDraft draft, bool allowEmptyBasic = false)
    {
        var material = draft.Material;
        material.Validate();
        if (draft.Excerpt is { } excerpt)
        {
            ExcerptCards.Validate(excerpt);
            var assignment = DeckRouting.Resolve(draft, [0]);
            return new(assignment.Deck, excerpt.Kind == ExcerptCardKind.Cloze ? ExcerptClozeModelName : ExcerptModelName,
                new() { ["ClipId"] = draft.Id, ["Front"] = ImageHtml(excerpt.FrontImages) + Html(excerpt.Front),
                    ["Back"] = ImageHtml(excerpt.BackImages) + Html(excerpt.Back), ["Source"] = Html(material.SourceTitle) },
                ["KnowledgeClipper", "kc_" + draft.Id], [0], assignment.CardDecks)
                { Media = excerpt.FrontImages.Concat(excerpt.BackImages).DistinctBy(i => i.FileName).ToArray() };
        }
        var modeId = draft.Route?.ModeId ?? draft.ModeId ?? draft.Content?.ModeId;
        var definition = draft.Configuration.Modes.SingleOrDefault(mode => mode.Definition.Id == modeId)?.Definition;
        if (draft.Content?.Basic is { } basic)
        {
            var question = definition?.OutputKind == "question" || draft.Route?.OutputKind == "question" ||
                draft.Content.Answer != null || draft.Content.ModeId == "question";
            var front = question ? draft.QuestionText : draft.Original.SelectedText;
            if (string.IsNullOrWhiteSpace(front)) throw new InvalidDataException("本次问题或原始输入不能为空。");
            if (!string.Equals(basic.Front, front, StringComparison.Ordinal))
                throw new InvalidDataException("卡片正面必须保留本次问题或原始输入。");
            if (string.IsNullOrWhiteSpace(basic.Back) && !allowEmptyBasic) throw new InvalidDataException("译文或答案尚未完成。");
            var imageFront = material.FrontImage != null;
            var back = imageFront && draft.ParentDraftId != null ? front + "\n\n" + basic.Back : basic.Back;
            if (imageFront && !string.IsNullOrEmpty(basic.Context)) back = basic.Context + "\n\n" + back;
            var basicFields = new Dictionary<string, string>
            {
                ["ClipId"] = draft.Id,
                ["Front"] = material.FrontImage is { } picture ? ImageHtml([picture]) : Html(front),
                ["Back"] = Html(back),
                ["Source"] = Html(material.SourceTitle)
            };
            if (!imageFront && basic.Context != null) basicFields["Context"] = Html(basic.Context);
            var assignment = DeckRouting.Resolve(draft, [0]);
            return new(assignment.Deck, !imageFront && basic.Context != null ? QuestionModelName : BasicModelName,
                basicFields, ["KnowledgeClipper", "kc_" + draft.Id], [0], assignment.CardDecks)
                { Media = material.FrontImage == null ? [] : [material.FrontImage], ImageQuestionContext = imageFront ? basic.Context : null };
        }
        var fields = draft.Content?.Fields ?? throw new InvalidOperationException("内容尚未完成。");
        var published = PublishedLanguage.Applies(definition);
        if (published)
        {
            PublishedLanguage.Validate(definition!, material, fields);
            if (fields.IsUnresolved) throw new InvalidDataException(PublishedLanguage.PendingReason(fields));
            if (draft.DictionaryDetails is { } dictionary &&
                (dictionary.Target != material.SelectedText.Trim() || dictionary.Language != definition!.Language))
                throw new InvalidDataException("划词词典结果与本次选区或语言模式不符。");
        }
        else ContentModule.ValidateFields(material, fields);
        var sentence = published ? PublishedLanguage.EffectiveSentence(material, fields) : material.ContextText ?? fields.GeneratedSentence!;
        var start = published ? PublishedLanguage.EffectiveTargetStart(material, fields) : material.TargetStart ?? fields.GeneratedTargetStart!.Value;
        var practice = draft.Practice;
        if (definition is { OutputKind: "language" } && (definition.Language != null || definition.Practice != null))
        {
            if (definition.Language is not ("zh" or "en") || definition.Practice is not (PracticeChoice.Reading or PracticeChoice.Writing))
                throw new InvalidDataException("具体语言模式必须声明语种与单一练习类型。");
            if (draft.Content.ModeId != definition.Id || draft.ModeId != null && draft.ModeId != definition.Id)
                throw new InvalidDataException("语言结果与本次具体模式不符。");
            practice = definition.Practice.Value;
        }
        else if (draft.ModeParentId != null)
            throw new InvalidDataException("独立语言子稿缺少具体模式定义。");
        var reading = practice != PracticeChoice.Writing;
        var writing = practice != PracticeChoice.Reading;
        int[] ordinals = reading && writing ? [0, 1] : reading ? [0] : [1];
        var languageAssignment = DeckRouting.Resolve(draft, ordinals);
        var languageFields = new Dictionary<string, string>
        {
            ["ClipId"] = draft.Id,
            ["Target"] = Html(material.SelectedText),
            ["SentenceBefore"] = Html(sentence[..start]),
            ["SentenceAfter"] = Html(sentence[(start + material.SelectedText.Length)..]),
            ["Pronunciation"] = Html(fields.Pronunciation!),
            ["Meaning"] = Html(fields.Meaning ?? ""),
            ["Source"] = Html(material.SourceTitle),
            ["Origin"] = material.ContextText == null ? "AI 生成例句" : material.ContextOrigin == "user" ? "用户提供原文" : "来源原文",
            ["ReadingEnabled"] = reading ? "1" : "",
            ["WritingEnabled"] = writing ? "1" : ""
        };
        if (published)
        {
            languageFields["LanguageFormat"] = PublishedLanguage.Format;
            languageFields["EnglishMode"] = definition!.Language == "en" ? "1" : "";
            languageFields["ExplanationEn"] = Html(fields.ExplanationEn ?? "");
            languageFields["SentenceTranslationZh"] = Html(fields.SentenceTranslationZh ?? "");
            languageFields["Lemma"] = Html(fields.Lemma ?? "");
            languageFields["DictionaryDetails"] = Html(draft.DictionaryDetails?.DisplayText ?? "划词词典尚未查询，可在草稿中重试。");
        }
        if (material.FrontImage is { } originalImage)
        {
            if (practice != PracticeChoice.Reading) throw new InvalidDataException("原图语言卡仅支持理解与朗读。");
            var back = string.Join("\n", new[] { sentence, fields.Pronunciation, fields.Meaning, fields.ExplanationEn, fields.SentenceTranslationZh }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var imageFields = new Dictionary<string, string> { ["ClipId"] = draft.Id, ["Front"] = ImageHtml([originalImage]),
                ["Back"] = Html(back), ["Source"] = Html(material.SourceTitle) };
            if (published) imageFields["DictionaryDetails"] = Html(draft.DictionaryDetails?.DisplayText ?? "划词词典尚未查询，可在草稿中重试。");
            return new(languageAssignment.Deck, published ? DictionaryImageModelName : BasicModelName, imageFields,
                ["KnowledgeClipper", "kc_" + draft.Id], [0], languageAssignment.CardDecks) { Media = [originalImage] };
        }
        return new(languageAssignment.Deck, published ? PublishedLanguage.DictionaryModelName(draft.Settings.AnkiModel) : draft.Settings.AnkiModel,
            languageFields, ["KnowledgeClipper", "kc_" + draft.Id], ordinals, languageAssignment.CardDecks);
    }
    private static string ImageHtml(IEnumerable<ImageAsset> images) => string.Join("", images.Select(i => "<div>" + i.Html + "</div>"));
}

public sealed class AnkiConnection(HttpClient http, string endpoint) : ICardImporter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ImportLocks = new(StringComparer.OrdinalIgnoreCase);
    public static event Action<ClipDraft, DraftStore>? ImportUpdated;
    public ExportDestination? FixedDestination => ExportDestination.Anki;
    // AnkiConnect's template objects use case-sensitive Name/Front/Back keys.
    private static readonly JsonSerializerOptions ApiJson = new();
    private static readonly string[] FieldNames = ["ClipId", "Target", "SentenceBefore", "SentenceAfter", "Pronunciation", "Meaning", "Source", "Origin", "ReadingEnabled", "WritingEnabled"];
    private static readonly string[] BasicFieldNames = ["ClipId", "Front", "Back", "Source"];
    private static readonly string[] DictionaryImageFieldNames = [..BasicFieldNames, "DictionaryDetails"];
    private static readonly string[] QuestionFieldNames = ["ClipId", "Front", "Back", "Context", "Source"];
    private static readonly string[] PublishedFieldNames = [..FieldNames, "LanguageFormat", "EnglishMode", "ExplanationEn", "SentenceTranslationZh", "Lemma"];
    private static readonly string[] DictionaryFieldNames = [..PublishedFieldNames, "DictionaryDetails"];
    public async Task<JsonElement> CallAsync(string action, object? parameters = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // AnkiConnect's local HTTP server reads Content-Length, not a chunked JSON stream.
        using var body = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { action, version = 6, @params = parameters ?? new { } }, ApiJson));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync(endpoint, body, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
            throw new InvalidDataException($"Anki {action} 响应缺少 error 字段。");
        if (error.ValueKind != JsonValueKind.Null) throw new InvalidOperationException("Anki：" + error.GetString());
        if (!root.TryGetProperty("result", out var result))
            throw new InvalidDataException($"Anki {action} 响应缺少 result 字段。");
        return result.Clone();
    }

    public async Task<string[]> DecksAsync(CancellationToken ct = default) =>
        (await CallAsync("deckNames", ct: ct)).EnumerateArray().Select(x => x.GetString()!).ToArray();
    public async Task<string[]> ModelsAsync(CancellationToken ct = default) =>
        (await CallAsync("modelNames", ct: ct)).EnumerateArray().Select(x => x.GetString()!).ToArray();

    private async Task PrepareAsync(AnkiNoteDraft note, CancellationToken ct)
    {
        foreach (var media in note.Media)
        {
            var existing = await CallAsync("retrieveMediaFile", new { filename = media.FileName }, ct);
            if (existing.ValueKind == JsonValueKind.False)
            {
                var bytes = media.ReadVerified();
                var saved = await CallAsync("storeMediaFile", new { filename = media.FileName, data = Convert.ToBase64String(bytes) }, ct);
                if (saved.GetString() != media.FileName) throw new InvalidDataException("Anki 返回的图片文件名不符。");
            }
            else VerifyMedia(media, existing);
        }
        await VerifyMediaAsync(note, ct);
        var fieldNames = note.IsDictionaryImage ? DictionaryImageFieldNames : note.HasDictionaryDetails && note.IsPublishedLanguage ? DictionaryFieldNames : note.IsPublishedLanguage ? PublishedFieldNames : note.HasQuestionContext ? QuestionFieldNames : note.IsBasic ? BasicFieldNames : FieldNames;
        var templateNames = note.IsCloze ? new[] { "挖空" } : note.IsBasic ? new[] { "正反面" } : new[] { "理解与朗读", "书写练习" };
        var cardTemplates = note.IsCloze
            ? new[] { new { Name = "挖空", Front = "<div>{{cloze:Front}}</div>", Back = "<div>{{cloze:Front}}</div>{{#Back}}<hr><div>{{Back}}</div>{{/Back}}" } }
            : note.IsPublishedLanguage && note.HasDictionaryDetails
            ? new[]
            {
                new { Name = "理解与朗读", Front = "{{#ReadingEnabled}}<div>{{SentenceBefore}}<span class='target'>{{Target}}</span>{{SentenceAfter}}</div>{{/ReadingEnabled}}", Back = "{{FrontSide}}<hr id='answer'>{{#EnglishMode}}<div>{{Pronunciation}}</div><hr><div>{{ExplanationEn}}</div><div>{{SentenceTranslationZh}}</div>{{/EnglishMode}}{{^EnglishMode}}<div>{{Target}}</div><div>{{Pronunciation}}</div><hr><div>{{Meaning}}</div>{{/EnglishMode}}<details class='dictionary'><summary>划词词典</summary><div>{{DictionaryDetails}}</div></details>" },
                new { Name = "书写练习", Front = "{{#WritingEnabled}}{{#EnglishMode}}<div>{{SentenceBefore}}{{Pronunciation}}{{SentenceAfter}}</div><div>{{ExplanationEn}}</div>{{/EnglishMode}}{{^EnglishMode}}<div>{{SentenceBefore}}（{{Pronunciation}}；{{Meaning}}）{{SentenceAfter}}</div>{{/EnglishMode}}{{/WritingEnabled}}", Back = "{{FrontSide}}<hr id='answer'><div>{{Target}}</div>{{#EnglishMode}}<div>{{SentenceTranslationZh}}</div>{{/EnglishMode}}<details class='dictionary'><summary>划词词典</summary><div>{{DictionaryDetails}}</div></details>" }
            }
            : note.IsPublishedLanguage
            ? new[]
            {
                new { Name = "理解与朗读", Front = "{{#ReadingEnabled}}<div>{{SentenceBefore}}<span class='target'>{{Target}}</span>{{SentenceAfter}}</div>{{/ReadingEnabled}}", Back = "{{FrontSide}}<hr id='answer'>{{#EnglishMode}}<div>{{Pronunciation}}</div><hr><div>{{ExplanationEn}}</div><div>{{SentenceTranslationZh}}</div>{{/EnglishMode}}{{^EnglishMode}}<div>{{Target}}</div><div>{{Pronunciation}}</div><hr><div>{{Meaning}}</div>{{/EnglishMode}}" },
                new { Name = "书写练习", Front = "{{#WritingEnabled}}{{#EnglishMode}}<div>{{SentenceBefore}}{{Pronunciation}}{{SentenceAfter}}</div><div>{{ExplanationEn}}</div>{{/EnglishMode}}{{^EnglishMode}}<div>{{SentenceBefore}}（{{Pronunciation}}；{{Meaning}}）{{SentenceAfter}}</div>{{/EnglishMode}}{{/WritingEnabled}}", Back = "{{FrontSide}}<hr id='answer'><div>{{Target}}</div>{{#EnglishMode}}<div>{{SentenceTranslationZh}}</div>{{/EnglishMode}}" }
            }
            : note.IsDictionaryImage
            ? new[] { new { Name = "正反面", Front = "{{#Front}}<div>{{Front}}</div>{{/Front}}", Back = "{{FrontSide}}<hr id='answer'><div>{{Back}}</div><details class='dictionary'><summary>划词词典</summary><div>{{DictionaryDetails}}</div></details>" } }
            : note.HasQuestionContext
            ? new[] { new { Name = "正反面", Front = "{{#Front}}{{#Context}}<div class='context'>{{Context}}</div>{{/Context}}<div>{{Front}}</div>{{/Front}}", Back = "{{FrontSide}}<hr id='answer'><div>{{Back}}</div>" } }
            : note.IsBasic
            ? new[] { new { Name = "正反面", Front = "{{#Front}}<div>{{Front}}</div>{{/Front}}", Back = "{{FrontSide}}<hr id='answer'><div>{{Back}}</div>" } }
            : new[]
            {
                new { Name = "理解与朗读", Front = "{{#ReadingEnabled}}<div>{{SentenceBefore}}<span class='target'>{{Target}}</span>{{SentenceAfter}}</div><div class='origin'>{{Origin}}</div>{{/ReadingEnabled}}", Back = "{{FrontSide}}<hr id='answer'><div>{{Target}} · {{Pronunciation}}</div><div>{{Meaning}}</div>" },
                new { Name = "书写练习", Front = "{{#WritingEnabled}}<div>{{SentenceBefore}}<span class='hint'>［{{Pronunciation}} · {{Meaning}}］</span>{{SentenceAfter}}</div><div class='origin'>{{Origin}}</div>{{/WritingEnabled}}", Back = "{{FrontSide}}<hr id='answer'><div class='target'>{{Target}}</div><div>{{SentenceBefore}}{{Target}}{{SentenceAfter}}</div>" }
            };
        if (!(await ModelsAsync(ct)).Contains(note.Model, StringComparer.Ordinal))
        {
            await CallAsync("createModel", new
            {
                modelName = note.Model, inOrderFields = fieldNames, isCloze = note.IsCloze,
                css = ".card{font-family:'Microsoft YaHei',sans-serif;font-size:24px;text-align:left;line-height:1.8;padding:20px;color:#253449;background:#fafbfc}.card>div{white-space:pre-wrap}.target{color:#235ba5;font-weight:bold}.hint{color:#8b5516}.origin{font-size:14px;color:#718096;margin-top:20px}.context{font-size:14px;color:#718096;margin-bottom:20px;border-bottom:1px solid #d9dfe7;padding-bottom:12px}.dictionary{font-size:16px;color:#4a5568;margin-top:18px;border-top:1px solid #d9dfe7;padding-top:8px}.dictionary summary{cursor:pointer}.dictionary div{white-space:pre-wrap}",
                cardTemplates
            }, ct);
        }
        var fieldsRequest = CallAsync("modelFieldNames", new { modelName = note.Model }, ct);
        var templatesRequest = CallAsync("modelTemplates", new { modelName = note.Model }, ct);
        await Task.WhenAll(fieldsRequest, templatesRequest);
        var fields = fieldsRequest.Result.EnumerateArray().Select(x => x.GetString()).ToArray();
        if (!fieldNames.All(name => fields.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("所选笔记类型缺少本次卡片字段，请选择对应专用类型；已有类型不会被修改。");
        var templates = templatesRequest.Result;
        if (!templates.EnumerateObject().Select(p => p.Name).SequenceEqual(templateNames))
            throw new InvalidDataException("所选类型的卡片模板不兼容，请选择对应专用类型。");
        // Repair only the exact managed publication that omitted the question on answer.
        // Custom templates remain untouched. Re-read the provider before accepting repair.
        const string answerPrefix = "{{FrontSide}}<hr id='answer'>";
        string UpgradeBack(string raw, string expected)
        {
            if (!TemplateContent(raw).StartsWith(answerPrefix, StringComparison.Ordinal)) raw = answerPrefix + raw;
            if (expected.Contains("<hr>", StringComparison.Ordinal) &&
                !TemplateContent(raw).Contains("<hr>", StringComparison.Ordinal))
                raw = Regex.Replace(raw, SpeechControls + @"|<div>\{\{Pronunciation\}\}</div>", match =>
                    match.Value == "<div>{{Pronunciation}}</div>" ? match.Value + "<hr>" : match.Value, RegexOptions.Singleline);
            return raw;
        }
        var managedBasic = note.IsBasic && !note.IsCloze && note.Model is (AnkiNoteDraft.BasicModelName or AnkiNoteDraft.QuestionModelName or AnkiNoteDraft.DictionaryImageModelName or AnkiNoteDraft.ExcerptModelName);
        if ((note.IsPublishedLanguage || managedBasic) && cardTemplates.Any(template =>
            TemplateContent(templates.GetProperty(template.Name).GetProperty("Back").GetString()!) != template.Back) &&
            cardTemplates.All(template =>
                TemplateContent(templates.GetProperty(template.Name).GetProperty("Front").GetString()!) == template.Front &&
                TemplateContent(UpgradeBack(templates.GetProperty(template.Name).GetProperty("Back").GetString()!, template.Back)) == template.Back))
        {
            await CallAsync("updateModelTemplates", new { model = new { name = note.Model,
                templates = cardTemplates.ToDictionary(template => template.Name, template => new {
                    Front = templates.GetProperty(template.Name).GetProperty("Front").GetString(),
                    Back = UpgradeBack(templates.GetProperty(template.Name).GetProperty("Back").GetString()!, template.Back) }) } }, ct);
            templates = await CallAsync("modelTemplates", new { modelName = note.Model }, ct);
        }
        if ((note.IsExcerpt || note.IsDictionaryImage || managedBasic) && cardTemplates.Any(template =>
            TemplateContent(templates.GetProperty(template.Name).GetProperty("Front").GetString()!) != template.Front ||
            TemplateContent(templates.GetProperty(template.Name).GetProperty("Back").GetString()!) != template.Back))
            throw new InvalidDataException("专用笔记类型的模板已改变，无法保证卡面与预览一致；已有模板不会被覆盖。");
        if (note.IsPublishedLanguage && cardTemplates.Any(template =>
            TemplateContent(templates.GetProperty(template.Name).GetProperty("Front").GetString()!) != template.Front ||
            TemplateContent(templates.GetProperty(template.Name).GetProperty("Back").GetString()!) != template.Back))
            throw new InvalidDataException("所选语言笔记类型的模板已改变，无法保证正式卡面格式；已有模板不会被覆盖。");
        if (MermaidCards.Contains(note))
        {
            if (!managedBasic) throw new InvalidDataException("Mermaid 需要剪藏专用正反面模板。");
            var runtimePath = Path.Combine(AppContext.BaseDirectory, "chat-view", "mermaid-runtime.js");
            if (!File.Exists(runtimePath)) throw new InvalidDataException("本地 Mermaid 渲染组件缺失，源码与草稿已保留。");
            var bytes = await File.ReadAllBytesAsync(runtimePath, ct);
            var existing = await CallAsync("retrieveMediaFile", new { filename = MermaidCards.RuntimeFile }, ct);
            if (existing.ValueKind == JsonValueKind.False)
            {
                var saved = await CallAsync("storeMediaFile", new { filename = MermaidCards.RuntimeFile, data = Convert.ToBase64String(bytes) }, ct);
                if (saved.GetString() != MermaidCards.RuntimeFile) throw new InvalidDataException("Anki Mermaid 组件文件名不符。");
            }
            else if (existing.ValueKind != JsonValueKind.String || !Convert.FromBase64String(existing.GetString()!).SequenceEqual(bytes))
                throw new InvalidDataException("Anki Mermaid 组件指纹不符，保留原文件并停止导入。");
            if (cardTemplates.Any(t => !templates.GetProperty(t.Name).GetProperty("Front").GetString()!.Contains(MermaidCards.Bootstrap, StringComparison.Ordinal) ||
                !templates.GetProperty(t.Name).GetProperty("Back").GetString()!.Contains(MermaidCards.Bootstrap, StringComparison.Ordinal)))
            {
                await CallAsync("updateModelTemplates", new { model = new { name = note.Model,
                    templates = cardTemplates.ToDictionary(t => t.Name, t => new {
                        Front = templates.GetProperty(t.Name).GetProperty("Front").GetString()!.Replace(MermaidCards.Bootstrap, "") + MermaidCards.Bootstrap,
                        Back = templates.GetProperty(t.Name).GetProperty("Back").GetString()!.Replace(MermaidCards.Bootstrap, "") + MermaidCards.Bootstrap }) } }, ct);
                var actual = await CallAsync("modelTemplates", new { modelName = note.Model }, ct);
                if (cardTemplates.Any(t => !actual.GetProperty(t.Name).GetProperty("Front").GetString()!.EndsWith(MermaidCards.Bootstrap, StringComparison.Ordinal) ||
                    !actual.GetProperty(t.Name).GetProperty("Back").GetString()!.EndsWith(MermaidCards.Bootstrap, StringComparison.Ordinal)))
                    throw new InvalidDataException("Anki Mermaid 模板回读不一致。");
            }
        }
        var decks = (await DecksAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var deck in note.Ordinals.Select(note.DeckFor).Prepend(note.Deck).Distinct(StringComparer.Ordinal))
            if (decks.Add(deck)) await CallAsync("createDeck", new { deck }, ct);
    }

    private async Task<long[]> FindAsync(ClipDraft draft, CancellationToken ct) =>
        (await CallAsync("findNotes", new { query = "tag:kc_" + draft.Id }, ct)).EnumerateArray().Select(x => x.GetInt64()).ToArray();

    public async Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
    {
        var sharedGate = ImportLocks.GetOrAdd(store.Identity(draft.Id), _ => new(1, 1));
        await sharedGate.WaitAsync(ct);
        try { await ImportCoreAsync(draft, store, ct); }
        finally { sharedGate.Release(); }
    }

    private async Task ImportCoreAsync(ClipDraft draft, DraftStore store, CancellationToken ct)
    {
        await draft.ImportGate.WaitAsync(ct);
        var wasAwaiting = draft.AwaitingAnki;
        try
        {
            var saved = store.Find(draft.Id);
            if (saved?.NoteId != null || saved?.ImportState is ImportState.Unknown or ImportState.Verified)
                CopyImportState(saved, draft);
            wasAwaiting |= draft.AwaitingAnki;
            if (draft.ImportState == ImportState.Verified) return;
            draft.AwaitingAnki = false;
            draft.ImportError = null;
            var note = draft.ConfirmedNote ?? throw new InvalidOperationException("请先确认预览。");
            foreach (var ordinal in note.Ordinals) _ = note.DeckFor(ordinal);
            var unresolved = draft.ImportState is ImportState.Unknown or ImportState.Writing;
            var matches = await FindAsync(draft, ct);
            if (matches.Length > 1) throw new InvalidDataException("发现多个对应结果，需要人工核对；不会再次添加。");
            if (matches.Length == 1)
            {
                draft.NoteId = matches[0];
                SaveImportState(draft, store);
                await ReconcileAsync(draft, store, ct);
            }
            else if (unresolved || draft.NoteId != null)
                throw new InvalidDataException("上次写入结果仍未确认。未查询到对应笔记，请在 Anki 中核对；不会自动再次添加。");
            else
            {
                await PrepareAsync(note, ct);
                draft.ImportState = ImportState.Writing;
                SaveImportState(draft, store); // A crash after this point must reconcile before any mutation.
                var result = await CallAsync("addNote", new { note = new { deckName = note.Deck, modelName = note.Model,
                    fields = note.Fields, tags = note.Tags, options = new { allowDuplicate = false } } }, ct);
                draft.NoteId = result.GetInt64();
                SaveImportState(draft, store);
                await ReconcileAsync(draft, store, ct);
            }
            draft.ImportState = ImportState.Verified;
            draft.ImportError = null;
            draft.Times["ankiVerified"] = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            draft.ImportState = draft.ImportState is ImportState.Writing or ImportState.Unknown || draft.NoteId != null ? ImportState.Unknown : ImportState.Failed;
            draft.AwaitingAnki = ct.IsCancellationRequested ? wasAwaiting || draft.ConfirmedNote != null : ex is HttpRequestException { StatusCode: null } or TaskCanceledException;
            draft.ImportError = draft.AwaitingAnki ? "待导入 Anki：内容已保存，Anki 打开后将自动导入并核验。" : ex.Message;
            if (draft.AwaitingAnki && !ct.IsCancellationRequested) return;
            throw;
        }
        finally
        {
            try { SaveImportState(draft, store); }
            finally { draft.ImportGate.Release(); }
            if (ImportUpdated != null)
                foreach (Action<ClipDraft, DraftStore> listener in ImportUpdated.GetInvocationList())
                    try { listener(draft, store); } catch { /* Observers cannot change import outcome. */ }
        }
    }

    public static void CopyImportState(ClipDraft source, ClipDraft target)
    {
        target.NoteId = source.NoteId; target.CardIds = source.CardIds;
        target.ImportState = source.ImportState; target.ImportError = source.ImportError;
        target.AwaitingAnki = source.AwaitingAnki;
        if (source.Times.TryGetValue("ankiVerified", out var time)) target.Times["ankiVerified"] = time;
    }

    private static void SaveImportState(ClipDraft draft, DraftStore store)
    {
        // A queue entry may outlive edits or new conversation turns. Importing
        // only changes its own metadata; never save the cached whole draft back.
        var current = store.Find(draft.Id);
        if (current == null) { store.Save(draft); return; }
        CopyImportState(draft, current);
        store.Save(current);
    }

    public async Task VerifyAsync(ClipDraft draft, CancellationToken ct = default)
    {
        await ReadCardsAsync(draft, allowInitialDeck: false, ct);
    }

    private async Task ReconcileAsync(ClipDraft draft, DraftStore store, CancellationToken ct)
    {
        var expected = draft.ConfirmedNote!;
        // Read every card's identity and contents before allowing any move from this note's initial deck.
        var cards = await ReadCardsAsync(draft, allowInitialDeck: expected.CardDecks != null, ct);
        var moves = cards.Where(card => card.Deck != expected.DeckFor(card.Ordinal))
            .GroupBy(card => expected.DeckFor(card.Ordinal), StringComparer.Ordinal).ToArray();
        if (moves.Length == 0) return;
        draft.ImportState = ImportState.Writing;
        SaveImportState(draft, store);
        foreach (var group in moves)
            await CallAsync("changeDeck", new { cards = group.Select(card => card.Id).ToArray(), deck = group.Key }, ct);
        await ReadCardsAsync(draft, allowInitialDeck: false, ct);
    }

    private async Task<CardReadback[]> ReadCardsAsync(ClipDraft draft, bool allowInitialDeck, CancellationToken ct)
    {
        var expected = draft.ConfirmedNote!;
        await VerifyMediaAsync(expected, ct);
        var notes = await CallAsync("notesInfo", new { notes = new[] { draft.NoteId!.Value } }, ct);
        if (notes.GetArrayLength() != 1) throw new InvalidDataException("未读回本次笔记。");
        var note = notes[0];
        if (note.GetProperty("noteId").GetInt64() != draft.NoteId || note.GetProperty("modelName").GetString() != expected.Model)
            throw new InvalidDataException("Anki 笔记身份或类型不符。");
        foreach (var field in expected.Fields)
            if (note.GetProperty("fields").GetProperty(field.Key).GetProperty("value").GetString() != field.Value)
                throw new InvalidDataException("Anki 实际字段与确认预览不同：" + field.Key);
        var tags = note.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToArray();
        if (!expected.Tags.All(t => tags.Contains(t))) throw new InvalidDataException("Anki 来源标记不符。");
        var ids = note.GetProperty("cards").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        if (ids.Length != expected.Ordinals.Length) throw new InvalidDataException("实际卡片数量与所选练习不同。");
        var cardsRequest = CallAsync("cardsInfo", new { cards = ids }, ct);
        var suspendedRequest = CallAsync("areSuspended", new { cards = ids }, ct);
        await Task.WhenAll(cardsRequest, suspendedRequest);
        var cards = cardsRequest.Result;
        if (cards.GetArrayLength() != ids.Length) throw new InvalidDataException("未读回所有实际卡片。");
        var seen = new HashSet<int>();
        var seenIds = new HashSet<long>();
        var readback = new List<CardReadback>();
        foreach (var card in cards.EnumerateArray())
        {
            var ordinal = card.GetProperty("ord").GetInt32();
            var id = card.GetProperty("cardId").GetInt64();
            var deck = card.GetProperty("deckName").GetString()!;
            if (!ids.Contains(id) || !seenIds.Add(id) || !expected.Ordinals.Contains(ordinal) || !seen.Add(ordinal) ||
                card.GetProperty("note").GetInt64() != draft.NoteId)
                throw new InvalidDataException("实际卡片的笔记或模板不符。");
            if (deck != expected.DeckFor(ordinal) && !(allowInitialDeck && deck == expected.Deck))
                throw new InvalidDataException("实际卡片已不在已确认的目标或初始牌组，请核对 Anki 中的位置；不会自动改回。");
            if (card.GetProperty("queue").GetInt32() < 0)
                throw new InvalidDataException("实际卡片已暂停或暂时埋藏，当前不可复习。");
            var question = card.GetProperty("question").GetString() ?? "";
            var answer = card.GetProperty("answer").GetString() ?? "";
            var neededQuestion = expected.HasQuestionContext ? new[] { "Front", "Context" } : expected.IsBasic ? new[] { "Front" } : ordinal == 0 ? new[] { "Target" } : new[] { "Pronunciation", "Meaning" };
            var neededAnswer = expected.IsBasic ? expected.HasQuestionContext ? new[] { "Front", "Context", "Back" } : new[] { "Front", "Back" } : ordinal == 0 ? new[] { "Target", "Pronunciation", "Meaning" } : new[] { "Target" };
            if (expected.IsExcerpt)
            {
                string FieldText(string name) => WebUtility.HtmlDecode(expected.Fields[name].Replace("<br>", "\n"));
                var front = FieldText("Front"); var back = FieldText("Back");
                var expectedQuestion = expected.IsCloze ? ExcerptCloze.Question(front) : front;
                var expectedAnswer = expected.IsCloze ? ExcerptCloze.Answer(front) + "\n" + back : front + "\n" + back;
                if (expected.Media.Length > 0)
                {
                    if (!ImageNames(question).SequenceEqual(ImageNames(expected.Fields["Front"])) || !ImageNames(answer).SequenceEqual(ImageNames(expected.Fields["Front"]).Concat(ImageNames(expected.Fields["Back"]))) ||
                        DisplayText(question) != DisplayText(expected.Fields["Front"]) || DisplayText(answer) != DisplayText(expected.Fields["Front"] + "<br>" + expected.Fields["Back"]))
                        throw new InvalidDataException("Anki 图片卡的正反面、图片顺序或文字不符。");
                }
                else if (DisplayText(question) != NormalizeDisplay(expectedQuestion) || DisplayText(answer) != NormalizeDisplay(expectedAnswer))
                    throw new InvalidDataException("Anki 实际摘录卡面与确认预览不一致。");
            }
            else if (expected.IsPublishedLanguage)
            {
                var faces = expected.PublishedFaces(ordinal);
                if (DisplayText(question) != NormalizeDisplay(faces.Front) || DisplayText(answer) != NormalizeDisplay(faces.Front + "\n" + faces.Back))
                    throw new InvalidDataException("Anki 实际卡面与正式语言模式的行序或字段内容不同。");
            }
            else if (!neededQuestion.All(f => Plain(question).Contains(Plain(expected.Fields[f]), StringComparison.Ordinal)) ||
                !neededAnswer.All(f => Plain(answer).Contains(Plain(expected.Fields[f]), StringComparison.Ordinal)))
                throw new InvalidDataException("Anki 实际卡面缺少本次练习内容。");
            if (expected.IsBasic && !expected.IsCloze && !Regex.IsMatch(answer, @"<hr\b[^>]*\bid\s*=\s*['"" ]?answer\b", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Anki 实际答案缺少正反面分割线。");
            readback.Add(new(id, ordinal, deck));
        }
        var suspended = suspendedRequest.Result;
        if (suspended.GetArrayLength() != ids.Length || suspended.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.False))
            throw new InvalidDataException("实际卡片处于暂停状态。");
        draft.CardIds = readback.OrderBy(card => card.Ordinal).Select(card => card.Id).ToArray();
        return readback.ToArray();
    }

    private sealed record CardReadback(long Id, int Ordinal, string Deck);
    private static string[] ImageNames(string html) => Regex.Matches(html, "<img\\b[^>]*\\bsrc=[\"']([^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase)
        .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
    private static void VerifyMedia(ImageAsset expected, JsonElement actual)
    {
        if (actual.ValueKind != JsonValueKind.String) throw new InvalidDataException("Anki 中缺少原图媒体。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(actual.GetString()!); } catch (FormatException) { throw new InvalidDataException("Anki 图片媒体编码无效。"); }
        if (ImageAsset.Hash(bytes) != expected.Sha256) throw new InvalidDataException("Anki 图片内容与采集原图不一致；不会覆盖已有媒体。");
    }
    private async Task VerifyMediaAsync(AnkiNoteDraft note, CancellationToken ct)
    {
        // Bound concurrent payloads; every real byte hash is still checked on every import.
        foreach (var batch in note.Media.Chunk(4))
        {
            var actual = await Task.WhenAll(batch.Select(media => CallAsync("retrieveMediaFile", new { filename = media.FileName }, ct)));
            for (var i = 0; i < batch.Length; i++) VerifyMedia(batch[i], actual[i]);
        }
    }
    private static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
    private static string NormalizeDisplay(string text) => string.Join("\n", text.Replace("\r", "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    private const string SpeechControls = @"\s*<!-- sftts-controls:start -->.*?<!-- sftts-controls:end -->";
    private static string TemplateContent(string html)
    {
        // The installed speech extension owns this bounded block; preserve it byte-for-byte.
        if (Regex.Matches(html, "<!-- sftts-controls:start -->").Count > 1 ||
            Regex.Matches(html, "<!-- sftts-controls:start -->").Count != Regex.Matches(html, "<!-- sftts-controls:end -->").Count) return html;
        return Regex.Replace(html, SpeechControls, "", RegexOptions.Singleline).Replace(MermaidCards.Bootstrap, "");
    }
    private static string DisplayText(string html)
    {
        html = Regex.Replace(html, SpeechControls, "", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<(style|script)\b[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // The managed disclosure label is UI chrome, not an extra learning field.
        // Match only the exact dictionary wrapper/label; its full body remains checked.
        html = Regex.Replace(html, "(<details\\b[^>]*\\bclass=['\"]dictionary['\"][^>]*>)\\s*<summary>划词词典</summary>",
            "$1", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>|</div\s*>", "\n", RegexOptions.IgnoreCase);
        return NormalizeDisplay(Plain(html));
    }
}
