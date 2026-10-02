using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using RoboRightClick.App;
using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>
/// One page mapping every config.json field: threads, retries, retry wait, conflict
/// default, max concurrent jobs (0 = unlimited), logging mode (labeled "Ephemeral: write
/// nothing about new jobs to disk"), log retention, start with Windows, notify on complete,
/// show progress window, extra args for copy and move (with the allowed switches listed).
/// Values are checked by round-tripping through <see cref="Core.SettingsSerializer.Parse"/>;
/// any problem is shown next to the field and Save stays disabled. Opened with a field
/// name highlights that field (settings-problem toast click). Also: "Open config file",
/// "Delete all job logs" (asks first; JobLogStore.DeleteAll), and the version. Saving goes
/// through <see cref="SettingsStore.Save"/>; an external edit while open reloads the fields
/// unless the user has unsaved changes, which are kept with a notice.
/// </summary>
/// <remarks>
/// <para>Each field is validated on its own, by parsing a JSON object holding just that key,
/// so a problem always lands next to the field it belongs to; the value saved is the parse
/// of all fields together. The serializer stays the only definition of what is valid.</para>
/// <para>Counting and deleting job logs runs off the UI thread. Single instance: closing
/// hides the window, and showing it again reloads the saved values.</para>
/// </remarks>
internal sealed class SettingsWindow : Gridline.Window
{
    private readonly List<Field> _fields = [];
    private readonly Gridline.CautionStrip _loadNotice;
    private readonly Gridline.CautionStrip _externalNotice;
    private readonly Gridline.CautionStrip _saveError;
    private readonly Gridline.Button _save;
    private readonly Label _logsStatus;
    private readonly Gridline.Button _deleteLogs;
    private readonly Gridline.TextField _threads;
    private readonly Gridline.TextField _retries;
    private readonly Gridline.TextField _retryWait;
    private readonly ComboBox _conflict;
    private readonly Gridline.TextField _maxJobs;
    private readonly Gridline.CheckBox _ephemeral;
    private readonly Gridline.TextField _retention;
    private readonly Gridline.CheckBox _startWithWindows;
    private readonly Gridline.CheckBox _notify;
    private readonly Gridline.CheckBox _progressWindow;
    private readonly Gridline.TextField _extraCopy;
    private readonly Gridline.TextField _extraMove;
    private Settings? _valid;
    private string _loadedState = string.Empty;
    private bool _loading;

    private static readonly (ConflictPolicy Policy, string Label)[] ConflictChoices =
    [
        (ConflictPolicy.Ask, "Ask: Replace, Skip or Let me decide (as Explorer)"),
        (ConflictPolicy.Replace, "Replace the files in the destination"),
        (ConflictPolicy.Skip, "Skip files that already exist"),
        (ConflictPolicy.KeepNewer, "Replace only where the pasted file is newer"),
    ];

