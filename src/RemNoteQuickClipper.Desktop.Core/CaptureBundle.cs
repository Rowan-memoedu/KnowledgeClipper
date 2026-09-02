using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class CaptureBundleV1
    {
        public string schemaVersion { get; set; }
        public string captureId { get; set; }
        public string createdAt { get; set; }
        public string intent { get; set; }
        public string source { get; set; }
        public CaptureModeSnapshot modeSnapshot { get; set; }
        public List<CaptureArtifact> artifacts { get; set; }
        public string selectedTextArtifactRef { get; set; }
        public ContextSegments contextSegments { get; set; }
        public List<string> questionImages { get; set; }
        public List<string> answerImages { get; set; }
        public List<QuestionTurnReference> questionTurns { get; set; }
    }

    internal sealed class CaptureModeSnapshot
    {
        public string workflow { get; set; }
        public bool contextEnabled { get; set; }
        public string contextGranularity { get; set; }
        public bool contextApplied { get; set; }
        public string imageHandling { get; set; }
    }

    internal sealed class CaptureArtifact
    {
        public string artifactRef { get; set; }
        public string relativePath { get; set; }
        public string sha256 { get; set; }
        public long bytes { get; set; }
        public string mediaType { get; set; }
        public string encoding { get; set; }

        // Desktop-owned staging metadata. It is intentionally internal so it is
        // never serialized into capture-bundle.v1.
        internal string sourcePath;

        internal CaptureArtifact Copy()
        {
            return new CaptureArtifact { artifactRef = artifactRef, relativePath = relativePath, sha256 = sha256, bytes = bytes, mediaType = mediaType, encoding = encoding, sourcePath = sourcePath };
        }
    }

    internal sealed class ContextSegments
    {
        public string prefixArtifactRef { get; set; }
        public string selectionArtifactRef { get; set; }
        public string suffixArtifactRef { get; set; }
        public string captureMethod { get; set; }

        internal ContextSegments Copy()
        {
            return new ContextSegments { prefixArtifactRef = prefixArtifactRef, selectionArtifactRef = selectionArtifactRef, suffixArtifactRef = suffixArtifactRef, captureMethod = captureMethod };
        }
    }

    internal sealed class QuestionTurnReference
    {
        public int turnIndex { get; set; }
        public string role { get; set; }
        public string artifactRef { get; set; }
    }
}
