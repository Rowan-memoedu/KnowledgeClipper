using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed partial class LanguageDesktopFlow
{
    private readonly List<LanguageChatForm> chatForms = new();
    private ChatConfigurationLoader ChatConfiguration => new(Path.Combine(ModeDefaults, "chat"), Path.Combine(ModeOverrides, "chat"));
    internal Task<bool> StartQuestionInputAsync(string text, IReadOnlyList<byte[]> images, bool autoAnswer = false)
    {
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0) return Task.FromResult(false);
        try
        {
            var assets = images.Select((bytes, index) => LanguageQuestionInputBox.StoreImage(dataRoot, bytes, index + 1)).ToArray();
            var material = new CaptureMaterial(string.IsNullOrWhiteSpace(text) ? "图片提问" : text, null, "generated_allowed", null,
                "提问输入", 0, DateTimeOffset.UtcNow) { FrontImage = assets.FirstOrDefault(), VisualQuestion = assets.Length > 0 };
            material.Validate();
            var draft = new ClipDraft { Original = material, Material = material, Settings = Settings with { },
                Configuration = ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides), Input = new("typed", "question", ExplicitQuestion: text), ModeId = "question" };
            StartChat(draft, autoAnswer, text, assets);
            return Task.FromResult(true);
        }
        catch (Exception ex) { status("输入未提交：" + ex.Message); return Task.FromResult(false); }
    }

    private void StartChat(ClipDraft draft, bool autoAnswer = false, string input = null, IReadOnlyList<ImageAsset> images = null, bool useTypeDefaults = false)
    {
        draft = LiveDraft(HistoryRecords.ResolveChatSession(draft, store));
        HistoryRecords.RestoreChatCards(draft, store);
        if (draft.Chat == null)
        {
            var path = Path.Combine(dataRoot, "chat-preferences.json");
            var preferences = File.Exists(path) ? WorkflowJson.Read<ChatPreferences>(path) : new();
            if (autoAnswer) preferences = new() { Provider = "api", Model = draft.Settings.ImageModel, TypeId = "fast-answer" };
            else if (useTypeDefaults) preferences = preferences with { Provider = "codex" };
            draft.Chat = new() { Binding = new() { Provider = preferences.Provider }, Model = preferences.Model,
                Effort = preferences.Effort, TypeId = preferences.TypeId,
                UseQuestionTypeDefaults = useTypeDefaults && !autoAnswer,
                Input = input ?? (draft.Original.FrontImage == null ? draft.QuestionText : "") };
            ChatTypeDefaults.Apply(draft.Chat);
            var attachments = images ?? (draft.Original.FrontImage is { } image ? new[] { image } : Array.Empty<ImageAsset>());
            draft.Chat.Attachments.AddRange(attachments); draft.Chat.Assets.AddRange(attachments.DistinctBy(a => a.Sha256));
            if (draft.Route == null) draft.Route = new(draft.ModeId ?? "question", "question", true, false);
            draft.Chat.LegacyTurnCount = draft.Conversation.Count;
            foreach (var turn in draft.Conversation)
            {
                draft.Chat.Messages.Add(new() { Role = "user", Parts = [new("text", turn.Question)] });
                draft.Chat.Messages.Add(new() { Role = "assistant", Parts = [new("text", turn.Answer.DisplayText)] });
            }
            if (draft.Conversation.Count > 0 && draft.Original.FrontImage is { } priorImage)
                draft.Chat.Messages[0].Parts.Insert(0, new("image", Image: priorImage));
            if (draft.Conversation.Count > 0) { draft.Chat.Input = draft.PendingFollowUp ?? ""; draft.Chat.Attachments.Clear(); }
            if (draft.Conversation.Count > 0)
            {
                try { draft.Chat.Compositions.AddRange(QuestionCards.RestoreChatCompositions(draft, store, draft.Chat)); }
                catch (InvalidDataException ex) { draft.Chat.Binding.Error = "历史卡片身份需要核对，原记录已保留：" + ex.Message; }
            }
            else if (HistoryRecords.Kind(draft) == "问答" && draft.PreviewContent is { } content)
            {
                var basic = content.Basic;
                var front = basic?.Front ?? draft.QuestionText;
                var back = basic?.Back ?? content.Answer?.DisplayText ?? "";
                var question = new ChatMessage { Role = "user", Parts = [new("text", front)] };
                if (draft.Original.FrontImage is { } originalImage) question.Parts.Add(new("image", Image: originalImage));
                var answer = new ChatMessage { Role = "assistant", Parts = [new("text", back)] };
                draft.Chat.Messages.AddRange([question, answer]);
                draft.Chat.Compositions.Add(new() { TypeId = draft.Chat.TypeId, Front = ChatComposition.Material(question), Back = back,
                    FrontSources = [question.Id], BackSources = [answer.Id], ManuallyEdited = true,
                    ExportDraftId = ExportRouting.Frozen(draft) ? draft.Id : null });
                draft.Chat.Input = draft.PendingFollowUp ?? ""; draft.Chat.Attachments.Clear();
            }
            if (!ExportRouting.Frozen(draft)) draft.QuestionTarget ??= ChatConfiguration.Target(preferences.TypeId);
            store.Save(draft);
        }
        // Existing floating Card drafts gain the defaults without changing a bound API thread.
        if (!autoAnswer && draft.Chat.Binding.Provider == "codex" && draft.Input.Intent == "question" && draft.Input.Source is "selection-dot" or "image-visual" or "image-ocr")
        {
            draft.Chat.UseQuestionTypeDefaults = true;
            ChatTypeDefaults.Apply(draft.Chat);
        }
        var existing = chatForms.FirstOrDefault(f => !f.IsDisposed && f.Draft.Id == draft.Id);
        if (existing != null) { existing.Show(); existing.Activate(); return; }
        var form = new LanguageChatForm(draft, store, dataRoot, baseRoot,
            () => ContentCredentials.Resolve(DesktopCredentialSettings.Load(dataRoot).DeepSeekApiKey), () =>
            {
                var material = new CaptureMaterial("新对话", null, "generated_allowed", null, "", 0, DateTimeOffset.UtcNow);
                var next = new ClipDraft { Original = material, Material = material, Settings = Settings,
                    Configuration = ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides), Input = new(Intent: "question"), ModeId = "question" };
                StartChat(next, useTypeDefaults: draft.Chat.UseQuestionTypeDefaults); next.Chat.Input = ""; store.Save(next);
            });
        form.Saved += saved => DraftChanged?.Invoke(saved);
        if (autoAnswer) form.RequestInitialSend();
        // A window owned by the topmost capture bar inherits its topmost band.
        chatForms.Add(form); form.FormClosed += (_, _) => chatForms.Remove(form); form.Show();
        latest = draft;
    }
}
