using System.Diagnostics;
using System.Reflection;
using System.IO;
using KnowledgeClipper;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

internal static class TableUiChecks
{
 internal static async Task Run(string[] args){
  var repo=args[0];var release=args[1];var output=args[2];Directory.CreateDirectory(output);
  var fixture=File.ReadAllText(Path.Combine(repo,"tests","table-fixture.md"));
  var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
  using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();var generationCalls=0;
  _=Task.Run(async()=>{while(listener.IsListening){try{var request=await listener.GetContextAsync();
    if(!request.Request.Url!.AbsolutePath.EndsWith("models"))Interlocked.Increment(ref generationCalls);
    var bytes=Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"public-table-fixture\"}]}");
    request.Response.ContentType="application/json";await request.Response.OutputStream.WriteAsync(bytes);request.Response.Close();
   }catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}}});
  var material=new CaptureMaterial("公开表格验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
  var draft=new ClipDraft{Original=material,Material=material,Settings=new(){ApiBaseUrl=$"http://127.0.0.1:{port}",Model="public-table-fixture"},Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),
   Chat=new(){Messages=[new(){Role="assistant",Parts=[new("text",fixture)]}],Compositions=[new(){Front=fixture,Back=fixture}]}};
  var store=new DraftStore(Path.Combine(output,"data"));store.Save(draft);
  using var form=new LanguageChatForm(draft,store,Path.Combine(output,"data"),release,()=>"",()=>{});form.Show();
  var web=(WebView2)typeof(LanguageChatForm).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
  async Task<string> Js(string source)=>await web.CoreWebView2.ExecuteScriptAsync(source);
  async Task Wait(string condition){var watch=Stopwatch.StartNew();while(web.CoreWebView2==null||await Js(condition)!="true"){
    if(watch.Elapsed.TotalSeconds>45)throw new Exception("Table UI timeout: "+condition);await Task.Delay(30);}}
  await Wait("document.querySelector('.preview-action')?.disabled===false");
  using(var command=JsonDocument.Parse(JsonSerializer.Serialize(new{command="preview",cardId=draft.Chat.Compositions[0].Id})))await form.CommandAsync(command.RootElement);
  await Wait("document.querySelectorAll('.card-preview table').length===2&&document.querySelectorAll('.card-preview .katex').length>=4");
  var measurements=new List<object>();
  foreach(var width in new[]{1240,768,414,375,320}){
   form.MinimumSize=Size.Empty;form.ClientSize=new Size((int)Math.Round(width*form.DeviceDpi/96d),(int)Math.Round(900*form.DeviceDpi/96d));
   await Task.Delay(120);await Js("document.querySelector('.card-preview').scrollIntoView({block:'start'})");
   var raw=await Js("""
   (()=>({width:innerWidth,rootOverflow:document.documentElement.scrollWidth>innerWidth+1,
    tables:[...document.querySelectorAll('.message-body table')].map(t=>({rows:t.rows.length,columns:t.rows[0].cells.length,
     display:getComputedStyle(t).display,scrollable:t.parentElement.scrollWidth>t.parentElement.clientWidth,
     collisions:[...t.rows].flatMap(r=>[...r.cells].flatMap(c=>{
      const cr=c.getBoundingClientRect();const bad=[];
      for(const m of c.querySelectorAll('.katex')){const mr=m.getBoundingClientRect();
       if(mr.right>cr.right+1||mr.left<cr.left-1)bad.push({text:m.textContent,cell:cr.toJSON(),math:mr.toJSON()});}
      return bad;}))}))}))()
   """);
   var value=JsonDocument.Parse(raw).RootElement.Clone();measurements.Add(value);
   using(var shot=File.Create(Path.Combine(output,$"table-{width}.png")))await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,shot);
   if(value.GetProperty("rootOverflow").GetBoolean()||value.GetProperty("tables").EnumerateArray().Any(t=>t.GetProperty("rows").GetInt32()!=4||t.GetProperty("columns").GetInt32()!=3||t.GetProperty("collisions").GetArrayLength()!=0||t.GetProperty("display").GetString()!="table")){
    WorkflowJson.Save(Path.Combine(output,"measurements.json"),measurements);throw new Exception("Table cells overlap or grid is malformed at "+width);}
  }
  if(draft.Chat.Compositions[0].Front!=fixture||draft.Chat.Compositions[0].Back!=fixture||draft.Chat.Prompts.Count!=0||generationCalls!=0)throw new Exception("Table display modified source or called a model");
  var native=ChatMarkdown.Render(fixture,fixture,[]).Card.Front.Single(p=>p.Kind=="latex").Text!;
  var escaped=ChatMarkdown.Render("问题","| 特殊 | 空格 |\n| --- | --- |\n| & % # _ { } \\ $ ^ ~ | |",[]).Card.Back[0][0].Text!;
  var nativeCard=new CardComposition{Front="$$\n"+native+"\n$$",Back="$$\n"+escaped+"\n$$"};draft.Chat.Compositions.Add(nativeCard);
  using(var command=JsonDocument.Parse(JsonSerializer.Serialize(new{command="preview",cardId=nativeCard.Id})))await form.CommandAsync(command.RootElement);
  await Wait("document.querySelectorAll('.card-preview .katex').length===2");
  if(await Js("document.querySelectorAll('.card-preview .katex-error').length===0")!="true")throw new Exception("Native LaTeX table does not render");
  form.ClientSize=new Size((int)Math.Round(1240*form.DeviceDpi/96d),(int)Math.Round(900*form.DeviceDpi/96d));await Task.Delay(100);
  await Js("document.querySelector('.card-preview').scrollIntoView({block:'start'})");
  using(var shot=File.Create(Path.Combine(output,"remnote-grid-syntax.png")))await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,shot);
  WorkflowJson.Save(Path.Combine(output,"table-results.json"),new{passed=true,measurements,sourceUnchanged=true,modelCalls=0});
  Console.WriteLine("PASS tables: chat/preview, text/math, five widths, cell bounds, source preserved");
 }
}
