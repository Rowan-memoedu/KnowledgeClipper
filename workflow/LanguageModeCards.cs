using System.Security.Cryptography;
using System.Text;

namespace KnowledgeClipper.Workflow;

public static class LanguageModeCards
{
    public static ClipDraft[] Ensure(ClipDraft parent, ModeDefinition[] selectedModes, DraftStore store)
    {
        lock (parent.ModeCards)
        {
            if (selectedModes == null || selectedModes.Length == 0 || selectedModes.Select(mode => mode.Id).Distinct(StringComparer.Ordinal).Count() != selectedModes.Length)
                throw new InvalidDataException("请选择不重复的具体语言模式。");
            var existing = ChildrenCore(parent, store, selectedOnly: false).ToDictionary(child => child.ModeId!, StringComparer.Ordinal);
            var selected = new List<ClipDraft>();
            var added = new List<ClipDraft>();
            foreach (var mode in selectedModes)
            {
                ValidateMode(mode);
                if (parent.Original.FrontImage != null && mode.Practice != PracticeChoice.Reading)
                    throw new InvalidDataException("原图语言卡仅支持理解与朗读。");
                var savedDefinition = parent.Configuration.Modes.SingleOrDefault(item => item.Definition.Id == mode.Id)?.Definition;
                if (savedDefinition != mode) throw new InvalidDataException("所选模式不属于本次保存的配置。");
                if (existing.TryGetValue(mode.Id, out var child)) { selected.Add(child); continue; }
                var id = ChildId(parent.Id, mode.Id);
                child = store.Find(id);
                if (child == null)
                {
                    child = new() { Id = id, Original = parent.Original, Material = parent.Material, Settings = parent.Settings,
                        Configuration = parent.Configuration, ModeId = mode.Id,
                        DictionaryDetails = parent.DictionaryDetails is { } details && details.Language == mode.Language &&
                            details.Target == parent.Material.SelectedText.Trim() ? details : null,
                        Route = new(mode.Id, "language", mode.FollowUpDefault, mode.RequiresContext),
                        Practice = mode.Practice!.Value, Input = parent.Input with { ReadRequested = false },
                        ModeParentId = parent.Id, ParentDraftId = parent.Id };
                    added.Add(child);
                }
                else ValidateChild(parent, mode.Id, id, child);
                selected.Add(child);
            }
            // Validate the entire selection before creating files or publishing any new binding.
            foreach (var child in added) store.Save(child);
            foreach (var child in selected) parent.ModeCards[child.ModeId!] = child.Id;
            parent.SelectedModeIds = selectedModes.Select(mode => mode.Id).ToArray();
            store.Save(parent);
            return selected.ToArray();
        }
    }

    public static ClipDraft[] Children(ClipDraft parent, DraftStore store)
    {
        lock (parent.ModeCards) return ChildrenCore(parent, store);
    }

    public static ClipDraft[] AllChildren(ClipDraft parent, DraftStore store)
    {
        lock (parent.ModeCards) return ChildrenCore(parent, store, selectedOnly: false);
    }

