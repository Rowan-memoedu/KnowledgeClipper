using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using KnowledgeClipper.Workflow;

internal static class MultiImageUiChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static IEnumerable<Control> Controls(Control parent) => parent.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(Controls(control)));

    internal static async Task RunAsync(string receipt)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Run multi-image UI checks on an isolated Windows desktop.");
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        var checks = new List<string>(); var actions = new List<string>(); string? failure = null;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        var desktop = Assembly.Load("KnowledgeClipper");
        var type = desktop.GetType("KnowledgeClipper.ScreenshotSessionForm", true)!;
        using var form = (Form)type.GetConstructor(All, null, [typeof(Func<string, Task>)], null)!
            .Invoke([(Func<string, Task>)(action => { actions.Add(action); return Task.CompletedTask; })]);
        try
        {
            var stateType = desktop.GetType("KnowledgeClipper.Desktop.Core.DesktopSemanticState", true)!;
            var state = stateType.GetMethod("CreateDefault", All)!.Invoke(null, null)!;
            var session = stateType.GetProperty("screenshotSession")!.GetValue(state)!;
            void Set(string key, object value) => session.GetType().GetProperty(key)!.SetValue(session, value);
            void Update() => type.GetMethod("UpdateState", All)!.Invoke(form, [state]);
            Set("active", true); Set("activeGroup", "question"); Set("questionImageCount", 2); Set("answerImageCount", 1);
            foreach (var flag in new[] { "canContinue", "canFinish", "canUndo", "canCancel" }) Set(flag, true);
            Update(); form.Show(); await Task.Delay(70);
            var buttons = Controls(form).OfType<Button>().ToArray();
            Check(form.Text == "多图模式", "collection window is named 多图模式");
            Check(buttons.Select(button => button.Text).SequenceEqual(new[] { "正面", "反面", "继续", "完成", "撤销上一张", "退出多图" }),
                "collection actions use front and back terminology in their existing order");
            Check(Controls(form).OfType<Label>().Single().Text == "正面 2　反面 1", "image counts retain front and back grouping");
            Check(buttons[0].BackColor == Color.FromArgb(220, 235, 255) && buttons[1].BackColor == SystemColors.Control,
                "front group selection is rendered from the existing question group state");
            Check(buttons.All(button => button.Visible && button.Enabled && button.Parent!.ClientRectangle.Contains(button.Bounds)),
                "all six actions remain fully visible and enabled at actual DPI");
            var expected = new[] { "screenshot.group.set-question", "screenshot.group.set-answer", "screenshot.session.continue",
                "screenshot.session.finish", "screenshot.session.undo-last", "screenshot.session.cancel" };
            foreach (var button in buttons) button.PerformClick();
            Check(actions.SequenceEqual(expected), "renamed controls still dispatch the original grouping and collection operations");
            Set("activeGroup", "answer"); Set("questionImageCount", 3); Set("answerImageCount", 2); Update();
            Check(buttons[1].BackColor == Color.FromArgb(220, 235, 255) && buttons[0].BackColor == SystemColors.Control &&
                Controls(form).OfType<Label>().Single().Text == "正面 3　反面 2", "back selection and updated counts preserve the existing state mapping");
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, form.ClientRectangle);
                image.Save(Path.Combine(Path.GetDirectoryName(receipt)!, "multi-image-mode.png"));
            }
            foreach (var flag in new[] { "canContinue", "canFinish", "canUndo", "canCancel" }) Set(flag, false);
            Update();
            foreach (var button in buttons.Skip(2)) button.PerformClick();
            Check(buttons.Skip(2).All(button => !button.Enabled) && actions.Count == 6,
                "disabled continuation finish undo and cancel cannot dispatch an operation");
            form.Close();
            Check(!form.Visible && !form.IsDisposed && actions.Count == 7 && actions[^1] == "screenshot.session.cancel",
                "closing the collector preserves its existing cancel behavior");
            type.GetMethod("CloseFromOwner", All)!.Invoke(form, null);
            Check(form.IsDisposed && actions.Count == 7, "owner shutdown closes collection without another cancel operation");
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            WorkflowJson.Save(receipt, new { passed = failure == null, checks, error = failure, desktop = desktop.Location,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(desktop.Location))).ToLowerInvariant(),
                scope = "Production multi-image collection form on an isolated Windows desktop with semantic state and action callbacks; no system screenshot or clipboard access." });
        }
    }
}
