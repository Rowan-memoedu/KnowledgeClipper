using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class ExcerptDraftForm : Form
{
    internal ExcerptSession Session { get; }
    internal event Action<ExcerptSession> CardsChanged;
    private readonly ExcerptStore store;
    private readonly Func<ModeSnapshot[]> loadModes;
    private readonly Func<ModeSnapshot, CancellationToken, Task> generate;
    private readonly Func<string[], CancellationToken, Action, Task> import;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CheckedListBox modes = new() { CheckOnClick = true, Width = 270, Height = 58 };
    private readonly FlowLayoutPanel cards = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly RichTextBox manualFront = Editor("manual-front", 120), manualBack = Editor("manual-back", 68);
    private readonly CheckBox cloze = new() { Text = "挖空：在正面选中文字后立即生成一张卡", AutoSize = true };
    private readonly Label notice = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(38, 90, 150), Padding = new Padding(4) };
    private readonly TableLayoutPanel manual = new() { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Visible = false };
    private readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly FlowLayoutPanel modeBar = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Dictionary<string, (RichTextBox Front, RichTextBox Back)> edits = new();
    private readonly Dictionary<string, CheckBox> checks = new();
    private readonly HashSet<string> selected = new();
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 600 };
    private bool busy, loading;

    internal ExcerptDraftForm(ExcerptSession session, ExcerptStore store, Func<ModeSnapshot[]> loadModes,
        Func<ModeSnapshot, CancellationToken, Task> generate, Func<string[], CancellationToken, Action, Task> import)
    {
        Session = session; this.store = store; this.loadModes = loadModes; this.generate = generate; this.import = import;
        Text = "摘录 · " + session.Original.SelectedText.Replace("\n", " ").TakeText(28);
        Name = AccessibleName = "knowledge-clipper.excerpt.preview";
        Font = new Font("微软雅黑", 10); Size = new Size(850, 840); MinimumSize = new Size(650, 560);
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        modes.Name = modes.AccessibleName = "knowledge-clipper.excerpt.modes";
        modeBar.Controls.Add(new Label { Text = "制卡模式", AutoSize = true, Margin = new Padding(3, 8, 8, 0) });
        modeBar.Controls.Add(modes);
        var run = Button("应用所选提示词", "generate"); run.Click += async (_, _) => await GenerateAsync();
        var reload = Button("刷新模式", "reload-modes"); reload.Click += (_, _) => LoadModes();
        modeBar.Controls.Add(run); modeBar.Controls.Add(reload);
        modes.ItemCheck += (_, e) => { if (e.Index == 0) manual.Visible = e.NewValue == CheckState.Checked; };
        manualFront.Text = session.ManualFront; manualBack.Text = session.ManualBack;
        manual.Controls.Add(new Label { Text = "手动正面", AutoSize = true }); manual.Controls.Add(manualFront);
        manual.Controls.Add(new Label { Text = "手动反面（可以留空）", AutoSize = true }); manual.Controls.Add(manualBack);
        cloze.Name = cloze.AccessibleName = "knowledge-clipper.excerpt.cloze";
        var manualActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var add = Button("添加手动卡", "add-manual");
        add.Click += (_, _) => Mutate(() => { SaveManual(); ExcerptCards.Add(Session, store, new(manualFront.Text, manualBack.Text), "手动"); });
        manualActions.Controls.Add(add); manualActions.Controls.Add(cloze); manual.Controls.Add(manualActions);
        manualFront.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) CaptureCloze(); };
        manualFront.KeyUp += (_, e) => { if (e.KeyCode == Keys.ShiftKey || e.Control && e.KeyCode == Keys.A) CaptureCloze(); };
        manualFront.TextChanged += (_, _) => { saveTimer.Stop(); saveTimer.Start(); };
        manualBack.TextChanged += (_, _) => { saveTimer.Stop(); saveTimer.Start(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); try { SaveManual(); } catch (Exception ex) { notice.Text = ex.Message; } };
        var merge = Button("合并勾选的挖空卡", "merge");
        merge.Click += (_, _) => Mutate(() => { ExcerptCards.Merge(Session, store, selected); selected.Clear(); });
        var all = Button("导入全部未导入卡片", "import-all");
        all.Click += async (_, _) => await ImportAsync(Session.Cards.Where(c => c.SupersededBy == null).Select(c => c.Id).ToArray());
        actions.Controls.Add(merge); actions.Controls.Add(all);
        root.Controls.Add(modeBar, 0, 0); root.Controls.Add(manual, 0, 1); root.Controls.Add(cards, 0, 2); root.Controls.Add(actions, 0, 3); root.Controls.Add(notice, 0, 4);
        Controls.Add(root);
        cards.SizeChanged += (_, _) => FitCards();
        FormClosing += (_, e) =>
        {
            if (busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; notice.Text = "正在处理卡片，请等待完成后关闭。"; return; }
            try { SaveManual(); SaveEdits(); }
            catch (Exception ex) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; notice.Text = "草稿未保存：" + ex.Message; } }
        };
        FormClosed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); saveTimer.Dispose(); };
        LoadModes(); RefreshCards();
        if (Session.Cards.Any(c => ExcerptCards.Get(Session, store, c.Id).Excerpt.HasImages))
        {
            modeBar.Visible = false; merge.Visible = false;
            manual.Visible = false; cloze.Enabled = false;
            notice.Text = Session.Cards.All(c => ExcerptCards.Get(Session, store, c.Id).Excerpt.BackImages.Length > 0)
                ? "正面与反面图片按各自采集顺序组成卡片。" : "正面保留原图，不支持文字挖空；反面文字可在导入前编辑。";
        }
    }
    private static RichTextBox Editor(string id, int height) => new() { Name = "knowledge-clipper.excerpt." + id,
        AccessibleName = "knowledge-clipper.excerpt." + id, Dock = DockStyle.Top, Height = height, DetectUrls = false,
        BorderStyle = BorderStyle.FixedSingle, MaxLength = int.MaxValue, ScrollBars = RichTextBoxScrollBars.Vertical };
    private static Button Button(string label, string id) => new() { Text = label, Name = "knowledge-clipper.excerpt." + id,
        AccessibleName = "knowledge-clipper.excerpt." + id, AutoSize = true, Padding = new Padding(4), Margin = new Padding(3) };
    private void SaveManual()
    { Session.ManualFront = manualFront.Text; Session.ManualBack = manualBack.Text; store.Save(Session); }
    private void LoadModes()
    {
        if (busy) return;
        var manualVisible = manual.Visible;
        modes.Items.Clear(); modes.Items.Add("手动", manualVisible);
        try
        {
            foreach (var mode in loadModes()) modes.Items.Add(new ModeItem(mode));
            notice.Text = modes.Items.Count == 1 ? "原文可直接导入。提示词模式尚未发布，手动制卡已可用。" : "勾选多个提示词可分别制卡，每个模式每次生成一张。";
        }
        catch (Exception ex) { notice.Text = "提示词未载入，原文与手动功能仍可使用：" + ex.Message; }
    }
    private void CaptureCloze()
    {
        if (!cloze.Checked || busy || manualFront.SelectionLength == 0) return;
        var start = manualFront.SelectionStart; var length = manualFront.SelectionLength;
        Mutate(() => { SaveManual(); ExcerptCards.Cloze(Session, store, start, length); manualFront.Select(start + length, 0); });
    }
    private void Mutate(Action action)
    {
        if (busy) return;
        try { SaveEdits(); action(); RefreshCards(); ShowLastCard(); notice.Text = "草稿已保存。"; }
        catch (Exception ex) { notice.Text = ex.Message; }
    }
    private void SaveEdits()
    {
        foreach (var (id, boxes) in edits)
        {
            if (boxes.Front.ReadOnly && boxes.Back.ReadOnly) continue;
            var draft = ExcerptCards.Get(Session, store, id);
            if (!ExportRouting.Frozen(draft) && (draft.Excerpt.Front != boxes.Front.Text || draft.Excerpt.Back != boxes.Back.Text))
                ExcerptCards.Edit(Session, store, id, boxes.Front.Text, boxes.Back.Text);
        }
    }
    private void SetBusy(bool value)
    { busy = value; modeBar.Enabled = manual.Enabled = actions.Enabled = cards.Enabled = !value; }
    private async Task GenerateAsync()
    {
        if (busy) return;
        var chosen = modes.CheckedItems.OfType<ModeItem>().Select(m => m.Mode).ToArray();
        if (chosen.Length == 0) { notice.Text = "请勾选提示词模式；手动制卡使用下方编辑区。"; return; }
        try { SaveManual(); SaveEdits(); }
        catch (Exception ex) { notice.Text = ex.Message; return; }
        SetBusy(true);
        var errors = new List<string>();
        try
        {
            foreach (var mode in chosen)
            {
                notice.Text = "正在制卡：" + mode.Definition.Name;
                try { await generate(mode, lifetime.Token); }
                catch (Exception ex) { errors.Add(mode.Definition.Name + "：" + ex.Message); }
                if (!IsDisposed) { RefreshCards(); ShowLastCard(); }
            }
            notice.Text = errors.Count == 0 ? "制卡完成，每张卡可独立编辑和导入。" : string.Join("\n", errors);
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }
    private async Task ImportAsync(string[] ids)
    {
        if (busy) return;
        try { SaveManual(); SaveEdits(); }
        catch (Exception ex) { notice.Text = ex.Message; return; }
        SetBusy(true); notice.Text = "正在导入并读回核对…";
        try { await import(ids, lifetime.Token, () => { if (!IsDisposed) RefreshCards(); }); notice.Text = ids.Any(id => store.Cards.Find(id)?.AwaitingAnki == true) ? "内容已保存，等待 Anki 打开后自动导入。" : "本次内容已导入并核对。"; }
        catch (Exception ex) { if (!IsDisposed) notice.Text = "未全部完成；已成功的卡片不会重复导入。" + ex.Message; }
        finally { if (!IsDisposed) { RefreshCards(); SetBusy(false); } }
    }
    internal void RefreshImportState()
    {
        if (IsDisposed || busy) return;
        foreach (var link in Session.Cards.Where(c => c.SupersededBy == null))
        {
            var draft = store.Cards.Find(link.Id);
            if (draft == null) continue;
            var label = cards.Controls.Find("knowledge-clipper.excerpt.state." + link.Id, true).OfType<Label>().FirstOrDefault();
            if (label != null) { label.Text = ImportCaption(draft); label.ForeColor = draft.ImportState == ImportState.Verified ? Color.SeaGreen : Color.DimGray; }
            var button = cards.Controls.Find("knowledge-clipper.excerpt.import." + link.Id, true).OfType<Button>().FirstOrDefault();
            if (button != null) button.Enabled = draft.ImportState != ImportState.Verified;
        }
    }
    private static string ImportCaption(ClipDraft draft) => draft.AwaitingAnki ? "等待 Anki 打开" : draft.ImportState switch
    { ImportState.Verified => ExportRouting.ResultSummary(draft), ImportState.Unknown or ImportState.Writing => "待核对", ImportState.Failed => "导入失败", _ => "未导入" };
    private void RefreshCards()
    {
        var scroll = new Point(-cards.AutoScrollPosition.X, -cards.AutoScrollPosition.Y);
        loading = true; cards.SuspendLayout();
        try
        {
            foreach (Control old in cards.Controls.Cast<Control>().ToArray()) old.Dispose();
            cards.Controls.Clear(); edits.Clear(); checks.Clear();
            foreach (var link in Session.Cards.Where(c => c.SupersededBy == null))
            {
                var draft = ExcerptCards.Get(Session, store, link.Id); var fields = draft.Excerpt;
                var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(10), Margin = new Padding(2, 5, 2, 5), BackColor = Color.FromArgb(246, 248, 251),
                    Name = "knowledge-clipper.excerpt.card." + link.Id };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                var check = new CheckBox { Text = link.Label, AutoSize = true, Checked = selected.Contains(link.Id),
                    Enabled = fields.Kind == ExcerptCardKind.Cloze && fields.ClozeSource != null,
                    Name = "knowledge-clipper.excerpt.select." + link.Id };
                check.CheckedChanged += (_, _) => { if (loading) return; if (check.Checked) selected.Add(link.Id); else selected.Remove(link.Id); };
                checks[link.Id] = check; toolbar.Controls.Add(check);
                var state = ImportCaption(draft);
                toolbar.Controls.Add(new Label { Name = "knowledge-clipper.excerpt.state." + link.Id, Text = state, AutoSize = true, Margin = new Padding(10, 5, 8, 0), ForeColor = draft.ImportState == ImportState.Verified ? Color.SeaGreen : Color.DimGray });
                var one = Button(draft.ImportState is ImportState.Unknown or ImportState.Writing ? "核对本卡" : "导入本卡", "import." + link.Id);
                one.Enabled = draft.ImportState != ImportState.Verified; one.Click += async (_, _) => await ImportAsync([link.Id]); toolbar.Controls.Add(one);
                if (link.Replaces.Length > 0 && !ExportRouting.Frozen(draft))
                {
                    var undo = Button("撤销合并", "undo." + link.Id); undo.Click += (_, _) => Mutate(() => ExcerptCards.UndoMerge(Session, store, link.Id)); toolbar.Controls.Add(undo);
                }
                panel.Controls.Add(toolbar);
                panel.Controls.Add(new Label { Text = fields.Kind == ExcerptCardKind.Cloze ? "正面（挖空语法）" : "正面", AutoSize = true });
                var front = Editor("front." + link.Id, 100); front.Text = fields.Front;
                var original = Session.Cards[0].Id == link.Id && !fields.HasImages;
                front.ReadOnly = original || ExportRouting.Frozen(draft); panel.Controls.Add(front);
                foreach (var image in fields.FrontImages) AddImage(panel, image);
                if (fields.FrontImages.Length > 0) { front.ReadOnly = true; front.Visible = fields.Front.Length > 0; }
                panel.Controls.Add(new Label { Text = fields.Kind == ExcerptCardKind.Cloze ? "反面补充（答案自动还原原文）" : "反面", AutoSize = true });
                var back = Editor("back." + link.Id, 65); back.Text = fields.Back; back.ReadOnly = original || ExportRouting.Frozen(draft); panel.Controls.Add(back);
                foreach (var image in fields.BackImages) AddImage(panel, image);
                if (fields.BackImages.Length > 0) { back.ReadOnly = true; back.Visible = fields.Back.Length > 0; }
                if (fields.HasImages && fields.BackImages.Length == 0) back.Height = 220;
                edits[link.Id] = (front, back);
                if (fields.Kind == ExcerptCardKind.Cloze)
                {
                    panel.Controls.Add(new Label { Text = "反面预览", AutoSize = true });
                    var answer = Editor("answer." + link.Id, 85); answer.ReadOnly = true;
                    answer.Text = ExcerptCloze.Answer(fields.Front) + (fields.Back.Length > 0 ? "\n" + fields.Back : ""); panel.Controls.Add(answer);
                    void UpdateAnswer()
                    {
                        try { answer.Text = ExcerptCloze.Answer(front.Text) + (back.Text.Length > 0 ? "\n" + back.Text : ""); }
                        catch (Exception ex) { answer.Text = ex.Message; }
                    }
                    front.TextChanged += (_, _) => UpdateAnswer(); back.TextChanged += (_, _) => UpdateAnswer();
                }
                panel.Controls.Add(new Label { Text = "导入：" + ExportRouting.Describe(draft) +
                    (ExportRouting.For(draft) == ExportDestination.RemNote && fields.Kind == ExcerptCardKind.Basic && string.IsNullOrWhiteSpace(fields.Back) && fields.BackImages.Length == 0 ? " · 仅保存正面内容" : ""),
                    AutoSize = true, ForeColor = Color.DimGray, Name = "knowledge-clipper.excerpt.destination." + link.Id });
                if (draft.ImportError != null) panel.Controls.Add(new Label { Text = draft.ImportError, AutoSize = true, ForeColor = Color.Firebrick });
                cards.Controls.Add(panel);
            }
            FitCards();
        }
        finally { cards.ResumeLayout(); cards.AutoScrollPosition = scroll; loading = false; }
        CardsChanged?.Invoke(Session);
    }
    private void FitCards()
    {
        var width = Math.Max(200, cards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        foreach (Control card in cards.Controls)
        {
            card.MinimumSize = new Size(width, 0); card.MaximumSize = new Size(width, 0); card.Width = width;
        }
    }
    internal static void AddImage(Control parent, ImageAsset asset)
    {
        using var stream = new System.IO.MemoryStream(asset.ReadVerified());
        using var loaded = Image.FromStream(stream);
        var picture = new PictureBox { Image = new Bitmap(loaded), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Top,
            Height = Math.Clamp(loaded.Height * 650 / Math.Max(1, loaded.Width), 130, 460), Name = "knowledge-clipper.image." + asset.Role + "." + asset.Index };
        picture.Disposed += (_, _) => picture.Image?.Dispose(); parent.Controls.Add(picture);
    }
    private void ShowLastCard() { if (cards.Controls.Count > 0) cards.ScrollControlIntoView(cards.Controls[cards.Controls.Count - 1]); }
    private sealed record ModeItem(ModeSnapshot Mode)
    { public override string ToString() => Mode.Definition.Name + (Mode.LoadError == null ? "" : "（待配置）"); }
}
