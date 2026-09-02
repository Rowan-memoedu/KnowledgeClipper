using System;
using System.Collections.Generic;
using System.Text.Json;

namespace System.Web.Script.Serialization
{
    // Minimal source-compatible bridge for the frozen desktop/common DTO layer.
    // The .NET Framework build continues to use System.Web.Extensions; only the
    // net8 self-contained project includes this file.
    public sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = int.MaxValue;
        public int RecursionLimit { get; set; } = 100;

        public string Serialize(object value)
        {
            var json = JsonSerializer.Serialize(value, Options());
            if (json.Length > MaxJsonLength) throw new InvalidOperationException("JSON exceeds MaxJsonLength.");
            return json;
        }

        public T Deserialize<T>(string json)
        {
            RequireLength(json);
            return JsonSerializer.Deserialize<T>(json, Options());
        }

        public object DeserializeObject(string json)
        {
            RequireLength(json);
            using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = RecursionLimit }))
                return ConvertElement(document.RootElement);
        }

        public T ConvertToType<T>(object value)
        {
            return Deserialize<T>(Serialize(value));
        }

        private JsonSerializerOptions Options()
        {
            return new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true, MaxDepth = RecursionLimit };
        }

        private void RequireLength(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (json.Length > MaxJsonLength) throw new InvalidOperationException("JSON exceeds MaxJsonLength.");
        }

        private static object ConvertElement(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var item in value.EnumerateObject()) dictionary[item.Name] = ConvertElement(item.Value);
                    return dictionary;
                case JsonValueKind.Array:
                    var items = new List<object>(); foreach (var item in value.EnumerateArray()) items.Add(ConvertElement(item)); return items.ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number:
                    long integer; return value.TryGetInt64(out integer) ? (object)integer : value.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
