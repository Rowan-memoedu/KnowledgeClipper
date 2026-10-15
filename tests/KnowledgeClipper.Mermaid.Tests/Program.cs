using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
 internal const string Flow="flowchart LR\n A[输入] --> B[处理]\n B --> C[输出]";
 internal const string Sequence="sequenceDiagram\n participant A as 学生\n participant B as 教师\n A->>B: 提问\n B-->>A: 回答";
 static string Fence(string value)=>"```mermaid\n"+value+"\n```";
 static readonly string Front="公开 Mermaid 验收：输入到输出。\n\n"+Fence(Flow)+"\n\n正文保留行内公式 $x=2$。";
 static readonly string Back="时序关系\n\n"+Fence(Sequence)+"\n\n- 正文层级\n  - Mermaid 源码保持可编辑。";
 static readonly List<string> Checks=[];
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);Checks.Add(message);Console.WriteLine("PASS "+message);}
 [STAThread] static int Main(string[] args){
  Directory.CreateDirectory(args[2]);
  if(args.Length>3&&args[3]=="--live"){Live(args).GetAwaiter().GetResult();return 0;}
  if(Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST")!="1")throw new Exception("Use isolated desktop runner");
  Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
  using var context=new ApplicationContext();using var timer=new System.Windows.Forms.Timer{Interval=10};var exit=0;
  timer.Tick+=async(_,_)=>{timer.Stop();try{await Ui(args);}catch(Exception ex){File.WriteAllText(Path.Combine(args[2],"error.txt"),ex.ToString());Console.Error.WriteLine(ex);exit=1;}finally{context.ExitThread();}};
  timer.Start();Application.Run(context);return exit;
 }
 static ClipDraft Draft(string repo,LanguageSettings settings){
  var material=new CaptureMaterial("公开 Mermaid 验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
  return new(){Original=material,Material=material,Settings=settings,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),ModeId="question",Input=new(Intent:"question"),
   Chat=new(){Model="public-model",Messages=[new(){Role="user",Parts=[new("text","请展示流程图和时序图。") ]},new(){Role="assistant",Parts=[new("text",Front+"\n\n"+Back)]}],Compositions=[new(){Front=Front,Back=Back,TypeId="fast-answer"}]}};
 }
 static async Task Ui(string[] args){
  var repo=args[0];var release=args[1];var output=args[2];var data=Path.Combine(output,"fixture");Directory.CreateDirectory(data);
  using var listenerPort=new TcpListener(IPAddress.Loopback,0);listenerPort.Start();var port=((IPEndPoint)listenerPort.LocalEndpoint).Port;listenerPort.Stop();
  using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();var generations=0;
  _=Task.Run(async()=>{while(server.IsListening)try{var request=await server.GetContextAsync();if(!request.Request.Url!.AbsolutePath.EndsWith("/models"))Interlocked.Increment(ref generations);var bytes=Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"public-model\"}]}");request.Response.ContentType="application/json";await request.Response.OutputStream.WriteAsync(bytes);request.Response.Close();}catch{break;}});
  var draft=Draft(repo,new(){ApiBaseUrl=$"http://127.0.0.1:{port}",Model="public-model"});var store=new DraftStore(data);store.Save(draft);
  var assembly=Assembly.LoadFrom(Path.Combine(release,"KnowledgeClipper.dll"));
  var ctor=assembly.GetType("KnowledgeClipper.LanguageChatForm",true)!.GetConstructors(Flags).Single(c=>c.GetParameters().Length==6);
  using var form=(Form)ctor.Invoke([draft,store,data,release,(Func<string>)(()=>"public-fixture"),(Action)(()=>{})]);form.Show();
  var web=(WebView2)form.GetType().GetField("web",Flags)!.GetValue(form)!;
  async Task<string> Js(string js)=>await web.CoreWebView2.ExecuteScriptAsync(js);
  async Task Wait(string js){var watch=Stopwatch.StartNew();while(web.CoreWebView2==null||await Js(js)!="true"){if(watch.Elapsed.TotalSeconds>45){if(web.CoreWebView2!=null)File.WriteAllText(Path.Combine(output,"failed-dom.json"),await Js("document.body.innerHTML"));throw new TimeoutException(js);}await Task.Delay(50);}}
  async Task Diagrams(string scope,string name){
   await Wait("document.querySelectorAll("+JsonSerializer.Serialize(scope+" [data-streamdown=mermaid-block]")+").length>=2");
   for(var i=0;i<2;i++){
    var selector="document.querySelectorAll("+JsonSerializer.Serialize(scope+" [data-streamdown=mermaid-block]")+")["+i+"]";
    await Js(selector+".scrollIntoView({block:'center'})");
    await Wait(selector+".querySelector('svg[aria-roledescription]')?.getBoundingClientRect().height>20");
    using var png=File.Create(Path.Combine(output,name+"-diagram-"+i+".png"));await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);
   }
  }
  await Diagrams(".conversation","chat");
  Check(true,"真实聊天页中文流程图与时序图已生成 SVG");
  using(var png=File.Create(Path.Combine(output,"chat.png")))await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);
  using var preview=JsonDocument.Parse(JsonSerializer.Serialize(new{command="preview",cardId=draft.Chat!.Compositions[0].Id}));
  await (Task)form.GetType().GetMethod("CommandAsync",Flags)!.Invoke(form,[preview.RootElement])!;
  await Diagrams(".card-preview","preview");
  File.WriteAllText(Path.Combine(output,"preview-dom.html"),JsonSerializer.Deserialize<string>(await Js("document.querySelector('.card-preview').outerHTML")));
  Check(true,"真实卡片预览正反面均显示 Mermaid SVG，含公式与嵌套正文");
  Check(await Js("Array.from(document.querySelectorAll('.card-preview svg[aria-roledescription]')).every(svg=>{let a=svg.parentElement;while(a&&a.dataset.streamdown!=='mermaid-block'){if(a.clientHeight+1<svg.getBoundingClientRect().height)return false;a=a.parentElement;}return true})")=="true","各层图形容器随真实 SVG 高度展开，不裁掉节点底部");
  foreach(var width in new[]{320,375,414,768,1240}){
   await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",JsonSerializer.Serialize(new{width,height=1000,deviceScaleFactor=1,mobile=false}));await Task.Delay(120);
   Check(await Js("document.documentElement.scrollWidth<=innerWidth+1&&Array.from(document.querySelectorAll('.card-preview svg[aria-roledescription]')).every(svg=>svg.getBoundingClientRect().width<=svg.closest('.message-body').clientWidth+1&&svg.getBoundingClientRect().height>20)")=="true","Mermaid 页面宽度 "+width+" 图形完整缩放且无全页横向溢出");
   using var png=File.Create(Path.Combine(output,"preview-"+width+".png"));await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);
  }
  Check(draft.Chat.Compositions[0].Front==Front&&draft.Chat.Compositions[0].Back==Back,"渲染、预览和调整尺寸不修改 Mermaid 或正文源码");
  Check(generations==0,"显示、预览与布局零模型生成请求");form.Close();
  Check(store.Find(draft.Id)!.Chat!.Compositions[0].Front==Front,"关闭保存后源码保持原样");
  await OfflineRuntime(args);
  WorkflowJson.Save(Path.Combine(output,"ui-results.json"),new{passed=true,checks=Checks,generations,workflowSha256=ImageAsset.Hash(File.ReadAllBytes(typeof(ChatMarkdown).Assembly.Location))});
 }
 static async Task OfflineRuntime(string[] args){
  var folder=Path.Combine(args[2],"offline-runtime");Directory.CreateDirectory(folder);
  File.Copy(Path.Combine(args[1],"chat-view","mermaid-runtime.js"),Path.Combine(folder,MermaidCards.RuntimeFile),true);
  var card=File.ReadAllText(Path.Combine(Path.GetDirectoryName(args[2])!,"live-final","Anki","answer.html"));
  File.WriteAllText(Path.Combine(folder,"card.html"),"<!doctype html><meta charset=utf-8><body>"+card);
  using var form=new Form{Width=900,Height=800};using var web=new WebView2{Dock=DockStyle.Fill};form.Controls.Add(web);form.Show();
  var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(folder,"webview"));await web.EnsureCoreWebView2Async(environment);
  web.CoreWebView2.SetVirtualHostNameToFolderMapping("mermaid.test",folder,CoreWebView2HostResourceAccessKind.Allow);
  var external=0;web.CoreWebView2.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
  web.CoreWebView2.WebResourceRequested+=(_,e)=>{if(new Uri(e.Request.Uri).Host!="mermaid.test"){external++;e.Response=web.CoreWebView2.Environment.CreateWebResourceResponse(null,403,"Offline","");}};
  async Task<string> Js(string value)=>await web.CoreWebView2.ExecuteScriptAsync(value);
  async Task Wait(string value){var watch=Stopwatch.StartNew();while(await Js(value)!="true"){if(watch.Elapsed.TotalSeconds>40)throw new TimeoutException(value);await Task.Delay(50);}}
  web.CoreWebView2.Navigate("https://mermaid.test/card.html");
  await Wait("document.querySelectorAll('.kc-mermaid[data-mermaid-state=complete] svg').length===2");
  Check(await Js("Array.from(document.querySelectorAll('.kc-mermaid svg')).every(s=>s.getBoundingClientRect().height>20)")=="true","真实 Anki 回读 HTML 在离线运行组件中生成正反面 SVG");
  Check(await Js("Array.from(document.querySelectorAll('.kc-mermaid-diagram')).every(e=>getComputedStyle(e).backgroundColor==='rgb(255, 255, 255)')")=="true","图形画布保留固定浅色底，夜间卡片仍可读");
  await Js("for(let i=0;i<4;i++)window.KnowledgeClipperMermaid.run()");await Task.Delay(100);
  Check(await Js("document.querySelectorAll('.kc-mermaid svg').length===2")=="true","重复触发不会重叠或重复绘图");
  const string invalid="flowchart LR\n A[未闭合";
  await Js("var e=document.querySelector('.kc-mermaid');e.querySelector('.kc-mermaid-source').textContent="+JsonSerializer.Serialize(invalid)+";e.dataset.mermaidState='';window.KnowledgeClipperMermaid.run()");
  await Wait("document.querySelector('.kc-mermaid').dataset.mermaidState==='error'");
  Check(JsonSerializer.Deserialize<string>(await Js("document.querySelector('.kc-mermaid-source').textContent"))==invalid,"错误语法显示明确提示且保留原始源码");
  const string edited="flowchart TB\n X[独立样例] --> Y[修改成功]";
  await Js("var e=document.querySelector('.kc-mermaid');e.querySelector('.kc-mermaid-source').textContent="+JsonSerializer.Serialize(edited)+";e.dataset.mermaidState='';window.KnowledgeClipperMermaid.run()");
  await Wait("document.querySelector('.kc-mermaid').dataset.mermaidState==='complete'");
  Check(await Js("document.querySelector('.kc-mermaid-diagram').textContent.includes('修改成功')")=="true","修正源码后重新生成矢量图，不依赖首次样例");
  Check(external==0,"Anki 渲染组件与样例无需外部网络请求");
  using(var png=File.Create(Path.Combine(args[2],"offline-runtime.png")))await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);
  form.Close();
 }
 static async Task Live(string[] args){
  var repo=args[0];var output=args[2];var doc=args[4];var label=args[5];
  foreach(var destination in new[]{ExportDestination.RemNote,ExportDestination.Anki}){
   var root=Path.Combine(output,destination.ToString());Directory.CreateDirectory(root);var store=new DraftStore(root);var idFile=Path.Combine(root,"draft-id.txt");
   var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes","chat"));
   var registry=WorkflowJson.Read<ChatConfiguration>("D:/AppData/KnowledgeClipper/modes/chat/question-types.json");
   WorkflowJson.Save(Path.Combine(root,"modes","chat","question-types.json"),registry with{Types=registry.Types.Select(t=>t.Id=="fast-answer"?t with{DocumentId=doc,Label=label}:t).ToArray()});
   ClipDraft parent;
   if(File.Exists(idFile))parent=store.Find(File.ReadAllText(idFile))!;
   else{var settings=WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json") with{AnkiRootDeck="KnowledgeClipper::公开验收::Mermaid 20261015"};parent=Draft(repo,settings);store.Save(parent);File.WriteAllText(idFile,parent.Id);}
   using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(2)};
   var composition=parent.Chat!.Compositions[0];
   await ChatComposition.ExportAsync(parent,composition,store,config,new CardImportRouter(http),destination);
   var child=store.Find(composition.ExportDraftId!)!;
   Check(child.ImportState==ImportState.Verified,"真实 "+destination+" Mermaid 图文卡回读 Verified");
   if(destination==ExportDestination.Anki){
    var anki=new AnkiConnection(http,child.Settings.AnkiEndpoint);
    var cards=await anki.CallAsync("cardsInfo",new{cards=child.CardIds});
    WorkflowJson.Save(Path.Combine(root,"cards.json"),cards);
    foreach(var face in new[]{"question","answer"})File.WriteAllText(Path.Combine(root,face+".html"),cards[0].GetProperty(face).GetString());
    var runtime=await anki.CallAsync("retrieveMediaFile",new{filename=MermaidCards.RuntimeFile});
    Check(ImageAsset.Hash(Convert.FromBase64String(runtime.GetString()!))==ImageAsset.Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"chat-view","mermaid-runtime.js"))),"真实 Anki 离线 Mermaid 组件字节与候选完全相同");
    // Leave only public canary cards reviewable for the later native display check; no answer is graded.
   }else{
    WorkflowJson.Save(Path.Combine(root,"plan.json"),child.ConfirmedRemNote);
    var rendering=new HttpRequestMessage(HttpMethod.Post,RemNoteConnection.ResolveEndpoint(child.Settings.RemNoteSdkEndpoint)+"/sdk/knowledge-clipper/mermaid-render");
    rendering.Headers.Add("X-KnowledgeClipper-Protocol","1");
    rendering.Content=new StringContent(JsonSerializer.Serialize(child.ConfirmedRemNote,new JsonSerializerOptions(WorkflowJson.Options){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull}),Encoding.UTF8,"application/json");
    using var result=await http.SendAsync(rendering);var body=await result.Content.ReadAsStringAsync();File.WriteAllText(Path.Combine(root,"initial-render-state.json"),body);
   }
   WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{status=child.ImportState.ToString(),child.Id,child.NoteId,child.CardIds,child.RemNoteResult,sourcePreserved=composition.Front==Front&&composition.Back==Back});
  }
 }
}
