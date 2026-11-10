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

internal static class CodeBlockUiChecks
{
    internal static async Task Run(string[] args)
    {
        var repo = args[0]; var release = args[1]; var output = args[2]; Directory.CreateDirectory(output);
        var lines = new[] {
            new[] { "function greet(name) {", "  const literal = \"\\n\";", "", "\treturn '<hello> ' + name;", "}" },
            new[] { "first", "", "", "    indented", "last" },
            new[] { "plain first", "  plain second", "long: " + new string('x', 180) },
            new[] { "indented first", "  indented second" }
        };
        var fixture = "行内 `x + y` 保持同一行；公式 $x^2$。\n\n```javascript\r\n" + string.Join("\r\n", lines[0]) +
            "\r\n```\n\n~~~unknown-language\n" + string.Join("\n", lines[1]) + "\n~~~\n\n```\n" +
            string.Join("\n", lines[2]) + "\n```\n\n" + string.Join("\n", lines[3].Select(line => "    " + line));
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start(); var generationCalls = 0;
        var serving = Task.Run(async () => {
            while (listener.IsListening) try {
                var request = await listener.GetContextAsync();
                if (!request.Request.Url!.AbsolutePath.EndsWith("/models")) Interlocked.Increment(ref generationCalls);
                var bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"public-code-fixture\"}]}");
                request.Response.ContentType = "application/json"; await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
            } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
        });
        var material = new CaptureMaterial("公开代码换行验收", null, "generated_allowed", null, "", 0, DateTimeOffset.UtcNow);
        var draft = new ClipDraft { Original = material, Material = material,
            Settings = new() { ApiBaseUrl = $"http://127.0.0.1:{port}", Model = "public-code-fixture" },
            Configuration = ModeConfigurationLoader.Load(Path.Combine(repo, "modes")),
            Chat = new() { Messages = [new() { Id = "code-fixture", Role = "assistant", Parts = [new("text", fixture)] }],
                Compositions = [new() { Front = fixture, Back = fixture }] } };
        var data = Path.Combine(output, "data"); var store = new DraftStore(data); store.Save(draft);
        using var form = new LanguageChatForm(draft, store, data, release, () => "", () => { }); form.Show();
        var web = (WebView2)typeof(LanguageChatForm).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        async Task<string> Js(string source) => await web.CoreWebView2.ExecuteScriptAsync(source);
        async Task Wait(string condition) {
            var watch = Stopwatch.StartNew();
            while (web.CoreWebView2 == null || await Js(condition) != "true") {
                if (watch.Elapsed.TotalSeconds > 45) throw new Exception("Code UI timeout: " + condition);
                await Task.Delay(30);
            }
        }
        async Task Command(object value) { using var json = JsonDocument.Parse(JsonSerializer.Serialize(value)); await form.CommandAsync(json.RootElement); }
        var checks = new List<string>(); var measurements = new List<JsonElement>();
        void Check(bool passed, string name) {
            if (!passed) { WorkflowJson.Save(Path.Combine(output, "code-measurements.json"), measurements); throw new Exception(name); }
            checks.Add(name); Console.WriteLine("PASS " + name);
        }
        async Task Measure(string selector, string[][] expected, string label) {
            await Js("document.querySelector(" + JsonSerializer.Serialize(selector) + ").scrollIntoView({block:'start'})");
            // Force layout of content-visibility:auto blocks, including blocks outside the viewport.
            var raw = await Js("""
                ((selector) => {
                    const body = document.querySelector(selector);
                    const blocks = [...body.querySelectorAll('[data-streamdown="code-block-body"] pre code')].map(code => {
                        const pre = code.parentElement;
                        const rows = [...code.children].map(row => {
                            const rect = row.getBoundingClientRect();
                            // Static Streamdown retains the CR half of CRLF in its line token.
                            const text = row.textContent.replace(/\r$/, '');
                            return {text:text === '\n' ? '' : text, top:rect.top, bottom:rect.bottom, height:rect.height};
                        });
                        return {rows, lineHeight:parseFloat(getComputedStyle(pre).lineHeight), scrollWidth:pre.scrollWidth, clientWidth:pre.clientWidth};
                    });
                    const inline = body.querySelector('p code');
                    return {blocks, inline:inline?.textContent, inlineDisplay:inline && getComputedStyle(inline).display,
                        math:body.querySelectorAll('.katex').length, rootOverflow:document.documentElement.scrollWidth > innerWidth + 1};
                })(
                """ + JsonSerializer.Serialize(selector) + ")");
            var value = JsonDocument.Parse(raw).RootElement.Clone(); measurements.Add(value);
            var blocks = value.GetProperty("blocks").EnumerateArray().ToArray();
            Check(blocks.Length == expected.Length, label + "：代码块数量正确");
            for (var i = 0; i < blocks.Length; i++) {
                var rows = blocks[i].GetProperty("rows").EnumerateArray().ToArray();
                Check(rows.Select(row => row.GetProperty("text").GetString()).SequenceEqual(expected[i]), label + $"：代码块 {i + 1} 保留文字、缩进与空行");
                var lineHeight = blocks[i].GetProperty("lineHeight").GetDouble();
                Check(rows.All(row => Math.Abs(row.GetProperty("height").GetDouble() - lineHeight) <= 1) &&
                    rows.Zip(rows.Skip(1)).All(pair => Math.Abs(pair.Second.GetProperty("top").GetDouble() - pair.First.GetProperty("bottom").GetDouble()) <= 1),
                    label + $"：代码块 {i + 1} 每个源代码行占据独立、连续的一行");
            }
            Check(!value.GetProperty("rootOverflow").GetBoolean(), label + "：长代码不撑宽页面");
            if (expected.Length > 1) {
                Check(value.GetProperty("inline").GetString() == "x + y" && value.GetProperty("inlineDisplay").GetString() == "inline" && value.GetProperty("math").GetInt32() == 1,
                    label + "：行内代码与公式保持原显示");
                Check(blocks[2].GetProperty("scrollWidth").GetDouble() > blocks[2].GetProperty("clientWidth").GetDouble(), label + "：长代码在自身区域横向滚动");
            }
        }
        await Wait("document.querySelector('.preview-action')?.disabled===false");
        await Command(new { command = "preview", cardId = draft.Chat.Compositions[0].Id });
        await Wait("document.querySelectorAll('.card-preview [data-streamdown=\"code-block-body\"]').length===8");
        foreach (var width in new[] { 1240, 768, 414, 375, 320 }) {
            form.MinimumSize = Size.Empty; form.ClientSize = new Size((int)Math.Round(width * form.DeviceDpi / 96d), (int)Math.Round(1000 * form.DeviceDpi / 96d));
            await Task.Delay(120);
            await Measure(".message-body[data-message-id='code-fixture']", lines, $"聊天 {width}px");
            for (var face = 1; face <= 2; face++) await Measure($".card-preview .message-body:nth-of-type({face})", lines, $"预览 {face} / {width}px");
            using var shot = File.Create(Path.Combine(output, $"code-{width}.png")); await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
        }
        var streaming = new ChatMessage { Id = "streaming-code", Role = "assistant", Status = "incomplete", Parts = [new("text", "```python\nfirst = 1\n  second = 2")] };
        draft.Chat.Messages.Add(streaming);
        var busyField = typeof(ChatCoordinator).GetField("<Busy>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        busyField.SetValue(form.Coordinator, true);
        try {
            await Command(new { command = "older" });
            await Wait("document.querySelector('[data-message-id=\"streaming-code\"] pre code')?.children.length===2");
            await Measure(".message-body[data-message-id='streaming-code']", [["first = 1", "  second = 2"]], "未闭合围栏的流式代码");
            streaming.Parts = [new("text", "```python\nfirst = 1\n  second = 2\n\nthird = 3\n```")]; streaming.Status = "completed";
        } finally { busyField.SetValue(form.Coordinator, false); }
        await Command(new { command = "older" });
        await Wait("document.querySelector('[data-message-id=\"streaming-code\"] pre code')?.children.length===4");
        await Measure(".message-body[data-message-id='streaming-code']", [["first = 1", "  second = 2", "", "third = 3"]], "流式代码完成后");
        var copied = ""; form.WriteClipboardText = value => copied = value;
        await Command(new { command = "copyMessage", messageId = "code-fixture" });
        Check(copied == fixture && draft.Chat.Messages[0].Text == fixture && draft.Chat.Compositions[0].Front == fixture && draft.Chat.Compositions[0].Back == fixture,
            "显示和整条复制保留原始 Markdown、CRLF 与卡片正文");
        Check(draft.Chat.Prompts.Count == 0 && generationCalls == 0, "显示与预览不调用模型");
        WorkflowJson.Save(Path.Combine(output, "code-results.json"), new { passed = true, checks, measurements, modelCalls = generationCalls });
        form.Close(); listener.Stop(); await serving;
    }
}
