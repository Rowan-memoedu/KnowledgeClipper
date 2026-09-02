using System.Text.Json;

namespace KnowledgeClipper.Core.V3.Kernel.Common;

internal static class StrictJson
{
    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON in {source}: {ex.Message}", ex);
        }

        try
        {
            RejectDuplicateProperties(document.RootElement, source, "$");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static JsonElement CloneElement(object value)
    {
        return JsonSerializer.SerializeToElement(value, KernelJson.Options);
    }

    private static void RejectDuplicateProperties(JsonElement element, string source, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"Duplicate JSON property '{property.Name}' at {path} in {source}.");
                }

                RejectDuplicateProperties(property.Value, source, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, source, $"{path}[{index}]");
                index++;
            }
        }
    }
}

internal static class KernelJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
