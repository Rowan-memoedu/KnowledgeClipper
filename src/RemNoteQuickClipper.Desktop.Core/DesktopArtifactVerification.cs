using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace KnowledgeClipper.Desktop.Application
{
    internal sealed class DesktopArtifactReference
    {
        internal DesktopArtifactReference(string artifactRef, string kind, string contractVersion, string manifestPath, string expectedSha256)
        {
            ArtifactRef = artifactRef; Kind = kind; ContractVersion = contractVersion; ManifestPath = manifestPath; ExpectedSha256 = expectedSha256;
        }
        internal string ArtifactRef { get; private set; }
        internal string Kind { get; private set; }
        internal string ContractVersion { get; private set; }
        internal string ManifestPath { get; private set; }
        internal string ExpectedSha256 { get; private set; }
    }

    internal sealed class VerifiedArtifactDocument
    {
        internal VerifiedArtifactDocument(IDictionary<string, object> manifest, IDictionary<string, object> entrypoint, byte[] entrypointBytes, string entrypointRelativePath, string entrypointFullPath, IReadOnlyList<string> verifiedFiles)
        {
            Manifest = manifest; Entrypoint = entrypoint; EntrypointBytes = entrypointBytes; EntrypointRelativePath = entrypointRelativePath; EntrypointFullPath = entrypointFullPath; VerifiedFiles = verifiedFiles;
        }
        internal IDictionary<string, object> Manifest { get; private set; }
        internal IDictionary<string, object> Entrypoint { get; private set; }
        internal byte[] EntrypointBytes { get; private set; }
        internal string EntrypointRelativePath { get; private set; }
        internal string EntrypointFullPath { get; private set; }
        internal IReadOnlyList<string> VerifiedFiles { get; private set; }
    }

    internal sealed class VerifiedArtifactReader
    {
        private readonly string root;
        private readonly string rootPrefix;
#if !NET8_0_OR_GREATER
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 1048576 };
#endif

        internal VerifiedArtifactReader(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("artifact root is required", "root");
            this.root = Path.GetFullPath(root);
            rootPrefix = this.root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        internal VerifiedArtifactDocument Read(DesktopArtifactReference reference, string expectedKind, string expectedSchemaVersion)
        {
            if (reference == null) throw new ArgumentNullException("reference");
            if (!string.Equals(reference.Kind, expectedKind, StringComparison.Ordinal)) throw new InvalidDataException("desktop.artifact-reference-kind-mismatch");
            var manifestPath = Contained(reference.ManifestPath, "desktop.artifact-manifest-path-invalid");
            var manifestBytes = File.ReadAllBytes(manifestPath);
            if (!EqualHash(Sha256(manifestBytes), reference.ExpectedSha256)) throw new InvalidDataException("desktop.artifact-manifest-sha256-mismatch");
            var manifest = ParseObject(manifestBytes);
            if (manifest == null || Value(manifest, "artifactId") != reference.ArtifactRef || Value(manifest, "kind") != expectedKind || Value(manifest, "contractVersion") != reference.ContractVersion)
                throw new InvalidDataException("desktop.artifact-manifest-identity-mismatch");
            object rawFiles;
            var files = manifest.TryGetValue("files", out rawFiles) && rawFiles is IEnumerable
                ? ((IEnumerable)rawFiles).Cast<object>().Select(item => item as IDictionary<string, object>).Where(item => item != null).ToArray()
                : new IDictionary<string, object>[0];
            if (files.Length == 0) throw new InvalidDataException("desktop.artifact-files-required");
            object rawMetadata;
            var metadata = manifest.TryGetValue("metadata", out rawMetadata) ? rawMetadata as IDictionary<string, object> : null;
            var entrypointRelative = Value(metadata, "entrypoint");
            if (string.IsNullOrWhiteSpace(entrypointRelative)) throw new InvalidDataException("desktop.artifact-entrypoint-required");

            var verified = new List<string>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            string entrypointPath = null;
            foreach (var file in files)
            {
                var relative = Value(file, "path"); var expectedHash = Value(file, "sha256"); long expectedBytes;
                if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(expectedHash) || !Int64(file, "bytes", out expectedBytes) || expectedBytes < 0)
                    throw new InvalidDataException("desktop.artifact-file-evidence-invalid");
                if (!paths.Add(relative)) throw new InvalidDataException("desktop.artifact-file-path-duplicate");
                var fullPath = Contained(relative, "desktop.artifact-file-path-invalid");
                var bytes = File.ReadAllBytes(fullPath);
                if (bytes.LongLength != expectedBytes) throw new InvalidDataException("desktop.artifact-file-bytes-mismatch");
                if (!EqualHash(Sha256(bytes), expectedHash)) throw new InvalidDataException("desktop.artifact-file-sha256-mismatch");
                verified.Add(relative);
                if (string.Equals(relative, entrypointRelative, StringComparison.Ordinal)) entrypointPath = fullPath;
            }
            if (entrypointPath == null) throw new InvalidDataException("desktop.artifact-entrypoint-not-declared");
            var entrypointBytes = File.ReadAllBytes(entrypointPath);
            IDictionary<string, object> entrypoint = null;
            if (!string.IsNullOrWhiteSpace(expectedSchemaVersion))
            {
                entrypoint = ParseObject(entrypointBytes);
                if (entrypoint == null || Value(entrypoint, "schemaVersion") != expectedSchemaVersion) throw new InvalidDataException("desktop.artifact-entrypoint-schema-version-mismatch");
            }
            return new VerifiedArtifactDocument(manifest, entrypoint, entrypointBytes, entrypointRelative, entrypointPath, verified.AsReadOnly());
        }

        internal VerifiedArtifactDocument ReadBoundOutput(string manifestPath, string expectedSha256, string expectedKind, string contractVersion, string expectedSchemaVersion)
        {
            var full = Contained(manifestPath, "desktop.artifact-manifest-path-invalid");
            var bytes = File.ReadAllBytes(full);
            if (!EqualHash(Sha256(bytes), expectedSha256)) throw new InvalidDataException("desktop.artifact-manifest-sha256-mismatch");
            var manifest = ParseObject(bytes);
            var artifactId = Value(manifest, "artifactId");
            if (string.IsNullOrWhiteSpace(artifactId)) throw new InvalidDataException("desktop.artifact-manifest-identity-mismatch");
            return Read(new DesktopArtifactReference(artifactId, expectedKind, contractVersion, manifestPath, expectedSha256), expectedKind, expectedSchemaVersion);
        }

        private string Contained(string relative, string code)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) throw new InvalidDataException(code);
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidDataException(code);
            return full;
        }

        private IDictionary<string, object> ParseObject(byte[] bytes)
        {
#if NET8_0_OR_GREATER
            using (var document = JsonDocument.Parse(bytes)) return ConvertElement(document.RootElement) as IDictionary<string, object>;
#else
            return serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as IDictionary<string, object>;
#endif
        }

#if NET8_0_OR_GREATER
        private static object ConvertElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject()) dictionary[property.Name] = ConvertElement(property.Value);
                    return dictionary;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(ConvertElement).ToArray();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    long integer; return element.TryGetInt64(out integer) ? (object)integer : element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif

        internal static string Value(IDictionary<string, object> source, string key)
        {
            object value; return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }
        private static bool Int64(IDictionary<string, object> source, string key, out long value) { object raw; value = 0; if (source == null || !source.TryGetValue(key, out raw) || raw == null) return false; try { value = Convert.ToInt64(raw); return true; } catch { return false; } }
        private static bool EqualHash(string left, string right) { return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        private static string Sha256(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
