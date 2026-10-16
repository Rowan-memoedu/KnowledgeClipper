using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var repo = args[0]; var output = args[1]; Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
void Reject(Action action, string name) { try { action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FileNotFoundException) { checks.Add(name); return; } throw new Exception(name); }
var defaults = Path.Combine(repo, "modes", "chat"); var overrides = Path.Combine(output, "unit-fixture", Guid.NewGuid().ToString("N"));
var config = new ChatConfigurationLoader(defaults, overrides);
var session = new ChatSession { Model = "fixture" };
var first = config.Freeze(session, "原始问题", []);
var common = config.Editable("system.md"); File.AppendAllText(common, "\n本轮验证规则。\n");
var revised = config.Freeze(session, "原始问题", []);
Check(first.Files[0].Sha256 != revised.Files[0].Sha256 && !first.Instructions.Contains("本轮验证规则"), "MD 保存后下一轮生效，原快照不变");
Reject(() => config.Resolve("../private.md"), "配置目录穿越拒绝");
var good = File.ReadAllText(common); File.WriteAllText(common, "");
Reject(() => config.Freeze(session, "仍保留的输入", []), "空配置拒绝，不回落旧规则"); File.WriteAllText(common, good);
var user = new ChatMessage { Parts = [new("text", "求 $x^2$。")], Role = "user" };
var assistant = new ChatMessage { Parts = [new("text", "先平方。")], Role = "assistant" };
session.Messages.AddRange([user, assistant]);
var card = new CardComposition(); session.Compositions.Add(card);
ChatComposition.Select(session, card, user.Id, "front", true);
ChatComposition.Edit(card, "我的改稿", "我的答案", "fast-answer");
ChatComposition.Select(session, card, assistant.Id, "back", true);
Check(card.Front == "我的改稿" && card.Back == "我的答案\n\n先平方。" && !card.SourcesChanged, "显式加入追加到手工稿，保留既有正文且不误报来源改变");
var expectedBack = card.Back;
var proposal = ChatComposition.Begin(session, card, "formalize", "", ["fast-answer"]);
ChatComposition.Parse(proposal, "{\"cards\":[{\"front\":\"书面问题\",\"back\":\"不能采用的回答\",\"typeId\":\"fast-answer\"}]}", session, ["fast-answer"]);
Check(proposal.Cards[0].Back == expectedBack, "书面化保留冻结输入中的完整追加背面");
ChatComposition.Edit(card, "更新后的改稿", "我的答案", "fast-answer");
Reject(() => ChatComposition.Adopt(session, proposal), "迟到候选不能覆盖更新稿");
var beforeUndoRevision = card.Revision;
ChatComposition.Undo(card);
Check(card.Front == "我的改稿" && card.Revision > beforeUndoRevision, "撤销恢复编辑稿并保持修订号递增");
Reject(() => ChatComposition.Adopt(session, proposal), "撤销不能使迟到候选重新有效");
var invalid = ChatComposition.Begin(session, card, "integrate", "", ["fast-answer"]);
ChatComposition.Parse(invalid, "模型没有返回 JSON，但该文本必须保留", session, ["fast-answer"]);
Check(invalid.Status == "unparsed" && invalid.RawOutput.Contains("必须保留"), "解析失败保留原回复");
var rendered = ChatMarkdown.Render("给定 $x=2$，求结果。", "# 主步骤\n\n- 先计算 $x^2=4$。\n  - 再验证。\n\n$$\n\\begin{cases}x=2\\\\y=3\\end{cases}\n$$", []);
var graphSource = "flowchart LR\n A[输入] --> B[处理]\n B --> C[输出]";
var sequenceSource = "sequenceDiagram\n participant A as 学生\n participant B as 教师\n A->>B: 提问\n B-->>A: 回答";
var graph = ChatMarkdown.Render("普通前文\n\n```mermaid\n"+graphSource+"\n```\n\n后文 $x=2$。", "```mermaid\n"+sequenceSource+"\n```", []);
Check(graph.Card.Front.Single(p=>p.Kind=="mermaid").Text==graphSource && graph.Card.Back.SelectMany(r=>r).Single(p=>p.Kind=="mermaid").Text==sequenceSource,"Mermaid 双面保留源码，不混入正文或数学解析");
Check(graph.FrontHtml.Contains("kc-mermaid-source") && graph.FrontHtml.Contains("A[输入] --&gt; B[处理]") && !graph.FrontHtml.Contains("<img") && graph.Media.Length==0,"Anki 保留转义源码和图块，不栅格化为图片");
var plainCode=ChatMarkdown.Render("```text\nflowchart LR\n A --> B\n```","答案",[]);
Check(plainCode.Card.Front.All(p=>p.Kind=="text"),"普通代码不冒充 Mermaid 图块");
Check(rendered.Card.Front.Any(p => p.Kind == "latex" && p.Block == false && p.Text == "x=2"), "单美元行内公式原生导出");
Check(rendered.Card.Back.SelectMany(r => r).Any(p => p.Kind == "latex" && p.Block == true), "独立公式原生导出");
Check(rendered.Card.BackParents!.Any(p => p >= 0), "列表与标题保留真实答案层级");
Check(!rendered.BackHtml.Contains("<br>\\begin"), "公式内部不插 HTML 换行");
var code = ChatMarkdown.Render("代码 `$literal$` 和金额 $5、$10", "```latex\n$x$\n\\(y\\)\n```\n\n正文 \\(z\\)", []);
Check(!code.Card.Front.Any(p => p.Kind == "latex"), "代码与金额不解析成公式");
Check(code.Card.Back.SelectMany(r => r).Count(p => p.Kind == "latex") == 1, "代码块隔离与括号公式");
Reject(() => ChatMarkdown.Render("![伪图](https://example.com/a.png)", "说明", []), "未登记远程图不能冒充可导出媒体");
var mediaPath = Path.Combine(output, "public-pixel.png");
File.WriteAllBytes(mediaPath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
var image = new ImageAsset(mediaPath, ImageAsset.Hash(File.ReadAllBytes(mediaPath)), "question", 1);
var withImages = ChatMarkdown.Render("前 $a$ ![图](asset:" + image.Sha256 + ") 后", "![图](asset:" + image.Sha256 + ")\n\n结论", [image]);
Check(withImages.Card.Front.Select(p => p.Kind).SequenceEqual(new[] { "text", "latex", "text", "image", "text" }), "正面图文公式交错顺序");
Check(withImages.Media.Length == 1 && withImages.Card.Back.SelectMany(r => r).Any(p => p.Kind == "image"), "双面图片去重并保持位置");
var localUrl = "https://clipper-media.local/" + image.Sha256 + ".png";
var dragged = ChatMarkdown.Render("前 " + localUrl + " 后", "![旧图](" + localUrl + ")", [image]);
Check(dragged.Card.Front.Any(p => p.Kind == "image") && dragged.Card.Back.SelectMany(r => r).Any(p => p.Kind == "image") && dragged.Media.Single().Sha256 == image.Sha256, "拖入裸链接及旧 Markdown 链接均导出真实双面图片");
Check(dragged.FrontHtml.Contains(image.Html) && dragged.BackHtml.Contains(image.Html), "拖入本地链接沿用 Anki 真实媒体 HTML");
Check(ChatMarkdown.NormalizeMediaReferences("`"+localUrl+"`\n\n```\n"+localUrl+"\n```", [image]).Contains("```\n"+localUrl), "代码中的链接保持原文");
Check(ChatMarkdown.NormalizeMediaReferences("[图片]("+localUrl+")", [image]) == "![图片](asset:"+image.Sha256+")", "普通 Markdown 链接兼容转换");
Check(ChatMarkdown.ForDisplay(localUrl, [image]).Contains("![图片]("+localUrl+")"), "旧裸链接预览兼容图片");
Reject(() => ChatMarkdown.Render(localUrl, "说明", []), "未登记本地链接不得冒充素材");
Reject(() => ChatMarkdown.Render(localUrl, "说明", [image with { Path = mediaPath+".missing" }]), "缺失真实图片拒绝导出");
var material = new CaptureMaterial("公开测试问题", null, "generated_allowed", null, "", 0, DateTimeOffset.UtcNow);
var draft = new ClipDraft { Original = material, Material = material, Settings = new(), Configuration = ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),
    Route = new("question","question",true,false), Input = new(Intent:"question"), ModeId = "question", Chat = session };
