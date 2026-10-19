using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Text;
using KnowledgeClipper;

internal static partial class Program
{
    private static async Task LinkChecks(byte[] png, byte[] red)
    {
        var url = new Uri("https://example.com/picture.svg?token=a%2Bb");
        foreach (var ordinary in new[] { "https://example.com/article", "https://example.com/?next=a.svg", "https://example.com/figure.svg/view", "text https://example.com/picture.png", "https://example.com/a.png\nhttps://example.com/b.png", "https://example.com/#a.png", "file:///D:/image.png", "blob:https://example.com/id", "data:image/png;base64,AA", "http://127.0.0.1/a.png", "http://[::1]/a.svg", "http://192.168.1.1/a.png", "https://user:pass@example.com/a.svg", "https://example.com:8080/a.svg" })
            Check(ClipboardImageLink.FromText(ordinary) == null, "ordinary or unsupported link keeps text semantics: " + ordinary);
        foreach (var image in new[] { "https://example.com/a.png", "https://example.com/A.JPG?download=1", "https://example.com/a.svg#layer", url.AbsoluteUri })
            Check(ClipboardImageLink.FromText(image)?.AbsoluteUri == image, "explicit image link preserves query and fragment: " + image);
        var data = new DataObject(); data.SetData(DataFormats.Html, "Version:0.9\r\n<!--StartFragment--><a href='https://example.com/'><img src='https://example.com/image?id=1&amp;size=2'></a><!--EndFragment-->");
        Check(ClipboardImageLink.FromDataObject(data)?.AbsoluteUri == "https://example.com/image?id=1&size=2", "single HTML image accepts an extensionless image source");
        data.SetText("https://example.com/article");
        Check(ClipboardImageLink.FromDataObject(data) == null, "ordinary copied link is not overridden by HTML image decoration");
        foreach (var html in new[] { "<p>article<img src='https://example.com/a.png'></p>", "<img src='https://example.com/a.png'><img src='https://example.com/b.png'>", "<a href='https://example.com/a.png'>link</a>" })
        { var item = new DataObject(); item.SetData(DataFormats.Html, html); Check(ClipboardImageLink.FromDataObject(item) == null, "mixed HTML does not trigger image downloading"); }
        using (var bitmap = Bitmap(png))
        {
            data.SetData(DataFormats.Bitmap, bitmap);
            Check(Pixel(SystemClipboardImageSource.EncodeDataObject(data)) == Pixel(png), "native bitmap is available unchanged even beside text and HTML");
        }
        var safeSvg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 50 40'><path fill='red' d='M0 0H50V40H0Z'/></svg>");
        ClipboardImageLinkLoader.ValidateSvg(safeSvg);
        foreach (var unsafeSvg in new[] {
            "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///D:/secret'>]><svg xmlns='http://www.w3.org/2000/svg'>&x;</svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='file:///D:/a.png'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><use href='https://example.com/a.svg#x'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><style>@import 'https://example.com/a.css';</style></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><rect fill='url(https://example.com/a)'/></svg>" })
        {
            var rejected = false; try { ClipboardImageLinkLoader.ValidateSvg(Encoding.UTF8.GetBytes(unsafeSvg)); } catch { rejected = true; }
            Check(rejected, "SVG active or external resource is rejected");
        }
        var requests = new List<string>(); var renders = 0;
        using var http = new HttpClient(new LinkHandler(request => {
            requests.Add(request.RequestUri.AbsoluteUri);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/redirect.svg") { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("/final.svg", UriKind.Relative); return redirect; }
            if (path == "/private.svg") { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("http://127.0.0.1/a.svg"); return redirect; }
            if (path == "/missing.svg") return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path == "/page.svg") return Content(Encoding.UTF8.GetBytes("<html>ordinary page</html>"), "text/html");
            if (path == "/oversize.png") { var large = Content(png, "image/png"); large.Content.Headers.ContentLength = ClipboardImageLinkLoader.MaximumBytes + 1; return large; }
            return path.EndsWith(".png") ? Content(png, "image/png") : Content(safeSvg, "image/svg+xml");
        }));
        var loader = new ClipboardImageLinkLoader(() => "node", "unused", http, (bytes, _) => { renders++; return Task.FromResult(red); });
        var downloaded = await loader.LoadAsync(new Uri("https://example.com/picture.png"), default);
        Check(Pixel(downloaded.Png) == Pixel(png) && renders == 0 && downloaded.Original.Bytes.Span.SequenceEqual(png), "downloaded bitmap uses native decoding and retains original bytes");
        var vector = await loader.LoadAsync(new Uri("https://example.com/redirect.svg"), default);
        Check(Pixel(vector.Png) == Pixel(red) && renders == 1 && vector.Original.SourceUrl.EndsWith("/redirect.svg"), "SVG redirect renders once and retains original link");
        foreach (var path in new[] { "page.svg", "missing.svg", "oversize.png", "private.svg" })
        { var rejected = false; try { await loader.LoadAsync(new Uri("https://example.com/" + path), default); } catch { rejected = true; } Check(rejected, "failed image candidate produces no material: " + path); }
        Check(!requests.Any(x => x.Contains("127.0.0.1")), "redirect to local service is rejected before a request");

