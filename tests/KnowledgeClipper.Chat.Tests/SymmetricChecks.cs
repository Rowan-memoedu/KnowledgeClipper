using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class SymmetricChecks
{
    internal static void Run(string repo,string output,Action<bool,string> check)
    {
        var path=Path.Combine(output,"symmetric.png");
        File.WriteAllBytes(path,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
        var image=new ImageAsset(path,ImageAsset.Hash(File.ReadAllBytes(path)),"question",1);
        var picture="![图片](asset:"+image.Sha256+")";
        foreach(var action in new[]{"formalize","formalize-answer"})
        foreach(var frontOnly in new[]{true,false})
        foreach(var visual in new[]{true,false})
        {
            var chat=new ChatSession{Assets=[image]};var content=visual?picture:"已知长6厘米、宽8厘米，面积48平方厘米。";
            var card=new CardComposition{Front=frontOnly?content:"",Back=frontOnly?"":content,FrontAnnotation="正面笔记 $a$",FrontAnnotationEnabled=true,Annotation="背面笔记 $b$",AnnotationEnabled=true};chat.Compositions.Add(card);
            var front=card.Front;var back=card.Back;var p=ChatComposition.Begin(chat,card,action,"",["fast-answer"]);
            check(!visual||ChatComposition.ProposalImages(p,chat).Single().ReadVerified().Length>0,"任一面有图片均真实传入书面化："+action+frontOnly);
            ChatComposition.Parse(p,JsonSerializer.Serialize(new{cards=new[]{new{front="规范问题",back="规范答案",typeId="fast-answer",frontAnnotation="不应采用",annotation="不应采用"}}}),chat,["fast-answer"]);
            ChatComposition.Adopt(chat,p);
            check((action=="formalize"?card.Front=="规范问题"&&card.Back==back:card.Back=="规范答案"&&card.Front==front)&&card.FrontAnnotation=="正面笔记 $a$"&&card.Annotation=="背面笔记 $b$","任一正文存在可书面化对应目标，另一面及双批注原样保留："+action+frontOnly+visual);
            ChatComposition.Undo(card,chat);check(card.Front==front&&card.Back==back&&card.FrontAnnotationEnabled&&card.AnnotationEnabled,"撤销书面化恢复双面及批注状态："+action+frontOnly+visual);
        }
        foreach(var bodyImage in new[]{true,false})
        {
            var chat=new ChatSession{Assets=[image]};var card=new CardComposition{Front=bodyImage?picture:"无图问题 $x$",FrontAnnotation="注释前\n\n"+picture+"\n\n注释后",FrontAnnotationEnabled=true,Back="原答案",Annotation="原背注"};chat.Compositions.Add(card);
            var front=card.Front;var note=card.FrontAnnotation;var p=ChatComposition.Begin(chat,card,"transcribe","",["fast-answer"],"front");
            ChatComposition.Parse(p,JsonSerializer.Serialize(new{cards=new[]{new{front="正文识别",frontAnnotation="注释前\n\n图片识别 $x=1$\n\n注释后",back="错误改写",annotation="错误改写",typeId="fast-answer"}}}),chat,["fast-answer"]);
            ChatComposition.Adopt(chat,p);
            check(card.Front==(bodyImage?"正文识别":front)&&card.FrontAnnotation.Contains("x=1")&&card.Back=="原答案"&&card.Annotation=="原背注","正面图转文字覆盖正文及正面批注图片，保留无图正文和另一面："+bodyImage);
            var saved=JsonSerializer.Serialize(chat,WorkflowJson.Options);chat=JsonSerializer.Deserialize<ChatSession>(saved,WorkflowJson.Options)!;card=chat.Compositions[0];ChatComposition.Undo(card,chat);
            check(card.Front==front&&card.FrontAnnotation==note&&card.FrontAnnotationEnabled,"重开后一次撤销恢复正面正文及批注原图："+bodyImage);
            p=ChatComposition.Begin(chat,card,"transcribe","",["fast-answer"],"front");
            ChatComposition.Parse(p,"{\"cards\":[{\"front\":\"识别文字\",\"back\":\"原答案\",\"typeId\":\"fast-answer\"}]}",chat,["fast-answer"]);
            check(p.Status=="unparsed"&&card.FrontAnnotation==note,"正面批注图片结果缺失时整次拒绝，不丢图："+bodyImage);
        }
        var rendered=ChatMarkdown.Render("原问题","原答案",[image],"背面批注 $b$","- 正面批注 $a$\n  - "+picture);
        check(rendered.FrontHtml.Contains("<hr><div style=\"text-align:left\"><div>批注：</div>")&&rendered.FrontHtml.Contains("\\(a\\)")&&rendered.FrontHtml.Contains("<img")&&rendered.BackHtml.Contains("背面批注"),"双面 Anki HTML 均有分割线与左侧批注，正面公式图片保留");
        check(rendered.Card.Front.Any(p=>p.Kind=="image")&&rendered.Card.Front.Any(p=>p.Text=="批注：")&&rendered.Card.Front.Any(p=>p.Kind=="latex"&&p.Text=="a")&&!rendered.Card.Back.SelectMany(p=>p).Any(p=>p.Text=="a"),"RemNote 正面批注保留在正面 RichText，不泄漏到答案节点");
        var frozen=new CardComposition{Front="问",Back="答",FrontAnnotation=picture,FrontAnnotationEnabled=true,ExportDraftId="old"};var copies=new ChatSession();var copy=ChatComposition.Editable(copies,frozen);
        check(copy.FrontAnnotation==picture&&copy.FrontAnnotationEnabled&&copy.ExportDraftId==null,"冻结卡复制为新草稿完整保留正面图片批注");
        var legacy=JsonSerializer.Deserialize<CardComposition>("{\"front\":\"旧问题\",\"back\":\"旧答案\",\"annotation\":\"旧背注\",\"annotationEnabled\":true}",WorkflowJson.Options)!;
        check(legacy.FrontAnnotation==""&&!legacy.FrontAnnotationEnabled&&legacy.Annotation=="旧背注","旧草稿新增正面字段默认空，背面批注不迁移或串位");
        var configuration=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(output,"symmetric-overrides"));
        check(configuration.Freeze(new(){Model="fixture"},"",[image],"formalize-answer").Files.Any(f=>f.RelativePath=="actions/formalize-answer.md"),"书面化答案冻结独立规则及真实图片");
    }
}
