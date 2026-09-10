using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public sealed record AnkiNoteDraft(string Deck, string Model, Dictionary<string, string> Fields, string[] Tags, int[] Ordinals)
{
    public static string Html(string value) => WebUtility.HtmlEncode(value).Replace("\r\n", "\n").Replace("\n", "<br>");
    public static AnkiNoteDraft From(ClipDraft draft)
    {
        var material = draft.Material;
        material.Validate();
        var fields = draft.Content?.Fields ?? throw new InvalidOperationException("内容尚未完成。");
        ContentModule.ValidateFields(material, fields);
        var sentence = material.ContextText ?? fields.GeneratedSentence!;
        var start = material.TargetStart ?? fields.GeneratedTargetStart!.Value;
        var reading = draft.Practice != PracticeChoice.Writing;
        var writing = draft.Practice != PracticeChoice.Reading;
        return new(draft.Settings.AnkiDeck, draft.Settings.AnkiModel, new()
        {
            ["ClipId"] = draft.Id,
            ["Target"] = Html(material.SelectedText),
            ["SentenceBefore"] = Html(sentence[..start]),
            ["SentenceAfter"] = Html(sentence[(start + material.SelectedText.Length)..]),
            ["Pronunciation"] = Html(fields.Pronunciation),
            ["Meaning"] = Html(fields.Meaning),
            ["Source"] = Html(material.SourceTitle),
            ["Origin"] = material.ContextText == null ? "AI 生成例句" : material.ContextOrigin == "user" ? "用户提供原文" : "来源原文",
            ["ReadingEnabled"] = reading ? "1" : "",
            ["WritingEnabled"] = writing ? "1" : ""
        }, ["KnowledgeClipper", "kc_" + draft.Id], reading && writing ? [0, 1] : reading ? [0] : [1]);
    }
}

