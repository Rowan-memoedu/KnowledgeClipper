using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace KnowledgeClipper.Workflow;

public static class QuestionCards
{
    public static IReadOnlyList<ConversationTurn> EffectiveConversation(ClipDraft parent, DraftStore? store = null) =>
        Snapshot(parent, store).Select(row => row.Turn).ToArray();

    public static IReadOnlyList<QuestionCardPreview> Preview(ClipDraft parent, DraftStore store)
    {
        var rows = Snapshot(parent, store);
        return rows.Select((row, index) => new QuestionCardPreview(index,
            row.Basic?.Front ?? row.Turn.Question,
            row.Basic?.Back ?? row.Turn.Answer.DisplayText,
            row.Frozen ? row.Basic?.Context ?? "" : Context(rows, index, parent.Settings.QuestionCardIncludeContext),
            row.IntegrityError != null ? ImportState.Unknown : row.Child?.ImportState ?? ImportState.Unconfirmed,
            row.Frozen,
            row.IntegrityError ?? (!row.Turn.Answer.Answered ? "该轮尚未解答，不能导入卡片。" :
                string.IsNullOrWhiteSpace(row.Turn.Answer.AnswerText) ? "该轮答案为空，请先补全。" : row.Child?.ImportError))
            { DestinationLabel = row.Child != null ? ExportRouting.Describe(row.Child) : ExportRouting.Describe(parent, true) }).ToArray();
    }