    public SettingsWindow(SettingsStore store, Logging.JobLogStore logStore, Jobs.JobManager jobs)
    {
        Store = store;
        LogStore = logStore;
        Jobs = jobs;

        BeginBuild();
        Text = "RoboRightClick settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(560, 420);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        Padding = new Padding(Gridline.Space3);

        _loadNotice = new Gridline.CautionStrip(string.Empty, "LoadProblems") { Visible = false };
        _externalNotice = new Gridline.CautionStrip(
            "config.json was changed outside this window. Your unsaved changes are kept; Save replaces the file with the values shown here, Cancel keeps the file as it is.",
            "ExternalChange")
        {
            Visible = false,
        };
        _saveError = new Gridline.CautionStrip(string.Empty, "SaveError", danger: true) { Visible = false };

        var allowed = string.Join(" ", RobocopyArgs.AllowedSwitches.Order(StringComparer.Ordinal))
            + " " + string.Join(" ", RobocopyArgs.AllowedSizeSwitches.Order(StringComparer.Ordinal).Select(s => s + ":n"));

        _threads = Number("threads");
        _retries = Number("retries");
        _retryWait = Number("retryWaitSeconds");
        _conflict = BuildConflictCombo();
        _maxJobs = Number("maxConcurrentJobs");
        _ephemeral = new Gridline.CheckBox("Ephemeral: write nothing about new jobs to disk", "logging");
        _retention = Number("logRetentionJobs");
        _startWithWindows = new Gridline.CheckBox("Start with Windows", "startWithWindows");
        _notify = new Gridline.CheckBox("Notify when a paste finishes without errors", "notifyOnComplete");
        _progressWindow = new Gridline.CheckBox("Show a progress window for each paste", "showProgressWindow");
        _extraCopy = new Gridline.TextField("extraArgs.copy", mono: true, logicalWidth: 420);
        _extraMove = new Gridline.TextField("extraArgs.move", mono: true, logicalWidth: 420);

        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Gridline.Transparent,
            Padding = Padding.Empty,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddField(table, "threads", "Robocopy threads", _threads,
            $"{Settings.MinThreads} to {Settings.MaxThreads} (robocopy /MT). Default 32.",
            () => IntNode(_threads), s => _threads.Box.Text = Invariant(s.Threads));
        AddField(table, "retries", "Retries per failed file", _retries,
            "0 to 1,000. Default 0, as Explorer: failures are listed at the end with Try again.",
            () => IntNode(_retries), s => _retries.Box.Text = Invariant(s.Retries));
        AddField(table, "retryWaitSeconds", "Wait between retries (seconds)", _retryWait,
            "0 to 3,600.",
            () => IntNode(_retryWait), s => _retryWait.Box.Text = Invariant(s.RetryWaitSeconds));
        AddField(table, "conflictDefault", "When a file already exists", _conflict,
            "Ask shows Explorer's Replace or Skip dialog before copying.",
            () => JsonValue.Create(JsonName(ConflictChoices[Math.Max(0, _conflict.SelectedIndex)].Policy.ToString())),
            s => _conflict.SelectedIndex = Array.FindIndex(ConflictChoices, c => c.Policy == s.ConflictDefault));
        AddField(table, "maxConcurrentJobs", "Pastes running at once", _maxJobs,
            "0 = no limit, as Explorer. Up to 64; extra pastes wait in the queue.",
            () => IntNode(_maxJobs), s => _maxJobs.Box.Text = Invariant(s.MaxConcurrentJobs));
        AddField(table, "logging", "Privacy", _ephemeral,
            "Applies to new jobs. Job logs already on disk stay until you delete them below.",
            () => JsonValue.Create(_ephemeral.Checked ? "ephemeral" : "normal"),
            s => _ephemeral.Checked = s.Logging == LoggingMode.Ephemeral);
        AddField(table, "logRetentionJobs", "Job logs to keep", _retention,
            "1 to 10,000. The oldest are removed first.",
            () => IntNode(_retention), s => _retention.Box.Text = Invariant(s.LogRetentionJobs));
        AddField(table, "startWithWindows", "Startup", _startWithWindows, null,
            () => JsonValue.Create(_startWithWindows.Checked), s => _startWithWindows.Checked = s.StartWithWindows);
        AddField(table, "notifyOnComplete", "Notifications", _notify,
            "Errors, failures and damaging cancels always notify.",
            () => JsonValue.Create(_notify.Checked), s => _notify.Checked = s.NotifyOnComplete);
        AddField(table, "showProgressWindow", "Progress", _progressWindow,
            "Opens about a second after a paste starts, like Explorer's copy dialog.",
            () => JsonValue.Create(_progressWindow.Checked), s => _progressWindow.Checked = s.ShowProgressWindow);
        AddField(table, "extraArgs.copy", "Extra switches for copies", _extraCopy,
            "Allowed: " + allowed + " (n with an optional K, M or G).",
            () => JsonValue.Create(_extraCopy.Box.Text), s => _extraCopy.Box.Text = s.ExtraArgs.Copy);
        AddField(table, "extraArgs.move", "Extra switches for moves", _extraMove,
            "Same switches as for copies.",
            () => JsonValue.Create(_extraMove.Box.Text), s => _extraMove.Box.Text = s.ExtraArgs.Move);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Gridline.White };
        scroll.Controls.Add(table);
        var pane = new Gridline.Pane("Settings") { Dock = DockStyle.Fill };
        pane.Controls.Add(scroll);

