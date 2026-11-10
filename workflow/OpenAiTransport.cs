using System.Text.Json;

namespace KnowledgeClipper.Workflow;

// Provider-specific transport only; content validation and draft ownership stay in the existing workflow.
public static class OpenAiTransport
{
    public static bool Selected(LanguageSettings settings) => new Uri(settings.ApiBaseUrl).Host == "api.openai.com";
    public static string Credential(LanguageSettings settings, Func<string> legacy)
    {
        if (!Selected(settings)) return legacy();
        var value = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(value))
        {
            var file = Environment.GetEnvironmentVariable("OPENAI_ENV_FILE") ?? @"D:\AppData\OpenAI\.env.local";
            value = File.ReadLines(file).FirstOrDefault(line => line.StartsWith("OPENAI_API_KEY="))?[15..].Trim().Trim('"', '\'');
        }
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException("未配置 OpenAI API 凭据。");
    }
    public static object Payload(string model, IEnumerable<object> messages, int? maxTokens, bool json, bool stream, string? effort = null)
    {
        var input = messages.Select(message =>
        {
            var m = JsonSerializer.SerializeToElement(message);
            var role = m.GetProperty("role").GetString()!;
            var content = m.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return (object)new { role, content = (object)content.GetString()! };
            return new { role, content = (object)content.EnumerateArray().Select(part =>
                part.GetProperty("type").GetString() == "image_url"
                    ? (object)new { type = "input_image", image_url = part.GetProperty("image_url").GetProperty("url").GetString(), detail = "high" }
                    : new { type = role == "assistant" ? "output_text" : "input_text", text = part.GetProperty("text").GetString() }).ToArray() };
        }).ToArray();
        var body = new Dictionary<string, object> { ["model"] = model, ["input"] = input, ["store"] = false,
            ["stream"] = stream, ["reasoning"] = new { effort = effort ?? "low" },
            ["text"] = json ? new { format = (object)new { type = "json_object" }, verbosity = "medium" } : new { verbosity = "medium" } };
        if (maxTokens != null) body["max_output_tokens"] = maxTokens.Value;
        return body;
    }
    public static string Text(JsonElement response)
    {
        if (response.GetProperty("status").GetString() != "completed") throw new InvalidDataException("OpenAI 响应未完整结束。");
        var text = string.Concat(response.GetProperty("output").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "message")
            .SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.GetProperty("type").GetString() == "output_text")
            .Select(x => x.GetProperty("text").GetString()));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("OpenAI 响应为空或被拒绝。");
        return text;
    }
}