        using var owner = new Form(); _ = owner.Handle;
        using var dot = LanguageSelectionDot.ForImages((_, _) => { });
        var source = new FakeSource(png); var shown = new List<ClipboardImageSnapshot>(); var errors = new List<string>();
        var pending = new TaskCompletionSource<(byte[], ClipboardImageOriginal)>();
        CancellationToken token = default; var downloads = 0;
        using var monitor = new ClipboardImageMonitor(owner, item => { shown.Add(item); dot.ShowImage(item); }, dot.HideImage, errors.Add, source,
            isDotPoint: dot.ContainsScreenPoint, loadLink: (_, ct) => { token = ct; downloads++; return pending.Task; });
        monitor.Start(); var anchor = new Point(320, 250);
        source.Copy(null, anchor); Notify(monitor); await Task.Delay(120);
        Check(downloads == 0 && shown.Count == 0 && errors.Count == 0, "ordinary text causes no download, float or diagnostic");
        source.Copy(png, anchor); source.Link = url; var linkReads = source.LinkReads; Notify(monitor);
        await Until(() => shown.Count == 1, "ordinary copied image still displays immediately");
        Check(downloads == 0 && source.LinkReads == linkReads && shown[0].Original == null, "native image takes precedence and never reads a coexisting image link");
        source.CopyLink(url, anchor); Notify(monitor);
        await Until(() => downloads == 1, "image-link download starts");
        await Task.Delay(2200); Check(shown.Count == 1 && !dot.Visible, "slow network does not consume the eventual float lifetime");
        pending.SetResult((red, vector.Original));
        await Until(() => shown.Count == 2, "ready image link shows existing float");
        Check(shown[^1].Anchor == anchor && shown[^1].Original.SourceUrl == vector.Original.SourceUrl, "link material freezes original position and provenance");
        await Task.Delay(1000); Check(dot.Visible && monitor.TryConsume(shown[^1]) && !monitor.TryConsume(shown[^1]), "resolved link retains display lifetime and exactly-once click");

        pending = new TaskCompletionSource<(byte[], ClipboardImageOriginal)>(); source.CopyLink(url, anchor); Notify(monitor);
        await Until(() => downloads == 2, "second link starts"); var cancelledToken = token;
        source.Copy(png, anchor); Notify(monitor);
        await Until(() => shown.Count == 3, "normal image bypasses a cancelled slow download");
        Check(cancelledToken.IsCancellationRequested && Pixel(shown[^1].PngBytes.ToArray()) == Pixel(png), "new normal image cancels link without changing its pixels");
        pending.SetResult((red, vector.Original)); await Task.Delay(100);
        Check(shown.Count == 3 && Pixel(dot.ImageSelection.PngBytes.ToArray()) == Pixel(png), "late old network result cannot replace new image");

