using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Core;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class ImageDesktopFlow : IDisposable
{
    private readonly Form owner;
    private readonly AppConfig config;
    private readonly LanguageDesktopFlow language;
    private readonly ExcerptDesktopFlow excerpt;
    private readonly ImageMaterialStore store;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Dictionary<string, ImagePreparationForm> forms = new();
    private readonly Dictionary<string, ImageMaterialJob> activeJobs = new();
    private readonly Dictionary<string, Task<bool>> processingTasks = new();
    private readonly ImagePreparation preparation;
    private readonly Func<ClipboardSnapshot> clipboard;
    internal ImageDesktopFlow(Form owner, AppConfig config, LanguageDesktopFlow language, ExcerptDesktopFlow excerpt, Func<ClipboardSnapshot> clipboard = null)
    {
        this.owner = owner; this.config = config; this.language = language; this.excerpt = excerpt;
        this.clipboard = clipboard ?? ClipboardSnapshot.ReadWithRetry;
        store = new(config.WorkDirectory); preparation = new(store, new MineruCli(), new ContentModule(http, () => ContentCredentials.Resolve()));
    }
    private LanguageSettings Snapshot()
    {
        var current = language.Settings;
        return current with { MineruScriptPath = string.IsNullOrWhiteSpace(current.MineruScriptPath) ? config.MineruScriptPath : current.MineruScriptPath };
    }
    private string Defaults => string.IsNullOrWhiteSpace(language.Settings.ExcerptModesDirectory) ? language.ModeDefaults : language.Settings.ExcerptModesDirectory;
    internal SubmissionResult Submit(CaptureBundleV1 bundle)
    {
        string Source(string id) => bundle.artifacts.Single(a => a.artifactRef == id).sourcePath;
        var inputs = bundle.questionImages.Select(id => (Source(id), "question")).Concat(bundle.answerImages.Select(id => (Source(id), "answer"))).ToArray();
        var job = store.Create(inputs, bundle.modeSnapshot.imageHandling == "original" ? MaterialKind.OriginalImages : MaterialKind.OcrImages,
            Snapshot(), Defaults, language.ModeOverrides);
        Open(job); return new SubmissionResult { accepted = true, noticeCode = "screenshot.submitted" };
    }
    internal bool TryClipboard(string intent = "excerpt")
    {
        using var snapshot = clipboard();
        if (snapshot.Image == null) return false;
        var staging = Path.Combine(config.WorkDirectory, "capture-staging"); Directory.CreateDirectory(staging);
        var path = Path.Combine(staging, "clipboard." + Guid.NewGuid().ToString("N") + ".png");
        snapshot.Image.Save(path, ImageFormat.Png);
        var settings = Snapshot();
        var job = store.Create([(path, "question")], settings.OriginalImageMode == true ? MaterialKind.OriginalImages : MaterialKind.OcrImages,
            settings, Defaults, language.ModeOverrides);
        ImageEntryRouting.SetSingle(job, intent, false); store.Save(job);
        Open(job); return true;
    }
    internal CaptureArtifact StageClipboardImage(byte[] png)
    {
        if (png == null || png.Length is < 24 or > 33554432) throw new InvalidDataException("图片为空或超过 32 MiB。");
        using (var stream = new MemoryStream(png))
        using (var image = Image.FromStream(stream, false, true))
            if (image.Width < 1 || image.Height < 1) throw new InvalidDataException("图片尺寸无效。");
        var staging = Path.Combine(config.WorkDirectory, "capture-staging"); Directory.CreateDirectory(staging);
        var id = "capture.clipboard." + Guid.NewGuid().ToString("N");
        var path = Path.Combine(staging, id + ".png");
        File.WriteAllBytes(path, png);
        return WindowsDesktopCaptureProvider.Artifact(id, "inputs/capture/" + id + ".png", png, "image/png", null, path);
    }
    internal void StartClipboardImage(byte[] png, string intent = "excerpt", bool quick = false)
    {
        var artifact = StageClipboardImage(png);
        var snapshot = Snapshot();
        var job = store.Create([(artifact.sourcePath, "question")], quick && intent == "excerpt" || snapshot.OriginalImageMode == true ? MaterialKind.OriginalImages : MaterialKind.OcrImages,
            snapshot, Defaults, language.ModeOverrides);
        ImageEntryRouting.SetSingle(job, intent, quick); store.Save(job);
        if (quick) _ = RunQuickAsync(job); else Open(job);
    }
    private readonly CancellationTokenSource lifetime = new();
    private async Task RunQuickAsync(ImageMaterialJob job)
    {
        try { await ProcessAsync(job, _ => { }, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            job.Error = ex.Message; store.Save(job);
            if (!owner.IsDisposed)
            {
                Open(job, autoStart: false);
                language.ReportQuickFeedback("图片处理失败：" + ex.Message);
            }
        }
    }
    private async Task<bool> ProcessAsync(ImageMaterialJob job, Action<string> progress, CancellationToken cancellation)
    {
        if (processingTasks.TryGetValue(job.Id, out var active)) return await active;
        if (job.DestinationId != null) return true;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        processingTasks.Add(job.Id, completion.Task); activeJobs[job.Id] = job;
        try
        {
            var result = await ProcessCoreAsync(job, progress, cancellation);
            completion.TrySetResult(result); return result;
        }
        catch (Exception ex) { completion.TrySetException(ex); _ = completion.Task.Exception; throw; }
        finally { processingTasks.Remove(job.Id); activeJobs.Remove(job.Id); }
    }
    private async Task<bool> ProcessCoreAsync(ImageMaterialJob job, Action<string> progress, CancellationToken cancellation)
    {
        string text = null;
        if (ImageEntryRouting.NeedsOcr(job)) text = await preparation.TextAsync(job, progress, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (job.Route == null) return false;
        if (job.Route is "language" or "question")
        {
            text ??= "请解释这张图片的主要内容；若是题目，请给出解答。";
            var material = ImageEntryRouting.Material(job, text);
            job.DestinationId = language.StartPreparedMaterial(material, job.Settings, job.Route, job.Quick);
            job.DestinationKind = "language";
        }
        else if (job.Kind == MaterialKind.OcrImages)
        {
            if (job.Route != "excerpt") throw new InvalidOperationException("请重新选择文字处理路径。");
            job.DestinationId = excerpt.StartMaterial(ImageEntryRouting.Material(job, text), job.Settings, job.Id, job.Quick);
            job.DestinationKind = "excerpt";
        }
        else if (job.Entry == "excerpt")
        {
            // Single-image excerpts share the daily excerpt path with text;
            // image-original remains the explicit multi-image mode.
            var material = new CaptureMaterial("图片摘录", null, "unavailable", null,
                "剪贴板图片", 0, job.CreatedAt) { FrontImage = job.Questions.Single() };
            job.DestinationId = excerpt.StartMaterial(material, job.Settings, job.Id, job.Quick);
            job.DestinationKind = "excerpt";
        }
        else
        {
            var result = await preparation.OriginalAsync(job, job.Route == "excerpt" ? "image-excerpt" : job.Route, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            job.DestinationId = excerpt.StartImages(job, result.Fields, result.Mode, job.Quick);
            job.DestinationKind = "excerpt";
        }
        store.Save(job); return true;
    }
    private void Open(ImageMaterialJob job, bool autoStart = true)
    {
        if (activeJobs.TryGetValue(job.Id, out var active)) job = active;
        if (job.DestinationId != null)
        {
            if (job.DestinationKind == "excerpt") excerpt.Resume(job.DestinationId); else language.Resume(job.DestinationId);
            return;
        }
        if (forms.TryGetValue(job.Id, out var existing) && !existing.IsDisposed) { existing.Activate(); return; }
        var choices = job.Entry == "multi" ? MaterialRouting.Choices(job.Kind, job.Modes) :
            new[] { new MaterialRoute(job.Entry, job.Entry switch { "language" => "语言学习", "question" => "问答", _ => "摘录" }) };
        if (!choices.Any(c => c.Id == job.Route)) job.Route = null;
        var form = new ImagePreparationForm(job, choices); forms[job.Id] = form;
        bool running = false;
        async Task Run()
        {
            if (running || form.IsDisposed) return; running = true; form.RetryEnabled = false; job.Error = null;
            var cancellation = form.Token;
            try
            {
                if (await ProcessAsync(job, form.Progress, cancellation)) { form.Close(); return; }
                if (ImageEntryRouting.NeedsOcr(job)) form.ShowText(job.Text);
                form.Progress("材料已就绪，请选择后续路径。");
            }
            catch (OperationCanceledException)
            {
                job.Error = "处理已暂停；原图和已完成步骤已保存。"; store.Save(job);
                // A reopened preview can be awaiting work canceled by the old window.
                // Its own token is still live, so make that saved job resumable here.
                if (!form.IsDisposed && !cancellation.IsCancellationRequested)
                { form.Progress(job.Error); form.EnableChoices(true); form.RetryEnabled = true; }
            }
            catch (Exception ex) { job.Error = ex.Message; store.Save(job); if (!form.IsDisposed) { form.Progress(ex.Message); form.EnableChoices(true); form.RetryEnabled = true; } }
            finally { running = false; }
        }
        form.RouteChosen += (route, remember) =>
        {
            job.Route = route; store.Save(job); form.EnableChoices(false);
            if (remember && job.Entry == "multi") language.SaveSettings(job.Kind == MaterialKind.OcrImages ? language.Settings with { OcrDefaultRoute = route } : language.Settings with { OriginalDefaultRoute = route });
            if (!running) _ = Run();
        };
        form.Retry += () => _ = Run();
        if (autoStart) form.Shown += (_, _) => _ = Run();
        else { form.Progress(job.Error); form.RetryEnabled = true; }
        form.FormClosed += (_, _) => forms.Remove(job.Id);
        form.Show(owner);
    }
    internal void ShowRecent()
    {
        var history = new Form { Text = "最近图片", Width = 620, Height = 420, Font = new Font("微软雅黑", 10), StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill };
        list.Items.AddRange(store.Recent().Select(j => new HistoryItem(j)).ToArray());
        var resume = new Button { Text = "打开 / 继续处理", Dock = DockStyle.Bottom, Height = 38 };
        void Selected() { if (list.SelectedItem is HistoryItem item) { Open(item.Job); history.Close(); } }
        resume.Click += (_, _) => Selected(); list.DoubleClick += (_, _) => Selected(); history.Controls.Add(list); history.Controls.Add(resume); history.Show(owner);
    }
    private sealed record HistoryItem(ImageMaterialJob Job)
    { public override string ToString() => Job.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + "  正面 " + Job.Questions.Length + " / 反面 " + Job.Answers.Length + (Job.DestinationId != null ? " · 已处理" : Job.Error != null ? " · 待重试" : " · 待选择"); }
    public void Dispose() { lifetime.Cancel(); foreach (var form in forms.Values.ToArray()) form.Close(); http.Dispose(); lifetime.Dispose(); }
}

internal sealed class ImagePreparationForm : Form
{
    private bool lifetimeDisposed;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(5), ForeColor = Color.FromArgb(38, 90, 150) };
    private readonly FlowLayoutPanel choices = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button retry = new() { Text = "重试未完成步骤", AutoSize = true, Enabled = false, Name = "knowledge-clipper.image.retry" };
    private readonly RichTextBox text = new() { Dock = DockStyle.Bottom, ReadOnly = true, Height = 140, Visible = false };
    internal event Action<string, bool> RouteChosen;
    internal event Action Retry;
    internal CancellationToken Token => lifetime.Token;
    internal bool RetryEnabled { set { if (!IsDisposed) retry.Enabled = value; } }
    internal ImagePreparationForm(ImageMaterialJob job, MaterialRoute[] routes)
    {
        Text = job.Entry == "multi" ? "多图模式" : job.Route switch { "language" => "图片语言学习", "question" => "图片问答", _ => "图片摘录" };
        Name = "knowledge-clipper.image.preparation"; Size = new Size(740, 650); MinimumSize = new Size(600, 400);
        Font = new Font("微软雅黑", 10); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var remember = new CheckBox { Text = "将所选路径设为多图模式的默认", AutoSize = true, Visible = job.Entry == "multi" };
        foreach (var route in routes)
        {
            var button = new Button { Text = route.Name, AutoSize = true, Name = "knowledge-clipper.route." + route.Id };
            button.Click += (_, _) => { Progress("已选择：" + route.Name + "；等待材料处理完成…"); RouteChosen?.Invoke(route.Id, remember.Checked); }; choices.Controls.Add(button);
        }
        choices.Controls.Add(remember); choices.Enabled = job.Route == null;
        var images = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };
        images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var image in job.Images)
        {
            images.Controls.Add(new Label { Text = (image.Role == "question" ? "正面 " : "反面 ") + image.Index, AutoSize = true });
            ExcerptDraftForm.AddImage(images, image);
        }
        var preview = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; preview.Controls.Add(images); preview.Controls.Add(text);
        retry.Click += (_, _) => Retry?.Invoke();
        root.Controls.Add(choices, 0, 0); root.Controls.Add(status, 0, 1); root.Controls.Add(preview, 0, 2); root.Controls.Add(retry, 0, 3); Controls.Add(root);
        Progress(job.Route == null ? job.Kind == MaterialKind.OcrImages ? "请选择后续路径。图片转文字会在后台同时进行。" : "请选择原图制卡模式。" : "处理路径：" + routes.FirstOrDefault(r => r.Id == job.Route)?.Name);
        status.MaximumSize = new Size(670, 0); SizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(400, ClientSize.Width - 38), 0);
        FormClosed += (_, _) => lifetime.Cancel();
    }
    internal void Progress(string message) { if (!IsDisposed && !Disposing) status.Text = message; }
    internal void EnableChoices(bool enabled) { if (!IsDisposed) choices.Enabled = enabled; }
    internal void ShowText(string value) { if (!IsDisposed) { text.Text = value; text.Visible = true; } }
    protected override void Dispose(bool disposing) { if (disposing && !lifetimeDisposed) { lifetimeDisposed = true; lifetime.Cancel(); lifetime.Dispose(); } base.Dispose(disposing); }
}

internal sealed class MaterialRouteForm : Form
{
    internal string SelectedRoute { get; private set; }
    internal bool Remember { get; private set; }
    internal MaterialRouteForm(MaterialRoute[] routes)
    {
        Text = "选择处理路径"; Name = "knowledge-clipper.route.ask"; Font = new Font("微软雅黑", 10);
        ClientSize = new Size(440, 125); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        var remember = new CheckBox { Text = "将所选路径设为文字输入默认", AutoSize = true };
        foreach (var route in routes)
        {
            var button = new Button { Text = route.Name, AutoSize = true, Name = "knowledge-clipper.route." + route.Id };
            button.Click += (_, _) => { SelectedRoute = route.Id; Remember = remember.Checked; DialogResult = DialogResult.OK; Close(); }; root.Controls.Add(button);
        }
        root.Controls.Add(remember); Controls.Add(root);
    }
}
