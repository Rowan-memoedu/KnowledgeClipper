using KnowledgeClipper.Workflow;

internal static class BranchLive
{
    internal static async Task Run(string repo,string output,bool api=false)
    {
        var root=Path.Combine(output,api?"api-branch":"native-branch");Directory.CreateDirectory(root);
        var store=new DraftStore(root);var idFile=Path.Combine(root,"draft-id.txt");
        ClipDraft draft;
        if(File.Exists(idFile))draft=store.Find(File.ReadAllText(idFile))!;
        else
        {
            var material=new CaptureMaterial("公开原生分叉验收",null,"generated_allowed",null,"public",0,DateTimeOffset.UtcNow);
            draft=new(){Original=material,Material=material,Settings=api?WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json"):new(),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),ModeId="question",Input=new(Intent:"question"),
                Chat=new(){Binding=new(){Provider=api?"api":"codex"},Model=api?"deepseek-flash":"gpt-6.1-sol",Effort=api?null:"low"}};
            store.Save(draft);File.WriteAllText(idFile,draft.Id);
        }
        using var http=new HttpClient();var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes"));
        ChatCoordinator Coordinate(ChatSession session)=>new(draft,store,config,new(Path.Combine(repo,"helpers","codex_chat.py"),root),http,()=>api?ContentCredentials.Resolve():throw new Exception("No API credential"),root,session);
        var original=Coordinate(draft.Chat!);var models=await original.ModelsAsync(api?"api":"codex");
        async Task Send(ChatCoordinator coordinator,string input)
        {
            if(coordinator.Session.Binding.NeedsReconciliation)await coordinator.RecoverAsync();
            coordinator.Session.Input=input;store.Save(draft);await coordinator.SendAsync(models);
            if(coordinator.Session.Binding.Stage!="completed")throw new Exception("Live turn incomplete");
        }
        if(draft.Chat!.Prompts.Count==0)await Send(original,"公开验收：材料代号杉木，基础值为7。记住这些信息，只回复‘已记住’。");
        if(draft.Chat.Prompts.Count==1)await Send(original,"后续分支私有口令为蓝松。记住它，只回复‘后续已记住’。");
        var sourceThread=draft.Chat.Binding.ThreadId;
        var anchor=draft.Chat.Messages.First(m=>m.Role=="assistant"&&m.Status=="completed");
        ChatBranch branch;
        if(draft.Chat.Branches.Count==0){ChatWorkspace.Switch(draft.Chat,"main");branch=ChatWorkspace.Fork(draft.Chat,anchor.Id);store.Save(draft);}
        else branch=draft.Chat.Branches.Single();
        var child=Coordinate(branch.Session);
        if(branch.Session.Fork is {Stage:not "verified"})await child.ForkAsync();
        if(branch.Session.Prompts.Count==1)await Send(child,"沿用此前材料，复述材料代号，将基础值加2。你是否见过一个后续私有口令？只答代号、新数值，以及‘未见口令’或实际口令，三项即可。");
        var answer=branch.Session.Messages.Last(m=>m.Role=="assistant").Text;
        if(!answer.Contains("杉木")||!answer.Contains('9')||!answer.Contains("未见口令")||answer.Contains("蓝松"))throw new Exception("Branch inherited future context: "+answer);
        if(draft.Chat.Binding.ThreadId!=sourceThread||draft.Chat.Messages.Any(m=>m.Text==answer)||draft.Chat.Prompts.Count!=2)throw new Exception("Original conversation changed");
        var shared=draft.Chat.Compositions.SingleOrDefault()??new CardComposition{TypeId="fast-answer"};if(!draft.Chat.Compositions.Contains(shared))draft.Chat.Compositions.Add(shared);
        ChatComposition.Select(draft.Chat,shared,anchor.Id,"front",true);
        ChatComposition.Select(draft.Chat,shared,branch.Session.Messages.Last(m=>m.Role=="assistant").Id,"back",true);store.Save(draft);
        var fresh=store.Find(draft.Id)!;
        if(fresh.Chat!.Branches.Single().Session.Binding.ThreadId!=branch.Session.Binding.ThreadId||fresh.Chat.Compositions.Last().Back!=shared.Back)throw new Exception("Branch/card reopen mismatch");
        WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{passed=true,sourceThread,branchThread=branch.Session.Binding.ThreadId,
            provider=api?"api":"codex",boundary=branch.Session.Fork?.LastTurnId,stage=branch.Session.Fork?.Stage,answer,originalTurns=draft.Chat.Prompts.Count,
            branchTurns=branch.Session.Prompts.Count,sharedCard=shared.Id,nativeForkNoGeneration=true,excludesFutureTurn=true});
        Console.WriteLine("PASS real "+(api?"API branch":"Codex native fork")+", exact prefix, independent context, same source identity and shared card persistence");
    }
}
