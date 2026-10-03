using System.Globalization;
using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>
/// Explorer's "Replace or Skip Files" dialog: "The destination has N files with the same
/// names" with Replace the files / Skip these files / Let me decide for each file. The
/// third choice expands to a per-file list with a source and a destination checkbox per
/// row, sizes and modified dates (<see cref="FileConflict"/>), and "Select all" for each
/// side. Both ticked = keep both where <see cref="FileConflict.KeepBothAllowed"/>, source
/// only = replace, destination only or neither = skip. Where keep-both is not allowed the
/// row says why ("Keeping both isn't available when moving between drives"). Closing or
/// Cancel = cancel the job. Title names the job ("Robo-Paste: 3 items → Archive"; in
/// ephemeral mode the dialog shows file names, which never leave memory, but its title is
/// generic). Owned by the job's <see cref="ProgressWindow"/> when there is one; otherwise
/// shown with Activate after AllowSetForegroundWindow from the COM call. Built in code (no
/// designer files); modeless; the result is read after FormClosed.
/// </summary>
/// <remarks>
/// On a row where keep-both is not allowed the two boxes are exclusive: ticking one clears
/// the other, so the list can never express a choice the engine would have to reinterpret
/// (<see cref="ConflictSelection.Decision(bool, bool, bool)"/>). The list is a virtual-mode
/// grid, so tens of thousands of conflicts cost one array of ticks, not one row object each.
/// Focus starts on "Skip these files": Enter on the dialog never overwrites anything. When some
/// files are ones the earlier paste may have left half written
/// (<see cref="FileConflict.SuspectedPartial"/>, only in a "Try again" or "Finish copying them"
/// job), an amber strip above the choices says so (<see cref="ConflictSelection.SuspectedNotice"/>),
/// their rows carry an amber note, and focus starts on "Let me decide" instead: Enter then
/// neither overwrites nor silently keeps a file that may be incomplete.
/// </remarks>
internal sealed class ConflictDialog : Gridline.Window
{
    private const int ColSource = 0;
    private const int ColName = 1;
    private const int ColFolder = 2;
    private const int ColSourceSize = 3;
    private const int ColSourceDate = 4;
    private const int ColDestination = 5;
    private const int ColDestinationSize = 6;
    private const int ColDestinationDate = 7;
    private const int ColNote = 8;

    // Logical widths at 96 dpi; the name and note columns fill what is left. The check columns
    // are as wide as their captions, so SOURCE and DESTINATION are never cut to "S..".
    private static readonly int[] ColumnWidths = [76, 0, 180, 90, 150, 108, 90, 150, 0];

    private readonly bool[] _source;
    private readonly bool[] _destination;
    private readonly Panel _choiceView;
    private readonly Panel _decideView;
    private readonly DataGridView _grid;
    private readonly Gridline.CheckBox _allSource;
    private readonly Gridline.CheckBox _allDestination;
    private readonly Label _summary;
    private readonly Gridline.Button _continue;
    private readonly Gridline.Button _skip;
    private readonly Gridline.Button _start;
    private bool _syncingSelectAll;

    public ConflictDialog(JobSnapshot job, IReadOnlyList<FileConflict> conflicts)
    {
        Job = job;
        Conflicts = conflicts;
        _source = new bool[conflicts.Count];
        _destination = new bool[conflicts.Count];

        BeginBuild();
        Text = JobStateText.Title(job);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 400);
        MinimumSize = new Size(480, 360);
        MinimizeBox = false;
        ShowInTaskbar = true;
        Padding = new Padding(Gridline.Space3);

        var count = conflicts.Count;
        var heading = count == 1
            ? $"The destination already has a file named \"{WinPath.GetFileName(conflicts[0].DestinationPath)}\""
            : string.Create(CultureInfo.InvariantCulture, $"The destination has {count:N0} files with the same names");
        var verb = job.Verb == TransferVerb.Move ? "Moving" : "Copying";
        var into = WinPath.GetFileName(job.Destination) is { Length: > 0 } name ? name : job.Destination;
        var context = $"{verb} {DisplayText.Items(job.Sources.Count)} to {into}";
        var words = ConflictSelection.ChoiceText(count);

        // The three big choices.
        var replace = BigChoice(words.Replace, "Replace", words.ReplaceNote);
        replace.Click += (_, _) => Finish(new ConflictChoice.ReplaceAll());
        _skip = BigChoice(words.Skip, "Skip", words.SkipNote);
        _skip.Click += (_, _) => Finish(new ConflictChoice.SkipAll());
        var decide = BigChoice(words.Decide, "Decide", words.DecideNote);
        decide.Click += (_, _) => ShowDecideView();

