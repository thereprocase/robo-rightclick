using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using RoboRightClick.Core;
using RoboRightClick.Jobs;
using RoboRightClick.Logging;

namespace RoboRightClick.UI;

/// <summary>
/// One row per job (newest first): verb, sources summary, destination, state (with
/// "Canceling…", "Paused (waiting)" and the queue reason), progress bar, bytes done/total,
/// files done/total, speed, ETA, error count. Row actions: pause, resume, cancel, try again
/// / show errors (<see cref="ErrorSummaryDialog"/>), open destination, open log (normal-mode
/// jobs whose folder exists). Selecting a job AwaitingDecision brings its conflict dialog to
/// the front. A filter shows only jobs needing attention (toast click). Rows of ephemeral
/// jobs show names (memory only). All text from <see cref="Core.DisplayText"/>. Single
/// instance, reused and activated by the tray; hides rather than closes. While visible, a
/// 250 ms WinForms timer pulls <see cref="JobManager.Snapshots"/> and updates rows in place
/// (BeginUpdate/EndUpdate); hidden, it does no work.
/// </summary>
/// <remarks>
/// <para>The list is a virtual-mode, owner-drawn ListView: a refresh swaps the snapshot list
/// and repaints, and only a change in the number of rows touches the control's item count.
/// The selection follows the job, not the row index, as new jobs push rows down.</para>
/// <para>Checking whether a destination or log folder exists runs off the UI thread: a
/// destination on a dead network share can take many seconds to answer, and the UI thread
/// also serves Explorer's right-click calls.</para>
/// <para>The integration sets <see cref="Prompts"/> after creating <see cref="UiPrompts"/>;
/// until then, selecting a job that waits for a decision cannot raise its dialog.</para>
/// </remarks>
internal sealed class JobsWindow : Gridline.Window
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private enum Column
    {
        State,
        Job,
        Destination,
        Progress,
        Bytes,
        Files,
        Speed,
        Eta,
        Errors,
        Status,
    }

    // Logical widths at 96 dpi. Status takes whatever the window has left (at least its
    // width here), so at the default size every column is visible without scrolling sideways.
    private static readonly (string Caption, int Width)[] Columns =
    [
        ("State", 100),
        ("Job", 200),
        ("To", 180),
        ("Progress", 110),
        ("Done", 150),
        ("Files", 90),
        ("Speed", 90),
        ("ETA", 64),
        ("Errors", 64),
        ("Status", 220),
    ];

    private readonly System.Windows.Forms.Timer _poll;
    private readonly JobList _list;
    private readonly ColumnHeaderStrip _header;
    private readonly ImageList _rowHeight;
    private readonly Label _empty;
    private readonly CountsBlock _counts;
    private readonly Gridline.StatusBar _status;
    private readonly Gridline.CautionStrip _ephemeralNotice;
    private readonly Gridline.CheckBox _attentionOnly;
    private readonly Gridline.Button _pause;
    private readonly Gridline.Button _resume;
    private readonly Gridline.Button _cancel;
    private readonly Gridline.Button _errors;
    private readonly Gridline.Button _openDestination;
    private readonly Gridline.Button _openLog;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<Guid, ErrorSummaryDialog> _summaries = [];

    private IReadOnlyList<JobSnapshot> _rows = [];
    private IReadOnlyList<JobSnapshot>? _lastAll;
    private LoggingMode? _lastMode;
    private Guid? _selected;
    private bool _restoringSelection;
    private bool _syncingFilter;

    public JobsWindow(JobManager jobs, JobLogStore logStore)
    {
        Jobs = jobs;
        LogStore = logStore;

        BeginBuild();
        Text = "RoboRightClick jobs";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 560);
        MinimumSize = new Size(720, 400);
        ShowInTaskbar = true;
        Padding = new Padding(Gridline.Space3, Gridline.Space3, Gridline.Space3, 0);

        _counts = new CountsBlock { Dock = DockStyle.Top, Height = 72 };

        _pause = ToolButton("Pause", "Pause", () => ForSelected(PauseJob));
        _resume = ToolButton("Resume", "Resume", () => ForSelected(ResumeJob));
        _cancel = ToolButton("Cancel", "Cancel", () => ForSelected(j => Jobs.Cancel(j.Id)));
        _errors = ToolButton("Show errors", "ShowErrors", () => ForSelected(ShowSummary));
        _openDestination = ToolButton("Open destination", "OpenDestination", () => ForSelected(j => OpenFolder(j.Destination)));
        _openLog = ToolButton("Open log", "OpenLog", () => ForSelected(OpenLog));
        _attentionOnly = new Gridline.CheckBox("Only jobs needing attention", "AttentionOnly") { Margin = new Padding(Gridline.Space3, 10, 0, 0) };
        _attentionOnly.CheckedChanged += (_, _) =>
        {
            if (!_syncingFilter)
            {
                RefreshRows(force: true);
            }
        };
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = Gridline.Transparent,
            Padding = new Padding(0, Gridline.Space2, 0, Gridline.Space2),
        };
        toolbar.Controls.AddRange([_pause, _resume, _cancel, _errors, _openDestination, _openLog, _attentionOnly]);

        _ephemeralNotice = new Gridline.CautionStrip(
            "Ephemeral mode is on: new jobs write nothing to disk. Names in this list stay in memory and are gone when the app exits.",
            "EphemeralNotice")
        {
            Visible = false,
        };
        // In a one-column table, like every other caution strip: docked directly, an
        // auto-sized label is only as wide as its text and the amber band stopped mid-window.
        var noticeRow = Gridline.Stack(_ephemeralNotice);
        noticeRow.Dock = DockStyle.Top;

        _rowHeight = new ImageList();
        _list = new JobList
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            VirtualMode = true,
            OwnerDraw = true,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            // The ListView's own header ignored owner drawing on Windows 11 and showed the
            // native white header; ColumnHeaderStrip draws the Gridline one instead.
            HeaderStyle = ColumnHeaderStyle.None,
            BackColor = Gridline.White,
            ForeColor = Gridline.Ink,
            Name = "Jobs",
            AccessibleName = "Jobs",
            SmallImageList = _rowHeight,
        };
        foreach (var (caption, width) in Columns)
        {
            _list.Columns.Add(caption.ToUpperInvariant(), width);
        }
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _header = new ColumnHeaderStrip(_list, Columns.Select(c => c.Caption).ToArray()) { Dock = DockStyle.Top };
        _list.HorizontalScrolled += (_, _) => _header.Invalidate();
        _list.Resize += (_, _) => FitColumns();
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += OnDrawSubItem;
        _list.SelectedIndexChanged += OnSelectionChanged;
        _list.ItemActivate += (_, _) => ForSelected(DefaultAction);

        _empty = Gridline.TextLabel(string.Empty, Gridline.Face.Sans, Gridline.SizeUi, Gridline.TextSecondary);
        _empty.AutoSize = false;
        _empty.Dock = DockStyle.Fill;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.BackColor = Gridline.White;
        _empty.Name = "EmptyList";
        _empty.Visible = false;

        var pane = new Gridline.Pane("Jobs") { Dock = DockStyle.Fill, Padding = Padding.Empty };
        pane.Controls.Add(_list);
        pane.Controls.Add(_empty);
        pane.Controls.Add(_header);

        _status = new Gridline.StatusBar();

        _menu = new Gridline.ContextMenu { ShowCheckMargin = false };
        _list.ContextMenuStrip = _menu;
        _menu.Opening += OnMenuOpening;

        // Docked controls lay out in reverse order of addition: the fill pane goes in first.
        Controls.Add(pane);
        Controls.Add(noticeRow);
        Controls.Add(toolbar);
        Controls.Add(_counts);
        Controls.Add(_status);
        EndBuild();

        _poll = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _poll.Tick += (_, _) => RefreshRows(force: false);
        UpdateButtons(null);
    }

    public JobManager Jobs { get; }

    public JobLogStore LogStore { get; }

    /// <summary>Set by the integration once the prompts exist; used to raise a waiting conflict dialog.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public UiPrompts? Prompts { get; set; }

    /// <summary>Show, restore and bring to front; optionally select a job, or show only jobs needing attention.</summary>
    public void ShowJobs(Guid? select = null, bool attentionOnly = false)
    {
        _syncingFilter = true;
        _attentionOnly.Checked = attentionOnly;
        _syncingFilter = false;
        if (select is { } id)
        {
            _selected = id;
        }
        if (!Visible)
        {
            Show();
        }
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        RefreshRows(force: true);
        if (select is { } wanted && IndexOf(wanted) < 0 && attentionOnly)
        {
            // The job asked for is not in the filtered view: show everything rather than hide it.
            _syncingFilter = true;
            _attentionOnly.Checked = false;
            _syncingFilter = false;
            RefreshRows(force: true);
        }
        Activate();
        BringToFront();
        _list.Focus();
    }

    protected override void ApplyDpi()
    {
        var height = Gridline.Scale(this, Gridline.RowHeight);
        _rowHeight.ImageSize = new Size(1, Math.Min(256, height));
        _list.SmallImageList = null;
        _list.SmallImageList = _rowHeight;
        Gridline.UseFont(_list, Gridline.Face.Sans, Gridline.SizeUi);
        _header.Height = height;
        FitColumns();
    }

    /// <summary>Every column at its logical width, and Status filling the rest of the list's width.</summary>
    private void FitColumns()
    {
        if (_list.Columns.Count != Columns.Length)
        {
            return;
        }
        var used = 0;
        for (var i = 0; i < Columns.Length - 1; i++)
        {
            _list.Columns[i].Width = Gridline.Scale(this, Columns[i].Width);
            used += _list.Columns[i].Width;
        }
        var status = Gridline.Scale(this, Columns[^1].Width);
        // ClientSize excludes a vertical scroll bar, so a long list does not push Status out.
        // A total exactly as wide as the list still made the ListView show a horizontal bar.
        _list.Columns[^1].Width = Math.Max(status, _list.ClientSize.Width - used - 2);
        _header.Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _poll.Enabled = Visible;
        if (Visible)
        {
            RefreshRows(force: true);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Single instance: the tray reopens it, so a user close only hides it.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _poll.Dispose();
        _rowHeight.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    private Gridline.Button ToolButton(string text, string automationId, Action onClick)
    {
        var button = new Gridline.Button(text, automationId) { Margin = new Padding(0, 0, Gridline.Space2, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void RefreshRows(bool force)
    {
        if (!Visible && !force)
        {
            return;
        }
        var all = Jobs.Snapshots();
        var mode = Jobs.CurrentSettings().Logging;
        // The mode is part of what is shown (notice, status bar), and it can change while no job does.
        if (!force && ReferenceEquals(all, _lastAll) && mode == _lastMode)
        {
            return;
        }
        _lastAll = all;
        _lastMode = mode;
        var rows = _attentionOnly.Checked ? all.Where(j => j.NeedsAttention).ToList() : all;

        _list.BeginUpdate();
        _rows = rows;
        if (_list.VirtualListSize != rows.Count)
        {
            _list.VirtualListSize = rows.Count;
        }
        RestoreSelection();
        _list.EndUpdate();
        _list.Invalidate();

        _counts.Counts = JobStateText.Counts(all);
        _status.Cells = JobStateText.StatusCells(all, mode);
        _ephemeralNotice.Visible = mode == LoggingMode.Ephemeral;
        _empty.Text = rows.Count > 0 ? string.Empty
            : _attentionOnly.Checked ? "Nothing needs attention. Clear \"Only jobs needing attention\" to see every job."
            : "No jobs yet. Right-click files, choose Robo-Copy or Robo-Cut, then Robo-Paste in a folder.";
        _empty.Visible = rows.Count == 0;
        _list.Visible = rows.Count > 0;
        UpdateButtons(Selected());
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Id == id)
            {
                return i;
            }
        }
        return -1;
    }

    private void RestoreSelection()
    {
        _restoringSelection = true;
        try
        {
            var index = _selected is { } id ? IndexOf(id) : -1;
            var current = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
            if (index != current)
            {
                _list.SelectedIndices.Clear();
                if (index >= 0)
                {
                    _list.SelectedIndices.Add(index);
                    _list.FocusedItem = _list.Items[index];
                    _list.EnsureVisible(index);
                }
            }
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private JobSnapshot? Selected() =>
        _list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _rows.Count ? _rows[_list.SelectedIndices[0]] : null;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_restoringSelection)
        {
            return;
        }
        var job = Selected();
        _selected = job?.Id;
        UpdateButtons(job);
        if (job is { State: JobState.AwaitingDecision })
        {
            Prompts?.Activate(job.Id);
        }
    }

    /// <summary>A pause latched before Running (<see cref="JobSnapshot.PauseRequested"/>); after that the state shows it.</summary>
    private static bool PauseLatched(JobSnapshot job) =>
        job.State is JobState.Queued or JobState.Scanning or JobState.AwaitingDecision && job.PauseRequested;

    private static bool CanControl(JobSnapshot job) =>
        !JobStates.IsTerminal(job.State) && !job.CancelRequested && job.State != JobState.Finalizing;

    private static bool HasSummary(JobSnapshot job) =>
        ProgressWindowPolicy.OnTerminal(job) == ProgressWindowAction.ShowSummary;

    private void UpdateButtons(JobSnapshot? job)
    {
        var latched = job is not null && PauseLatched(job);
        _pause.Enabled = job is not null && CanControl(job) && job.State != JobState.Paused && !latched;
        _resume.Enabled = job is not null && CanControl(job) && (job.State == JobState.Paused || latched);
        _cancel.Enabled = job is not null && CanControl(job);
        _errors.Enabled = job is not null && HasSummary(job);
        _errors.Text = job is { ErrorCount: > 0 } && job.State == JobState.DoneWithErrors
            ? string.Create(CultureInfo.InvariantCulture, $"Show errors ({job.ErrorCount:N0})")
            : "Show errors";
        _openDestination.Enabled = job is not null;
        _openLog.Enabled = job is { Logging: LoggingMode.Normal };
    }

    private void ForSelected(Action<JobSnapshot> action)
    {
        if (Selected() is { } job)
        {
            action(job);
            RefreshRows(force: true);
        }
    }

    private void PauseJob(JobSnapshot job) => Jobs.Pause(job.Id);

    private void ResumeJob(JobSnapshot job) => Jobs.Resume(job.Id);

    /// <summary>Enter or double-click: answer a waiting question, review an outcome, or open the destination.</summary>
    private void DefaultAction(JobSnapshot job)
    {
        if (job.State == JobState.AwaitingDecision)
        {
            Prompts?.Activate(job.Id);
        }
        else if (HasSummary(job))
        {
            ShowSummary(job);
        }
        else
        {
            OpenFolder(job.Destination);
        }
    }

    private void ShowSummary(JobSnapshot job)
    {
        if (_summaries.TryGetValue(job.Id, out var open) && !open.IsDisposed)
        {
            open.Activate();
            return;
        }
        var dialog = new ErrorSummaryDialog(job, Jobs.ErrorsOf(job.Id), Jobs);
        dialog.FormClosed += (_, _) =>
        {
            _summaries.Remove(job.Id);
            RefreshRows(force: true);
        };
        _summaries[job.Id] = dialog;
        dialog.Show(this);
    }

    private async void OpenLog(JobSnapshot job)
    {
        string? folder;
        try
        {
            folder = await Task.Run(() => LogStore.FolderOf(job.CreatedAt, job.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            folder = null;
        }
        if (IsDisposed)
        {
            return;
        }
        if (folder is null)
        {
            Gridline.Inform(this, "No log for this job",
                "This job's log folder isn't on disk. Older logs are removed once there are more than the number kept in Settings (log retention), and \"Delete all job logs\" removes them all.");
            return;
        }
        OpenFolder(folder);
    }

    /// <summary>Opens a folder in Explorer, after checking off the UI thread that it is there.</summary>
    private async void OpenFolder(string path)
    {
        bool exists;
        try
        {
            exists = await Task.Run(() => Directory.Exists(path));
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
            Gridline.Inform(this, "Folder not found",
                $"This folder doesn't exist or can't be reached right now:\n{path}\n\nIf it is on a network drive, check that the drive is connected.");
            return;
        }
        try
        {
            // Shell-execute the folder itself: Explorer opens it without parsing a command
            // line, so commas or spaces in the name cannot be misread.
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
        }
        catch (Win32Exception ex)
        {
            Gridline.Inform(this, "Couldn't open the folder", $"Windows could not open {path}: {ex.Message}");
        }
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        // The items are rebuilt for the selected job on every open; dispose the last set
        // rather than only clearing it, so each right-click does not leave components behind.
        foreach (var item in _menu.Items.Cast<ToolStripItem>().ToList())
        {
            item.Dispose();
        }
        _menu.Items.Clear();
        if (Selected() is not { } job)
        {
            e.Cancel = true;
            return;
        }
        var latched = PauseLatched(job);
        Add("Pause", CanControl(job) && job.State != JobState.Paused && !latched, () => PauseJob(job));
        Add("Resume", CanControl(job) && (job.State == JobState.Paused || latched), () => ResumeJob(job));
        Add("Cancel", CanControl(job), () => Jobs.Cancel(job.Id));
        _menu.Items.Add(new ToolStripSeparator());
        if (job.State == JobState.AwaitingDecision)
        {
            Add("Show the question", Prompts is not null, () => Prompts?.Activate(job.Id));
        }
        Add("Show errors", HasSummary(job), () => ShowSummary(job));
        Add("Open destination", true, () => OpenFolder(job.Destination));
        Add("Open log", job.Logging == LoggingMode.Normal, () => OpenLog(job));
        Gridline.UseFont(_menu, Gridline.Face.Sans, Gridline.SizeUi);

        void Add(string text, bool enabled, Action action)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled, Name = text.Replace(" ", string.Empty, StringComparison.Ordinal) };
            item.Click += (_, _) =>
            {
                action();
                RefreshRows(force: true);
            };
            _menu.Items.Add(item);
        }
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        // Real text for every column: screen readers and UI Automation read these, not the paint.
        var job = e.ItemIndex < _rows.Count ? _rows[e.ItemIndex] : null;
        var cells = job is null ? new string[Columns.Length] : CellTexts(job);
        var item = new ListViewItem(cells[0] ?? string.Empty);
        for (var i = 1; i < cells.Length; i++)
        {
            item.SubItems.Add(cells[i] ?? string.Empty);
        }
        e.Item = item;
    }

    private string[] CellTexts(JobSnapshot job)
    {
        var latched = PauseLatched(job);
        var running = job.State == JobState.Running && !job.CancelRequested;
        return
        [
            JobStateText.Label(job, latched),
            $"{JobStateText.VerbName(job.Verb)}: {DisplayText.SourcesSummary(job.Sources)}",
            job.Destination,
            string.Create(CultureInfo.InvariantCulture, $"{JobStateText.Percent(job)}%"),
            job.TotalBytes > 0 || job.DoneBytes > 0 ? $"{DisplayText.Bytes(job.DoneBytes)} / {DisplayText.Bytes(job.TotalBytes)}" : string.Empty,
            job.TotalFiles > 0 ? string.Create(CultureInfo.InvariantCulture, $"{job.DoneFiles:N0} / {job.TotalFiles:N0}") : string.Empty,
            running && job.BytesPerSecond is > 0 ? DisplayText.Speed(job.BytesPerSecond.Value) : string.Empty,
            running && job.Remaining is { } eta ? DisplayText.Duration(eta) : string.Empty,
            job.ErrorCount + job.RefusedCount > 0 ? (job.ErrorCount + job.RefusedCount).ToString("N0", CultureInfo.InvariantCulture) : string.Empty,
            JobStateText.For(job, Jobs.CurrentSettings().MaxConcurrentJobs, latched),
        ];
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Graphics is null || e.ItemIndex < 0 || e.ItemIndex >= _rows.Count)
        {
            return;
        }
        var job = _rows[e.ItemIndex];
        var selected = _list.SelectedIndices.Contains(e.ItemIndex);
        var g = e.Graphics;
        var r = e.Bounds;
        Gridline.DrawRow(g, r, selected);

        var column = (Column)e.ColumnIndex;
        var latched = PauseLatched(job);
        var tone = JobStateText.Tone(job, latched);
        var mono = Gridline.FontFor(_list, Gridline.Face.Mono, Gridline.SizeDense);
        var sans = Gridline.FontFor(_list, Gridline.Face.Sans, Gridline.SizeUi);
        var text = e.SubItem?.Text ?? string.Empty;
        switch (column)
        {
            case Column.State:
                Gridline.DrawCellText(g, text, Gridline.FontFor(_list, Gridline.Face.MonoSemiBold, Gridline.SizeDense), r,
                    Gridline.RowText(selected, Gridline.ToneColor(tone)));
                break;
            case Column.Job:
                Gridline.DrawCellText(g, text, sans, r, Gridline.RowText(selected, Gridline.Ink));
                break;
            case Column.Destination:
                Gridline.DrawCellText(g, text, mono, r, Gridline.RowText(selected, Gridline.Ink), path: true);
                break;
            case Column.Progress:
                var pad = Gridline.Scale(_list, 6);
                var barHeight = Gridline.Scale(_list, 12);
                var bar = new Rectangle(r.X + pad, r.Y + (r.Height - barHeight) / 2, Math.Max(4, r.Width - pad * 2), barHeight);
                Gridline.ProgressBar.DrawBar(g, bar, JobStateText.Percent(job), tone);
                break;
            case Column.Errors:
                Gridline.DrawCellText(g, text, mono, r, Gridline.RowText(selected, Gridline.Red), right: true);
                break;
            case Column.Status:
                Gridline.DrawCellText(g, text, sans, r, Gridline.RowText(selected, Gridline.TextSecondary));
                break;
            default:
                Gridline.DrawCellText(g, text, mono, r, Gridline.RowText(selected, Gridline.Ink), right: column is Column.Bytes or Column.Files or Column.Speed or Column.Eta);
                break;
        }
        if (column == Column.State && selected && _list.Focused)
        {
            Gridline.DrawFocus(g, new Rectangle(0, r.Y, _list.ClientSize.Width, r.Height));
        }
    }

    /// <summary>A ListView that paints without flicker while rows refresh four times a second, and says when it scrolls sideways.</summary>
    private sealed class JobList : ListView
    {
        private const int WmHScroll = 0x0114;
        private const int WmMouseHWheel = 0x020E;
        private const int SbHorz = 0;

        public JobList()
        {
            DoubleBuffered = true;
        }

        public event EventHandler? HorizontalScrolled;

        /// <summary>Pixels the rows are scrolled to the left (report view scrolls horizontally in pixels).</summary>
        public int HorizontalOffset => IsHandleCreated ? App.AppNative.GetScrollPos(Handle, SbHorz) : 0;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is WmHScroll or WmMouseHWheel)
            {
                HorizontalScrolled?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            // Keyboard navigation can scroll a column into view without a WM_HSCROLL.
            HorizontalScrolled?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The Gridline column header above the job list: gray cells with Plex Mono UPPERCASE
    /// captions and rule borders, at the list's column widths and horizontal scroll offset.
    /// </summary>
    private sealed class ColumnHeaderStrip : Control
    {
        private readonly JobList _list;
        private readonly string[] _captions;

        public ColumnHeaderStrip(JobList list, string[] captions)
        {
            _list = list;
            _captions = captions;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Gridline.Gray;
            AccessibleRole = AccessibleRole.ColumnHeader;
            AccessibleName = "Columns";
            AccessibleDescription = string.Join(", ", captions);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Gridline.Gray);
            var font = Gridline.FontFor(this, Gridline.Face.MonoSemiBold, Gridline.SizeDense);
            var x = -_list.HorizontalOffset;
            for (var i = 0; i < _captions.Length && i < _list.Columns.Count; i++)
            {
                var width = _list.Columns[i].Width;
                Gridline.DrawHeader(g, new Rectangle(x, 0, width, Height), _captions[i], font);
                x += width;
            }
            using var rule = new Pen(Gridline.Rule);
            g.DrawLine(rule, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>The counts block: white boxes with a rule frame, Plex Mono caption and a large tabular value.</summary>
    private sealed class CountsBlock : Control
    {
        private JobCounts _counts = new(0, 0, 0, 0);

        public CountsBlock()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Gridline.Gray;
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = "Counts";
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public JobCounts Counts
        {
            get => _counts;
            set
            {
                if (_counts != value)
                {
                    _counts = value;
                    AccessibleDescription = string.Create(CultureInfo.InvariantCulture,
                        $"{value.Active} active, {value.Paused} paused, {value.NeedAttention} need attention, {value.Finished} finished");
                    Invalidate();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Gridline.Gray);
            (string Caption, int Value, Color Color)[] boxes =
            [
                ("Active", _counts.Active, _counts.Active > 0 ? Gridline.Cyan : Gridline.Ink),
                ("Paused", _counts.Paused, _counts.Paused > 0 ? Gridline.Amber : Gridline.Ink),
                ("Need attention", _counts.NeedAttention, _counts.NeedAttention > 0 ? Gridline.Red : Gridline.Ink),
                ("Finished", _counts.Finished, Gridline.Ink),
            ];
            var gap = Gridline.Scale(this, Gridline.Space2);
            var bottom = Gridline.Scale(this, Gridline.Space2);
            var width = (Width - gap * (boxes.Length - 1)) / boxes.Length;
            var captionFont = Gridline.FontFor(this, Gridline.Face.MonoSemiBold, Gridline.SizeDense);
            var valueFont = Gridline.FontFor(this, Gridline.Face.MonoMedium, Gridline.SizeMeasure);
            var pad = Gridline.Scale(this, Gridline.Space3);
            using var white = new SolidBrush(Gridline.White);
            using var rule = new Pen(Gridline.Rule);
            for (var i = 0; i < boxes.Length; i++)
            {
                var box = new Rectangle(i * (width + gap), 0, width, Height - bottom);
                g.FillRectangle(white, box);
                g.DrawRectangle(rule, box.X, box.Y, box.Width - 1, box.Height - 1);
                var captionHeight = captionFont.Height + Gridline.Scale(this, 6);
                TextRenderer.DrawText(g, boxes[i].Caption.ToUpperInvariant(), captionFont,
                    new Rectangle(box.X + pad, box.Y + Gridline.Scale(this, 4), box.Width - pad * 2, captionHeight), Gridline.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, boxes[i].Value.ToString("N0", CultureInfo.InvariantCulture), valueFont,
                    new Rectangle(box.X + pad, box.Y + captionHeight, box.Width - pad * 2, box.Height - captionHeight), boxes[i].Color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }
}
