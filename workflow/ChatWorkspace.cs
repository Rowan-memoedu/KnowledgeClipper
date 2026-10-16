using System.Text.Json;

namespace KnowledgeClipper.Workflow;

// The original draft owns every tab and the shared cards. Main remains in the
// original fields so existing history, exports and version-1 drafts keep identity.
public static class ChatWorkspace
{
    public static ChatSession Active(ChatSession root) => Session(root, root.ActiveConversationId);
    public static ChatSession Session(ChatSession root, string id) => id == "main" ? root :
        root.Branches.SingleOrDefault(b => b.Id == id)?.Session ?? throw new InvalidDataException("找不到原对话 Tab。");
    public static IEnumerable<ChatMessage> Messages(ChatSession root) => root.Messages
        .Concat(root.Branches.SelectMany(b => b.Session.Messages)).DistinctBy(m => m.Id);
    public static IReadOnlyList<string> OpenIds(ChatSession root) => root.ConversationOrder ??
        new[] { "main" }.Concat(root.Branches.Select(b => b.Id)).ToList();
    public static void Switch(ChatSession root, string id)
    {
        if (!OpenIds(root).Contains(id)) throw new InvalidOperationException("此对话 Tab 已关闭，可撤回关闭后继续。");
        _ = Session(root, id);
        root.ActiveConversationId = id;
    }
    public static void Close(ChatSession root, string id)
    {
        var order = OpenIds(root).ToList(); var index = order.IndexOf(id);
        if (index < 0) throw new InvalidOperationException("此对话 Tab 已关闭。");
        order.RemoveAt(index); root.ConversationOrder = order;
        root.ClosedTabs.Add(new(id, index)); root.Version = 3;
        // Retain the session, native identity and card sources, even while a
        // response is in flight. Closing is a view operation, never deletion.
        if (order.Count == 0) New(root);
        else if (root.ActiveConversationId == id) root.ActiveConversationId = order[Math.Min(index, order.Count - 1)];
    }
    public static bool UndoClose(ChatSession root)
    {
        if (root.ClosedTabs.Count == 0) return false;
        var closed = root.ClosedTabs[^1]; _ = Session(root, closed.Id);
        var order = OpenIds(root).ToList();
        if (order.Contains(closed.Id)) throw new InvalidDataException("关闭记录与当前 Tab 不一致。");
        order.Insert(Math.Clamp(closed.Index, 0, order.Count), closed.Id);
        root.ConversationOrder = order; root.ClosedTabs.RemoveAt(root.ClosedTabs.Count - 1);
        root.ActiveConversationId = closed.Id; root.Version = 3;
        return true;
    }
    public static void Move(ChatSession root, string id, string targetId, bool after)
    {
        var order = OpenIds(root).ToList();
        if (!order.Contains(id) || !order.Contains(targetId)) throw new InvalidOperationException("Tab 已关闭，未改变排序。");
        if (id == targetId) return;
        order.Remove(id); order.Insert(order.IndexOf(targetId) + (after ? 1 : 0), id);
        root.ConversationOrder = order; root.Version = 3;
    }
    private static ChatSession Empty(ChatSession source) => new()
    {
        Binding = new() { Provider = source.Binding.Provider }, Model = source.Model, Effort = source.Effort,
        TypeId = source.TypeId, IncludeExamples = source.IncludeExamples, UseQuestionTypeDefaults = source.UseQuestionTypeDefaults
    };
    public static ChatBranch New(ChatSession root)
    {
        var order = OpenIds(root).ToList();
        var branch = new ChatBranch { Title = "新对话 " + (root.Branches.Count + 1), Session = Empty(Active(root)) };
        root.Branches.Add(branch); order.Add(branch.Id); root.ConversationOrder = order;
        root.ActiveConversationId = branch.Id; root.Version = Math.Max(root.Version, 2);
        return branch;
    }
    public static bool CanFork(ChatSession session, ChatMessage message)
    {
        if (session.Fork is { Stage: not "verified" } || message.Action != null || message.Status is not ("completed" or "interrupted")) return false;
        // Native history forks at turn boundaries. Never display a prefix while
        // secretly giving the model later output from the same turn.
        if (session.Binding.Provider == "codex" && message.Role == "assistant" && message.NativeTurnId != null)
            return session.Messages.LastOrDefault(m => m.NativeTurnId == message.NativeTurnId)?.Id == message.Id;
        return true;
    }
    public static ChatBranch Fork(ChatSession root, string messageId)
    {
        var order = OpenIds(root).ToList();
        var source = Active(root);
        var selected = source.Messages.Single(m => m.Id == messageId);
        if (!CanFork(source, selected)) throw new InvalidOperationException("请从本轮最后一条已完成回答分叉；未完成内容先核对原回合。");
        var index = source.Messages.IndexOf(selected);
        // A user-message fork reopens that question for editing, without sending
        // it or inheriting its old answer. An answer fork continues after it.
        var prefix = source.Messages.Take(index + (selected.Role == "assistant" ? 1 : 0)).ToArray();
        var retainedTurn = prefix.LastOrDefault(m => m.NativeTurnId != null)?.NativeTurnId;
        var session = Empty(source);
        session.Messages = JsonSerializer.Deserialize<List<ChatMessage>>(JsonSerializer.Serialize(prefix, WorkflowJson.Options), WorkflowJson.Options)!;
        session.Assets = prefix.SelectMany(m => m.Parts).Where(p => p.Image != null).Select(p => p.Image!).DistinctBy(a => a.Sha256).ToList();
        var turnIds = prefix.Select(m => m.NativeTurnId).Where(id => id != null).ToHashSet();
        session.Prompts = JsonSerializer.Deserialize<List<PromptSnapshot>>(JsonSerializer.Serialize(
            source.Prompts.Where(p => p.TurnId != null && turnIds.Contains(p.TurnId)), WorkflowJson.Options), WorkflowJson.Options)!;
        if (selected.Role == "user")
        {
            session.Input = selected.Text;
            session.Attachments = selected.Parts.Where(p => p.Image != null).Select(p => p.Image!).ToList();
        }
        if (source.Binding.Provider == "codex" && source.Binding.ThreadId != null && retainedTurn != null)
            session.Fork = new() { SourceThreadId = source.Binding.ThreadId, LastTurnId = retainedTurn };
        var branch = new ChatBranch { Title = "分叉 " + (root.Branches.Count + 1), ParentId = root.ActiveConversationId,
            MessageId = selected.Id, Session = session };
        root.Branches.Add(branch); order.Add(branch.Id); root.ConversationOrder = order;
        root.ActiveConversationId = branch.Id; root.Version = Math.Max(root.Version, 2);
        return branch;
    }
}
