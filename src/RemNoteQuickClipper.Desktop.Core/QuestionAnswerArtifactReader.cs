using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KnowledgeClipper.Desktop.Application;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class QuestionAnswerArtifactReference
    {
        public string artifactRef { get; set; }
        public string kind { get; set; }
        public string contractVersion { get; set; }
        public string manifestPath { get; set; }
        public string expectedSha256 { get; set; }

        internal QuestionAnswerArtifactReference Copy()
        {
            return new QuestionAnswerArtifactReference
            {
                artifactRef = artifactRef, kind = kind, contractVersion = contractVersion,
                manifestPath = manifestPath, expectedSha256 = expectedSha256
            };
        }
    }

    internal sealed class QuestionAnswerReadResult
    {
        public bool ready { get; set; }
        public string text { get; set; }
        public string noticeCode { get; set; }
    }

    internal interface IQuestionAnswerArtifactReader
    {
        QuestionAnswerReadResult Read(string taskId, QuestionAnswerArtifactReference reference);
    }

    internal sealed class FileQuestionAnswerArtifactReader : IQuestionAnswerArtifactReader
    {
        private readonly string jobsRoot;

        internal FileQuestionAnswerArtifactReader(string jobsRoot)
        {
            if (string.IsNullOrWhiteSpace(jobsRoot)) throw new ArgumentException("jobsRoot is required", "jobsRoot");
            this.jobsRoot = Path.GetFullPath(jobsRoot);
        }

        public QuestionAnswerReadResult Read(string taskId, QuestionAnswerArtifactReference reference)
        {
            try
            {
                if (!IsIdentifier(taskId)) return Failed("question.answer.task-id-invalid");
                if (reference == null || !IsIdentifier(reference.artifactRef) || reference.kind != "question.answer.text" ||
                    string.IsNullOrWhiteSpace(reference.contractVersion) || !IsSha256(reference.expectedSha256))
                    return Failed("question.answer.reference-invalid");

                var jobRoot = ResolveTaskRoot(taskId);
                var reader = new VerifiedArtifactReader(jobRoot);
                var verified = reader.Read(ToDesktop(reference), "question.answer.text", null);
                object rawFiles;
                var files = verified.Manifest.TryGetValue("files", out rawFiles) && rawFiles is IEnumerable
                    ? ((IEnumerable)rawFiles).Cast<object>().Select(item => item as IDictionary<string, object>).Where(item => item != null).ToArray()
                    : new IDictionary<string, object>[0];
                if (VerifiedArtifactReader.Value(verified.Manifest, "mediaType") != "text/plain" || files.Length != 1 || VerifiedArtifactReader.Value(files[0], "mediaType") != "text/plain")
                    return Failed("question.answer.media-type-invalid");

                object rawInputRefs;
                var inputRefs = verified.Manifest.TryGetValue("inputRefs", out rawInputRefs) && rawInputRefs is IEnumerable
                    ? ((IEnumerable)rawInputRefs).Cast<object>().Select(item => item as IDictionary<string, object>).Where(item => item != null).ToArray()
                    : new IDictionary<string, object>[0];
                if (inputRefs.Length != 1)
                    return Failed("question.answer.text-ancestry-invalid");
                var ancestry = inputRefs[0];
                DesktopArtifactReference ancestryReference;
                if (!TryReference(ancestry, out ancestryReference) || ancestryReference.Kind != "question.answer")
                    return Failed("question.answer.text-ancestry-invalid");
                reader.Read(ancestryReference, "question.answer", null);

                var text = new UTF8Encoding(false, true).GetString(verified.EntrypointBytes);
                if (text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
                return new QuestionAnswerReadResult { ready = true, text = text, noticeCode = "question.answer.verified" };
            }
            catch (DecoderFallbackException) { return Failed("question.answer.encoding-invalid"); }
            catch (InvalidDataException ex) { return Failed(Map(ex.Message)); }
            catch (ArgumentException) { return Failed("question.answer.path-invalid"); }
            catch (IOException) { return Failed("question.answer.read-failed"); }
            catch (UnauthorizedAccessException) { return Failed("question.answer.read-denied"); }
        }

        private string ResolveTaskRoot(string taskId)
        {
            var prefix = jobsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var value = Path.GetFullPath(Path.Combine(jobsRoot, taskId));
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(value)) throw new InvalidDataException("question.answer.task-path-invalid");
            return value;
        }

        private static DesktopArtifactReference ToDesktop(QuestionAnswerArtifactReference value)
        {
            return new DesktopArtifactReference(value.artifactRef, value.kind, value.contractVersion, value.manifestPath, value.expectedSha256);
        }

        private static bool TryReference(IDictionary<string, object> value, out DesktopArtifactReference reference)
        {
            reference = null;
            var artifactRef = VerifiedArtifactReader.Value(value, "artifactRef"); var kind = VerifiedArtifactReader.Value(value, "kind");
            var contractVersion = VerifiedArtifactReader.Value(value, "contractVersion"); var manifestPath = VerifiedArtifactReader.Value(value, "manifestPath");
            var expectedSha256 = VerifiedArtifactReader.Value(value, "expectedSha256");
            if (!IsIdentifier(artifactRef) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(contractVersion) || string.IsNullOrWhiteSpace(manifestPath) || !IsSha256(expectedSha256)) return false;
            reference = new DesktopArtifactReference(artifactRef, kind, contractVersion, manifestPath, expectedSha256);
            return true;
        }

        private static string Map(string code)
        {
            if (code == "desktop.artifact-manifest-path-invalid") return "question.answer.manifest-path-invalid";
            if (code == "desktop.artifact-manifest-sha256-mismatch") return "question.answer.manifest-hash-mismatch";
            if (code == "desktop.artifact-file-path-invalid") return "question.answer.payload-path-invalid";
            if (code == "desktop.artifact-file-bytes-mismatch") return "question.answer.payload-bytes-mismatch";
            if (code == "desktop.artifact-file-sha256-mismatch") return "question.answer.payload-hash-mismatch";
            if (code == "desktop.artifact-manifest-identity-mismatch") return "question.answer.manifest-identity-mismatch";
            return code.StartsWith("question.answer.", StringComparison.Ordinal) ? code : "question.answer.artifact-invalid:" + code;
        }

        private static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128 || !AlphaNumeric(value[0])) return false;
            return value.Skip(1).All(character => AlphaNumeric(character) || character == '.' || character == '_' || character == '-');
        }

        private static bool AlphaNumeric(char value) { return value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z' || value >= '0' && value <= '9'; }
        private static bool IsSha256(string value) { return !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f' || character >= 'A' && character <= 'F'); }
        private static QuestionAnswerReadResult Failed(string code) { return new QuestionAnswerReadResult { ready = false, text = null, noticeCode = code }; }
    }
}
