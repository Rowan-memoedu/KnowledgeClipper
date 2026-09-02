using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using KnowledgeClipper.Desktop.Application;

namespace KnowledgeClipper.Desktop.Core
{
    internal interface IOcrHumanResolutionArtifactReader
    {
        QuestionAnswerReadResult ReadText(string taskId, QuestionAnswerArtifactReference reference, string expectedKind);
        OcrHumanResolutionProvenanceReadResult ReadProvenance(string taskId, QuestionAnswerArtifactReference reference);
    }

    internal sealed class OcrHumanResolutionProvenanceReadResult
    {
        internal bool ready { get; set; }
        internal string markdownText { get; set; }
        internal string structuredText { get; set; }
        internal string noticeCode { get; set; }
    }

    internal sealed class FileOcrHumanResolutionArtifactReader : IOcrHumanResolutionArtifactReader
    {
        private readonly string jobsRoot;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = NamedPipeCommonIpcClient.MaximumMessageBytes };

        internal FileOcrHumanResolutionArtifactReader(string jobsRoot)
        {
            if (string.IsNullOrWhiteSpace(jobsRoot)) throw new ArgumentException("jobsRoot");
            this.jobsRoot = Path.GetFullPath(jobsRoot);
        }

        public QuestionAnswerReadResult ReadText(string taskId, QuestionAnswerArtifactReference reference, string expectedKind)
        {
            try
            {
                byte[] payloadBytes; IDictionary<string, object> manifest; string ignored;
                var error = ReadPayload(taskId, reference, expectedKind, "text/plain", out payloadBytes, out manifest, out ignored);
                if (error != null) return Failed(error);
                var text = Utf8(payloadBytes);
                if (text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
                return new QuestionAnswerReadResult { ready = true, text = text, noticeCode = "ocr-resolution.text-verified" };
            }
            catch (DecoderFallbackException) { return Failed("ocr-resolution.encoding-invalid"); }
            catch (InvalidOperationException) { return Failed("ocr-resolution.manifest-invalid"); }
            catch (IOException) { return Failed("ocr-resolution.read-failed"); }
            catch (UnauthorizedAccessException) { return Failed("ocr-resolution.read-denied"); }
        }

        public OcrHumanResolutionProvenanceReadResult ReadProvenance(string taskId, QuestionAnswerArtifactReference reference)
        {
            try
            {
                byte[] proofreadBytes; IDictionary<string, object> proofreadManifest; string ignoredPath;
                var error = ReadPayload(taskId, reference, "ocr-proofread-bundle", "application/json", out proofreadBytes, out proofreadManifest, out ignoredPath);
                if (error != null) return FailedProvenance(error);
                var proofread = serializer.DeserializeObject(Utf8(proofreadBytes)) as IDictionary<string, object>;
                if (proofread == null || Value(proofread, "schema_version") != "ocr-proofread-bundle/1.0.0" || Value(proofread, "release_state") != "unresolved")
                    return FailedProvenance("ocr-resolution.proofread-bundle-invalid");
                object rawItems;
                var unresolved = proofread.TryGetValue("items", out rawItems) && rawItems is IEnumerable
                    ? ((IEnumerable)rawItems).Cast<object>().Select(item => item as IDictionary<string, object>).Where(item => item != null && Value(item, "state") == "unresolved").ToArray()
                    : new IDictionary<string, object>[0];
                if (unresolved.Length != 1) return FailedProvenance("ocr-resolution.proofread-cardinality-invalid");
                object rawProvenance;
                var provenance = unresolved[0].TryGetValue("provenance", out rawProvenance) ? rawProvenance as IDictionary<string, object> : null;
                if (provenance == null || Value(provenance, "schema_version") != "ocr-provenance/1.0.0") return FailedProvenance("ocr-resolution.provenance-invalid");
                var markdown = NestedReference(provenance, "markdown_artifact", "image-ocr.markdown");
                var structured = NestedReference(provenance, "structured_artifact", "image-ocr.structured");
                if (markdown == null || structured == null || !ManifestContainsReference(proofreadManifest, markdown.reference) || !ManifestContainsReference(proofreadManifest, structured.reference))
                    return FailedProvenance("ocr-resolution.provenance-ancestry-invalid");
                byte[] markdownBytes; IDictionary<string, object> markdownManifest; string markdownPath;
                error = ReadPayload(taskId, markdown.reference, "image-ocr.markdown", "text/markdown", out markdownBytes, out markdownManifest, out markdownPath);
                if (error != null || markdown.payloadPath != markdownPath || !HashEquals(markdownBytes, markdown.payloadSha256)) return FailedProvenance(error ?? "ocr-resolution.markdown-integrity-mismatch");
                byte[] structuredBytes; IDictionary<string, object> structuredManifest; string structuredPath;
                error = ReadPayload(taskId, structured.reference, "image-ocr.structured", "application/json", out structuredBytes, out structuredManifest, out structuredPath);
                if (error != null || structured.payloadPath != structuredPath || !HashEquals(structuredBytes, structured.payloadSha256)) return FailedProvenance(error ?? "ocr-resolution.structured-integrity-mismatch");
                object rawSource;
                var source = unresolved[0].TryGetValue("source_ref", out rawSource) ? rawSource as IDictionary<string, object> : null;
                if (source == null || Value(source, "path") != markdown.payloadPath || Value(source, "sha256") != markdown.payloadSha256)
                    return FailedProvenance("ocr-resolution.source-ref-mismatch");
                return new OcrHumanResolutionProvenanceReadResult { ready = true, markdownText = Utf8(markdownBytes), structuredText = Utf8(structuredBytes), noticeCode = "ocr-resolution.provenance-verified" };
            }
            catch (DecoderFallbackException) { return FailedProvenance("ocr-resolution.encoding-invalid"); }
            catch (InvalidOperationException) { return FailedProvenance("ocr-resolution.provenance-invalid"); }
            catch (IOException) { return FailedProvenance("ocr-resolution.read-failed"); }
            catch (UnauthorizedAccessException) { return FailedProvenance("ocr-resolution.read-denied"); }
        }

        private string ReadPayload(string taskId, QuestionAnswerArtifactReference reference, string expectedKind, string expectedMediaTypePrefix, out byte[] payloadBytes, out IDictionary<string, object> manifest, out string payloadRelativePath)
        {
            payloadBytes = null; manifest = null; payloadRelativePath = null;
            if (!Identifier(taskId) || reference == null || !Identifier(reference.artifactRef) || reference.kind != expectedKind || reference.contractVersion != "1.0.0" || !Sha(reference.expectedSha256)) return "ocr-resolution.reference-invalid";
            try
            {
                string jobRoot;
                if (!Resolve(jobsRoot, taskId, out jobRoot) || !Directory.Exists(jobRoot)) return "ocr-resolution.path-invalid";
                var verified = new VerifiedArtifactReader(jobRoot).Read(
                    new DesktopArtifactReference(reference.artifactRef, reference.kind, reference.contractVersion, reference.manifestPath, reference.expectedSha256),
                    expectedKind, null);
                manifest = verified.Manifest;
                object rawFiles;
                var files = manifest != null && manifest.TryGetValue("files", out rawFiles) && rawFiles is IEnumerable ? ((IEnumerable)rawFiles).Cast<object>().Select(item => item as IDictionary<string, object>).Where(item => item != null).ToArray() : new IDictionary<string, object>[0];
                if (files.Length != 1 || !MediaMatches(Value(files[0], "mediaType"), expectedMediaTypePrefix)) return "ocr-resolution.payload-count-or-type-invalid";
                payloadRelativePath = verified.EntrypointRelativePath;
                payloadBytes = verified.EntrypointBytes;
                if (payloadBytes.LongLength > NamedPipeCommonIpcClient.MaximumMessageBytes) return "ocr-resolution.payload-metadata-invalid";
                return null;
            }
            catch (InvalidDataException ex) { return MapArtifactError(ex.Message); }
            catch (ArgumentException) { return "ocr-resolution.path-invalid"; }
        }

        private static string MapArtifactError(string code)
        {
            if (code == "desktop.artifact-manifest-path-invalid" || code == "desktop.artifact-file-path-invalid") return "ocr-resolution.path-invalid";
            if (code == "desktop.artifact-manifest-sha256-mismatch") return "ocr-resolution.manifest-hash-mismatch";
            if (code == "desktop.artifact-manifest-identity-mismatch") return "ocr-resolution.manifest-identity-mismatch";
            if (code == "desktop.artifact-file-bytes-mismatch" || code == "desktop.artifact-file-sha256-mismatch") return "ocr-resolution.payload-integrity-mismatch";
            if (code == "desktop.artifact-entrypoint-required" || code == "desktop.artifact-entrypoint-not-declared") return "ocr-resolution.payload-metadata-invalid";
            return "ocr-resolution.artifact-invalid:" + code;
        }

        private static bool MediaMatches(string actual, string expectedPrefix)
        {
            return !string.IsNullOrWhiteSpace(actual) && (actual == expectedPrefix || actual.StartsWith(expectedPrefix + ";", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ManifestContainsReference(IDictionary<string, object> manifest, QuestionAnswerArtifactReference reference)
        {
            object raw;
            if (manifest == null || !manifest.TryGetValue("inputRefs", out raw) || !(raw is IEnumerable)) return false;
            return ((IEnumerable)raw).Cast<object>().Select(item => item as IDictionary<string, object>).Any(item => item != null && Value(item, "artifactRef") == reference.artifactRef && Value(item, "kind") == reference.kind && Value(item, "contractVersion") == reference.contractVersion && Value(item, "manifestPath") == reference.manifestPath && Value(item, "expectedSha256") == reference.expectedSha256);
        }

        private static NestedArtifactReference NestedReference(IDictionary<string, object> provenance, string key, string expectedKind)
        {
            object raw; var value = provenance.TryGetValue(key, out raw) ? raw as IDictionary<string, object> : null;
            if (value == null || Value(value, "kind") != expectedKind || !Sha(Value(value, "payload_sha256"))) return null;
            var reference = new QuestionAnswerArtifactReference { artifactRef = Value(value, "artifactRef"), kind = expectedKind, contractVersion = Value(value, "contractVersion"), manifestPath = Value(value, "manifestPath"), expectedSha256 = Value(value, "expectedSha256") };
            return Identifier(reference.artifactRef) && reference.contractVersion == "1.0.0" && Sha(reference.expectedSha256) ? new NestedArtifactReference { reference = reference, payloadPath = Value(value, "payload_path"), payloadSha256 = Value(value, "payload_sha256") } : null;
        }

        private sealed class NestedArtifactReference
        {
            internal QuestionAnswerArtifactReference reference;
            internal string payloadPath;
            internal string payloadSha256;
        }

        private static bool Resolve(string root, string relative, out string full)
        {
            full = null;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
            var parts = relative.Replace('\\', '/').Split('/');
            if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == "..")) return false;
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(rootFull, string.Join(Path.DirectorySeparatorChar.ToString(), parts)));
            if (!candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            full = candidate; return true;
        }
        private static string Value(IDictionary<string, object> source, string key) { object value; return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null; }
        private static bool Int64(IDictionary<string, object> source, string key, out long value) { object raw; value = 0; if (!source.TryGetValue(key, out raw) || raw == null) return false; try { value = Convert.ToInt64(raw); return true; } catch { return false; } }
        private static bool Identifier(string value) { if (string.IsNullOrEmpty(value) || value.Length > 128 || !AlphaNumeric(value[0])) return false; for (var i = 1; i < value.Length; i++) if (!AlphaNumeric(value[i]) && value[i] != '.' && value[i] != '_' && value[i] != '-') return false; return true; }
        private static bool AlphaNumeric(char value) { return value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z' || value >= '0' && value <= '9'; }
        private static bool Sha(string value) { if (string.IsNullOrEmpty(value) || value.Length != 64) return false; for (var i = 0; i < value.Length; i++) if (!(value[i] >= '0' && value[i] <= '9' || value[i] >= 'a' && value[i] <= 'f')) return false; return true; }
        private static bool HashEquals(byte[] bytes, string expected) { using (var hash = SHA256.Create()) return string.Equals(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(), expected, StringComparison.Ordinal); }
        private static string Utf8(byte[] bytes) { return new UTF8Encoding(false, true).GetString(bytes); }
        private static QuestionAnswerReadResult Failed(string code) { return new QuestionAnswerReadResult { ready = false, noticeCode = code }; }
        private static OcrHumanResolutionProvenanceReadResult FailedProvenance(string code) { return new OcrHumanResolutionProvenanceReadResult { ready = false, noticeCode = code }; }
    }
}
