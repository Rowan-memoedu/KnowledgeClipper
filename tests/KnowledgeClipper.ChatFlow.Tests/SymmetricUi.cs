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
    static async Task SymmetricUi(string repo,bool live)
    {
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        string Picture(string name,params string[] lines)
        {
            var path=Path.Combine(output,name+".png");using var bitmap=new Bitmap(1050,100+lines.Length*64);using var g=Graphics.FromImage(bitmap);using var font=new Font("Microsoft YaHei",30,GraphicsUnit.Pixel);
            g.Clear(Color.White);for(var i=0;i<lines.Length;i++)g.DrawString(lines[i],font,Brushes.Black,35,30+i*64);bitmap.Save(path,ImageFormat.Png);return path;
        }
        var question=Picture("question","一块长方形木板，长9厘米，宽5厘米。","求这块木板的面积。");
        var answer=Picture("answer","矩形长9厘米，宽5厘米。","解：面积=9×5=45平方厘米。");
        var note=Picture("note","计算前检查长度单位。","1米=100厘米。");
        var settings=live?WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json"):new LanguageSettings();
        using var server=new HttpListener();Task? serving=null;var requests=0;
        if(!live)
        {
            var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();settings=settings with{ApiBaseUrl=$"http://127.0.0.1:{port}",Model="symmetric-fixture",ImageModel="symmetric-fixture"};
            server.Prefixes.Add(settings.ApiBaseUrl+"/");server.Start();serving=Task.Run(async()=>{while(server.IsListening)try{
                var c=await server.GetContextAsync();string response;
                if(c.Request.Url!.AbsolutePath.EndsWith("/models"))response="{\"data\":[{\"id\":\"symmetric-fixture\",\"input_modalities\":[\"text\",\"image\"]}]}";
                else
                {
                    requests++;using var payload=JsonDocument.Parse(await new StreamReader(c.Request.InputStream).ReadToEndAsync());using var snapshot=JsonDocument.Parse(payload.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("text").GetString()!);var s=snapshot.RootElement;var action=s.GetProperty("action").GetString();
                    var f=action=="formalize"?"矩形长9厘米、宽5厘米，求面积。":s.GetProperty("front").GetString()!;
                    var b=action=="formalize-answer"?"矩形面积 $S=9\\times5=45$ 平方厘米。":s.GetProperty("back").GetString()!;
                    var n=Regex.Replace(s.GetProperty("frontAnnotation").GetString()!,@"!\[[^\]]*\]\(asset:[a-f0-9]{64}\)","计算前检查长度单位，1米=100厘米。");
                    var result=JsonSerializer.Serialize(new{cards=new[]{new{front=f,back=b,frontAnnotation=n,typeId="fast-answer"}}});response="data: "+JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=result},finish_reason="stop"}}})+"\n\ndata: [DONE]\n\n";c.Response.ContentType="text/event-stream";
                }
                await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response));c.Response.Close();
            }catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        }
        try
        {
            for(var sample=0;sample<(live?5:4);sample++)
            {
                var material=new CaptureMaterial("公开双面操作验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
                var card=new CardComposition{Front=sample==0?"矩形长9厘米，宽5厘米，求面积。":"",Back=sample==1?"矩形长9厘米、宽5厘米，面积45平方厘米。":"",Annotation="原背面批注 $b$",AnnotationEnabled=true};
                var chat=new ChatSession{Model=live?settings.ImageModel:"symmetric-fixture",Compositions=[card]};
                if(sample>=2){var p=sample==3?answer:question;var a=new ImageAsset(p,ImageAsset.Hash(File.ReadAllBytes(p)),"question",1);chat.Assets.Add(a);if(sample==3)card.Back="![图片](asset:"+a.Sha256+")";else card.Front="![图片](asset:"+a.Sha256+")";}
                var draft=new ClipDraft{Original=material,Material=material,Settings=settings,Chat=chat,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes"))};store.Save(draft);File.WriteAllText(Path.Combine(output,$"sample-{sample}-draft-id.txt"),draft.Id);
                var form=(Form)desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single().Invoke([draft,store,data,AppContext.BaseDirectory,(Func<string>)(()=>live?ContentCredentials.Resolve():"fixture"),(Action)(()=>{})]);form.Show();
                try
                {
                    await Ready(form);await Wait(form,"document.querySelector('select[aria-label=\"辅助模型\"]').options.length>1&&document.querySelector('.auxiliary-settings').getAttribute('aria-busy')==='false'");
                    if(sample==4){await SelectAuxiliary(form,"辅助服务","codex");await Wait(form,"Array.from(document.querySelector('select[aria-label=\"辅助模型\"]').options).some(o=>o.value==='gpt-6.1-sol')&&document.querySelector('.auxiliary-settings').getAttribute('aria-busy')==='false'");await SelectAuxiliary(form,"辅助模型","gpt-6.1-sol");await SelectAuxiliary(form,"辅助推理","low");}
                    async Task Apply(string label,int side)
                    {
                        var before=chat.Proposals.Count;await Button(form,label,$"document.querySelectorAll('.face')[{side}]");var timer=System.Diagnostics.Stopwatch.StartNew();var stage="";
                        while(timer.Elapsed.TotalSeconds<240){var current=chat.Auxiliary!.Current.Binding.Stage;if(current!=stage){stage=current;Console.WriteLine($"SYMMETRIC {sample} {label} {stage}");}var p=chat.Proposals.Skip(before).SingleOrDefault();if(p?.Status=="adopted")break;if(p?.Status is "failed" or "unparsed" or "stale" or "incomplete")throw new Exception(p.Error);await Task.Delay(60);}
                        Check(chat.Proposals.Count==before+1&&chat.Proposals.Last().Status=="adopted","本次操作只执行一次并直接替换："+sample+label);
                        await Wait(form,"document.querySelectorAll('.proposal').length===0&&document.querySelector('textarea[aria-label=\"卡片背面\"]').value==="+JsonSerializer.Serialize(card.Back));
                    }
                    var main=JsonSerializer.Serialize(new{chat.Binding,chat.Messages,chat.Model,chat.Effort},WorkflowJson.Options);var oldFront=card.Front;var oldBack=card.Back;
                    await Apply("生成书面化答案",1);Check(card.Back.Contains("45")&&card.Front==oldFront&&card.Annotation=="原背面批注 $b$","任一面单独存在可生成/整理答案，纯图兼容且不改正面与批注："+sample);
                    await CtrlUndo(form,"卡片背面");Check(card.Front==oldFront&&card.Back==oldBack,"Ctrl+Z 恢复书面化答案前原稿："+sample);
                    if(sample==1){await Apply("生成书面化问题",0);Check(card.Front.Contains('9')&&card.Front.Contains('5')&&!card.Front.Contains("45")&&card.Back==oldBack,"只有答案时可提取问题，原答案不变");await Button(form,"撤销上次编辑");await Task.Delay(150);}
                    if(sample==0||sample==4)
                    {
                        await Button(form,"添加批注","document.querySelectorAll('.face')[0]");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面批注\"]')!==null");await Edit(form,"卡片正面批注","正注前文。\n\n\n\n正注后文。");var bytes=Convert.ToBase64String(File.ReadAllBytes(note));
                        await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label=\"卡片正面批注\"]');t.focus();t.setSelectionRange(7,7);const d=new DataTransfer();d.items.add(new File([Uint8Array.from(atob("+JsonSerializer.Serialize(bytes)+"),c=>c.charCodeAt(0))],'note.png',{type:'image/png'}));t.dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:d}));})()");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面批注\"]').value.includes('asset:')");
                        var originalNote=card.FrontAnnotation;var originalBody=card.Front;Check(card.FrontAnnotationEnabled&&card.Annotation=="原背面批注 $b$","正面展开并粘图，不覆盖背面批注");
                        await Apply("图转文字",0);Check(card.FrontAnnotation.Contains("100")&&!card.FrontAnnotation.Contains("asset:")&&card.FrontAnnotation.Contains("正注前文")&&card.FrontAnnotation.Contains("正注后文")&&(sample==4||card.Front==originalBody)&&card.Annotation=="原背面批注 $b$","正面正文与批注图片均转换，各归原位，无图正文不变");
                        await Button(form,"撤销上次编辑");await Task.Delay(180);Check(card.FrontAnnotation==originalNote&&card.Front==originalBody,"撤销按钮同时恢复正面正文和批注原图");
                        if(sample==0)
                        {
                            await Edit(form,"卡片背面","原答案");await Button(form,"预览卡片");await Wait(form,"document.querySelectorAll('.card-preview img').length>0");Check(await Js(form,"document.querySelector('.card-preview').textContent.includes('正注前文')&&document.querySelector('.card-preview').textContent.includes('原背面批注')")=="true","预览显示双面图文批注");
                            foreach(var width in new[]{1240,1024,901,768,414,375,320})
                            {
                                var scale=form.DeviceDpi/96d;form.MinimumSize=new(1,1);form.ClientSize=new((int)(width*scale),(int)(1050*scale));await Task.Delay(160);
                                var geometry=await Js(form,"(()=>{const rows=[...document.querySelectorAll('.face-actions')];return {pageFits:document.documentElement.scrollWidth<=innerWidth,rows:rows.map(e=>{const r=e.getBoundingClientRect(),bs=[...e.children].map(b=>{const x=b.getBoundingClientRect();return {left:x.left-r.left,right:r.right-x.right,top:x.top,fit:b.scrollWidth<=b.clientWidth}});return {count:bs.length,fit:bs.every(b=>b.fit),fills:[...new Set(bs.map(b=>b.top))].every(y=>Math.abs(bs.filter(b=>b.top===y)[0].left)<2&&Math.abs(bs.filter(b=>b.top===y).at(-1).right)<2)};})};})()");File.WriteAllText(Path.Combine(output,$"layout-{width}.json"),geometry);using var parsed=JsonDocument.Parse(geometry);var g=parsed.RootElement;
                                Check(g.GetProperty("pageFits").GetBoolean()&&g.GetProperty("rows").EnumerateArray().All(r=>r.GetProperty("count").GetInt32()==4&&r.GetProperty("fit").GetBoolean()&&r.GetProperty("fills").GetBoolean()),"各四按钮铺满每行，无空洞或文字截断："+width);await Js(form,"document.querySelector('.face').scrollIntoView({block:'start'})");await Shot(form,"four-actions-"+width);
                            }
                        }
                        var read=store.Find(draft.Id)!.Chat!.Compositions[0];Check(read.FrontAnnotation==originalNote&&read.Annotation=="原背面批注 $b$","重开读回双面独立图片批注");
                    }
                    Check(main==JsonSerializer.Serialize(new{chat.Binding,chat.Messages,chat.Model,chat.Effort},WorkflowJson.Options),"双面操作不改主模型及历史："+sample);WorkflowJson.Save(Path.Combine(output,$"sample-{sample}-acceptance.json"),new{live,card,actions=chat.Proposals.Select(p=>new{p.Action,p.Status}),requests,noExport=true});
                }
                finally{form.Dispose();}
            }
            Check(errors.Count==0,"双面书面化、批注与布局无 JavaScript 异常");
        }
        finally{if(server.IsListening)server.Stop();if(serving!=null)await serving;}
    }
}
