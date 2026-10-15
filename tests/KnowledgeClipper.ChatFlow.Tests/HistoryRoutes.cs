global using System.IO;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task HistoryRoutes(string repo)
    {
        Directory.CreateDirectory(data); store=new(data); desktop=Assembly.Load("KnowledgeClipper");
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();var sends=0;
        var serving=Task.Run(async()=>{while(server.IsListening)try{var c=await server.GetContextAsync();if(!c.Request.Url!.AbsolutePath.EndsWith("/models"))Interlocked.Increment(ref sends);var bytes=Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"history-fixture\",\"input_modalities\":[\"text\",\"image\"]}]}");c.Response.ContentType="application/json";await c.Response.OutputStream.WriteAsync(bytes);c.Response.Close();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        var settings=new LanguageSettings{ApiBaseUrl=$"http://127.0.0.1:{port}",Model="history-fixture",ImageModel="history-fixture",LanguageAutoRead=false};
        WorkflowJson.Save(Path.Combine(data,"language-settings.json"),settings);
        WorkflowJson.Save(Path.Combine(data,"chat-preferences.json"),new ChatPreferences("api","history-fixture",null,"fast-answer"));
        var configuration=ModeConfigurationLoader.Load(Path.Combine(repo,"modes"),Path.Combine(data,"modes"));
        using var owner=new Form();owner.Show();
        var initial=desktop.GetType("KnowledgeClipper.UserSettings",true)!.GetMethod("Defaults",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,null);
        flow=desktop.GetType("KnowledgeClipper.LanguageDesktopFlow",true)!.GetConstructors(Flags).Single().Invoke([owner,data,(Action<string>)(_=>{}),initial]);
        ClipDraft Make(string title)=>new(){Original=new(title,null,"generated_allowed",null,"公开历史验收",0,DateTimeOffset.UtcNow),Material=new(title,null,"generated_allowed",null,"公开历史验收",0,DateTimeOffset.UtcNow),Settings=settings,Configuration=configuration,Input=new(Intent:"question"),ModeId="question",Route=new("question","question",true,false)};
        try
        {
            var legacy=Make("旧问答：九加四等于多少？");legacy.Conversation.Add(new(legacy.QuestionText,new("answered","原答案十三。",[])));legacy.EditedAnswers[0]="手工编辑后答案：13。";legacy.PendingFollowUp="保留未发出的输入";store.Save(legacy);
            var verified=Make("已导入的旧问答");verified.Conversation.Add(new(verified.QuestionText,new("answered","冻结答案",[])));verified.Content=new("question",Basic:new(verified.QuestionText,"冻结答案"),Answer:verified.Conversation[0].Answer);verified.ConfirmedNote=AnkiNoteDraft.From(verified);verified.ImportState=ImportState.Verified;store.Save(verified);
            var unknown=Make("待核对的旧问答");unknown.Conversation.Add(new(unknown.QuestionText,new("answered","不能重发的答案",[])));unknown.Content=new("question",Basic:new(unknown.QuestionText,"不能重发的答案"),Answer:unknown.Conversation[0].Answer);unknown.ConfirmedNote=AnkiNoteDraft.From(unknown);unknown.ImportState=ImportState.Unknown;store.Save(unknown);
            var single=Make("无 Conversation 的旧单轮");single.Content=new("question",Basic:new(single.QuestionText,"单轮答案"));store.Save(single);
            var fresh=Make("新版历史");fresh.Chat=new(){Model="history-fixture",Input="原输入"};fresh.Chat.Compositions.Add(new(){Front="原正面",Back="原背面",FrontAnnotation="原批注"});store.Save(fresh);
            var excerpts=new ExcerptStore(data);
            foreach(var original in Environment.GetEnvironmentVariable("KC_HISTORY_GROUPING_ONLY")=="1"?Array.Empty<ClipDraft>():new[]{legacy,verified,unknown,single,fresh})
            {
                var record=new HistoryRecord(Draft:store.Find(original.Id)!);var label=HistoryRecords.ActionText(record,store,excerpts);
                using var history=(Form)desktop.GetType("KnowledgeClipper.RecordHistoryForm",true)!.GetConstructors(Flags).Single().Invoke([(Action<HistoryRecord>)(_=>{}),(Action<HistoryRecord>)(r=>Call(flow,"ContinueHistory",r.Draft)),(Action)(()=>{}),(Action)(()=>{}),(Func<HistoryRecord,string>)(_=>""),(Func<HistoryRecord,string>)(r=>HistoryRecords.ActionText(r,store,excerpts))]);
                Call(history,"SetRecords",new object[]{new[]{record}});history.Show(owner);
                var button=Controls(history).OfType<Button>().Single(b=>b.Name=="knowledge-clipper.history.continue."+record.Key);
                Check(button.Text==label && (original!=verified||label=="查看"),original.Original.SelectedText+"：真实历史按钮状态正确");button.PerformClick();
                var chatForm=Chats().Single();await Ready(chatForm);var chat=Draft(chatForm).Chat!;
                Check(chatForm.Name=="knowledge-clipper.chat" && !((IEnumerable)flow.GetType().GetField("forms",Flags)!.GetValue(flow)!).Cast<Form>().Any(),label+"统一进入新版问答制卡 UI");
                Check(chat.Prompts.Count==0 && sends==0,"打开历史不发送模型或导入请求");
                if(original==legacy)Check(chat.Compositions[0].Back=="手工编辑后答案：13。"&&chat.Input==legacy.PendingFollowUp&&chat.Messages.Count==2,"旧消息、手工答案和未发送输入均保留");
                if(original==verified||original==unknown)Check(chat.Compositions.Single().ExportDraftId==original.Id&&store.Find(original.Id)!.ImportState==original.ImportState,"旧冻结卡复用原身份与导入状态");
                if(original==single)Check(chat.Compositions.Single().Back=="单轮答案"&&chat.Messages.Count==2,"旧单轮正文进入新版卡稿");
                if(original==fresh)Check(chat.Compositions.Single().FrontAnnotation=="原批注"&&chat.Input=="原输入","新版历史保留既有卡稿与输入");
                var messageIds=chat.Messages.Select(m=>m.Id).ToArray();button.PerformClick();await Task.Delay(100);
                Check(Chats().Length==1&&ReferenceEquals(Draft(Chats()[0]),Draft(chatForm))&&chat.Messages.Select(m=>m.Id).SequenceEqual(messageIds),"重复打开复用 live 会话，不重复迁移");
                await Shot(chatForm,"history-"+original.Id);chatForm.Close();history.Close();
                Call(flow,"Resume",original.Id);var reopened=Chats().Single();await Ready(reopened);
                Check(Draft(reopened).Chat!.Messages.Select(m=>m.Id).SequenceEqual(messageIds),"保存重开消息身份保持不变");reopened.Close();
            }
            await GroupedHistoryRoutes(owner,settings,Make,initial!);
            Check(desktop.GetType("KnowledgeClipper.LanguageFollowUpPanel")==null&&flow.GetType().GetMethod("FollowUpAsync",Flags)==null,"废除的追问面板与执行入口已从程序集移除");
            Check(errors.Count==0&&sends==0,"历史全流程无 JS 异常、模型发送及学习库写入");
        }
        finally{((IDisposable)flow).Dispose();server.Stop();}
    }
}
