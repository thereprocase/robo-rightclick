using System.Globalization;
using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

internal enum ErrorSummaryChoice
{
    /// <summary>Dialog closed without a choice: the job keeps its attention state.</summary>
    None,

    /// <summary>DoneWithErrors: JobManager.Retry. Failed: JobManager.Rerun. Damaged cancel: Retry of the damaged files ("Finish replacing them").</summary>
    TryAgain,
    Skip,
}

/// <summary>
/// The end-of-job counterpart of Explorer's per-file error prompt (the one deliberate
/// deviation, docs/parity.md). Sections, each shown only when non-empty: the failure reason
/// (<see cref="JobSnapshot.FailureReason"/>); retryable errors (path, Windows message,
/// code; the first <see cref="JobRecords.MaxRecordedErrors"/>, then "and N more, see the
/// log"); refused items with their reason (not retryable); files damaged by a cancel; files
/// skipped because their name appeared during the copy. Buttons: "Try again (N)" where N
/// counts only retryable items (absent when N is 0, except for a Failed job, where it
/// re-runs the whole paste) and "Skip". Shown from the Jobs window, the progress window or
/// a toast click, never by the job itself, so a failed job never blocks anything.
/// </summary>
/// <remarks>
/// The content lives in <see cref="ErrorSummaryView"/> so the progress window can turn into
/// the same summary in place. With a <see cref="JobManager"/> the view acts on the choice
/// itself (Retry, Rerun or Acknowledge; opening and closing does not acknowledge, see
/// <see cref="JobSnapshot.Acknowledged"/>) and lists refused items, damaged files and late
/// arrivals per item (<see cref="JobManager.IssuesOf"/>, <see cref="JobManager.DamagedOf"/>,
/// <see cref="JobManager.SkippedAppearedOf"/>; the first
/// <see cref="JobRecords.MaxRecordedErrors"/> of each, with the snapshot's exact counts).
/// Without one it shows those sections as counts. <see cref="Choice"/> reports what was picked.
/// </remarks>
internal sealed class ErrorSummaryDialog : Gridline.Window
{
    public ErrorSummaryDialog(JobSnapshot job, IReadOnlyList<ErrorReported> errors)
        : this(job, errors, jobs: null)
    {
    }

    public ErrorSummaryDialog(JobSnapshot job, IReadOnlyList<ErrorReported> errors, JobManager? jobs)
    {
        Job = job;
        Errors = errors;

        BeginBuild();
        Text = JobStateText.Title(job);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 440);
        MinimumSize = new Size(480, 320);
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = true;
        Padding = new Padding(Gridline.Space3);

        var view = new ErrorSummaryView(job, errors, jobs) { Dock = DockStyle.Fill };
        view.Chosen += (_, choice) =>
        {
            Choice = choice;
            Close();
        };
        Controls.Add(view);
        AcceptButton = view.DefaultButton;
        EndBuild();
    }

    /// <summary>Escape closes without a choice, so the job keeps its attention state rather than being skipped by a stray key.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    public JobSnapshot Job { get; }

    public IReadOnlyList<ErrorReported> Errors { get; }

    public ErrorSummaryChoice Choice { get; private set; }
}

/// <summary>The summary's sections and its Try again / Skip buttons, shared by the dialog and the progress window.</summary>
internal sealed class ErrorSummaryView : TableLayoutPanel
{
    private readonly JobSnapshot _job;
    private readonly JobManager? _jobs;

