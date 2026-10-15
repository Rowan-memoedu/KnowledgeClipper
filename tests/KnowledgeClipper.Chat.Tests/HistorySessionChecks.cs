using KnowledgeClipper.Workflow;

internal static class HistorySessionChecks
{
    internal static void Run(string repo, string output, Action<bool,string> check)
    {
        var root=Path.Combine(output,"history-grouping",Guid.NewGuid().ToString("N"));
        var store=new DraftStore(root);var excerpts=new ExcerptStore(root);
        var config=ModeConfigurationLoader.Load(Path.Combine(repo,"modes"));
        var material=new CaptureMaterial("公开会话",null,"generated_allowed",null,"公开历史分组",0,DateTimeOffset.UtcNow);
        ClipDraft Make(string? parent=null)=>new(){Original=material,Material=material,
            Settings=new(),Configuration=config,Input=new(Intent:"question"),ModeId="question",ParentDraftId=parent,Chat=parent==null?new():null};
        var owner=Make();owner.Chat!.Messages.AddRange([new(){Role="user",Parts=[new("text","原问题")]},new(){Role="assistant",Parts=[new("text","原答案")]}]);
        var first=Make(owner.Id);first.ImportState=ImportState.Verified;store.Save(first);
        var second=Make(owner.Id);second.ImportState=ImportState.Unknown;store.Save(second);
        owner.Chat.Compositions.AddRange([new(){Id=first.Id,ExportDraftId=first.Id,Front="第一卡",Back="原答案"},
            new(){Id=second.Id,ExportDraftId=second.Id,Front="第二卡",Back="待核对"},new(){Front="未导出卡",Back="本地稿"}]);store.Save(owner);
        var before=Directory.GetFiles(store.DirectoryPath).ToDictionary(p=>p,File.ReadAllText);
        var sessions=store.AllSessions().ToArray();
        check(sessions.Length==1&&sessions[0].Id==owner.Id,"同一对话三张卡只产生一条历史，Verified/Unknown 子卡不单列");
        check(HistoryRecords.ResolveChatSession(first,store).Id==owner.Id,"旧单卡历史入口解析到原会话");
        check(HistoryRecords.ChatStatus(sessions[0],store).Contains("共 3 张 · 已导入 1 张"),"问答历史汇总同一会话的卡片总数与已导入数");
        check(HistoryRecords.ActionText(new(Draft:sessions[0]),store,excerpts)=="继续制卡","混合已导入、Unknown 与未导出卡保留继续制卡");
        check(before.All(p=>File.ReadAllText(p.Key)==p.Value),"历史分组、状态汇总与入口解析均不写草稿");
        first.Chat=new(){Messages=[new(){Role="user"},new(){Role="assistant"}]};
        first.Chat.Compositions.Add(new(){ExportDraftId=first.Id,Front="第一卡",Back="原答案"});
        var extra=new CardComposition{Front="错误单卡页内新增的卡",Back="必须保留的编辑稿",FrontAnnotation="原批注"};
        first.Chat.Compositions.Add(extra);store.Save(first);
        var childBytes=File.ReadAllText(store.Identity(first.Id));
        var grouped=store.AllSessions().Single();
        check(grouped.Chat!.Compositions.Count==4&&grouped.Chat.Compositions.Single(c=>c.Id==extra.Id).FrontAnnotation=="原批注","错误单卡页内新增卡稿和批注恢复到原会话且不重复冻结卡");
        check(ReferenceEquals(grouped.Chat.Compositions[0],owner.Chat.Compositions[0])==false&&File.ReadAllText(store.Identity(first.Id))==childBytes,"恢复使用独立投影，原子草稿字节保留");
        HistoryRecords.RestoreChatCards(grouped,store);store.Save(grouped);
        check(store.AllSessions().Single().Chat!.Compositions.Count==4,"保存后重复恢复不增加卡片");
        var grandchild=Make(first.Id);grandchild.ImportState=ImportState.Failed;store.Save(grandchild);
        first.Chat.Compositions.Add(new(){Id=grandchild.Id,ExportDraftId=grandchild.Id,Front="更深层导出",Back="失败稿"});store.Save(first);
        check(store.AllSessions().Single().Chat!.Compositions.Count==5&&HistoryRecords.ResolveChatSession(grandchild,store).Id==owner.Id,"嵌套导出仍归原会话，失败卡保留同一视图");
        var orphan=Make(Guid.NewGuid().ToString("N"));store.Save(orphan);
        check(store.AllSessions().Any(d=>d.Id==orphan.Id)&&HistoryRecords.ResolveChatSession(orphan,store).Id==orphan.Id,"缺失父会话时保留孤立草稿入口");
        var unrelated=Make(owner.Id);store.Save(unrelated);
        check(store.AllSessions().Any(d=>d.Id==unrelated.Id),"仅有 ParentDraftId 而没有反向绑定不得吞并独立记录");
        first.Chat.Prompts.Add(new());store.Save(first);
        check(store.AllSessions().Any(d=>d.Id==first.Id)&&HistoryRecords.ResolveChatSession(first,store).Id==first.Id,"子卡后来实际发送的新对话作为独立分支保留");
        var missingLink=Make(owner.Id);missingLink.ImportState=ImportState.Unknown;store.Save(missingLink);
        owner.Chat.Compositions.Add(new(){Id=missingLink.Id,Front="崩溃前已冻结卡"});store.Save(owner);
        var recovered=store.AllSessions().Single(d=>d.Id==owner.Id);
        check(recovered.Chat!.Compositions.Single(c=>c.Id==missingLink.Id).ExportDraftId==missingLink.Id,"子卡落盘但父链接回执丢失时恢复原冻结身份");
        var other=Make();other.Chat!.Compositions.Add(new(){ExportDraftId=other.Id,Front="旧会话自身冻结"});store.Save(other);
        check(store.AllSessions().Any(d=>d.Id==other.Id),"会话自身就是导出草稿时不产生自循环或丢失历史");
        var cycleId=Guid.NewGuid().ToString("N");var cycleFirst=Make(cycleId);cycleFirst.Chat=new(){Compositions=[new(){ExportDraftId=cycleId,Front="循环一"}]};
        var cycleSecond=new ClipDraft{Id=cycleId,Original=material,Material=material,Settings=new(),Configuration=config,Input=new(Intent:"question"),ParentDraftId=cycleFirst.Id,Chat=new(){Compositions=[new(){ExportDraftId=cycleFirst.Id,Front="循环二"}]}};
        store.Save(cycleFirst);store.Save(cycleSecond);
        check(store.AllSessions().Count(d=>d.Id==cycleId||d.Id==cycleFirst.Id)==2&&HistoryRecords.ResolveChatSession(cycleFirst,store).Id==cycleFirst.Id,"损坏父链循环保留两条可访问记录，不吞并或任意选择根");
    }
}
