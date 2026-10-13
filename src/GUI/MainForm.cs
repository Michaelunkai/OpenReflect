using OpenReflect.Core.Abstractions;

namespace OpenReflect.GUI;

/// <summary>OpenReflect main window — original, functional WinForms shell: Backup / Restore /
/// Verify / Schedule tabs, progress bar, tray icon, typed error dialogs.</summary>
public class MainForm : Form
{
    private readonly GuiBackupService _service = new();
    private readonly System.ComponentModel.IContainer _components = new System.ComponentModel.Container();
    private readonly NotifyIcon _tray;
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 22, Visible = false };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f) };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    public MainForm()
    {
        Text = "OpenReflect 0.1 — open disk imaging";
        Size = new Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;

        _tray = new NotifyIcon(_components)
        {
            Icon = Icon,
            Text = "OpenReflect",
            Visible = true,
        };

        var status = new StatusStrip();
        status.Items.Add(new ToolStripStatusLabel("ready"));

        Controls.Add(_tabs);
        Controls.Add(_progress);
        Controls.Add(_log);
        Controls.Add(status);

        _tabs.TabPages.Add(BuildBackupTab());
        _tabs.TabPages.Add(BuildRestoreTab());
        _tabs.TabPages.Add(BuildVerifyTab());
        _tabs.TabPages.Add(BuildScheduleTab());

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1500, "OpenReflect", "Minimized to tray. Use the tray menu to exit.", ToolTipIcon.Info);
                var menu = new ContextMenuStrip();
                menu.Items.Add("Show", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; });
                menu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
                _tray.ContextMenuStrip = menu;
            }
        };
    }

    private void LogLine(string text)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => _log.AppendText(text + Environment.NewLine)); return; }
        _log.AppendText(text + Environment.NewLine);
    }

    private void ShowError(Error error) =>
        MessageBox.Show(this, error.Message + (error.Detail is null ? "" : $"\n\n{error.Detail}"), $"OpenReflect — error {(int)error.Code}",
            MessageBoxButtons.OK, MessageBoxIcon.Error);

    private TabPage BuildBackupTab()
    {
        var src = new TextBox { Width = 420, Text = "" };
        var dest = new TextBox { Width = 420, Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        var prefix = new TextBox { Width = 120, Text = "image" };
        var kind = new ComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        kind.Items.AddRange(["full", "incremental", "differential"]);
        kind.SelectedIndex = 0;
        var parent = new TextBox { Width = 420 };
        var compress = new CheckBox { Text = "Compress (deflate)", Checked = true, AutoSize = true };
        var encrypt = new CheckBox { Text = "Encrypt (AES-256-GCM)", AutoSize = true };
        var pwEnv = new TextBox { Width = 260, PlaceholderText = "environment variable name" };
        var skipZero = new CheckBox { Text = "Used-sector imaging (skip zero regions)", Checked = true, AutoSize = true };
        var go = new Button { Text = "Create image", AutoSize = true };

        go.Click += async (_, _) =>
        {
            if (!File.Exists(src.Text) && !src.Text.StartsWith(@"\\.\")) { MessageBox.Show(this, "Source does not exist.", "OpenReflect"); return; }
            string? password() => encrypt.Checked ? Environment.GetEnvironmentVariable(pwEnv.Text) : null;
            var options = new ImageOptions
            {
                Compression = compress.Checked ? CompressionKind.Deflate : CompressionKind.None,
                Encryption = encrypt.Checked ? EncryptionKind.Aes256Gcm : EncryptionKind.None,
                PasswordProvider = password,
                SkipZeroRegions = skipZero.Checked,
            };
            if (encrypt.Checked && string.IsNullOrEmpty(password()))
            {
                MessageBox.Show(this, $"Environment variable '{pwEnv.Text}' is not set.", "OpenReflect");
                return;
            }
            go.Enabled = false;
            _progress.Visible = true;
            var progress = new Progress<ProgressInfo>(p =>
            {
                _progress.Value = (int)Math.Min(100, p.ProcessedBytes * 100 / Math.Max(1, p.TotalBytes));
                _tray.Text = $"OpenReflect — {p.Phase} {p.ProcessedBytes * 100 / Math.Max(1, p.TotalBytes)}%";
            });
            LogLine($"[{DateTime.Now:HH:mm:ss}] creating {kind.Text} image…");
            try
            {
                var r = await _service.BackupAsync(src.Text, dest.Text, prefix.Text, kind.Text!,
                    kind.SelectedIndex == 0 ? null : parent.Text, options, progress);
                if (r.IsSuccess) LogLine($"[{DateTime.Now:HH:mm:ss}] OK: {Path.GetFileName(dest.Text)} chunks={r.Value.ChunkCount}");
                else ShowError(r.Error);
            }
            catch (Exception ex) { LogLine(ex.Message); MessageBox.Show(this, ex.Message, "OpenReflect"); }
            finally { go.Enabled = true; _progress.Visible = false; _tray.Text = "OpenReflect"; }
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        void Row(string label, Control c) { table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }); table.Controls.Add(c); }
        Row("Source (file / VHD / device path):", src);
        Row("Destination folder:", dest);
        Row("File prefix:", prefix);
        Row("Backup type:", kind);
        Row("Parent image (inc/diff):", parent);
        table.Controls.Add(compress); table.Controls.Add(encrypt);
        Row("Password env var:", pwEnv);
        table.Controls.Add(skipZero);
        table.Controls.Add(new Label()); table.Controls.Add(go);
        return new TabPage("Backup") { Controls = { table, new Panel { Dock = DockStyle.Fill } } };
    }

    private TabPage BuildRestoreTab()
    {
        var image = new TextBox { Width = 420 };
        var target = new TextBox { Width = 420 };
        var pw = new TextBox { Width = 260, PlaceholderText = "password (if encrypted)", UseSystemPasswordChar = true };
        var dry = new CheckBox { Text = "Dry run (plan only)", Checked = true, AutoSize = true };
        var confirm = new CheckBox { Text = "I understand the target will be overwritten", AutoSize = true };
        var go = new Button { Text = "Plan + restore", AutoSize = true };

        go.Click += async (_, _) =>
        {
            if (!File.Exists(image.Text)) { MessageBox.Show(this, "Image not found.", "OpenReflect"); return; }
            var plan = await _service.PlanRestoreAsync(image.Text, target.Text, pw.Text.Length == 0 ? null : pw.Text);
            if (!plan.IsSuccess) { ShowError(plan.Error); return; }
            LogLine($"[{DateTime.Now:HH:mm:ss}] plan: {plan.Value.Regions.Count} regions, {plan.Value.BytesToWrite:N0} B");
            if (!dry.Checked && !confirm.Checked)
            {
                var answer = MessageBox.Show(this, $"Write {plan.Value.BytesToWrite:N0} bytes to\n{target.Text}?\n\nThis overwrites the target.", "Confirm restore",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { LogLine("restore cancelled by user"); return; }
            }
            var r = await _service.RestoreAsync(plan.Value, confirmed: !dry.Checked && confirm.Checked, pw.Text.Length == 0 ? null : pw.Text);
            if (r.IsSuccess) LogLine(dry.Checked ? "dry-run OK (nothing written)" : "restore complete");
            else ShowError(r.Error);
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        void Row(string label, Control c) { table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }); table.Controls.Add(c); }
        Row("Image (.orf):", image);
        Row("Target file / mounted VHD:", target);
        Row("Password:", pw);
        table.Controls.Add(dry); table.Controls.Add(confirm);
        table.Controls.Add(new Label()); table.Controls.Add(go);
        return new TabPage("Restore") { Controls = { table } };
    }

    private TabPage BuildVerifyTab()
    {
        var image = new TextBox { Width = 420 };
        var pw = new TextBox { Width = 260, PlaceholderText = "password (if encrypted)", UseSystemPasswordChar = true };
        var go = new Button { Text = "Verify (full hash)", AutoSize = true };
        go.Click += async (_, _) =>
        {
            if (!File.Exists(image.Text)) { MessageBox.Show(this, "Image not found.", "OpenReflect"); return; }
            var r = await _service.VerifyAsync(image.Text, pw.Text.Length == 0 ? null : pw.Text);
            if (r.Success) LogLine($"[{DateTime.Now:HH:mm:ss}] verify OK: {r.ChunksChecked} chunks, complete={r.IsComplete}");
            else
            {
                LogLine($"verify FAILED: {string.Join("; ", r.FailedChunks.Select(f => $"chunk {f.ChunkIndex}: {f.Reason}"))}");
                MessageBox.Show(this, "Image verification FAILED — see log.", "OpenReflect", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        table.Controls.Add(new Label { Text = "Image (.orf):", AutoSize = true }); table.Controls.Add(image);
        table.Controls.Add(new Label { Text = "Password:", AutoSize = true }); table.Controls.Add(pw);
        table.Controls.Add(new Label()); table.Controls.Add(go);
        return new TabPage("Verify") { Controls = { table } };
    }

    private TabPage BuildScheduleTab()
    {
        var list = new ListBox { Width = 560, Height = 200 };
        var name = new TextBox { Width = 180 };
        var job = new TextBox { Width = 340 };
        var kind = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
        kind.Items.AddRange(["daily", "weekly", "monthly", "logon"]);
        kind.SelectedIndex = 0;
        var time = new TextBox { Width = 80, Text = "03:00" };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var add = new Button { Text = "Add", AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };

        async Task RefreshList()
        {
            list.Items.Clear();
            var r = await _service.Scheduler.ListAsync();
            if (r.IsSuccess) foreach (var s in r.Value) list.Items.Add(s.Name);
        }
        refresh.Click += async (_, _) => await RefreshList();
        add.Click += async (_, _) =>
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "OpenReflect.CLI.exe");
            var spec = new JobScheduleSpec($"OpenReflect-{name.Text}", $"\"{exe}\" jobs \"{job.Text}\"", AppContext.BaseDirectory,
                kind.Text switch { "weekly" => ScheduleKind.Weekly, "monthly" => ScheduleKind.Monthly, "logon" => ScheduleKind.Logon, _ => ScheduleKind.Daily }, time.Text);
            var r = await _service.Scheduler.RegisterAsync(spec);
            if (!r.IsSuccess) ShowError(r.Error); else await RefreshList();
        };
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string sel) return;
            var r = await _service.Scheduler.DeleteAsync(sel);
            if (!r.IsSuccess) ShowError(r.Error); else await RefreshList();
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        table.Controls.Add(list); table.SetColumnSpan(list, 2);
        table.Controls.Add(new Label { Text = "Name:", AutoSize = true }); table.Controls.Add(name);
        table.Controls.Add(new Label { Text = "Job JSON:", AutoSize = true }); table.Controls.Add(job);
        table.Controls.Add(kind); table.Controls.Add(time);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(refresh); buttons.Controls.Add(add); buttons.Controls.Add(remove);
        table.Controls.Add(new Label()); table.Controls.Add(buttons);
        return new TabPage("Schedule") { Controls = { table } };
    }
}
