using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class AuxiliaryChecks
{
    internal static async Task Run(string repo, string output, Action<bool,string> check)
    {
        var root=Path.Combine(output,"auxiliary-unit");Directory.CreateDirectory(root);
        var main=new ChatSession{Model="main-model",Effort="low",TypeId="global-motivation",UseQuestionTypeDefaults=true,
            Binding=new(){Provider="codex",ThreadId="original-learning-thread",TurnId="original-learning-turn",Stage="completed"}};
        main.Messages.AddRange([new(){Role="user",Parts=[new("text","矩形长7厘米、宽4厘米，求面积。") ]},
            new(){Role="assistant",Parts=[new("text","$S=7\\times4=28$ 平方厘米。")]}]);
        var card=new CardComposition{Front="",Back="已保存解答：$S=28$，保留逐字原文。",TypeId="fast-answer"};main.Compositions.Add(card);
        main.Auxiliary=new();
        check(main.Auxiliary.Provider=="api"&&main.Auxiliary.Current.Binding.Provider=="api","旧草稿新增辅助设置默认 API，与主 Codex 无关");
        var auxiliary=main.Auxiliary.Current;auxiliary.Model="fixture-api";auxiliary.Effort="high";
        main.TypeId="fast-answer";ChatTypeDefaults.Apply(main);
        check(auxiliary.Model=="fixture-api"&&auxiliary.Effort=="high","主类型默认值切换不改辅助模型和推理");
        main.Model="manual-main";main.Effort="max";
        var material=new CaptureMaterial("公开辅助动作测试",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        var draft=new ClipDraft{Original=material,Material=material,Settings=new(){ImageModel="fixture-api"},Chat=main,
            Configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes"))};
        var store=new DraftStore(root);var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(root,"modes"));
        using var backend=new Handler();using var http=new HttpClient(backend);
        var coordinator=new ChatCoordinator(draft,store,config,new("unused",root),http,()=>"fixture",root,auxiliary);
        var catalog=await coordinator.ModelsAsync("api");
        check(catalog[0].Name=="Fixture Flash"&&catalog[0].Efforts.SequenceEqual(new[]{"low","high","max"})&&catalog[0].DefaultEffort=="high"&&catalog[0].InputModalities.Contains("image"),"API 读取真实目录形状中的名称、推理默认值、能力与图片模态");
        var proposal=ChatComposition.Begin(main,card,"formalize","",["fast-answer"]);
        using(var payload=JsonDocument.Parse(proposal.InputSnapshot))check(payload.RootElement.GetProperty("generateFront").GetBoolean()&&payload.RootElement.GetProperty("context").GetArrayLength()==2&&payload.RootElement.GetProperty("back").GetString()==card.Back,"空正面可仅凭反面和实际上下文建立生成快照");
        var before=JsonSerializer.Serialize(new{main.Model,main.Effort,main.Binding,main.Messages,main.Prompts},WorkflowJson.Options);
        await coordinator.SendAsync(catalog,"formalize",proposal.InputSnapshot,[]);
        check(backend.LastRequest.GetProperty("model").GetString()=="fixture-api"&&backend.LastRequest.GetProperty("reasoning_effort").GetString()=="high","辅助 API 请求实际发送独立模型及推理等级");
        check(before==JsonSerializer.Serialize(new{main.Model,main.Effort,main.Binding,main.Messages,main.Prompts},WorkflowJson.Options),"辅助生成保持主对话的模型、推理、Thread、Turn、消息及快照原样");
        ChatComposition.Parse(proposal,auxiliary.Messages.Last().Text,main,["fast-answer"]);
        check(proposal.Status=="preview"&&card.Front==""&&proposal.Cards[0].Back==card.Back,"空正面生成先形成候选，背面由程序保留，不提前改稿");
        var back=card.Back;ChatComposition.Adopt(main,proposal);
        check(card.Front.Contains("矩形")&&card.Back==back,"明确采用生成问题只写入正面，背面逐字不变");
        var existing=ChatComposition.Begin(main,card,"formalize","",["fast-answer"]);
        var identical=ChatComposition.Begin(main,card,"formalize","",["fast-answer"]);
        check(existing.InputSnapshot!=identical.InputSnapshot&&existing.Id!=identical.Id,"相同材料的辅助候选仍有唯一快照，失败请求不能误绑上一次回答");
        using(var payload=JsonDocument.Parse(existing.InputSnapshot))check(!payload.RootElement.GetProperty("generateFront").GetBoolean()&&payload.RootElement.GetProperty("context").GetArrayLength()==0,"已有正面沿用整理逻辑，不追加整段对话");
        var emptyMain=new ChatSession();var emptyCard=new CardComposition();
        try{ChatComposition.Begin(emptyMain,emptyCard,"formalize","",["fast-answer"]);throw new Exception("Empty material allowed");}catch(InvalidDataException){check(true,"正反面及上下文均空时不发模型请求");}
        emptyCard.Back="仅反面材料";
        using(var onlyBack=JsonDocument.Parse(ChatComposition.Begin(emptyMain,emptyCard,"formalize","",["fast-answer"]).InputSnapshot))
            check(onlyBack.RootElement.GetProperty("back").GetString()==emptyCard.Back&&onlyBack.RootElement.GetProperty("context").GetArrayLength()==0,"无对话时仍允许只根据反面生成问题");
        main.Auxiliary.Provider="codex";var native=main.Auxiliary.Current;native.Model="aux-native-model";native.Effort="low";native.Binding.ThreadId="auxiliary-thread";native.Binding.Stage="unknown";
        main.Auxiliary.Provider="api";
        check(ReferenceEquals(main.Auxiliary.Current,auxiliary)&&main.Auxiliary.Current.Effort=="high","辅助服务来回切换保留各自选择和原绑定");
        store.Save(draft);var restored=store.Find(draft.Id)!.Chat!;
        check(restored.Auxiliary!.Sessions["codex"].Binding.ThreadId=="auxiliary-thread"&&restored.Auxiliary.Sessions["codex"].Binding.Stage=="unknown"&&restored.Binding.ThreadId=="original-learning-thread","重开保留独立辅助 Unknown 身份，主绑定不变");
        check(!JsonSerializer.Serialize(restored,WorkflowJson.Options).Contains("\"current\""),"辅助运行时访问器不写入持久数据");
        var firstPath=Path.Combine(root,"first.png");var secondPath=Path.Combine(root,"second.png");
        var pixel=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII=");
        File.WriteAllBytes(firstPath,pixel);File.WriteAllBytes(secondPath,pixel.Concat(new byte[]{0}).ToArray());
        var first=new ImageAsset(firstPath,ImageAsset.Hash(File.ReadAllBytes(firstPath)),"answer",1);
        var second=new ImageAsset(secondPath,ImageAsset.Hash(File.ReadAllBytes(secondPath)),"answer",2);
        var imageMain=new ChatSession();imageMain.Assets.AddRange([second,first]);
        var imageCard=new CardComposition{Back=$"https://clipper-media.local/{first.Sha256}.png\n\n![续页](asset:{second.Sha256})"};
        var imageProposal=ChatComposition.Begin(imageMain,imageCard,"formalize","",["fast-answer"]);
        check(ChatComposition.ProposalImages(imageProposal,imageMain).Select(a=>a.Sha256).SequenceEqual(new[]{first.Sha256,second.Sha256}),"背面裸显示链接与 asset 图片按阅读顺序变成真实附件");
        using(var payload=JsonDocument.Parse(imageProposal.InputSnapshot))check(payload.RootElement.GetProperty("media")[1].GetProperty("attachmentIndex").GetInt32()==2&&payload.RootElement.GetProperty("media")[1].GetProperty("sources")[0].GetString()=="back","冻结快照标明图片编号和卡面来源");
        check(ChatMarkdown.ReferencedImages($"`![代码](asset:{first.Sha256})`\n\n普通文字 asset:{second.Sha256}",imageMain.Assets).Length==0,"代码与正文中的素材哈希不伪装成图片附件");
        imageCard.Back=$"![首图](asset:{first.Sha256})\n\n![重复](asset:{first.Sha256})";
        check(ChatComposition.ProposalImages(ChatComposition.Begin(imageMain,imageCard,"formalize","",["fast-answer"]),imageMain).Length==1,"重复图片只传一次");
        try{ChatMarkdown.ReferencedImages("![缺图](asset:"+new string('0',64)+")",imageMain.Assets);throw new Exception("Missing image allowed");}catch(InvalidDataException){check(true,"缺失图片发送前拒绝，不能用路径文字冒充识别结果");}
        var imageRules=config.Freeze(auxiliary,imageProposal.InputSnapshot,[first,second],"formalize");
        check(imageRules.Instructions.Contains("原题")&&imageRules.Instructions.Contains("续页")&&imageRules.Instructions.Contains("attachmentIndex"),"实际冻结提示词包含视觉读图、续页和题干答案分离规则");
        var compactCard=new CardComposition{Front="三角形两边8和9，夹角60度。",Back="原答案"};
        check(ChatComposition.Begin(new(),compactCard,"formalize","",["fast-answer"]).InputSnapshot.Contains("三角形两边8和9"),"模型快照使用可读中文，紧邻中文的数值不混入 Unicode 转义");
        auxiliary.Effort="unsupported";var calls=backend.Writes;
        try{await coordinator.SendAsync(catalog,"integrate","保留输入");throw new Exception("Unsupported effort allowed");}catch(InvalidDataException){check(backend.Writes==calls,"无效辅助推理在请求前拒绝，不静默采用主推理");}
    }
    private sealed class Handler:HttpMessageHandler
    {
        internal JsonElement LastRequest;internal int Writes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"id\":\"fixture-api\",\"name\":\"Fixture Flash\",\"input_modalities\":[\"text\",\"image\"],\"effort\":{\"supported_levels\":[\"low\",\"high\",\"max\"],\"default_level\":\"high\"}}]}")};
            Writes++;LastRequest=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            var answer=JsonSerializer.Serialize(new{cards=new[]{new{front="矩形长7厘米、宽4厘米，求其面积。",back="不得采用的模型改写背面",typeId="fast-answer"}}});
            var stream="data: "+JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=answer},finish_reason="stop"}}})+"\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK){Content=new StringContent(stream,Encoding.UTF8,"text/event-stream")};
        }
    }
}
