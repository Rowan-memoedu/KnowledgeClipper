using KnowledgeClipper.Workflow;

internal static class LiveCompaction
{
    internal static async Task Run(string repo,string output)
    {
        var root=Path.Combine(output,"compaction-live");Directory.CreateDirectory(root);var store=new DraftStore(root);var idPath=Path.Combine(root,"draft-id.txt");
        ClipDraft draft;
        if(File.Exists(idPath))draft=store.Find(File.ReadAllText(idPath))!;
        else {
            var material=new CaptureMaterial("公开自动压缩验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
            draft=new(){Original=material,Material=material,Settings=new(),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),
                Chat=new(){Binding=new(){Provider="codex"},Model="gpt-6-luna",Effort="low"}};
            store.Save(draft);File.WriteAllText(idPath,draft.Id);
        }
        var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes"));using var http=new HttpClient();
        var coordinator=new ChatCoordinator(draft,store,config,new(Path.Combine(repo,"tests","KnowledgeClipper.Chat.Tests","fixtures","compact_native.py"),root),http,()=>throw new Exception("No API key"),root);
        var catalog=await coordinator.ModelsAsync("codex");var chat=draft.Chat!;
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        if(chat.Binding.NeedsReconciliation)await coordinator.RecoverAsync();
        if(chat.Prompts.Count==0){
            chat.Input="公开材料：木板代号红枫，长 7 厘米、宽 4 厘米。以下为独立公开算术练习。最后只回答「已记住红枫」。\n"+
                string.Join("\n",Enumerable.Range(1,600).Select(i=>$"练习 {i}：正方形边长 {i}，面积为 {i*i}；矩形长 {i+1}、宽 {i}，面积为 {i*(i+1)}。这不是红枫木板的尺寸。"));
            await coordinator.SendAsync(catalog,ct:deadline.Token);Console.WriteLine("Live automatic compaction first turn "+chat.Binding.Stage);
        }
        if(chat.Prompts.Count==1&&chat.Binding.Stage=="completed") {
            var rule=config.Editable("types/fast-answer.md");File.AppendAllText(rule,"\n本次类型的输出格式：回复以「压缩后规则有效」开头，然后给出答案。即使要求简短也保留此格式。\n");
            chat.Input="最初的红枫木板长增加 2 厘米。复述原宽并计算新面积。只回答两行。";await coordinator.SendAsync(catalog,ct:deadline.Token);
            Console.WriteLine("Live automatic compaction second turn "+chat.Binding.Stage);
        }
        var events=Path.Combine(root,"chat-workspace","compactions.jsonl");
        if(!File.Exists(events)||chat.Binding.Stage!="completed"||!chat.Prompts.Any(p=>p.CompactedItemIds.Count>0&&p.RulesReinjectedAfterTurn))
            throw new Exception("Actual automatic compaction/rule continuation was not verified");
        // Reopen with normal production configuration. Never keep the artificially low
        // threshold for the next question or replay any Unknown/failed question.
        var normal=new ChatCoordinator(draft,store,config,new(Path.Combine(repo,"helpers","codex_chat.py"),root),http,()=>throw new Exception("No API key"),root);
        if(chat.Prompts.Count==2) {
            chat.Model="gpt-6.1-sol";chat.Input="只复述红枫木板的原始宽度。";await normal.SendAsync(catalog,ct:deadline.Token);
        }
        var final=chat.Messages.Last(m=>m.Role=="assistant").Text;
        if(chat.Prompts.Count!=3||chat.Binding.Stage!="completed"||!final.Contains("压缩后规则有效")||!final.Contains("4"))
            throw new Exception("Same-thread rule continuity after compaction was not verified");
        WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{chat.Binding.ThreadId,chat.Binding.TurnId,chat.Binding.Stage,turns=chat.Prompts.Count,
            completedCompactions=chat.Prompts.Sum(p=>p.CompactedItemIds.Count),processOnlyThreshold=8000,
            reinjectionTiming="after native turn/completed, before local terminal receipt; next send also refreshes rules",
            limitation="The in-flight answer may have used compacted rules; safe reinjection does not regenerate it."});
        Console.WriteLine("PASS actual automatic compaction, rule reinjection and same-thread continuation");
    }

    internal static async Task Recover(string repo,string output)
    {
        var root=Path.Combine(output,"compaction-live");var store=new DraftStore(root);
        var draft=store.Find(File.ReadAllText(Path.Combine(root,"draft-id.txt")))!;using var http=new HttpClient();
        var coordinator=new ChatCoordinator(draft,store,new(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes")),
            new(Path.Combine(repo,"helpers","codex_chat.py"),root),http,()=>throw new Exception("No API key"),root);
        var before=(draft.Chat!.Binding.ThreadId,draft.Chat.Binding.TurnId,draft.Chat.Prompts.Count);
        await coordinator.RecoverAsync();
        if(before!=(draft.Chat.Binding.ThreadId,draft.Chat.Binding.TurnId,draft.Chat.Prompts.Count))throw new Exception("Recovery changed native identity or resent a turn");
        WorkflowJson.Save(Path.Combine(root,"recovery.json"),new{draft.Chat.Binding,turns=draft.Chat.Prompts.Count,readOnlyRecovery=true});
        Console.WriteLine("Recovered exact original compaction turn: "+draft.Chat.Binding.Stage);
    }
}
