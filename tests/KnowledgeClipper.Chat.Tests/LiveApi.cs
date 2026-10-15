using KnowledgeClipper.Workflow;

internal static class LiveApi
{
    internal static async Task Run(string repo, string output)
    {
        var root=Path.Combine(output,"api-live");Directory.CreateDirectory(root);var store=new DraftStore(root);
        var idPath=Path.Combine(root,"draft-id.txt");ClipDraft draft;
        if(File.Exists(idPath))draft=store.Find(File.ReadAllText(idPath))!;
        else {
            var material=new CaptureMaterial("公开 API 验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
            var settings=WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
            draft=new(){Original=material,Material=material,Settings=settings,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),
                Chat=new(){Model=settings.Model,TypeId="chunk-solution",IncludeExamples=true,Input="矩形长 7 厘米，宽 4 厘米。求面积，并说明乘法中的两个量。保留公式，最多三行。"}};
            store.Save(draft);File.WriteAllText(idPath,draft.Id);
        }
        using var http=new HttpClient();var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes"));
        var coordinator=new ChatCoordinator(draft,store,config,new(Path.Combine(repo,"helpers","codex_chat.py"),root),http,()=>ContentCredentials.Resolve(),root);
        var catalog=await coordinator.ModelsAsync("api");var chat=draft.Chat!;
        WorkflowJson.Save(Path.Combine(root,"models.json"),catalog);
        // Explicit model selection for this public fixture; never change the user's settings.
        if(chat.Prompts.Count==0){chat.Model=catalog.Single(m=>m.Id=="deepseek-flash").Id;store.Save(draft);}
        if(chat.Binding.NeedsReconciliation)throw new Exception("Previous API result is unknown; no replay.");
        if(chat.Prompts.Count==0)await coordinator.SendAsync(catalog);
        if(chat.Binding.Stage!="completed"||!chat.Messages.Any(m=>m.Role=="assistant"&&m.Text.Contains("28")))throw new Exception("Live API math answer not accepted");
        if(chat.Compositions.Count==0){
            var card=new CardComposition{TypeId="chunk-solution"};chat.Compositions.Add(card);
            ChatComposition.Select(chat,card,chat.Messages.First(m=>m.Role=="user").Id,"front",true);
            ChatComposition.Select(chat,card,chat.Messages.First(m=>m.Role=="assistant").Id,"back",true);store.Save(draft);
        }
        foreach(var action in new[]{"formalize","integrate"}) {
            var proposal=chat.Proposals.SingleOrDefault(p=>p.Action==action);
            if(proposal==null){
                proposal=ChatComposition.Begin(chat,chat.Compositions[0],action,"一个知识目标保留一张卡。",config.Load().Types.Select(t=>t.Id).ToArray());store.Save(draft);
                var first=chat.Messages.Count;
                await coordinator.SendAsync(catalog,action,proposal.InputSnapshot,[]);
                ChatComposition.Parse(proposal,string.Join("\n",chat.Messages.Skip(first).Where(m=>m.Role=="assistant").Select(m=>m.Text)),chat,config.Load().Types.Select(t=>t.Id).ToArray());store.Save(draft);
            }
            if(proposal.Status=="preview") {
                if(proposal.Cards.Count!=1||!proposal.Cards[0].Front.Contains('7')||!proposal.Cards[0].Front.Contains('4')||!proposal.Cards[0].Back.Contains("28"))
                    throw new Exception("Candidate lost explicit problem conditions or answer: "+action);
                ChatComposition.Adopt(chat,proposal);store.Save(draft);
            }
            if(proposal.Status!="adopted")throw new Exception("Candidate not adopted: "+action+" "+proposal.Status);
            Console.WriteLine("PASS live API "+action+" candidate and explicit adoption");
        }
        WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{model=chat.Model,chat.Binding.Stage,turns=chat.Prompts.Count,
            fewShotFiles=chat.Prompts[0].Files.Where(f=>f.RelativePath.StartsWith("few-shots/")).Select(f=>f.RelativePath),
            proposals=chat.Proposals.Select(p=>new{p.Action,p.Status,cardCount=p.Cards.Count}),cards=chat.Compositions.Count,noExport=true});
    }
}