var store = new DraftStore(output); store.Save(draft);
Check(store.Find(draft.Id)!.Chat!.Messages[0].Text == user.Text, "增量消息随原草稿恢复");
var legacy = JsonSerializer.Serialize(draft, WorkflowJson.Options);
using var legacyDoc = JsonDocument.Parse(legacy);
Check(!legacyDoc.RootElement.GetProperty("chat").TryGetProperty("gate",out _), "不持久化运行时同步对象");
var manualCalls = 0; using var http = new HttpClient(new CountingHandler(()=>manualCalls++));
var coordinator = new ChatCoordinator(draft, store, config, new CodexChatProcess(Path.Combine(repo,"helpers","codex_chat.py"), output), http, () => "fixture", output);
session.Binding.Stage = "unknown"; session.Binding.ThreadId = "original-thread"; session.Input = "保留输入";
try { await coordinator.SendAsync([new("fixture","fixture",[],["text"])]); throw new Exception("Unknown 重发未被阻止"); }
catch (InvalidOperationException) { }
Check(manualCalls == 0 && session.Binding.ThreadId == "original-thread" && session.Input == "保留输入", "Unknown 不调用模型、不新建、不重发");
session.Binding.Stage = "completed";
try { await coordinator.SendAsync([new("another-model","fixture",[],["text"])]); throw new Exception("失效模型未拒绝"); }
catch (InvalidDataException) { }
Check(manualCalls == 0 && session.Binding.ThreadId == "original-thread" && session.Input == "保留输入", "失效模型保留原绑定和输入，不静默切换");
var examplesPath = config.Editable("few-shots/fast-answer.md");
File.WriteAllText(examplesPath,"![未登记示例](asset:"+image.Sha256+")");session.IncludeExamples=true;
Reject(()=>config.Freeze(session,"仍保留的输入",[]),"缺少示例附件时拒绝路径冒充图片");session.IncludeExamples=false;
var apiDraft = new ClipDraft { Original=material,Material=material,Configuration=draft.Configuration,Settings=new(),Chat=new(){Model="fixture",Input="保留 $x$ 源码"} };
using(var apiHttp=new HttpClient(new StreamingHandler())) {
    var api=new ChatCoordinator(apiDraft,store,config,new CodexChatProcess("unused",output),apiHttp,()=>"fixture",output);
    var updates=0;api.Changed+=()=>updates++;
    await api.SendAsync([new("fixture","fixture",[],["text"])]);
    Check(api.Session.Messages.Last().Text=="答案 $x$\n\n第二段"&&updates>2&&api.Session.Binding.Stage=="completed","API 逐段流式回复且保留原 Markdown");
    api.Session.Input="仅整理正面";
    await api.SendAsync([new("fixture","fixture",[],["text"])],"formalize","仅返回 cards JSON");
    Check(api.Session.Messages.Last().Text.StartsWith("{\"cards\""),"API 辅助动作使用自身 JSON 合同，不套旧 Answer Schema");
    var lastSnapshot=api.Session.Prompts.Last();api.Session.Input="模型目录变化后的新问题";
    try {await api.SendAsync([]);throw new Exception("失效模型未拒绝");}catch(InvalidDataException){}
    Check(lastSnapshot.Stage=="completed"&&api.Session.Binding.Stage=="completed"&&api.Session.Prompts.Count==2,"发送前校验失败不能改坏上一回合的完成状态");
}
await AuxiliaryChecks.Run(repo,output,Check);
await AnnotationChecks.Run(repo,output,Check);
TranscriptionChecks.Run(repo,output,Check);
SymmetricChecks.Run(repo,output,Check);
AllFormalizeChecks.Run(repo,output,Check);
DetachedSourceChecks.Run(output,Check);
RedoChecks.Run(Check);
await HistoryChecks.Run(repo,output,Check);
HistorySessionChecks.Run(repo,output,Check);
await BranchChecks.Run(repo,output,Check);
await IntegrationChecks.Run(repo,output,Check);
WorkflowJson.Save(Path.Combine(output,"unit-results.json"), new { checks = checks.Count, passed = checks });
Console.WriteLine($"PASS {checks.Count} chat checks");

