using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

public sealed partial class ContentModule
{
    // The new chat view streams plain Markdown. Existing structured AnswerAsync consumers retain their schema.
    public async Task StreamChatAsync(string instructions, IReadOnlyList<ChatMessage> history, string input,
        IReadOnlyList<ImageAsset> images, LanguageSettings settings, Func<string, Task> append, CancellationToken ct, string? effort = null)
    {
        object Parts(IEnumerable<ChatPart> parts) => parts.Select(p => p.Kind == "image" ? (object)new {
            type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(p.Image!.ReadVerified()) }
        } : new { type = "text", text = p.Text ?? "" }).ToArray();
        var messages = new List<object> { new { role = "system", content = (object)instructions } };
        foreach (var message in history.Where(m => m.Status == "completed"))
            messages.Add(new { role = message.Role, content = Parts(message.Parts) });
        messages.Add(new { role = "user", content = Parts((string.IsNullOrEmpty(input) ? [] : new[] { new ChatPart("text", input) })
            .Concat(images.Select(image => new ChatPart("image", Image: image)))) });
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key());
        var payload = new Dictionary<string, object> { ["model"] = settings.Model, ["stream"] = true, ["messages"] = messages };
        if (effort != null) payload["reasoning_effort"] = effort;
        request.Content = JsonContent.Create(payload);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ContentTimeoutSeconds));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("内容 API 身份验证失败，原输入已保留。");
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"内容 API 拒绝请求（HTTP {(int)response.StatusCode}），原输入已保留。");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        string? line; string? finish = null; var done = false;
        while ((line = await reader.ReadLineAsync(timeout.Token)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") { done = true; break; }
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.TryGetProperty("error", out _)) throw new IOException("API 在流式回复中报告失败；原文和部分回复已保留。");
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                await append(content.GetString()!);
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) finish = reason.GetString();
        }
        if (!done || finish != "stop") throw new IOException("API 回复未完整结束；部分回复保留为未完成，不会自动重发。");
    }
}
