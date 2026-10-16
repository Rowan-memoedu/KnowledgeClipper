using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Security.Cryptography;

namespace KnowledgeClipper.Workflow;

public static class ChatComposition
{
    // These snapshots are model input, not HTML: readable Chinese avoids ambiguous
    // unicode escapes next to numeric conditions while retaining JSON escaping.
    private static readonly JsonSerializerOptions ModelJson = new(WorkflowJson.Options) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    public static string Material(ChatMessage message) => string.Join("\n", message.Parts.Select(p => p.Kind == "image"
        ? "![图片](asset:" + p.Image!.Sha256 + ")" : p.Text ?? ""));

    public static void Select(ChatSession session, CardComposition card, string messageId, string side, bool selected)
    {
        EnsureUnfrozen(card);
        var message = ChatWorkspace.Messages(session).Single(m => m.Id == messageId);
        if (message.Action != null && selected) throw new InvalidOperationException("辅助操作保留在对话中，请从候选采用内容。");
        if (selected && string.IsNullOrWhiteSpace(Material(message))) throw new InvalidDataException("该消息尚无可加入的正文或图片。");
        var sources = side == "front" ? card.FrontSources : side == "back" ? card.BackSources : throw new InvalidDataException("卡面无效。");
        if (sources.Contains(messageId) == selected) return;
        Remember(card);
        if (selected && !sources.Contains(messageId)) sources.Add(messageId);
        if (!selected) sources.Remove(messageId);
        card.Revision++;
        if (!card.ManuallyEdited) Rebuild(session, card, false);
        else if (selected)
        {
            // Adding is an explicit edit: append to this face without replacing either manual body.
            var body = side == "front" ? card.Front : card.Back;
            var material = Material(message);
            if (!ContainsBlock(body, material)) body = string.IsNullOrWhiteSpace(body) ? material : body + "\n\n" + material;
            if (side == "front") card.Front = body; else card.Back = body;
            if (card.FrontSources.Count + card.BackSources.Count == 1) card.SourcesChanged = false;
        }
        else card.SourcesChanged = true;
    }

    public static void Rebuild(ChatSession session, CardComposition card, bool remember = true)
    {
        EnsureUnfrozen(card);
        if (remember) Remember(card);
        string Join(IEnumerable<string> ids) => string.Join("\n\n", ids.Select(id => Material(ChatWorkspace.Messages(session).Single(m => m.Id == id))));
        card.Front = Join(card.FrontSources); card.Back = Join(card.BackSources);
        card.ManuallyEdited = false; card.SourcesChanged = false; card.Revision++;
    }

    public static CardComposition Editable(ChatSession session, CardComposition card)
    {
        if (card.ExportDraftId == null) return card;
        var copy = new CardComposition { TypeId = card.TypeId, Front = card.Front, Back = card.Back,
            Annotation = card.Annotation, AnnotationEnabled = card.AnnotationEnabled,
            FrontAnnotation = card.FrontAnnotation, FrontAnnotationEnabled = card.FrontAnnotationEnabled,
            FrontSources = [.. card.FrontSources], BackSources = [.. card.BackSources], ManuallyEdited = true };
        session.Compositions.Add(copy); return copy;
    }

    public static void Edit(CardComposition card, string front, string back, string typeId, string? annotation = null, bool? annotationEnabled = null,
        string? frontAnnotation = null, bool? frontAnnotationEnabled = null)
    {
        EnsureUnfrozen(card);
        annotation ??= card.Annotation; annotationEnabled ??= card.AnnotationEnabled;
        frontAnnotation ??= card.FrontAnnotation; frontAnnotationEnabled ??= card.FrontAnnotationEnabled;
        if (card.Front == front && card.Back == back && card.TypeId == typeId && card.Annotation == annotation && card.AnnotationEnabled == annotationEnabled &&
            card.FrontAnnotation == frontAnnotation && card.FrontAnnotationEnabled == frontAnnotationEnabled) return;
        Remember(card);
        var bodyChanged = card.Front != front || card.Back != back;
        card.Front = front; card.Back = back; card.TypeId = typeId;
        card.Annotation = annotation; card.AnnotationEnabled = annotationEnabled.Value;
        card.FrontAnnotation = frontAnnotation; card.FrontAnnotationEnabled = frontAnnotationEnabled.Value;
        if (bodyChanged) { card.ManuallyEdited = true; card.SourcesChanged = false; }
        card.Revision++;
    }

