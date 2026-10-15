using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    static async Task BackImagesUi(string repo, bool live)
    {
        Directory.CreateDirectory(data); store = new(data); desktop = Assembly.Load("KnowledgeClipper");
        ImageAsset Screenshot(string name, params string[] lines)
        {
            var path = Path.Combine(data, name + ".png");
            using var font = new Font("Microsoft YaHei", 36, GraphicsUnit.Pixel);
            using var measuring = new Bitmap(1, 1); using var measure = Graphics.FromImage(measuring);
            var width = Math.Max(900, (int)Math.Ceiling(lines.Max(line => measure.MeasureString(line, font).Width)) + 100);
            using var bitmap = new Bitmap(width, 140 + lines.Length * 70);
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
            for (var i = 0; i < lines.Length; i++) graphics.DrawString(lines[i], font, Brushes.Black, 50, 35 + i * 70);
            bitmap.Save(path, ImageFormat.Png);
            return new(path, ImageAsset.Hash(File.ReadAllBytes(path)), "answer", 1);
        }
        var physics = Screenshot("single-question-answer", "题目", "质量为 5 kg 的物体在光滑水平面上运动。", "物体受到水平方向 20 N 的合力，求加速度。", "参考答案与解析", "由牛顿第二定律 F = ma，得 a = 4 m/s²。");
        var page1 = Screenshot("question-page-1", "题目（第 1 页）", "已知函数 f(x) = x² - 4x + 1，定义域为 [0, 5]。", "（1）求函数在该区间上的最小值和最大值。", "（第 2 小问见下页）");
        var page2 = Screenshot("question-page-2", "题目续页（第 2 页）", "（2）求方程 f(x) = 0 的所有实数根。", "参考答案与解析", "f(x) = (x - 2)² - 3。", "最小值为 -3，最大值为 6。", "方程的根为 2 ± √3。");
        using var server = new HttpListener(); Task? serving = null; var requests = new List<JsonElement>();
        var settings = live ? WorkflowJson.Read<LanguageSettings>("D:/AppData/KnowledgeClipper/language-settings.json") : new LanguageSettings();
        if (!live)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            settings = settings with { ApiBaseUrl = $"http://127.0.0.1:{port}", Model = "vision-fixture", ImageModel = "vision-fixture" };
            server.Prefixes.Add(settings.ApiBaseUrl + "/"); server.Start();
            serving = Task.Run(async () => { while (server.IsListening) try {
                var request = await server.GetContextAsync(); string response;
                if (request.Request.Url!.AbsolutePath.EndsWith("/models")) response = "{\"data\":[{\"id\":\"vision-fixture\",\"input_modalities\":[\"text\",\"image\"]}]}";
                else {
                    using var payload = JsonDocument.Parse(await new StreamReader(request.Request.InputStream).ReadToEndAsync());
                    lock (requests) requests.Add(payload.RootElement.Clone());
                    using var snapshot=JsonDocument.Parse(payload.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("text").GetString()!);
                    var transcribe=snapshot.RootElement.GetProperty("action").GetString()=="transcribe";
                    var front = snapshot.RootElement.GetProperty("back").GetString()!.Contains(page1.Sha256) ? "已知 $f(x)=x^2-4x+1$，定义域 $[0,5]$。（1）求最小值与最大值。（2）求 $f(x)=0$ 的实数根。" : snapshot.RootElement.GetProperty("front").GetString()=="三角形两边8和9夹角60度面积？" ? "已知三角形两边长为8和9，夹角为60度，求面积。" : "质量5 kg，水平合力20 N，光滑水平面上求加速度。";
                    if(transcribe)front=snapshot.RootElement.GetProperty("front").GetString()!;
                    var answer = JsonSerializer.Serialize(new { cards = new[] { new { front, back = transcribe ? "题目：质量为5 kg，在光滑水平面受20 N合力，求加速度。\n\n参考答案：$a=4$ m/s²。" : "不允许采用的模型改写", typeId = "fast-answer" } } });
                    response = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer }, finish_reason = "stop" } } }) + "\n\ndata: [DONE]\n\n";
                    request.Response.ContentType = "text/event-stream";
                }
                await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response)); request.Response.Close();
            } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; } });
        }
        try
        {
            for (var sample = 0; sample < (live ? 4 : 3); sample++)
            {
                var material = new CaptureMaterial("公开图片书面化验收", null, "generated_allowed", null, "", 0, DateTimeOffset.UtcNow);
                var chat = new ChatSession { Model = live ? settings.ImageModel : "vision-fixture" };
                // Deliberately register the pages in reverse order to detect ordering regressions.
                chat.Assets.AddRange([page2, physics, page1]);
                var card = new CardComposition { Front = sample == 2 ? "三角形两边8和9夹角60度面积？" : "",
                    Back = sample == 1 ? $"![图片](asset:{page1.Sha256})\n\n![图片](asset:{page2.Sha256})" : $"https://clipper-media.local/{physics.Sha256}.png" };
                chat.Compositions.Add(card);
                var draft = new ClipDraft { Original = material, Material = material, Settings = settings,
                    Configuration = ModeConfigurationLoader.Load(Path.Combine(repo, "modes")), Chat = chat };
                var idPath = Path.Combine(output, $"sample-{sample + 1}-draft-id.txt");
                if (live && File.Exists(idPath)) { draft = store.Find(File.ReadAllText(idPath))!; chat = draft.Chat!; card = chat.Compositions[0]; }
                else { store.Save(draft); File.WriteAllText(idPath, draft.Id); }
                var form = (Form)desktop.GetType("KnowledgeClipper.LanguageChatForm", true)!.GetConstructors(Flags).Single()
                    .Invoke([draft, store, data, AppContext.BaseDirectory, (Func<string>)(() => live ? ContentCredentials.Resolve() : "fixture"), (Action)(() => { })]);
                form.Show();
                try
                {
                    await Ready(form); await Wait(form, "document.querySelector('select[aria-label=\"辅助模型\"]')?.options.length>1&&!document.querySelector('.auxiliary-settings').getAttribute('aria-busy').includes('true')");
                    if (sample == 3 && chat.Proposals.Count == 0)
                    {
                        await SelectAuxiliary(form, "辅助服务", "codex");
                        await Wait(form, "Array.from(document.querySelector('select[aria-label=\"辅助模型\"]').options).some(o=>o.value==='gpt-6.1-sol')");
                        await SelectAuxiliary(form, "辅助模型", "gpt-6.1-sol"); await SelectAuxiliary(form, "辅助推理", "low");
                    }
                    var before = JsonSerializer.Serialize(new { chat.Binding, chat.Messages, chat.Prompts }, WorkflowJson.Options);
                    var back = card.Back;
                    if (chat.Proposals.Count == 0) await Button(form, "生成书面化问题");
                    var timer = System.Diagnostics.Stopwatch.StartNew(); var stage = "";
                    while (timer.Elapsed.TotalSeconds < 240)
                    {
                        var current = chat.Auxiliary!.Current;
                        if (stage != current.Binding.Stage) { stage = current.Binding.Stage; Console.WriteLine($"IMAGE {sample + 1} {stage}"); }
                        if (chat.Proposals.LastOrDefault()?.Status is "preview" or "adopted") break;
                        if (chat.Proposals.LastOrDefault()?.Status is "unparsed" or "incomplete") throw new Exception(current.Binding.Error + " " + chat.Proposals.Last().Error);
                        await Task.Delay(80);
                    }
                    var proposal = chat.Proposals.Last(); Check(proposal.Status == "adopted", $"截图样本 {sample + 1} 直接生成并替换正面");
                    var expected = sample == 1 ? new[] { page1, page2 } : new[] { physics };
                    Check(chat.Auxiliary!.Current.Prompts.Last().Images.Select(a => a.Sha256).SequenceEqual(expected.Select(a => a.Sha256)), "附件按卡片阅读顺序冻结且未丢图");
                    Check(card.Front == proposal.Cards[0].Front && card.Back == back && proposal.Cards[0].Back == back, "书面化直接替换正面，原背面逐字保留");
                    var front = proposal.Cards[0].Front;
                    if (sample is 0 or 3) Check(front.Contains('5') && front.Contains("20") && front.Contains("光滑") && front.Contains("加速度") && !front.Contains("4") && !front.Contains("参考答案") && !front.Contains("asset:"), "单张完整截图提取题干条件且排除答案");
                    else if (sample == 1) Check(front.Contains('5') && front.Contains("最小") && front.Contains("最大") && front.Contains("根") && front.Contains('0') && !front.Contains("-3") && !front.Contains('6') && !front.Contains("sqrt") && !front.Contains("√") && !front.Contains("asset:"), "多图续页保留定义域和两小问且排除解答结论");
                    else Check(front.Contains('8') && front.Contains('9') && front.Contains("60") && front.Contains("面积") && !front.Contains("20") && !front.Contains("加速度"), "已有正面保持原问题，不被背面其他题目替换");
                    if (!live)
                    {
                        var parts = requests.Last().GetProperty("messages").EnumerateArray().Last().GetProperty("content");
                        var attached = parts.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "image_url")
                            .Select(p => ImageAsset.Hash(Convert.FromBase64String(p.GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1])));
                        Check(attached.SequenceEqual(expected.Select(a => a.Sha256)), "真实 API 形状承载原 PNG 字节，数量顺序哈希一致");
                    }
                    await Shot(form, $"sample-{sample + 1}-candidate");
                    if (proposal.Status == "preview") await Button(form, "采用候选"); await Wait(form, "document.querySelectorAll('.proposal').length===0");
                    Check(card.Front == front && card.Back == back && before == JsonSerializer.Serialize(new { chat.Binding, chat.Messages, chat.Prompts }, WorkflowJson.Options), "采用仅更新正面，主会话与背面保持原样");
                    var restored = store.Find(draft.Id)!;
                    Check(restored.Chat!.Compositions[0].Front == front && restored.Chat.Compositions[0].Back == back, "持久化回读正面和原图片背面一致");
                    WorkflowJson.Save(Path.Combine(output, $"sample-{sample + 1}.json"), new { live, draftId = draft.Id, front, back, proposal.InputSnapshot,
                        images = expected.Select(a => new { a.Path, a.Sha256 }), noExport = true });
                    if(sample==0)
                    {
                        var transcription=chat.Proposals.LastOrDefault(p=>p.Action=="transcribe");
                        if(transcription==null)await Button(form,"图转文字","document.querySelectorAll('.face')[1]");
                        timer.Restart();
                        while(timer.Elapsed.TotalSeconds<240)
                        {
                            transcription=chat.Proposals.LastOrDefault(p=>p.Action=="transcribe");
                            if(transcription?.Status is "preview" or "adopted")break;
                            if(transcription?.Status is "unparsed" or "incomplete")throw new Exception(transcription.Error);
                            await Task.Delay(80);
                        }
                        Check(transcription?.Status == "adopted","真实图片图转文字直接采用转换结果");
                        var recognized=transcription!.Cards[0].Back;
                        Check(recognized.Contains('5')&&recognized.Contains("20")&&recognized.Contains('4')&&recognized.Contains("光滑")&&!recognized.Contains("asset:")&&!recognized.Contains("clipper-media.local"),"图转文字识别整张截图的题目及答案，与书面化只提取题目区分");
                        if(transcription.Status=="preview")await Button(form,"采用候选");await Wait(form,"document.querySelectorAll('.proposal').length===0");
                        Check(card.Front==front&&card.Back==recognized,"采用图转文字替换背面原图，保留生成的问题");
                        WorkflowJson.Save(Path.Combine(output,"transcription.json"),new{live,recognized,transcription.InputSnapshot,sourceHash=physics.Sha256});
                        await Button(form,"撤销上次编辑");await Task.Delay(180);
                        Check(card.Back==back&&card.Front==front&&chat.Assets.Any(a=>a.Sha256==physics.Sha256),"实际按钮撤销图转文字恢复原背面图片与原问题");
                        await Button(form,"撤销上次编辑");await Task.Delay(180);
                        Check(card.Front==""&&card.Back==back,"连续撤销恢复书面化前空正面，背面原图不变");
                    }
                    if(sample==3)
                    {
                        var pictured="原有前文：完整题目与解析。\n\n![图片](asset:"+physics.Sha256+")\n\n原有后文：逐项核对。";
                        await Edit(form,"卡片正面",pictured);await Task.Delay(150);
                        await Button(form,"图转文字","document.querySelectorAll('.face')[0]");
                        timer.Restart();
                        while(timer.Elapsed.TotalSeconds<240)
                        {
                            if(chat.Proposals.Last().Status=="adopted")break;
                            if(chat.Proposals.Last().Status is "unparsed" or "incomplete")throw new Exception(chat.Proposals.Last().Error);
                            await Task.Delay(80);
                        }
                        var converted=chat.Proposals.Last();
                        Check(converted.Status=="adopted"&&converted.Cards[0].Front.Contains("原有前文")&&converted.Cards[0].Front.Contains("原有后文")&&converted.Cards[0].Front.Contains('4')&&!converted.Cards[0].Front.Contains("asset:"),"Codex 视觉图转文字直接替换正面，保留前后文并识别完整题解");
                        await Wait(form,"document.querySelectorAll('.proposal').length===0");
                        Check(card.Back==back&&card.Front==converted.Cards[0].Front,"实际正面图转文字只替换正面，原背面保持不变");
                        WorkflowJson.Save(Path.Combine(output,"native-transcription.json"),new{front=card.Front,back,converted.InputSnapshot,chat.Auxiliary.Current.Binding});
                        await Button(form,"撤销上次编辑");await Task.Delay(150);
                        Check(card.Front==pictured&&card.Back==back,"正面图转文字撤销恢复原图及其前后文");
                        await Button(form,"撤销上次编辑");await Task.Delay(150);
                        Check(card.Front==front,"继续撤销图片编辑恢复书面化问题");
                    }
                }
                finally { form.Dispose(); }
            }
            Check(errors.Count == 0, "图片书面化实际页面无 JavaScript 异常");
        }
        finally { if (server.IsListening) server.Stop(); if (serving != null) await serving; }
    }
}
