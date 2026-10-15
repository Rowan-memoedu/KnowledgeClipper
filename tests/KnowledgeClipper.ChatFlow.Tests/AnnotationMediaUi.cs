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
    static async Task CtrlUndo(Form form,string label)
    {
        await Js(form,"document.querySelector('textarea[aria-label="+JsonSerializer.Serialize(label)+"]').focus()");
        await Web(form).CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"modifiers\":2,\"key\":\"z\",\"code\":\"KeyZ\",\"windowsVirtualKeyCode\":90}");
        await Web(form).CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyUp\",\"modifiers\":0,\"key\":\"z\",\"code\":\"KeyZ\",\"windowsVirtualKeyCode\":90}");
        await Task.Delay(180);
    }
    static async Task AnnotationMediaUi(string repo,bool live)
    {
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        string Screenshot(string name,params string[] lines)
        {
            var path=Path.Combine(output,name+".png");using var font=new Font("Microsoft YaHei",32,GraphicsUnit.Pixel);
            using var measureBitmap=new Bitmap(1,1);using var measure=Graphics.FromImage(measureBitmap);
            using var bitmap=new Bitmap(Math.Max(900,(int)Math.Ceiling(lines.Max(l=>measure.MeasureString(l,font).Width))+100),120+lines.Length*64);
            using var g=Graphics.FromImage(bitmap);g.Clear(Color.White);
            for(var i=0;i<lines.Length;i++)g.DrawString(lines[i],font,Brushes.Black,45,35+i*64);
            bitmap.Save(path,ImageFormat.Png);return path;
        }
        var bodyPng=Screenshot("body-material","已知矩形两边长 6 厘米和 8 厘米。","其面积为 48 平方厘米。");
        var notePng=Screenshot("annotation-material","先统一长度单位，再计算面积。","换算关系：1 米 = 100 厘米。");
        using var server=new HttpListener();Task? serving=null;var requests=new List<JsonElement>();
        var settings=live?WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json"):new LanguageSettings();
        if(!live)
        {
            var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();var port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
            settings=settings with{ApiBaseUrl=$"http://127.0.0.1:{port}",Model="vision-fixture",ImageModel="vision-fixture"};
            server.Prefixes.Add(settings.ApiBaseUrl+"/");server.Start();
            serving=Task.Run(async()=>{while(server.IsListening)try{
                var context=await server.GetContextAsync();string response;
                if(context.Request.Url!.AbsolutePath.EndsWith("/models"))response="{\"data\":[{\"id\":\"vision-fixture\",\"input_modalities\":[\"text\",\"image\"]}]}";
                else
                {
                    using var payload=JsonDocument.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync());lock(requests)requests.Add(payload.RootElement.Clone());
                    using var snapshot=JsonDocument.Parse(payload.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("text").GetString()!);
                    var input=snapshot.RootElement;var requirement=input.GetProperty("requirements").GetString();
                    if(requirement=="受控延迟")await Task.Delay(1400);
                    string Replace(string field,string replacement)=>Regex.Replace(input.GetProperty(field).GetString()!,@"!\[[^\]]*\]\(asset:[a-f0-9]{64}\)",replacement);
                    var card=new Dictionary<string,string>{{"front","不应替换的问题"},{"back",Replace("back","正文识别：6厘米、8厘米，面积48平方厘米。")},{"typeId","fast-answer"}};
                    if(requirement!="受控缺字段")card["annotation"]=Replace("annotation","批注识别：统一长度单位，1米=100厘米。");
                    var answer=JsonSerializer.Serialize(new{cards=new[]{card}});
                    response="data: "+JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=answer},finish_reason="stop"}}})+"\n\ndata: [DONE]\n\n";
                    context.Response.ContentType="text/event-stream";
                }
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response));context.Response.Close();
            }catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        }
        try
        {
            for(var sample=0;sample<(live?3:4);sample++)
            {
                var material=new CaptureMaterial("公开批注图文验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
                var card=new CardComposition{Front="保留原问题。",Back=sample==0?"正文前文。\n\n\n\n正文后文。":"背面正文保持原样。$F=ma$"};
                var draft=new ClipDraft{Original=material,Material=material,Settings=settings,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Chat=new(){Model=live?settings.ImageModel:"vision-fixture",Compositions=[card]}};
                store.Save(draft);File.WriteAllText(Path.Combine(output,$"sample-{sample}-draft-id.txt"),draft.Id);
                var form=(Form)desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single().Invoke([draft,store,data,AppContext.BaseDirectory,(Func<string>)(()=>live?ContentCredentials.Resolve():"fixture"),(Action)(()=>{})]);
                form.Show();
                try
                {
                    await Ready(form);await Wait(form,"document.querySelector('select[aria-label=\"辅助模型\"]')?.options.length>1&&!document.querySelector('.auxiliary-settings').getAttribute('aria-busy').includes('true')");
                    var chat=draft.Chat!;var main=JsonSerializer.Serialize(new{chat.Binding,chat.Messages,chat.Prompts},WorkflowJson.Options);
                    await Button(form,"添加批注","document.querySelectorAll('.face')[1]");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]')!==null");
                    await Edit(form,"卡片批注","批注前文。\n\n\n\n批注后文。");
                    var beforeNote=card.Annotation;var beforeBody=card.Back;
                    async Task Paste(string label,string path)
                    {
                        var bytes=Convert.ToBase64String(File.ReadAllBytes(path));
                        await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label="+JsonSerializer.Serialize(label)+"]');t.focus();const at=t.value.indexOf('\\n\\n')+2;t.setSelectionRange(at,at);const d=new DataTransfer();d.items.add(new File([Uint8Array.from(atob("+JsonSerializer.Serialize(bytes)+"),c=>c.charCodeAt(0))],'public.png',{type:'image/png'}));t.dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:d}));})()");
                        await Wait(form,"document.querySelector('textarea[aria-label="+JsonSerializer.Serialize(label)+"]').value.includes('asset:')");
                    }
                    if(sample==0)await Paste("卡片背面",bodyPng);
                    await Paste("卡片批注",notePng);
                    Check(card.Annotation.Contains("批注前文")&&card.Annotation.Contains("批注后文")&&card.Annotation.Contains("asset:")&&card.Front=="保留原问题。","批注直接粘贴真实图片，保留光标前后文字与正面");
                    if(sample==0)
                    {
                        await CtrlUndo(form,"卡片批注");Check(card.Annotation==beforeNote&&card.Back.Contains("asset:"),"Ctrl+Z 撤销批注粘图，不误撤背面正文");
                        await Paste("卡片批注",notePng);
                    }
                    var sourceBack=card.Back;var sourceNote=card.Annotation;
                    Check(await Js(form,"!Array.from(document.querySelectorAll('.face')[1].querySelectorAll('button')).find(b=>b.textContent==='图转文字').disabled")=="true","仅批注有图时背面图转文字同样可用");
                    if(live&&sample==2)
                    {
                        await SelectAuxiliary(form,"辅助服务","codex");await Wait(form,"Array.from(document.querySelector('select[aria-label=\"辅助模型\"]').options).some(o=>o.value==='gpt-6.1-sol')");
                        await SelectAuxiliary(form,"辅助模型","gpt-6.1-sol");await SelectAuxiliary(form,"辅助推理","low");
                    }
                    if(!live&&sample>=2)
                    {
                        await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label=\"补充要求\"]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,"+JsonSerializer.Serialize(sample==2?"受控延迟":"受控缺字段")+");t.dispatchEvent(new Event('input',{bubbles:true}));})()");
                        await Task.Delay(100);
                    }
                    await Button(form,"图转文字","document.querySelectorAll('.face')[1]");
                    var timer=System.Diagnostics.Stopwatch.StartNew();
                    if(!live&&sample==2)
                    {
                        while(chat.Proposals.Count==0&&timer.Elapsed.TotalSeconds<10)await Task.Delay(30);
                        await Edit(form,"卡片批注","模型运行期间的新手工批注。");
                    }
                    var stage="";
                    while(timer.Elapsed.TotalSeconds<240)
                    {
                        var p=chat.Proposals.LastOrDefault();
                        var current=chat.Auxiliary!.Current.Binding.Stage;
                        if(current!=stage){stage=current;Console.WriteLine($"ANNOTATION {sample} {stage}");}
                        if(p?.Status is "adopted" or "stale" or "unparsed")break;
                        if(p?.Status is "failed" or "incomplete")throw new Exception(p.Error);
                        await Task.Delay(60);
                    }
                    var proposal=chat.Proposals.Last();
                    await Wait(form,"document.querySelectorAll('.proposal').length===0");
                    if(!live&&sample>=2)
                    {
                        Check(proposal.Status==(sample==2?"stale":"unparsed")&&card.Back==sourceBack&&card.Annotation==(sample==2?"模型运行期间的新手工批注。":sourceNote),"迟到/缺批注字段的结果整次拒绝，原稿与用户新编辑不被覆盖");
                        continue;
                    }
                    Check(proposal.Status=="adopted"&&!card.Annotation.Contains("asset:")&&card.Annotation.Contains("100")&&card.Annotation.Contains("单位"),"正文和批注图片识别后直接替换，不显示候选或模型原回复");
                    await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]').value==="+JsonSerializer.Serialize(card.Annotation)+"&&document.querySelector('textarea[aria-label=\"卡片背面\"]').value==="+JsonSerializer.Serialize(card.Back));
                    Check(card.Front=="保留原问题。"&&(sample!=0?card.Back==sourceBack:card.Back.Contains("48")&&!card.Back.Contains("asset:"))&&card.Annotation.Contains("批注前文")&&card.Annotation.Contains("批注后文")&&!card.Annotation.Contains("48"),"识别文字留在各自区域，纯文本正文逐字保留，前后文衔接完整");
                    Check(main==JsonSerializer.Serialize(new{chat.Binding,chat.Messages,chat.Prompts},WorkflowJson.Options),"共享独立辅助模型，不写入或切换主对话");
                    if(!live)
                    {
                        var payload=requests.Last().GetProperty("messages").EnumerateArray().Last().GetProperty("content");
                        var hashes=payload.EnumerateArray().Where(p=>p.GetProperty("type").GetString()=="image_url").Select(p=>ImageAsset.Hash(Convert.FromBase64String(p.GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1])));
                        Check(hashes.SequenceEqual(ChatComposition.ProposalImages(proposal,chat).Select(a=>a.Sha256)),"正文与批注附件均为真实 PNG 字节，数量、顺序与冻结哈希一致");
                    }
                    await Shot(form,$"sample-{sample}-converted");
                    WorkflowJson.Save(Path.Combine(output,$"sample-{sample}-conversion.json"),new{live,card.Front,card.Back,card.Annotation,proposal.InputSnapshot,proposal.Status});
                    await CtrlUndo(form,"卡片批注");
                    Check(card.Back==sourceBack&&card.Annotation==sourceNote,"一次 Ctrl+Z 恢复正文和批注原图，保留分区及前后文");
                    await Button(form,"图转文字","document.querySelectorAll('.face')[1]");
                    timer.Restart();while((chat.Proposals.Count<2||chat.Proposals.Last().Status!="adopted")&&timer.Elapsed.TotalSeconds<240)await Task.Delay(60);
                    Check(chat.Proposals.Count==2&&chat.Proposals.Last().Status=="adopted","撤销后可再次执行转换，每次独立回合只发送一次");
                    await Button(form,"撤销上次编辑");await Task.Delay(150);
                    Check(card.Back==sourceBack&&card.Annotation==sourceNote,"撤销按钮与 Ctrl+Z 恢复相同完整原稿");
                    var restored=store.Find(draft.Id)!;
                    Check(restored.Chat!.Compositions[0].Annotation==sourceNote&&restored.Chat.Compositions[0].Back==sourceBack,"重开读回完整图片批注与正文");
                }
                finally{form.Dispose();}
            }
            Check(errors.Count==0,"批注图文、自动转换及 Ctrl+Z 无 JavaScript 异常");
        }
        finally{if(server.IsListening)server.Stop();if(serving!=null)await serving;}
    }
}
