using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    static T EmptyContext<T>(string text, string granularity) => default!;
    static async Task EntryRoutes(string repo, bool live)
    {
        if (Directory.Exists(Path.Combine(data, "language-drafts"))) throw new Exception("Use fresh entry acceptance output");
        Directory.CreateDirectory(data); store = new(data); desktop = Assembly.Load("KnowledgeClipper");
        using var server = new HttpListener(); var requests = new ConcurrentQueue<string>();
        var settings = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json") with {
            GlobalSelection = false, ClipboardImageDotEnabled = false, LanguageAutoRead = false, TypedInputAutoRead = false,
            OriginalExcerptHotkey = "", ReplayLastHotkey = "", QuickQuestion = false
        };
        if (!live)
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
            settings = settings with { ApiBaseUrl = $"http://127.0.0.1:{port}", Model = "deepseek-chat", ImageModel = "deepseek-flash" };
            _ = Task.Run(async () => { while (server.IsListening) try {
                var request = await server.GetContextAsync(); string response;
                if (request.Request.Url!.AbsolutePath.EndsWith("/models")) response = "{\"data\":[{\"id\":\"deepseek-flash\"},{\"id\":\"deepseek-v4-pro\"}]}";
                else {
                    requests.Enqueue(await new StreamReader(request.Request.InputStream).ReadToEndAsync());
                    response = "data: {\"choices\":[{\"delta\":{\"content\":\"公开验收：长7宽4的面积是28。\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                }
                request.Response.ContentType = response.StartsWith("data:") ? "text/event-stream" : "application/json";
                await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response)); request.Response.Close();
            } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; } });
        }
        WorkflowJson.Save(Path.Combine(data, "language-settings.json"), settings);
        WorkflowJson.Save(Path.Combine(data, "chat-preferences.json"), new ChatPreferences("codex", "gpt-6.1-sol", "low", "fast-answer"));
        var configType = desktop.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(repo, "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, data);
        var controllerType = desktop.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Flags).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        using var main = (Form)desktop.GetType("KnowledgeClipper.BoundClipperForm", true)!.GetConstructor(Flags, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        Call(main, "EnableLanguageWorkflow"); main.Show(); await Task.Delay(100);
        flow = main.GetType().GetField("languageFlow", Flags)!.GetValue(main)!;
        using (var settingsForm = (Form)desktop.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Flags).Single().Invoke([flow, data]))
        {
            settingsForm.Show(main);
            Controls(settingsForm).OfType<Button>().Single(b=>b.Name=="knowledge-clipper.language.save-settings").PerformClick();
            Check(settingsForm.IsDisposed && WorkflowJson.Read<LanguageSettings>(Path.Combine(data,"language-settings.json")).TypedDefaultRoute == settings.TypedDefaultRoute,
                "实际设置页说明统一问答入口，保存时保留旧默认路径兼容数据");
        }
        var input = (TextBox)main.GetType().GetField("inlineQuestionInput", Flags)!.GetValue(main)!;
        var size = main.Size;
        using var image = new Bitmap(400, 180); using (var graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.White); graphics.DrawRectangle(Pens.DarkGreen, 30, 30, 300, 110);
            graphics.DrawString("7 cm x 4 cm", new Font("Arial", 24), Brushes.Black, 65, 65);
        }
        using var pngStream = new MemoryStream(); image.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png); var png = pngStream.ToArray();
        input.GetType().GetProperty("ContainsClipboardImage", Flags)!.SetValue(input, (Func<bool>)(() => true));
        input.GetType().GetProperty("ReadClipboardImage", Flags)!.SetValue(input, (Func<Image>)(() => new Bitmap(image)));
        async Task Command(Form form, object command) { using var json = JsonDocument.Parse(JsonSerializer.Serialize(command)); await (Task)Call(form, "CommandAsync", json.RootElement)!; }
        async Task Api(Form form)
        {
            await Ready(form);
            await Js(form, "(()=>{const s=document.querySelectorAll('.selectors select')[0];s.value='api';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
            await Wait(form, "document.querySelectorAll('.selectors select')[0].value==='api'&&document.querySelectorAll('.selectors select')[1].options.length>1");
            await Js(form, "(()=>{const m=document.querySelectorAll('.selectors select')[1];m.value='deepseek-v4-pro';m.dispatchEvent(new Event('change',{bubbles:true}));})()");
            await Wait(form, "document.querySelectorAll('.selectors select')[1].value==='deepseek-v4-pro'");
            await Js(form, "(()=>{const m=document.querySelectorAll('.selectors select')[1];m.value='deepseek-flash';m.dispatchEvent(new Event('change',{bubbles:true}));})()");
            await Wait(form, "document.querySelectorAll('.selectors select')[1].value==='deepseek-flash'");
            Check(Draft(form).Chat!.Binding.Provider == "api" && Draft(form).Chat!.Model == "deepseek-flash", "主输入入口在实际页面切换 API 和两个实时模型");
        }
        try
        {
            if (live)
            {
                input.Text = "计算3的平方加4的平方，只给出公式和结果";
                await (Task)Call(main, "SubmitInlineQuestionAsync")!; var nativeForm = Chats().Last(); await Ready(nativeForm);
                await Button(nativeForm, "发送"); await Turn(nativeForm, 0); await Shot(nativeForm, "main-codex-answer");
                Check(Draft(nativeForm).Chat!.Binding.Provider == "codex" && Draft(nativeForm).Chat!.Binding.ThreadId != null && Draft(nativeForm).Chat!.Messages.Last().Text.Contains("25"),
                    "主输入入口使用真实 Codex 完成独立问题，原生 Thread 和正确答案回读成功"); nativeForm.Close();
            }
            input.Text = "请说明长7宽4的矩形面积，不需要末尾问号";
            await (Task)Call(main, "SubmitInlineQuestionAsync")!; var textForm = Chats().Last();
            await Ready(textForm);
            Check(textForm.Name == "knowledge-clipper.chat" && Draft(textForm).Chat!.Input == "请说明长7宽4的矩形面积，不需要末尾问号" && Draft(textForm).Chat!.Prompts.Count == 0,
                "无问号的主输入进入同一问答页面，保留原问题并等待手动发送");
            await Api(textForm); await Button(textForm, "发送"); await Turn(textForm, 0); await Shot(textForm, "main-text-answer");
            Check(Draft(textForm).Chat!.Messages.Last().Text.Length > 0, "主输入提问真实完成回答，无白屏"); textForm.Close();

            SendMessage(input.Handle, 0x0302, IntPtr.Zero, IntPtr.Zero);
            var indicator = Controls(main).OfType<Button>().Single(b => b.Name == "knowledge-clipper.desktop.inline-images");
            Check(indicator.Visible && indicator.Text.Contains("1") && input.Text == "", "原生粘贴接受图片，不插入文件名或覆盖文字");
            input.Text = "根据图片求矩形面积，说明计算过程";
            SendMessage(input.Handle, 0x0302, IntPtr.Zero, IntPtr.Zero); indicator.PerformClick();
            Check(indicator.Text.Contains("1") && input.Text == "根据图片求矩形面积，说明计算过程", "连续粘贴后移除最后一张图片，保留文字和先前图片");
            using (var screenshot = new Bitmap(main.Width, main.Height)) { main.DrawToBitmap(screenshot, main.ClientRectangle); screenshot.Save(Path.Combine(output, "main-mixed-input.png")); }
            await (Task)Call(main, "SubmitInlineQuestionAsync")!; var mixedForm = Chats().Last(); await Ready(mixedForm);
            var mixed = Draft(mixedForm).Chat!;
            Check(mixed.Attachments.Count == 1 && mixed.Input == "根据图片求矩形面积，说明计算过程" && mixed.Attachments[0].ReadVerified().Length > 0 && mixed.Prompts.Count == 0,
                "图文混合输入完整进入同一页面，图片已登记且等待发送");
            Check(input.Text == "" && !indicator.Visible && main.Size == size, "成功提交清空文字和图片，浮窗尺寸保持不变");
            await Api(mixedForm); await Button(mixedForm, "发送"); await Turn(mixedForm, 0); await Shot(mixedForm, "main-mixed-answer");
            Check(mixed.Messages[0].Parts.Any(p => p.Kind == "image") && mixed.Messages[0].Parts.Any(p => p.Kind == "text"), "实际请求和保存回合同时包含图与文字"); mixedForm.Close();
            Check(mixed.Messages.Last().Text.Contains("28"), "实际视觉回答正确读取图中7与4，面积为28");

            SendMessage(input.Handle, 0x0302, IntPtr.Zero, IntPtr.Zero);
            await (Task)Call(main, "SubmitInlineQuestionAsync")!; var imageForm = Chats().Last(); await Ready(imageForm);
            Check(Draft(imageForm).Chat!.Input == "" && Draft(imageForm).Chat!.Attachments.Count == 1, "纯图片也能提交，不注入伪造问题文字"); imageForm.Close();

            // Deliberately restore a different manual preference: shortcut defaults must be independent.
            WorkflowJson.Save(Path.Combine(data, "chat-preferences.json"), new ChatPreferences("codex", "gpt-6.1-sol", "low", "chunk-solution"));
            var preferences = File.ReadAllText(Path.Combine(data, "chat-preferences.json"));
            await (Task<bool>)Call(flow, "StartQuestionInputAsync", "", new byte[][] { png }, true)!;
            var quickImage = Chats().Last(); await Ready(quickImage); await Turn(quickImage, 0); await Shot(quickImage, "quick-image-answer");
            var quick = Draft(quickImage).Chat!;
            Check(quick.Prompts.Count == 1 && quick.Prompts[0].Provider == "api" && quick.Prompts[0].Model == settings.ImageModel && quick.Prompts[0].TypeId == "fast-answer" && quick.Prompts[0].Images.Length == 1,
                "快捷图片问打开页面后自动发送一次，使用 API Flash 和快速问答");
            await Command(quickImage, new { command = "ready" }); await Task.Delay(300);
            Check(quick.Prompts.Count == 1 && File.ReadAllText(Path.Combine(data, "chat-preferences.json")) == preferences && quick.Compositions.All(c => c.ExportDraftId == null),
                "重复就绪事件不重发，不覆盖手动偏好，不自动导出卡片"); quickImage.Close();

            using var source = new Form(); source.Show();
            var capture = flow.GetType().GetField("capture", Flags)!.GetValue(flow)!;
            capture.GetType().GetField("readForeground", Flags)!.SetValue(capture, (Func<long>)(() => source.Handle.ToInt64()));
            var contextField = capture.GetType().GetField("readContext", Flags)!;
            contextField.SetValue(capture, typeof(Program).GetMethod(nameof(EmptyContext), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(contextField.FieldType.GenericTypeArguments.Last()).CreateDelegate(contextField.FieldType));
            var snapshot = desktop.GetType("KnowledgeClipper.SelectionSnapshot", true)!.GetConstructors(Flags).Single(c=>c.GetParameters().Length==9)
                .Invoke(["为什么矩形面积等于长乘宽？", source.Handle.ToInt64(), "公开选区", 100, 100, DateTimeOffset.UtcNow, 0L, 0L, 0U]);
            await (Task)Call(flow, "StartQuickQuestionAsync", snapshot)!;
            var quickText = Chats().Last(); await Ready(quickText); await Turn(quickText, 0); await Shot(quickText, "quick-text-answer");
            Check(Draft(quickText).Chat!.Prompts.Count == 1 && Draft(quickText).Chat!.Prompts[0].TypeId == "fast-answer" && Draft(quickText).Times.ContainsKey("captureStarted"), "选区问通过真实受保护捕获路由，自动给出一轮答案"); quickText.Close();

            await (Task)Call(flow, "StartQuestionAsync", snapshot, false)!;
            var cardEntry = Chats().Last(); await Ready(cardEntry);
            var cardChat = Draft(cardEntry).Chat!;
            Check(cardChat.Binding.Provider == "codex" && cardChat.TypeId == "chunk-solution" && cardChat.Model == "gpt-6.1-sol" && cardChat.Effort == "high" && cardChat.UseQuestionTypeDefaults && cardChat.Prompts.Count == 0,
                "真实浮点卡入口自动匹配 Codex 6.1 sol 和 high，仍等待发送");
            async Task SelectType(string typeId)
            {
                await Js(cardEntry, "(()=>{const s=document.querySelectorAll('.selectors select')[3];s.value="+JsonSerializer.Serialize(typeId)+";s.dispatchEvent(new Event('change',{bubbles:true}));})()");
                var expected = typeId == "fast-answer" ? "low" : "high";
                await Wait(cardEntry, "(()=>{const s=document.querySelectorAll('.selectors select');return s[3].value==="+JsonSerializer.Serialize(typeId)+"&&s[1].value==='gpt-6.1-sol'&&s[2].value==="+JsonSerializer.Serialize(expected)+"&&s[1].disabled&&s[2].disabled})()");
                Check(cardChat.TypeId == typeId && cardChat.Model == "gpt-6.1-sol" && cardChat.Effort == expected, "实际类型选择自动匹配模型与等级："+typeId);
            }
            var types = new ChatConfigurationLoader(Path.Combine(repo,"modes","chat"),Path.Combine(data,"modes","chat")).Load().Types;
            foreach(var type in types) await SelectType(type.Id);
            await SelectType("fast-answer");
            if(live)
            {
                await Input(cardEntry,"计算2加3，仅给出结果"); await Button(cardEntry,"发送"); await Turn(cardEntry,0);
                var nativeId = cardChat.Binding.ThreadId;
                Check(cardChat.Prompts[0].Model == "gpt-6.1-sol" && cardChat.Prompts[0].Effort == "low" && cardChat.Messages.Last().Text.Contains("5"), "快速回答真实 SDK 回合使用 6.1 sol low 并答对独立公开题");
                await SelectType("chunk-solution"); await Input(cardEntry,"计算6乘7，说明计算过程"); await Button(cardEntry,"发送"); await Turn(cardEntry,1);
                Check(cardChat.Binding.ThreadId == nativeId && cardChat.Prompts[0].Effort == "low" && cardChat.Prompts[1].Effort == "high" && cardChat.Prompts[1].Model == "gpt-6.1-sol" && cardChat.Messages.Last().Text.Contains("42"), "切换其他类型真实使用 high，复用同一原生 Thread，既有回合快照不变");
                await Shot(cardEntry,"card-high-answer");
            }
            var cardId=Draft(cardEntry).Id; var beforePrompts=cardChat.Prompts.Count;
            cardEntry.Close(); Call(flow,"Resume",cardId); cardEntry=Chats().Last(); await Ready(cardEntry);
            cardChat=Draft(cardEntry).Chat!;
            Check(cardChat.UseQuestionTypeDefaults && cardChat.Model=="gpt-6.1-sol" && cardChat.Prompts.Count==beforePrompts, "重开保留类型默认策略和历史，不自动重发");
            await Button(cardEntry,"新建对话"); var nextCard=Chats().Last(); await Ready(nextCard);
            Check(Draft(nextCard).Chat!.UseQuestionTypeDefaults && Draft(nextCard).Chat!.Binding.Provider=="codex" && Draft(nextCard).Chat!.Model=="gpt-6.1-sol" && Draft(nextCard).Chat!.Prompts.Count==0, "卡页面新建对话继承自动类型匹配，仍等待输入与发送"); nextCard.Close(); cardEntry.Close();
            WorkflowJson.Save(Path.Combine(data,"chat-preferences.json"),new ChatPreferences("api","deepseek-flash",null,"fast-answer"));
            var imageMaterial=new CaptureMaterial("公开单图卡",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow){FrontImage=mixed.Assets[0],VisualQuestion=true};
            Call(flow,"StartPreparedMaterial",imageMaterial,settings,"question",false); var imageCard=Chats().Last(); await Ready(imageCard);
            Check(Draft(imageCard).Chat!.Binding.Provider=="codex" && Draft(imageCard).Chat!.Model=="gpt-6.1-sol" && Draft(imageCard).Chat!.Effort=="low" && Draft(imageCard).Chat!.Attachments.Count==1, "单图卡入口覆盖原 API 偏好，自动选 6.1 sol low 并保留图片");
            await Api(imageCard);
            await Command(imageCard,new{command="settings",typeId="chunk-solution"});
            Check(Draft(imageCard).Chat!.Binding.Provider=="api" && Draft(imageCard).Chat!.Model=="deepseek-flash" && Draft(imageCard).Chat!.Effort==null, "显式切换 API 后按 API 目录工作，类型切换不破坏服务和模型"); imageCard.Close();

            Call(flow, "SaveSettings", settings with { ImageModel = "invalid-public-model" });
            await (Task<bool>)Call(flow, "StartQuestionInputAsync", "保留这个失败输入", Array.Empty<byte[]>(), true)!;
            var invalid = Chats().Last(); await Ready(invalid); await Wait(invalid, "!!document.querySelector('[role=alert]')");
            await Command(invalid, new { command = "ready" });
            Check(Draft(invalid).Chat!.Prompts.Count == 0 && Draft(invalid).Chat!.Input == "保留这个失败输入", "快捷无效模型保留输入，拒绝静默换模型或自动重试"); invalid.Close();
            Call(flow, "SaveSettings", settings);
            Check(errors.Count == 0 && store.Recent().All(d => d.ImportState != ImportState.Verified), "完整入口验收无 JavaScript 异常和学习库写入");
            if (!live) {
                Check(requests.Count == 4, "四个预期回合各发送一次，无重复后台请求");
                WorkflowJson.Save(Path.Combine(output, "public-fixture-requests.json"), requests.ToArray());
            }
            WorkflowJson.Save(Path.Combine(output, "entry-acceptance.json"), new { passed = true, live, checks, model = settings.ImageModel, desktop = desktop.Location });
        }
        finally { server.Stop(); }
    }
}
