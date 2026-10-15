using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class TranscriptionChecks
{
    internal static void Run(string repo,string output,Action<bool,string> check)
    {
        var path=Path.Combine(output,"conversion-pixel.png");
        File.WriteAllBytes(path,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/N2kAAAAASUVORK5CYII="));
        var image=new ImageAsset(path,ImageAsset.Hash(File.ReadAllBytes(path)),"answer",1);
        var markdown="前文。\n\n![图片](asset:"+image.Sha256+")\n\n后文。";
        foreach(var side in new[]{"front","back"})
        {
            var chat=new ChatSession();chat.Assets.Add(image);chat.Messages.Add(new(){Parts=[new("text","相关原始上下文")]});
            var card=new CardComposition{Front=side=="front"?markdown:"原问题",Back=side=="back"?markdown:"原答案",Annotation="原批注",AnnotationEnabled=true};chat.Compositions.Add(card);
            var front=card.Front;var back=card.Back;var proposal=ChatComposition.Begin(chat,card,"transcribe","",["fast-answer"],side);
            using(var snapshot=JsonDocument.Parse(proposal.InputSnapshot))check(snapshot.RootElement.GetProperty("targetSide").GetString()==side&&snapshot.RootElement.GetProperty("context").GetArrayLength()==1,"图转文字冻结目标卡面与真实上下文："+side);
            var converted="前文。\n\n识别正文 $x^2$。\n\n后文。";
            ChatComposition.Parse(proposal,JsonSerializer.Serialize(new{cards=new[]{new{front=side=="front"?converted:"模型不应修改的正面",back=side=="back"?converted:"模型不应修改的背面",typeId="fast-answer"}}}),chat,["fast-answer"]);
            check(proposal.Status=="preview"&&(side=="front"?proposal.Cards[0].Back==back:proposal.Cards[0].Front==front)&&card.Front==front&&card.Back==back,"图转文字候选不提前改稿，代码锁定另一卡面："+side);
            ChatComposition.Adopt(chat,proposal);
            check((side=="front"?card.Front:card.Back)==converted&&card.Annotation=="原批注","采用识别正文只替换指定卡面并保留批注："+side);
            ChatComposition.Undo(card);
            check(card.Front==front&&card.Back==back&&card.Annotation=="原批注"&&ChatComposition.ProposalImages(proposal,chat)[0].ReadVerified().Length>0,"撤销图转文字恢复完整原图、周边文字、另一卡面和批注："+side);
            var bad=ChatComposition.Begin(chat,card,"transcribe","",["fast-answer"],side);
            ChatComposition.Parse(bad,JsonSerializer.Serialize(new{cards=new[]{new{front,back,typeId="fast-answer"}}}),chat,["fast-answer"]);
            check(bad.Status=="unparsed","仍返回图片占位的伪转换拒绝采用："+side);
        }
        var blank=new ChatSession();var empty=new CardComposition{Front="纯文字",Back="原答案"};
        try{ChatComposition.Begin(blank,empty,"transcribe","",["fast-answer"],"front");throw new Exception("Text-only conversion allowed");}catch(InvalidDataException){check(true,"无图片卡面在发送前拒绝图转文字");}
        var config=new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(output,"no-overrides"));
        check(config.Freeze(new(){Model="fixture"},"视觉输入",[image],"transcribe").Instructions.Contains("targetSide"),"独立辅助动作冻结实际图转文字规则");
        foreach(var both in new[]{false,true})
        {
            var chat=new ChatSession();chat.Assets.Add(image);
            var card=new CardComposition{Front="保持原问题",Back=both?markdown:"纯文字正文 $x^2$",Annotation=markdown,AnnotationEnabled=true};chat.Compositions.Add(card);
            var originalBack=card.Back;var proposal=ChatComposition.Begin(chat,card,"transcribe","",["fast-answer"],"back");
            using(var input=JsonDocument.Parse(proposal.InputSnapshot))check(input.RootElement.GetProperty("annotation").GetString()==markdown&&input.RootElement.GetProperty("media")[0].GetProperty("sources").EnumerateArray().Any(v=>v.GetString()=="annotation"),"正文/纯批注图片冻结原批注与附件归属："+both);
            ChatComposition.Parse(proposal,JsonSerializer.Serialize(new{cards=new[]{new{front="不应修改",back="正文识别结果",annotation="批注前文。识别公式 $a=b$。批注后文。",typeId="fast-answer"}}}),chat,["fast-answer"]);
            check(proposal.Status=="preview"&&proposal.Cards[0].Front=="保持原问题"&&proposal.Cards[0].Annotation.Contains("a=b")&&proposal.Cards[0].Back==(both?"正文识别结果":originalBack),"批注图片识别留在批注，无图正文逐字锁定："+both);
            ChatComposition.Adopt(chat,proposal);ChatComposition.Undo(card);
            check(card.Back==originalBack&&card.Annotation==markdown&&card.AnnotationEnabled,"一次撤销同时恢复正文和批注原图："+both);
            var missing=ChatComposition.Begin(chat,card,"transcribe","",["fast-answer"],"back");
            ChatComposition.Parse(missing,"{\"cards\":[{\"front\":\"原问题\",\"back\":\"正文识别结果\",\"typeId\":\"fast-answer\"}]}",chat,["fast-answer"]);
            check(missing.Status=="unparsed"&&card.Annotation==markdown,"遗漏批注识别结果时整次不采用，保留原图："+both);
        }
        foreach(var sameFirst in new[]{false,true})
        {
            var chat=new ChatSession();var original=new CardComposition{Front="原问题",Back="原答案",Annotation="原批注",AnnotationEnabled=true};chat.Compositions.Add(original);
            var proposal=ChatComposition.Begin(chat,original,"integrate","",["fast-answer"]);
            var first=sameFirst?"原问题":"整理后的问题";
            ChatComposition.Parse(proposal,JsonSerializer.Serialize(new{cards=new[]{new{front=first,back="原答案",typeId="fast-answer"},new{front="另一张问题",back="另一张答案",typeId="fast-answer"}}}),chat,["fast-answer"]);
            ChatComposition.Adopt(chat,proposal);
            check(chat.Compositions.Count==2&&original.Annotation=="原批注","整合多卡同时保留原批注："+sameFirst);
            var serialized=JsonSerializer.Serialize(chat,WorkflowJson.Options);chat=JsonSerializer.Deserialize<ChatSession>(serialized,WorkflowJson.Options)!;original=chat.Compositions[0];
            ChatComposition.Undo(original,chat);
            check(chat.Compositions.Count==1&&original.Front=="原问题"&&original.Back=="原答案"&&original.Annotation=="原批注","重开后一次撤销整合恢复原卡并撤回新增卡，含首卡未变情况："+sameFirst);
        }
        {
            var chat=new ChatSession();var original=new CardComposition{Front="原问题",Back="原答案"};chat.Compositions.Add(original);
            var p=ChatComposition.Begin(chat,original,"integrate","",["fast-answer"]);
            ChatComposition.Parse(p,"{\"cards\":[{\"front\":\"更新问题\",\"back\":\"更新答案\",\"typeId\":\"fast-answer\"},{\"front\":\"新增问题\",\"back\":\"新增答案\",\"typeId\":\"fast-answer\"}]}",chat,["fast-answer"]);ChatComposition.Adopt(chat,p);
            var extra=chat.Compositions[1];ChatComposition.Edit(extra,"用户后续改稿",extra.Back,extra.TypeId);
            try{ChatComposition.Undo(original,chat);throw new Exception("Edited card removed");}catch(InvalidOperationException){check(chat.Compositions.Count==2&&extra.Front=="用户后续改稿"&&original.Front=="更新问题","撤销整合拒绝丢弃新增卡的后续手工改稿，整批状态保持不变");}
            ChatComposition.Undo(extra,chat);ChatComposition.Undo(original,chat);
            check(chat.Compositions.Count==1&&original.Front=="原问题","撤回新增卡后续编辑后可继续撤销整合，不依赖旧修订号");
        }
    }
}