if (args.Contains("--extras")) await LiveExtras.Run(repo,output);
if (args.Contains("--branch-live")) await BranchLive.Run(repo,output);
if (args.Contains("--api-branch-live")) await BranchLive.Run(repo,output,true);
if (args.Contains("--annotations-live")) await AnnotationLive.Run(repo,output);
if (args.Contains("--symmetric-exports-live")) await AnnotationLive.Run(repo,output,true);
var corpusIndex=Array.IndexOf(args,"--corpus");
if(corpusIndex>=0)CorpusChecks.Run(args[corpusIndex+1],output);
if(args.Contains("--faults"))await FaultChecks.Run(repo,output);
if(args.Contains("--api-live"))await LiveApi.Run(repo,output);
if(args.Contains("--integration-live"))await IntegrationLive.Run(repo,output,"api");
if(args.Contains("--integration-codex-live"))await IntegrationLive.Run(repo,output,"codex");
if(args.Contains("--integration-exports"))await IntegrationExports.Run(repo,output);
if(args.Contains("--anki-live"))await LiveDelivery.Run(repo,output,ExportDestination.Anki);
if(args.Contains("--remnote-live"))await LiveDelivery.Run(repo,output,ExportDestination.RemNote);
if(args.Contains("--compaction-live"))await LiveCompaction.Run(repo,output);
if(args.Contains("--recover-compaction"))await LiveCompaction.Recover(repo,output);