    public ErrorSummaryView(JobSnapshot job, IReadOnlyList<ErrorReported> errors, JobManager? jobs)
    {
        _job = job;
        _jobs = jobs;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        BackColor = Gridline.Transparent;
        Margin = Padding.Empty;
        Padding = Padding.Empty;

        var done = job.Verb == TransferVerb.Move ? "moved" : "copied";
        var into = WinPath.GetFileName(job.Destination) is { Length: > 0 } name ? name : job.Destination;

        AddAuto(Gridline.Caption(JobStateText.Label(job), Gridline.ToneColor(JobStateText.Tone(job))));
        AddAuto(Gridline.TextLabel(Heading(job, done, into), Gridline.Face.SansSemiBold, Gridline.SizeHeading));

        if (job.FailureReason is { Length: > 0 } reason)
        {
            AddAuto(new Gridline.CautionStrip(reason, "FailureReason", danger: true));
        }

        var filled = false;
        if (errors.Count > 0 || job.ErrorCount > 0)
        {
            var total = Math.Max(job.ErrorCount, errors.Count);
            var title = string.Create(CultureInfo.InvariantCulture, $"Could not be {done} ({total:N0})");
            var rows = errors.Select(e => new DetailRow(e.Path, string.Create(CultureInfo.InvariantCulture, $"{e.Message.Trim()} (error {e.Code})")));
            AddFill(ListPane(title, text: null, rows, "Errors"));
            filled = true;

            var unlisted = total - errors.Count;
            if (unlisted > 0)
            {
                var more = job.Logging == LoggingMode.Ephemeral
                    ? string.Create(CultureInfo.InvariantCulture, $"and {unlisted:N0} more. Ephemeral mode keeps no log, so only the first {errors.Count:N0} are listed.")
                    : string.Create(CultureInfo.InvariantCulture, $"and {unlisted:N0} more, see the log (Jobs window, Open log).");
                AddAuto(Gridline.TextLabel(more, Gridline.Face.Mono, Gridline.SizeDense, Gridline.TextSecondary));
            }
        }

        var refused = jobs?.IssuesOf(job.Id) ?? [];
        if (job.RefusedCount > 0)
        {
            var title = string.Create(CultureInfo.InvariantCulture, $"Refused ({job.RefusedCount:N0})");
            if (refused.Count > 0)
            {
                var text = $"Not {done}. Trying again would not change this.";
                AddFill(ListPane(title, text, refused.Select(r => new DetailRow(r.Path, r.Reason)), "Refused"));
                filled = true;
            }
            else
            {
                var text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DisplayText.Items(job.RefusedCount)} {(job.RefusedCount == 1 ? "was" : "were")} not {done}. {job.RefusalReason} Trying again would not change this.");
                AddAuto(Section(title, text));
            }
        }

        if (job.DamagedOnCancel > 0)
        {
            var files = job.DamagedOnCancel == 1 ? "1 file was" : string.Create(CultureInfo.InvariantCulture, $"{job.DamagedOnCancel:N0} files were");
            var title = string.Create(CultureInfo.InvariantCulture, $"May be incomplete ({job.DamagedOnCancel:N0})");
            var text = $"{files} being replaced when you canceled. They can look complete but hold only part of the new data. Finish replacing them, or check them before you use them.";
            var damaged = jobs?.DamagedOf(job.Id) ?? [];
            if (damaged.Count > 0)
            {
                AddFill(ListPane(title, text, damaged.Select(p => new DetailRow(p, "May hold partial data")), "Damaged"));
                filled = true;
            }
            else
            {
                AddAuto(Section(title, text));
            }
        }

        if (job.SkippedAppeared > 0)
        {
            var title = string.Create(CultureInfo.InvariantCulture, $"Skipped ({job.SkippedAppeared:N0})");
            var text = job.SkippedAppeared == 1
                ? "A file with this name appeared at the destination during the paste, so it was left alone. Check which version you want."
                : "Files with these names appeared at the destination during the paste, so they were left alone. Check which versions you want.";
            var skipped = jobs?.SkippedAppearedOf(job.Id) ?? [];
            if (skipped.Count > 0)
            {
                AddFill(ListPane(title, text, skipped.Select(p => new DetailRow(p, "Appeared during the paste")), "SkippedAppeared"));
                filled = true;
            }
            else
            {
                AddAuto(Section(title, text));
            }
        }

        if (!filled)
        {
            // Absorbs the spare height so the buttons sit at the bottom.
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(new Panel { BackColor = Gridline.Transparent, Dock = DockStyle.Fill, Margin = Padding.Empty });
        }

