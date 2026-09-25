using System.Security.Cryptography;
using KnowledgeClipper.Desktop.Core;

internal static class ScreenshotControllerChecks
{
    internal static void Run(string output, byte[] png, Action<bool, string> check)
    {
        CaptureArtifact Image(string name)
        {
            var path = Path.Combine(output, name + ".png"); File.WriteAllBytes(path, png);
            return new CaptureArtifact { artifactRef = "capture." + name, relativePath = "images/" + name + ".png", sourcePath = path,
                bytes = png.Length, sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), mediaType = "image/png" };
        }
        var first = Image("clipboard-first");
        var second = Image("question-second");
        var removed = Image("answer-undone");
        var answer = Image("answer-kept");
        var third = Image("question-third");
        var screenshots = new FakeScreenshotProvider(new[] { second, removed, answer, third }.Select(a => new ScreenshotResult { outcome = "captured", artifact = a }));
        var capture = new FakeCaptureProvider(null);
        var orchestrator = new FakeDesktopOrchestratorPort();
        var controller = new DesktopController(capture, screenshots, orchestrator, new FakeClock(DateTime.UtcNow), new FakeIdProvider());
        DesktopSemanticState Execute(string action, bool? enabled = null) => controller.Execute(new SemanticActionEnvelope
        { schemaVersion = AutomationCatalog.ActionSchemaVersion, action = action, arguments = new SemanticActionArguments { enabled = enabled } });
        Execute(AutomationCatalog.OriginalImageSet, true);
        var state = controller.StartScreenshotFromImage(first);
        check(state.foregroundState == "screenshot-review" && state.screenshotSession.active && state.screenshotSession.questionImageCount == 1 && state.screenshotSession.answerImageCount == 0,
            "clipboard first image starts ordinary screenshot review as question image");
        check(state.screenshotSession.canContinue && state.screenshotSession.canFinish && state.screenshotSession.canUndo && screenshots.RequestedGroups.Count == 0 && capture.CallCount == 0,
            "clipboard first image needs no screenshot provider and supports normal next actions");
        var rejected = controller.StartScreenshotFromImage(Image("unrelated-copy"));
        check(rejected.noticeCode == "desktop.foreground-busy" && rejected.screenshotSession.questionImageCount == 1,
            "another clipboard image cannot overwrite an active screenshot session");
        // The collector owns a copy of all metadata, not a caller-mutable DTO.
        first.sourcePath = "caller-mutated";
        state = Execute(AutomationCatalog.ScreenshotContinue);
        check(state.screenshotSession.questionImageCount == 2 && screenshots.RequestedGroups.SequenceEqual(new[] { "question" }), "continuing captures second question after frozen clipboard first");
        Execute(AutomationCatalog.ScreenshotGroupAnswer); state = Execute(AutomationCatalog.ScreenshotContinue);
        check(state.screenshotSession.answerImageCount == 1 && screenshots.RequestedGroups[^1] == "answer", "answer grouping applies to following captures");
        state = Execute(AutomationCatalog.ScreenshotUndoLast);
        check(state.screenshotSession.questionImageCount == 2 && state.screenshotSession.answerImageCount == 0 && state.screenshotSession.canFinish, "undo removes last answer and retains both question images");
        Execute(AutomationCatalog.ScreenshotContinue);
        Execute(AutomationCatalog.ScreenshotGroupQuestion); Execute(AutomationCatalog.ScreenshotContinue);
        // Settings changes during collection must not reinterpret its original input.
        Execute(AutomationCatalog.OriginalImageSet, false);
        CaptureBundleV1 submitted = null;
        controller.ScreenshotSubmission = bundle => { submitted = bundle; return new SubmissionResult { accepted = true }; };
        state = Execute(AutomationCatalog.ScreenshotFinish);
        check(submitted != null && state.foregroundState == "idle" && !state.screenshotSession.active && state.noticeCode == "screenshot.submitted",
            "finish submits the reused image workflow and closes screenshot session");
        check(submitted.questionImages.SequenceEqual(new[] { "capture.clipboard-first", second.artifactRef, third.artifactRef }) && submitted.answerImages.SequenceEqual(new[] { answer.artifactRef }),
            "finish preserves first image and per-group order without the undone image");
        check(submitted.artifacts.Select(a => a.artifactRef).SequenceEqual(new[] { "capture.clipboard-first", second.artifactRef, answer.artifactRef, third.artifactRef }) && submitted.artifacts[0].sourcePath == Path.Combine(output, "clipboard-first.png"),
            "submitted artifact chronology and first image staging identity remain intact");
        check(submitted.modeSnapshot.imageHandling == "original" && !submitted.modeSnapshot.contextEnabled && !submitted.modeSnapshot.contextApplied && orchestrator.SubmittedBundles.Count == 0,
            "first image freezes capture mode and uses current image submission port without old orchestrator fallback");

        state = controller.StartScreenshotFromImage(Image("only-first"));
        state = Execute(AutomationCatalog.ScreenshotUndoLast);
        check(state.screenshotSession.active && !state.screenshotSession.canFinish && !state.screenshotSession.canUndo && state.screenshotSession.canContinue,
            "undoing the sole clipboard image leaves an empty collector for continuing");
        state = Execute(AutomationCatalog.ScreenshotFinish);
        check(state.noticeCode == "screenshot.no-active-session" && state.screenshotSession.active, "empty collector cannot finish into an invalid image bundle");
        state = Execute(AutomationCatalog.ScreenshotCancel);
        check(!state.screenshotSession.active && state.foregroundState == "idle", "cancel resets clipboard-initiated screenshot collection");
        var invalidRejected = false;
        try { controller.StartScreenshotFromImage(new CaptureArtifact { mediaType = "text/plain", sourcePath = "fixture.txt" }); }
        catch (ArgumentException) { invalidRejected = true; }
        check(invalidRejected && !controller.Snapshot().screenshotSession.active, "non-image first material is rejected before starting collection");
    }
}