if (args.Contains("--live"))
{
    var liveRoot = Path.Combine(output, "native-live"); Directory.CreateDirectory(liveRoot);
    var liveStore = new DraftStore(liveRoot);
    var idFile = Path.Combine(liveRoot, "draft-id.txt");
    ClipDraft live;
    if (File.Exists(idFile)) live = liveStore.Find(File.ReadAllText(idFile))!;
    else
    {
        live = new() { Original = material, Material = material, Settings = new(), Configuration = draft.Configuration,
            Input = new(Intent:"question"), ModeId="question", Route=new("question","question",true,false),
            Chat=new() { Binding=new() { Provider="codex" } } };
        liveStore.Save(live); File.WriteAllText(idFile, live.Id);
    }
    var liveConfig = new ChatConfigurationLoader(defaults, Path.Combine(liveRoot,"modes"));
    using var liveHttp = new HttpClient();
    var liveCoordinator = new ChatCoordinator(live, liveStore, liveConfig,
        new CodexChatProcess(Path.Combine(repo,"helpers","codex_chat.py"),liveRoot), liveHttp, ()=>throw new Exception("Codex 不应读取 API Key"),liveRoot);
    var catalog = await liveCoordinator.ModelsAsync("codex");
    WorkflowJson.Save(Path.Combine(liveRoot,"models.json"),catalog);
    Console.WriteLine($"Live catalog: {catalog.Length} models");
    if (live.Chat!.Binding.NeedsReconciliation) await liveCoordinator.RecoverAsync();
    if (live.Chat.Prompts.Count == 0)
    {
        live.Chat.Model = catalog.First(m=>m.Id=="gpt-6-luna").Id; live.Chat.Effort = "low";
        live.Chat.Input = "公开验收：一块长方形木板长 7 厘米、宽 4 厘米，材料代号杉木。求面积。仅答两行。";
        await liveCoordinator.SendAsync(catalog);
        Console.WriteLine("Live first turn: " + live.Chat.Binding.NativeStatus);
    }
    if (live.Chat.Prompts.Count == 1 && live.Chat.Binding.Stage == "completed")
    {
        var rule = liveConfig.Editable("system.md"); File.AppendAllText(rule,"\n本次验收的回答必须以「规则已更新」开头。\n");
        live.Chat.Model = catalog.First(m=>m.Id=="gpt-6.1-sol").Id;
        live.Chat.Input = "沿用前一块木板，长增加 2 厘米。复述材料代号，求新面积。最多三行。";
        await liveCoordinator.SendAsync(catalog);
        Console.WriteLine("Live second turn: " + live.Chat.Binding.NativeStatus);
    }
    Check(live.Chat.Binding.Stage=="completed" && live.Chat.Messages.Any(m=>m.Role=="assistant"&&m.Text.Contains("规则已更新")&&m.Text.Contains("杉木")), "产品原生多轮换模型与同文件 MD 更新生效");
    WorkflowJson.Save(Path.Combine(liveRoot,"acceptance.json"),new{live.Chat.Binding.ThreadId,live.Chat.Binding.TurnId,turns=live.Chat.Prompts.Count,checks});
    Console.WriteLine("PASS live product Codex adapter");
}

sealed class CountingHandler(Action count) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { count(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{}",Encoding.UTF8,"application/json")}); }
}

sealed class StreamingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var body=await request.Content!.ReadAsStringAsync(ct);
        var values=body.Contains("formalize.md")?new[]{"{\"cards\":[{\"front\":\"书面问题\",\"back\":\"原背面\",\"typeId\":\"fast-answer\"}]}",""}:new[]{"答案 $x$","\n\n第二段"};
        var stream=string.Concat(values.Select(t=>"data: "+JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=t},finish_reason=(string?)null}}})+"\n\n"));
        stream+="data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        return new(HttpStatusCode.OK){Content=new StringContent(stream,Encoding.UTF8,"text/event-stream")};
    }
}
