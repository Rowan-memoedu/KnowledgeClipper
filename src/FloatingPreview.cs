using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RemNoteQuickClipper
{
    internal static class FloatingPreview
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            var configPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "appsettings.json"));
            var config = AppConfig.Load(configPath);
            using (var form = new ClipperForm(config))
            {
                form.Shown += delegate
                {
                    var timer = new Timer { Interval = 400 };
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        var setStrip = typeof(ClipperForm).GetMethod("SetStrip", BindingFlags.Instance | BindingFlags.NonPublic);
                        var states = new[]
                        {
                            new object[] { StripMode.Empty, null, Color.Empty },
                            new object[] { StripMode.Processing, "处理中…", Color.FromArgb(85, 95, 105) },
                            new object[] { StripMode.Success, "✓", Color.FromArgb(35, 166, 92) },
                            new object[] { StripMode.Replay, "▶ 重播", Color.FromArgb(38, 116, 242) }
                        };
                        using (var composite = new Bitmap(form.Width, form.Height * states.Length))
                        using (var graphics = Graphics.FromImage(composite))
                        {
                            graphics.Clear(Color.White);
                            for (var i = 0; i < states.Length; i++)
                            {
                                setStrip.Invoke(form, states[i]); form.Update();
                                using (var shot = new Bitmap(form.Width, form.Height))
                                {
                                    form.DrawToBitmap(shot, new Rectangle(Point.Empty, form.Size));
                                    graphics.DrawImageUnscaled(shot, 0, i * form.Height);
                                }
                            }
                            var output = "D:\\RemNoteQuickClipperData\\visual-qa"; Directory.CreateDirectory(output);
                            composite.Save(Path.Combine(output, "floating-states-v3.png"), ImageFormat.Png);
                        }
                        form.Close();
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
        }
    }
}