        pending = new TaskCompletionSource<(byte[], ClipboardImageOriginal)>(); source.CopyLink(url, anchor); Notify(monitor);
        await Until(() => downloads == 3, "link before ordinary text replacement starts"); cancelledToken = token;
        source.Copy(null, anchor); Notify(monitor); pending.SetResult((red, vector.Original)); await Task.Delay(120);
        Check(cancelledToken.IsCancellationRequested && shown.Count == 3 && !dot.Visible, "ordinary text cancels pending link and cannot resurrect its float");
        pending = new TaskCompletionSource<(byte[], ClipboardImageOriginal)>(); source.CopyLink(url, anchor); Notify(monitor);
        await Until(() => downloads == 4, "failing link starts"); pending.SetException(new HttpRequestException("controlled offline"));
        await Until(() => errors.Count == 1, "failed link uses existing error feedback");
        Check(!dot.Visible && shown.Count == 3, "failed download never offers an actionable image");
        source.Copy(png, anchor); Notify(monitor); await Until(() => shown.Count == 4, "normal image still works after network failure");
        pending = new TaskCompletionSource<(byte[], ClipboardImageOriginal)>(); source.CopyLink(url, anchor); Notify(monitor);
        await Until(() => downloads == 5, "link before screenshot suppression starts"); cancelledToken = token;
        using (monitor.Suppress())
        {
            pending.SetResult((red, vector.Original)); await Task.Delay(120);
            Check(cancelledToken.IsCancellationRequested && shown.Count == 4 && !dot.Visible, "screenshot suppression cancels pending image-link download");
        }
        pending = new TaskCompletionSource<(byte[], ClipboardImageOriginal)>(); source.CopyLink(url, anchor); Notify(monitor);
        await Until(() => downloads == 6, "link before disabling monitoring starts"); cancelledToken = token;
        monitor.Stop(); pending.SetResult((red, vector.Original)); await Task.Delay(120);
        Check(cancelledToken.IsCancellationRequested && shown.Count == 4 && !dot.Visible, "disabling monitoring cancels link without a late float");
        var saved = new ClipboardImageSnapshot(10, anchor, DateTimeOffset.UtcNow, vector.Png, vector.Original);
        ClipboardImageLinkLoader.SaveOriginal(output, saved); ClipboardImageLinkLoader.SaveOriginal(output, saved);
        Check(Directory.GetFiles(Path.Combine(output, "image-link-sources"), "*.svg", SearchOption.AllDirectories).Length == 1,
            "original SVG is saved idempotently outside the clipboard and card pipeline");
    }
    private static async Task LiveLinkChecks(string helper)
    {
        var node = Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_IMAGE_LINK_NODE") ?? "node";
        // This explicit opt-in downloads only the public example supplied by the user.
        var url = new Uri("https://storage.googleapis.com/3blue1brown-website-bucket/lessons/2016/inverse-matrices/rank_2.svg");
        var loader = new ClipboardImageLinkLoader(() => node, helper);
        using var owner = new Form(); _ = owner.Handle;
        using var dot = LanguageSelectionDot.ForImages((_, _) => { });
        var source = new FakeSource(null); ClipboardImageSnapshot selected = null; string failure = null;
        using var monitor = new ClipboardImageMonitor(owner, item => { selected = item; dot.ShowImage(item); }, dot.HideImage,
            message => failure = message, source, isDotPoint: dot.ContainsScreenPoint, loadLink: loader.LoadAsync);
        monitor.Start(); source.CopyLink(url, new Point(480, 320)); Notify(monitor);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (selected == null && failure == null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(failure == null && selected != null, "public SVG resolves through production downloader and native renderer: " + failure);
        using (var rendered = Bitmap(selected.PngBytes.ToArray()))
            Check(rendered.Width == 3840 && rendered.Height == 2160, "public viewBox-only SVG preserves 16:9 dimensions at 2x resolution");
        Check(dot.Visible && selected.Original.SourceUrl == url.AbsoluteUri && selected.Original.MediaType == "image/svg+xml",
            "real image link opens the existing five-action float with preserved source");
        using (var capture = new Bitmap(dot.Width, dot.Height))
        { dot.DrawToBitmap(capture, dot.ClientRectangle); var path = Path.Combine(output, "public-svg-float.png"); capture.Save(path); screenshots.Add(path); }
        File.WriteAllBytes(Path.Combine(output, "public-svg-render.png"), selected.PngBytes.ToArray());
        ClipboardImageLinkLoader.SaveOriginal(output, selected);
        Check(monitor.TryConsume(selected), "real SVG snapshot is accepted by the same image action gate");

        var fixture = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 60'><defs><linearGradient id='g'><stop stop-color='#f00'/><stop offset='1' stop-color='#00f'/></linearGradient></defs><rect x='10' y='10' width='80' height='40' fill='url(#g)'/></svg>");
        using var http = new HttpClient(new LinkHandler(_ => Content(fixture, "image/svg+xml")));
        var independent = await new ClipboardImageLinkLoader(() => node, helper, http).LoadAsync(new Uri("https://example.com/independent.svg"), default);
        using var picture = Bitmap(independent.Png);
        Check(picture.Width == 200 && picture.Height == 120 && picture.GetPixel(0, 0).A == 0 && picture.GetPixel(100, 60).A == 255,
            "independent SVG preserves dimensions, transparent margins and internal gradient references");
    }
    private static HttpResponseMessage Content(byte[] bytes, string type)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        result.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type); return result;
    }
    private sealed class LinkHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
