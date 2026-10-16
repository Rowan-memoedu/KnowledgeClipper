using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class BranchChecks
{
    internal static async Task Run(string repo, string output, Action<bool,string> check)
    {
        ChatSession Source(string provider="api") => new() { Model="fixture", Binding=new(){Provider=provider,ThreadId=provider=="codex"?"original-thread":null,Stage="completed"},
            Input="原页未发送输入", Messages=[
                new(){Id="u1",Role="user",NativeTurnId="t1",Parts=[new("text","基础值为7")],CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-4)},
                new(){Id="a1",Role="assistant",NativeTurnId="t1",Parts=[new("text","已记住 $x=7$。")],CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-3)},
                new(){Id="u2",Role="user",NativeTurnId="t2",Parts=[new("text","未来口令：不可泄漏")],CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-2)},
                new(){Id="a2",Role="assistant",NativeTurnId="t2",Parts=[new("text","未来回答")],CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-1)}] };
        var root=Source();var shared=new CardComposition{Front="手工正文",ManuallyEdited=true};root.Compositions.Add(shared);
        var branch=ChatWorkspace.Fork(root,"a1");
        check(branch.Session.Messages.Select(m=>m.Id).SequenceEqual(["u1","a1"])&&branch.Session.Binding.ThreadId==null,"API 分叉严格截断后续消息并保留消息身份");
        branch.Session.Input="分叉独立输入";ChatWorkspace.Switch(root,"main");
        check(root.Input=="原页未发送输入"&&root.Branches.Single().Session.Input=="分叉独立输入"&&root.Compositions.Single()==shared,"Tab 输入独立且共用原卡片对象");
        ChatWorkspace.Switch(root,branch.Id);
        var fixture=Path.Combine(output,"branch-fixture",Guid.NewGuid().ToString("N"));var store=new DraftStore(fixture);
        var material=new CaptureMaterial("公开分叉验收",null,"generated_allowed",null,"public",0,DateTimeOffset.UtcNow);
        var draft=new ClipDraft{Original=material,Material=material,Settings=new(),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Chat=root};
        var handler=new ApiHistory();using var http=new HttpClient(handler);
        var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(fixture,"modes"));
        var coordinator=new ChatCoordinator(draft,store,config,new("unused",fixture),http,()=>"fixture",fixture,branch.Session);
        await coordinator.SendAsync([new("fixture","fixture",[],["text"])]);
        check(handler.Body.Contains("基础值")&&!handler.Body.Contains("不可泄漏")&&!handler.Body.Contains("未来回答"),"API 分支请求只含选定前缀与当前输入");
        check(root.Messages.Count==4&&branch.Session.Messages.Count==4&&draft.Conversation.Count==0,"分支回复只写入原分支，不污染原对话或旧线性投影");
        var reply=branch.Session.Messages.Last();
        ChatComposition.Select(root,shared,"a2","back",true);ChatComposition.Select(root,shared,reply.Id,"back",true);
        check(shared.Back.Contains("未来回答")&&shared.Back.Contains("分支回答")&&shared.Front=="手工正文","不同分支消息可追加到同一卡片，手工正文保持");
        ChatComposition.Move(root,shared,"back",1,-1);ChatComposition.Undo(shared,root);
        check(shared.BackSources.SequenceEqual(["a2",reply.Id]),"跨分支来源排序与撤销继续使用原卡片历史");
        store.Save(draft);var reopened=store.Find(draft.Id)!;
        check(reopened.Chat!.ActiveConversationId==branch.Id&&ChatWorkspace.Active(reopened.Chat).Messages.Last().Text=="分支回答"&&reopened.Chat.Compositions.Single().Id==shared.Id,"同一草稿重开恢复所有 Tab、当前分支与共享卡片");
        ChatWorkspace.Switch(root,"main");var question=ChatWorkspace.Fork(root,"u2");
        check(question.Session.Messages.Count==2&&question.Session.Input=="未来口令：不可泄漏","从提问分叉将原问题放入输入框，排除旧答案，不自动发送");
        var blank=ChatWorkspace.New(root);
        check(blank.Session.Messages.Count==0&&blank.Session.Input==""&&root.Compositions.Single()==shared,"加号新建空白 Tab，保留共享卡片且不新建窗口草稿");
        var lifecycle=Source("codex");
        check(ChatWorkspace.OpenIds(lifecycle).SequenceEqual(["main"])&&lifecycle.ConversationOrder==null,"旧稿读取不迁移或改写 Tab 顺序");
        var first=ChatWorkspace.Fork(lifecycle,"a1");first.Session.Binding.ThreadId="retained-fork";
        first.Session.Messages.Add(new(){Id="only-fork",Role="assistant",Parts=[new("text","关闭后仍属于卡片的材料")]});
        var second=ChatWorkspace.New(lifecycle);var originalCards=new CardComposition();lifecycle.Compositions.Add(originalCards);
        ChatComposition.Select(lifecycle,originalCards,"only-fork","back",true);
        ChatWorkspace.Move(lifecycle,"main",second.Id,true);
        check(ChatWorkspace.OpenIds(lifecycle).SequenceEqual([first.Id,second.Id,"main"])&&lifecycle.ActiveConversationId==second.Id,"拖动可将原对话移到末尾，选中会话不变");
        ChatWorkspace.Move(lifecycle,"main",first.Id,false);
        check(ChatWorkspace.OpenIds(lifecycle).SequenceEqual(["main",first.Id,second.Id]),"拖动支持移动到目标之前");
        ChatWorkspace.Close(lifecycle,first.Id);
        check(lifecycle.ActiveConversationId==second.Id&&!ChatWorkspace.OpenIds(lifecycle).Contains(first.Id)&&first.Session.Binding.ThreadId=="retained-fork","关闭非当前 Tab 不切页、不删除原生身份");
        check(ChatWorkspace.Messages(lifecycle).Any(m=>m.Id=="only-fork")&&originalCards.Back.Contains("关闭后仍属于卡片"),"关闭 Tab 保留共享卡片与原消息来源");
        try{ChatWorkspace.Switch(lifecycle,first.Id);throw new Exception("Closed tab switched");}catch(InvalidOperationException){}
        check(lifecycle.ActiveConversationId==second.Id,"迟到切换不能重新打开已关闭 Tab");
        var beforeInvalid=ChatWorkspace.OpenIds(lifecycle).ToArray();
        try{ChatWorkspace.Move(lifecycle,first.Id,"main",true);throw new Exception("Closed tab moved");}catch(InvalidOperationException){}
        check(ChatWorkspace.OpenIds(lifecycle).SequenceEqual(beforeInvalid),"失效拖拽命令不改变现有排序");
        ChatWorkspace.Close(lifecycle,second.Id);
        check(lifecycle.ActiveConversationId=="main","关闭当前最右 Tab 自动切到左侧相邻项");
        check(ChatWorkspace.UndoClose(lifecycle)&&lifecycle.ActiveConversationId==second.Id&&ChatWorkspace.UndoClose(lifecycle)&&ChatWorkspace.OpenIds(lifecycle).SequenceEqual(["main",first.Id,second.Id]),"连续撤回按关闭逆序恢复原位置");
        check(ChatWorkspace.Active(lifecycle)==first.Session&&first.Session.Binding.ThreadId=="retained-fork"&&first.Session.Messages.Last().Id=="only-fork","撤回恢复同一对话对象、原消息与原生身份");
        ChatWorkspace.Close(lifecycle,"main");ChatWorkspace.Close(lifecycle,first.Id);ChatWorkspace.Close(lifecycle,second.Id);
        var replacement=ChatWorkspace.Active(lifecycle);
        check(ChatWorkspace.OpenIds(lifecycle).Count==1&&replacement.Messages.Count==0&&lifecycle.Compositions.Single()==originalCards,"关闭最后一张 Tab 新建空白页并保留共享卡片");
        lifecycle=JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(lifecycle,WorkflowJson.Options),WorkflowJson.Options)!;
        check(lifecycle.ClosedTabs.Count==3&&ChatWorkspace.OpenIds(lifecycle).Count==1&&ChatWorkspace.UndoClose(lifecycle)&&lifecycle.ActiveConversationId==second.Id,"重开草稿仍能撤回关闭，保留关闭历史和显示顺序");
        check(ChatWorkspace.Session(lifecycle,first.Id).Binding.ThreadId=="retained-fork"&&ChatWorkspace.Messages(lifecycle).Any(m=>m.Id=="only-fork"),"重开后关闭对话及其卡片来源仍完整");
        var native=Source("codex");var nativeBranch=ChatWorkspace.Fork(native,"a1");
        check(nativeBranch.Session.Fork is {SourceThreadId:"original-thread",LastTurnId:"t1",Stage:"prepared"},"Codex 分叉冻结原 Thread 和指定回合边界");
        var multi=Source("codex");multi.Messages.Insert(2,new(){Role="assistant",NativeTurnId="t1",Parts=[new("text","同轮末项")]});
        check(!ChatWorkspace.CanFork(multi,multi.Messages[1])&&ChatWorkspace.CanFork(multi,multi.Messages[2]),"同一原生回合多项输出只在真实边界分叉，不暗含后续内容");
        var stopped=Source("codex");stopped.Messages[1].Status="interrupted";
        check(ChatWorkspace.Fork(stopped,"a1").Session.Fork?.LastTurnId=="t1","原生停止后可按已确认回合边界分叉，保留部分内容");
        check(!ChatWorkspace.CanFork(nativeBranch.Session,nativeBranch.Session.Messages.Last()),"尚未核对完成的原生分叉不能被再次用作分叉来源");
        foreach(var mode in new[]{"lost-fork","lost-readback"})
        {
            var faultRoot=Path.Combine(fixture,mode);var owner=Source("codex");var child=ChatWorkspace.Fork(owner,"a1");child.Session.Model=mode;
            var faultDraft=new ClipDraft{Original=material,Material=material,Settings=new(),Configuration=draft.Configuration,Chat=owner};var faultStore=new DraftStore(faultRoot);
            var process=new CodexChatProcess(Path.Combine(repo,"tests","KnowledgeClipper.Chat.Tests","fixtures","fork_native.py"),faultRoot);
            var fault=new ChatCoordinator(faultDraft,faultStore,config,process,http,()=>"fixture",faultRoot,child.Session);
            try{await fault.ForkAsync();throw new Exception("Fault was not raised");}catch(IOException){}
            var restored=faultStore.Find(faultDraft.Id)!;var preserved=restored.Chat!.Branches.Single().Session;
            check(preserved.Fork!.Stage=="unknown"&&preserved.Binding.NeedsReconciliation,"分叉故障窗口保存未知状态："+mode);
            var count=File.ReadAllLines(Path.Combine(faultRoot,"chat-workspace","invocations.jsonl")).Length;
            if(mode=="lost-fork")
            {
                try{await fault.RecoverAsync();throw new Exception("Unknown fork was repeated");}catch(InvalidOperationException){}
                check(child.Session.Binding.ThreadId==null&&File.ReadAllLines(Path.Combine(faultRoot,"chat-workspace","invocations.jsonl")).Length==count,"缺少新 Thread ID 的未知分叉不重新创建");
            }
            else
            {
                await fault.RecoverAsync();
                check(child.Session.Binding.ThreadId=="fork-thread"&&child.Session.Fork!.Stage=="verified"&&File.ReadAllText(Path.Combine(faultRoot,"chat-workspace","invocations.jsonl")).Contains("verifyFork"),"已保存新身份的分叉只做精确回读恢复");
            }
        }
    }
    private sealed class ApiHistory:HttpMessageHandler
    {
        public string Body="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Body=System.Text.RegularExpressions.Regex.Unescape(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"分支回答\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
    }
}
