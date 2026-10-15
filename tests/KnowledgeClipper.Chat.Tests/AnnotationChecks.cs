using KnowledgeClipper.Workflow;

internal static class AnnotationChecks
{
    internal static async Task Run(string repo, string output, Action<bool,string> check)
    {
        var root=Path.Combine(output,"annotation-unit");Directory.CreateDirectory(root);
        var store=new DraftStore(root);var overrides=Path.Combine(root,"modes");
        var configuration=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),overrides);
        var registry=configuration.Load();
        WorkflowJson.Save(Path.Combine(overrides,"question-types.json"),registry with { RootId="fixture-root",BaseId="fixture-base",Types=registry.Types.Select(t=>t with{DocumentId="fixture-type"}).ToArray() });
        var chat=new ChatSession();var card=new CardComposition{Front="求面积。",Back="# 原解答\n\n- 原步骤\n  - 原理由"};chat.Compositions.Add(card);
        ChatComposition.Edit(card,card.Front,card.Back,card.TypeId,"",true);
        check(card.AnnotationEnabled&&!card.ManuallyEdited,"展开空批注不改正文或手工稿状态");
        var text="第一段批注：$x^2$。\n\n# 复核\n\n- 核对条件\n  - 子项理由";
        ChatComposition.Edit(card,card.Front,card.Back,card.TypeId,text);
        var back=card.Back;ChatComposition.Undo(card);
        check(card.Annotation==""&&card.AnnotationEnabled&&card.Back==back,"撤销批注只恢复批注，原正文保持不变");
        ChatComposition.Edit(card,card.Front,card.Back,card.TypeId,text);
        var rendered=ChatMarkdown.Render(card.Front,card.Back,[],card.Annotation);
        var plan=rendered.Card;var index=Array.FindIndex(plan.Back,row=>row.Length==1&&row[0].Text=="批注");
        check(index>=0&&plan.BackParents![index]==-1&&plan.Back[index+1].Any(p=>p.Kind=="latex"&&p.Text=="x^2")&&plan.BackParents[index+1]==index,"RemNote 批注为顶格背面母节点，内容与公式在其下");
        check(plan.BackParents![index+2]==index&&plan.BackParents[index+3]==index+2&&plan.BackParents[index+4]==index+3,"批注内原有标题和列表层级整体嵌入母节点下，不改变正文层级");
        check(rendered.BackHtml.Contains("<hr><div style=\"text-align:left\"><div>批注：</div>")&&rendered.BackHtml.IndexOf("原解答",StringComparison.Ordinal)<rendered.BackHtml.IndexOf("<hr>",StringComparison.Ordinal)&&rendered.BackHtml.Contains("\\(x^2\\)"),"Anki 正文之后分割线、左侧批注标题与原公式内容完整");
        check(!ChatMarkdown.Render("问题","答案",[]," \n").BackHtml.Contains("<hr>")&&ChatMarkdown.Render("问题","答案",[]).Card.Back.Length==1,"未输入批注时不导出空标题或分割线，旧卡格式不变");
        var pending=ChatComposition.Begin(chat,card,"formalize","",["fast-answer"]);
        ChatComposition.Parse(pending,"{\"cards\":[{\"front\":\"正式问题\",\"back\":\"无效\",\"typeId\":\"fast-answer\"}]}",chat,["fast-answer"]);
        ChatComposition.Adopt(chat,pending);
        check(card.Annotation==text&&card.Back==back,"书面化采用只更新问题，批注及背面正文不变");
        var material=new CaptureMaterial("公开批注格式验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        var draft=new ClipDraft{Original=material,Material=material,Settings=new(),Chat=chat,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes"))};store.Save(draft);
        var importer=new CapturingImporter();
        await ChatComposition.ExportAsync(draft,card,store,configuration,importer,ExportDestination.RemNote);
        check(importer.Imported!.ConfirmedRemNote!.Cards[0].BackParents!.SequenceEqual(plan.BackParents!)&&importer.Imported.ConfirmedRemNote.Cards[0].Back[index][0].Text=="批注","实际 RemNote 导出冻结完整批注节点与层级");
        var copy=ChatComposition.Editable(chat,card);
        check(copy.Annotation==text&&copy.AnnotationEnabled&&copy.ExportDraftId==null,"冻结卡编辑为新草稿保留批注，原卡身份不变");
        await ChatComposition.ExportAsync(draft,copy,store,configuration,importer,ExportDestination.Anki);
        check(importer.Imported!.ConfirmedNote!.Fields["Back"]==rendered.BackHtml,"实际 Anki 导出冻结分割线及批注 HTML");
        var restored=store.Find(draft.Id)!;
        check(restored.Chat!.Compositions.All(c=>c.Annotation==text&&c.AnnotationEnabled),"重开与冻结草稿均保留批注数据");
        var dual=new CardComposition{Front="双面问题",Back="双面答案",FrontAnnotation="正面批注 $x$",FrontAnnotationEnabled=true,Annotation=text,AnnotationEnabled=true};chat.Compositions.Add(dual);
        var dualRendered=ChatMarkdown.Render(dual.Front,dual.Back,[],dual.Annotation,dual.FrontAnnotation);
        await ChatComposition.ExportAsync(draft,dual,store,configuration,importer,ExportDestination.RemNote);
        check(importer.Imported!.ConfirmedRemNote!.Cards[0].Front.Any(p=>p.Text=="正面批注 ")&&importer.Imported.Content!.Basic!.Front.Contains("正面批注"),"组合卡真实导出入口将正面批注冻结在正面内容及 RemNote RichText");
        var dualCopy=ChatComposition.Editable(chat,dual);await ChatComposition.ExportAsync(draft,dualCopy,store,configuration,importer,ExportDestination.Anki);
        check(importer.Imported!.ConfirmedNote!.Fields["Front"]==dualRendered.FrontHtml&&importer.Imported.ConfirmedNote.Fields["Back"]==dualRendered.BackHtml,"组合卡真实导出入口冻结双面批注 Anki HTML，分区不串位");
    }
    private sealed class CapturingImporter:ICardImporter
    {
        internal ClipDraft? Imported;
        public ExportDestination? FixedDestination=>null;
        public Task ImportAsync(ClipDraft draft,DraftStore store,CancellationToken ct=default){Imported=draft;draft.ImportState=ImportState.Verified;store.Save(draft);return Task.CompletedTask;}
    }
}