    private static bool ContainsBlock(string body, string material)
    {
        body = body.Replace("\r\n", "\n"); material = material.Replace("\r\n", "\n").Trim('\n');
        for (var at = body.IndexOf(material, StringComparison.Ordinal); at >= 0; at = body.IndexOf(material, at + 1, StringComparison.Ordinal))
        {
            var end = at + material.Length;
            if ((at == 0 || at >= 2 && body.AsSpan(at - 2, 2).SequenceEqual("\n\n")) &&
                (end == body.Length || end + 2 <= body.Length && body.AsSpan(end, 2).SequenceEqual("\n\n"))) return true;
        }
        return false;
    }

    public static void Move(ChatSession session, CardComposition card, string side, int index, int offset)
    {
        EnsureUnfrozen(card);
        var sources = side == "front" ? card.FrontSources : side == "back" ? card.BackSources : throw new InvalidDataException("卡面无效。");
        if (offset is not (-1 or 1) || index < 0 || index >= sources.Count || index + offset < 0 || index + offset >= sources.Count)
            throw new InvalidDataException("排序位置无效。");
        Remember(card);
        (sources[index], sources[index + offset]) = (sources[index + offset], sources[index]);
        card.Revision++;
        if (card.ManuallyEdited) card.SourcesChanged = true; else Rebuild(session, card, false);
    }

    public static void Undo(CardComposition card, ChatSession? session = null)
    {
        EnsureUnfrozen(card);
        if (card.UndoHistory.Count == 0) return;
        var previous = card.UndoHistory[^1];
        var redo = Snapshot(card);
        if (previous.AddedCards is { Length: > 0 } additions)
        {
            if (session == null || additions.Any(a => session.Compositions.SingleOrDefault(c => c.Id == a.Id) is not { } added ||
                added.ExportDraftId != null || CardFingerprint(added) != a.Fingerprint))
                throw new InvalidOperationException("本次整合新增的卡片已编辑或导出，不能整批撤销并丢弃更新后的内容。");
            redo = redo with { RestoreCards = additions.Select(a => {
                var added = session.Compositions.Single(c => c.Id == a.Id);
                return new CardRedoAddition(Clone(added), session.Compositions.IndexOf(added));
            }).ToArray() };
            session.Compositions.RemoveAll(c => additions.Any(a => a.Id == c.Id));
        }
        card.UndoHistory.RemoveAt(card.UndoHistory.Count - 1);
        AppendHistory(card.RedoHistory, redo);
        Restore(card, previous);
    }

    public static void Redo(CardComposition card, ChatSession? session = null)
    {
        EnsureUnfrozen(card);
        if (card.RedoHistory.Count == 0) return;
        var next = card.RedoHistory[^1];
        var undo = Snapshot(card);
        if (next.RestoreCards is { Length: > 0 } additions)
        {
            if (session == null || additions.Select(a => a.Card.Id).Distinct().Count() != additions.Length ||
                additions.Any(a => a.Index < 0 || a.Card.ExportDraftId != null || session.Compositions.Any(c => c.Id == a.Card.Id)))
                throw new InvalidOperationException("本次整合的卡片身份已存在或已冻结，不能重做并覆盖现有卡片。");
            foreach (var addition in additions.OrderBy(a => a.Index))
            {
                var restored = Clone(addition.Card);
                restored.Revision++; // Do not make a pre-undo model result valid after restoring a card.
                session.Compositions.Insert(Math.Min(addition.Index, session.Compositions.Count), restored);
            }
            undo = undo with { AddedCards = additions.Select(a => new CardUndoAddition(a.Card.Id, CardFingerprint(a.Card))).ToArray() };
        }
        card.RedoHistory.RemoveAt(card.RedoHistory.Count - 1);
        AppendHistory(card.UndoHistory, undo);
        Restore(card, next);
    }

