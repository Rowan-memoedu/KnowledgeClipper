using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class AllFormalizeChecks
{
    internal static void Run(string repo,string output,Action<bool,string> check)
    {
        foreach(var frontOnly in new[]{true,false})
        foreach(var notes in new[]{true,false})
        {
            var card=new CardComposition{Front=frontOnly?"原问题":"",Back=frontOnly?"":"原答案",FrontAnnotation=notes?"原正注":"",Annotation=notes?"原背注":"",FrontAnnotationEnabled=notes,AnnotationEnabled=notes};var chat=new ChatSession{Compositions=[card]};
            var before=JsonSerializer.Serialize(new{card.Front,card.Back,card.FrontAnnotation,card.Annotation},WorkflowJson.Options);
            var p=ChatComposition.Begin(chat,card,"formalize-all","",["fast-answer"]);
            using(var input=JsonDocument.Parse(p.InputSnapshot))check(input.RootElement.GetProperty(frontOnly?"generateBack":"generateFront").GetBoolean(),"全部书面化沿用缺面补齐标记："+frontOnly+notes);
            ChatComposition.Parse(p,JsonSerializer.Serialize(new{cards=new[]{new{front="整理问题",back="整理答案",frontAnnotation="整理正注",annotation="整理背注",typeId="fast-answer"}}}),chat,["fast-answer"]);ChatComposition.Adopt(chat,p);
            check(card.Front=="整理问题"&&card.Back=="整理答案"&&card.FrontAnnotation==(notes?"整理正注":"")&&card.Annotation==(notes?"整理背注":""),"全部书面化同时更新四字段，空批注不凭空添加："+frontOnly+notes);
            var restored=JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(chat,WorkflowJson.Options),WorkflowJson.Options)!;card=restored.Compositions[0];ChatComposition.Undo(card,restored);
            check(JsonSerializer.Serialize(new{card.Front,card.Back,card.FrontAnnotation,card.Annotation},WorkflowJson.Options)==before,"一次撤销及重开同时恢复四个原字段："+frontOnly+notes);
        }
        foreach(var side in new[]{"front","back"})
        {
            var card=new CardComposition{Front="原问题",Back="原答案",FrontAnnotation="原正注",Annotation="原背注"};var chat=new ChatSession{Compositions=[card]};
            var p=ChatComposition.Begin(chat,card,"formalize-annotation","",["fast-answer"],side);
            ChatComposition.Parse(p,"{\"cards\":[{\"front\":\"不应覆盖\",\"back\":\"不应覆盖\",\"frontAnnotation\":\"整理正注\",\"annotation\":\"整理背注\",\"typeId\":\"fast-answer\"}]}",chat,["fast-answer"]);ChatComposition.Adopt(chat,p);
            check(card.Front=="原问题"&&card.Back=="原答案"&&card.FrontAnnotation==(side=="front"?"整理正注":"原正注")&&card.Annotation==(side=="back"?"整理背注":"原背注"),"单独书面化批注由代码锁定双面正文及另一批注："+side);
            ChatComposition.Undo(card,chat);check(card.FrontAnnotation=="原正注"&&card.Annotation=="原背注","单批注书面化可撤销："+side);
            p=ChatComposition.Begin(chat,card,"formalize-all","",["fast-answer"]);
            ChatComposition.Parse(p,"{\"cards\":[{\"front\":\"问题\",\"back\":\"答案\",\"typeId\":\"fast-answer\"}]}",chat,["fast-answer"]);
            check(p.Status=="unparsed"&&card.Front=="原问题"&&card.Annotation=="原背注"&&card.FrontAnnotation=="原正注","全部结果遗漏批注时整次拒绝，四字段原稿不变："+side);
        }
        var cfg=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(output,"all-overrides"));var snapshot=cfg.Freeze(new(){Model="fixture"},"",[],"formalize-all");
        check(snapshot.Files.Any(f=>f.RelativePath=="actions/formalize.md")&&snapshot.Files.Any(f=>f.RelativePath=="actions/formalize-answer.md")&&snapshot.Files.Last().RelativePath=="actions/formalize-all.md","全部书面化复用问题和答案规则，最后冻结批量事务规则");
        check(cfg.Freeze(new(){Model="fixture"},"",[],"formalize-annotation").Action=="formalize-annotation","批注书面化有独立可冻结规则");
    }
}
