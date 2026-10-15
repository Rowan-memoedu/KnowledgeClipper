using System.Diagnostics;
using System.IO;
using System.Text.Json;
using KnowledgeClipper;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class RequirementOptionsChecks
{
    internal static async Task Run(LanguageChatForm form, WebView2 web, ClipDraft draft, string output,
        Func<int> modelRequests, Action<bool,string> check)
    {
        async Task<string> Js(string script)=>await web.CoreWebView2.ExecuteScriptAsync(script);
        async Task Wait(string condition)
        {
            var timer=Stopwatch.StartNew();
            while(await Js(condition)!="true") { if(timer.Elapsed.TotalSeconds>15)throw new Exception("Requirements UI: "+condition); await Task.Delay(25); }
        }
        var before=modelRequests();
        await Wait("document.querySelector('select[aria-label=\"辅助模型\"]')?.options.length>1");
        await Js("(()=>{const s=document.querySelector('select[aria-label=\"辅助模型\"]');s.value='public-fixture';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
        await Wait("document.querySelector('.face-actions button')?.disabled===false");
        check(await Js("document.querySelectorAll('.requirement-options button').length===2&&Array.from(document.querySelectorAll('.requirement-options button')).every(b=>b.getAttribute('aria-pressed')==='false')")=="true","补充要求默认两项均未选中");
        const string freeText="保留中文说明与原始公式 $S=ab$。";
        await Js("(()=>{const t=document.querySelector('textarea[aria-label=\"补充要求\"]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,"+JsonSerializer.Serialize(freeText)+");t.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await Js("document.querySelector('[data-option=mermaid]').click()");
        await Wait("document.querySelector('[data-option=mermaid]').getAttribute('aria-pressed')==='true'");
        check(await Js("document.querySelector('[data-option=clean]').getAttribute('aria-pressed')==='false'")=="true","Mermaid 选项可独立选中");
        await Js("document.querySelector('[data-option=clean]').click()");
        await Wait("document.querySelectorAll('.requirement-options [aria-pressed=true]').length===2");
        check(await Js("(()=>{const c=getComputedStyle(document.querySelector('[data-option=mermaid]')).color.match(/\\d+/g).map(Number);return c[2]>c[1]&&c[1]>c[0]})()")=="true","选中颜色为蓝色");
        await Js("document.querySelector('[data-option=mermaid]').click()");
        await Wait("document.querySelector('[data-option=mermaid]').getAttribute('aria-pressed')==='false'");
        check(await Js("document.querySelector('[data-option=clean]').getAttribute('aria-pressed')==='true'&&document.querySelector('textarea[aria-label=\"补充要求\"]').value==="+JsonSerializer.Serialize(freeText))=="true","取消单项保留另一项和手写要求");
        await Js("document.querySelector('[data-option=mermaid]').focus()");
        await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"key\":\" \",\"code\":\"Space\",\"windowsVirtualKeyCode\":32}");
        await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyUp\",\"key\":\" \",\"code\":\"Space\",\"windowsVirtualKeyCode\":32}");
        await Wait("document.querySelector('[data-option=mermaid]').getAttribute('aria-pressed')==='true'");
        check(modelRequests()==before,"选择、取消和键盘切换选项均不调用模型");
        foreach(var width in new[]{1240,414,320})
        {
            form.MinimumSize=Size.Empty;form.ClientSize=new Size((int)Math.Round(width*form.DeviceDpi/96d),(int)Math.Round(860*form.DeviceDpi/96d));
            await Task.Delay(120);
            await Js("document.querySelector('.supplemental-requirements').scrollIntoView({block:'center'})");
            check(await Js("(()=>{const f=document.querySelector('.requirements-field').getBoundingClientRect();const t=document.querySelector('.requirements-field textarea').getBoundingClientRect();return Array.from(document.querySelectorAll('.requirement-options button')).every(b=>{const r=b.getBoundingClientRect();return r.left>=f.left&&r.right<=f.right&&r.top>=t.bottom&&r.bottom<=f.bottom})})()")=="true",$"宽度 {width} 选项位于文本框底部且未溢出");
            var clip=await Js("(()=>{const r=document.querySelector('.supplemental-requirements').getBoundingClientRect();return {x:Math.max(0,r.x-5),y:Math.max(0,r.y-5),width:Math.min(innerWidth-r.x,r.width+10),height:r.height+10,scale:1}})()");
            using var shot=JsonDocument.Parse(await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot","{\"format\":\"png\",\"clip\":"+clip+"}"));
            File.WriteAllBytes(Path.Combine(output,$"requirements-{width}.png"),Convert.FromBase64String(shot.RootElement.GetProperty("data").GetString()!));
        }
        var proposals=draft.Chat.Proposals.Count;
        await Js("document.querySelector('.face-actions button').click()");
        await Wait("Array.from(document.querySelectorAll('.requirement-options button')).every(b=>b.disabled)");
        var deadline=Stopwatch.StartNew();
        while(draft.Chat.Proposals.Count==proposals) { if(deadline.Elapsed.TotalSeconds>15)throw new Exception("No compose snapshot"); await Task.Delay(25); }
        var proposal=draft.Chat.Proposals.Last();
        using var snapshot=JsonDocument.Parse(proposal.InputSnapshot);
        var requirements=snapshot.RootElement.GetProperty("requirements").GetString()!;
        check(requirements.StartsWith(freeText)&&requirements.Contains("```mermaid")&&requirements.Contains("保留完整正文")&&requirements.Contains("水印、题号")&&requirements.Contains("不删去有效内容"),"实际辅助操作冻结快照同时携带手写要求、Mermaid 与清洗规则");
        while(form.Draft.Chat.Auxiliary.Current.Prompts.Count==0||modelRequests()==before) { if(deadline.Elapsed.TotalSeconds>15)throw new Exception("No auxiliary provider input"); await Task.Delay(25); }
        var sent=JsonDocument.Parse(form.Draft.Chat.Auxiliary.Current.Prompts.Last().Input).RootElement.GetProperty("requirements").GetString();
        check(sent==requirements,"实际发往辅助模型的输入保留全部选择要求");
        WorkflowJson.Save(Path.Combine(output,"requirements-results.json"),new{passed=true,requirements,requestsForSelection=0,auxiliaryInputVerified=true,widths=new[]{1240,414,320}});
        // The existing local SSE fault fixture intentionally has no terminal receipt.
        // Let it finish and preserve Unknown; never retry it to make this UI test pass.
        while(form.Draft.Chat.Auxiliary.Current.Binding.Stage is "running" or "sending") { if(deadline.Elapsed.TotalSeconds>20)throw new Exception("Local fixture did not finish"); await Task.Delay(50); }
    }
}