    public static async Task ImportAsync(ClipDraft parent, string[] modeIds, DraftStore store, ICardImporter anki,
        Action? changed = null, CancellationToken ct = default)
    {
        await parent.ModeImportGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (modeIds == null || modeIds.Length == 0) throw new InvalidDataException("请选择至少一个已就绪的语言模式。");
            var children = Children(parent, store).ToDictionary(child => child.ModeId!, StringComparer.Ordinal);
            var plans = new List<ImportPlan>();
            foreach (var modeId in modeIds.Distinct(StringComparer.Ordinal))
            {
                if (!children.TryGetValue(modeId, out var child)) throw new InvalidDataException("所选模式没有已保存的独立结果：" + modeId);
                if (Frozen(child))
                {
                    if (child.ConfirmedNote == null) throw new InvalidDataException("本模式缺少已确认卡片记录；不会重新添加：" + modeId);
                    plans.Add(new(child, null, ExportRouting.Prepare(child, child.ConfirmedNote, anki.FixedDestination)));
                    continue;
                }
                var content = child.PreviewContent;
                if (content == null || content.Fields == null && content.Basic == null || content.ModeId != modeId)
                    throw new InvalidDataException("本模式的独立结果尚未就绪：" + modeId);
                var candidate = new ClipDraft { Id = child.Id, Original = child.Original, Material = child.Material,
                    Settings = child.Settings, Configuration = child.Configuration, Input = child.Input,
                    ModeId = child.ModeId, Route = child.Route, Practice = child.Practice, ModeParentId = child.ModeParentId,
                    ParentDraftId = child.ParentDraftId, Content = content, DictionaryDetails = child.DictionaryDetails };
                plans.Add(new(child, content, ExportRouting.Prepare(child, ExportRouting.BuildNote(candidate, anki.FixedDestination), anki.FixedDestination)));
            }
            // A single confirmation freezes every chosen result and its edits before the first provider call.
            foreach (var plan in plans)
            {
                if (plan.Content != null)
                {
                    plan.Child.Content = plan.Content;
                    plan.Child.EditedFields = null;
                    plan.Child.EditedBasic = null;
                    plan.Note.Apply(plan.Child);
                    plan.Child.ImportState = ImportState.Ready;
                    plan.Child.ImportError = null;
                    plan.Child.Times["confirmed"] = DateTimeOffset.UtcNow;
                }
                store.Save(plan.Child);
            }
            store.Save(parent);
            Notify(changed);
            foreach (var plan in plans)
            {
                if (plan.Child.ImportState == ImportState.Verified) continue;
                ct.ThrowIfCancellationRequested();
                try { await anki.ImportAsync(plan.Child, store, ct); }
                finally { Notify(changed); }
            }
        }
        finally
        {
            try { store.Save(parent); }
            finally { parent.ModeImportGate.Release(); Notify(changed); }
        }
    }

    private static ClipDraft[] ChildrenCore(ClipDraft parent, DraftStore store, bool selectedOnly = true)
    {
        if (!Guid.TryParseExact(parent.Id, "N", out _)) throw new InvalidDataException("父草稿身份无效。");
        var persisted = store.Find(parent.Id);
        if (persisted != null)
        {
            if (persisted.Id != parent.Id) throw new InvalidDataException("父草稿本机身份不符。");
            foreach (var pair in persisted.ModeCards)
            {
                if (parent.ModeCards.TryGetValue(pair.Key, out var id) && id != pair.Value)
                    throw new InvalidDataException("本模式已有不同的子稿绑定；不会创建替代结果。");
                parent.ModeCards.TryAdd(pair.Key, pair.Value);
            }
        }
        var children = new List<ClipDraft>();
        foreach (var pair in parent.ModeCards)
        {
            var child = store.Find(pair.Value) ?? throw new InvalidDataException("已绑定的语言子稿缺失；不会重新生成：" + pair.Key);
            ValidateChild(parent, pair.Key, pair.Value, child);
            children.Add(child);
        }
        if (children.Select(child => child.Id).Distinct(StringComparer.Ordinal).Count() != children.Count)
            throw new InvalidDataException("多个语言模式关联了同一份结果。");
        if (!selectedOnly || parent.SelectedModeIds.Length == 0) return children.ToArray();
        if (parent.SelectedModeIds.Distinct(StringComparer.Ordinal).Count() != parent.SelectedModeIds.Length)
            throw new InvalidDataException("本次语言模式选择重复。");
        var byMode = children.ToDictionary(child => child.ModeId!, StringComparer.Ordinal);
        return parent.SelectedModeIds.Select(id => byMode.TryGetValue(id, out var child) ? child :
            throw new InvalidDataException("本次所选模式缺少已绑定子稿：" + id)).ToArray();
    }

    private static void ValidateChild(ClipDraft parent, string modeId, string id, ClipDraft child)
    {
        if (child.Id != id || child.ModeParentId != parent.Id || child.ParentDraftId != parent.Id ||
            child.ModeId != modeId || child.Route?.ModeId != modeId || child.Route.OutputKind != "language" || child.Original != parent.Original)
            throw new InvalidDataException("已绑定语言子稿的父级、模式或原文身份不符。");
        var definition = child.Configuration.Modes.SingleOrDefault(item => item.Definition.Id == modeId)?.Definition;
        if (definition == null) throw new InvalidDataException("已绑定语言子稿缺少模式配置。");
        ValidateMode(definition);
        if (child.Content != null && child.Content.ModeId != modeId) throw new InvalidDataException("已保存结果属于另一具体模式。");
    }

    private static void ValidateMode(ModeDefinition mode)
    {
        if (string.IsNullOrWhiteSpace(mode.Id) || mode.OutputKind != "language" || mode.Language is not ("zh" or "en") ||
            mode.Practice is not (PracticeChoice.Reading or PracticeChoice.Writing))
            throw new InvalidDataException("独立语言卡片需要具体语种和单一练习模式。");
    }
    private static string ChildId(string parentId, string modeId) => new Guid(SHA256.HashData(
        Encoding.UTF8.GetBytes("KnowledgeClipper.LanguageModeCards:" + parentId + ":" + modeId)).AsSpan(0, 16)).ToString("N");
    private static bool Frozen(ClipDraft draft) => draft.ConfirmedNote != null || draft.NoteId != null ||
        draft.ImportState is ImportState.Writing or ImportState.Unknown or ImportState.Verified;
    private static void Notify(Action? changed) { try { changed?.Invoke(); } catch (ObjectDisposedException) { } }
    private sealed record ImportPlan(ClipDraft Child, ContentResult? Content, CardExportConfirmation Note);
}
