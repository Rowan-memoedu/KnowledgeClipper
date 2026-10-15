using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static partial class Program
{
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int ChatMain(string[] args)
    {
        if(Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST")!="1")throw new Exception("Use isolated desktop runner");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var context=new ApplicationContext();using var start=new System.Windows.Forms.Timer{Interval=10};var exit=0;
        start.Tick+=async(_,_)=>{start.Stop();try{await Chat(args);}catch(Exception ex){Console.Error.WriteLine(ex);exit=1;}finally{context.ExitThread();}};
        start.Start();Application.Run(context);return exit;
    }
    static async Task Chat(string[] args)
    {
        var repo=args[1];var output=args[2];var release=args[3];Directory.CreateDirectory(output);
        var root=Path.Combine(output,"fixture");var store=new DraftStore(root);
        var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();var unexpected=0;
        var serving=Task.Run(async()=>{while(server.IsListening){try{var request=await server.GetContextAsync();
            if(!request.Request.Url!.AbsolutePath.EndsWith("/models"))Interlocked.Increment(ref unexpected);
            var bytes=Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"public-fixture\"}]}");request.Response.ContentType="application/json";
            await request.Response.OutputStream.WriteAsync(bytes);request.Response.Close();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}}});
        var material=new CaptureMaterial("公开历史性能样本",null,"generated_allowed",null,"公开 fixture",0,DateTimeOffset.Parse("2026-10-16T00:00:00+08:00"));
        var draft=new ClipDraft{Original=material,Material=material,Input=new(Intent:"question"),ModeId="question",Route=new("question","question",true,false),
            Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}",Model="public-fixture"},Chat=new(){Model="public-fixture"}};
        for(var i=0;i<200;i++)draft.Chat.Messages.Add(new(){Role=i%2==0?"user":"assistant",Parts=[new("text",$"第 {i} 条公开历史，求面积 $S=ab$。\n\n$$x+y=7$$")]});
        for(var i=0;i<24;i++)
        {
            var child=new ClipDraft{Original=material,Material=material,ParentDraftId=draft.Id,Settings=draft.Settings,Configuration=draft.Configuration,
                ImportState=ImportState.Verified,ConfirmedDestination=ExportDestination.Anki,NoteId=i+1,
                Content=new("question",Basic:new("公开问题",string.Concat(Enumerable.Repeat("保留既有解答及公式 $S=ab$。\n",400))))};
            draft.Chat.Compositions.Add(new(){Front="公开问题 "+i,Back="面积 $S=28$。",ExportDraftId=child.Id,ManuallyEdited=true});store.Save(child);
        }
        store.Save(draft);
        var type=Assembly.Load("KnowledgeClipper").GetType("KnowledgeClipper.LanguageChatForm",true)!;var measures=new List<object>();
        for(var i=0;i<4;i++)
        {
            var maxGap=0d;var heartbeat=Stopwatch.StartNew();using var pulse=new System.Windows.Forms.Timer{Interval=10};
            pulse.Tick+=(_,_)=>{maxGap=Math.Max(maxGap,heartbeat.Elapsed.TotalMilliseconds);heartbeat.Restart();};pulse.Start();
            var clock=Stopwatch.StartNew();var reopened=store.Find(draft.Id)!;
            HistoryRecords.RestoreChatCards(reopened,store);
            var restoreMs=clock.Elapsed.TotalMilliseconds;
            using var form=(Form)type.GetConstructors(Instance).Single().Invoke([reopened,store,root,release,(Func<string>)(()=>"fixture"),(Action)(()=>{})]);
            var constructedMs=clock.Elapsed.TotalMilliseconds;
            form.Show();var showCompletedMs=clock.Elapsed.TotalMilliseconds;var web=(WebView2)type.GetField("web",Instance)!.GetValue(form)!;
            async Task Wait(string script){var watch=Stopwatch.StartNew();while(web.CoreWebView2==null||await web.CoreWebView2.ExecuteScriptAsync(script)!="true"){
                if(watch.Elapsed.TotalSeconds>45)throw new Exception("Chat did not load: "+script);await Task.Delay(15);}}
            await Wait("document.querySelectorAll('.message').length===20&&document.querySelectorAll('.selectors select')[1]?.options.length>1");
            var openMs=clock.Elapsed.TotalMilliseconds;await Task.Delay(100);
            var publish=new List<double>();
            for(var j=0;j<10;j++){clock.Restart();type.GetMethod("Publish",Instance)!.Invoke(form,null);publish.Add(clock.Elapsed.TotalMilliseconds);await Task.Delay(40);}
            if(i==0){using var screenshot=File.Create(Path.Combine(output,"chat.png"));await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,screenshot);}
            measures.Add(new{iteration=i,openMs,restoreMs,constructedMs,showCompletedMs,maxUiGapMs=maxGap,publishMs=publish});
            Console.WriteLine($"chat {i}: open {openMs:F1} ms; max UI gap {maxGap:F1} ms; publish median {publish.Order().ElementAt(5):F1} ms");
            var exported=store.Find(reopened.Chat!.Compositions.Last().ExportDraftId!)!;
            exported.ImportState=ImportState.Unknown;exported.ImportError="公开状态失效回归";store.Save(exported);
            type.GetMethod("Publish",Instance)!.Invoke(form,null);
            await Wait("document.querySelector('.export [role=status]')?.textContent==='公开状态失效回归'");
            exported.ImportState=ImportState.Verified;exported.ImportError=null;store.Save(exported);
            type.GetMethod("Publish",Instance)!.Invoke(form,null);
            await Wait("document.querySelector('.export [role=status]')?.textContent?.includes('已导入 Anki')");
            form.Close();await Task.Delay(150);
        }
        server.Stop();await serving;if(unexpected!=0)throw new Exception("History opening sent model requests");
        WorkflowJson.Save(Path.Combine(output,"chat.json"),new{measures,messages=200,cards=24,unexpectedModelRequests=unexpected});
    }
}
