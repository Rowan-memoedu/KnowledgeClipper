using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace RemNoteQuickClipper
{
    internal static class SettingsPreview
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            var configPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "appsettings.json"));
            var config = AppConfig.Load(configPath);
            using (var form = new SettingsForm(config, UserSettings.Load(config)))
            {
                form.Shown += delegate
                {
                    var timer = new Timer { Interval = 500 };
                    timer.Tick += delegate
                    {
                        timer.Stop(); var output = "D:\\RemNoteQuickClipperData\\visual-qa"; Directory.CreateDirectory(output);
                        using (var shot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(shot, new Rectangle(Point.Empty, form.Size)); shot.Save(Path.Combine(output, "settings-panel-v4.png"), ImageFormat.Png); }
                        form.Close();
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
        }
    }
}
