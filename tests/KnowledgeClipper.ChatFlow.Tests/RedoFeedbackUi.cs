using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task RedoFeedbackUi(string repo)
    {
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();
        var apiCalls=0;
        var serving=Task.Run(async()=>{while(server.IsListening)try{
            var context=await server.GetContextAsync();
            if(context.Request.Url!.AbsolutePath.EndsWith("/models")){
                var bytes=Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"feedback-fixture\"}]}");context.Response.ContentType="application/json";
                await context.Response.OutputStream.WriteAsync(bytes);context.Response.Close();continue;
            }
            Interlocked.Increment(ref apiCalls);await Task.Delay(350);context.Response.ContentType="text/event-stream";
            var reply=Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"公开回答 $2+3=5$。\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
            await context.Response.OutputStream.WriteAsync(reply);context.Response.Close();
        }catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        var material=new CaptureMaterial("公开反馈验收",null,"generated_allowed",null,"公开测试",0,DateTimeOffset.UtcNow);
        var draft=new ClipDraft{Original=material,Material=material,Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}",Model="feedback-fixture"},
            Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),ModeId="question",Input=new(Intent:"question"),
            Chat=new(){Model="feedback-fixture",Input="求2+3。",Auxiliary=new(){Provider="api"},Compositions=[new(){Front="原问题 $x$",Back="原答案",Annotation="原批注",AnnotationEnabled=true}]}};
        draft.Chat.Auxiliary.Current.Model="feedback-fixture";store.Save(draft);
        var constructor=desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single(c=>c.GetParameters().Length==6);
        Form Open(ClipDraft value)=>(Form)constructor.Invoke([value,store,data,AppContext.BaseDirectory,(Func<string>)(()=>"fixture"),(Action)(()=>{})]);
        async Task Hold(Form form)=>await Js(form,"window.__held=[];window.__post=window.chrome.webview.postMessage.bind(window.chrome.webview);window.chrome.webview.postMessage=v=>window.__held.push(v)");
        string Find(string label)=>"Array.from(document.querySelectorAll('button')).find(b=>b.textContent==="+JsonSerializer.Serialize(label)+")";
        void Ack(Form form,string id)=>Web(form).CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{kind="commandAcknowledged",requestId=id}));
        async Task<string> HeldId(Form form)=>JsonSerializer.Deserialize<string>(await Js(form,"window.__held.at(-1).clientCommandId"))!;
        var measurements=new List<JsonElement>();
        using(var form=Open(draft)){
            form.Show();await Ready(form);await Wait(form,"document.querySelector('.auxiliary-settings select[aria-label=\"辅助模型\"]').value==='feedback-fixture'");
            await Hold(form);
            var immediate=await Js(form,"(()=>{const b="+Find("发送")+";const before=getComputedStyle(b).backgroundColor;const swatch=document.createElement('span');swatch.style.backgroundColor='var(--canvas)';document.body.append(swatch);const expected=getComputedStyle(swatch).backgroundColor;swatch.remove();const t=performance.now();b.click();b.click();return {disabled:b.disabled,busy:b.getAttribute('aria-busy'),ms:performance.now()-t,background:getComputedStyle(b).backgroundColor,before,expected,transition:getComputedStyle(b).transitionDuration};})()");
            var measured=JsonDocument.Parse(immediate).RootElement.Clone();measurements.Add(measured);
            WorkflowJson.Save(Path.Combine(output,"first-click.json"),measured);
            Check(measured.GetProperty("disabled").GetBoolean()&&measured.GetProperty("busy").GetString()=="true","有效点击在当前JS事件内同步变灰，不等待桌面端或厂商");
            Check(measured.GetProperty("background").GetString()==measured.GetProperty("expected").GetString()&&measured.GetProperty("transition").GetString()=="0s","置灰样式同事件生效，无100ms颜色动画延迟");
            await Wait(form,"window.__held.length===1");
            Check(apiCalls==0&&draft.Chat.Prompts.Count==0,"桌面端完全未收到请求时已反馈，双击只排入一次发送");
            await Js(form,Find("发送")+".scrollIntoView({block:'center'})");
            Call(form,"Publish");Ack(form,"unrelated-or-older-request");await Task.Delay(150);
            Check(await Js(form,Find("发送")+".disabled")=="true","旧空闲状态和无关回执不能提前解除本次置灰");
            await Shot(form,"send-before-host");
            await Js(form,"window.__held.splice(0).forEach(v=>window.__post(v))");
            await Turn(form,0);await Wait(form,"!document.querySelector('textarea[aria-label=\"问题\"]').disabled");
            Check(apiCalls==1&&draft.Chat.Binding.Stage=="completed","正常发送完成后解除本地等待，只有一次实际模型调用");

            foreach(var label in new[]{"生成书面化问题","生成书面化答案","全部书面化","整合所选对话","预览卡片","确认并导入 RemNote","确认并导入 Anki","新卡片"}){
                await Wait(form,"!!("+Find(label)+")&&!"+Find(label)+".disabled");
                var result=await Js(form,"(()=>{window.__held=[];const b="+Find(label)+";const t=performance.now();b.click();return {disabled:b.disabled,busy:b.getAttribute('aria-busy'),ms:performance.now()-t};})()");
                var row=JsonDocument.Parse(result).RootElement.Clone();measurements.Add(row);
                Check(row.GetProperty("disabled").GetBoolean()&&row.GetProperty("busy").GetString()=="true","按钮同步反馈，未调用后端："+label);
                await Wait(form,"window.__held.length===1");var id=await HeldId(form);
                Call(form,"Publish");await Task.Delay(70);Check(await Js(form,Find(label)+".disabled")=="true","状态刷新保留本次按钮锁："+label);
                Ack(form,id);await Wait(form,"!"+Find(label)+".disabled");
            }
            Check(apiCalls==1&&draft.Chat.Compositions.All(c=>c.ExportDraftId==null),"反馈夹具没有执行辅助模型或真实导出，未冻结任何测试卡");
            await Js(form,"window.chrome.webview.postMessage=window.__post");
            await Edit(form,"卡片正面","改后问题 $y$");
            await CtrlUndo(form,"卡片正面");Check(draft.Chat.Compositions[0].Front=="原问题 $x$","实际Ctrl+Z撤销卡片编辑");
            async Task RedoKey(){await Js(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').focus()");
                await Web(form).CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"modifiers\":2,\"key\":\"y\",\"code\":\"KeyY\",\"windowsVirtualKeyCode\":89}");
                await Web(form).CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyUp\",\"modifiers\":0,\"key\":\"y\",\"code\":\"KeyY\",\"windowsVirtualKeyCode\":89}");
                await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').value==='改后问题 $y$'");}
            await RedoKey();Check(draft.Chat.Compositions[0].Front=="改后问题 $y$"&&draft.Chat.Compositions[0].Annotation=="原批注","实际Ctrl+Y重做，与撤销链共用并保留批注");
            await Button(form,"撤销上次编辑");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').value==='原问题 $x$'");
            await Button(form,"重做上次编辑");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').value==='改后问题 $y$'");
            Check(true,"重做按钮与Ctrl+Y恢复同一编辑记录");
            await CtrlUndo(form,"卡片正面");form.Close();
        }
        var reopened=store.Find(draft.Id)!;
        using(var form=Open(reopened)){
            form.Show();await Ready(form);await Button(form,"重做上次编辑");
            await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').value==='改后问题 $y$'");
            Check(apiCalls==1&&reopened.Chat!.Compositions[0].Front=="改后问题 $y$","重开后保留重做链，不重发模型请求");
            await Edit(form,"卡片正面","![缺图](asset:"+new string('a',64)+")");
            await Button(form,"预览卡片");await Wait(form,"document.querySelector('[role=alert]')!==null");
            await Wait(form,"!"+Find("预览卡片")+".disabled");Check(true,"后端校验失败也解除按钮锁并保留错误反馈");
            await Shot(form,"redo-and-validation-error");
        }
        Check(errors.Count==0,"本地即时反馈、重做、重开及校验失败无JavaScript异常");
        WorkflowJson.Save(Path.Combine(output,"immediate-feedback.json"),new{passed=true,measurements,apiCalls,exportCalls=0,checks});
        server.Stop();await serving;
    }
}
