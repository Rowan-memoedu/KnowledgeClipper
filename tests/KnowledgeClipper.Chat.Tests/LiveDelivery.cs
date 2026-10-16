using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class LiveDelivery
{
    internal static async Task Run(string repo,string output,ExportDestination destination)
    {
        var root=Path.Combine(output,"export-live",destination.ToString());Directory.CreateDirectory(root);
        var store=new DraftStore(root);var idPath=Path.Combine(root,"draft-id.txt");ClipDraft draft;
        var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes"));
        if(File.Exists(idPath))draft=store.Find(File.ReadAllText(idPath))!;
        else {
            var settings=WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json") with {
                AnkiRootDeck="KnowledgeClipper 验收::问答组合 20261009",RemNoteRootTitle="零碎知识剪藏"};
            var material=new CaptureMaterial("公开几何组合卡验收",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
            draft=new(){Original=material,Material=material,Settings=settings,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),
                ModeId="question",Route=new("question","question",true,false),Input=new(Intent:"question"),Chat=new()};
            var fixtures=Directory.GetFiles(Path.Combine(output,"ui","fixture"),"public-0.png",SearchOption.AllDirectories).Order().First();
            for(var i=0;i<4;i++) {
                var path=Path.Combine(Path.GetDirectoryName(fixtures)!,"public-"+i+".png");
                draft.Chat.Assets.Add(new(path,ImageAsset.Hash(File.ReadAllBytes(path)),i<2?"question":"answer",i+1));
            }
            string Image(int index)=>"![公开矩形示意](asset:"+draft.Chat.Assets[index].Sha256+")";
            var materials=new CardComposition{TypeId="fast-answer",ManuallyEdited=true,
                Front="公开组合卡：矩形的长 $a=7$、宽 \\(b=4\\)。\n同一正面第二行。\n\n"+Image(0)+"\n\n"+Image(1)+"\n\n求面积，并说明单位。",
                Back="- 定义与条件：长度单位为厘米。\n  - 面积为 $S=ab$。\n  - 代入得到 \\(S=28\\) 平方厘米。\n- 独立公式\n\n$$\n\\begin{aligned}a&=7\\\\b&=4\\\\S&=ab=28\\end{aligned}\n$$\n\n"+Image(2)+"\n\n"+Image(3)+"\n\n核验 → 保留普通箭头，不新增卡片。"};
            var frontSource=new ChatMessage{Role="assistant",Parts=[new("text",materials.Front)]};draft.Chat.Messages.Add(frontSource);
            var branch=ChatWorkspace.Fork(draft.Chat,frontSource.Id);
            var backSource=new ChatMessage{Role="assistant",Parts=[new("text",materials.Back)]};branch.Session.Messages.Add(backSource);
            var composition=new CardComposition{TypeId="fast-answer"};draft.Chat.Compositions.Add(composition);
            ChatComposition.Select(draft.Chat,composition,frontSource.Id,"front",true);
            ChatComposition.Select(draft.Chat,composition,backSource.Id,"back",true);
            store.Save(draft);File.WriteAllText(idPath,draft.Id);
        }
        var card=draft.Chat!.Compositions.Single();var child=card.ExportDraftId==null?null:store.Find(card.ExportDraftId);
        // A disconnected health check can fail before the first write. Keep the response-loss
        // fault armed across that restart, but never discard recovery-only readback responses.
        using var trace=new ExportTrace(Path.Combine(root,"calls.jsonl")){InnerHandler=new HttpClientHandler(),
            DropWrite=!File.Exists(Path.Combine(root,"lost-provider-receipt.json"))};
        using var http=new HttpClient(trace){Timeout=TimeSpan.FromMinutes(2)};
        try {await ChatComposition.ExportAsync(draft,card,store,config,new CardImportRouter(http),destination);}
        catch(IOException) when(trace.Dropped){Console.WriteLine("PASS live "+destination+" response loss retained original frozen identity");}
        child=store.Find(card.ExportDraftId!)!;
        if(trace.Dropped&&child.ImportState!=ImportState.Unknown)throw new Exception("Lost write did not persist Unknown");
        if(child.ImportState!=ImportState.Verified) {
            var before=trace.Actions.Count;
            draft=store.Find(draft.Id)!;card=draft.Chat!.Compositions.Single();
            await ChatComposition.ExportAsync(draft,card,store,config,new CardImportRouter(http),destination);
            if(trace.Actions.Skip(before).Any(a=>a is "addNote" or "export"))throw new Exception("Recovery repeated a write");
        }
        child=store.Find(card.ExportDraftId!)!;
        if(child.ImportState!=ImportState.Verified)throw new Exception("Live export is not Verified");
        var callsBeforeRepeat=trace.Actions.Count;
        await ChatComposition.ExportAsync(draft,card,store,config,new CardImportRouter(http),destination);
        if(trace.Actions.Count!=callsBeforeRepeat)throw new Exception("Verified was sent to provider again");
        object readback;
        if(destination==ExportDestination.Anki) {
            var anki=new AnkiConnection(http,draft.Settings.AnkiEndpoint);
            await anki.VerifyAsync(child);
            var notes=await anki.CallAsync("notesInfo",new{notes=new[]{child.NoteId!.Value}});
            foreach(var field in child.ConfirmedNote!.Fields)
                if(notes[0].GetProperty("fields").GetProperty(field.Key).GetProperty("value").GetString()!=field.Value)throw new Exception("Actual Anki field changed: "+field.Key);
            foreach(var image in child.ConfirmedNote.Media) {
                var bytes=await anki.CallAsync("retrieveMediaFile",new{filename=image.FileName});
                if(ImageAsset.Hash(Convert.FromBase64String(bytes.GetString()!))!=image.Sha256)throw new Exception("Actual Anki image bytes differ");
            }
            readback=new{notes,cards=await anki.CallAsync("cardsInfo",new{cards=child.CardIds}),imageHashes=child.ConfirmedNote.Media.Select(m=>m.Sha256)};
        } else {
            using var request=new HttpRequestMessage(HttpMethod.Post,RemNoteConnection.ResolveEndpoint(draft.Settings.RemNoteSdkEndpoint).TrimEnd('/')+"/sdk/knowledge-clipper/readback");
            var wireJson=new JsonSerializerOptions(WorkflowJson.Options){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
            request.Headers.Add("X-KnowledgeClipper-Protocol","1");request.Content=JsonContent.Create(child.ConfirmedRemNote,options:wireJson);
            using var response=await http.SendAsync(request);
            var body=await response.Content.ReadAsStringAsync();
            if(!response.IsSuccessStatusCode)throw new InvalidDataException("Live readback failed: "+response.StatusCode+" "+body);
            var raw=JsonDocument.Parse(body).RootElement.Clone();
            RemNoteConnection.Validate(child.ConfirmedRemNote!,raw,child.RemNoteResult);readback=raw;
        }
        WorkflowJson.Save(Path.Combine(root,"acceptance.json"),new{destination,state=child.ImportState.ToString(),child.Id,child.NoteId,child.CardIds,
            remIds=child.RemNoteResult?.RemIds,trace.Dropped,trace.Actions,duplicateProviderCalls=0,
            responseLossReceipt=File.Exists(Path.Combine(root,"lost-provider-receipt.json"))?"lost-provider-receipt.json":null,readback});
        Console.WriteLine("PASS live "+destination+" actual formulas, multiline, four media hashes, Unknown readback and duplicate confirmation");
    }

    private sealed class ExportTrace(string log):DelegatingHandler
    {
        public bool DropWrite;public bool Dropped;public List<string> Actions=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var action=request.RequestUri!.AbsolutePath.Split('/').Last();
            if(request.RequestUri.Port==8765&&request.Content!=null){using var json=JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));action=json.RootElement.GetProperty("action").GetString()!;}
            Actions.Add(action);File.AppendAllText(log,JsonSerializer.Serialize(new{action,utc=DateTimeOffset.UtcNow})+"\n");
            var response=await base.SendAsync(request,ct);
            if(DropWrite&&action is "addNote" or "export"&&response.IsSuccessStatusCode) {
                var body=await response.Content.ReadAsStringAsync(ct);
                using var result=JsonDocument.Parse(body);
                if(action=="addNote"&&result.RootElement.TryGetProperty("error",out var error)&&error.ValueKind!=JsonValueKind.Null)return response;
                WorkflowJson.Save(Path.Combine(Path.GetDirectoryName(log)!,"lost-provider-receipt.json"),result.RootElement);
                DropWrite=false;Dropped=true;response.Dispose();throw new IOException("Controlled response loss after actual provider success");
            }
            return response;
        }
    }
}
