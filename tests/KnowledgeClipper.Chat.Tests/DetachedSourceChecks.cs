using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class DetachedSourceChecks
{
    internal static void Run(string output, Action<bool, string> check)
    {
        var path = Path.Combine(output, "detached-note.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
        var image = new ImageAsset(path, ImageAsset.Hash(File.ReadAllBytes(path)), "question", 1);
        foreach (var side in new[] { "front", "back" })
        foreach (var withImage in new[] { false, true })
        {
            var note = "口语批注：比值趋近1时还要另行判断。" + (withImage ? "\n![图](asset:" + image.Sha256 + ")" : "");
            var card = new CardComposition { Front = "求级数收敛区间", Back = "先求半径，再验证端点。",
                FrontAnnotation = side == "front" ? note : "保留正注", Annotation = side == "back" ? note : "保留背注",
                FrontAnnotationEnabled = true, AnnotationEnabled = true,
                FrontSources = ["previous-question"], BackSources = ["previous-answer"] };
            var chat = new ChatSession { Compositions = [card], Assets = [image] };
            string State(CardComposition c) => JsonSerializer.Serialize(new { c.Front, c.Back, c.FrontAnnotation, c.Annotation,
                c.FrontAnnotationEnabled, c.AnnotationEnabled, c.FrontSources, c.BackSources }, WorkflowJson.Options);
            var before = State(card);
            var proposal = ChatComposition.Begin(chat, card, "formalize-annotation", "", ["fast-answer"], side);
            using (var input = JsonDocument.Parse(proposal.InputSnapshot))
                check(input.RootElement.GetProperty("messages").GetArrayLength() == 0 &&
                    input.RootElement.GetProperty(side == "front" ? "frontAnnotation" : "annotation").GetString() == note,
                    $"历史消息不在当前对话时仍使用完整原批注：{side}/{withImage}");
            check(ChatComposition.ProposalImages(proposal, chat).Length == (withImage ? 1 : 0), $"脱离原会话的批注图片仍作为真实附件发送：{side}/{withImage}");
            ChatComposition.Parse(proposal, JsonSerializer.Serialize(new { cards = new[] { new {
                front = "不能覆盖的问题", back = "不能覆盖的答案", frontAnnotation = "书面正注", annotation = "书面背注", typeId = "fast-answer"
            } } }), chat, ["fast-answer"]);
            ChatComposition.Adopt(chat, proposal);
            check(card.Front == "求级数收敛区间" && card.Back == "先求半径，再验证端点。" &&
                card.FrontAnnotation == (side == "front" ? "书面正注" : "保留正注") && card.Annotation == (side == "back" ? "书面背注" : "保留背注") &&
                card.FrontSources.SequenceEqual(new[] { "previous-question" }) && card.BackSources.SequenceEqual(new[] { "previous-answer" }),
                $"书面化仅替换目标批注并保留历史来源：{side}/{withImage}");
            var after = State(card);
            var restored = JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(chat, WorkflowJson.Options), WorkflowJson.Options)!;
            card = restored.Compositions[0]; ChatComposition.Undo(card, restored);
            check(State(card) == before, $"重开后撤销精确恢复批注、图片、正文及来源：{side}/{withImage}");
            ChatComposition.Redo(card, restored);
            check(State(card) == after, $"重开后重做恢复书面化结果：{side}/{withImage}");
        }
        var first = new ChatMessage { Id = "first", Role = "user", Parts = [new("text", "第一条可用来源")] };
        var second = new ChatMessage { Id = "second", Role = "assistant", Parts = [new("text", "第二条可用来源")] };
        var mixed = new CardComposition { Front = "问题", Back = "答案", Annotation = "批注", FrontSources = ["missing", "second", "first"], BackSources = ["second", "absent"] };
        var session = new ChatSession { Messages = [first, second], Compositions = [mixed] };
        foreach (var action in new[] { "formalize-annotation", "formalize-all", "formalize", "formalize-answer", "integrate" })
        {
            var proposal = ChatComposition.Begin(session, mixed, action, "", ["fast-answer"], action == "formalize-annotation" ? "back" : null);
            using var input = JsonDocument.Parse(proposal.InputSnapshot);
            check(input.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetString()).SequenceEqual(new[] { "second", "first" }),
                "缺失和现存来源混合时保留可用来源的选择顺序并去重：" + action);
        }
        var empty = new CardComposition { FrontSources = ["missing"] };
        mixed.Annotation += "\n![图](asset:" + image.Sha256 + ")"; session.Assets.Add(image);
        var transcribe = ChatComposition.Begin(session, mixed, "transcribe", "", ["fast-answer"], "back");
        check(ChatComposition.ProposalImages(transcribe, session).Single().Sha256 == image.Sha256,
            "历史来源不在当前会话时图转文字仍能读取批注图片");
        var rejected = false;
        try { ChatComposition.Begin(new(), empty, "formalize-all", "", ["fast-answer"]); }
        catch (InvalidDataException ex) { rejected = ex.Message.Contains("请先填写"); }
        check(rejected && empty.FrontSources.SequenceEqual(new[] { "missing" }), "空白卡仅有缺失来源时仍拒绝生成，保留原来源");
    }
}
