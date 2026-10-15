using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task GroupedHistoryRoutes(Form owner, LanguageSettings settings, Func<string,ClipDraft> make, object initial)
    {
        var parent=make("公开多卡会话历史");parent.Chat=new(){Model="history-fixture",Input="未发送的原输入",Messages=[new(){Role="user",Parts=[new("text","公开原问题")]},new(){Role="assistant",Parts=[new("text","公开原答案")]}]};
        var first=make("第一张已导入卡");
        first=new(){Id=first.Id,Original=first.Original,Material=first.Material,Settings=settings,Configuration=first.Configuration,
            Input=first.Input,ModeId="question",Route=first.Route,ParentDraftId=parent.Id,ImportState=ImportState.Verified,Content=new("question",Basic:new("第一张已导入卡","公开答案一"))};
        var second=new ClipDraft{Original=first.Original,Material=first.Material,Settings=settings,Configuration=first.Configuration,Input=first.Input,
            ModeId="question",Route=first.Route,ParentDraftId=parent.Id,ImportState=ImportState.Unknown,Content=new("question",Basic:new("第二张待核对卡","公开答案二"))};
        parent.Chat.Compositions.AddRange([new(){Id=first.Id,ExportDraftId=first.Id,Front="第一张已导入卡",Back="公开答案一"},
            new(){Id=second.Id,ExportDraftId=second.Id,Front="第二张待核对卡",Back="公开答案二"},new(){Front="第三张本地稿",Back="公开答案三",FrontAnnotation="保留批注"}]);
        first.Chat=new(){Model="history-fixture",Messages=[new(){Role="user"},new(){Role="assistant"}],Compositions=[new(){ExportDraftId=first.Id,Front="第一张已导入卡",Back="公开答案一"},new(){Front="错误单卡页新增的第四张",Back="额外手工稿"}]};
        store.Save(first);store.Save(second);store.Save(parent);
        var firstBytes=File.ReadAllText(store.Identity(first.Id));var secondBytes=File.ReadAllText(store.Identity(second.Id));
        var messageIds=parent.Chat.Messages.Select(m=>m.Id).ToArray();
        for(var cold=0;cold<2;cold++)
        {
            if(cold==1){((IDisposable)flow).Dispose();flow=desktop.GetType("KnowledgeClipper.LanguageDesktopFlow",true)!.GetConstructors(Flags).Single().Invoke([owner,data,(Action<string>)(_=>{}),initial]);}
            using var excerpt=(IDisposable)desktop.GetType("KnowledgeClipper.ExcerptDesktopFlow",true)!.GetConstructors(Flags).Single().Invoke([owner,data,settings,(Action<string>)(_=>{})]);
            using var historyFlow=(IDisposable)desktop.GetType("KnowledgeClipper.RecordHistoryFlow",true)!.GetConstructors(Flags).Single().Invoke([owner,data,flow,excerpt]);
            Call(historyFlow,"Show");
            var history=(Form)historyFlow.GetType().GetField("history",Flags)!.GetValue(historyFlow)!;
            var until=DateTime.UtcNow.AddSeconds(20);
            while(!Controls(history).OfType<Button>().Any(b=>b.Name=="knowledge-clipper.history.continue.draft."+parent.Id)){
                if(DateTime.UtcNow>until)throw new TimeoutException("Grouped history row did not load");await Task.Delay(30);
            }
            Check(!Controls(history).OfType<Button>().Any(b=>b.Name.EndsWith(first.Id)||b.Name.EndsWith(second.Id)),"真实历史列表只显示会话，不单列两张导出子卡");
            Check(Controls(history).OfType<Label>().Any(l=>l.Text.Contains("共 4 张 · 已导入 1 张")),"真实历史行显示恢复后的四张卡及完成数量");
            var open=Controls(history).OfType<Button>().Single(b=>b.Name=="knowledge-clipper.history.continue.draft."+parent.Id);
            for(var reopen=0;reopen<2;reopen++)
            {
                open.PerformClick();var form=Chats().Single();await Ready(form);var current=Draft(form);
                Check(current.Id==parent.Id&&current.Chat!.Compositions.Count==4,"关闭后从真实历史按钮恢复原会话与四张卡");
                Check(current.Chat!.Messages.Select(m=>m.Id).SequenceEqual(messageIds)&&current.Chat.Input=="未发送的原输入","重开保留原消息身份与未发送输入");
                await Wait(form,"document.querySelectorAll('nav[aria-label=\"卡片\"] button').length===4");
                await Js(form,"document.querySelectorAll('nav[aria-label=\"卡片\"] button')[3].click()");
                await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]')?.value==="+System.Text.Json.JsonSerializer.Serialize(cold==0&&reopen==0?"错误单卡页新增的第四张":"恢复后继续编辑的第四张"));
                Check(current.Chat.Compositions.Single(c=>c.Id==second.Id).ExportDraftId==second.Id&&store.Find(second.Id)!.ImportState==ImportState.Unknown,"待核对卡在多卡页复用原冻结身份，不新建或重发");
                if(cold==0&&reopen==0){
                    await Edit(form,"卡片正面","恢复后继续编辑的第四张");
                    var deadline=DateTime.UtcNow.AddSeconds(5);while(!current.Chat.Compositions.Any(c=>c.Front=="恢复后继续编辑的第四张")){if(DateTime.UtcNow>deadline)throw new TimeoutException("Recovered card edit was not saved");await Task.Delay(30);}
                    Call(flow,"ContinueHistory",store.Find(first.Id)!);await Task.Delay(100);Check(Chats().Length==1&&Draft(Chats()[0]).Id==parent.Id,"旧单卡历史入口返回已打开的原会话窗口");
                    await Js(form,"document.querySelector('aside.cards').scrollIntoView({block:'start'})");await Task.Delay(100);await Shot(form,"history-grouped-four-cards");
                }
                form.Close();await Task.Delay(100);
            }
        }
        Check(File.ReadAllText(store.Identity(first.Id))==firstBytes&&File.ReadAllText(store.Identity(second.Id))==secondBytes,"分组、恢复和冷重开不改原导出子草稿字节");
    }
}
