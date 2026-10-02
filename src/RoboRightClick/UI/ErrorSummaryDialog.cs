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
/// itself (Retry, Rerun or Acknowledge); <see cref="Choice"/> reports what was picked.
/// Per-item refusals, the files a cancel damaged and files skipped because their name
/// appeared mid-copy are shown as counts where the snapshot carries them; the snapshot does
/// not list them per item.
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
            var pane = new Gridline.Pane(title) { Dock = DockStyle.Fill, Padding = Padding.Empty };
            var list = new ErrorList(errors) { Dock = DockStyle.Fill };
            pane.Controls.Add(list);
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(pane);
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

        if (job.RefusedCount > 0)
        {
            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"{DisplayText.Items(job.RefusedCount)} {(job.RefusedCount == 1 ? "was" : "were")} not {done}. {job.RefusalReason} Trying again would not change this.");
            AddAuto(Section($"Refused ({job.RefusedCount:N0})", text));
        }

        if (job.DamagedOnCancel > 0)
        {
            var files = job.DamagedOnCancel == 1 ? "1 file was" : string.Create(CultureInfo.InvariantCulture, $"{job.DamagedOnCancel:N0} files were");
            AddAuto(Section(
                $"May be incomplete ({job.DamagedOnCancel:N0})",
                $"{files} being replaced when you canceled. They can look complete but hold only part of the new data. Finish replacing them, or check them before you use them."));
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

    private static Gridline.Pane Section(string title, string text)
    {
        var pane = new Gridline.Pane(title) { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        pane.Controls.Add(Gridline.Stack(Gridline.TextLabel(text)));
        return pane;
    }

    /// <summary>Ruled 26 px rows: the path in Plex Mono, then the Windows message and code.</summary>
    private sealed class ErrorList : ListBox
    {
        public ErrorList(IReadOnlyList<ErrorReported> errors)
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            BackColor = Gridline.White;
            Name = "Errors";
            AccessibleName = "Errors";
            ItemHeight = Gridline.Scale(this, Gridline.RowHeight);
            BeginUpdate();
            foreach (var error in errors)
            {
                Items.Add(error);
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
            if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not ErrorReported error)
            {
                return;
            }
            var selected = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            var r = e.Bounds;
            Gridline.DrawRow(g, r, selected);
            var pathWidth = (int)(r.Width * 0.55);
            Gridline.DrawCellText(g, error.Path, Gridline.FontFor(this, Gridline.Face.Mono, Gridline.SizeDense),
                new Rectangle(r.X, r.Y, pathWidth, r.Height), Gridline.RowText(selected, Gridline.Ink), path: true);
            var message = string.Create(CultureInfo.InvariantCulture, $"{error.Message.Trim()} (error {error.Code})");
            Gridline.DrawCellText(g, message, Gridline.FontFor(this, Gridline.Face.Sans, Gridline.SizeDense),
                new Rectangle(r.X + pathWidth, r.Y, r.Width - pathWidth, r.Height), Gridline.RowText(selected, Gridline.TextSecondary));
            if ((e.State & DrawItemState.Focus) != 0 && Focused)
            {
                Gridline.DrawFocus(g, r);
            }
        }
    }
}
