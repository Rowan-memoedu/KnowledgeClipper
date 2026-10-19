using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace KnowledgeClipper;

internal static class ClipboardImageLink
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg" };
    internal static Uri FromText(string text)
    {
        var uri = PublicUri(text?.Trim());
        return uri != null && Extensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase) ? uri : null;
    }
    internal static Uri PublicUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.IsLoopback ||
            !uri.DnsSafeHost.Contains('.') && !IPAddress.TryParse(uri.DnsSafeHost, out _)) return null;
        if (IPAddress.TryParse(uri.DnsSafeHost, out var address) && !IsPublic(address)) return null;
        return uri;
    }
    internal static Uri FromDataObject(IDataObject data)
    {
        if (data == null) return null;
        var text = data.GetData(DataFormats.UnicodeText, true) as string;
        // A normal copied link/text retains precedence over any HTML decoration.
        if (!string.IsNullOrWhiteSpace(text)) return FromText(text);
        var html = data.GetData(DataFormats.Html, false) as string;
        if (string.IsNullOrWhiteSpace(html) || html.Length > 131072) return null;
        var start = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start) html = html.Substring(start + 20, end - start - 20);
        // Accept one standalone image (optionally linked), never an article or a
        // normal copied hyperlink which happens to contain a thumbnail.
        var match = Regex.Match(html, "^\\s*(?:<a\\b[^>]*>\\s*)?<img\\b(?<attrs>[^<>]+)>\\s*(?:</a>\\s*)?$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        if (!match.Success) return null;
        var src = Regex.Match(match.Groups["attrs"].Value, "(?:^|\\s)src\\s*=\\s*(?:\"(?<url>[^\"]+)\"|'(?<url>[^']+)')", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        return src.Success ? PublicUri(WebUtility.HtmlDecode(src.Groups["url"].Value)) : null;
    }
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
            !(b[0] == 198 && b[1] is 18 or 19);
    }
}

internal sealed class ClipboardImageLinkLoader
{
    internal const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = ConnectPublicAsync
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http;
    private readonly Func<byte[], CancellationToken, Task<byte[]>> render;
    internal ClipboardImageLinkLoader(Func<string> nodePath, string helperPath, HttpClient http = null,
        Func<byte[], CancellationToken, Task<byte[]>> render = null)
    {
        this.http = http ?? Client;
        this.render = render ?? ((bytes, ct) => RenderSvgAsync(nodePath(), helperPath, bytes, ct));
    }
    internal async Task<(byte[] Png, ClipboardImageOriginal Original)> LoadAsync(Uri uri, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var originalUrl = uri.AbsoluteUri;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (ClipboardImageLink.PublicUri(uri.AbsoluteUri) == null) throw new InvalidDataException("图片地址不可访问。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/*");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (redirects == 3 || location == null) throw new InvalidDataException("图片链接重定向次数过多。");
                uri = new Uri(uri, location); continue;
            }
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mime != null && !mime.StartsWith("image/") && mime != "application/octet-stream")
                throw new InvalidDataException("链接返回的不是图片。");
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("图片超过 32 MiB。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, MaximumBytes, ct).ConfigureAwait(false);
            byte[] png;
            if (mime == "image/svg+xml" || LooksLikeSvg(bytes))
            {
                ValidateSvg(bytes);
                png = await render(bytes, ct).ConfigureAwait(false); mime = "image/svg+xml";
            }
            else png = DecodeBitmap(bytes);
            if (png.Length > MaximumBytes) throw new InvalidDataException("转换后的图片超过 32 MiB。");
            return (png, new ClipboardImageOriginal(originalUrl, mime ?? "application/octet-stream", bytes));
        }
        throw new InvalidDataException("图片下载未完成。");
    }
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(a => !ClipboardImageLink.IsPublic(a))) throw new HttpRequestException("图片链接不允许访问本机或内网。");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct).ConfigureAwait(false); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); ct.ThrowIfCancellationRequested(); }
        }
        throw new HttpRequestException("无法连接图片服务器。");
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[16384];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("图片数据超过大小限制。");
            output.Write(buffer, 0, count);
        }
    }
    private static byte[] DecodeBitmap(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, false);
        using var image = Image.FromStream(input, false, true);
        if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > 16000000)
            throw new InvalidDataException("链接图片尺寸过大。");
        using var output = new MemoryStream(); image.Save(output, ImageFormat.Png); return output.ToArray();
    }
    private static bool LooksLikeSvg(byte[] bytes)
        => Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).Contains("<svg", StringComparison.OrdinalIgnoreCase);
    internal static void ValidateSvg(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("SVG 超过 2 MiB。");
        using var input = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var document = new XmlDocument { XmlResolver = null }; document.Load(reader);
        if (document.DocumentElement?.LocalName != "svg" || document.DocumentElement.NamespaceURI != "http://www.w3.org/2000/svg")
            throw new InvalidDataException("不是有效的 SVG 图片。");
        foreach (XmlElement element in document.SelectNodes("//*"))
        {
            if (element.LocalName.ToLowerInvariant() is "script" or "foreignobject" or "image" or "animate" or "animatetransform" or "animatemotion" or "set")
                throw new InvalidDataException("暂不支持包含脚本、动态效果或外部图片的 SVG。");
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (attribute.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    attribute.LocalName == "href" && !attribute.Value.StartsWith('#')) throw new InvalidDataException("SVG 外部资源已禁用。");
                ValidateCss(attribute.Value);
            }
            if (element.LocalName == "style") ValidateCss(element.InnerText);
        }
    }
    private static void ValidateCss(string value)
    {
        if (value.Contains("@import", StringComparison.OrdinalIgnoreCase) || value.Contains('\\')) throw new InvalidDataException("SVG 外部样式已禁用。");
        foreach (Match match in Regex.Matches(value, @"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))
            if (!match.Groups[1].Value.Trim().Trim('\'', '"').StartsWith('#')) throw new InvalidDataException("SVG 外部资源已禁用。");
    }
    private static async Task<byte[]> RenderSvgAsync(string nodePath, string helperPath, byte[] bytes, CancellationToken ct)
    {
        var start = new ProcessStartInfo(nodePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(helperPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 SVG 转换组件。");
        using var kill = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumBytes, ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false); process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(ct), output, error).ConfigureAwait(false);
            if (process.ExitCode != 0 || (await output).Length == 0) throw new InvalidDataException("SVG 转换失败。");
            return await output;
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }
    internal static void SaveOriginal(string workDirectory, ClipboardImageSnapshot snapshot)
    {
        var original = snapshot.Original;
        if (original == null) return;
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var bytes = original.Bytes.ToArray();
        var folder = Path.Combine(workDirectory, "image-link-sources", Hash(snapshot.PngBytes.ToArray()));
        Directory.CreateDirectory(folder);
        var name = Hash(bytes) + (original.MediaType == "image/svg+xml" ? ".svg" : ".image");
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { original.SourceUrl, original.MediaType, sourceFile = name });
        var metadataPath = Path.Combine(folder, Hash(metadata) + ".json");
        if (!File.Exists(metadataPath)) File.WriteAllBytes(metadataPath, metadata);
    }
}
