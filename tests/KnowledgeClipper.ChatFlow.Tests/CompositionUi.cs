using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task CompositionUi(string repo)
    {
        Directory.CreateDirectory(data); store=new(data); desktop=Assembly.Load("KnowledgeClipper");
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();var modelCalls=0;
        var serving=Task.Run(async()=>{while(server.IsListening)try{
            var request=await server.GetContextAsync();
            if(!request.Request.Url!.AbsolutePath.EndsWith("/models"))Interlocked.Increment(ref modelCalls);
            var bytes=Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"public-fixture\"}]}");
            await request.Response.OutputStream.WriteAsync(bytes);request.Response.Close();
        }catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        var material=new CaptureMaterial("公开卡片追加验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        var png=Path.Combine(data,"public-pixel.png");File.WriteAllBytes(png,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
        var asset=new ImageAsset(png,ImageAsset.Hash(File.ReadAllBytes(png)),"answer",1);
        var draft=new ClipDraft{Original=material,Material=material,Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}",Model="public-fixture"},Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Chat=new(){Model="public-fixture"}};
        for(var i=0;i<8;i++)draft.Chat.Messages.Add(new(){Id=$"composition-{i}",Role=i%2==0?"user":"assistant",TypeId="fast-answer",Parts=[new("text",$"独立材料 {i}：$x={i+2}$。\n\n第二段 {i}。")]});
        draft.Chat.Messages[0].Parts=[new("text",@"前缀文字 \(\frac{1}{2}+x^2\) 后缀文字。"+"\n\n第二段：保留未选部分。")];
        draft.Chat.Messages[6].Parts=[new("text","# 公开公式\n\n$$\n\\begin{cases}x=2\\\\y=3\\end{cases}\n$$\n\n代码 `$literal$` 与金额 $5、$10。")];
        draft.Chat.Messages.Add(new(){Id="composition-8",Role="assistant",Action="formalize",Parts=[new("text",@"辅助材料：\[E=mc^2\]。同式 \(x=2\) 与 $x=2$。") ]});
        draft.Chat.Messages[7].Parts.Add(new("image",Image:asset));draft.Chat.Assets.Add(asset);draft.Chat.Compositions.Add(new());store.Save(draft);
        var copied=new List<string>();
        Form Open(ClipDraft saved){var opened=(Form)desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single(c=>c.GetParameters().Length==6).Invoke([saved,store,data,AppContext.BaseDirectory,(Func<string>)(()=>"fixture"),(Action)(()=>{})]);opened.GetType().GetProperty("WriteClipboardText",Flags)!.SetValue(opened,(Action<string>)(text=>copied.Add(text)));return opened;}
        var form=Open(draft);form.Show();await Ready(form);var card=draft.Chat.Compositions[0];
        string Source(int i)=>ChatComposition.Material(draft.Chat.Messages[i]);
        async Task Add(int index,string side)
        {
            var pos=side=="front"?0:1;
            await Js(form,$"document.querySelectorAll('.message')[{index}].querySelectorAll('.message-actions button')[{pos}].click()");
            await Wait(form,$"document.querySelectorAll('.message')[{index}].querySelectorAll('.message-actions button')[{pos}].getAttribute('aria-pressed')==='true'");
        }
        async Task Edit(string side,string text)
        {
            await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label="+JsonSerializer.Serialize(side=="front"?"卡片正面":"卡片背面")+"]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,"+JsonSerializer.Serialize(text)+");t.dispatchEvent(new Event('input',{bubbles:true}));})()");
            text=ChatMarkdown.NormalizeMediaReferences(text,draft.Chat.Assets);
            await Wait(form,"document.querySelector('textarea[aria-label="+JsonSerializer.Serialize(side=="front"?"卡片正面":"卡片背面")+"]').value==="+JsonSerializer.Serialize(text));
            var timer=System.Diagnostics.Stopwatch.StartNew();while((side=="front"?card.Front:card.Back)!=text){if(timer.Elapsed.TotalSeconds>10)throw new Exception("Manual edit not persisted");await Task.Delay(30);}
        }
        try
        {
            await Add(0,"front");Check(card.Front==Source(0)&&!card.ManuallyEdited,"第一次加入正面写入实际正文");
            await Js(form,"(()=>{const t=document.querySelector('.card-editor select');t.value='chunk-solution';t.dispatchEvent(new Event('change',{bubbles:true}));})()");
            await Wait(form,"document.querySelector('.card-editor select').value==='chunk-solution'");
            Check(!card.ManuallyEdited&&!card.SourcesChanged,"仅切换主要提问类型不误标手工正文");
            await Add(2,"front");await Add(1,"back");await Add(3,"back");
            Check(card.Front==Source(0)+"\n\n"+Source(2)&&card.Back==Source(1)+"\n\n"+Source(3),"正反面连续加入多个多段消息，实际正文与来源一致");
            await Edit("front",card.Front+"\n\n手工补充：保留这段备注。");var manualFront=card.Front;
            await Add(4,"front");await Add(5,"back");
            Check(card.Front==manualFront+"\n\n"+Source(4)&&card.Back==Source(1)+"\n\n"+Source(3)+"\n\n"+Source(5)&&!card.SourcesChanged,"手工编辑后仍能追加正反面，保留全部原文且无虚假来源警告");
            var beforeBack=card.Back;var updatedFront=card.Front+"\n\n同轮编辑正面。";
            await Js(form,"(()=>{document.querySelectorAll('.message')[7].querySelectorAll('.message-actions button')[1].click();const t=document.querySelector('textarea[aria-label=\"卡片正面\"]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,"+JsonSerializer.Serialize(updatedFront)+");t.dispatchEvent(new Event('input',{bubbles:true}));})()");
            await Wait(form,"document.querySelector('textarea[aria-label=\"卡片背面\"]').value.includes('asset:')");
            Check(card.Front==updatedFront&&card.Back==beforeBack+"\n\n"+Source(7),"快速加入背面并编辑正面，不用旧整卡状态覆盖新内容；图片引用保留");
            await Button(form,"预览卡片");await Wait(form,"document.querySelector('.card-preview img')?.naturalWidth>0");
            Check(await Js(form,"document.querySelectorAll('.card-preview .katex').length>=6")=="true","完整正文预览成功，公式和图片显示，无正反面缺内容错误");
            await Shot(form,"composed-card");
            await Button(form,"撤销上次编辑");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').value==="+JsonSerializer.Serialize(manualFront+"\n\n"+Source(4)));
            Check(card.Back.Contains("asset:"),"撤销正面编辑不丢失此前加入背面的图片");
            // A deselected edited source retains its body; selecting it again must not duplicate that block.
            await Js(form,"document.querySelectorAll('.message')[5].querySelectorAll('.message-actions button')[1].click()");
            await Wait(form,"document.querySelectorAll('.message')[5].querySelectorAll('.message-actions button')[1].getAttribute('aria-pressed')==='false'");
            var keptBack=card.Back;await Add(5,"back");Check(card.Back==keptBack,"取消后重新加入同一来源不重复追加已有完整消息块");
            async Task<string> CopyRange(string selection)
            {
                var result=await Js(form,"(()=>{"+selection+"const s=getSelection();s.removeAllRanges();s.addRange(r);const d=new DataTransfer();const e=new ClipboardEvent('copy',{bubbles:true,cancelable:true,clipboardData:d});document.dispatchEvent(e);return {plain:d.getData('text/plain'),markdown:d.getData('text/markdown'),html:d.getData('text/html'),handled:e.defaultPrevented};})()");
                using var value=JsonDocument.Parse(result);Check(value.RootElement.GetProperty("handled").GetBoolean()&&value.RootElement.GetProperty("html").GetString()==""&&value.RootElement.GetProperty("plain").GetString()==value.RootElement.GetProperty("markdown").GetString(),"选区复制写入原文格式，不提供渲染 HTML");return value.RootElement.GetProperty("plain").GetString()!;
            }
            var inline=await CopyRange("const p=document.querySelector('[data-message-id=\"composition-0\"] p');const r=document.createRange();r.selectNodeContents(p);");
            Check(inline==draft.Chat.Messages[0].Text.Split("\n\n")[0],"文字加分式公式复制精确保留原始括号 LaTeX，不包含未选段落");
            var partial=await CopyRange("const p=document.querySelector('[data-message-id=\"composition-0\"] p');const r=document.createRange();r.setStart(p.firstChild,2);r.setEnd(p.lastChild,3);");
            Check(partial=="文字 \\(\\frac{1}{2}+x^2\\) 后缀","选区从段落中间开始和结束，准确保留边界文字及完整 LaTeX");
            var atomic=await CopyRange("const t=document.querySelector('[data-message-id=\"composition-0\"] .katex-html .mord');const r=document.createRange();r.selectNodeContents(t);");
            Check(atomic==@"\(\frac{1}{2}+x^2\)","选中公式局部仍复制完整原始公式，不复制视觉符号或重复 MathML");
            var display=await CopyRange("const p=document.querySelector('[data-message-id=\"composition-6\"] .katex-display');const r=document.createRange();r.selectNodeContents(p);");
            Check(display=="$$\n\\begin{cases}x=2\\\\y=3\\end{cases}\n$$","独立公式保留双美元、多行及反斜杠");
            var bracket=await CopyRange("const p=document.querySelector('[data-message-id=\"composition-8\"] .katex-display');const r=document.createRange();r.selectNodeContents(p);");
            Check(bracket==@"\[E=mc^2\]","括号独立公式准确回到原始反斜杠定界符");
            var repeats=await CopyRange("const p=document.querySelector('[data-message-id=\"composition-8\"]');const r=document.createRange();r.selectNodeContents(p);");
            Check(repeats.Contains(@"\(x=2\)")&&repeats.Contains("$x=2$"),"重复相同公式仍按源码顺序保留各自不同的原始定界符");
            var literals=await CopyRange("const p=document.querySelector('[data-message-id=\"composition-6\"]');const r=document.createRange();r.selectNodeContents(p);");
            Check(literals.Contains("`$literal$`")&&literals.Contains("$5、$10"),"复制代码及金额不把美元符号误当作公式");
            var cross=await CopyRange("const p=document.querySelector('[data-message-id=\"composition-0\"]');const q=document.querySelector('[data-message-id=\"composition-1\"]');const r=document.createRange();r.setStart(p,0);r.setEnd(q,q.childNodes.length);");
            Check(cross.Contains(@"\(\frac{1}{2}+x^2\)")&&cross.Contains("$x=3$")&&!cross.Contains("独立材料 2"),"跨消息选区按顺序保留文字和各自原始公式");
            await Button(form,"复制整条","document.querySelectorAll('.message')[7]");await Task.Delay(100);
            Check(copied.Last()==Source(7),"复制整条与加入卡面使用同一原始消息材料，含图片引用");
            await Button(form,"复制整条","document.querySelectorAll('.message')[8]");await Task.Delay(100);Check(copied.Last()==Source(8),"辅助回答也可复制整条原始材料");
            async Task Paste(string label)
            {
                var base64=Convert.ToBase64String(File.ReadAllBytes(png));
                await Js(form,"(()=>{const t=document.querySelector('textarea[aria-label="+JsonSerializer.Serialize(label)+"]');t.focus();t.setSelectionRange(t.value.length,t.value.length);const d=new DataTransfer();d.items.add(new File([Uint8Array.from(atob("+JsonSerializer.Serialize(base64)+"),c=>c.charCodeAt(0))],'public.png',{type:'image/png'}));t.dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:d}));})()");
            }
            var beforeFront=card.Front;beforeBack=card.Back;await Paste("卡片正面");
            await Wait(form,"document.querySelector('textarea[aria-label=\"卡片正面\"]').value.includes('asset:')");
            Check(card.Front.StartsWith(beforeFront)&&card.Back==beforeBack,"正面直接粘贴图片，在光标处插入真实素材引用，其他卡面不变");
            var frontWithImage=card.Front;await Paste("卡片背面");await Task.Delay(250);
            Check(card.Back.StartsWith(beforeBack)&&card.Back.Length>beforeBack.Length&&card.Front==frontWithImage,"背面直接粘贴图片保留文字和正面内容");
            await Button(form,"撤销上次编辑");await Task.Delay(150);
            Check(card.Back==beforeBack&&card.Front==frontWithImage,"撤销背面补图恢复补图前正文，正面图片保持不变");
            await Button(form,"撤销上次编辑");await Task.Delay(150);
            Check(card.Front==beforeFront&&card.Back==beforeBack,"继续撤销正面补图恢复两面原正文");
            await Paste("卡片正面");await Task.Delay(150);frontWithImage=card.Front;
            await Paste("卡片背面");await Task.Delay(150);
            var localUrl="https://clipper-media.local/"+asset.Sha256+".png";
            var beforeDropBack=card.Back;
            await Edit("front",card.Front+"\n\n"+localUrl);
            await Wait(form,"!document.querySelector('textarea[aria-label=\"卡片正面\"]').value.includes('https://clipper-media.local/')");
            Check(card.Front.EndsWith("![图片](asset:"+asset.Sha256+")")&&card.Back==beforeDropBack,"拖拽带入正面裸链接转换为原素材引用，背面不变");
            frontWithImage=card.Front;
            await Edit("back",card.Back+"\n\n![拖入]("+localUrl+")");
            await Wait(form,"!document.querySelector('textarea[aria-label=\"卡片背面\"]').value.includes('https://clipper-media.local/')");
            Check(card.Back.EndsWith("![图片](asset:"+asset.Sha256+")")&&card.Front==frontWithImage,"背面兼容拖拽 Markdown 图片链接，正面不变");
            await Button(form,"预览卡片");await Wait(form,"document.querySelectorAll('.card-preview img').length>=2&&[...document.querySelectorAll('.card-preview img')].every(i=>i.naturalWidth>0)");
            Check(await Js(form,"[...document.querySelectorAll('.card-preview img')].every(i=>i.naturalWidth>0)")=="true","拖拽兼容转换后双面预览真实图片");
            await Input(form,"带图片的文字问题");await Paste("问题");await Wait(form,"document.querySelectorAll('.attachments img').length===1");
            Check(draft.Chat.Input=="带图片的文字问题"&&draft.Chat.Attachments[0].ReadVerified().Length>0,"提问框直接粘贴图片，图文混合输入和素材哈希有效");
            Check(await Js(form,"(()=>{const faces=document.querySelectorAll('.face');return Array.from(faces[0].querySelectorAll('.face-title button')).map(b=>b.textContent).join('|')==='生成书面化问题|添加批注|补充图片|图转文字'&&Array.from(faces[1].querySelectorAll('.face-title button')).map(b=>b.textContent).join('|')==='生成书面化答案|添加批注|补充图片|图转文字';})()") == "true","正反面各四个操作，书面化、批注、补图及图转文字顺序一致");
            await Button(form,"添加批注","document.querySelectorAll('.face')[1]");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]')!==null");
            var annotatedFront=card.Front;var annotatedBack=card.Back;var annotation="我的批注：$x^2$。\n\n- 第一层\n  - 下一层";
            await Program.Edit(form,"卡片批注",annotation);await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]').value==="+JsonSerializer.Serialize(annotation));
            Check(card.Annotation==annotation&&card.Front==annotatedFront&&card.Back==annotatedBack,"独立批注文本框输入立即持久保存，不覆盖正文或正面");
            await Button(form,"预览卡片");await Wait(form,"document.querySelector('.card-preview')?.textContent.includes('批注：')&&document.querySelector('.card-preview hr')!==null");
            Check(await Js(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]').getBoundingClientRect().top>document.querySelector('textarea[aria-label=\"卡片背面\"]').getBoundingClientRect().bottom") == "true","批注输入框位于背面正文下方，预览显示分割线和批注内容");
            await Button(form,"撤销上次编辑");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]').value===''");
            Check(card.Back==annotatedBack&&card.AnnotationEnabled,"撤销批注保留背面原图、正文和已展开输入框");
            await Button(form,"撤销上次编辑");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]')===null");
            Check(!card.AnnotationEnabled&&card.Back==annotatedBack,"继续撤销添加批注关闭空输入框，正文不变");
            await Button(form,"添加批注","document.querySelectorAll('.face')[1]");await Wait(form,"document.querySelector('textarea[aria-label=\"卡片批注\"]')!==null");
            await Program.Edit(form,"卡片批注",annotation);await Task.Delay(150);
            foreach(var width in new[]{1240,768,414,375,320})
            {
                var scale=form.DeviceDpi/96d;form.MinimumSize=new(1,1);form.ClientSize=new((int)(width*scale),(int)(900*scale));await Task.Delay(160);
                Check(await Js(form,"document.documentElement.scrollWidth<=innerWidth&&Array.from(document.querySelectorAll('.face-title button')).every(b=>b.scrollWidth<=b.clientWidth&&b.getBoundingClientRect().right<=innerWidth)") == "true","卡面操作和批注在宽度 "+width+" 无横向溢出或按钮截断");
                await Shot(form,"annotation-"+width);
            }
            keptBack=card.Back;
            var restored=store.Find(draft.Id)!;form.Close();form=Open(restored);form.Show();await Ready(form);card=restored.Chat!.Compositions[0];
            Check(card.Front.Contains("手工补充")&&card.Back==keptBack&&card.Annotation==annotation&&card.AnnotationEnabled,"重开草稿保留来源、手工稿、追加正文与批注");
            form.Close();
            var legacy=new ClipDraft{Original=material,Material=material,Settings=draft.Settings,Configuration=draft.Configuration,Chat=new(){Model="public-fixture",Messages=draft.Chat.Messages,Assets=[asset],Compositions=[new(){ManuallyEdited=true,SourcesChanged=true}]}};
            store.Save(legacy);draft=legacy;form=Open(legacy);form.Show();await Ready(form);card=legacy.Chat.Compositions[0];
            await Add(1,"back");await Add(0,"front");await Add(3,"back");await Add(2,"front");
            Check(card.Front==Source(0)+"\n\n"+Source(2)&&card.Back==Source(1)+"\n\n"+Source(3)&&!card.SourcesChanged,"旧版空手工稿先加背面再加正面，连续追加恢复正常并清除空来源的旧警告");
            await Button(form,"预览卡片");await Wait(form,"document.querySelector('.card-preview')!==null");
            Check(modelCalls==0&&errors.Count==0,"选取、编辑、预览、撤销和重开零生成调用、零 JavaScript 异常");
        }
        finally{form.Dispose();server.Stop();await serving;}
    }
}