    public static async Task ImportAsync(ClipDraft parent, int[] indexes, DraftStore store, ICardImporter anki,
        Action changed, CancellationToken ct = default)
    {
        await parent.ConversationImportGate.WaitAsync(ct);
        int? importing = null;
        ClipDraft? activeChild = null;
        try
        {
            parent.ConversationImportError = null;
            ct.ThrowIfCancellationRequested();
            if (indexes == null || indexes.Length == 0) throw new InvalidDataException("请选择至少一轮问答。");
            var selected = indexes.Distinct().Order().ToArray();
            var rows = Snapshot(parent, store);
            if (selected.Any(index => index < 0 || index >= rows.Length)) throw new InvalidDataException("所选问答轮次不存在。");

            // Validate the whole selection and prepare immutable note values before saving or sending any item.
            var plans = new List<ImportPlan>();
            foreach (var index in selected)
            {
                var row = rows[index];
                if (row.IntegrityError != null) throw new InvalidDataException(row.IntegrityError);
                if (!row.Turn.Answer.Answered || string.IsNullOrWhiteSpace(row.Turn.Question) || string.IsNullOrWhiteSpace(row.Turn.Answer.AnswerText))
                    throw new InvalidDataException($"第 {index + 1} 轮尚无完整答案，不能导入卡片。");
                var child = row.Child ?? CreateChild(parent, row.Turn, index, SharedId(row.Owners[^1].Id, index));
                if (row.Frozen)
                {
                    if (child.ConfirmedNote == null) throw new InvalidDataException($"第 {index + 1} 轮缺少已确认卡片记录，不能重新添加。");
                    plans.Add(new(index, child, null, ExportRouting.Prepare(child, child.ConfirmedNote, anki.FixedDestination), row.Owners));
                }
                else
                {
                    var basic = new BasicFields(child.QuestionText, row.Turn.Answer.AnswerText!, Context(rows, index, parent.Settings.QuestionCardIncludeContext));
                    // Build with a temporary view so a later invalid selection cannot partially confirm an old child.
                    var candidate = new ClipDraft { Id = child.Id, Original = child.Original, Material = child.Material,
                        Settings = child.Settings, Configuration = child.Configuration, Practice = child.Practice, Input = child.Input,
                        ModeId = child.ModeId, Route = child.Route, ParentDraftId = child.ParentDraftId,
                        Content = new(child.Content?.ModeId ?? child.ModeId ?? parent.ModeId ?? "question", Basic: basic,
                            Answer: child.Content?.Answer ?? parent.Conversation[index].Answer) };
                    plans.Add(new(index, child, candidate.Content, ExportRouting.Prepare(child,
                        ExportRouting.BuildNote(candidate, anki.FixedDestination), anki.FixedDestination), row.Owners));
                }
            }
            if (plans.Select(plan => plan.Child.Id).Distinct(StringComparer.Ordinal).Count() != plans.Count)
                throw new InvalidDataException("多个轮次关联了同一张卡片，请先核对本机记录。");

            // Confirmation freezes every selected card and its context before the first Anki call.
            foreach (var plan in plans)
            {
                if (plan.Content != null)
                {
                    plan.Child.Content = plan.Content;
                    plan.Child.EditedBasic = null;
                    plan.Note.Apply(plan.Child);
                    plan.Child.ImportState = ImportState.Ready;
                    plan.Child.ImportError = null;
                    plan.Child.Times["confirmed"] = DateTimeOffset.UtcNow;
                }
                store.Save(plan.Child);
                foreach (var owner in plan.Owners) owner.FollowUpCards[plan.Index] = plan.Child.Id;
            }
            foreach (var owner in plans.SelectMany(plan => plan.Owners).DistinctBy(owner => owner.Id)) store.Save(owner);
            Notify(changed);

            foreach (var plan in plans)
            {
                if (plan.Child.ImportState == ImportState.Verified) continue;
                importing = plan.Index;
                activeChild = plan.Child;
                ct.ThrowIfCancellationRequested();
                try { await anki.ImportAsync(plan.Child, store, ct); }
                finally { Notify(changed); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            parent.ConversationImportError = "导入已停止；已完成结果和已确认卡片已保留，可继续核对剩余轮次。";
            throw;
        }
        catch (Exception ex)
        {
            parent.ConversationImportError = (importing is int index ? $"第 {index + 1} 轮未完成：" : "") + (activeChild?.ImportError ?? ex.Message);
            throw;
        }
        finally
        {
            try { store.Save(parent); }
            finally { parent.ConversationImportGate.Release(); Notify(changed); }
        }
    }

    private static Row[] Snapshot(ClipDraft parent, DraftStore? store)
    {
        var conversation = parent.Conversation.ToArray();
        Owner[] owners;
        string? lineageError = null;
        try { owners = Lineage(parent, store); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        { owners = [new(parent, conversation.Length, parent.FollowUpCards)]; lineageError = "旧分支父链无法核对；不会新建替代卡片。" + ex.Message; }
        var known = owners.ToDictionary(owner => owner.Draft.Id, owner => owner.Draft, StringComparer.Ordinal);
        var ownUnconfirmedIndex = OwnUnconfirmedIndex(parent, conversation);
        var rows = new Row[conversation.Length];
        for (var index = 0; index < conversation.Length; index++)
        {
            var turn = conversation[index];
            ClipDraft? child = null;
            var sharedOwners = owners.Where(owner => owner.SharedPrefix > index).ToArray();
            string? error = lineageError;
            try
            {
                if (error == null)
                {
                    var candidates = new Dictionary<string, ClipDraft>(StringComparer.Ordinal);
                    foreach (var owner in sharedOwners)
                    {
                        if (owner.Bindings.TryGetValue(index, out var id))
                        {
                            if (known.TryGetValue(id, out var loaded)) candidates[id] = loaded;
                            else if (store != null)
                            {
                                loaded = store.Find(id) ?? throw new InvalidDataException("已关联卡片的本机草稿缺失；不会重新添加。");
                                if (loaded.Id != id) throw new InvalidDataException("已关联卡片的本机身份不符。");
                                known[id] = candidates[id] = loaded;
                            }
                        }
                        if (OwnFrozenIndex(owner.Draft, owner.Draft.Conversation.ToArray()) == index)
                            candidates[owner.Draft.Id] = owner.Draft;
                    }
                    if (candidates.Count > 1) throw new InvalidDataException("共同历史轮次存在不同的卡片绑定，请先核对；不会新增。");
                    child = candidates.Values.SingleOrDefault();
                    // Stable identity also protects a stale open ancestor whose in-memory index predates a branch import.
                    if (child == null && store != null)
                    {
                        var id = SharedId(sharedOwners[^1].Draft.Id, index);
                        child = store.Find(id);
                        if (child != null && child.Id != id) throw new InvalidDataException("共同历史卡片的本机身份不符。");
                    }
                    if (child != null && Normalize(child.QuestionText) != Normalize(turn.Question))
                        throw new InvalidDataException("已关联卡片的原文与本轮问题不符；不会重新添加。");
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                error = $"第 {index + 1} 轮：" + ex.Message;
            }

            var frozen = error != null || child != null && Frozen(child);
            BasicFields? basic = null;
            if (child != null)
            {
                if (frozen && child.ConfirmedNote is { IsBasic: true } confirmed)
                {
                    try { basic = FrozenBasic(child, confirmed); }
                    catch (InvalidDataException ex) { error ??= $"第 {index + 1} 轮：" + ex.Message; }
                }
                else if (frozen) error ??= $"第 {index + 1} 轮缺少可读的已确认正反面记录。";
                else basic = child.PreviewContent?.Basic;
            }
            if (turn.Answer.Answered)
            {
                var answer = basic?.Back ?? turn.Answer.AnswerText;
                if (!frozen && parent.EditedAnswers.TryGetValue(index, out var edited)) answer = edited;
                else if (!frozen && child == null && ownUnconfirmedIndex == index && parent.PreviewContent?.Basic is { } own)
                    answer = own.Back;
                turn = turn with { Answer = turn.Answer with { AnswerText = answer } };
                if (!frozen) basic = null; // The current row answer and context are computed from this snapshot.
            }
            rows[index] = new(turn, child, frozen, basic, error, sharedOwners.Select(owner => owner.Draft).ToArray());
        }
        return rows;
    }

    private static Owner[] Lineage(ClipDraft parent, DraftStore? store)
    {
        var result = new List<Owner>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = parent;
        var shared = parent.Conversation.Count;
        while (true)
        {
            if (!Guid.TryParseExact(current.Id, "N", out _) || !seen.Add(current.Id)) throw new InvalidDataException("父链身份损坏或存在循环。");
            var bindings = new Dictionary<int, string>(current.FollowUpCards);
            var persisted = store?.Find(current.Id);
            if (persisted != null)
            {
                if (persisted.Id != current.Id || persisted.ParentDraftId != current.ParentDraftId)
                    throw new InvalidDataException("父链本机身份不符。");
                foreach (var pair in persisted.FollowUpCards)
                {
                    if (bindings.TryGetValue(pair.Key, out var previous) && previous != pair.Value)
                        throw new InvalidDataException("本机与已打开草稿的卡片绑定冲突。");
                    bindings[pair.Key] = pair.Value;
                }
            }
            // Hydrate only existing bindings so saving a stale window cannot discard an unselected round.
            foreach (var pair in bindings) current.FollowUpCards.TryAdd(pair.Key, pair.Value);
            result.Add(new(current, shared, bindings));
            if (store == null || current.ParentDraftId == null) break;
            var ancestor = store.Find(current.ParentDraftId) ?? throw new InvalidDataException("找不到对应父会话。");
            if (ancestor.Id != current.ParentDraftId) throw new InvalidDataException("父会话的本机身份不符。");
            shared = Math.Min(shared, CommonPrefix(parent.Conversation, ancestor.Conversation));
            current = ancestor;
        }
        return result.ToArray();
    }

    private static int CommonPrefix(IReadOnlyList<ConversationTurn> first, IReadOnlyList<ConversationTurn> second)
    {
        var length = Math.Min(first.Count, second.Count);
        var index = 0;
        while (index < length && first[index].Question == second[index].Question &&
            first[index].Answer.AnswerState == second[index].Answer.AnswerState &&
            first[index].Answer.AnswerText == second[index].Answer.AnswerText &&
            first[index].Answer.UnresolvedReasons.SequenceEqual(second[index].Answer.UnresolvedReasons, StringComparer.Ordinal)) index++;
        return index;
    }

    private static string SharedId(string earliestOwner, int index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("KnowledgeClipper.QuestionCards:" + earliestOwner + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new Guid(bytes.AsSpan(0, 16)).ToString("N");
    }

    private static bool Frozen(ClipDraft draft) => draft.ConfirmedNote != null || draft.NoteId != null ||
        draft.ImportState is ImportState.Writing or ImportState.Unknown or ImportState.Verified;

    private static int OwnFrozenIndex(ClipDraft parent, ConversationTurn[] conversation)
    {
        if (!Frozen(parent) || parent.ConfirmedNote is not { IsBasic: true } note) return -1;
        BasicFields basic;
        try { basic = FrozenBasic(parent, note); }
        catch (InvalidDataException) { return -1; }
        if (parent.ParentDraftId == null && conversation.Length > 0 && conversation[0].Question == parent.QuestionText) return 0;
        var answer = basic.Back;
        var matches = conversation.Select((turn, index) => (turn, index)).Where(x => x.turn.Question == parent.QuestionText &&
            (Normalize(x.turn.Answer.AnswerText ?? "") == answer || x.turn.Answer.AnswerText == parent.Content?.Answer?.AnswerText)).ToArray();
        return matches.Length == 1 ? matches[0].index : -1;
    }

    private static BasicFields FrozenBasic(ClipDraft draft, AnkiNoteDraft note)
    {
        var back = note.Fields["Back"];
        if (draft.Original.FrontImage is { } image)
        {
            if (note.Fields["Front"] != "<div>" + image.Html + "</div>" ||
                note.Media.Length != 1 || note.Media[0] != image)
                throw new InvalidDataException("已确认卡片的原图与本轮材料不符。");
            if (!string.IsNullOrEmpty(note.ImageQuestionContext))
            {
                var contextPrefix = AnkiNoteDraft.Html(note.ImageQuestionContext + "\n\n");
                if (!back.StartsWith(contextPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException("已确认图片卡缺少对应的问答上下文。");
                back = back[contextPrefix.Length..];
            }
            if (draft.ParentDraftId != null)
            {
                var prefix = AnkiNoteDraft.Html(draft.QuestionText + "\n\n");
                if (!back.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("已确认图片卡缺少对应的追问文字。");
                back = back[prefix.Length..];
            }
        }
        else if (note.Fields["Front"] != AnkiNoteDraft.Html(draft.QuestionText))
            throw new InvalidDataException("已确认卡片的正面与本轮问题不符。");
        return new(draft.QuestionText, Decode(back),
            draft.Original.FrontImage != null ? note.ImageQuestionContext :
                note.Fields.TryGetValue("Context", out var context) ? Decode(context) : null);
    }

    private static int OwnUnconfirmedIndex(ClipDraft parent, ConversationTurn[] conversation)
    {
        if (Frozen(parent) || parent.PreviewContent?.Basic == null) return -1;
        var matches = conversation.Select((turn, index) => (turn, index)).Where(x => x.turn.Question == parent.QuestionText &&
            x.turn.Answer.AnswerText == parent.Content?.Answer?.AnswerText).ToArray();
        return matches.Length == 1 ? matches[0].index : -1;
    }

    private static ClipDraft CreateChild(ClipDraft parent, ConversationTurn turn, int index, string id)
    {
        var mode = parent.Configuration.Modes.SingleOrDefault(m => m.Definition.OutputKind == "question");
        var modeId = mode?.Definition.Id ?? parent.Content?.ModeId ?? parent.ModeId ?? "question";
        var material = new CaptureMaterial(turn.Question, null, "generated_allowed", null, parent.Original.SourceTitle, 0, DateTimeOffset.UtcNow)
            { FrontImage = parent.Original.FrontImage, VisualQuestion = parent.Original.VisualQuestion };
        return new() { Id = id, Original = material, Material = material, Settings = parent.Settings, Configuration = parent.Configuration,
            Input = parent.Input with { Intent = "question", ReadRequested = false, ExplicitQuestion = null }, ParentDraftId = parent.Id,
            ModeId = modeId, Route = new(modeId, "question", true, false),
            ConversationMaterial = parent.ConversationMaterial ?? parent.Material,
            ConversationContent = parent.ConversationContent ?? parent.PreviewContent,
            Conversation = parent.Conversation.Take(index + 1).ToList() };
    }

    private static string Context(Row[] rows, int index, bool include) => !include ? "" : string.Join("\n\n",
        rows.Take(index).Select((row, previous) => $"第 {previous + 1} 轮\n问：{row.Turn.Question}\n答：{row.Turn.Answer.DisplayText}"));
    private static string Normalize(string value) => value.Replace("\r\n", "\n");
    private static string Decode(string value) => WebUtility.HtmlDecode(value.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase));
    private static void Notify(Action changed) { try { changed(); } catch (ObjectDisposedException) { } }
    private sealed record Owner(ClipDraft Draft, int SharedPrefix, Dictionary<int, string> Bindings);
    private sealed record Row(ConversationTurn Turn, ClipDraft? Child, bool Frozen, BasicFields? Basic, string? IntegrityError, ClipDraft[] Owners);
    private sealed record ImportPlan(int Index, ClipDraft Child, ContentResult? Content, CardExportConfirmation Note, ClipDraft[] Owners);
}
