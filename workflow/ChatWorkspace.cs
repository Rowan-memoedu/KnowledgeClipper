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
    public static void Switch(ChatSession root, string id)
    {
        _ = Session(root, id);
        root.ActiveConversationId = id;
    }
    private static ChatSession Empty(ChatSession source) => new()
    {
        Binding = new() { Provider = source.Binding.Provider }, Model = source.Model, Effort = source.Effort,
        TypeId = source.TypeId, IncludeExamples = source.IncludeExamples, UseQuestionTypeDefaults = source.UseQuestionTypeDefaults
    };
    public static ChatBranch New(ChatSession root)
    {
        var branch = new ChatBranch { Title = "新对话 " + (root.Branches.Count + 1), Session = Empty(Active(root)) };
        root.Branches.Add(branch); root.ActiveConversationId = branch.Id; root.Version = 2;
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
        root.Branches.Add(branch); root.ActiveConversationId = branch.Id; root.Version = 2;
        return branch;
    }
}
