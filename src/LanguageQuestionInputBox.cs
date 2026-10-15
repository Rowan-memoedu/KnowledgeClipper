using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

// Intercept native paste as well as Ctrl+V, while leaving ordinary text paste to Windows.
internal sealed class LanguageQuestionInputBox : TextBox
{
    internal const string Hint = "输入问题或粘贴图片；Enter 打开问答";
    private readonly List<byte[]> images = new();
    internal Func<bool> ContainsClipboardImage { get; set; } = Clipboard.ContainsImage;
    internal Func<Image> ReadClipboardImage { get; set; } = Clipboard.GetImage;
    internal IReadOnlyList<byte[]> Images => images;
    internal bool HasQuestion => !string.IsNullOrWhiteSpace(Text) || images.Count > 0;
    internal Button ImageIndicator { get; } = new() {
        Name = "knowledge-clipper.desktop.inline-images", AccessibleName = "已粘贴图片，点击移除最后一张",
        Dock = DockStyle.Fill, Margin = new Padding(0, 4, 4, 3), AutoSize = true, Visible = false, TabStop = true
    };
    internal LanguageQuestionInputBox()
    {
        ImageIndicator.Click += (_, _) => { if (images.Count > 0) images.RemoveAt(images.Count - 1); UpdateImages(); Focus(); };
        Disposed += (_, _) => ImageIndicator.Dispose();
    }
    internal bool PasteImage()
    {
        if (!ContainsClipboardImage()) return false;
        using var image = ReadClipboardImage();
        if (image == null) return false;
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        if (bytes.Length > 33554432) throw new InvalidDataException("图片超过 32 MiB。");
        images.Add(bytes); UpdateImages(); return true;
    }
    internal void ClearQuestion() { Clear(); images.Clear(); UpdateImages(); }
    private void UpdateImages()
    {
        ImageIndicator.Text = $"图 {images.Count} ×";
        ImageIndicator.Visible = images.Count > 0;
        AccessibleDescription = images.Count > 0 ? $"已添加 {images.Count} 张图片，可继续输入文字；Enter 打开问答" : Hint;
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0302 && Enabled && !ReadOnly)
        {
            try { if (PasteImage()) return; }
            catch (Exception ex) { AccessibleDescription = ex.Message; System.Media.SystemSounds.Exclamation.Play(); return; }
        }
        base.WndProc(ref message);
    }
    internal static ImageAsset StoreImage(string dataRoot, byte[] bytes, int index)
    {
        if (bytes.Length > 33554432) throw new InvalidDataException("图片超过 32 MiB。");
        using (var stream = new MemoryStream(bytes)) using (var image = Image.FromStream(stream)) { _ = image.Width; }
        var sha = ImageAsset.Hash(bytes); var root = Path.Combine(dataRoot, "chat-media"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, sha + ".png"); if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        var asset = new ImageAsset(path, sha, "question", index); asset.ReadVerified(); return asset;
    }
}
