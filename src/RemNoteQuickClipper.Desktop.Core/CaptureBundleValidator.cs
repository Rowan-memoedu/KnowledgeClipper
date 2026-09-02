using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Desktop.Core
{
    internal static class CaptureBundleValidator
    {
        private static readonly Regex Hash = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex CaptureArtifactReference = new Regex("^capture\\.[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);

        internal static void Validate(CaptureBundleV1 bundle)
        {
            if (bundle == null) throw new InvalidOperationException("capture-bundle.null");
            if (bundle.schemaVersion != AutomationCatalog.CaptureBundleSchemaVersion) throw new InvalidOperationException("capture-bundle.unsupported-version");
            if (string.IsNullOrWhiteSpace(bundle.captureId)) throw new InvalidOperationException("capture-bundle.capture-id-required");
            if (bundle.artifacts == null || bundle.questionImages == null || bundle.answerImages == null || bundle.questionTurns == null) throw new InvalidOperationException("capture-bundle.required-array-null");

            var artifacts = new Dictionary<string, CaptureArtifact>(StringComparer.Ordinal);
            foreach (var artifact in bundle.artifacts)
            {
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.artifactRef)) throw new InvalidOperationException("capture-bundle.artifact-ref-required");
                if (artifact.artifactRef.Length > 128 || !CaptureArtifactReference.IsMatch(artifact.artifactRef)) throw new InvalidOperationException("capture-bundle.artifact-ref-common-incompatible");
                if (artifacts.ContainsKey(artifact.artifactRef)) throw new InvalidOperationException("capture-bundle.artifact-ref-duplicate");
                if (string.IsNullOrWhiteSpace(artifact.relativePath) || System.IO.Path.IsPathRooted(artifact.relativePath) || HasTraversal(artifact.relativePath)) throw new InvalidOperationException("capture-bundle.artifact-path-unsafe");
                if (string.IsNullOrWhiteSpace(artifact.sha256) || !Hash.IsMatch(artifact.sha256)) throw new InvalidOperationException("capture-bundle.artifact-hash-invalid");
                if (artifact.bytes < 0 || string.IsNullOrWhiteSpace(artifact.mediaType)) throw new InvalidOperationException("capture-bundle.artifact-metadata-invalid");
                artifacts.Add(artifact.artifactRef, artifact);
            }

            var question = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in bundle.questionImages)
            {
                if (!question.Add(item)) throw new InvalidOperationException("capture-bundle.question-image-duplicate");
                RequireImageArtifact(artifacts, item);
            }
            var answer = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in bundle.answerImages)
            {
                if (!answer.Add(item)) throw new InvalidOperationException("capture-bundle.answer-image-duplicate");
                if (question.Contains(item)) throw new InvalidOperationException("capture-bundle.image-groups-overlap");
                RequireImageArtifact(artifacts, item);
            }
            if (bundle.intent == "screenshot" && bundle.questionImages.Count == 0) throw new InvalidOperationException("capture-bundle.question-images-required");

            if (!string.IsNullOrWhiteSpace(bundle.selectedTextArtifactRef) && !artifacts.ContainsKey(bundle.selectedTextArtifactRef)) throw new InvalidOperationException("capture-bundle.selected-text-ref-unresolved");
            if (bundle.modeSnapshot == null) throw new InvalidOperationException("capture-bundle.mode-snapshot-required");
            if (bundle.modeSnapshot.contextApplied && !bundle.modeSnapshot.contextEnabled) throw new InvalidOperationException("capture-bundle.context-applied-without-mode");
            if (bundle.modeSnapshot.contextApplied && bundle.contextSegments == null) throw new InvalidOperationException("capture-bundle.context-segments-required");
            if (bundle.contextSegments != null)
            {
                RequireOptionalArtifact(artifacts, bundle.contextSegments.prefixArtifactRef);
                RequireOptionalArtifact(artifacts, bundle.contextSegments.selectionArtifactRef);
                RequireOptionalArtifact(artifacts, bundle.contextSegments.suffixArtifactRef);
            }
            foreach (var turn in bundle.questionTurns)
            {
                if (turn == null || turn.turnIndex < 1 || (turn.role != "user" && turn.role != "assistant")) throw new InvalidOperationException("capture-bundle.question-turn-invalid");
                RequireOptionalArtifact(artifacts, turn.artifactRef);
            }
            if (bundle.intent == "question" && bundle.questionTurns.Count == 0) throw new InvalidOperationException("capture-bundle.question-turn-required");
        }

        private static void RequireImageArtifact(IDictionary<string, CaptureArtifact> artifacts, string artifactRef)
        {
            CaptureArtifact artifact;
            if (string.IsNullOrWhiteSpace(artifactRef) || !artifacts.TryGetValue(artifactRef, out artifact)) throw new InvalidOperationException("capture-bundle.image-ref-unresolved");
            if (!artifact.mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("capture-bundle.image-ref-not-image");
        }

        private static void RequireOptionalArtifact(IDictionary<string, CaptureArtifact> artifacts, string artifactRef)
        {
            if (!string.IsNullOrWhiteSpace(artifactRef) && !artifacts.ContainsKey(artifactRef)) throw new InvalidOperationException("capture-bundle.artifact-ref-unresolved");
        }

        private static bool HasTraversal(string path)
        {
            return path.Replace('\\', '/').Split('/').Any(part => part == "..");
        }
    }
}
