using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    static readonly List<string> checks = [];
    static readonly List<double> warm = [], input = [], selection = [];
    static int modelRequests;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
    [STAThread] static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1") throw new Exception("Use Invoke-IsolatedDesktopTest.ps1");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        var context = new ApplicationContext(); var result = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += async (_, _) => { timer.Stop(); try { await Run(args); } catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(args[2],"ui-error.txt"),ex.ToString()); result = 1; } finally { context.ExitThread(); } };
        timer.Start(); Application.Run(context); return result;
    }
    static async Task Run(string[] args)
    {
        var repo=args[0];var release=args[1];var output=args[2];Directory.CreateDirectory(output);
        var data=Path.Combine(output,"fixture",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(data);
        var listenerPort=new TcpListener(IPAddress.Loopback,0);listenerPort.Start();var port=((IPEndPoint)listenerPort.LocalEndpoint).Port;listenerPort.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();
        var catalogRequests=0;
        var serving=Task.Run(async()=>{while(server.IsListening) { try {var request=await server.GetContextAsync();
            if(request.Request.Url.AbsolutePath=="/race/models") {
                var number=Interlocked.Increment(ref catalogRequests);
                _=Task.Run(async()=>{
                    if(number==1)await Task.Delay(350);
                    var result=Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"catalog-"+number+"\"}]}");
                    request.Response.ContentType="application/json";await request.Response.OutputStream.WriteAsync(result);request.Response.Close();
                });continue;
            }
            var json=request.Request.Url.AbsolutePath.EndsWith("/models")?"{\"data\":[{\"id\":\"public-fixture\"}]}":"{}";
            if(!request.Request.Url.AbsolutePath.EndsWith("/models")) {
                Interlocked.Increment(ref modelRequests);request.Response.SendChunked=true;request.Response.ContentType="text/event-stream";
                var delta=Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"关闭前的部分答案 $x$\"},\"finish_reason\":null}]}\n\n");
                await request.Response.OutputStream.WriteAsync(delta);await request.Response.OutputStream.FlushAsync();await Task.Delay(3000);request.Response.Close();continue;
            }
            var bytes=Encoding.UTF8.GetBytes(json);request.Response.ContentType="application/json";await request.Response.OutputStream.WriteAsync(bytes);request.Response.Close();
        }catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}}});
        var store=new DraftStore(data);var material=new CaptureMaterial("公开测试",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        // Start with an empty conversation and use the actual page send button. Preloaded
        // history alone cannot exercise runtime/context updates when the first item arrives.
        var firstDraft=new ClipDraft {Original=material,Material=material,Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}",Model="public-fixture"},
            Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Chat=new(){Model="public-fixture",Input="首次提问：求 $2+3$。"}};
        string cachedScript="";
        if(args.Length>3&&args[3]!=release) {
            using var oldForm=new LanguageChatForm(firstDraft,store,data,args[3],()=>"fixture",()=>{});oldForm.Show();
            var oldWeb=(WebView2)typeof(LanguageChatForm).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(oldForm);
            var timeout=Stopwatch.StartNew();while(oldWeb.CoreWebView2==null||await oldWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('.composer')!==null")!="true"){
                if(timeout.Elapsed.TotalSeconds>30)throw new Exception("Previous build cache did not warm");await Task.Delay(30);
            }
            cachedScript=await oldWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('script[type=module]').src");oldForm.Close();
        }
        using(var firstForm=new LanguageChatForm(firstDraft,store,data,release,()=>"fixture",()=>{})) {
            firstForm.Show();
            var firstWeb=(WebView2)typeof(LanguageChatForm).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(firstForm);
            var firstWatch=Stopwatch.StartNew();
            while(firstWeb.CoreWebView2==null){if(firstWatch.Elapsed.TotalSeconds>30)throw new Exception("First-send WebView did not start");await Task.Delay(20);}
            var exceptions=new List<string>();
            firstWeb.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived+=(_,e)=>exceptions.Add(e.ParameterObjectAsJson);
            await firstWeb.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable","{}");
            while(await firstWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('.composer-actions .primary')?.disabled===false&&document.querySelectorAll('.selectors select')[1]?.options.length>1")!="true"){
                if(firstWatch.Elapsed.TotalSeconds>40)throw new Exception("First-send page did not become ready");await Task.Delay(20);
            }
            if(cachedScript.Length>0){
                var loaded=await firstWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('script[type=module]').src");
                var index=File.ReadAllText(Path.Combine(release,"chat-view","index.html"));
                var script=System.Text.RegularExpressions.Regex.Match(index,"<script[^>]*src=\"([^\"]+)\"").Groups[1].Value;
                var expected=new Uri(new Uri("https://clipper-chat.local/index.html"),script).AbsoluteUri;
                Check(script.Length>0&&JsonSerializer.Deserialize<string>(loaded)==expected,"沿用旧版 WebView 用户目录和缓存时加载正式包指定的资源");
                WorkflowJson.Save(Path.Combine(output,"upgrade-cache.json"),new{passed=true,previous=cachedScript,current=loaded,expected,frontendChanged=loaded!=cachedScript});
            }
            // Codex spends time preparing the native turn before its first assistant item.
            // Reproduce that public state deterministically, independently of network timing.
            firstDraft.Chat.Messages.Add(new(){Role="user",Parts=[new("text","等待首个回答的公开问题")]});
            typeof(ChatCoordinator).GetField("<Busy>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(firstForm.Coordinator,true);
            using(var pending=JsonDocument.Parse("{\"command\":\"older\"}"))await firstForm.CommandAsync(pending.RootElement);
            await Task.Delay(300);
            File.WriteAllText(Path.Combine(output,"pending-exceptions.json"),JsonSerializer.Serialize(exceptions));
            File.WriteAllText(Path.Combine(output,"pending-dom.json"),await firstWeb.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML"));
            using(var shot=File.Create(Path.Combine(output,"pending.png")))await firstWeb.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,shot);
            Check(exceptions.Count==0&&await firstWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('.composer')!==null&&document.querySelector('.message.user')!==null")=="true","原生回合等待首项期间的临时回答节点不会导致白屏");
            typeof(ChatCoordinator).GetField("<Busy>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(firstForm.Coordinator,false);
            firstDraft.Chat.Messages.Clear();
            using(var reset=JsonDocument.Parse("{\"command\":\"older\"}"))await firstForm.CommandAsync(reset.RootElement);
            await Task.Delay(100);
            await firstWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('.composer-actions .primary').click()");
            while(!firstDraft.Chat.Messages.Any(m=>m.Role=="assistant"&&m.Text.Length>0)){
                if(firstWatch.Elapsed.TotalSeconds>50)throw new Exception("First-send response missing");await Task.Delay(20);
            }
            await Task.Delay(200);
            File.WriteAllText(Path.Combine(output,"first-send-exceptions.json"),JsonSerializer.Serialize(exceptions));
            File.WriteAllText(Path.Combine(output,"first-send-dom.json"),await firstWeb.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML"));
            using(var shot=File.Create(Path.Combine(output,"first-send.png")))await firstWeb.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,shot);
            Check(exceptions.Count==0&&await firstWeb.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.message').length===2&&document.querySelector('.composer')!==null")=="true", "空对话首次点击发送与流式回答始终保留界面，无 JavaScript 异常");
            firstForm.Close();
        }
        Interlocked.Exchange(ref modelRequests,0);
        var draft=new ClipDraft {Original=material,Material=material,Input=new(Intent:"question"),ModeId="question",Route=new("question","question",true,false),
            Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}",Model="public-fixture",ImageModel="public-fixture"},
            Chat=new(){Model="public-fixture"}};
        for(var i=0;i<10;i++) {
            var path=Path.Combine(data,$"public-{i}.png");using(var bitmap=new Bitmap(180,90)){using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.White);graphics.DrawRectangle(Pens.DarkGreen,10,10,140,60);graphics.DrawString($"{i+1} cm",SystemFonts.DefaultFont,Brushes.Black,20,30);bitmap.Save(path,ImageFormat.Png);}
            var bytes=File.ReadAllBytes(path);draft.Chat.Assets.Add(new(path,ImageAsset.Hash(bytes),"question",i+1));
        }
        for(var i=0;i<200;i++) {
            var text=$"第 {i+1} 条公开消息。由已知条件逐步推导，保留全部问题与解答。";
            if(i>=170)text+="\n\n行内 \\(x^2+y^2=25\\)。\n\n$$\n\\begin{aligned}x+y&=7\\\\xy&=12\\end{aligned}\n$$";
            var message=new ChatMessage {Id=$"message-{i}",Role=i%2==0?"user":"assistant",Parts=[new("text",text)]};
            if(i%20==19)message.Parts.Add(new("image",Image:draft.Chat.Assets[i/20]));draft.Chat.Messages.Add(message);
        }
        draft.Chat.Compositions.Add(new(){Front="求 $x+y$。",Back="- 第一层\n  - 第二层 \\(x+y=7\\)\n\n![矩形](asset:"+draft.Chat.Assets[0].Sha256+")"});store.Save(draft);
        var cold=Stopwatch.StartNew();
        using var form=new LanguageChatForm(draft,store,data,release,()=>"fixture",()=>{});form.Show();
        var web=(WebView2)typeof(LanguageChatForm).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
        async Task<string> Js(string script) => await web.CoreWebView2.ExecuteScriptAsync(script);
        async Task Wait(string script,int seconds=45){var watch=Stopwatch.StartNew();while(watch.Elapsed.TotalSeconds<seconds){if(web.CoreWebView2!=null&&await Js(script)=="true")return;await Task.Delay(30);}
            if(web.CoreWebView2!=null){File.WriteAllText(Path.Combine(output,"failed-dom.json"),await Js("document.body.innerHTML"));using var shot=File.Create(Path.Combine(output,"failed-view.png"));await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,shot);}throw new TimeoutException("UI condition: "+script);}
        await Wait("document.querySelectorAll('.message').length===20");var coldMs=cold.Elapsed.TotalMilliseconds;
        Check(await Js("document.querySelectorAll('.message .katex').length>=20")=="true","真实 WebView2 最近 20 条消息渲染括号与美元公式");
        await Wait("Array.from(document.querySelectorAll('.message-body img')).some(i=>i.complete&&i.naturalWidth>0)");
        Check(draft.Chat.Messages.Count==200,"200 条原始消息完整保留");
        async Task Command(object value){using var json=JsonDocument.Parse(JsonSerializer.Serialize(value));await form.CommandAsync(json.RootElement);}
        if(args.Contains("--requirements-only")) {
            await RequirementOptionsChecks.Run(form,web,draft,output,()=>modelRequests,Check);
            WorkflowJson.Save(Path.Combine(output,"complete-results.json"),new{passed=true,checks});
            form.Close();server.Stop();await serving;return;
        }
        // Navigation and model loading are measured separately from warm rendering of the last 20 messages.
        for(var i=0;i<25;i++){
            await Js("window.__paint=0;document.querySelector('.message-body').dataset.measure='before';");
            var watch=Stopwatch.StartNew();await Command(new{command="input",text="性能输入 "+i});
            await Wait("document.querySelector('textarea[aria-label=\"问题\"]').value==="+JsonSerializer.Serialize("性能输入 "+i));input.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();await Command(new{command="select",cardId=draft.Chat.Compositions[0].Id,messageId="message-198",side="front",selected=i%2==0});
            await Wait("Array.from(document.querySelectorAll('.message.user')).at(-1)?.querySelector('.message-actions button')?.getAttribute('aria-pressed')==="+JsonSerializer.Serialize(i%2==0?"true":"false"));
            selection.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();await Js("window.location.reload()");await Wait("document.querySelectorAll('.message').length===20");warm.Add(watch.Elapsed.TotalMilliseconds);
        }
        await Command(new{command="editCard",cardId=draft.Chat.Compositions[0].Id,front="给定 $a=3$，求面积。",back="- 面积公式\n  - \\(S=a^2=9\\)\n\n![矩形](asset:"+draft.Chat.Assets[0].Sha256+")",typeId="fast-answer"});
        await Command(new{command="preview",cardId=draft.Chat.Compositions[0].Id});
        await Wait("document.querySelector('.card-preview .katex')!==null");
        await Wait("document.querySelector('.card-preview img')?.naturalWidth>0");
        Check(true,"卡片预览显示真实公式、两级答案与登记图片");
        Check(await Js("document.querySelector('.card-preview ul ul')!==null")=="true","预览保留嵌套大纲");
        for(var page=0;page<9;page++)await Command(new{command="older"});
        await Wait("document.querySelectorAll('.message').length===200");Check(true,"逐页可达全部历史");
        foreach(var width in new[]{1240,768,414,375,320}) {
            form.MinimumSize=Size.Empty;form.ClientSize=new Size((int)Math.Round(width*form.DeviceDpi/96d),(int)Math.Round(860*form.DeviceDpi/96d));await Task.Delay(120);
            Check(Math.Abs(JsonSerializer.Deserialize<double>(await Js("innerWidth"))-width)<=1,$"实际 CSS 视口宽度 {width}");
            Check(await Js("document.documentElement.scrollWidth<=innerWidth+1")=="true",$"宽度 {width} 无横向溢出");
            using var screenshot=File.Create(Path.Combine(output,$"chat-{width}.png"));await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,screenshot);
        }
        Check(modelRequests==0,"选取、编辑、预览、分页均未调用模型");
        Check(draft.Chat.Messages[170].Text.Contains("\\("),"显示归一化没有覆盖原始 LaTeX");
        static double P95(List<double> values)=>values.Order().ElementAt((int)Math.Ceiling(values.Count*.95)-1);
        var warmP95=P95(warm);var inputP95=P95(input);var selectP95=P95(selection);
        WorkflowJson.Save(Path.Combine(output,"ui-results.json"),new{checks,coldMs,warmMs=warm,warmP95,inputMs=input,inputP95,selectionMs=selection,selectP95,modelRequests,
            performancePassed=warmP95<=500&&inputP95<=100&&selectP95<=100});
        Check(warmP95<=500,$"暖首屏 P95 {warmP95:F1}ms <= 500ms");Check(inputP95<=100&&selectP95<=100,$"输入/选取 P95 {inputP95:F1}/{selectP95:F1}ms <= 100ms");
        form.Close();
        var closingDraft=new ClipDraft{Original=material,Material=material,Settings=draft.Settings,Configuration=draft.Configuration,Chat=new(){Model="public-fixture",Input="在途关闭公开测试"}};
        store.Save(closingDraft);
        using var closingForm=new LanguageChatForm(closingDraft,store,data,release,()=>"fixture",()=>{});closingForm.Show();
        var closingWeb=(WebView2)typeof(LanguageChatForm).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(closingForm);
        var deadline=Stopwatch.StartNew();
        while(closingWeb.CoreWebView2==null||await closingWeb.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.selectors select')[1]?.options.length>1")!="true") {
            if(deadline.Elapsed.TotalSeconds>30)throw new Exception("Close fixture did not load");await Task.Delay(30);
        }
        using var send=JsonDocument.Parse("{\"command\":\"send\"}");var inFlight=closingForm.CommandAsync(send.RootElement);
        while(!closingDraft.Chat.Messages.Any(m=>m.Role=="assistant"&&m.Text.Length>0)) {
            if(inFlight.IsCompleted)await inFlight;if(deadline.Elapsed.TotalSeconds>40)throw new Exception("No partial reply");await Task.Delay(20);
        }
        closingForm.Close();try {await inFlight;}catch(OperationCanceledException){}
        var reopened=store.Find(closingDraft.Id)!;
        Check(closingForm.IsDisposed&&reopened.Chat!.Binding.LocalCancelRequested&&reopened.Chat.Binding.Stage=="unknown"&&
            reopened.Chat.Input=="在途关闭公开测试"&&reopened.Chat.Messages.Any(m=>m.Text.Contains("部分答案")&&m.Status=="unknown"),"在途关闭保存输入、部分回复和未知状态，未冒充远端停止成功");
        WorkflowJson.Save(Path.Combine(output,"close-reopen.json"),new{passed=true,reopened.Id,reopened.Chat!.Binding.Stage,reopened.Chat.Binding.TurnId,modelRequests});
        var catalogDraft=new ClipDraft{Original=material,Material=material,Settings=draft.Settings with {ApiBaseUrl=$"http://127.0.0.1:{port}/race"},
            Configuration=draft.Configuration,Chat=new()};
        using(var catalogForm=new LanguageChatForm(catalogDraft,store,data,release,()=>"fixture",()=>{})) {
            using var query=JsonDocument.Parse("{\"command\":\"models\"}");
            var oldCatalog=catalogForm.CommandAsync(query.RootElement);
            var queryWait=Stopwatch.StartNew();
            while(Volatile.Read(ref catalogRequests)<1){if(queryWait.Elapsed.TotalSeconds>5)throw new Exception("Catalog fixture did not start");await Task.Delay(10);}
            var latestCatalog=catalogForm.CommandAsync(query.RootElement);await Task.WhenAll(oldCatalog,latestCatalog);
            var current=(ChatModel[])typeof(LanguageChatForm).GetField("models",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(catalogForm);
            Check(current.Select(m=>m.Id).SequenceEqual(["catalog-2"]),"迟到模型目录不能覆盖当前选择服务的较新目录");
            WorkflowJson.Save(Path.Combine(output,"catalog-race.json"),new{passed=true,requests=catalogRequests,models=current.Select(m=>m.Id)});
        }
        var faultDraft=new ClipDraft{Original=material,Material=material,Settings=draft.Settings,Configuration=draft.Configuration,
            Chat=new(){Model="public-fixture",Input="故障前输入必须保留"}};
        using(var faultForm=new LanguageChatForm(faultDraft,store,data,release,()=>"fixture",()=>{})) {
            faultForm.Show();var faultWeb=(WebView2)typeof(LanguageChatForm).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(faultForm);
            async Task FaultWait(string script){var watch=Stopwatch.StartNew();while(faultWeb.CoreWebView2==null||await faultWeb.CoreWebView2.ExecuteScriptAsync(script)!="true"){
                if(watch.Elapsed.TotalSeconds>30)throw new Exception("Fault recovery condition: "+script);await Task.Delay(30);
            }}
            await FaultWait("document.querySelectorAll('.selectors select')[1]?.options.length>1");
            var callsBeforeFault=modelRequests;
            faultDraft.Chat.Messages.Add(new(){Parts=[new("image")]});
            using(var update=JsonDocument.Parse("{\"command\":\"older\"}"))await faultForm.CommandAsync(update.RootElement);
            await FaultWait("document.querySelector('[role=alert]')?.textContent.includes('重新加载界面')===true");
            var log=Path.Combine(data,"logs","chat-ui.jsonl");var faultDeadline=Stopwatch.StartNew();
            while(!File.Exists(log)){if(faultDeadline.Elapsed.TotalSeconds>5)throw new Exception("UI exception was not logged");await Task.Delay(20);}
            Check(File.ReadAllText(log).Contains("sha256"),"受控渲染故障保留可恢复提示并记录本机异常，避免纯白页");
            faultDraft.Chat.Messages.Clear();store.Save(faultDraft);
            await faultWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('[role=alert] button').click()");
            await FaultWait("document.querySelector('textarea[aria-label=\"问题\"]')?.value==='故障前输入必须保留'");
            Check(modelRequests==callsBeforeFault&&faultDraft.Chat.Prompts.Count==0,"异常后页面按钮重新加载恢复原输入，不重发模型请求");
            WorkflowJson.Save(Path.Combine(output,"render-fault-recovery.json"),new{passed=true,inputPreserved=true,modelRequestsBefore=callsBeforeFault,modelRequestsAfter=modelRequests,log});
        }
        WorkflowJson.Save(Path.Combine(output,"complete-results.json"),new{passed=true,checks});
        server.Stop();await serving;
    }
}
