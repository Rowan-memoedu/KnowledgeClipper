using System.Security.Cryptography;
using System.Text;

namespace KnowledgeClipper.Workflow;

public static class GeneratedExampleDrafts
{
    public static ClipDraft Ensure(ClipDraft source, DraftStore store)
    {
        lock (source)
        {
            source.Material.Validate();
            if (source.Route?.OutputKind != "language" || source.Material.ContextText == null)
                throw new InvalidDataException("生成例句选项适用于已有来源上下文的语言卡片。");
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("KnowledgeClipper.GeneratedExample:" + source.Id)).AsSpan(0, 16)).ToString("N");
            if (source.GeneratedExampleDraftId != null && source.GeneratedExampleDraftId != id)
                throw new InvalidDataException("生成例句草稿关联不一致。");
            var draft = store.Find(id);
            if (draft == null)
            {
                draft = new() { Id = id, Original = source.Original,
                    Material = source.Material with { ContextText = null, TargetStart = null, ContextOrigin = "generated_allowed" },
                    ParentDraftId = source.Id, Settings = source.Settings, Configuration = source.Configuration,
                    ModeId = source.ModeId, Route = source.Route, Practice = source.Practice,
                    Input = source.Input with { ReadRequested = false } };
                draft.Times["requested"] = DateTimeOffset.UtcNow;
                store.Save(draft);
            }
            else if (draft.ParentDraftId != source.Id || draft.Material.ContextOrigin != "generated_allowed" ||
                draft.Material.ContextText != null || draft.Material.SelectedText != source.Material.SelectedText)
                throw new InvalidDataException("已存在的生成例句草稿与来源不一致。");
            source.GeneratedExampleDraftId = id;
            store.Save(source);
            return draft;
        }
    }
}