public sealed class AnkiConnection(HttpClient http, string endpoint)
{
    // AnkiConnect's template objects use case-sensitive Name/Front/Back keys.
    private static readonly JsonSerializerOptions ApiJson = new();
    private static readonly string[] FieldNames = ["ClipId", "Target", "SentenceBefore", "SentenceAfter", "Pronunciation", "Meaning", "Source", "Origin", "ReadingEnabled", "WritingEnabled"];
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
        if (!(await ModelsAsync(ct)).Contains(note.Model, StringComparer.Ordinal))
        {
            await CallAsync("createModel", new
            {
                modelName = note.Model, inOrderFields = FieldNames, isCloze = false,
                css = ".card{font-family:'Microsoft YaHei',sans-serif;font-size:24px;text-align:left;line-height:1.8;padding:20px;color:#253449;background:#fafbfc}.target{color:#235ba5;font-weight:bold}.hint{color:#8b5516}.origin{font-size:14px;color:#718096;margin-top:20px}",
                cardTemplates = new[]
                {
                    new { Name = "理解与朗读", Front = "{{#ReadingEnabled}}<div>{{SentenceBefore}}<span class='target'>{{Target}}</span>{{SentenceAfter}}</div><div class='origin'>{{Origin}}</div>{{/ReadingEnabled}}", Back = "{{FrontSide}}<hr id='answer'><div>{{Target}} · {{Pronunciation}}</div><div>{{Meaning}}</div>" },
                    new { Name = "书写练习", Front = "{{#WritingEnabled}}<div>{{SentenceBefore}}<span class='hint'>［{{Pronunciation}} · {{Meaning}}］</span>{{SentenceAfter}}</div><div class='origin'>{{Origin}}</div>{{/WritingEnabled}}", Back = "{{FrontSide}}<hr id='answer'><div class='target'>{{Target}}</div><div>{{SentenceBefore}}{{Target}}{{SentenceAfter}}</div>" }
                }
            }, ct);
        }
        var fields = (await CallAsync("modelFieldNames", new { modelName = note.Model }, ct)).EnumerateArray().Select(x => x.GetString()).ToArray();
        if (!FieldNames.All(name => fields.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("所选笔记类型缺少语言共享字段，请选择专用语言类型；已有类型不会被修改。");
        var templates = await CallAsync("modelTemplates", new { modelName = note.Model }, ct);
        if (!templates.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "理解与朗读", "书写练习" }))
            throw new InvalidDataException("所选类型的卡片模板不兼容，请选择专用语言类型。");
        if (!(await DecksAsync(ct)).Contains(note.Deck, StringComparer.Ordinal)) await CallAsync("createDeck", new { deck = note.Deck }, ct);
    }

    private async Task<long[]> FindAsync(ClipDraft draft, CancellationToken ct) =>
        (await CallAsync("findNotes", new { query = "tag:kc_" + draft.Id }, ct)).EnumerateArray().Select(x => x.GetInt64()).ToArray();

    public async Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
    {
        await draft.ImportGate.WaitAsync(ct);
        try
        {
            if (draft.ImportState == ImportState.Verified) return;
            draft.ImportError = null;
            var note = draft.ConfirmedNote ?? throw new InvalidOperationException("请先确认预览。");
            var unresolved = draft.ImportState is ImportState.Unknown or ImportState.Writing;
            var matches = await FindAsync(draft, ct);
            if (matches.Length > 1) throw new InvalidDataException("发现多个对应结果，需要人工核对；不会再次添加。");
            if (matches.Length == 1)
            {
                draft.NoteId = matches[0];
                await VerifyAsync(draft, ct);
            }
            else if (unresolved || draft.NoteId != null)
                throw new InvalidDataException("上次写入结果仍未确认。未查询到对应笔记，请在 Anki 中核对；不会自动再次添加。");
            else
            {
                await PrepareAsync(note, ct);
                draft.ImportState = ImportState.Writing;
                store.Save(draft); // A crash after this point must reconcile before any mutation.
                var result = await CallAsync("addNote", new { note = new { deckName = note.Deck, modelName = note.Model,
                    fields = note.Fields, tags = note.Tags, options = new { allowDuplicate = false } } }, ct);
                draft.NoteId = result.GetInt64();
                store.Save(draft);
                await VerifyAsync(draft, ct);
            }
            draft.ImportState = ImportState.Verified;
            draft.ImportError = null;
            draft.Times["ankiVerified"] = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            draft.ImportState = draft.ImportState is ImportState.Writing or ImportState.Unknown || draft.NoteId != null ? ImportState.Unknown : ImportState.Failed;
            draft.ImportError = ex is HttpRequestException or TaskCanceledException ? "暂时无法连接 Anki；草稿已保留。" : ex.Message;
            throw;
        }
        finally { store.Save(draft); draft.ImportGate.Release(); }
    }

    public async Task VerifyAsync(ClipDraft draft, CancellationToken ct = default)
    {
        var expected = draft.ConfirmedNote!;
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
        var cards = await CallAsync("cardsInfo", new { cards = ids }, ct);
        if (cards.GetArrayLength() != ids.Length) throw new InvalidDataException("未读回所有实际卡片。");
        var seen = new HashSet<int>();
        foreach (var card in cards.EnumerateArray())
        {
            var ordinal = card.GetProperty("ord").GetInt32();
            if (!ids.Contains(card.GetProperty("cardId").GetInt64()) || !expected.Ordinals.Contains(ordinal) || !seen.Add(ordinal) ||
                card.GetProperty("deckName").GetString() != expected.Deck || card.GetProperty("note").GetInt64() != draft.NoteId)
                throw new InvalidDataException("实际卡片的牌组、笔记或模板不符。");
            if (card.GetProperty("queue").GetInt32() < 0)
                throw new InvalidDataException("实际卡片已暂停或暂时埋藏，当前不可复习。");
            var question = card.GetProperty("question").GetString() ?? "";
            var answer = card.GetProperty("answer").GetString() ?? "";
            var neededQuestion = ordinal == 0 ? new[] { "Target" } : new[] { "Pronunciation", "Meaning" };
            var neededAnswer = ordinal == 0 ? new[] { "Target", "Pronunciation", "Meaning" } : new[] { "Target" };
            if (!neededQuestion.All(f => Plain(question).Contains(Plain(expected.Fields[f]), StringComparison.Ordinal)) ||
                !neededAnswer.All(f => Plain(answer).Contains(Plain(expected.Fields[f]), StringComparison.Ordinal)))
                throw new InvalidDataException("Anki 实际卡面缺少本次练习内容。");
        }
        var suspended = await CallAsync("areSuspended", new { cards = ids }, ct);
        if (suspended.GetArrayLength() != ids.Length || suspended.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.False))
            throw new InvalidDataException("实际卡片处于暂停状态。");
        draft.CardIds = ids;
    }

    private static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
}
