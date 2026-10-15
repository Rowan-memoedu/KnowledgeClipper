using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task AuxiliaryUi(string repo,bool live)
    {
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        var material=new CaptureMaterial("公开辅助模型验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        using var server=new HttpListener();var requests=new List<JsonElement>();Task? serving=null;
        var settings=live?WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json"):new LanguageSettings();
        if(!live)
        {
            var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();var port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
            settings=settings with{ApiBaseUrl=$"http://127.0.0.1:{port}",Model="main-fixture",ImageModel="aux-fixture"};
            server.Prefixes.Add(settings.ApiBaseUrl+"/");server.Start();
            serving=Task.Run(async()=>{while(server.IsListening)try{
                var request=await server.GetContextAsync();string response;
                if(request.Request.Url!.AbsolutePath.EndsWith("/models"))response=JsonSerializer.Serialize(new{data=new[]{"main-fixture","aux-fixture","alternate-fixture"}.Select(id=>new{id,name=id,input_modalities=new[]{"text","image"},effort=new{supported_levels=new[]{"low","high","max"},default_level="high"}})});
                else
                {
                    using var input=JsonDocument.Parse(await new StreamReader(request.Request.InputStream).ReadToEndAsync());var payload=input.RootElement.Clone();lock(requests)requests.Add(payload);
                    using var snapshot=JsonDocument.Parse(payload.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("text").GetString()!);
                    if(snapshot.RootElement.GetProperty("requirements").GetString()=="受控关闭验收")
                    {
                        request.Response.ContentType="text/event-stream";
                        await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"辅助回合的部分回复\"},\"finish_reason\":null}]}\n\n"));
                        await request.Response.OutputStream.FlushAsync();await Task.Delay(2000);request.Response.Close();continue;
                    }
                    var answer=JsonSerializer.Serialize(new{cards=new[]{new{front="已知矩形长7厘米、宽4厘米，求面积。",back="$S=7\\times4=28$ 平方厘米。",typeId="fast-answer"}}});
                    response="data: "+JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=answer},finish_reason="stop"}}})+"\n\ndata: [DONE]\n\n";
                    request.Response.ContentType="text/event-stream";
                }
                await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response));request.Response.Close();
            }catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        }
        var idPath=Path.Combine(output,"draft-id.txt");ClipDraft draft;
        if(live&&File.Exists(idPath))draft=store.Find(File.ReadAllText(idPath))!;
        else
        {
            draft=new(){Original=material,Material=material,Settings=settings,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),
                Chat=new(){Model=live?"gpt-6.1-sol":"main-fixture",Effort=live?"high":"max",UseQuestionTypeDefaults=live,
                    Binding=new(){Provider=live?"codex":"api",Stage="completed",ThreadId="public-original-main-thread",TurnId="public-original-main-turn"}}};
            draft.Chat.Messages.AddRange([new(){Role="user",Parts=[new("text","矩形长7厘米、宽4厘米，求其面积。") ]},new(){Role="assistant",Parts=[new("text","$S=7\\times4=28$ 平方厘米。") ]}]);
            draft.Chat.Compositions.Add(new(){Front="长7宽4的矩形面积怎么算？",Back="$S=7\\times4=28$ 平方厘米。",TypeId="fast-answer"});
            store.Save(draft);File.WriteAllText(idPath,draft.Id);
        }
        var constructor=desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single();
        Form Open(ClipDraft value)=>(Form)constructor.Invoke([value,store,data,AppContext.BaseDirectory,(Func<string>)(()=>live?ContentCredentials.Resolve():"fixture"),(Action)(()=>{})]);
        var preservedAuxiliary=JsonSerializer.Serialize(draft.Chat!.Auxiliary,WorkflowJson.Options);
        var form=Open(draft);form.Show();
        try
        {
            await Ready(form);await Wait(form,"document.querySelector('select[aria-label=\"辅助模型\"]')?.options.length>1&&!document.querySelector('.auxiliary-settings').getAttribute('aria-busy').includes('true')");
            if(live&&File.Exists(Path.Combine(output,"auxiliary-acceptance.json")))
            {
                Check(JsonSerializer.Serialize(draft.Chat!.Auxiliary,WorkflowJson.Options)==preservedAuxiliary&&draft.Chat.Auxiliary!.Sessions["api"].Prompts.Count==3&&draft.Chat.Auxiliary.Sessions["codex"].Prompts.Count==1,"最终构建重开已验收真实 API/Codex 草稿，完整恢复原辅助身份，不重放成功请求");
                await Shot(form,"final-build-readback");
                var preservedNative=draft.Chat!.Auxiliary!.Sessions["codex"];
                WorkflowJson.Save(Path.Combine(output,"final-build-readback.json"),new{passed=true,desktop=desktop.Location,thread=preservedNative.Binding.ThreadId,turn=preservedNative.Binding.TurnId,generationCalls=0});
                return;
            }
            var chat=draft.Chat!;var card=chat.Compositions[0];
            string MainState()=>JsonSerializer.Serialize(new{chat.Model,chat.Effort,chat.Binding,chat.Messages,chat.Prompts},WorkflowJson.Options);
            var mainState=MainState();var originalBack=card.Back;
            Check(chat.Auxiliary!.Provider=="api"&&chat.Auxiliary.Current.Model==(live?settings.ImageModel:"aux-fixture"),"辅助控件初始使用 API 默认模型，与主会话独立");
            await SelectAuxiliary(form,"辅助推理","low");
            await Wait(form,"document.querySelector('select[aria-label=\"辅助推理\"]').value==='low'");
            Check(MainState()==mainState,"修改辅助推理不改主模型、等级或原生绑定");
            async Task Generate(string action)
            {
                var proposal=chat.Proposals.LastOrDefault(p=>p.Action==action&&p.Status!="adopted"&&p.Status!="discarded");
                var previousCount=chat.Proposals.Count;
                if(proposal==null){await Button(form,action=="formalize"?"生成书面化问题":"整合所选对话");}
                var timer=System.Diagnostics.Stopwatch.StartNew();var reported="";
                while(timer.Elapsed.TotalSeconds<240)
                {
                    if(proposal==null&&chat.Proposals.Count>previousCount)proposal=chat.Proposals.Last();
                    if(proposal==null){await Task.Delay(40);continue;}var aux=chat.Auxiliary.Current;
                    if(aux.Binding.Stage!=reported){reported=aux.Binding.Stage;Console.WriteLine("AUXILIARY "+aux.Binding.Provider+" "+reported);}
                    if(proposal.Status=="adopted")break;
                    if(proposal.Status is "incomplete" or "unparsed")throw new Exception(proposal.Error+" "+aux.Binding.Error);
                    await Task.Delay(80);
                }
                if(proposal?.Status!="adopted")throw new Exception("Auxiliary timeout; original identity and draft retained.");
                Check(proposal.Cards.Count==1&&card.Front==proposal.Cards[0].Front&&card.Front.Contains('7')&&card.Front.Contains('4'),"辅助操作直接更新原卡片，保留完整条件");
                if(action=="formalize")Check(proposal.Cards[0].Back==originalBack,"书面化候选强制保留实际背面原文");
                await Wait(form,"document.querySelectorAll('.proposal').length===0");
            }
            if(chat.Proposals.Count==0)await Generate("formalize");
            if(!live){await CtrlUndo(form,"卡片正面");Check(card.Front=="长7宽4的矩形面积怎么算？"&&card.Back==originalBack,"书面化直接替换后，Ctrl+Z 恢复原问题与背面");}
            Check(card.Back==originalBack,"已有正面整理及明确采用后，背面逐字不变");
            if(chat.Proposals.Count<2)
            {
                await Edit(form,"卡片正面","");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').value===''");
                await Generate("formalize");
            }
            Check(card.Front.Contains('7')&&card.Front.Contains('4')&&!card.Front.Contains("28")&&card.Back==originalBack,"空正面从反面与上下文生成问题，不把答案结论当作题设");
            await SelectAuxiliary(form,"辅助推理","high");
            if(chat.Proposals.Count<3)await Generate("integrate");
            Check(MainState()==mainState,"三个辅助 API 回合不新增或改写主 Thread、Turn、快照及对话消息");
            if(!live)
            {
                Check(requests.Count==3&&requests.All(r=>r.GetProperty("model").GetString()=="aux-fixture")&&requests[0].GetProperty("reasoning_effort").GetString()=="low"&&requests[^1].GetProperty("reasoning_effort").GetString()=="high","真实 UI 发出的三次 HTTP 请求使用独立模型和 low/high 参数");
                await Js(form,"(()=>{const s=document.querySelectorAll('.selectors select')[1];s.value='alternate-fixture';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
                await Wait(form,"document.querySelectorAll('.selectors select')[1].value==='alternate-fixture'");
                Check(chat.Auxiliary.Current.Model=="aux-fixture"&&chat.Auxiliary.Current.Effort=="high","主模型切换不跟随改动辅助控件");
            }
            else
            {
                originalBack=card.Back;
                var native=chat.Auxiliary.Sessions.GetValueOrDefault("codex");
                if(native?.Prompts.Count==0||native==null)
                {
                    await SelectAuxiliary(form,"辅助服务","codex");await Wait(form,"document.querySelector('select[aria-label=\"辅助模型\"]').options.length>1&&document.querySelector('.auxiliary-settings').getAttribute('aria-busy')==='false'");
                    await SelectAuxiliary(form,"辅助模型","gpt-6.1-sol");await SelectAuxiliary(form,"辅助推理","low");
                    await Generate("formalize");
                }
                else await SelectAuxiliary(form,"辅助服务","codex");
                Check(chat.Auxiliary.Current.Binding.ThreadId!=null&&chat.Auxiliary.Current.Binding.ThreadId!=chat.Binding.ThreadId&&MainState()==mainState,"真实官方 Codex 辅助回合使用独立 Thread，主绑定与模型完整保留");
            }
            foreach(var width in new[]{1240,768,414,375,320})
            {
                var scale=form.DeviceDpi/96d;
                form.MinimumSize=new(1,1);form.ClientSize=new((int)(width*scale),(int)(900*scale));await Task.Delay(180);
                await Js(form,"document.querySelector('.auxiliary-panel').scrollIntoView({block:'center'})");
                var geometry=await Js(form,"(()=>{const p=document.querySelector('.auxiliary-panel'),a=p.querySelector('.auxiliary-actions').getBoundingClientRect(),r=p.querySelector('.auxiliary-settings').getBoundingClientRect();return {overflow:document.documentElement.scrollWidth>innerWidth,below:r.top>=a.bottom,compact:r.height<60,rows:r.width,buttons:a.width,labels:p.querySelectorAll('select').length};})()");
                using var parsed=JsonDocument.Parse(geometry);var g=parsed.RootElement;
                Check(!g.GetProperty("overflow").GetBoolean()&&g.GetProperty("below").GetBoolean()&&g.GetProperty("compact").GetBoolean()&&g.GetProperty("rows").GetDouble()==g.GetProperty("buttons").GetDouble()&&g.GetProperty("labels").GetInt32()==3,"共用辅助三控件保留在整合按钮下方，比例紧凑且无溢出："+width);
                await Shot(form,"auxiliary-"+width);
            }
            var snapshot=JsonSerializer.Serialize(chat.Auxiliary,WorkflowJson.Options);var saved=store.Find(draft.Id)!;form.Close();form=Open(saved);form.Show();await Ready(form);
            await Wait(form,"document.querySelector('select[aria-label=\"辅助模型\"]').options.length>1");
            Check(JsonSerializer.Serialize(saved.Chat!.Auxiliary,WorkflowJson.Options)==snapshot,"关闭重开后辅助选择、全部原生身份及候选历史保持原样");
            if(!live)
            {
                var beforeClose=JsonSerializer.Serialize(new{saved.Chat.Model,saved.Chat.Effort,saved.Chat.Binding,saved.Chat.Messages,saved.Chat.Prompts},WorkflowJson.Options);
                var configuration=new ChatConfigurationLoader(Path.Combine(AppContext.BaseDirectory,"modes","chat"),Path.Combine(data,"modes","chat"));
                var rules=configuration.Editable("types/fast-answer.md");var content=File.ReadAllText(rules);File.WriteAllText(rules,"");
                var requestsBeforeFailure=requests.Count;await Button(form,"生成书面化问题");
                var failed=System.Diagnostics.Stopwatch.StartNew();
                while(saved.Chat.Proposals.Last().Status!="failed"){if(failed.Elapsed.TotalSeconds>10)throw new Exception("Preflight failure not recorded");await Task.Delay(30);}
                Check(requests.Count==requestsBeforeFailure&&saved.Chat.Proposals.Last().RawOutput=="","辅助发送前校验失败只保留本次输入，不能冒用上一回合答案");File.WriteAllText(rules,content);
                await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label=\"补充要求\"]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,'受控关闭验收');t.dispatchEvent(new Event('input',{bubbles:true}));})()");
                await Button(form,"生成书面化问题");
                var closing=System.Diagnostics.Stopwatch.StartNew();
                while(saved.Chat.Auxiliary!.Current.Messages.LastOrDefault()?.Text.Contains("部分回复")!=true){if(closing.Elapsed.TotalSeconds>15)throw new Exception("Auxiliary partial response not received");await Task.Delay(30);}
                form.Close();while(!form.IsDisposed){if(closing.Elapsed.TotalSeconds>20)throw new Exception("Auxiliary close did not finish");await Task.Delay(30);}
                var closed=store.Find(saved.Id)!;var aux=closed.Chat!.Auxiliary!.Current;
                Check(aux.Binding.Stage=="unknown"&&aux.Binding.LocalCancelRequested&&aux.Prompts.Last().Input.Contains("requirements")&&aux.Messages.Last().Text.Contains("部分回复"),"辅助生成中关闭保存原快照、部分回复和 Unknown，不中断主绑定");
                Check(JsonSerializer.Serialize(new{closed.Chat.Model,closed.Chat.Effort,closed.Chat.Binding,closed.Chat.Messages,closed.Chat.Prompts},WorkflowJson.Options)==beforeClose,"辅助关闭与停止保持主对话完整状态");
                var writes=requests.Count;form=Open(closed);form.Show();await Ready(form);await Task.Delay(300);
                Check(requests.Count==writes&&closed.Chat.Auxiliary.Current.Binding.Stage=="unknown","重开未知辅助回合只加载界面，不自动重发");
            }
            Check(errors.Count==0,"辅助生成和响应式控件无 JavaScript 异常");
            WorkflowJson.Save(Path.Combine(output,"auxiliary-acceptance.json"),new{live,mainModel=chat.Model,mainEffort=chat.Effort,mainThread=chat.Binding.ThreadId,
                auxiliary=chat.Auxiliary,card.Front,card.Back,proposalCount=chat.Proposals.Count,requests,noExport=true});
        }
        finally{form.Dispose();if(server.IsListening)server.Stop();if(serving!=null)await serving;}
    }
    static async Task SelectAuxiliary(Form form,string label,string value)
    {
        await Js(form,"(()=>{const s=document.querySelector('select[aria-label="+JsonSerializer.Serialize(label)+"]');s.value="+JsonSerializer.Serialize(value)+";s.dispatchEvent(new Event('change',{bubbles:true}));})()");
        await Wait(form,"document.querySelector('select[aria-label="+JsonSerializer.Serialize(label)+"]').value==="+JsonSerializer.Serialize(value));
    }
    static async Task AuxiliaryTurn(Form form,int previous)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(timer.Elapsed.TotalSeconds<240)
        {
            var chat=Draft(form).Chat!;var auxiliary=chat.Auxiliary!.Current;
            if(auxiliary.Prompts.Count>previous&&auxiliary.Binding.Stage is "failed" or "unknown" or "interrupted")throw new Exception(auxiliary.Binding.Error);
            if(auxiliary.Prompts.Count>previous&&auxiliary.Binding.Stage=="completed"&&chat.Proposals.Last().Status=="preview")return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Original auxiliary turn retained; no resend.");
    }
}
