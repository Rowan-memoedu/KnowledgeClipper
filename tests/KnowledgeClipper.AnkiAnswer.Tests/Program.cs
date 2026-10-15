using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var repo=args[0];var output=args[1];Directory.CreateDirectory(output);
using var http=new HttpClient();
var settings=WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json");
var anki=new AnkiConnection(http,settings.AnkiEndpoint);
var before=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"anki-models-before.json")));
var prepare=typeof(AnkiConnection).GetMethod("PrepareAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
var checks=new List<string>();
void Check(bool value,string message){if(!value)throw new Exception(message);checks.Add(message);Console.WriteLine("PASS "+message);}
const string prefix="{{FrontSide}}<hr id='answer'>";
foreach(var name in new[]{AnkiNoteDraft.BasicModelName,AnkiNoteDraft.QuestionModelName,AnkiNoteDraft.ExcerptModelName})
{
    if(!before.RootElement.TryGetProperty(name,out var original))continue;
    var fields=new Dictionary<string,string>{["ClipId"]="template-maintenance",["Front"]="公开模板核验",["Back"]="公开答案",["Source"]=""};
    if(name==AnkiNoteDraft.QuestionModelName)fields["Context"]="公开上下文";
    var decks=(await anki.DecksAsync()).ToArray();
    var note=new AnkiNoteDraft(decks[0],name,fields,[],[0]);
    await (Task)prepare.Invoke(anki,[note,CancellationToken.None])!;
    var actual=await anki.CallAsync("modelTemplates",new{modelName=name});
    var old=original.GetProperty("templates").GetProperty("正反面");var current=actual.GetProperty("正反面");
    Check(current.GetProperty("Front").GetString()==old.GetProperty("Front").GetString(),name+" keeps front and installed speech controls unchanged");
    var oldBack=old.GetProperty("Back").GetString()!;
    Check(current.GetProperty("Back").GetString()==(oldBack.StartsWith(prefix)?oldBack:prefix+oldBack),name+" changes only the front-and-divider prefix on answer");
    Check((await anki.CallAsync("modelStyling",new{modelName=name})).GetProperty("css").GetString()==original.GetProperty("css").GetProperty("css").GetString(),name+" preserves styling");
}
var store=new DraftStore(Path.Combine(output,"live-fixture"));
var png=Path.Combine(output,"public-pixel.png");
File.WriteAllBytes(png,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
var image=new ImageAsset(png,ImageAsset.Hash(File.ReadAllBytes(png)),"question",1);
for(var sample=0;sample<2;sample++)
{
    var idPath=Path.Combine(output,$"public-draft-{sample}.txt");ClipDraft draft;
    if(File.Exists(idPath))draft=store.Find(File.ReadAllText(idPath))!;
    else
    {
        var material=new CaptureMaterial("公开测试正面",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        draft=new(){Original=material,Material=material,Settings=settings,Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes")),ModeId="question",Content=new("question",Basic:new("公开测试正面","公开测试背面"))};
        var fields=new Dictionary<string,string>{["ClipId"]=draft.Id,["Front"]="<div>公开问题：求正方形面积，边长为3。</div><div>\\(s^2\\)</div>"+(sample==0?image.Html:""),["Back"]="<div>公开答案：面积为9。</div><hr><div>批注：核对平方单位。</div>",["Source"]=""};
        if(sample==1)fields["Context"]="公开上下文：长度单位为厘米。";
        var note=new AnkiNoteDraft("KnowledgeClipper::公开验收::保留正面",sample==0?AnkiNoteDraft.BasicModelName:AnkiNoteDraft.QuestionModelName,fields,["KnowledgeClipper","kc_"+draft.Id],[0]){Media=sample==0?[image]:[]};
        new CardExportConfirmation(note,ExportDestination.Anki,null).Apply(draft);store.Save(draft);File.WriteAllText(idPath,draft.Id);
    }
    if(draft.ImportState!=ImportState.Verified)await anki.ImportAsync(draft,store);
    Check(draft.ImportState==ImportState.Verified,"actual new import verifies original fields and rendered front on answer");
    var noteInfo=(await anki.CallAsync("notesInfo",new{notes=new[]{draft.NoteId!.Value}}))[0];
    var cardIds=noteInfo.GetProperty("cards").EnumerateArray().Select(v=>v.GetInt64()).ToArray();
    var cards=await anki.CallAsync("cardsInfo",new{cards=cardIds});
    var answer=cards[0].GetProperty("answer").GetString()!;var at=answer.IndexOf("<hr id='answer'>",StringComparison.Ordinal);
    Check(at>0&&answer[..at].Contains("公开问题")&&answer[(at+1)..].Contains("公开答案")&&answer[(at+1)..].Contains("批注"),"actual answer keeps question above the divider and answer/annotation below");
    Check(sample==0?answer[..at].Contains(image.FileName):answer[..at].Contains("公开上下文"),"actual front image or source context remains visible on answer");
    WorkflowJson.Save(Path.Combine(output,$"rendered-{sample}.json"),cards);
    File.WriteAllText(Path.Combine(output,$"rendered-{sample}.html"),"<!doctype html><meta charset=utf-8><body class=card>"+answer);
    await anki.CallAsync("suspend",new{cards=cardIds});
    Check((await anki.CallAsync("areSuspended",new{cards=cardIds})).EnumerateArray().All(v=>v.GetBoolean()),"public test cards are suspended and excluded from normal reviews");
}
WorkflowJson.Save(Path.Combine(output,"live-validation.json"),new{passed=true,checks,workflowSha256=ImageAsset.Hash(File.ReadAllBytes(typeof(AnkiConnection).Assembly.Location))});
