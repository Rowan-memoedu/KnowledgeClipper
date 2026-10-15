using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args[0] == "--chat") return ChatMain(args);
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static async Task Run(string[] args)
    {
        var mode=args[0]; var repo=args[1]; var output=args[2]; Directory.CreateDirectory(output);
        if(mode=="--history") { History(args[3], output); return; }
        if(mode is not ("--live" or "--live-anki"))throw new ArgumentException("--history repo output data | --live[‑anki] repo output target-id | --chat repo output release");
        foreach(var destination in mode=="--live-anki" ? new[]{ExportDestination.Anki} : new[]{ExportDestination.Anki,ExportDestination.RemNote})
        foreach(var kind in new[]{"plain","mixed"})
        {
            var root=Path.Combine(output,destination+"-"+kind);Directory.CreateDirectory(root);
            var store=new DraftStore(root);var idFile=Path.Combine(root,"draft-id.txt");
            var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes"));
            WorkflowJson.Save(Path.Combine(config.OverrideRoot,"question-types.json"), new ChatConfiguration([
                new("fast-answer","性能验收 20261016","types/fast-answer.md","few-shots/fast-answer.md",[],DocumentId:args[3])
            ],"2TzilXHPwxnJzXY24","aEcPOoSEnv0usSg3r"));
            ClipDraft draft;
            if(File.Exists(idFile))draft=store.Find(File.ReadAllText(idFile))!;
            else
            {
                var material=new CaptureMaterial("公开性能验收：长方形面积",null,"generated_allowed",null,"公开 fixture",0,DateTimeOffset.UtcNow);
                draft=new(){Original=material,Material=material,Input=new(Intent:"question"),ModeId="question",Route=new("question","question",true,false),
                    Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),Settings=new(){AnkiRootDeck="KnowledgeClipper 验收::性能 20261016"},Chat=new()};
                var front="长方形长 7 厘米、宽 4 厘米。求面积。";
                var back="- 已知条件\n  - 长为 7 厘米。\n  - 宽为 4 厘米。\n- 计算\n  - $S=ab=7\\times4=28$。\n- 结论\n  - 面积为 28 平方厘米。";
                if(kind=="mixed")
                {
                    for(var i=0;i<4;i++)
                    {
                        var path=Path.Combine(root,"public-"+i+".png");
                        using(var bitmap=new System.Drawing.Bitmap(180,90))
                        {using var g=System.Drawing.Graphics.FromImage(bitmap);g.Clear(System.Drawing.Color.White);g.DrawRectangle(System.Drawing.Pens.Green,10+i,10,140,60);bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);}
                        var image=new ImageAsset(path,ImageAsset.Hash(File.ReadAllBytes(path)),i<2?"question":"answer",i+1);
                        draft.Chat.Assets.Add(image);
                        if(i<2)front+="\n\n![公开矩形](asset:"+image.Sha256+")";else back+="\n\n![公开矩形](asset:"+image.Sha256+")";
                    }
                }
                draft.Chat.Compositions.Add(new(){TypeId="fast-answer",Front=front,Back=back,FrontAnnotation="保留条件",Annotation="单位为平方厘米",ManuallyEdited=true});
                store.Save(draft);File.WriteAllText(idFile,draft.Id);
            }
            using var trace=new Trace{InnerHandler=new HttpClientHandler()};using var http=new HttpClient(trace){Timeout=TimeSpan.FromMinutes(3)};
            var clock=Stopwatch.StartNew();var card=draft.Chat!.Compositions.Single();
            try {await ChatComposition.ExportAsync(draft,card,store,config,new CardImportRouter(http),destination);}
            finally {WorkflowJson.Save(Path.Combine(root,"timing.json"),new{elapsedMs=clock.Elapsed.TotalMilliseconds,calls=trace.Calls});}
            var child=store.Find(card.ExportDraftId!)!;
            if(child.ImportState!=ImportState.Verified)throw new Exception("Import did not verify: "+child.ImportError);
            if(child.RemNoteRevealError!=null)throw new Exception("Navigation: "+child.RemNoteRevealError);
            var calls=trace.Calls.Count;
            await ChatComposition.ExportAsync(draft,card,store,config,new CardImportRouter(http),destination);
            if(calls!=trace.Calls.Count)throw new Exception("Verified export repeated a provider call");
            if(destination==ExportDestination.Anki)await new AnkiConnection(http,draft.Settings.AnkiEndpoint).VerifyAsync(child);
            else
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,RemNoteConnection.DefaultEndpoint+"/sdk/knowledge-clipper/readback");
                request.Headers.Add("X-KnowledgeClipper-Protocol","1");request.Content=JsonContent.Create(child.ConfirmedRemNote,options:new JsonSerializerOptions(WorkflowJson.Options){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull});
                using var response=await http.SendAsync(request);response.EnsureSuccessStatusCode();
                var raw=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
                RemNoteConnection.Validate(child.ConfirmedRemNote!,raw,child.RemNoteResult);
                WorkflowJson.Save(Path.Combine(root,"readback.json"),raw);
            }
            WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{passed=true,destination,kind,child.Id,child.NoteId,child.CardIds,remIds=child.RemNoteResult?.RemIds,verifiedDuplicateCalls=0});
            Console.WriteLine($"PASS live {destination} {kind}; {calls} calls; timing.json saved");
        }
    }
    static void History(string root,string output)
    {
        var files=new[]{"language-drafts","excerpt-cards","excerpt-sessions"}.SelectMany(folder=>Directory.GetFiles(Path.Combine(root,folder),"*.json")).ToArray();
        string HashFiles()=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",files.Order().Select(f=>f+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))))));
        var before=HashFiles();var results=new List<object>();string? signature=null;
        for(var iteration=0;iteration<6;iteration++)
        {
            var clock=Stopwatch.StartNew();var drafts=new DraftStore(root);var excerpts=new ExcerptStore(root);
            if(typeof(DraftStore).GetMethod("CreateReadSnapshot") is {} create)drafts=(DraftStore)create.Invoke(drafts,null)!;
            if(typeof(ExcerptStore).GetMethod("CreateReadSnapshot") is {} createEx)excerpts=(ExcerptStore)createEx.Invoke(excerpts,null)!;
            var records=HistoryRecords.Read(drafts,excerpts);
            var loaded=clock.Elapsed.TotalMilliseconds;
            var projection=records.Select(r=>new {r.Key,r.Kind,r.Original,action=HistoryRecords.ActionText(r,drafts,excerpts),
                status=r.Draft?.Chat!=null&&r.Kind=="问答"?HistoryRecords.ChatStatus(r.Draft,drafts):r.Excerpt!=null?
                    string.Join(",",r.Excerpt.Cards.Where(c=>c.SupersededBy==null).Select(c=>excerpts.Cards.Find(c.Id)?.ImportState)):""}).ToArray();
            var elapsed=clock.Elapsed.TotalMilliseconds;
            var current=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(projection,WorkflowJson.Options)));
            if(signature!=null&&signature!=current)throw new Exception("History changed between reads");signature=current;
            results.Add(new{iteration,records=records.Length,loadedMs=loaded,totalMs=elapsed});
            Console.WriteLine($"history {iteration}: {records.Length} rows, {elapsed:F1} ms");
        }
        var after=HashFiles();if(before!=after)throw new Exception("Source files changed; inspect before claiming read-only acceptance");
        WorkflowJson.Save(Path.Combine(output,"history.json"),new{results,signature,sourceFiles=files.Length,sourceHash=before,sourceUnchanged=true});
    }
    sealed class Trace:DelegatingHandler
    {
        public List<object> Calls=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var action=request.RequestUri!.AbsolutePath.Split('/').Last();
            if(request.RequestUri.Port==8765&&request.Content!=null)action=JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("action").GetString()!;
            var clock=Stopwatch.StartNew();var result=await base.SendAsync(request,ct);
            await result.Content.LoadIntoBufferAsync();lock(Calls)Calls.Add(new{action,elapsedMs=clock.Elapsed.TotalMilliseconds,status=(int)result.StatusCode});
            return result;
        }
    }
}
