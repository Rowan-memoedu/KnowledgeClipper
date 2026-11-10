using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var previous=Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if(args.Contains("--live"))
{
    var liveSettings=new LanguageSettings { ApiBaseUrl="https://api.openai.com/v1",Model="gpt-6.1-sol" };
    using var liveHttp=new HttpClient();
    var liveModule=new ContentModule(liveHttp,()=>throw new Exception("domestic key must not be used"));
    var answer="";
    await liveModule.StreamChatAsync("Answer the arithmetic question briefly.",[],"What is 7 times 8?",[],liveSettings,
        text=>{answer+=text;return Task.CompletedTask;},default,"high");
    if(!answer.Contains("56"))throw new Exception("Live streaming arithmetic failed");
    var config=ModeConfigurationLoader.Load(Path.Combine(args[0],"modes"));
    var translated=await liveModule.ProcessAsync(new CaptureMaterial("好好学习", "好好学习", "source",0,"synthetic",0,DateTimeOffset.UtcNow),"translation",config,
        liveSettings with {Model="gpt-6-luna"},default);
    if(string.IsNullOrWhiteSpace(translated.Basic?.Back))throw new Exception("Live structured translation failed");
    var imagePath=Path.Combine(args[1],"public-red-square.png");
    var asset=new ImageAsset(imagePath,ImageAsset.Hash(File.ReadAllBytes(imagePath)),"question",1);
    var imageMaterial=new CaptureMaterial("图片是什么颜色？",null,"source",null,"synthetic image",0,DateTimeOffset.UtcNow)
        {VisualQuestion=true,FrontImage=asset};
    var imageAnswer=await liveModule.AnswerAsync(imageMaterial,null,[],"图片是什么颜色？",config,
        liveSettings with {ImageModel="gpt-6.1-sol"},default);
    if(!imageAnswer.Answered || !imageAnswer.AnswerText!.Contains("红"))throw new Exception("Live image answer failed");
    Directory.CreateDirectory(args[1]);
    WorkflowJson.Save(Path.Combine(args[1],"live.json"),new {stream=answer,structured=translated,image=imageAnswer,externalWrites=false});
    Console.WriteLine("PASS live OpenAI streaming, application-schema translation and image answer; no external note writes");
    return;
}
try
{
    Environment.SetEnvironmentVariable("OPENAI_API_KEY","synthetic-openai-test");
    var settings=new LanguageSettings { ApiBaseUrl="https://api.openai.com/v1",Model="gpt-6.1-sol" };
    foreach(var complete in new[]{true,false})
    {
        using var http=new HttpClient(new Handler(complete));
        var module=new ContentModule(http,()=>throw new Exception("OpenAI must not request a domestic key"));
        var output="";var failed=false;
        try { await module.StreamChatAsync("synthetic instructions",[new ChatMessage {Role="assistant",Parts=[new("text","previous reply")] }],"public arithmetic question",[],settings,s=>{output+=s;return Task.CompletedTask;},default,"high"); }
        catch(IOException){failed=true;}
        if(output!="synthetic answer" || failed==complete)throw new Exception("stream completion/partial-output contract failed");
    }
    using var doc=JsonDocument.Parse("{\"status\":\"incomplete\",\"output\":[]}");
    var rejected=false;try{OpenAiTransport.Text(doc.RootElement);}catch(InvalidDataException){rejected=true;}
    if(!rejected)throw new Exception("incomplete JSON response accepted");
    var retained=new LanguageSettings();
    if(OpenAiTransport.Credential(retained,()=>"legacy-synthetic")!="legacy-synthetic")throw new Exception("retained route changed");
    Console.WriteLine("PASS OpenAI streaming success, interrupted output, stateless history and isolated retained credentials");
}
finally{Environment.SetEnvironmentVariable("OPENAI_API_KEY",previous);}

sealed class Handler(bool completed):HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(request.RequestUri!.AbsolutePath!="/v1/responses" || request.Headers.Authorization!.Parameter!="synthetic-openai-test")throw new Exception("incorrect OpenAI route");
        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=body.RootElement;
        if(root.GetProperty("store").GetBoolean() || root.TryGetProperty("messages",out _) || root.GetProperty("input")[1].GetProperty("content")[0].GetProperty("type").GetString()!="output_text")throw new Exception("incorrect history/request conversion");
        var stream="data: {\"type\":\"response.output_text.delta\",\"delta\":\"synthetic answer\"}\n\n";
        if(completed)stream+="data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"synthetic answer\"}]}]}}\n\n";
        return new(HttpStatusCode.OK){Content=new StringContent(stream,Encoding.UTF8,"text/event-stream")};
    }
}
