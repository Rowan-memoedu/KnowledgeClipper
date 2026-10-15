using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task AllFormalizeUi(string repo,bool live)
    {
        var processArgs=Environment.GetCommandLineArgs();var readback=Array.IndexOf(processArgs,"--readback");
        if(readback>=0){await AllFormalizeReadbackUi(processArgs[readback+1]);return;}
        var annotationReadback=Array.IndexOf(processArgs,"--annotation-readback");
        if(annotationReadback>=0){await AnnotationReadbackUi(processArgs[annotationReadback+1]);return;}
        var annotationsOnly=processArgs.Contains("--all-formalize-annotations-live");
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        string Picture(string name,params string[] lines){var p=Path.Combine(output,name+".png");using var b=new Bitmap(1000,100+lines.Length*64);using var g=Graphics.FromImage(b);using var f=new Font("Microsoft YaHei",30,GraphicsUnit.Pixel);g.Clear(Color.White);for(var i=0;i<lines.Length;i++)g.DrawString(lines[i],f,Brushes.Black,35,30+i*64);b.Save(p,ImageFormat.Png);return p;}
        var question=Picture("question","长方形长7厘米，宽11厘米，求面积。");var answer=Picture("answer","已知矩形长7厘米、宽11厘米。","面积=7×11=77平方厘米。");var note=Picture("note","先检查长度单位，再计算。","1米=100厘米。");
        var settings=live?WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json"):new LanguageSettings();using var server=new HttpListener();Task? serving=null;
        if(!live)
        {
            var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();settings=settings with{ApiBaseUrl=$"http://127.0.0.1:{port}",ImageModel="all-fixture",Model="all-fixture"};server.Prefixes.Add(settings.ApiBaseUrl+"/");server.Start();
            serving=Task.Run(async()=>{while(server.IsListening)try{var c=await server.GetContextAsync();string response;
                if(c.Request.Url!.AbsolutePath.EndsWith("/models"))response="{\"data\":[{\"id\":\"all-fixture\",\"input_modalities\":[\"text\",\"image\"]}]}";
                else{using var payload=JsonDocument.Parse(await new StreamReader(c.Request.InputStream).ReadToEndAsync());using var input=JsonDocument.Parse(payload.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("text").GetString()!);var s=input.RootElement;
                    string Note(string field){var v=s.GetProperty(field).GetString()!;return string.IsNullOrWhiteSpace(v)?v:Regex.Replace(v,@"!\[[^\]]*\]\(asset:[a-f0-9]{64}\)","检查长度单位，1米=100厘米。").Replace("口语","规范");}
                    var result=JsonSerializer.Serialize(new{cards=new[]{new{front="已知矩形长7厘米、宽11厘米，求面积。",back="矩形面积 $S=7\\times11=77$ 平方厘米。",frontAnnotation=Note("frontAnnotation"),annotation=Note("annotation"),typeId="fast-answer"}}});response="data: "+JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=result},finish_reason="stop"}}})+"\n\ndata: [DONE]\n\n";c.Response.ContentType="text/event-stream";}
                await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response));c.Response.Close();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        }
        try
        {
            for(var sample=0;sample<(annotationsOnly?1:4);sample++)
            {
                var material=new CaptureMaterial("公开全部书面化验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);var chat=new ChatSession{Model=live?settings.ImageModel:"all-fixture"};
                string Image(string p){var a=new ImageAsset(p,ImageAsset.Hash(File.ReadAllBytes(p)),"question",chat.Assets.Count+1);if(!chat.Assets.Any(i=>i.Sha256==a.Sha256))chat.Assets.Add(a);return "![图片](asset:"+a.Sha256+")";}
                var card=new CardComposition{Front=sample==2?"":sample==0?"长7宽11的矩形面积怎么算？":Image(question),Back=sample==1||sample==3?"":sample==2?Image(answer):"7乘11等于77平方厘米。",FrontAnnotation=sample==0||sample==1?"正注前文。口语\n\n"+Image(note)+"\n\n正注后文。":"正面口语批注：需要核对单位。",Annotation=sample==1?"":"背注前文。口语\n\n"+Image(note)+"\n\n背注后文。",FrontAnnotationEnabled=true,AnnotationEnabled=true};chat.Compositions.Add(card);
                // Restored cards can retain provenance from a different message graph.
                card.FrontSources.Add("previous-question-"+sample);card.BackSources.Add("previous-answer-"+sample);
                var draft=new ClipDraft{Original=material,Material=material,Settings=settings,Chat=chat,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes"))};store.Save(draft);File.WriteAllText(Path.Combine(output,$"sample-{sample}-draft-id.txt"),draft.Id);
                var form=(Form)desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single().Invoke([draft,store,data,AppContext.BaseDirectory,(Func<string>)(()=>live?ContentCredentials.Resolve():"fixture"),(Action)(()=>{})]);form.Show();
                try
                {
                    await Ready(form);await Wait(form,"document.querySelector('select[aria-label=\"辅助模型\"]').options.length>1&&document.querySelector('.auxiliary-settings').getAttribute('aria-busy')==='false'");
                    if(live&&sample==3){await SelectAuxiliary(form,"辅助服务","codex");await Wait(form,"Array.from(document.querySelector('select[aria-label=\"辅助模型\"]').options).some(o=>o.value==='gpt-6.1-sol')&&document.querySelector('.auxiliary-settings').getAttribute('aria-busy')==='false'");await SelectAuxiliary(form,"辅助模型","gpt-6.1-sol");await SelectAuxiliary(form,"辅助推理","low");}
                    string Fields()=>JsonSerializer.Serialize(new{card.Front,card.Back,card.FrontAnnotation,card.Annotation},WorkflowJson.Options);
                    var before=Fields();var main=JsonSerializer.Serialize(new{chat.Binding,chat.Messages,chat.Model,chat.Prompts},WorkflowJson.Options);
                    async Task Apply(string label,string scope="document")
                    {
                        var count=chat.Proposals.Count;await Button(form,label,scope);var timer=System.Diagnostics.Stopwatch.StartNew();var stage="";
                        while(timer.Elapsed.TotalSeconds<240){var current=chat.Auxiliary!.Current.Binding.Stage;if(current!=stage){stage=current;Console.WriteLine($"ALL {sample} {label} {stage}");}var p=chat.Proposals.Skip(count).SingleOrDefault();if(p?.Status=="adopted")break;if(p?.Status is "failed" or "stale" or "unparsed" or "incomplete")throw new Exception(p.Error);var alert=await Js(form,"document.querySelector('[role=alert]')?.textContent||''");if(alert!="\"\"")throw new Exception("辅助操作失败："+alert);await Task.Delay(60);}
                        Check(chat.Proposals.Count==count+1&&chat.Proposals.Last().Status=="adopted","一次操作直接替换，无候选："+sample+label);await Wait(form,"document.querySelectorAll('.proposal').length===0&&document.querySelector('textarea[aria-label=\"卡片背面\"]').value==="+JsonSerializer.Serialize(card.Back));
                    }
                    if(!annotationsOnly){await Apply("全部书面化");
                    Check(card.Front.Contains('7')&&card.Front.Contains("11")&&!card.Front.Contains("77")&&card.Back.Contains("77"),"全部书面化保留题设并生成/整理答案，缺少任一面自动补齐："+sample);
                    Check((sample>1||card.FrontAnnotation.Contains("100")&&!card.FrontAnnotation.Contains("asset:"))&&(sample==1?card.Annotation=="":card.Annotation.Contains("100")&&!card.Annotation.Contains("asset:")),"两面批注图片均实际识别，空批注不新增："+sample);
                    await Shot(form,"all-applied-"+sample);WorkflowJson.Save(Path.Combine(output,$"sample-{sample}-applied.json"),new{live,card,actions=chat.Proposals.Select(p=>new{p.Action,p.Status})});
                    await CtrlUndo(form,"卡片背面");Check(Fields()==before,"一次 Ctrl+Z 完整恢复正面、背面及双面图片批注："+sample);}
                    if(sample==0||sample==3)
                    {
                        foreach(var side in new[]{"front","back"})
                        {
                            var f=card.Front;var b=card.Back;var fn=card.FrontAnnotation;var bn=card.Annotation;
                            await Apply("书面化",$"document.querySelectorAll('.face')[{(side=="front"?0:1)}].querySelector('.annotation-tools')");
                            var originalNote=side=="front"?fn:bn;var resultNote=side=="front"?card.FrontAnnotation:card.Annotation;
                            Check(card.Front==f&&card.Back==b&&(side=="front"?card.Annotation==bn:card.FrontAnnotation==fn)&&(originalNote.Contains("asset:")?resultNote.Contains("100")&&!resultNote.Contains("asset:"):resultNote.Contains("单位")),"单独书面化只改变当前批注，兼容图片并锁定其他字段："+sample+side);
                            await Js(form,$"document.querySelectorAll('.face')[{(side=="front"?0:1)}].querySelector('.annotation-tools').scrollIntoView({{block:'center'}})");
                            await Shot(form,"annotation-applied-"+sample+side);
                            await Button(form,"撤销上次编辑");await Task.Delay(160);Check(Fields()==before,"撤销按钮恢复单独批注书面化："+sample+side);
                        }
                    }
                    if(sample==0)
                    {
                        foreach(var width in new[]{1240,768,414,375,320})
                        {
                            var scale=form.DeviceDpi/96d;form.MinimumSize=new(1,1);form.ClientSize=new((int)(width*scale),(int)(1050*scale));await Task.Delay(160);
                            var geometry=await Js(form,"(()=>{const t=document.querySelector('.card-tools'),r=t.getBoundingClientRect(),p=t.querySelector('.preview-action'),b=p.getBoundingClientRect();return {fit:document.documentElement.scrollWidth<=innerWidth,oldAbsent:![...t.children].some(e=>e.textContent==='从所选消息重新生成'),previewLast:p===t.lastElementChild,previewRight:Math.abs(r.right-b.right)<2,notes:[...document.querySelectorAll('.annotation')].every(n=>{const a=n.querySelector('textarea').getBoundingClientRect(),z=n.querySelector('button').getBoundingClientRect();return z.top>=a.bottom&&Math.abs(z.right-a.right)<2;})};})()");File.WriteAllText(Path.Combine(output,$"controls-{width}.json"),geometry);using var j=JsonDocument.Parse(geometry);
                            Check(j.RootElement.EnumerateObject().All(v=>v.Value.GetBoolean()),"预览独立靠最右，双面批注书面化位于框右下角，无溢出："+width);await Js(form,"document.querySelector('.annotation-tools').scrollIntoView({block:'center'})");await Shot(form,"controls-"+width);
                        }
                    }
                    Check(main==JsonSerializer.Serialize(new{chat.Binding,chat.Messages,chat.Model,chat.Prompts},WorkflowJson.Options)&&store.Find(draft.Id)!.Chat!.Compositions[0].FrontAnnotation==card.FrontAnnotation,"辅助动作不改主对话，四字段与撤销记录持久保存："+sample);
                }
                finally{form.Dispose();}
            }
            Check(errors.Count==0,"全部与单批注书面化无 JavaScript 异常");
        }
        finally{if(server.IsListening)server.Stop();if(serving!=null)await serving;}
    }
    static async Task AnnotationReadbackUi(string priorOutput)
    {
        data=Path.Combine(priorOutput,"fixture");store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        var id=File.ReadAllText(Path.Combine(priorOutput,"sample-0-draft-id.txt"));
        Form Open(ClipDraft d)=>(Form)desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single()
            .Invoke([d,store,data,AppContext.BaseDirectory,(Func<string>)(()=>ContentCredentials.Resolve()),(Action)(()=>{})]);
        var draft=store.Find(id)!;var chat=draft.Chat!;var card=chat.Compositions[0];
        string Fields()=>JsonSerializer.Serialize(new{card.Front,card.Back,card.FrontAnnotation,card.Annotation,card.FrontSources,card.BackSources},WorkflowJson.Options);
        string Turns(ChatSession s)=>JsonSerializer.Serialize(new{s.Messages,s.Prompts,s.Proposals,s.Auxiliary},WorkflowJson.Options);
        var original=Fields();var turns=Turns(chat);var result=chat.Proposals.Last().Cards.Single().Annotation;
        Check(chat.Messages.Count==0&&card.FrontSources.Count==1&&card.BackSources.Count==1&&chat.Proposals.Count==2&&chat.Proposals.All(p=>p.Status=="adopted"),"重开原两次真实批注调用，缺失来源及原输出完整保留");
        using(var form=Open(draft))
        {
            form.Show();await Ready(form);await Button(form,"重做上次编辑");
            await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]').value==="+JsonSerializer.Serialize(result));
            Check(card.Annotation==result&&result.Contains("100")&&!result.Contains("asset:"),"重开后重做恢复真实 API 识图书面化结果，无需再次调用模型");
            await Js(form,"document.querySelectorAll('.face')[1].querySelector('.annotation-tools').scrollIntoView({block:'center'})");await Shot(form,"annotation-real-result");
            await CtrlUndo(form,"卡片批注");Check(Fields()==original,"批注框内 Ctrl+Z 精确恢复原图片批注、正文及历史来源");
            Check(Turns(chat)==turns,"重做、Ctrl+Z 与预览回读不产生新辅助回合或改动主对话");
        }
        draft=store.Find(id)!;chat=draft.Chat!;card=chat.Compositions[0];
        using(var form=Open(draft))
        {
            form.Show();await Ready(form);
            Check(Fields()==original&&Turns(chat)==turns&&card.RedoHistory.Count>0,"再次冷重开保留原稿、来源、重做链和原两次模型回合");
        }
        Check(errors.Count==0,"批注真实结果回读及撤销重做无 JavaScript 异常");
    }
    static async Task AllFormalizeReadbackUi(string priorOutput)
    {
        data=Path.Combine(priorOutput,"fixture");store=new(data);desktop=Assembly.Load("KnowledgeClipper");var completed=0;
        for(var sample=0;sample<4;sample++)
        {
            var draft=store.Find(File.ReadAllText(Path.Combine(priorOutput,$"sample-{sample}-draft-id.txt")))!;var chat=draft.Chat!;
            string Snapshot()=>JsonSerializer.Serialize(new{cards=chat.Compositions,proposals=chat.Proposals,
                sessions=chat.Auxiliary!.Sessions.Select(s=>new{s.Key,s.Value.Binding.ThreadId,s.Value.Binding.TurnId,prompts=s.Value.Prompts,messages=s.Value.Messages})},WorkflowJson.Options);
            var before=Snapshot();completed+=chat.Proposals.Count(p=>p.Status=="adopted");
            var form=(Form)desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single().Invoke([draft,store,data,AppContext.BaseDirectory,(Func<string>)(()=>ContentCredentials.Resolve()),(Action)(()=>{})]);form.Show();
            try{await Ready(form);await Wait(form,"document.querySelectorAll('.annotation-tools button').length===2&&Array.from(document.querySelectorAll('.card-tools button')).some(b=>b.textContent==='全部书面化')");await Task.Delay(200);
                Check(before==Snapshot()&&chat.Proposals.All(p=>p.Status=="adopted"),"最终构建重开原真实回合，原生 Thread/Turn、输入快照、原稿与撤销记录不变，无重发："+sample);await Shot(form,"readback-"+sample);
                await Js(form,"document.querySelector('.card-tools').scrollIntoView({block:'center'})");await Shot(form,"readback-actions-"+sample);}
            finally{form.Dispose();}
        }
        Check(completed==8&&errors.Count==0,"完整回读原 8 个真实 API/Codex 回合，无新增生成或 JavaScript 异常");
        WorkflowJson.Save(Path.Combine(output,"readback-receipt.json"),new{passed=true,completedRealActions=completed,generationCalls=0,priorOutput,desktop=desktop.Location,workflow=typeof(ChatComposition).Assembly.Location});
    }
}
