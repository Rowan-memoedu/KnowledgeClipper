using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;

internal static class TableDelivery
{
 internal static async Task Run(string repo,string output){
  var root=Path.Combine(output,"table-live");Directory.CreateDirectory(root);var store=new DraftStore(root);
  var source=File.ReadAllText(Path.Combine(repo,"tests","table-fixture.md"));
  const string independent="| 名称 | 说明 |\n| :--- | :---: |\n| 甲 | 空值见下一行 |\n| 乙 | |\n| 货币 | 5 美元 & 10% |";
  using var trace=new Trace{InnerHandler=new HttpClientHandler()};using var http=new HttpClient(trace){Timeout=TimeSpan.FromMinutes(3)};
  var receipts=new List<object>();
  foreach(var destination in new[]{ExportDestination.RemNote,ExportDestination.Anki}){
   var idPath=Path.Combine(root,destination+"-id.txt");ClipDraft draft;
   if(File.Exists(idPath))draft=store.Find(File.ReadAllText(idPath))!;
   else{
    var material=new CaptureMaterial(source,null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
    draft=new(){Original=material,Material=material,Settings=WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json"),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),ModeId="question",Content=new("question",Basic:new(source,source))};
    var rendered=ChatMarkdown.Render(source,source,[],independent,independent);
    var note=new AnkiNoteDraft("KnowledgeClipper 验收::表格 20261018",AnkiNoteDraft.BasicModelName,
     new(){["ClipId"]=draft.Id,["Front"]=rendered.FrontHtml,["Back"]=rendered.BackHtml,["Source"]=""},["KnowledgeClipper","kc_"+draft.Id],[0]){Media=rendered.Media};
    var plan=destination==ExportDestination.RemNote?new RemNoteExportPlan("knowledgeclipper."+draft.Id,"零碎知识剪藏",null,["公开验收","表格卡面 20261018"],[rendered.Card]):null;
    new CardExportConfirmation(note,destination,plan).Apply(draft);store.Save(draft);File.WriteAllText(idPath,draft.Id);
   }
   var importer=new CardImportRouter(http);await importer.ImportAsync(draft,store);
   if(draft.ImportState!=ImportState.Verified)throw new Exception("Table import did not verify: "+destination);
   var before=trace.Writes;await importer.ImportAsync(draft,store);if(trace.Writes!=before)throw new Exception("Duplicate table provider write");
   object readback;
   if(destination==ExportDestination.RemNote){
    using var request=new HttpRequestMessage(HttpMethod.Post,RemNoteConnection.DefaultEndpoint+"/sdk/knowledge-clipper/readback");
    request.Headers.Add("X-KnowledgeClipper-Protocol","1");request.Content=JsonContent.Create(draft.ConfirmedRemNote,options:new JsonSerializerOptions(WorkflowJson.Options){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull});
    using var response=await http.SendAsync(request);response.EnsureSuccessStatusCode();var actual=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    var receipt=RemNoteConnection.Validate(draft.ConfirmedRemNote!,actual,draft.RemNoteResult);
    if(receipt.CardIds.Length!=1)throw new Exception("A table must not create per-cell cards");readback=actual;
    WorkflowJson.Save(Path.Combine(root,"remnote-plan.json"),draft.ConfirmedRemNote);
   }else{
    var anki=new AnkiConnection(http,draft.Settings.AnkiEndpoint);
    // The prior run intentionally suspended this public canary after full verification.
    // Rechecking it must not unsuspend it or create a replacement note.
    if(!File.Exists(Path.Combine(root,"Anki-readback.json")))await anki.VerifyAsync(draft);
    var notes=await anki.CallAsync("notesInfo",new{notes=new[]{draft.NoteId!.Value}});
    foreach(var field in draft.ConfirmedNote!.Fields)if(notes[0].GetProperty("fields").GetProperty(field.Key).GetProperty("value").GetString()!=field.Value)throw new Exception("Actual table field differs");
    var actualCards=await anki.CallAsync("cardsInfo",new{cards=draft.CardIds});
    if(actualCards.GetArrayLength()!=1||actualCards[0].GetProperty("note").GetInt64()!=draft.NoteId)throw new Exception("Original public card identity changed");
    await anki.CallAsync("suspend",new{cards=draft.CardIds});
    readback=new{notes,cards=actualCards};
    WorkflowJson.Save(Path.Combine(root,"anki-fixture.json"),new{productionNoteId=draft.NoteId,fieldNames=await anki.CallAsync("modelFieldNames",new{modelName=draft.ConfirmedNote.Model}),
      templates=await anki.CallAsync("modelTemplates",new{modelName=draft.ConfirmedNote.Model}),styling=await anki.CallAsync("modelStyling",new{modelName=draft.ConfirmedNote.Model}),fields=draft.ConfirmedNote.Fields});
   }
   WorkflowJson.Save(Path.Combine(root,destination+"-readback.json"),readback);
   receipts.Add(new{destination=destination.ToString(),draft.Id,draft.NoteId,cardIds=(object?)draft.RemNoteResult?.CardIds??draft.CardIds,remIds=draft.RemNoteResult?.RemIds,state=draft.ImportState.ToString(),duplicateWrites=0});
   Console.WriteLine("PASS actual "+destination+" tables on both faces/annotations, exact readback and no duplicate writes");
  }
  WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{passed=true,receipts,providerWrites=trace.Writes,actions=trace.Actions});
 }
 sealed class Trace:DelegatingHandler{
  public int Writes;public List<string> Actions=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
   var action=request.RequestUri!.AbsolutePath.Split('/').Last();
   if(request.RequestUri.Port==8765&&request.Content!=null)action=JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("action").GetString()!;
   if(action is "export" or "addNote")Writes++;Actions.Add(action);return await base.SendAsync(request,ct);
  }
 }
}