    private static CardComposition Clone(CardComposition card) =>
        JsonSerializer.Deserialize<CardComposition>(JsonSerializer.Serialize(card, WorkflowJson.Options), WorkflowJson.Options)!;

    private static void Restore(CardComposition card, CardEditSnapshot previous)
    {
        card.Front = previous.Front; card.Back = previous.Back; card.TypeId = previous.TypeId;
        card.FrontSources = [.. previous.FrontSources]; card.BackSources = [.. previous.BackSources];
        card.ManuallyEdited = previous.ManuallyEdited; card.SourcesChanged = previous.SourcesChanged;
        card.Annotation = previous.Annotation; card.AnnotationEnabled = previous.AnnotationEnabled;
        card.FrontAnnotation = previous.FrontAnnotation; card.FrontAnnotationEnabled = previous.FrontAnnotationEnabled;
        // History navigation must not make stale model proposals adoptable again.
        card.Revision++;
    }

    private static string CardFingerprint(CardComposition card)
    {
        var original = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { card.Front, card.Back, card.TypeId, card.Annotation, card.AnnotationEnabled, card.FrontSources, card.BackSources,
                card.ManuallyEdited, card.SourcesChanged }, WorkflowJson.Options)));
        // Preserve fingerprints in earlier persisted batch undo records when the new fields are empty.
        return card.FrontAnnotation.Length == 0 && !card.FrontAnnotationEnabled ? original :
            "front-v1:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { original, card.FrontAnnotation, card.FrontAnnotationEnabled }, WorkflowJson.Options)));
    }

    private static void Remember(CardComposition card)
    {
        AppendHistory(card.UndoHistory, Snapshot(card));
        card.RedoHistory.Clear();
    }

    private static CardEditSnapshot Snapshot(CardComposition card) => new(card.Front, card.Back, card.TypeId,
        [.. card.FrontSources], [.. card.BackSources], card.ManuallyEdited, card.SourcesChanged, card.Annotation, card.AnnotationEnabled,
        FrontAnnotation: card.FrontAnnotation, FrontAnnotationEnabled: card.FrontAnnotationEnabled);

    private static void AppendHistory(List<CardEditSnapshot> history, CardEditSnapshot snapshot)
    {
        history.Add(snapshot);
        if (history.Count > 20) history.RemoveAt(0);
    }

    public static ComposeProposal Begin(ChatSession session, CardComposition card, string action, string requirements, string[] allowedTypes, string? targetSide = null, ChatSession? contextSession = null)
    {
        if (action is not ("integrate" or "formalize" or "formalize-answer" or "formalize-all" or "formalize-annotation" or "transcribe")) throw new InvalidDataException("未知辅助动作。");
        if (action == "formalize-annotation" && (targetSide is not ("front" or "back") ||
            string.IsNullOrWhiteSpace(targetSide == "front" ? card.FrontAnnotation : card.Annotation)))
            throw new InvalidDataException("请先在要书面化的批注中填写内容或图片。");
        if (action == "transcribe" && (targetSide is not ("front" or "back") ||
            ChatMarkdown.ReferencedImages(targetSide == "front" ? card.Front + "\n\n" + card.FrontAnnotation : card.Back + "\n\n" + card.Annotation, session.Assets).Length == 0))
            throw new InvalidDataException("请先在要转换的卡面中添加图片。");
        // Restored cards can retain provenance from another session. Their saved
        // bodies remain usable even when those source messages are unavailable.
        var messages = card.FrontSources.Concat(card.BackSources).Distinct()
            .Join(ChatWorkspace.Messages(session), id => id, m => m.Id, (_, m) => m, StringComparer.Ordinal).ToArray();
        var generateFront = action is "formalize" or "formalize-all" && string.IsNullOrWhiteSpace(card.Front);
        var generateBack = action is "formalize-answer" or "formalize-all" && string.IsNullOrWhiteSpace(card.Back);
        var context = action == "integrate"
            ? ChatWorkspace.Messages(session).Where(m => m.Action == null && m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(Material(m))).ToArray()
            : generateFront || generateBack || action is "transcribe" or "formalize-annotation"
                ? (contextSession ?? session).Messages.Where(m => m.Action == null && m.Status == "completed").ToArray() : [];
        var focused = messages.Length > 0 || !string.IsNullOrWhiteSpace(card.Front) || !string.IsNullOrWhiteSpace(card.Back) ||
            !string.IsNullOrWhiteSpace(card.FrontAnnotation) || !string.IsNullOrWhiteSpace(card.Annotation);
        var requestedCardCount = action == "integrate" ? IntegrationRequirements.CardCount(requirements) : null;
        if (string.IsNullOrWhiteSpace(card.Front) && string.IsNullOrWhiteSpace(card.Back) && messages.Length == 0 &&
            action is not ("transcribe" or "formalize-annotation") && context.Length == 0 &&
            (action != "formalize-all" || string.IsNullOrWhiteSpace(card.FrontAnnotation) && string.IsNullOrWhiteSpace(card.Annotation)))
            throw new InvalidDataException("请先填写卡片内容或提供相关对话。");
        var proposalId = Guid.NewGuid().ToString("N");
        var materials = new[] { (Source: "front", Text: card.Front), (Source: "back", Text: card.Back), (Source: "frontAnnotation", Text: card.FrontAnnotation), (Source: "annotation", Text: card.Annotation) }
            .Concat(messages.Select(m => (Source: "message:" + m.Id, Text: Material(m))))
            .Concat(context.Select(m => (Source: "context:" + m.Id, Text: Material(m))))
            .Select(m => new { m.Source, Images = ChatMarkdown.ReferencedImages(m.Text, session.Assets) }).ToArray();
        var images = materials.SelectMany(m => m.Images).DistinctBy(a => a.Sha256).ToArray();
        var input = JsonSerializer.Serialize(new { proposalId, action, targetSide, allowedTypes, front = card.Front, back = card.Back,
            annotation = card.Annotation, frontAnnotation = card.FrontAnnotation, typeId = card.TypeId, requirements, generateFront, generateBack,
            contextScope = action == "integrate" ? focused ? "selected-and-draft" : "all-conversations" : null,
            requestedCardCount,
            conversations = action == "integrate" ? new[] { (Id: "main", Title: "原对话", Session: session) }
                .Concat(session.Branches.Select(b => (b.Id, b.Title, b.Session))).Select(c => new {
                    c.Id, c.Title, messageIds = c.Session.Messages.Where(m => context.Any(x => x.Id == m.Id)).Select(m => m.Id) }).ToArray() : null,
            context = context.Select(m => new { m.Id, m.Role, material = Material(m), m.Status }),
            messages = messages.Select(m => new { m.Id, m.Role, material = Material(m), m.Status }),
            media = images.Select((a, index) => new { id = "asset:" + a.Sha256, attachmentIndex = index + 1,
                sources = materials.Where(m => m.Images.Any(i => i.Sha256 == a.Sha256)).Select(m => m.Source) }) }, ModelJson);
        var proposal = new ComposeProposal { Id = proposalId, Action = action, TargetSide = targetSide, CompositionId = card.Id, BaseRevision = card.Revision, InputSnapshot = input };
        session.Proposals.Add(proposal); return proposal;
    }

    public static ImageAsset[] ProposalImages(ComposeProposal proposal, ChatSession session)
    {
        using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
        return snapshot.RootElement.GetProperty("media").EnumerateArray().Select(media =>
        {
            var id = media.GetProperty("id").GetString()!;
            var asset = session.Assets.SingleOrDefault(a => "asset:" + a.Sha256 == id)
                ?? throw new InvalidDataException("候选的原图片已缺失：" + id);
            asset.ReadVerified(); return asset;
        }).ToArray();
    }

    public static void Parse(ComposeProposal proposal, string output, ChatSession session, string[] allowedTypes)
    {
        proposal.RawOutput = output;
        try
        {
            var text = output.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
                text = text[(text.IndexOf('\n') + 1)..^3];
            using var json = JsonDocument.Parse(text);
            var cards = json.RootElement.GetProperty("cards").EnumerateArray().Select(c => new CardComposition {
                Front = c.GetProperty("front").GetString()!, Back = c.GetProperty("back").GetString()!,
                TypeId = c.GetProperty("typeId").GetString()!, ManuallyEdited = true }).ToList();
            if (cards.Count is < 1 or > 20 || cards.Any(c => c.Front == null || c.Back == null || !allowedTypes.Contains(c.TypeId)) ||
                proposal.Action is "integrate" or "formalize" or "formalize-all" && cards.Any(c => string.IsNullOrWhiteSpace(c.Front)))
                throw new InvalidDataException("候选数量、正面或类型无效。");
            if (proposal.Action == "integrate")
            {
                if (cards.Any(c => string.IsNullOrWhiteSpace(c.Back)))
                    throw new InvalidDataException("整合结果缺少完整答案；原稿未改，请重试整合。");
                if (cards.Select(c => (c.Front.Trim(), c.Back.Trim(), c.TypeId)).Distinct().Count() != cards.Count)
                    throw new InvalidDataException("整合结果含重复卡片，不能靠重复内容满足数量；原稿未改，请重试整合。");
                using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
                if (snapshot.RootElement.TryGetProperty("requestedCardCount", out var count) && count.ValueKind == JsonValueKind.Number && cards.Count != count.GetInt32())
                    throw new InvalidDataException($"补充要求指定 {count.GetInt32()} 张卡，实际返回 {cards.Count} 张；原稿未改，请重试整合。");
            }
            if (proposal.Action == "formalize")
            {
                if (cards.Count != 1) throw new InvalidDataException("生成书面化问题必须只返回一个正面。");
                using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
                cards[0].Back = snapshot.RootElement.GetProperty("back").GetString()!;
                cards[0].TypeId = snapshot.RootElement.GetProperty("typeId").GetString()!;
            }
            if (proposal.Action == "formalize-answer")
            {
                if (cards.Count != 1 || string.IsNullOrWhiteSpace(cards[0].Back)) throw new InvalidDataException("生成书面化答案必须只返回一个完整背面。");
                using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
                cards[0].Front = snapshot.RootElement.GetProperty("front").GetString()!;
                cards[0].TypeId = snapshot.RootElement.GetProperty("typeId").GetString()!;
            }
            if (proposal.Action is "formalize-all" or "formalize-annotation")
            {
                if (cards.Count != 1 || proposal.Action == "formalize-all" && string.IsNullOrWhiteSpace(cards[0].Back))
                    throw new InvalidDataException("书面化需返回当前一张卡片的完整结果。");
                using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
                var original = snapshot.RootElement; var candidate = json.RootElement.GetProperty("cards")[0];
                string Note(string field)
                {
                    var source = original.GetProperty(field).GetString()!;
                    if (string.IsNullOrWhiteSpace(source)) return source;
                    var result = candidate.GetProperty(field).GetString();
                    if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("书面化不能遗漏原批注：" + field);
                    return result;
                }
                cards[0].TypeId = original.GetProperty("typeId").GetString()!;
                if (proposal.Action == "formalize-all")
                {
                    cards[0].FrontAnnotation = Note("frontAnnotation"); cards[0].Annotation = Note("annotation");
                }
                else
                {
                    if (proposal.TargetSide is not ("front" or "back")) throw new InvalidDataException("批注卡面无效。");
                    cards[0].Front = original.GetProperty("front").GetString()!; cards[0].Back = original.GetProperty("back").GetString()!;
                    if (proposal.TargetSide == "front") cards[0].FrontAnnotation = Note("frontAnnotation");
                    else cards[0].Annotation = Note("annotation");
                }
            }
            if (proposal.Action == "transcribe")
            {
                if (cards.Count != 1 || proposal.TargetSide is not ("front" or "back")) throw new InvalidDataException("图转文字必须只返回当前卡面。");
                using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
                if (proposal.TargetSide == "front") cards[0].Back = snapshot.RootElement.GetProperty("back").GetString()!;
                else cards[0].Front = snapshot.RootElement.GetProperty("front").GetString()!;
                cards[0].TypeId = snapshot.RootElement.GetProperty("typeId").GetString()!;
                string ConvertField(string field, string candidate)
                {
                    var original = snapshot.RootElement.GetProperty(field).GetString()!;
                    if (ChatMarkdown.ReferencedImages(original, session.Assets).Length == 0) return original;
                    if (string.IsNullOrWhiteSpace(candidate) || ChatMarkdown.ReferencedImages(candidate, session.Assets).Length > 0)
                        throw new InvalidDataException("图转文字需返回完整识别正文，不能遗漏内容或保留图片占位：" + field);
                    return candidate;
                }
                if (proposal.TargetSide == "front")
                {
                    cards[0].Front = ConvertField("front", cards[0].Front);
                    if (snapshot.RootElement.TryGetProperty("frontAnnotation", out var frontAnnotation))
                    {
                        var source = frontAnnotation.GetString()!;
                        cards[0].FrontAnnotation = ChatMarkdown.ReferencedImages(source, session.Assets).Length == 0 ? source :
                            ConvertField("frontAnnotation", json.RootElement.GetProperty("cards")[0].GetProperty("frontAnnotation").GetString()!);
                    }
                }
                else
                {
                    cards[0].Back = ConvertField("back", cards[0].Back);
                    if (snapshot.RootElement.TryGetProperty("annotation", out var annotation))
                    {
                        var source = annotation.GetString()!;
                        cards[0].Annotation = ChatMarkdown.ReferencedImages(source, session.Assets).Length == 0 ? source :
                            ConvertField("annotation", json.RootElement.GetProperty("cards")[0].GetProperty("annotation").GetString()!);
                        cards[0].AnnotationEnabled = session.Compositions.Single(c => c.Id == proposal.CompositionId).AnnotationEnabled;
                    }
                }
            }
            foreach (var card in cards) ChatMarkdown.ValidateAssets(card.Front + "\n" + card.Back + "\n" + card.Annotation + "\n" + card.FrontAnnotation, session.Assets);
            proposal.Cards = cards; proposal.Status = "preview";
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { proposal.Error = "处理结果未通过完整性核验，原内容已保留。" + ex.Message; proposal.Status = "unparsed"; }
    }

    public static void Adopt(ChatSession session, ComposeProposal proposal)
    {
        if (proposal.Status != "preview") throw new InvalidOperationException("候选不能重复采用或尚未解析。");
        var original = session.Compositions.Single(c => c.Id == proposal.CompositionId);
        if (original.Revision != proposal.BaseRevision || original.ExportDraftId != null)
            throw new InvalidOperationException("编辑稿已更新或冻结；迟到候选不能覆盖。可手工复制到新卡。");
        var first = proposal.Cards[0];
        string? annotation = null;
        string? frontAnnotation = null;
        if (proposal.Action == "formalize-all") { annotation = first.Annotation; frontAnnotation = first.FrontAnnotation; }
        if (proposal.Action == "formalize-annotation")
        {
            if (proposal.TargetSide == "front") frontAnnotation = first.FrontAnnotation; else annotation = first.Annotation;
        }
        if (proposal.Action == "transcribe" && proposal.TargetSide == "back")
        {
            using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
            if (snapshot.RootElement.TryGetProperty("annotation", out _)) annotation = first.Annotation;
        }
        if (proposal.Action == "transcribe" && proposal.TargetSide == "front")
        {
            using var snapshot = JsonDocument.Parse(proposal.InputSnapshot);
            if (snapshot.RootElement.TryGetProperty("frontAnnotation", out _)) frontAnnotation = first.FrontAnnotation;
        }
        var additions = proposal.Cards.Skip(1).Select(Clone).ToArray();
        foreach (var extra in additions)
        {
            extra.FrontSources = [.. original.FrontSources]; extra.BackSources = [.. original.BackSources];
            extra.FrontAnnotationEnabled = original.FrontAnnotationEnabled; extra.AnnotationEnabled = original.AnnotationEnabled;
        }
        var beforeRevision = original.Revision;
        Edit(original, first.Front, proposal.Action == "formalize" ? original.Back : first.Back, first.TypeId, annotation, frontAnnotation: frontAnnotation);
        if (proposal.Cards.Count > 1)
        {
            if (original.Revision == beforeRevision) { Remember(original); original.Revision++; }
            original.UndoHistory[^1] = original.UndoHistory[^1] with { AddedCards = additions.Select(c => new CardUndoAddition(c.Id, CardFingerprint(c))).ToArray() };
        }
        session.Compositions.InsertRange(session.Compositions.IndexOf(original) + 1, additions);
        proposal.Status = "adopted";
    }

    public static async Task ExportAsync(ClipDraft parent, CardComposition card, DraftStore store, ChatConfigurationLoader configuration,
        ICardImporter importer, ExportDestination destination, CancellationToken ct = default)
    {
        if (!await parent.ConversationImportGate.WaitAsync(0, ct)) throw new InvalidOperationException("已有卡片正在导入。");
        try
        {
            var session = parent.Chat ?? throw new InvalidDataException("缺少对话。");
            ClipDraft child;
            if (card.ExportDraftId != null)
                child = card.ExportDraftId == parent.Id ? parent : store.Find(card.ExportDraftId) ?? throw new InvalidDataException("已冻结卡片草稿缺失，不会创建替代卡。");
            else if (store.Find(card.Id) is { } existing)
            {
                if (existing.ParentDraftId != parent.Id) throw new InvalidDataException("冻结草稿身份冲突。");
                child = existing; card.ExportDraftId = child.Id; store.Save(parent);
            }
            else
            {
                var material = new CaptureMaterial(card.Front, null, "generated_allowed", null, "", 0, DateTimeOffset.UtcNow);
                child = new() { Id = card.Id, Original = material, Material = material, Settings = parent.Settings,
                    Configuration = parent.Configuration, ParentDraftId = parent.Id, ModeId = "question",
                    Input = new(Intent: "question"), Route = new("question", "question", true, false),
                    QuestionTarget = configuration.Target(card.TypeId), Content = new("question", Basic: new(ChatMarkdown.BackWithAnnotation(card.Front, card.FrontAnnotation), ChatMarkdown.BackWithAnnotation(card.Back, card.Annotation))) };
                var rendered = ChatMarkdown.Render(card.Front, card.Back, session.Assets, card.Annotation, card.FrontAnnotation);
                var assignment = DeckRouting.Resolve(child, [0]);
                var note = new AnkiNoteDraft(assignment.Deck, AnkiNoteDraft.BasicModelName,
                    new() { ["ClipId"] = child.Id, ["Front"] = rendered.FrontHtml, ["Back"] = rendered.BackHtml, ["Source"] = "" },
                    ["KnowledgeClipper", "kc_" + child.Id], [0], assignment.CardDecks) { Media = rendered.Media };
                var confirmation = ExportRouting.Prepare(child, note, destination, rendered.Card);
                confirmation.Apply(child); store.Save(child);
                card.ExportDraftId = child.Id; store.Save(parent);
            }
            if (child.ConfirmedDestination != destination) throw new InvalidOperationException("该卡片已经确认到原目标；更换目标请编辑为新草稿。");
            if (child.ImportState != ImportState.Verified) await importer.ImportAsync(child, store, ct);
            store.Save(parent);
        }
        finally { parent.ConversationImportGate.Release(); }
    }

    private static void EnsureUnfrozen(CardComposition card)
    { if (card.ExportDraftId != null) throw new InvalidOperationException("已确认卡片已冻结，请编辑为新草稿。"); }
}
