using KnowledgeClipper.Workflow;

internal static class LiveExtras
{
    internal static async Task Run(string repo,string output)
    {
        var root=Path.Combine(output,"native-extra");Directory.CreateDirectory(root);var store=new DraftStore(root);
        var defaults=Path.Combine(repo,"modes","chat");using var http=new HttpClient();
        var reports=new List<object>();
        async Task RunCase(string name,string question,ImageAsset[] images,Action<ClipDraft> check,bool stop=false)
        {
            var index=Path.Combine(root,name+".id");ClipDraft draft;
            if(File.Exists(index))draft=store.Find(File.ReadAllText(index))!;
            else {var material=new CaptureMaterial("公开原生验收："+name,null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
                draft=new(){Original=material,Material=material,Settings=new(),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),ModeId="question",Input=new(Intent:"question"),
                    Chat=new(){Binding=new(){Provider="codex"},Model="gpt-6.1-sol",Effort="low",Input=question,Attachments=images.ToList()}};
                store.Save(draft);File.WriteAllText(index,draft.Id);}
            var coordinator=new ChatCoordinator(draft,store,new(defaults,Path.Combine(root,name+"-rules")),new(Path.Combine(repo,"helpers","codex_chat.py"),root),http,()=>throw new Exception("No API key"),root);
            var models=await coordinator.ModelsAsync("codex");
            if(draft.Chat!.Binding.NeedsReconciliation)await coordinator.RecoverAsync();
            if(draft.Chat.Prompts.Count==0){
                Console.WriteLine("Live begin "+name);var pending=coordinator.SendAsync(models);
                if(stop){while(draft.Chat.Binding.TurnId==null&&!pending.IsCompleted)await Task.Delay(50);await coordinator.StopAsync();}
                await pending;
            }
            check(draft);
            // Simulate a process closing after a terminal receipt was lost. Readback must retain identities and not send again.
            var threadId=draft.Chat.Binding.ThreadId;var turnId=draft.Chat.Binding.TurnId;var promptCount=draft.Chat.Prompts.Count;
            draft.Chat.Binding.Stage="unknown";store.Save(draft);await coordinator.RecoverAsync();
            if(threadId!=draft.Chat.Binding.ThreadId||turnId!=draft.Chat.Binding.TurnId||draft.Chat.Prompts.Count!=promptCount)throw new Exception("Recovery changed identity");
            reports.Add(new{name,threadId,turnId,status=draft.Chat.Binding.NativeStatus,chars=draft.Chat.Messages.Sum(m=>m.Text.Length),images=draft.Chat.Assets.Select(a=>a.Sha256).ToArray()});
            WorkflowJson.Save(Path.Combine(root,"acceptance.json"),reports);Console.WriteLine("PASS live "+name+" and exact readback");
        }
        var fixture=Directory.GetFiles(Path.Combine(output,"ui","fixture"),"public-0.png",SearchOption.AllDirectories).Order().First();
        var asset=new ImageAsset(fixture,ImageAsset.Hash(File.ReadAllBytes(fixture)),"question",1);
        await RunCase("image-only","",[asset],draft=>{
            if(draft.Chat!.Prompts[0].Input!=""||draft.Chat.Messages[0].Parts.Any(p=>p.Kind=="text")||!draft.Chat.Messages.Any(m=>m.Role=="assistant"&&m.Text.Length>0))throw new Exception("Image-only input failed");
        });
        await RunCase("image-output","请调用图像生成工具生成一幅简单图片：白底正中央一个蓝色实心圆，无文字、无其他物体。只返回真实生成图片，不写最终说明。",[],draft=>{
            if(!draft.Chat!.Messages.Where(m=>m.Role=="assistant").SelectMany(m=>m.Parts).Any(p=>p.Kind=="image"))throw new Exception("No native imageGeneration result");
            foreach(var media in draft.Chat.Assets)media.ReadVerified();
        });
        await RunCase("interrupt","公开停止验收，材料代号蓝松。请完整给出 100 个二次方程例子，每个包含条件、逐步推导与验证，不省略或使用省略号。",[],draft=>{
            if(draft.Chat!.Binding.NativeStatus!="interrupted"||!draft.Chat.Binding.LocalCancelRequested)throw new Exception("Native interrupt was not confirmed");
        },stop:true);
        await RunCase("long-math","生成公开数学教材：依次完整列出 80 个不同的配方求解二次方程例子，每例至少 150 个汉字，逐步写出条件、计算、根的验证。每例至少 3 个 LaTeX 公式，包含矩阵、cases、aligned 的解释示例。总长度至少 15000 字符。不要省略中间例子，不要用代码生成，不调用工具。",[],draft=>{
            var answer=string.Concat(draft.Chat!.Messages.Where(m=>m.Role=="assistant").Select(m=>m.Text));
            if(draft.Chat.Binding.NativeStatus!="completed"||answer.Length<12000||!answer.Contains('\\'))throw new Exception("Long math response incomplete or too short");
        });
    }
}
