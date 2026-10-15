using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task MathLayoutUi(string repo)
    {
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();var writes=0;
        var serving=Task.Run(async()=>{while(server.IsListening)try{var c=await server.GetContextAsync();if(!c.Request.Url!.AbsolutePath.EndsWith("/models"))writes++;
            await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"math-fixture\"}]}"));c.Response.Close();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        var text="前文，行内 $\\frac{1}{2}+x^2$ 后文，保持正常基线。\n\n"+
            "$$\n\\frac{a+b}{c+d}=\\sqrt{x^2+y^2}\n$$\n\n"+
            "$$\n\\begin{pmatrix}1&2\\\\3&4\\end{pmatrix}\n$$\n\n"+
            "$$\n\\begin{aligned}f(x)&=x^2+2x+1\\\\f'(x)&=2x+2\\end{aligned}\n$$\n\n"+
            "$$\n\\int_0^1 x^2\\,dx=\\frac{1}{3}\n$$\n\n"+
            "$$\n"+string.Join("+",Enumerable.Range(1,32).Select(i=>"x_{"+i+"}"))+"\n$$";
        var material=new CaptureMaterial("公开公式布局验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        var chat=new ChatSession{Model="math-fixture",Messages=[new(){Role="assistant",Parts=[new("text",text)]}],Compositions=[new(){Front="行内 $x^2+y^2=1$。",Back=text}]};
        var draft=new ClipDraft{Original=material,Material=material,Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}"},Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Chat=chat};store.Save(draft);
        var original=JsonSerializer.Serialize(new{chat.Messages,chat.Compositions[0].Front,chat.Compositions[0].Back},WorkflowJson.Options);
        var form=(Form)desktop.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single().Invoke([draft,store,data,AppContext.BaseDirectory,(Func<string>)(()=>"fixture"),(Action)(()=>{})]);
        form.Show();
        try
        {
            await Ready(form);await Button(form,"预览卡片");
            await Wait(form,"document.querySelectorAll('.card-preview .katex').length>=7&&document.querySelectorAll('.message .katex').length>=6&&document.fonts.status==='loaded'");
            await Js(form,"document.querySelector('.viewport').scrollTop=0;document.querySelector('.cards').scrollTop=0;");
            await Shot(form,"math-before-assert");
            var layout=await Js(form,"(()=>{const roots=[...document.querySelectorAll('.message .katex,.card-preview .katex')];return {fractionBars:[...document.querySelectorAll('.frac-line')].map(e=>e.getBoundingClientRect().width),vlists:[...document.querySelectorAll('.vlist-t')].map(e=>getComputedStyle(e).display),mathml:roots.every(e=>e.querySelector('.katex-mathml math')),html:roots.every(e=>e.querySelector('.katex-html')),fonts:[...document.fonts].filter(f=>f.family.includes('KaTeX')).map(f=>({family:f.family,status:f.status})),display:roots.map(e=>e.getBoundingClientRect().height)};})()");
            File.WriteAllText(Path.Combine(output,"math-layout.json"),layout);
            using(var json=JsonDocument.Parse(layout))
            {
                var value=json.RootElement;
                Check(value.GetProperty("fractionBars").EnumerateArray().All(v=>v.GetDouble()>3),"问答与卡片预览的分式横线具有正确宽度");
                Check(value.GetProperty("vlists").EnumerateArray().All(v=>v.GetString()=="inline-table"),"公式 HTML 堆叠结构与标准 KaTeX CSS 匹配，分式及矩阵不散位");
                Check(value.GetProperty("mathml").GetBoolean()&&value.GetProperty("html").GetBoolean(),"标准 KaTeX HTML 呈现并保留无障碍 MathML，不使用图片或伪造文本公式");
                Check(value.GetProperty("fonts").EnumerateArray().Any(v=>v.GetProperty("family").GetString()!.Contains("KaTeX_Main")&&v.GetProperty("status").GetString()=="loaded"),"真实数学字体成功加载，无回退字体导致的错位");
            }
            foreach(var width in new[]{1240,768,414,375,320})
            {
                var scale=form.DeviceDpi/96d;form.MinimumSize=new(1,1);form.ClientSize=new((int)(width*scale),(int)(950*scale));await Task.Delay(180);
                var geometry=await Js(form,"(()=>{const equations=[...document.querySelectorAll('.message-body .katex-display')];return {pageFits:document.documentElement.scrollWidth<=innerWidth,count:equations.length,items:equations.map(e=>{e.scrollLeft=0;const r=e.getBoundingClientRect(),parts=[...e.querySelectorAll('.katex-html > .base')].map(n=>n.getBoundingClientRect()),left=Math.min(...parts.map(b=>b.left)),right=Math.max(...parts.map(b=>b.right)),wide=e.scrollWidth>e.clientWidth+2;return {wide,left:left-r.left,center:Math.abs((left+right-r.left-r.right)/2),reachable:e.scrollWidth>=right-r.left-2};})};})()");
                File.WriteAllText(Path.Combine(output,$"math-geometry-{width}.json"),geometry);
                using var parsed=JsonDocument.Parse(geometry);var g=parsed.RootElement;
                Check(g.GetProperty("count").GetInt32()>=10&&g.GetProperty("pageFits").GetBoolean()&&g.GetProperty("items").EnumerateArray().All(v=>Math.Abs(v.GetProperty("left").GetDouble())<3&&v.GetProperty("reachable").GetBoolean()),"宽度 "+width+" 独立公式沿正文左边界对齐，长公式从左端完整滚动，无横向漂移");
                await Shot(form,"math-"+width);
                await Js(form,"document.querySelector('.card-preview').scrollIntoView({block:'start'})");
                await Shot(form,"math-preview-"+width);
            }
            Check(original==JsonSerializer.Serialize(new{chat.Messages,chat.Compositions[0].Front,chat.Compositions[0].Back},WorkflowJson.Options)&&writes==0&&errors.Count==0,"显示修复不改原始公式、不调用模型且无前端异常");
        }
        finally{form.Dispose();server.Stop();await serving;}
    }
}
