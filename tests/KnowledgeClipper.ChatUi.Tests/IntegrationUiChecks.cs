using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class IntegrationUiChecks
{
    internal static async Task Run(string[] args)
    {
        var repo = args[0]; var release = args[1]; var output = args[2]; Directory.CreateDirectory(output);
        var data = Path.Combine(output, "fixture", Guid.NewGuid().ToString("N")); var store = new DraftStore(data);
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
        using var stop = new CancellationTokenSource(); var requests = 0; var wrongCount = false;
        var bodies = new ConcurrentQueue<JsonElement>(); var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new ConcurrentBag<Task>();
        var serving = Task.Run(async () => {
            while (server.IsListening) try {
                var context = await server.GetContextAsync();
                workers.Add(Task.Run(async () => {
                    var response = context.Response;
                    try {
                        if (context.Request.Url!.AbsolutePath.EndsWith("/models"))
                            await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"fixture\",\"name\":\"Public integration fixture\",\"effort\":{\"supported_levels\":[\"high\"],\"default_level\":\"high\"}}]}"));
                        else {
                            var body = JsonDocument.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync()).RootElement.Clone(); bodies.Enqueue(body);
                            var ordinal = Interlocked.Increment(ref requests); response.SendChunked = true; response.ContentType = "text/event-stream";
                            if (ordinal == 1) {
                                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"未完成的公开回复\"}}]}\n\n"));
                                await response.OutputStream.FlushAsync(); firstEntered.SetResult(); await Task.Delay(Timeout.Infinite, stop.Token);
                            } else {
                                var content = body.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("text").GetString()!;
                                using var input = JsonDocument.Parse(content);
                                var count = input.RootElement.GetProperty("requestedCardCount");
                                var expected = count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 2;
                                var result = JsonSerializer.Serialize(new { cards = Enumerable.Range(1, wrongCount ? Math.Max(1, expected - 1) : expected)
                                    .Select(i => new { front = "公开问题 " + i + "：求 $x$。", back = "公开答案 " + i + "：$x=7$。", typeId = input.RootElement.GetProperty("typeId").GetString() }) });
                                await Task.Delay(250, stop.Token);
                                var stream = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = result }, finish_reason = "stop" } } }) + "\n\ndata: [DONE]\n\n";
                                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(stream));
                            }
                        }
                    } catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException) { }
                    finally { try { response.Close(); } catch (ObjectDisposedException) { } catch (HttpListenerException) { } }
                }));
            } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
        });
        var main = new ChatSession { Model = "fixture", Messages = [
            new() { Id = "rectangle-question", Role = "user", Parts = [new("text", "矩形长7、宽4，求面积。")] },
            new() { Id = "rectangle-answer", Role = "assistant", Parts = [new("text", "面积为 $S=28$ 平方厘米。")] }
        ], Auxiliary = new() };
        main.Auxiliary.Current.Model = "fixture";
        var fork = ChatWorkspace.Fork(main, "rectangle-answer");
        fork.Session.Messages.AddRange([
            new() { Id = "triangle-question", Role = "user", Parts = [new("text", "三角形底6、高3，求面积。")] },
            new() { Id = "triangle-answer", Role = "assistant", Parts = [new("text", "面积为 $S=9$ 平方厘米。")] }
        ]);
        ChatWorkspace.New(main);
        var material = new CaptureMaterial("公开整合重试界面验收", null, "generated_allowed", null, "public", 0, DateTimeOffset.UtcNow);
        var draft = new ClipDraft { Original = material, Material = material, Settings = new() { ApiBaseUrl = $"http://127.0.0.1:{port}", Model = "fixture" },
            Configuration = ModeConfigurationLoader.Load(Path.Combine(repo, "modes")), Chat = main };
        var checks = new List<string>(); Form form = null; WebView2 web = null;
        var formType = args.Contains("--shipped-form")
            ? Assembly.LoadFrom(Path.Combine(release, "KnowledgeClipper.dll")).GetType("KnowledgeClipper.LanguageChatForm", true)!
            : typeof(LanguageChatForm);
        void Check(bool pass, string label) { if (!pass) throw new Exception(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        async Task<string> Js(string script) => await web.CoreWebView2.ExecuteScriptAsync(script);
        async Task Wait(string script) {
            var watch = Stopwatch.StartNew();
            while (web.CoreWebView2 == null || await Js(script) != "true") {
                if (watch.Elapsed.TotalSeconds > 35) {
                    if (web.CoreWebView2 != null) { File.WriteAllText(Path.Combine(output, "failed-dom.json"), await Js("document.body.innerHTML")); using var shot = File.Create(Path.Combine(output, "failed-view.png")); await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot); }
                    throw new TimeoutException("Integration UI wait: " + script);
                }
                await Task.Delay(30);
            }
        }
        async Task Open(ClipDraft current) {
            form = (Form)formType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(c => c.GetParameters().Length == 6).Invoke([current, store, data, release, (Func<string>)(() => "fixture"), (Action)(() => { })]); form.Show();
            web = (WebView2)formType.GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            await Wait("document.querySelector('.auxiliary-settings select[aria-label=辅助模型]')?.value==='fixture'&&document.querySelector('.auxiliary-actions button')!==null");
        }
        Task Click(string selector) => Js("document.querySelector(" + JsonSerializer.Serialize(selector) + ").click()");
        Task Type(string label, string value) => Js("(()=>{const el=document.querySelector('textarea[aria-label=" + label + "]');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el," + JsonSerializer.Serialize(value) + ");el.dispatchEvent(new Event('input',{bubbles:true}));})()");
        try {
            await Open(draft);
            await Wait("document.querySelectorAll('.message').length===0&&!document.querySelector('.auxiliary-actions button').disabled");
            Check(true, "当前空白 Tab、空白卡面也能直接点击整合对话");
            await Type("补充要求", "请生成三张卡片"); await Click(".auxiliary-actions button"); await firstEntered.Task;
            await Wait("document.querySelector('.auxiliary-actions button').disabled&&document.querySelector('.auxiliary-actions button').getAttribute('aria-busy')==='true'");
            Check(requests == 1, "整合发出一次请求，按钮立即进入忙碌状态并防重复点击");
            await Wait("[...document.querySelectorAll('.composer-actions button')].some(b=>b.textContent==='停止'&&!b.disabled)");
            await Js("[...document.querySelectorAll('.composer-actions button')].find(b=>b.textContent==='停止').click()");
            await Wait("!document.querySelector('.auxiliary-actions button').disabled&&document.querySelector('.auxiliary-panel .notice')!==null");
            var stopped = main.Auxiliary.Current; var stoppedTurn = stopped.Binding.TurnId;
            Check(stopped.Binding.Stage == "unknown" && stopped.Messages.Last().Text.Contains("未完成") && main.Proposals.Single().Status == "incomplete",
                "真实 SSE 在途停止保留原输入与部分回复，并恢复整合按钮");
            Check(await Js("document.querySelector('textarea[aria-label=补充要求]').value==='请生成三张卡片'") == "true", "停止后补充要求仍可编辑且保留原输入");
            form.Close(); draft = store.Find(draft.Id)!; main = draft.Chat!; await Open(draft);
            await Wait("!document.querySelector('.auxiliary-actions button').disabled");
            Check(requests == 1 && main.Auxiliary!.Current.Binding.TurnId == stoppedTurn, "关闭重开不会重发未知请求，原稿可在同页手动重试");
            await Type("补充要求", "请生成三张卡片"); await Click(".auxiliary-actions button");
            await Wait("document.querySelectorAll('nav[aria-label=卡片] button').length===3&&!document.querySelector('.auxiliary-actions button').disabled");
            var original = main.Compositions[0];
            Check(main.Auxiliary.PreviousAttempts.Single().Binding.TurnId == stoppedTurn && main.Auxiliary.Current.Binding.Stage == "completed" && requests == 2,
                "明确重试成功生成三张卡，旧失败身份保留且没有额外请求");
            Check(await Js("document.querySelector('textarea[aria-label=卡片正面]').value.startsWith('公开问题 1')") == "true" &&
                await Js("document.querySelector('nav[aria-label=卡片] button').getAttribute('aria-pressed')==='true'") == "true",
                "多卡采用后保留原卡焦点，补充要求和编辑组件不被末卡替换");
            await Click("#conversation-main"); await Wait("document.querySelector('.message-body[data-message-id=rectangle-question]')!==null");
            await Js("document.querySelector('.message-body[data-message-id=rectangle-question]').closest('.message').querySelectorAll('.message-actions button')[0].click()");
            await Click("#conversation-" + fork.Id); await Wait("document.querySelector('.message-body[data-message-id=triangle-answer]')!==null");
            await Js("document.querySelector('.message-body[data-message-id=triangle-answer]').closest('.message').querySelectorAll('.message-actions button')[1].click()");
            await Wait("document.querySelector('textarea[aria-label=卡片正面]').value.includes('矩形')&&document.querySelector('textarea[aria-label=卡片背面]').value.includes('S=9')");
            await Js("(()=>{const select=document.querySelector('.card-editor>label select');select.value='explain-concept';select.dispatchEvent(new Event('change',{bubbles:true}));})()");
            await Wait("document.querySelector('.card-editor>label select').value==='explain-concept'");
            await Type("补充要求", "生成2张卡片");
            var priorCount = main.Compositions.Count;
            await Js("(()=>{const button=document.querySelector('.auxiliary-actions button');button.click();button.click();})()");
            await Wait("document.querySelectorAll('nav[aria-label=卡片] button').length===4&&!document.querySelector('.auxiliary-actions button').disabled");
            var last = main.Proposals.Last(); using var focus = JsonDocument.Parse(last.InputSnapshot);
            Check(priorCount == 3 && requests == 3 && focus.RootElement.GetProperty("contextScope").GetString() == "selected-and-draft" &&
                focus.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetString()).SequenceEqual(["rectangle-question", "triangle-answer"]),
                "跨 Tab 选取正反面后可正常整合，快速双击仍只有一次模型请求");
            Check(bodies.Last().GetProperty("messages").GetArrayLength() == 2, "辅助 API 只发送本次上下文，不夹带旧辅助结果");
            Check(main.Auxiliary.Current.Prompts.Last().TypeId == "explain-concept" && main.Compositions.Take(2).All(c => c.TypeId == "explain-concept"),
                "整合使用当前卡片的类型规则，兼容与左侧对话类型不同的设置");
            await Js("[...document.querySelectorAll('.card-tools button')].find(b=>b.textContent==='预览卡片').click()");
            await Wait("document.querySelector('.card-preview .katex')!==null");
            Check(true, "多卡共用现有预览，实际公式显示正确");
            var before = JsonSerializer.Serialize(main.Compositions, WorkflowJson.Options); wrongCount = true;
            await Type("补充要求", "生成3张卡片"); await Click(".auxiliary-actions button");
            await Wait("document.querySelector('.auxiliary-panel .error')?.textContent.includes('指定 3 张')&&!document.querySelector('.auxiliary-actions button').disabled");
            Check(before == JsonSerializer.Serialize(main.Compositions, WorkflowJson.Options) && main.Proposals.Last().Status == "unparsed",
                "数量错误在原页面显示具体原因，卡稿保持原样且按钮可再次点击");
            wrongCount = false; await Type("补充要求", "生成2张卡片"); await Click(".auxiliary-actions button");
            await Wait("document.querySelectorAll('nav[aria-label=卡片] button').length===5&&!document.querySelector('.auxiliary-actions button').disabled");
            Check(requests == 5 && main.Proposals.Last().Status == "adopted", "数量失败后修改补充要求可直接重试，不需新建草稿");
            await Type("补充要求", "生成0张卡片"); await Click(".auxiliary-actions button");
            await Wait("document.querySelector('.auxiliary-panel .error')?.textContent.includes('1 至 20')&&!document.querySelector('.auxiliary-actions button').disabled");
            Check(requests == 5, "数量校验失败发生在模型请求前，不会留下灰色按钮");
            foreach (var width in new[] { 1240, 768, 414, 375, 320 }) {
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", JsonSerializer.Serialize(new { width, height = 950, deviceScaleFactor = 1, mobile = false })); await Task.Delay(120);
                Check(await Js("document.documentElement.scrollWidth<=innerWidth+1") == "true", "整合、错误反馈和多卡导航无横向溢出：" + width);
                using var shot = File.Create(Path.Combine(output, "integration-" + width + ".png")); await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
            }
            WorkflowJson.Save(Path.Combine(output, "integration-ui-results.json"), new { passed = true, checks, requests, draft.Id,
                currentCardCount = main.Compositions.Count, archivedApiAttempts = main.Auxiliary.PreviousAttempts.Count, data,
                desktopAssembly = formType.Assembly.Location,
                contextSnapshots = main.Proposals.Select(p => new { p.Id, p.Status, p.InputSnapshot, p.Error }) });
        } finally {
            form?.Close(); stop.Cancel(); server.Stop(); await serving;
            try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
        }
    }
}