        var tryAgain = TryAgainText(job);
        var skip = new Gridline.Button(tryAgain is null ? "OK" : "Skip", "SkipErrors");
        skip.Click += (_, _) => Choose(ErrorSummaryChoice.Skip);
        if (tryAgain is not null)
        {
            var retry = new Gridline.Button(tryAgain, "TryAgain");
            retry.Click += (_, _) => Choose(ErrorSummaryChoice.TryAgain);
            DefaultButton = retry;
            AddAuto(Gridline.ButtonRow(retry, skip));
        }
        else
        {
            DefaultButton = skip;
            AddAuto(Gridline.ButtonRow(skip));
        }
    }

    public event EventHandler<ErrorSummaryChoice>? Chosen;

    /// <summary>The primary action: Try again when there is something to try, else the close button.</summary>
    public IButtonControl DefaultButton { get; }

    /// <summary>"Try again (N)", "Try again" for a failed paste, "Finish replacing them" after a damaging cancel, or null when nothing can be retried.</summary>
    private static string? TryAgainText(JobSnapshot job) => job.State switch
    {
        JobState.Failed => "Try again",
        JobState.Canceled when job.DamagedOnCancel > 0 => "Finish replacing them",
        _ when job.ErrorCount > 0 => string.Create(CultureInfo.InvariantCulture, $"Try again ({job.ErrorCount:N0})"),
        _ => null,
    };

    private static string Heading(JobSnapshot job, string done, string into) => job.State switch
    {
        JobState.Failed => $"Nothing was {done} to {into}.",
        JobState.Canceled => $"The paste into {into} was canceled.",
        JobState.Done => job.SkippedAppeared == 1
            ? $"Everything was {done} to {into} except 1 file whose name appeared there during the paste."
            : string.Create(CultureInfo.InvariantCulture, $"Everything was {done} to {into} except {job.SkippedAppeared:N0} files whose names appeared there during the paste."),
        _ => (job.ErrorCount + job.RefusedCount) switch
        {
            1 => $"1 item could not be {done} to {into}. Everything else finished.",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n:N0} items could not be {done} to {into}. Everything else finished."),
        },
    };

    private void Choose(ErrorSummaryChoice choice)
    {
        if (_jobs is not null)
        {
            if (choice == ErrorSummaryChoice.TryAgain)
            {
                var started = _job.State == JobState.Failed ? _jobs.Rerun(_job.Id) : _jobs.Retry(_job.Id);
                if (started is null)
                {
                    Gridline.Inform(FindForm(), "Nothing to try again",
                        "There was nothing left to try again: the files may have been handled already, or this job is no longer in the list.");
                }
            }
            else if (choice == ErrorSummaryChoice.Skip)
            {
                _jobs.Acknowledge(_job.Id);
            }
        }
        Chosen?.Invoke(this, choice);
    }

    private void AddAuto(Control control)
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(control);
    }

    /// <summary>A row that shares the spare height with the other lists.</summary>
    private void AddFill(Control control)
    {
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(control);
    }

    /// <summary>A titled pane with an optional explanation above a list of paths and notes.</summary>
    private static Gridline.Pane ListPane(string title, string? text, IEnumerable<DetailRow> rows, string automationId)
    {
        var pane = new Gridline.Pane(title) { Dock = DockStyle.Fill, Padding = Padding.Empty };
        var list = new DetailList(rows, automationId) { Dock = DockStyle.Fill };
        if (text is null)
        {
            pane.Controls.Add(list);
            return pane;
        }
        var body = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            BackColor = Gridline.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = Gridline.TextLabel(text);
        label.Margin = new Padding(Gridline.Space2, Gridline.Space1, Gridline.Space2, Gridline.Space1);
        body.Controls.Add(label);
        body.Controls.Add(list);
        pane.Controls.Add(body);
        return pane;
    }

    /// <summary>One line of a summary list: the path in Plex Mono, then a short note.</summary>
    private sealed record DetailRow(string Path, string Note);

    private static Gridline.Pane Section(string title, string text)
    {
        var pane = new Gridline.Pane(title) { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        pane.Controls.Add(Gridline.Stack(Gridline.TextLabel(text)));
        return pane;
    }

    /// <summary>Ruled 26 px rows: the path in Plex Mono, then the Windows message and code or another short note.</summary>
    private sealed class DetailList : ListBox
    {
        public DetailList(IEnumerable<DetailRow> rows, string automationId)
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            BackColor = Gridline.White;
            Name = automationId;
            AccessibleName = automationId;
            ItemHeight = Gridline.Scale(this, Gridline.RowHeight);
            BeginUpdate();
            foreach (var row in rows)
            {
                Items.Add(row);
            }
            EndUpdate();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ItemHeight = Gridline.Scale(this, Gridline.RowHeight);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not DetailRow row)
            {
                return;
            }
            var selected = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            var r = e.Bounds;
            Gridline.DrawRow(g, r, selected);
            var pathWidth = (int)(r.Width * 0.55);
            Gridline.DrawCellText(g, row.Path, Gridline.FontFor(this, Gridline.Face.Mono, Gridline.SizeDense),
                new Rectangle(r.X, r.Y, pathWidth, r.Height), Gridline.RowText(selected, Gridline.Ink), path: true);
            Gridline.DrawCellText(g, row.Note, Gridline.FontFor(this, Gridline.Face.Sans, Gridline.SizeDense),
                new Rectangle(r.X + pathWidth, r.Y, r.Width - pathWidth, r.Height), Gridline.RowText(selected, Gridline.TextSecondary));
            if ((e.State & DrawItemState.Focus) != 0 && Focused)
            {
                Gridline.DrawFocus(g, r);
            }
        }
    }
}
