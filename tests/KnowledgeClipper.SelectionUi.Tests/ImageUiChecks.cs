using System.Collections;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class ImageUiChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static object? Property(object target, string key) => target.GetType().GetProperty(key, Flags)!.GetValue(target);
    private static void Set(object target, string key, object? value) => target.GetType().GetProperty(key, Flags)!.SetValue(target, value);
    private static void Click(Control control) => control.GetType().GetMethod("OnClick", Flags)!.Invoke(control, [EventArgs.Empty]);
    private static void Render(Form form, string path)
    {
        form.Refresh(); using var bitmap = new Bitmap(form.Width, form.Height); using var graphics = Graphics.FromImage(bitmap); var dc = graphics.GetHdc();
        try { if (!PrintWindow(form.Handle, dc, 2)) throw new Exception("Window render failed"); } finally { graphics.ReleaseHdc(dc); }
        bitmap.Save(path);
    }
    internal static async Task RunAsync(string project, string receipt)
    {
        var checks = new List<string>();
        void Check(bool yes, string name) { if (!yes) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var assembly = Assembly.Load("KnowledgeClipper");
        Type Type(string name) => assembly.GetType("KnowledgeClipper." + name, true)!;
        object New(string name, params object?[] args) => Type(name).GetConstructors(Flags).Single(c => c.GetParameters().Length == args.Length).Invoke(args);
        var output = Path.GetDirectoryName(receipt)!; var fixture = Path.Combine(output, "fixtures");
        var data = Path.Combine(@"D:\Caches\KnowledgeClipper\image-ui-tests", Guid.NewGuid().ToString("N"));
        var images = new ImageMaterialStore(data);
        var job = images.Create([(Path.Combine(fixture, "question-1.png"), "question"), (Path.Combine(fixture, "question-2.png"), "question")],
            MaterialKind.OcrImages, new(), Path.Combine(project, "modes"), Path.Combine(data, "modes"));
        using var preparation = (Form)New("ImagePreparationForm", job, MaterialRouting.Choices(job.Kind));
        string? chosen = null;
        Action<string, bool> choice = (id, remember) => chosen = id;
        Type("ImagePreparationForm").GetEvent("RouteChosen", Flags)!.GetAddMethod(true)!.Invoke(preparation, [choice]);
        preparation.Show(); Call(preparation, "Progress", "OCR 正在后台处理中…"); await Task.Delay(40);
        var route = preparation.Controls.Find("knowledge-clipper.route.language", true).Single();
        Check(route.Enabled, "Ask route choices are available while image text preparation is pending");
        Click(route); Check(chosen == "language" && job.Ocr.All(o => o.RawText == null), "language can be selected before OCR has produced text");
        Render(preparation, Path.Combine(output, "image-ask.png"));
        var token = (CancellationToken)Property(preparation, "Token")!;
        preparation.Close(); Check(token.IsCancellationRequested, "closing the preparation form cancels pending work");
        var paired = images.Create([(Path.Combine(fixture, "question-1.png"), "question"), (Path.Combine(fixture, "question-2.png"), "question"),
            (Path.Combine(fixture, "answer-1.png"), "answer"), (Path.Combine(fixture, "answer-2.png"), "answer")], MaterialKind.OriginalImages, new(), Path.Combine(project, "modes"), Path.Combine(data, "modes"));
        var store = new ExcerptStore(data);
        var session = new ExcerptSession { Original = new("两张题图", null, "unavailable", null, "Public image UI fixture", 0, DateTimeOffset.UtcNow), Settings = new(), ImageMaterialId = paired.Id };
        var fields = new ExcerptFields("", "") { FrontImages = paired.Questions, BackImages = paired.Answers };
        var card = ExcerptCards.Add(session, store, fields, "题图与答案图");
        Func<ModeSnapshot[]> modes = () => [];
        Func<ModeSnapshot, CancellationToken, Task> generate = (_, _) => throw new Exception("Unexpected prompt request");
        string[]? imported = null;
        Func<string[], CancellationToken, Action, Task> import = (ids, _, _) => { imported = ids; return Task.CompletedTask; };
        using var preview = (Form)New("ExcerptDraftForm", session, store, modes, generate, import);
        preview.Show(); await Task.Delay(60); preview.PerformLayout();
        foreach (var image in paired.Images) Check(preview.Controls.Find("knowledge-clipper.image." + image.Role + "." + image.Index, true).Single() is PictureBox { Image: not null }, "image preview loads " + image.Role + image.Index);
        Render(preview, Path.Combine(output, "image-card-preview.png"));
        Click(preview.Controls.Find("knowledge-clipper.excerpt.import." + card.Id, true).Single());
        Check(imported?.SequenceEqual(new[] { card.Id }) == true, "image card uses the existing independent import action"); preview.Close();
        using var ask = (Form)New("MaterialRouteForm", (object)MaterialRouting.Choices(MaterialKind.TypedText)); ask.Show();
        Click(ask.Controls.Find("knowledge-clipper.route.excerpt", true).Single());
        Check((string?)Property(ask, "SelectedRoute") == "excerpt", "typed Ask resolves an explicit route without classification");

        var resultType = Type("Desktop.Core.ScreenshotResult"); var frames = Array.CreateInstance(resultType, 5);
        var sourceNames = new[] { "question-1.png", "question-2.png", "answer-1.png", "answer-2.png", "answer-2.png" };
        for (var i = 0; i < frames.Length; i++)
        {
            var path = Path.Combine(fixture, sourceNames[i]); var bytes = File.ReadAllBytes(path);
            var artifact = New("Desktop.Core.CaptureArtifact");
            Set(artifact, "artifactRef", "capture.image." + i); Set(artifact, "relativePath", "inputs/" + i + ".png");
            Set(artifact, "sha256", ImageAsset.Hash(bytes)); Set(artifact, "bytes", (long)bytes.Length); Set(artifact, "mediaType", "image/png");
            artifact.GetType().GetField("sourcePath", Flags)!.SetValue(artifact, path);
            var frame = New("Desktop.Core.ScreenshotResult"); Set(frame, "outcome", "captured"); Set(frame, "artifact", artifact); frames.SetValue(frame, i);
        }
        var oldPort = New("Desktop.Core.FakeDesktopOrchestratorPort");
        var controller = New("Desktop.Core.DesktopController", New("Desktop.Core.FakeCaptureProvider", new object?[] { null }), New("Desktop.Core.FakeScreenshotProvider", frames),
            oldPort, New("Desktop.Core.FakeClock", DateTime.UtcNow), New("Desktop.Core.FakeIdProvider"));
        object? bundle = null;
        Func<object, object> submit = value => { bundle = value; var result = New("Desktop.Core.SubmissionResult"); Set(result, "accepted", true); return result; };
        var target = controller.GetType().GetProperty("ScreenshotSubmission", Flags)!;
        var parameter = Expression.Parameter(target.PropertyType.GenericTypeArguments[0]);
        target.SetValue(controller, Expression.Lambda(target.PropertyType, Expression.Convert(Expression.Invoke(Expression.Constant(submit), Expression.Convert(parameter, typeof(object))), target.PropertyType.GenericTypeArguments[1]), parameter).Compile());
        var catalog = Type("Desktop.Core.AutomationCatalog");
        string Constant(string name) => (string)catalog.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object Execute(string action, object? arguments = null)
        {
            var envelope = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { schemaVersion = Constant("ActionSchemaVersion"), action = Constant(action), arguments }), Type("Desktop.Core.SemanticActionEnvelope"))!;
            return Call(controller, "Execute", envelope)!;
        }
        Execute("OriginalImageSet", new { enabled = true }); Execute("ScreenshotStart"); Execute("ScreenshotContinue");
        Execute("ScreenshotGroupAnswer"); Execute("ScreenshotContinue"); Execute("ScreenshotContinue"); Execute("ScreenshotContinue");
        var state = Execute("ScreenshotUndoLast");
        var screenshot = Property(state, "screenshotSession")!;
        Check((int)Property(screenshot, "questionImageCount")! == 2 && (int)Property(screenshot, "answerImageCount")! == 2, "multi-image capture keeps groups and undo removes only the latest frame");
        Execute("OriginalImageSet", new { enabled = false }); Execute("ScreenshotFinish");
        Check(bundle != null && ((IEnumerable)Property(bundle, "questionImages")!).Cast<string>().SequenceEqual(new[] { "capture.image.0", "capture.image.1" }) &&
            ((IEnumerable)Property(bundle, "answerImages")!).Cast<string>().SequenceEqual(new[] { "capture.image.2", "capture.image.3" }), "Finish submits both ordered groups to the new material consumer");
        Check((string?)Property(Property(bundle!, "modeSnapshot")!, "imageHandling") == "original" &&
            ((IList)oldPort.GetType().GetField("SubmittedBundles", Flags)!.GetValue(oldPort)!).Count == 0, "capture freezes original mode and bypasses the retired downstream workflow");

        var clipboardData = Path.Combine(data, "clipboard");
        WorkflowJson.Save(Path.Combine(clipboardData, "language-settings.json"), new LanguageSettings { GlobalSelection = false, LanguageAutoRead = false,
            OriginalImageMode = true, OriginalDefaultRoute = "image-excerpt" });
        var configType = Type("AppConfig"); var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(project, "appsettings.json")])!;
        Set(config, "WorkDirectory", clipboardData);
        var mainController = New("Desktop.Core.DesktopController", new object?[5]);
        using var main = (Form)Type("BoundClipperForm").GetConstructor(Flags, null, [configType, Type("Desktop.Core.DesktopController"), typeof(bool)], null)!.Invoke([config, mainController, false]);
        Call(main, "EnableLanguageWorkflow");
        object Field(string name) => main.GetType().GetField(name, Flags)!.GetValue(main)!;
        var flowField = main.GetType().GetField("imageFlow", Flags)!; ((IDisposable)flowField.GetValue(main)!).Dispose();
        var snapshotType = Type("ClipboardSnapshot"); var supplied = true;
        using var bitmap = Image.FromFile(Path.Combine(fixture, "question-1.png"));
        Func<object> clipboard = () =>
        {
            var snapshot = New("ClipboardSnapshot"); snapshotType.GetField("Text", Flags)!.SetValue(snapshot, "text offered alongside the image");
            if (supplied) snapshotType.GetField("Image", Flags)!.SetValue(snapshot, (Image)bitmap.Clone()); return snapshot;
        };
        var clipboardDelegateType = typeof(Func<>).MakeGenericType(snapshotType);
        var callback = Expression.Lambda(clipboardDelegateType, Expression.Convert(Expression.Invoke(Expression.Constant(clipboard)), snapshotType)).Compile();
        var imageFlow = New("ImageDesktopFlow", main, config, Field("languageFlow"), Field("excerptFlow"), callback); flowField.SetValue(main, imageFlow);
        Click((Button)Field("clipButton")); await Task.Delay(90);
        var capturedJobs = new ImageMaterialStore(clipboardData).Recent().ToArray(); var capturedCards = new ExcerptStore(clipboardData).Recent().ToArray();
        Check(capturedJobs.Length == 1 && capturedJobs[0].Images.Length == 1 && capturedCards.Length == 1 &&
            new ExcerptStore(clipboardData).Cards.Find(capturedCards[0].Cards[0].Id)!.Excerpt!.FrontImages.Length == 1,
            "the actual excerpt button prioritizes the newest image even when the clipboard also offers text");
        supplied = false;
        Check((bool)Call(imageFlow, "TryClipboard", "excerpt")! == false, "a text-only clipboard leaves the regular excerpt capture route available");
        ((IDisposable)imageFlow).Dispose(); ((IDisposable)Field("languageFlow")).Dispose(); ((IDisposable)Field("excerptFlow")).Dispose();
        ((IDisposable)Field("selectionMonitor")).Dispose(); ((Form)Field("selectionDot")).Dispose();
        WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot = data, scope = "real WinForms controls on private desktop; scripted capture provider; no user clipboard or screenshot gesture" });
    }
}
