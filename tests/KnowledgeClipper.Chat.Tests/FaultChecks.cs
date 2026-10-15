using KnowledgeClipper.Workflow;

internal static class FaultChecks
{
    internal static async Task Run(string repo,string output)
    {
        var results=new List<object>();
        foreach(var fault in new[]{"create-response-lost","injection-response-lost","turn-response-lost","stream-disconnected"}){
            var root=Path.Combine(output,"faults",fault,Guid.NewGuid().ToString("N"));var store=new DraftStore(root);
            var material=new CaptureMaterial("fixture",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
            var draft=new ClipDraft {Original=material,Material=material,Settings=new(),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Chat=new(){Binding=new(){Provider="codex"},Model="fixture",Input=fault}};
            using var http=new HttpClient();var coordinator=new ChatCoordinator(draft,store,new(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes")),
                new(Path.Combine(repo,"tests","KnowledgeClipper.Chat.Tests","fixtures","fault_native.py"),root),http,()=>throw new Exception("No API key"),root);
            try { await coordinator.SendAsync([new("fixture","fixture",[],["text"])]);throw new Exception("Fixture did not fail"); } catch(IOException) { }
            var saved=store.Find(draft.Id)!;
            if(saved.Chat!.Binding.Stage!="unknown"||saved.Chat.Input!=fault||saved.Chat.Prompts.Count!=1)throw new Exception("Fault state was lost: "+fault);
            if(fault!="create-response-lost"&&saved.Chat.Binding.ThreadId!="fixture-thread")throw new Exception("Thread ID was not durably retained");
            if(fault=="stream-disconnected"&&(saved.Chat.Binding.TurnId!="fixture-turn"||!saved.Chat.Messages.Last().Text.Contains("不得丢失")))throw new Exception("Partial output lost");
            try{await coordinator.SendAsync([new("fixture","fixture",[],["text"])]);throw new Exception("Unknown replay allowed");}catch(InvalidOperationException){}
            if(File.ReadAllLines(Path.Combine(root,"chat-workspace","invocations.txt")).Length!=1)throw new Exception("Unknown started replacement process");
            results.Add(new{fault,stage=saved.Chat.Binding.Stage,saved.Chat.Binding.ThreadId,saved.Chat.Binding.TurnId,requests=1});Console.WriteLine("PASS fault "+fault);
        }
        WorkflowJson.Save(Path.Combine(output,"fault-results.json"),results);
    }
}
