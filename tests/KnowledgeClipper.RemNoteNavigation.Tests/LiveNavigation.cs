using System.Net.Http.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class LiveNavigation
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    private static bool RemNoteIsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(),out var processId);
        try{return Process.GetProcessById((int)processId).ProcessName.Equals("RemNote",StringComparison.OrdinalIgnoreCase);}
        catch(ArgumentException){return false;}
    }
    internal static async Task RunAsync(string output)
    {
        var store=new ExcerptStore(Path.Combine(output,"data"));
        var endpoint=RemNoteConnection.DefaultEndpoint;
        using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(3)};
        http.DefaultRequestHeaders.Add("X-KnowledgeClipper-Protocol","1");
        var client=new RemNoteConnection(http,endpoint);
        var health=await client.HealthAsync();
        if(!health.GetProperty("capabilities").EnumerateArray().Any(c=>c.GetString()=="knowledge-clipper.import-navigation.v1"))
            throw new InvalidOperationException("The active RemNote plugin has not loaded navigation support; no fixtures imported.");
        var manifest=Path.Combine(output,"sessions.json");
        string[] sessions;
        if(File.Exists(manifest))sessions=WorkflowJson.Read<string[]>(manifest);
        else
        {
            // Only the selected root ID is reused; personal settings and drafts are never rewritten.
            var personal=WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
            var settings=new LanguageSettings{RemNoteRootId=personal.RemNoteRootId,RemNoteRootTitle=personal.RemNoteRootTitle};
            var material=new CaptureMaterial("Public import navigation acceptance",null,"unavailable",null,"public test fixture",0,DateTimeOffset.UtcNow);
            var mode=new ModeSnapshot(new("navigation-public-fixture","定位验收-"+Guid.NewGuid().ToString("N")[..10],true,"","","","excerpt",RequiresContext:false),"","");
            var png=Path.Combine(output,"public.png");
            // Valid 8 x 8 RGB PNG with checked chunk CRCs; no private screenshot material.
            File.WriteAllBytes(png,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGMwaDiAFTEMLQkAztVcAX2XhiIAAAAASUVORK5CYII="));
            var asset=new ImageAsset(png,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(png))).ToLowerInvariant(),"question",1);
            var fields=new[]{new ExcerptFields("Public single paragraph.",""),new ExcerptFields("Public paragraph one.\nPublic paragraph two.",""),
                new ExcerptFields("Public question.","First public answer.\nSecond public answer."),
                new ExcerptFields("The {{c1::Earth}} orbits the Sun.","",ExcerptCardKind.Cloze),
                new ExcerptFields("",""){FrontImages=[asset]},new ExcerptFields("Public image caption.","Public image answer."){FrontImages=[asset],BackImages=[asset]}};
            var created=fields.Select((field,index)=>{
                var session=new ExcerptSession{Original=material,Settings=settings};
                ExcerptCards.Add(session,store,field,"public-case-"+index,mode);
                return session.Id;
            }).ToList();
            var batch=new ExcerptSession{Original=material,Settings=settings};
            ExcerptCards.Add(batch,store,new("Public batch first.",""),"batch-first",mode);
            ExcerptCards.Add(batch,store,new("Public batch final.","Public final answer."),"batch-final",mode);
            created.Add(batch.Id);
            sessions=created.ToArray();
            WorkflowJson.Save(manifest,sessions);
        }
        var checks=new List<string>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks.Add(label);Console.WriteLine("PASS "+label);}
        var router=new CardImportRouter(http);
        foreach(var id in sessions)
        {
            var remNoteWasForeground=RemNoteIsForeground();
            var session=store.Find(id)!;
            var before=store.Cards.Find(session.Cards[^1].Id)!;
            await ExcerptCards.ImportAsync(session,store,router,session.Cards.Select(c=>c.Id));
            var draft=store.Cards.Find(session.Cards[^1].Id)!;
            Check(session.Cards.All(c=>store.Cards.Find(c.Id)!.ImportState==ImportState.Verified),"case "+Array.IndexOf(sessions,id)+" imported and independently verified");
            // If a previous run already verified the import, exercise navigation explicitly without another export.
            if(before.ImportState==ImportState.Verified) {
                await client.RevealAsync(draft);
                draft.RemNoteRevealError=null;store.Cards.Save(draft);
            }
            else if(draft.RemNoteRevealError!=null)throw new Exception("Imported, navigation failed: "+draft.RemNoteRevealError);
            await Task.Delay(700);
            var state=await http.GetFromJsonAsync<JsonElement>(endpoint+"/sdk/knowledge-clipper/reveal-state");
            WorkflowJson.Save(Path.Combine(output,"state-"+Array.IndexOf(sessions,id)+".json"),state);
            Check(state.GetProperty("documentRemId").GetString()==draft.RemNoteResult!.DocumentRemId &&
                state.GetProperty("remIds").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(draft.RemNoteResult.RemIds),
                "case "+Array.IndexOf(sessions,id)+" real editor keeps exact document and content block selection");
            var wire=new JsonSerializerOptions(WorkflowJson.Options){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
            using var response=await http.PostAsJsonAsync(endpoint+"/sdk/knowledge-clipper/readback",draft.ConfirmedRemNote,wire);
            if(!response.IsSuccessStatusCode)throw new Exception("Independent raw readback failed: "+await response.Content.ReadAsStringAsync());
            var raw=await response.Content.ReadFromJsonAsync<JsonElement>();
            RemNoteConnection.Validate(draft.ConfirmedRemNote!,raw,draft.RemNoteResult);
            WorkflowJson.Save(Path.Combine(output,"readback-"+Array.IndexOf(sessions,id)+".json"),raw);
            // The user may switch between other applications while the test runs.
            // Detect RemNote activation itself instead of assuming the desktop is idle.
            Check(remNoteWasForeground||!RemNoteIsForeground(),"case "+Array.IndexOf(sessions,id)+" does not activate RemNote");
        }
        var last=store.Cards.Find(store.Find(sessions[^1])!.Cards[^1].Id)!;
        var retry=store.Find(sessions[0])!;
        await ExcerptCards.ImportAsync(retry,store,router,retry.Cards.Select(c=>c.Id));
        var after=await http.GetFromJsonAsync<JsonElement>(endpoint+"/sdk/knowledge-clipper/reveal-state");
        Check(after.GetProperty("remIds").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(last.RemNoteResult!.RemIds),"retrying an old verified import preserves latest selection");
        WorkflowJson.Save(Path.Combine(output,"validation.json"),new{passed=true,checks,sessions,workflowSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ExcerptCards).Assembly.Location))).ToLowerInvariant(),scope="Real SDK writes of public fixtures, real editor ID readback, raw content and image hash validation; no user content edits or window activation."});
    }
}
