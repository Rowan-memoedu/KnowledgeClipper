using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class AnnotationLive
{
    internal static async Task Run(string repo,string output,bool symmetric=false)
    {
        var root=Path.Combine(output,symmetric?"symmetric-exports":"annotation-live");Directory.CreateDirectory(root);var store=new DraftStore(root);
        var idPath=Path.Combine(root,"draft-id.txt");ClipDraft draft;
        if(File.Exists(idPath))draft=store.Find(File.ReadAllText(idPath))!;
        else
        {
            var imagePath=Path.Combine(output,"symmetric.png");
            var assets=symmetric?new[]{new ImageAsset(imagePath,ImageAsset.Hash(File.ReadAllBytes(imagePath)),"question",1)}:[];
            var picture=symmetric?"\n\n![公开像素图](asset:"+assets[0].Sha256+")":"";
            var rendered=ChatMarkdown.Render("公开验收：求边长为 3 的正方形面积。","# 正文\n\n$S=3^2=9$。\n\n- 解答依据\n  - 正方形面积公式",assets,"核对单位。\n\n# 补充说明\n\n- 面积单位应平方\n  - 不改变题设"+picture,symmetric?"正面批注 $a=3$。\n\n- 保留题设"+picture:"");
            var material=new CaptureMaterial("公开批注结构验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
            draft=new(){Original=material,Material=material,Settings=new(),Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),ModeId="question",Content=new("question",Basic:new("公开验收", "公开答案"))};
            var note=new AnkiNoteDraft("KnowledgeClipper::公开验收",AnkiNoteDraft.BasicModelName,new(){["ClipId"]=draft.Id,["Front"]=rendered.FrontHtml,["Back"]=rendered.BackHtml,["Source"]=""},["KnowledgeClipper","kc_"+draft.Id],[0]){Media=rendered.Media};
            var plan=new RemNoteExportPlan("knowledgeclipper."+draft.Id,"零碎知识剪藏",null,["公开验收",symmetric?"双面批注 20261015":"制卡批注 20261014"],[rendered.Card]);
            new CardExportConfirmation(note,ExportDestination.RemNote,plan).Apply(draft);store.Save(draft);File.WriteAllText(idPath,draft.Id);
        }
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(180)};
        var connection=new RemNoteConnection(http,RemNoteConnection.DefaultEndpoint);
        if(draft.ImportState!=ImportState.Verified)await connection.ImportAsync(draft,store);
        if(draft.ImportState!=ImportState.Verified)throw new Exception("Live annotation import was not verified");
        using var request=new HttpRequestMessage(HttpMethod.Post,RemNoteConnection.DefaultEndpoint+"/sdk/knowledge-clipper/readback");
        request.Headers.Add("X-KnowledgeClipper-Protocol","1");
        request.Content=JsonContent.Create(draft.ConfirmedRemNote,options:new JsonSerializerOptions(WorkflowJson.Options){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull});
        using var response=await http.SendAsync(request);response.EnsureSuccessStatusCode();
        using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var receipt=RemNoteConnection.Validate(draft.ConfirmedRemNote!,document.RootElement,draft.RemNoteResult);
        WorkflowJson.Save(Path.Combine(root,"readback.json"),document.RootElement.Clone());
        WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{passed=true,draftId=draft.Id,receipt.RootRemId,receipt.DocumentRemId,receipt.RemIds,receipt.CardIds,
            publicFixtureOnly=true,productionDataRootUntouched=true,independentMcpAvailable=false,sourceId=draft.ConfirmedRemNote!.SourceId});
        Console.WriteLine("PASS actual RemNote annotation import and exact original node hierarchy readback");
        var ankiIdPath=Path.Combine(root,"anki-draft-id.txt");ClipDraft ankiDraft;
        if(File.Exists(ankiIdPath))ankiDraft=store.Find(File.ReadAllText(ankiIdPath))!;
        else
        {
            ankiDraft=new(){Original=draft.Original,Material=draft.Material,Settings=WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json"),Configuration=draft.Configuration,ModeId="question",Content=draft.Content};
            var original=draft.ConfirmedNote!;var fields=new Dictionary<string,string>(original.Fields){["ClipId"]=ankiDraft.Id};
            var note=original with{Fields=fields,Tags=["KnowledgeClipper","kc_"+ankiDraft.Id]};
            new CardExportConfirmation(note,ExportDestination.Anki,null).Apply(ankiDraft);store.Save(ankiDraft);File.WriteAllText(ankiIdPath,ankiDraft.Id);
        }
        if(ankiDraft.ImportState!=ImportState.Verified)await new AnkiConnection(http,ankiDraft.Settings.AnkiEndpoint).ImportAsync(ankiDraft,store);
        var anki=new AnkiConnection(http,ankiDraft.Settings.AnkiEndpoint);
        Task<JsonElement> Anki(string action,object parameters)=>anki.CallAsync(action,parameters);
        var info=await Anki("notesInfo",new{notes=new[]{ankiDraft.NoteId!.Value}});
        if(new[]{"Front","Back"}.Any(field=>info[0].GetProperty("fields").GetProperty(field).GetProperty("value").GetString()!=ankiDraft.ConfirmedNote!.Fields[field]))throw new Exception("Actual Anki annotation field mismatch");
        var cardIds=info[0].GetProperty("cards").EnumerateArray().Select(c=>c.GetInt64()).ToArray();
        await Anki("suspend",new{cards=cardIds});
        var suspended=await Anki("areSuspended",new{cards=cardIds});
        if(suspended.EnumerateArray().Any(v=>!v.GetBoolean()))throw new Exception("Public Anki fixture should not enter user's review queue");
        WorkflowJson.Save(Path.Combine(root,"anki-readback.json"),info);
        WorkflowJson.Save(Path.Combine(root,"anki-acceptance.json"),new{passed=true,draftId=ankiDraft.Id,noteId=ankiDraft.NoteId,cardIds,publicFixtureSuspended=true});
        Console.WriteLine("PASS actual Anki annotation HTML readback; only public fixture cards suspended");
    }
}
