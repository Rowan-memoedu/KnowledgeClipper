using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace RemNoteQuickClipper
{
    internal static class ReviewPreview
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            var image = new Bitmap(720, 360);
            using (var g = Graphics.FromImage(image))
            using (var font = new Font("微软雅黑", 26, FontStyle.Bold))
            {
                g.Clear(Color.WhiteSmoke);
                g.DrawString("图片剪藏预览\nE = mc²", font, Brushes.Black, 45, 95);
            }
            using (var form = new ReviewForm("# MinerU 原稿\r\n\r\n这里保留 OCR 的原始结构。\r\n\r\nCopyright 2026 Example", "# 清洗稿\r\n\r\n这里保留 OCR 的原始结构。", image, null))
            {
                form.Shown += delegate
                {
                    var timer = new Timer { Interval = 700 };
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        var output = "D:\\RemNoteQuickClipperData\\visual-qa";
                        Directory.CreateDirectory(output);
                        using (var shot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(shot, new Rectangle(Point.Empty, form.Size)); shot.Save(Path.Combine(output, "review-window.png"), ImageFormat.Png); }
                        form.Close();
                    };
                    timer.Start();
                };
                form.ShowDialog();
            }
            image.Dispose();
        }
    }
}
