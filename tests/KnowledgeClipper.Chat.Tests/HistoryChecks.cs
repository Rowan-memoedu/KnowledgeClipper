using KnowledgeClipper.Workflow;

internal static class HistoryChecks
{
    internal static async Task Run(string repo,string output,Action<bool,string> check)
    {
        var store=new DraftStore(Path.Combine(output,"history-unit",Guid.NewGuid().ToString("N")));
        var material=new CaptureMaterial("原问题",null,"generated_allowed",null,"公开回归",0,DateTimeOffset.UtcNow);
        var parent=new ClipDraft{Original=material,Material=material,Settings=new(),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Input=new(Intent:"question"),ModeId="question",Route=new("question","question",true,false),Content=new("question",Basic:new("原问题","原答案"))};
        parent.Conversation.Add(new("原问题",new("answered","原答案",[])));parent.EditedAnswers[0]="编辑后的答案";store.Save(parent);
        var chat=new ChatSession{Messages=[new(){Role="user",Parts=[new("text","原问题")]},new(){Role="assistant",Parts=[new("text","原答案")]}]};
        var restored=QuestionCards.RestoreChatCompositions(parent,store,chat).Single();
        check(restored.Back=="编辑后的答案"&&restored.ExportDraftId==null&&restored.FrontSources.Single()==chat.Messages[0].Id,"旧历史读取保留编辑答案和消息来源");
        parent.ConfirmedNote=AnkiNoteDraft.From(parent);parent.ConfirmedDestination=ExportDestination.Anki;parent.ImportState=ImportState.Unknown;store.Save(parent);
        restored=QuestionCards.RestoreChatCompositions(parent,store,chat).Single();parent.Chat=chat;chat.Compositions.Add(restored);
        check(restored.ExportDraftId==parent.Id&&restored.Back=="原答案","旧冻结记录保留原身份和冻结答案");
        var importer=new HistoryImporter(parent);var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(output,"history-prompts"));
        await ChatComposition.ExportAsync(parent,restored,store,config,importer,ExportDestination.Anki);
        check(importer.Calls==1&&parent.ImportState==ImportState.Verified&&store.Find(parent.Id)!.ImportState==ImportState.Verified,"旧卡等于会话本身时复用对象，核对结果不会被旧父状态覆盖");
        await ChatComposition.ExportAsync(parent,restored,store,config,importer,ExportDestination.Anki);
        check(importer.Calls==1,"已核验历史卡重复操作不重发导入");
        var bad=new ClipDraft{Original=material,Material=material,Settings=new(),Configuration=parent.Configuration,Input=new(Intent:"question")};bad.Conversation.Add(parent.Conversation[0]);bad.FollowUpCards[0]=Guid.NewGuid().ToString("N");store.Save(bad);
        var rejected=false;try{QuestionCards.RestoreChatCompositions(bad,store,chat);}catch(InvalidDataException){rejected=true;}
        check(rejected,"旧身份缺失时拒绝建立替代卡，保留原记录待核对");
        var imagePath=Path.Combine(output,"history-pixel.png");File.WriteAllBytes(imagePath,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
        var image=new ImageAsset(imagePath,ImageAsset.Hash(File.ReadAllBytes(imagePath)),"question",1);
        var visual=new ClipDraft{Original=material with{FrontImage=image},Material=material with{FrontImage=image},Settings=new(),Configuration=parent.Configuration,Input=new(Intent:"question")};
        visual.Conversation.AddRange([new("图中是什么？",new("answered","正方形",[])),new("还有哪些条件？",new("unresolved",null,["missing_facts"]))]);store.Save(visual);
        var imageChat=new ChatSession{Messages=[new(),new(),new(),new()]};var imageCards=QuestionCards.RestoreChatCompositions(visual,store,imageChat);
        check(imageCards.All(c=>c.Front.Contains("asset:"+image.Sha256))&&imageCards[1].Back.Contains("缺少必要条件"),"旧多轮图片每张卡保留同一原图，未解答提示正常兼容");
    }
    private sealed class HistoryImporter(ClipDraft expected):ICardImporter
    {
        public int Calls;public ExportDestination? FixedDestination=>ExportDestination.Anki;
        public Task ImportAsync(ClipDraft draft,DraftStore store,CancellationToken ct=default){if(!ReferenceEquals(draft,expected))throw new Exception("Historical parent identity was copied");Calls++;draft.ImportState=ImportState.Verified;store.Save(draft);return Task.CompletedTask;}
    }
}
