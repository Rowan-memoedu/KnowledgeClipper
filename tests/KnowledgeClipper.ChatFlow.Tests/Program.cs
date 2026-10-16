using System.Collections;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Uses the shipped desktop assembly, its real entry forms, WebView buttons, SDK
// and providers. Runs only on an isolated desktop with public test material.
internal static partial class Program
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly List<string> checks=[], errors=[];
    static readonly HashSet<Form> observed=[];
    static string output="", data="";
    static DraftStore store=null!;
    static object flow=null!;
    static Assembly desktop=null!;
    static ClipDraft Draft(Form form)=>(ClipDraft)form.GetType().GetProperty("Draft",Flags)!.GetValue(form)!;
    static WebView2 Web(Form form)=>(WebView2)form.GetType().GetField("web",Flags)!.GetValue(form)!;
    static object? Call(object target,string method,params object?[] args)=>target.GetType().GetMethod(method,Flags)!.Invoke(target,args);
    static Form[] Chats()=>((IEnumerable)flow.GetType().GetField("chatForms",Flags)!.GetValue(flow)!).Cast<Form>().Where(f=>!f.IsDisposed).ToArray();
    static IEnumerable<Control> Controls(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(Controls(x)));
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
    [STAThread] static int Main(string[] args)
    {
        if(Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST")!="1")throw new Exception("Use isolated desktop runner");
        output=args[0];data=Path.Combine(output,"fixture");Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var context=new ApplicationContext();using var start=new System.Windows.Forms.Timer{Interval=10};var result=0;
        start.Tick+=async(_,_)=>{start.Stop();try{if(args[2]=="--redo-feedback")await RedoFeedbackUi(args[1]);else if(args[2]=="--history-routes")await HistoryRoutes(args[1]);else if(args[2].StartsWith("--all-formalize"))await AllFormalizeUi(args[1],args[2].EndsWith("-live"));else if(args[2].StartsWith("--symmetric"))await SymmetricUi(args[1],args[2]=="--symmetric-live");else if(args[2].StartsWith("--annotation-media"))await AnnotationMediaUi(args[1],args[2]=="--annotation-media-live");else if(args[2]=="--math-layout")await MathLayoutUi(args[1]);else if(args[2].StartsWith("--back-images"))await BackImagesUi(args[1],args[2]=="--back-images-live");else if(args[2].StartsWith("--auxiliary"))await AuxiliaryUi(args[1],args[2]=="--auxiliary-live");else if(args[2]=="--composition")await CompositionUi(args[1]);else if(args[2].StartsWith("--entries"))await EntryRoutes(args[1],args[2]=="--entries-live");else if(args[2]=="--render-saved")await RenderSaved(args[1]);else if(args[2]=="--stop-recover")await StopRecover();else await Run(args[1],args[2]);}catch(Exception ex){result=1;Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
            finally{foreach(var form in Application.OpenForms.Cast<Form>().ToArray())form.Dispose();WorkflowJson.Save(Path.Combine(output,"results.json"),new{passed=result==0,checks,errors,data,desktop=desktop?.Location});context.ExitThread();}};
        start.Start();Application.Run(context);return result;
    }
    static async Task<string> Js(Form form,string script)=>await Web(form).CoreWebView2.ExecuteScriptAsync(script);
    static async Task Wait(Form form,string condition,int seconds=60)
    {
        var timer=Stopwatch.StartNew();
        while(timer.Elapsed.TotalSeconds<seconds){
            if(Web(form).CoreWebView2!=null){
                if(observed.Add(form)){
                    Web(form).CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived+=(_,e)=>errors.Add(e.ParameterObjectAsJson);
                    await Web(form).CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable","{}");
                }
                if(errors.Count>0)throw new Exception("JavaScript exception: "+errors[^1]);
                if(await Js(form,condition)=="true")return;
            }
            await Task.Delay(50);
        }
        if(Web(form).CoreWebView2!=null)File.WriteAllText(Path.Combine(output,"failed-dom.json"),await Js(form,"document.body.innerHTML"));
        throw new TimeoutException(condition);
    }
    static async Task Ready(Form form)=>await Wait(form,"document.querySelectorAll('.selectors select')[1]?.options.length>1&&document.querySelector('.composer')!==null");
    static async Task Button(Form form,string text,string scope="document")
    {
        var find=$"Array.from({scope}.querySelectorAll('button')).find(b=>b.textContent==={JsonSerializer.Serialize(text)})";
        await Wait(form,$"!!({find})&&!({find}).disabled");await Js(form,$"({find}).click()");
    }
    static async Task Input(Form form,string text)
    {
        await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label=\"问题\"]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,"+JsonSerializer.Serialize(text)+");t.dispatchEvent(new Event('input',{bubbles:true}));})()");
        var timer=Stopwatch.StartNew();while(Draft(form).Chat!.Input!=text){if(timer.Elapsed.TotalSeconds>5)throw new Exception("Input bridge did not persist");await Task.Delay(30);}
    }
    static async Task Turn(Form form,int previous)
    {
        var timer=Stopwatch.StartNew();var lastProgress=-1;
        while(timer.Elapsed.TotalSeconds<240){
            var chat=Draft(form).Chat!;
            await Wait(form,"document.querySelector('.composer')!==null&&!document.querySelector('[role=alert]')",5);
            if(chat.Messages.Count!=lastProgress){lastProgress=chat.Messages.Count;Console.WriteLine("PROGRESS native turn messages="+lastProgress+" stage="+chat.Binding.Stage);}
            if(chat.Prompts.Count>previous&&chat.Binding.Stage is "failed" or "unknown" or "interrupted")throw new Exception("Turn failed: "+chat.Binding.Error);
            var coordinator=(ChatCoordinator)form.GetType().GetProperty("Coordinator",Flags)!.GetValue(form)!;
            if(chat.Prompts.Count>previous&&chat.Binding.Stage=="completed"&&!coordinator.Busy){await Wait(form,"document.querySelectorAll('.message').length==="+chat.Messages.Count);return;}
            await Task.Delay(50);
        }
        throw new TimeoutException("Real model turn did not finish; original draft identity retained");
    }
    static async Task Shot(Form form,string name){using var file=File.Create(Path.Combine(output,name+".png"));await Web(form).CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);}
    static async Task Select(Form form,string messageId,string side)
    {
        var index=Draft(form).Chat!.Messages.FindIndex(m=>m.Id==messageId);
        await Js(form,$"document.querySelectorAll('.message')[{index}].querySelectorAll('.message-actions button')[{(side=="front"?0:1)}].click()");
        var timer=Stopwatch.StartNew();while(!(side=="front"?Draft(form).Chat!.Compositions[0].FrontSources:Draft(form).Chat!.Compositions[0].BackSources).Contains(messageId)){
            if(timer.Elapsed.TotalSeconds>5)throw new Exception("Selection did not persist");await Task.Delay(30);
        }
    }
    static async Task Edit(Form form,string label,string text)
    {
        await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label="+JsonSerializer.Serialize(label)+"]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,"+JsonSerializer.Serialize(text)+");t.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await Task.Delay(150);
    }
    static async Task RenderSaved(string path)
    {
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var saved=WorkflowJson.Read<ClipDraft>(path);var turns=saved.Chat!.Prompts.Count;
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        // A local clone renders the exact persisted messages; no send or recovery
        // command is issued, and the production DraftStore is never opened for writes.
        var constructor=desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single(c=>c.GetParameters().Length==6);
        using var form=(Form)constructor.Invoke([saved,store,data,AppContext.BaseDirectory,(Func<string>)(()=>throw new Exception("No API request allowed")),(Action)(()=>{})]);
        form.Show();await Ready(form);
        await Wait(form,"document.querySelectorAll('.message').length==="+saved.Chat.Messages.Count+"&&document.querySelector('.message.assistant .message-body')?.textContent.trim().length>0");
        if(saved.Chat.Messages.Any(m=>m.Parts.Any(p=>p.Kind=="image")))await Wait(form,"document.querySelector('.message.user img')?.naturalWidth>0");
        await Shot(form,"recovered-view");form.Close();
        Check(saved.Chat.Prompts.Count==turns,"报告白屏的原草稿在修复版完整显示，没有重发问题");
        Check(hash==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),"恢复显示测试没有修改用户原始草稿字节");
        Check(errors.Count==0&&!File.Exists(Path.Combine(data,"logs","chat-ui.jsonl")),"原草稿重开界面无 JavaScript 异常");
    }
    static async Task StopRecover()
    {
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        var material=new CaptureMaterial("公开停止测试",null,"generated_allowed",null,"公开测试",0,DateTimeOffset.UtcNow);
        var draft=new ClipDraft{Original=material,Material=material,Configuration=ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory,"modes")),Settings=new(),ModeId="question",Input=new(Intent:"question"),Route=new("question","question",true,false),
            Chat=new(){Binding=new(){Provider="codex"},Model="gpt-6.1-sol",Effort="low",Input="依次写300条不同的课堂学习检查要点，每条至少30字。编号逐条完整写出，不要省略、合并或概述。"}};
        var constructor=desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single(c=>c.GetParameters().Length==6);
        Form Open(ClipDraft d)=>(Form)constructor.Invoke([d,store,data,AppContext.BaseDirectory,(Func<string>)(()=>throw new Exception("No API key needed")),(Action)(()=>{})]);
        using(var form=Open(draft)) {
            form.Show();await Ready(form);await Button(form,"发送");
            var timer=Stopwatch.StartNew();while(!draft.Chat.Messages.Any(m=>m.Role=="assistant"&&m.Text.Length>20)){
                if(timer.Elapsed.TotalSeconds>150)throw new TimeoutException("No live partial reply");await Wait(form,"document.querySelector('.composer')!==null",5);await Task.Delay(50);
            }
            await Button(form,"停止");
            var coordinator=(ChatCoordinator)form.GetType().GetProperty("Coordinator",Flags)!.GetValue(form)!;
            timer.Restart();while(coordinator.Busy){if(timer.Elapsed.TotalSeconds>60)throw new TimeoutException("Native interrupt did not finish");await Wait(form,"document.querySelector('.composer')!==null",5);await Task.Delay(50);}
            Check(draft.Chat.Binding.LocalCancelRequested&&draft.Chat.Binding.InterruptAcknowledged&&draft.Chat.Binding.Stage=="interrupted","真实页面停止取得原生中断回执，保存部分回复并保持界面可见");
            await Shot(form,"stopped-partial");form.Close();
        }
        var saved=store.Find(draft.Id)!;var thread=saved.Chat!.Binding.ThreadId;var turn=saved.Chat.Binding.TurnId;var partial=saved.Chat.Messages.Last().Text;
        using(var reopened=Open(saved)) {
            reopened.Show();await Ready(reopened);Check(Draft(reopened).Chat!.Messages.Last().Text==partial,"停止后重开显示同一部分回复与原输入");
            using var command=JsonDocument.Parse("{\"command\":\"recover\"}");await(Task)Call(reopened,"CommandAsync",command.RootElement)!;
            Check(saved.Chat.Prompts.Count==1&&saved.Chat.Binding.ThreadId==thread&&saved.Chat.Binding.TurnId==turn&&saved.Chat.Binding.Stage=="interrupted","精确回读已停止的原回合，不创建替代会话或重发");
            await Wait(reopened,"document.querySelector('.message.assistant .message-body')?.textContent.length>0");await Shot(reopened,"recovered-partial");
        }
        Check(errors.Count==0&&!File.Exists(Path.Combine(data,"logs","chat-ui.jsonl")),"真实停止、重开与回读全过程无 JavaScript 异常");
    }
    static async Task Run(string testRem,string testLabel)
    {
        if(Directory.Exists(Path.Combine(data,"language-drafts")))throw new Exception("Use a fresh output for live acceptance; never replay an uncertain test turn");
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        var settings=WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json") with {AnkiRootDeck="KnowledgeClipper 验收::首次发送修复 20261009",LanguageAutoRead=false,TypedInputAutoRead=false};
        WorkflowJson.Save(Path.Combine(data,"language-settings.json"),settings);
        WorkflowJson.Save(Path.Combine(data,"chat-preferences.json"),new ChatPreferences("codex","gpt-6.1-sol","low","fast-answer"));
        var registry=WorkflowJson.Read<ChatConfiguration>(@"D:\AppData\KnowledgeClipper\modes\chat\question-types.json");
        WorkflowJson.Save(Path.Combine(data,"modes","chat","question-types.json"),registry with {Types=registry.Types.Select(t=>t.Id=="fast-answer"?t with {DocumentId=testRem,Label=testLabel}:t).ToArray()});
        using var owner=new Form();owner.Show();
        var initial=desktop.GetType("KnowledgeClipper.UserSettings",true)!.GetMethod("Defaults",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,null);
        flow=desktop.GetType("KnowledgeClipper.LanguageDesktopFlow",true)!.GetConstructors(Flags).Single(c=>c.GetParameters().Length==4).Invoke([owner,data,(Action<string>)(s=>Console.WriteLine("ENTRY "+s)),initial]);
        using var lifetime=(IDisposable)flow;
        // Real ordinary input form, then the real chat page send button.
        Call(flow,"OpenInput");var entry=Application.OpenForms.Cast<Form>().Single(f=>f.Name=="knowledge-clipper.text-input");
        Controls(entry).OfType<TextBox>().Single().Text="求 3 与 4 的平方和，写出公式并简要说明。";
        Controls(entry).OfType<Button>().Single(b=>b.Name=="knowledge-clipper.text-input.submit").PerformClick();
        var textForm=Chats().Single();await Ready(textForm);Check(Draft(textForm).Chat!.Prompts.Count==0,"普通提问入口打开后等待用户发送，不自动请求");
        await Button(textForm,"发送");await Wait(textForm,"Array.from(document.querySelectorAll('button')).some(b=>b.textContent==='停止')");
        Web(textForm).CoreWebView2.Reload();await Ready(textForm);await Turn(textForm,0);
        Check(Draft(textForm).Chat!.Prompts.Count==1,"在途重新加载界面不重复发送、不启动竞争恢复");
        Check(Draft(textForm).Chat!.Messages.Last().Text.Contains("25"),"普通入口首次真实 Codex 回答、公式与界面均可见");await Shot(textForm,"text-first-turn");
        await Input(textForm,"如果改为 5 与 12，平方和是多少？保持两行以内。");await Button(textForm,"发送");await Turn(textForm,1);
        Check(Draft(textForm).Chat!.Messages.Last().Text.Contains("169"),"同一真实会话多轮追问全程显示并保留历史");textForm.Close();
        // Independent image-only canary enters through the same material boundary as a capture.
        var imagePath=Path.Combine(data,"public-rectangle.png");using(var image=new Bitmap(500,260)){
            using var graphics=Graphics.FromImage(image);graphics.Clear(Color.White);graphics.DrawRectangle(Pens.Black,40,45,360,150);
            graphics.DrawString("a = 7 cm",new Font("Arial",18),Brushes.Black,130,10);graphics.DrawString("b = 4 cm",new Font("Arial",16),Brushes.Black,280,205);
            graphics.DrawString("Find the area",new Font("Arial",16),Brushes.Black,80,110);image.Save(imagePath,ImageFormat.Png);
        }
        var asset=new ImageAsset(imagePath,ImageAsset.Hash(File.ReadAllBytes(imagePath)),"question",1);
        var material=new CaptureMaterial("公开矩形题",null,"generated_allowed",null,"公开测试图片",0,DateTimeOffset.UtcNow){FrontImage=asset,VisualQuestion=true};
        Call(flow,"StartPreparedMaterial",material,settings,"question",false);var form=Chats().Single();await Ready(form);
        Check(Draft(form).Chat!.Input==""&&Draft(form).Chat!.Attachments.Count==1,"图片入口只保留原图，首次发送无需填文字");
        await Button(form,"发送");await Turn(form,0);var chat=Draft(form).Chat!;
        Check(chat.Messages.First().Parts.All(p=>p.Kind=="image")&&chat.Messages.Last().Text.Contains("28"),"纯图片首次提问与真实 Codex 识图回答全程可见，无白屏");
        await Wait(form,"document.querySelector('.message.user img')?.naturalWidth>0");await Shot(form,"image-first-turn");
        var firstThread=chat.Binding.ThreadId;var firstRules=chat.Prompts[0].Files;
        var ruleConfig=new ChatConfigurationLoader(Path.Combine(AppContext.BaseDirectory,"modes","chat"),Path.Combine(data,"modes","chat"));
        var rulePath=ruleConfig.Editable("types/chunk-solution.md");File.AppendAllText(rulePath,"\n本轮回答末尾单独写验收标记：规则更新已生效。\n");
        await Js(form,"(()=>{const m=document.querySelectorAll('.selectors select')[1];m.value='gpt-6-sol';m.dispatchEvent(new Event('change',{bubbles:true}));const t=document.querySelectorAll('.selectors select')[3];t.value='chunk-solution';t.dispatchEvent(new Event('change',{bubbles:true}));})()");
        await Wait(form,"document.querySelectorAll('.selectors select')[1].value==='gpt-6-sol'&&document.querySelectorAll('.selectors select')[3].value==='chunk-solution'");
        await Input(form,"请用分层列表说明面积公式，再给出一个显示公式。不超过四行。");await Button(form,"发送");await Turn(form,1);
        Check(chat.Binding.ThreadId==firstThread&&chat.Prompts[1].Model=="gpt-6-sol"&&chat.Prompts[1].TypeId=="chunk-solution","真实页面逐轮切换模型与问题类型，沿用同一 Thread");
        Check(chat.Prompts[1].Files.Any(f=>f.Text.Contains("规则更新已生效"))&&!firstRules.Any(f=>f.Text.Contains("规则更新已生效"))&&chat.Messages.Last().Text.Contains("规则更新已生效"),"同一 MD 保存后下一轮读取并实际生效，旧快照保持不变");
        await Select(form,chat.Messages[0].Id,"front");await Select(form,chat.Messages[1].Id,"back");await Select(form,chat.Messages[^1].Id,"back");
        Check(chat.Compositions[0].Front.Contains("asset:")&&chat.Compositions[0].Back.Contains("28"),"实际页面选取多条消息组成含图片与数学公式的卡片");
        await Edit(form,"卡片正面",chat.Compositions[0].Front+"\n\n公开验收：长 $7$ 厘米、宽 $4$ 厘米，求面积。");
        Check(chat.Compositions[0].Front.Contains("公开验收"),"卡片手工编辑从页面保存到原草稿");
        var previousBack=chat.Compositions[0].Back;
        await SelectAuxiliary(form,"辅助服务","codex");
        await Wait(form,"document.querySelector('select[aria-label=\"辅助模型\"]').options.length>1&&document.querySelector('.auxiliary-settings').getAttribute('aria-busy')==='false'");
        await SelectAuxiliary(form,"辅助模型","gpt-6.1-sol");await SelectAuxiliary(form,"辅助推理","low");
        await Button(form,"生成书面化问题");await AuxiliaryTurn(form,0);
        Check(chat.Proposals.Last().Status=="preview"&&chat.Proposals.Last().Cards.Count==1,"真实 Codex 书面化候选在界面正常呈现");
        await Button(form,"采用候选");await Wait(form,"document.querySelectorAll('.proposal').length===0");
        Check(chat.Compositions[0].Back==previousBack,"采用书面化候选保留原解答");
        await Button(form,"整合对话");await AuxiliaryTurn(form,1);
        Check(chat.Proposals.Last().Status=="preview"&&chat.Proposals.Last().Cards.Any(c=>c.Back.Contains("28")),"真实整合候选保留问题条件与正确结论");
        await Button(form,"采用候选");await Wait(form,"document.querySelectorAll('.proposal').length===0");
        await Button(form,"预览卡片");await Wait(form,"document.querySelector('.card-preview .katex')!==null");await Shot(form,"composition-preview");
        var config=new ChatConfigurationLoader(Path.Combine(AppContext.BaseDirectory,"modes","chat"),Path.Combine(data,"modes","chat"));
        var card=chat.Compositions[0];
        await Button(form,"确认并导入 RemNote");await Wait(form,"document.querySelector('.export [role=status]').textContent.includes('已导入 RemNote')",90);
        var remChild=store.Find(card.ExportDraftId!)!;Check(remChild.ImportState==ImportState.Verified,"真实页面 RemNote 导入完成并通过原生结构回读");
        var remId=remChild.Id;await Button(form,"确认并导入 RemNote");await Task.Delay(300);Check(card.ExportDraftId==remId,"重复点击已确认导入保留同一冻结身份");
        await Button(form,"编辑为新草稿");await Wait(form,"document.querySelectorAll('nav[aria-label=卡片] button').length===2");
        var ankiCard=chat.Compositions[^1];await Button(form,"确认并导入 Anki");
        var exportTimer=Stopwatch.StartNew();while(ankiCard.ExportDraftId==null||store.Find(ankiCard.ExportDraftId)?.ImportState!=ImportState.Verified){
            if(exportTimer.Elapsed.TotalSeconds>90)throw new Exception("Anki export did not verify");await Wait(form,"document.querySelector('.composer')!==null",5);await Task.Delay(100);
        }
        var ankiChild=store.Find(ankiCard.ExportDraftId)!;using var http=new HttpClient();var anki=new AnkiConnection(http,settings.AnkiEndpoint);await anki.VerifyAsync(ankiChild);
        var notes=await anki.CallAsync("notesInfo",new{notes=new[]{ankiChild.NoteId!.Value}});
        foreach(var field in ankiChild.ConfirmedNote!.Fields)Check(notes[0].GetProperty("fields").GetProperty(field.Key).GetProperty("value").GetString()==field.Value,"Anki 实际字段回读一致："+field.Key);
        foreach(var media in ankiChild.ConfirmedNote.Media){var bytes=await anki.CallAsync("retrieveMediaFile",new{filename=media.FileName});Check(ImageAsset.Hash(Convert.FromBase64String(bytes.GetString()!))==media.Sha256,"Anki 实际图片 SHA256 一致");}
        WorkflowJson.Save(Path.Combine(output,"exports.json"),new{remChild=remChild.Id,remResult=remChild.RemNoteResult,ankiChild=ankiChild.Id,ankiChild.NoteId,ankiChild.CardIds,deck=ankiChild.ConfirmedNote.Deck,testRem});
        string Content(ChatSession c)=>JsonSerializer.Serialize(new{c.Messages,c.Prompts,c.Compositions,c.Proposals,c.Input,c.Binding.ThreadId,c.Binding.TurnId},WorkflowJson.Options);
        await Shot(form,"both-exports");var draftId=Draft(form).Id;var persisted=Content(chat);form.Close();Call(flow,"Resume",draftId);
        var reopened=Chats().Single();await Ready(reopened);Check(Content(Draft(reopened).Chat!)==persisted,"关闭后重新打开，同一草稿、全部对话与导出身份保持一致");await Shot(reopened,"reopened");
        // New conversation and API service use the same page, with an explicitly valid catalog model.
        await Button(reopened,"新建对话");var apiForm=Chats().Single(f=>f!=reopened);await Ready(apiForm);
        await Js(apiForm,"(()=>{const s=document.querySelector('.selectors select');s.value='api';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
        await Wait(apiForm,"Array.from(document.querySelectorAll('.selectors select')[1].options).some(o=>o.value==='deepseek-flash')");
        await Js(apiForm,"(()=>{const s=document.querySelectorAll('.selectors select')[1];s.value='deepseek-flash';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
        await Input(apiForm,"求 8 与 15 的平方和，用一句话回答。");await Button(apiForm,"发送");await Turn(apiForm,0);
        Check(Draft(apiForm).Chat!.Messages.Last().Text.Contains("289"),"新建对话、切换 API、真实首次发送全流程通过");await Shot(apiForm,"api-first-turn");
        Check(errors.Count==0&&!File.Exists(Path.Combine(data,"logs","chat-ui.jsonl")),"全部真实 GUI 流程零 JavaScript 异常，无白屏兜底触发");
    }
}
