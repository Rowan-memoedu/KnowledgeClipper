using System.Diagnostics;
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

internal static class BranchUiChecks
{
    internal static async Task Run(string[] args)
    {
        var repo=args[0];var release=args[1];var output=args[2];Directory.CreateDirectory(output);
        var data=Path.Combine(output,"fixture",Guid.NewGuid().ToString("N"));var store=new DraftStore(data);
        var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();var port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();var requests=0;
        var serving=Task.Run(async()=>{while(server.IsListening){try{var context=await server.GetContextAsync();_ = Task.Run(async()=>{
            var response=context.Response;
            if(context.Request.Url!.AbsolutePath.EndsWith("models"))await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"fixture\"},{\"id\":\"other\"}]}"));
            else{Interlocked.Increment(ref requests);var body=await new StreamReader(context.Request.InputStream).ReadToEndAsync();response.ContentType="text/event-stream";await Task.Delay(1200);
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body.Contains("ERROR_FIXTURE")?"data: {\"choices\":[{\"delta\":{\"content\":\"部分回复\"}}]}\n\n":"data: {\"choices\":[{\"delta\":{\"content\":\"隔离分支回复 $x=9$\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));}
            response.Close();});}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}}});
        var material=new CaptureMaterial("公开 Tab 界面验收",null,"generated_allowed",null,"public",0,DateTimeOffset.UtcNow);
        var draft=new ClipDraft{Original=material,Material=material,Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}"},Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Chat=new(){Model="fixture"}};
        draft.Chat.Messages.AddRange(new[]{("u1","user","公开第一问"),("a1","assistant","第一轮回答 $x=7$"),("u2","user","后续原对话问题"),("a2","assistant","后续原对话回答")}.Select(x=>new ChatMessage{Id=x.Item1,Role=x.Item2,NativeTurnId=x.Item1.EndsWith('1')?"t1":"t2",Parts=[new("text",x.Item3)]}));
        using var form=new LanguageChatForm(draft,store,data,release,()=>"fixture",()=>throw new Exception("Must not create another window"));form.Show();
        var web=(WebView2)typeof(LanguageChatForm).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
        var checks=new List<string>();var screenshots=new List<object>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks.Add(label);Console.WriteLine("PASS "+label);}
        async Task Wait(string script){var watch=Stopwatch.StartNew();while(web.CoreWebView2==null||await web.CoreWebView2.ExecuteScriptAsync(script)!="true"){if(watch.Elapsed.TotalSeconds>40)throw new Exception("Tab UI wait: "+script);await Task.Delay(30);}}
        Task Script(string script)=>web.CoreWebView2.ExecuteScriptAsync(script);
        Task Click(string selector)=>Script($"document.querySelector({JsonSerializer.Serialize(selector)}).click()");
        async Task Type(string text){await Script($"(()=>{{const e=document.querySelector('textarea[aria-label=问题]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(e,{JsonSerializer.Serialize(text)});e.dispatchEvent(new Event('input',{{bubbles:true}}));}})()");await Task.Delay(120);}
        await Wait("document.querySelectorAll('[role=tab]').length===1&&document.querySelectorAll('.selectors select')[1]?.options.length>1");
        // The app keeps a hidden WebView warm host. Count chat windows, not that
        // unrelated controller whose asynchronous lifetime is intentionally free.
        var windows=Application.OpenForms.OfType<LanguageChatForm>().Count();
        await Script("document.querySelector('.message-body[data-message-id=a1]').closest('.message').querySelector('.fork-action').click()");
        await Wait("document.querySelectorAll('[role=tab]').length===2&&document.querySelector('[role=tab][aria-selected=true]').textContent.includes('分叉')");
        var branch=draft.Chat.Branches.Single();
        WorkflowJson.Save(Path.Combine(output,"first-fork-observed.json"),new{windows,currentWindows=Application.OpenForms.OfType<LanguageChatForm>().Count(),count=branch.Session.Messages.Count,
            messages=branch.Session.Messages.Select(m=>new{m.Id,m.Role,m.Text}),draft.Chat.ActiveConversationId});
        Check(Application.OpenForms.OfType<LanguageChatForm>().Count()==windows&&branch.Session.Messages.Count==2,"点击第五个分叉按钮在同页新增 Tab，仅保留选定前缀");
        await Type("分支未发送输入");await Click("#conversation-main");
        await Wait("document.querySelector('[data-message-id=a2]')!==null");await Type("原对话未发送输入");
        await Script("document.querySelector('.message-body[data-message-id=a2]').closest('.message').querySelectorAll('.message-actions button')[1].click()");
        await Click("#conversation-"+branch.Id);await Wait("document.querySelector('textarea[aria-label=问题]').value==='分支未发送输入'");
        Check(draft.Chat.Input=="原对话未发送输入"&&draft.Chat.Compositions.Single().Back.Contains("后续原对话回答"),"切换保留各 Tab 输入和共享卡片内容");
        await Click(".composer-actions .primary");
        await Wait("document.querySelector('[role=tab][aria-selected=true]').textContent.includes('回复中')");
        await Click(".new-conversation-tab");await Wait("document.querySelectorAll('[role=tab]').length===3&&document.querySelectorAll('.message').length===0");
        var blank=draft.Chat.Branches.Last();await Wait("document.querySelector('.composer-actions .primary')!==null&&document.querySelector('.composer-actions').textContent.includes('草稿已保存')");
        Check(blank.Session.Messages.Count==0&&branch.Session.Messages.Last().Text.Contains("隔离分支回复")&&Application.OpenForms.OfType<LanguageChatForm>().Count()==windows,"生成中切换空白 Tab，流式回复仍写入原分支且不串页");
        await Click("#conversation-"+branch.Id);await Wait("document.querySelector('.viewport').textContent.includes('隔离分支回复')");
        await Script("[...document.querySelectorAll('.message.assistant')].at(-1).querySelectorAll('.message-actions button')[1].click()");
        await Wait("document.querySelector('textarea[aria-label=卡片背面]').value.includes('隔离分支回复')");
        Check(draft.Chat.Compositions.Single().Back.Contains("后续原对话回答")&&draft.Chat.Compositions.Single().Back.Contains("隔离分支回复"),"原对话和分支消息可实际加入同一张卡片");
        Check(await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sources').parentElement.parentElement.textContent.includes('分叉')")=="true"||await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.cards').textContent.includes('分叉')")=="true","卡片来源保留所属对话标签");
        await Click(".cards-heading button");await Wait("document.querySelectorAll('nav[aria-label=卡片] button').length===2&&!document.querySelector('.cards-heading button').disabled");
        await Click(".cards-heading button");await Wait("document.querySelectorAll('nav[aria-label=卡片] button').length===3");
        Check(draft.Chat.Compositions.Count==3&&draft.Chat.Branches.Count==2,"右侧可独立增加多张卡，不新建对话或窗口");
        using(var late=JsonDocument.Parse("{\"command\":\"input\",\"conversationId\":\"main\",\"text\":\"迟到的原对话输入\"}"))await form.CommandAsync(late.RootElement);
        var activeInput=branch.Session.Input;
        try{using var lateSend=JsonDocument.Parse("{\"command\":\"send\",\"conversationId\":\"main\"}");await form.CommandAsync(lateSend.RootElement);throw new Exception("Stale send was accepted");}
        catch(InvalidOperationException){}
        Check(draft.Chat.Input=="迟到的原对话输入"&&branch.Session.Input==activeInput&&requests==1,"迟到输入归原 Tab，迟到发送不能落到其他分支");
        await Script($"document.getElementById('conversation-{branch.Id}').dispatchEvent(new KeyboardEvent('keydown',{{key:'Home',bubbles:true}}))");
        await Wait("document.activeElement?.id==='conversation-main'");
        await Script("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}))");
        await Wait($"document.activeElement?.id==='conversation-{branch.Id}'");
        Check(draft.Chat.ActiveConversationId==branch.Id,"Tab 支持方向键和 Home 切换，键盘焦点保持在所选 Tab");
        await Click("nav[aria-label=卡片] button");await Wait("document.querySelector('textarea[aria-label=卡片背面]').value.includes('隔离分支回复')");
        foreach(var width in new[]{1240,768,414,375,320})
        {
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",JsonSerializer.Serialize(new{width,height=950,deviceScaleFactor=1,mobile=false}));await Task.Delay(150);
            Check(await web.CoreWebView2.ExecuteScriptAsync("document.documentElement.scrollWidth<=window.innerWidth+1")=="true","Tab 与分叉控件无页面横向溢出："+width);
            using var shot=File.Create(Path.Combine(output,"tabs-"+width+".png"));await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,shot);
            screenshots.Add(new{width,path="tabs-"+width+".png"});
        }
        await Type("ERROR_FIXTURE");await Click(".composer-actions .primary");await Wait("document.querySelector('[role=tab][aria-selected=true]').textContent.includes('回复中')");
        await Click(".new-conversation-tab");await Wait("document.querySelectorAll('[role=tab]').length===4&&document.querySelector('.composer-actions').textContent.includes('草稿已保存')");
        Check(branch.Session.Binding.Stage=="unknown"&&draft.Chat.Branches.Last().Session.Messages.Count==0&&await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.composer .error')===null")=="true","后台分支断线保留原输入和未知状态，错误不会串到新 Tab");
        await Click("#conversation-"+branch.Id);await Wait("document.querySelector('.composer .error')!==null");
        var id=draft.Id;form.Close();var restored=store.Find(id)!;
        Check(restored.Chat!.Branches.Count==3&&restored.Chat.Compositions.Count==3&&restored.Chat.ActiveConversationId==branch.Id,"关闭重开数据保留 Tab 顺序、当前分支与三张共享卡");
        Check(requests==2,"分叉、新建、切换、选材和新增卡片均不调用模型，仅两次显式发送");
        WorkflowJson.Save(Path.Combine(output,"tabs-ui-results.json"),new{passed=true,checks,screenshots,requests,data,id});
        server.Stop();await serving;
    }
}
