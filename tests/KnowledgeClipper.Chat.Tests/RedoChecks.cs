using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class RedoChecks
{
    internal static void Run(Action<bool,string> check)
    {
        void Reject(Action action,string name) { try { action(); } catch(InvalidOperationException) {check(true,name);return;}throw new Exception(name); }
        ChatSession Reload(ChatSession chat)=>JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(chat,WorkflowJson.Options),WorkflowJson.Options)!;
        var card=new CardComposition{Front="原问题 $x$",Back="原答案",FrontAnnotation="正注",Annotation="背注"};
        var chat=new ChatSession{Compositions=[card]};
        ChatComposition.Edit(card,"新问题 $y$","新答案","explain-concept","新背注",true,"新正注",true);
        var revision=card.Revision;
        ChatComposition.Undo(card,chat);
        check(card.Front=="原问题 $x$"&&card.RedoHistory.Count==1&&card.Revision>revision,"撤销保存可持久化重做记录，修订号递增");
        chat=Reload(chat);card=chat.Compositions[0];revision=card.Revision;
        ChatComposition.Redo(card,chat);
        check(card.Front=="新问题 $y$"&&card.Back=="新答案"&&card.TypeId=="explain-concept"&&card.FrontAnnotation=="新正注"&&card.Annotation=="新背注"&&card.FrontAnnotationEnabled&&card.AnnotationEnabled,"重开后重做恢复双面、类型及双批注");
        check(card.RedoHistory.Count==0&&card.Revision>revision,"重做消耗对应记录并保持修订号递增");
        ChatComposition.Undo(card,chat);ChatComposition.Edit(card,card.Front,"另一答案",card.TypeId);
        check(card.RedoHistory.Count==0,"撤销后新编辑清除旧分支，防止重做覆盖新稿");
        var before=card.Revision;ChatComposition.Redo(card,chat);
        check(card.Revision==before&&card.Back=="另一答案","无重做记录不修改正文或修订号");
        ChatComposition.Undo(card,chat);ChatComposition.Edit(card,card.Front,card.Back,card.TypeId);
        check(card.RedoHistory.Count==1,"相同内容的保存不清空有效重做链");
        card.ExportDraftId="frozen";
        Reject(()=>ChatComposition.Redo(card,chat),"冻结导出拒绝重做并保留原身份");card.ExportDraftId=null;
        var old=JsonSerializer.Deserialize<CardComposition>("{\"front\":\"旧问题\"}",WorkflowJson.Options)!;
        check(old.RedoHistory.Count==0,"旧草稿缺少重做字段时保持兼容");
        var multi=new CardComposition{Front="0"};
        for(var i=1;i<=25;i++)ChatComposition.Edit(multi,i.ToString(),"",multi.TypeId);
        for(var i=0;i<20;i++)ChatComposition.Undo(multi);
        check(multi.Front=="5"&&multi.RedoHistory.Count==20&&multi.UndoHistory.Count==0,"撤销与重做均有界保存最近20步");
        for(var i=0;i<20;i++)ChatComposition.Redo(multi);
        check(multi.Front=="25"&&multi.RedoHistory.Count==0&&multi.UndoHistory.Count==20,"连续重做按原顺序恢复全部20步");
        var message=new ChatMessage{Role="user",Parts=[new("text","来源问题")]};
        card=new(){Front="手工问题",ManuallyEdited=true};chat=new(){Compositions=[card],Messages=[message]};
        ChatComposition.Select(chat,card,message.Id,"front",true);ChatComposition.Undo(card,chat);ChatComposition.Redo(card,chat);
        check(card.FrontSources.SequenceEqual([message.Id])&&card.Front=="手工问题\n\n来源问题","重做来源选取完整恢复正文与来源身份");

        card=new(){Front="原整合问题",Back="原整合答案"};chat=new(){Compositions=[card]};
        var proposal=ChatComposition.Begin(chat,card,"integrate","",["fast-answer"]);
        ChatComposition.Parse(proposal,"{\"cards\":[{\"front\":\"问题一\",\"back\":\"答案一\",\"typeId\":\"fast-answer\"},{\"front\":\"问题二\",\"back\":\"答案二\",\"typeId\":\"fast-answer\"}]}",chat,["fast-answer"]);
        ChatComposition.Adopt(chat,proposal);
        var added=chat.Compositions[1];var addedId=added.Id;var addedRevision=added.Revision;
        ChatComposition.Undo(card,chat);
        check(chat.Compositions.Count==1&&card.Front=="原整合问题","整合多卡撤销同时保存新增卡的重做快照");
        chat=Reload(chat);card=chat.Compositions[0];ChatComposition.Redo(card,chat);
        check(chat.Compositions.Count==2&&chat.Compositions[1].Id==addedId&&chat.Compositions[1].Front=="问题二"&&card.Front=="问题一","整合多卡重开后重做恢复原卡ID、顺序和内容");
        check(chat.Compositions[1].Revision>addedRevision,"恢复新增卡使旧辅助结果的基准修订失效");
        ChatComposition.Undo(card,chat);ChatComposition.Redo(card,chat);
        check(chat.Compositions.Count==2&&chat.Compositions.Select(c=>c.Id).Distinct().Count()==2,"多次撤销重做不重复新增卡");
        chat.Compositions[1].ExportDraftId="confirmed";
        Reject(()=>ChatComposition.Undo(card,chat),"重做后已冻结的新增卡仍受整批撤销保护");
        chat.Compositions[1].ExportDraftId=null;ChatComposition.Undo(card,chat);
        chat.Compositions.Add(new(){Id=addedId,Front="独立新内容"});var count=card.RedoHistory.Count;
        Reject(()=>ChatComposition.Redo(card,chat),"重做遇到原卡ID已存在时拒绝覆盖");
        check(card.RedoHistory.Count==count&&chat.Compositions.Single(c=>c.Id==addedId).Front=="独立新内容","重做冲突保留整个待重做记录及现有卡片");
    }
}