        var openConfig = new Gridline.Button("Open config file", "OpenConfig");
        openConfig.Click += (_, _) => OpenConfigFile();
        _deleteLogs = new Gridline.Button("Delete all job logs", "DeleteLogs");
        _deleteLogs.Click += (_, _) => DeleteLogs();
        _logsStatus = Gridline.TextLabel(string.Empty, Gridline.Face.Mono, Gridline.SizeDense, Gridline.TextSecondary);
        _logsStatus.Anchor = AnchorStyles.Left;
        _logsStatus.Name = "LogsStatus";
        var version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var versionLabel = Gridline.TextLabel("Version " + version, Gridline.Face.Mono, Gridline.SizeDense, Gridline.TextSecondary);
        versionLabel.Anchor = AnchorStyles.Left;
        versionLabel.Name = "Version";
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = Gridline.Transparent,
            Padding = new Padding(0, Gridline.Space2, 0, 0),
        };
        tools.Controls.AddRange([openConfig, _deleteLogs, _logsStatus, versionLabel]);

        _save = new Gridline.Button("Save", "Save");
        _save.Click += (_, _) => SaveAndClose();
        var cancel = new Gridline.Button("Cancel", "Cancel");
        cancel.Click += (_, _) => Hide();
        var buttons = Gridline.ButtonRow(_save, cancel);
        buttons.Dock = DockStyle.Bottom;

        var notices = Gridline.Stack(_loadNotice, _externalNotice, _saveError);
        notices.Dock = DockStyle.Top;

        Controls.Add(pane);
        Controls.Add(notices);
        Controls.Add(tools);
        Controls.Add(buttons);
        AcceptButton = _save;
        CancelButton = cancel;
        EndBuild();

        Store.Changed += OnStoreChanged;
    }

    public SettingsStore Store { get; }

    public Logging.JobLogStore LogStore { get; }

    public Jobs.JobManager Jobs { get; }

    /// <summary>Show and activate; optionally focus the field for a config key such as "threads".</summary>
    public void ShowSettings(string? highlightKey = null)
    {
        if (!Visible)
        {
            foreach (var field in _fields)
            {
                field.Label.ForeColor = Gridline.Ink;
            }
            LoadFields(Store.Current);
            ShowLoadProblems(Store.LoadProblems);
            _externalNotice.Visible = false;
            _saveError.Visible = false;
            _logsStatus.Text = string.Empty;
            Show();
        }
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
        BringToFront();
        if (highlightKey is not null && _fields.FirstOrDefault(f => KeyMatches(f.Key, highlightKey)) is { } target)
        {
            target.Label.ForeColor = Gridline.Amber;
            target.Focus.Focus();
            if (target.Row.Parent?.Parent is ScrollableControl panel)
            {
                panel.ScrollControlIntoView(target.Row);
            }
        }
        else
        {
            _fields[0].Focus.Focus();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Closing discards unsaved edits, like Cancel; the window is reused next time.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Store.Changed -= OnStoreChanged;
        base.OnFormClosed(e);
    }

    private sealed record Field(string Key, Label Label, Control Focus, Control Row, Label Problem, Func<JsonNode?> Value, Action<Settings> Load);

    private static bool KeyMatches(string fieldKey, string key) =>
        string.Equals(fieldKey, key, StringComparison.OrdinalIgnoreCase)
        || (key.Equals("extraArgs", StringComparison.OrdinalIgnoreCase) && fieldKey.StartsWith("extraArgs.", StringComparison.Ordinal));

    private Gridline.TextField Number(string key)
    {
        var field = new Gridline.TextField(key, mono: true, logicalWidth: 120);
        field.Box.TextAlign = HorizontalAlignment.Right;
        return field;
    }

    private ComboBox BuildConflictCombo()
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Gridline.White,
            ForeColor = Gridline.Ink,
            DrawMode = DrawMode.OwnerDrawFixed,
            Width = 380,
            Name = "conflictDefault",
            AccessibleName = "conflictDefault",
            Margin = new Padding(0, Gridline.Space1 / 2, Gridline.Space2, Gridline.Space1 / 2),
        };
        Gridline.UseFont(combo, Gridline.Face.Sans, Gridline.SizeUi);
        foreach (var (_, label) in ConflictChoices)
        {
            combo.Items.Add(label);
        }
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
            {
                return;
            }
            var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            Gridline.DrawRow(e.Graphics, e.Bounds, selected);
            Gridline.DrawCellText(e.Graphics, ConflictChoices[e.Index].Label, combo.Font, e.Bounds, Gridline.RowText(selected, Gridline.Ink));
        };
        return combo;
    }

    private void AddField(TableLayoutPanel table, string key, string label, Control input, string? hint, Func<JsonNode?> value, Action<Settings> load)
    {
        var caption = Gridline.TextLabel(label, Gridline.Face.SansMedium, Gridline.SizeUi);
        caption.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        caption.Margin = new Padding(0, Gridline.Space2, Gridline.Space4, 0);
        var problem = Gridline.TextLabel(string.Empty, Gridline.Face.Sans, Gridline.SizeDense, Gridline.Red);
        problem.Name = key + ".problem";
        problem.AccessibleName = key + ".problem";
        problem.Visible = false;
        var rows = new List<Control> { input };
        if (hint is not null)
        {
            rows.Add(Gridline.TextLabel(hint, Gridline.Face.Sans, Gridline.SizeDense, Gridline.TextSecondary));
        }
        rows.Add(problem);
        var cell = Gridline.Stack([.. rows]);
        cell.Margin = new Padding(0, Gridline.Space1, 0, Gridline.Space2);

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(caption, 0, table.RowStyles.Count - 1);
        table.Controls.Add(cell, 1, table.RowStyles.Count - 1);

        var focus = input is Gridline.TextField text ? text.Box : input;
        _fields.Add(new Field(key, caption, focus, cell, problem, value, load));

        switch (focus)
        {
            case TextBox box:
                box.TextChanged += (_, _) => Revalidate();
                break;
            case ComboBox combo:
                combo.SelectedIndexChanged += (_, _) => Revalidate();
                break;
            case System.Windows.Forms.CheckBox check:
                check.CheckedChanged += (_, _) => Revalidate();
                break;
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A whole number becomes a JSON number; anything else stays text, so the parser reports it as not an integer.</summary>
    private static JsonNode? IntNode(Gridline.TextField field)
    {
        var text = field.Box.Text.Trim();
        return int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : JsonValue.Create(text);
    }

    private static string JsonName(string enumName) => char.ToLowerInvariant(enumName[0]) + enumName[1..];

    /// <summary>A JSON object holding the given fields, with extraArgs.copy/move nested as config.json has them.</summary>
    private static JsonObject ToJson(IEnumerable<Field> fields)
    {
        var root = new JsonObject();
        foreach (var field in fields)
        {
            var dot = field.Key.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                root[field.Key] = field.Value();
                continue;
            }
            var parent = field.Key[..dot];
            if (root[parent] is not JsonObject nested)
            {
                nested = new JsonObject();
                root[parent] = nested;
            }
            nested[field.Key[(dot + 1)..]] = field.Value();
        }
        return root;
    }

    private void LoadFields(Settings settings)
    {
        _loading = true;
        foreach (var field in _fields)
        {
            field.Load(settings);
        }
        _loading = false;
        Revalidate();
        _loadedState = ToJson(_fields).ToJsonString();
    }

    private bool HasUnsavedChanges => ToJson(_fields).ToJsonString() != _loadedState;

    private void Revalidate()
    {
        if (_loading)
        {
            return;
        }
        var anyProblem = false;
        foreach (var field in _fields)
        {
            var problems = SettingsSerializer.Parse(ToJson([field]).ToJsonString()).Problems;
            field.Problem.Text = string.Join(" ", problems.Select(p => Clean(field.Key, p)));
            field.Problem.Visible = problems.Count > 0;
            anyProblem |= problems.Count > 0;
        }
        var all = SettingsSerializer.Parse(ToJson(_fields).ToJsonString());
        _valid = !anyProblem && all.Problems.Count == 0 ? all.Settings : null;
        _save.Enabled = _valid is not null;
        _saveError.Visible = false;
    }

    /// <summary>"'threads' must be an integer from 1 to 128; using 32" → "Must be an integer from 1 to 128." (nothing is "used" while Save is disabled).</summary>
    private static string Clean(string key, string problem)
    {
        var text = problem;
        var prefix = $"'{key}' ";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            text = text[prefix.Length..];
        }
        if (text.StartsWith("ignored: ", StringComparison.Ordinal))
        {
            text = text["ignored: ".Length..];
        }
        var cut = text.IndexOf("; using ", StringComparison.Ordinal);
        if (cut >= 0)
        {
            text = text[..cut];
        }
        if (text.EndsWith("; ignored", StringComparison.Ordinal))
        {
            text = text[..^"; ignored".Length];
        }
        text = text.Trim();
        return text.Length == 0 ? problem : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? string.Empty : ".");
    }

    private void ShowLoadProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0)
        {
            _loadNotice.Visible = false;
            return;
        }
        _loadNotice.Text = "config.json has problems, so these settings are using their defaults: "
            + string.Join("; ", problems)
            + ". Save writes the values shown here and fixes the file.";
        _loadNotice.Visible = true;
        foreach (var field in _fields)
        {
            if (problems.Any(p => p.StartsWith($"'{field.Key}'", StringComparison.Ordinal)))
            {
                field.Label.ForeColor = Gridline.Amber;
            }
        }
    }

    private void OnStoreChanged(object? sender, Settings settings)
    {
        if (!Visible)
        {
            return;
        }
        if (HasUnsavedChanges)
        {
            _externalNotice.Visible = true;
            return;
        }
        LoadFields(settings);
        ShowLoadProblems(Store.LoadProblems);
    }

    private void SaveAndClose()
    {
        Revalidate();
        if (_valid is not { } settings)
        {
            return;
        }
        try
        {
            Store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Save also writes the Run value for "Start with Windows", so registry refusals land here too.
            _saveError.Text = $"Couldn't save the settings: {ex.Message} Your changes are still here; close any program that has config.json open and press Save again.";
            _saveError.Visible = true;
            return;
        }
        _loadedState = ToJson(_fields).ToJsonString();
        _externalNotice.Visible = false;
        Hide();
    }

    private async void OpenConfigFile()
    {
        var path = Store.Paths.ConfigFile;
        bool exists;
        try
        {
            exists = await Task.Run(() => File.Exists(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exists = false;
        }
        if (IsDisposed)
        {
            return;
        }
        if (!exists)
        {
            Gridline.Inform(this, "No config file yet",
                $"{path} doesn't exist, so the app is using its defaults. Press Save in this window to create it.");
            return;
        }
        try
        {
            // Notepad by absolute path: .json often has no editor associated, and a
            // "How do you want to open this file?" prompt is a dead end for most people.
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var start = new ProcessStartInfo(notepad) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                Gridline.Inform(this, "Couldn't open config.json", $"Windows could not open {path}: {ex.Message}");
            }
        }
    }

    private async void DeleteLogs()
    {
        _deleteLogs.Enabled = false;
        try
        {
            int count;
            try
            {
                count = await Task.Run(LogStore.CountJobFolders);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logsStatus.Text = "Couldn't read the job logs folder: " + ex.Message;
                return;
            }
            if (IsDisposed)
            {
                return;
            }
            if (count == 0)
            {
                _logsStatus.Text = "There are no job logs on disk.";
                return;
            }
            var logs = count == 1 ? "the 1 job log" : string.Create(CultureInfo.InvariantCulture, $"all {count:N0} job logs");
            if (!Gridline.Confirm(this, "Delete all job logs",
                $"Delete {logs} and the job history? Logs of jobs that are still running are kept. This can't be undone.",
                "Delete", "ConfirmDelete", "Keep", "KeepLogs"))
            {
                return;
            }
            var active = Jobs.ActiveLogFolders();
            try
            {
                await Task.Run(() => LogStore.DeleteAll(active));
                _logsStatus.Text = "Job logs deleted.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logsStatus.Text = "Some job logs couldn't be deleted: " + ex.Message + " Close any program using them and try again.";
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _deleteLogs.Enabled = true;
            }
        }
    }
}