        var choicePane = new Gridline.Pane(count == 1 ? "Replace or skip the file" : "Replace or skip files") { Dock = DockStyle.Fill };
        var suspectedNotice = ConflictSelection.SuspectedNotice(conflicts);
        var choiceRows = new List<Control>
        {
            Gridline.TextLabel(heading, Gridline.Face.SansSemiBold, Gridline.SizeHeading),
            Gridline.TextLabel(context, Gridline.Face.Sans, Gridline.SizeUi, Gridline.TextSecondary),
        };
        if (suspectedNotice is not null)
        {
            choiceRows.Add(new Gridline.CautionStrip(suspectedNotice, "SuspectedPartial"));
        }
        choiceRows.AddRange([replace, _skip, decide]);
        var choices = Gridline.Stack([.. choiceRows]);
        // Top, not Fill: a filled table hands its spare height to the last row, which made the
        // third choice taller than the other two.
        choices.Dock = DockStyle.Top;
        choicePane.Controls.Add(choices);
        _choiceView = new Panel { Dock = DockStyle.Fill, BackColor = Gridline.Transparent };
        _choiceView.Controls.Add(choicePane);

        // The per-file list.
        _allSource = new Gridline.CheckBox("All files from the source", "SelectAllSource");
        _allSource.CheckedChanged += (_, _) => SelectAll(source: true, _allSource.Checked);
        _allDestination = new Gridline.CheckBox("All files in the destination", "SelectAllDestination");
        _allDestination.CheckedChanged += (_, _) => SelectAll(source: false, _allDestination.Checked);
        var selectAll = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = true,
            BackColor = Gridline.Transparent,
            Margin = Padding.Empty,
        };
        selectAll.Controls.Add(_allSource);
        selectAll.Controls.Add(_allDestination);

        _grid = BuildGrid(count);
        var gridPane = new Gridline.Pane(ConflictSelection.ListTitle(count)) { Dock = DockStyle.Fill, Padding = Padding.Empty };
        gridPane.Controls.Add(_grid);

        _summary = Gridline.TextLabel(string.Empty, Gridline.Face.Mono, Gridline.SizeUi);
        _summary.Name = "DecisionSummary";
        _summary.AccessibleName = "DecisionSummary";
        _continue = new Gridline.Button("Continue", "Continue");
        _continue.Click += (_, _) => Finish(ConflictSelection.Build(Rows()));
        var back = new Gridline.Button("Back", "Back");
        back.Click += (_, _) => ShowChoiceView();

        var decideLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Gridline.Transparent };
        decideLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        decideLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        decideLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        decideLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        decideLayout.Controls.Add(selectAll);
        decideLayout.Controls.Add(gridPane);
        decideLayout.Controls.Add(_summary);
        _decideView = new Panel { Dock = DockStyle.Fill, BackColor = Gridline.Transparent, Visible = false };
        _decideView.Controls.Add(decideLayout);

        var cancel = new Gridline.Button("Cancel", "Cancel");
        cancel.Click += (_, _) => Close();
        var footer = Gridline.ButtonRow(back, _continue, cancel);
        footer.Dock = DockStyle.Bottom;
        back.Visible = false;
        _continue.Visible = false;
        _decideView.VisibleChanged += (_, _) =>
        {
            back.Visible = _decideView.Visible;
            _continue.Visible = _decideView.Visible;
        };

        Controls.Add(_decideView);
        Controls.Add(_choiceView);
        Controls.Add(footer);
        CancelButton = cancel;
        // Skip keeps a file that may be incomplete; with such a file in the list, the safe
        // place for Enter is the per-file view, where each one is shown and marked.
        _start = suspectedNotice is null ? _skip : decide;
        ActiveControl = _start;
        UpdateSummary();
        EndBuild();
    }

    public JobSnapshot Job { get; }

    public IReadOnlyList<FileConflict> Conflicts { get; }

    /// <summary>Null until the user picks; stays null on cancel.</summary>
    public ConflictChoice? Choice { get; private set; }

    protected override void ApplyDpi()
    {
        var rowHeight = Gridline.Scale(this, Gridline.RowHeight);
        _grid.ColumnHeadersHeight = rowHeight;
        _grid.ColumnHeadersDefaultCellStyle.Font = Gridline.FontFor(this, Gridline.Face.MonoSemiBold, Gridline.SizeDense);
        _grid.DefaultCellStyle.Font = Gridline.FontFor(this, Gridline.Face.Sans, Gridline.SizeUi);
        foreach (var column in new[] { ColFolder, ColSourceSize, ColSourceDate, ColDestinationSize, ColDestinationDate })
        {
            _grid.Columns[column].DefaultCellStyle.Font = Gridline.FontFor(this, Gridline.Face.Mono, Gridline.SizeDense);
        }
        for (var i = 0; i < ColumnWidths.Length; i++)
        {
            if (ColumnWidths[i] > 0)
            {
                _grid.Columns[i].Width = Gridline.Scale(this, ColumnWidths[i]);
            }
        }
        // A narrow window scrolls sideways rather than squeezing the file name to nothing.
        _grid.Columns[ColName].MinimumWidth = Gridline.Scale(this, 120);
        _grid.Columns[ColNote].MinimumWidth = Gridline.Scale(this, 160);
        // Row height comes from the template; existing virtual rows are rebuilt to pick it up.
        _grid.RowTemplate.Height = rowHeight;
        var count = _grid.RowCount;
        _grid.RowCount = 0;
        _grid.RowCount = count;
    }

    private Gridline.Button BigChoice(string text, string automationId, string note)
    {
        var button = new Gridline.Button(text, automationId)
        {
            Note = note,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 52,
            Margin = new Padding(0, Gridline.Space2, 0, 0),
        };
        return button;
    }

    private DataGridView BuildGrid(int count)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            VirtualMode = true,
            Name = "Conflicts",
            AccessibleName = "Conflicts",
            BackgroundColor = Gridline.White,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Gridline.RuleLight,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EditMode = DataGridViewEditMode.EditOnEnter,
            StandardTab = true,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Gridline.Gray;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Gridline.Ink;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Gridline.Gray;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Gridline.Ink;
        grid.DefaultCellStyle.BackColor = Gridline.White;
        grid.DefaultCellStyle.ForeColor = Gridline.Ink;
        grid.DefaultCellStyle.SelectionBackColor = Gridline.Blue;
        grid.DefaultCellStyle.SelectionForeColor = Gridline.White;

        grid.Columns.Add(CheckColumn("Source"));
        grid.Columns.Add(TextColumn("Name", fill: 50));
        grid.Columns.Add(TextColumn("In folder"));
        grid.Columns.Add(TextColumn("Size", right: true));
        grid.Columns.Add(TextColumn("Modified"));
        grid.Columns.Add(CheckColumn("Destination"));
        grid.Columns.Add(TextColumn("Size", right: true));
        grid.Columns.Add(TextColumn("Modified"));
        grid.Columns.Add(TextColumn("Note", fill: 50));

        grid.CellValueNeeded += OnCellValueNeeded;
        grid.CellValuePushed += OnCellValuePushed;
        grid.CellPainting += OnCellPainting;
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit a tick at once, so the summary and the exclusive-box rule follow the click.
            if (grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        grid.RowCount = count;
        return grid;
    }

    private static DataGridViewCheckBoxColumn CheckColumn(string header) => new()
    {
        HeaderText = header.ToUpperInvariant(),
        Name = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        Resizable = DataGridViewTriState.False,
        AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
    };

    private static DataGridViewTextBoxColumn TextColumn(string header, int fill = 0, bool right = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            HeaderText = header.ToUpperInvariant(),
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = fill > 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
        };
        if (fill > 0)
        {
            column.FillWeight = fill;
        }
        if (right)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        return column;
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= Conflicts.Count)
        {
            return;
        }
        var c = Conflicts[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            ColSource => _source[e.RowIndex],
            ColName => WinPath.GetFileName(c.DestinationPath),
            ColFolder => WinPath.GetParent(c.DestinationPath),
            ColSourceSize => DisplayText.Bytes(c.Source.Size),
            ColSourceDate => DateText(c.Source.LastWriteUtc),
            ColDestination => _destination[e.RowIndex],
            ColDestinationSize => DisplayText.Bytes(c.Existing.Size),
            ColDestinationDate => DateText(c.Existing.LastWriteUtc),
            ColNote => ConflictSelection.Note(c),
            _ => null,
        };
    }

    // Which side is newer or larger is in the note column, so these stay short enough to fit.
    private static string DateText(DateTimeOffset utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private void OnCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= Conflicts.Count || e.Value is not bool ticked)
        {
            return;
        }
        if (e.ColumnIndex == ColSource)
        {
            SetTick(e.RowIndex, source: true, ticked);
        }
        else if (e.ColumnIndex == ColDestination)
        {
            SetTick(e.RowIndex, source: false, ticked);
        }
        _grid.InvalidateRow(e.RowIndex);
        UpdateSummary();
    }

    private void SetTick(int row, bool source, bool ticked)
    {
        (source ? _source : _destination)[row] = ticked;
        if (ticked && !Conflicts[row].KeepBothAllowed)
        {
            (source ? _destination : _source)[row] = false;
        }
    }

    private void SelectAll(bool source, bool ticked)
    {
        if (_syncingSelectAll)
        {
            return;
        }
        _grid.EndEdit();
        for (var i = 0; i < Conflicts.Count; i++)
        {
            SetTick(i, source, ticked);
        }
        _grid.Invalidate();
        UpdateSummary();
    }

    private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.Graphics is null)
        {
            return;
        }
        if (e.RowIndex < 0)
        {
            // Header row in Gridline style.
            if (e.ColumnIndex >= 0)
            {
                Gridline.DrawHeader(e.Graphics, e.CellBounds, _grid.Columns[e.ColumnIndex].HeaderText,
                    Gridline.FontFor(this, Gridline.Face.MonoSemiBold, Gridline.SizeDense));
                e.Handled = true;
            }
            return;
        }
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        if (e.ColumnIndex is ColSource or ColDestination)
        {
            Gridline.DrawRow(e.Graphics, e.CellBounds, selected);
            var size = Gridline.Scale(this, 13);
            var box = new Rectangle(
                e.CellBounds.X + (e.CellBounds.Width - size) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - size) / 2,
                size,
                size);
            var ticked = (e.ColumnIndex == ColSource ? _source : _destination)[e.RowIndex];
            Gridline.DrawCheckBox(e.Graphics, box, ticked, enabled: true);
            e.Handled = true;
        }
        else if (e.ColumnIndex == ColFolder)
        {
            // Cut in the middle, so the end of the path, where folders differ, stays visible.
            Gridline.DrawRow(e.Graphics, e.CellBounds, selected);
            Gridline.DrawCellText(e.Graphics, WinPath.GetParent(Conflicts[e.RowIndex].DestinationPath),
                Gridline.FontFor(this, Gridline.Face.Mono, Gridline.SizeDense), e.CellBounds, Gridline.RowText(selected, Gridline.Ink), path: true);
            e.Handled = true;
        }
        else if (e.ColumnIndex == ColNote)
        {
            // Amber where keep-both is not offered, or where the earlier paste may have left the
            // file half written: that row has a qualification to read.
            Gridline.DrawRow(e.Graphics, e.CellBounds, selected);
            var conflict = Conflicts[e.RowIndex];
            var color = conflict.KeepBothAllowed && !conflict.SuspectedPartial ? Gridline.TextSecondary : Gridline.Amber;
            Gridline.DrawCellText(e.Graphics, ConflictSelection.Note(Conflicts[e.RowIndex]),
                Gridline.FontFor(this, Gridline.Face.Sans, Gridline.SizeDense), e.CellBounds, Gridline.RowText(selected, color));
            e.Handled = true;
        }
    }

    private IEnumerable<ConflictRow> Rows() =>
        Conflicts.Select((c, i) => new ConflictRow(c, _source[i], _destination[i]));

    private void UpdateSummary()
    {
        _summary.Text = "Continue: " + ConflictSelection.Summary(Rows().Select(ConflictSelection.Decision));
        _syncingSelectAll = true;
        _allSource.Checked = _source.Length > 0 && _source.All(t => t);
        _allDestination.Checked = _destination.Length > 0 && _destination.All(t => t);
        _syncingSelectAll = false;
    }

    private void ShowDecideView()
    {
        _choiceView.Visible = false;
        _decideView.Visible = true;
        Gridline.RescaleFonts(this, DeviceDpi);
        AcceptButton = _continue;
        var wanted = new Size(Gridline.Scale(this, 1180), Gridline.Scale(this, 560));
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Math.Max(Width, wanted.Width), area.Width), Math.Min(Math.Max(Height, wanted.Height), area.Height));
        if (!area.Contains(Bounds))
        {
            Location = new Point(
                Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
        _grid.Focus();
    }

    private void ShowChoiceView()
    {
        _grid.EndEdit();
        _decideView.Visible = false;
        _choiceView.Visible = true;
        Gridline.RescaleFonts(this, DeviceDpi);
        AcceptButton = null;
        _start.Focus();
    }

    private void Finish(ConflictChoice choice)
    {
        _grid.EndEdit();
        Choice = choice;
        Close();
    }
}
