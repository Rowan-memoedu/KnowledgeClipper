using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace KnowledgeClipper
{
    internal static class LoopbackProviderOverrides
    {
        internal const string RemNoteMcpUrlEnvironmentVariable = "KNOWLEDGE_CLIPPER_REMNOTE_MCP_URL";
        internal const string AnkiEndpointEnvironmentVariable = "KNOWLEDGE_CLIPPER_ANKI_ENDPOINT";
        private static readonly Regex RemNotePattern = new Regex(@"^http://127\.0\.0\.1:([1-9][0-9]{0,4})/mcp$", RegexOptions.CultureInvariant);
        private static readonly Regex AnkiPattern = new Regex(@"^http://127\.0\.0\.1:([1-9][0-9]{0,4})/?$", RegexOptions.CultureInvariant);

        internal static string ResolveRemNoteMcpUrl(string fallback)
        {
            return Resolve(RemNoteMcpUrlEnvironmentVariable, fallback, RemNotePattern, "provider-override.remnote-loopback-required");
        }

        internal static string ResolveAnkiEndpoint(string fallback)
        {
            return Resolve(AnkiEndpointEnvironmentVariable, fallback, AnkiPattern, "provider-override.anki-loopback-required");
        }

        internal static void ApplyTo(ProcessStartInfo start)
        {
            if (start == null) throw new ArgumentNullException("start");
            var remnote = Environment.GetEnvironmentVariable(RemNoteMcpUrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(remnote)) start.EnvironmentVariables[RemNoteMcpUrlEnvironmentVariable] = ResolveRemNoteMcpUrl(null);
            var anki = Environment.GetEnvironmentVariable(AnkiEndpointEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(anki)) start.EnvironmentVariables[AnkiEndpointEnvironmentVariable] = ResolveAnkiEndpoint(null);
        }

        private static string Resolve(string name, string fallback, Regex pattern, string failure)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            value = value.Trim();
            var match = pattern.Match(value);
            int port;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out port) || port > 65535)
                throw new InvalidDataException(failure);
            return value;
        }
    }
}
