using System.Globalization;
using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

/// <summary>
/// The per-job counterpart of Explorer's copy dialog, so a paste always shows that it is
/// working. Opened by the tray about one second after a job is created (so tiny pastes do
/// not flash a window; timing from Core's ProgressWindowPolicy), when showProgressWindow
/// is on. Shows: title "Robo-Copy: 3 items → Archive" (ephemeral: "Robo-Copy job", no
/// names), state text including the queue reason (<see cref="JobSnapshot.Wait"/>) and
/// "Discovered N items (X GB)" while scanning, a progress bar, bytes and files done of
/// total, speed and ETA, and Pause/Resume, Cancel ("Canceling…" and disabled while
/// <see cref="JobSnapshot.CancelRequested"/>), "More details" (Jobs window). Polls
/// <see cref="JobManager.SnapshotOf"/> every 250 ms while visible. On Done it closes; on
/// DoneWithErrors, Failed or a damaging cancel it turns into the
/// <see cref="ErrorSummaryDialog"/> content instead of closing. It owns the job's
/// <see cref="ConflictDialog"/>, which is how that dialog comes to the front.
/// </summary>
/// <remarks>
/// Closing the window never cancels the job: the paste carries on and stays in the tray
/// and the Jobs window. Only Cancel cancels. That includes an open conflict question: WinForms
/// closes owned forms with their owner, and a closed question answers null, which cancels the
/// job, so the window lets go of the question before it closes. The window appears without taking keyboard
/// focus, because it opens a second after the paste and the user may be typing elsewhere;
/// a conflict question activates its own dialog. A pause latched before the job reaches
/// Running (here, in the Jobs window or by "Pause all") comes from
/// <see cref="JobSnapshot.PauseRequested"/>, so every window agrees.
/// </remarks>
internal sealed class ProgressWindow : Gridline.Window
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private const int WmClose = 0x0010;

    private readonly System.Windows.Forms.Timer _poll;
    private readonly Gridline.Pane _pane;
    private readonly TableLayoutPanel _progress;
    private readonly Label _stateLabel;
    private readonly Label _stateText;
    private readonly Label _from;
    private readonly Label _to;
    private readonly Gridline.ProgressBar _bar;
    private readonly Label _amounts;
    private readonly Label _rate;
    private readonly Gridline.CautionStrip _ephemeralNotice;
    private readonly Gridline.Button _pauseResume;
    private readonly Gridline.Button _cancel;
    private readonly FlowLayoutPanel _buttons;
    private JobSnapshot? _last;
    private bool _showingSummary;

    public ProgressWindow(Guid jobId, JobManager jobs)
    {
        JobId = jobId;
        Jobs = jobs;

        BeginBuild();
        Text = "Robo-Copy";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        ClientSize = new Size(500, 340);
        Padding = new Padding(Gridline.Space3);

        _stateLabel = Gridline.Caption("Queued");
        _stateLabel.Name = "StateLabel";
        _stateText = Gridline.TextLabel(string.Empty);
        _stateText.Name = "StateText";
        _stateText.AccessibleName = "StateText";
        _stateText.Anchor = AnchorStyles.Left;
        var stateRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Gridline.Transparent,
            Margin = Padding.Empty,
            Dock = DockStyle.Fill,
        };
        stateRow.Controls.Add(_stateLabel);
        stateRow.Controls.Add(_stateText);

        _from = Line("From");
        _to = Line("To");
        _bar = new Gridline.ProgressBar();
        _amounts = Line("Amounts");
        _rate = Line("Rate");
        _ephemeralNotice = new Gridline.CautionStrip(
            "Ephemeral: nothing about this job is written to disk. Names shown here stay in memory.",
            "EphemeralNotice")
        {
            Visible = false,
        };

        _progress = Gridline.Stack(stateRow, _from, _to, _bar, _amounts, _rate, _ephemeralNotice);
        _pane = new Gridline.Pane("Robo-Copy") { Dock = DockStyle.Fill };
        _pane.Controls.Add(_progress);

        var details = new Gridline.Button("More details", "MoreDetails");
        details.Click += (_, _) => MoreDetails?.Invoke(this, JobId);
        _pauseResume = new Gridline.Button("Pause", "Pause");
        _pauseResume.Click += (_, _) => TogglePause();
        _cancel = new Gridline.Button("Cancel", "Cancel");
        _cancel.Click += (_, _) => CancelJob();
        _buttons = Gridline.ButtonRow(details, _pauseResume, _cancel);
        _buttons.Dock = DockStyle.Bottom;

        Controls.Add(_pane);
        Controls.Add(_buttons);
        EndBuild();

        _poll = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _poll.Tick += (_, _) => Poll();
    }

    public Guid JobId { get; }

    public JobManager Jobs { get; }

    /// <summary>"More details": the host opens the Jobs window with this job selected.</summary>
    public event EventHandler<Guid>? MoreDetails;

    /// <summary>Appears without taking the keyboard from whatever the user is typing in.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Poll();
        FitHeight();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitHeight();
    }

    /// <summary>As tall as the lines shown, so the pane has no empty block under the rate line.</summary>
    private void FitHeight()
    {
        if (_showingSummary)
        {
            return;
        }
        var inner = _pane.DisplayRectangle.Width;
        var body = _progress.GetPreferredSize(new Size(inner, 0)).Height;
        var pane = body + Gridline.Scale(this, Gridline.TitleStripHeight) + 2 + _pane.Padding.Vertical;
        var buttons = _buttons.GetPreferredSize(Size.Empty).Height + _buttons.Margin.Vertical;
        ClientSize = new Size(ClientSize.Width, pane + buttons + Padding.Vertical);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // Polling only while visible: a hidden or closed window does no work.
        _poll.Enabled = Visible && !_showingSummary;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            // Escape closes the window. The job keeps running, and a summary closed this way
            // keeps the job's attention state (ErrorSummaryChoice.None).
            e.Handled = true;
            Close();
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Form.Close, the title bar's X and Alt+F4 all arrive as WM_CLOSE, and WinForms raises
        // FormClosing and FormClosed on owned forms before this form's own handlers run, so
        // the question has to be released here, ahead of base processing.
        if (m.Msg == WmClose)
        {
            foreach (var question in OwnedForms.OfType<ConflictDialog>())
            {
                question.Owner = null;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _poll.Dispose();
        base.OnFormClosed(e);
    }

    private Label Line(string automationId)
    {
        var label = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,
            Height = 20,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            BackColor = Gridline.Transparent,
            ForeColor = Gridline.Ink,
            Name = automationId,
            AccessibleName = automationId,
            Margin = new Padding(0, 1, 0, 1),
        };
        return Gridline.UseFont(label, Gridline.Face.Mono, Gridline.SizeDense);
    }

    private static bool PauseLatched(JobSnapshot job) =>
        job.State is JobState.Queued or JobState.Scanning or JobState.AwaitingDecision && job.PauseRequested;

    private void Poll()
    {
        if (_showingSummary || IsDisposed)
        {
            return;
        }
        var job = Jobs.SnapshotOf(JobId);
        if (job is null)
        {
            // The job left the in-memory history; there is nothing left to show.
            Close();
            return;
        }
        switch (ProgressWindowPolicy.OnTerminal(job))
        {
            case ProgressWindowAction.Close:
                Close();
                return;
            case ProgressWindowAction.ShowSummary:
                TurnIntoSummary(job);
                return;
        }
        if (job == _last)
        {
            return;
        }
        _last = job;
        Render(job);
    }

    private void Render(JobSnapshot job)
    {
        var latched = PauseLatched(job);
        var maxJobs = Jobs.CurrentSettings().MaxConcurrentJobs;
        var tone = JobStateText.Tone(job, latched);

        Text = JobStateText.Title(job);
        var percent = JobStateText.Percent(job);
        _pane.Title = string.Create(CultureInfo.InvariantCulture, $"{JobStateText.VerbName(job.Verb)} · {percent}%");
        _pane.Live = job.State == JobState.Running && !job.CancelRequested;

        _stateLabel.Text = JobStateText.Label(job, latched);
        _stateLabel.ForeColor = Gridline.ToneColor(tone);
        _stateText.Text = JobStateText.For(job, maxJobs, latched);
        _from.Text = "From  " + DisplayText.SourcesSummary(job.Sources);
        _to.Text = "To    " + job.Destination;
        _bar.Value = percent;
        _bar.Tone = tone;
        _amounts.Text = JobStateText.Amounts(job);
        _rate.Text = JobStateText.Rate(job);
        var ephemeral = job.Logging == LoggingMode.Ephemeral;
        if (_ephemeralNotice.Visible != ephemeral)
        {
            _ephemeralNotice.Visible = ephemeral;
            FitHeight();
        }

        var canControl = !JobStates.IsTerminal(job.State) && !job.CancelRequested && job.State != JobState.Finalizing;
        var showResume = job.State == JobState.Paused || latched;
        _pauseResume.Text = showResume ? "Resume" : "Pause";
        _pauseResume.Name = _pauseResume.Text;
        _pauseResume.AccessibleName = _pauseResume.Text;
        _pauseResume.Enabled = canControl;
        _cancel.Text = job.CancelRequested ? "Canceling…" : "Cancel";
        _cancel.Enabled = canControl;
    }

    private void TogglePause()
    {
        if (_last is not { } job)
        {
            return;
        }
        if (job.State == JobState.Paused || PauseLatched(job))
        {
            Jobs.Resume(JobId);
        }
        else
        {
            Jobs.Pause(JobId);
        }
        _last = null;
        Poll();
    }

    private void CancelJob()
    {
        _cancel.Enabled = false;
        _cancel.Text = "Canceling…";
        Jobs.Cancel(JobId);
        _last = null;
        Poll();
    }

    /// <summary>The window becomes the error summary in place, so the user sees the outcome where they were watching.</summary>
    private void TurnIntoSummary(JobSnapshot job)
    {
        _showingSummary = true;
        _poll.Stop();
        Text = JobStateText.Title(job);

        SuspendLayout();
        _pane.Visible = false;
        _buttons.Visible = false;
        var view = new ErrorSummaryView(job, Jobs.ErrorsOf(JobId), Jobs) { Dock = DockStyle.Fill };
        view.Chosen += (_, _) => Close();
        Controls.Add(view);
        view.BringToFront();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = new Size(Gridline.Scale(this, 480), Gridline.Scale(this, 320));
        var wanted = new Size(Gridline.Scale(this, 640), Gridline.Scale(this, 440));
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Math.Max(Width, wanted.Width), area.Width), Math.Min(Math.Max(Height, wanted.Height), area.Height));
        AcceptButton = view.DefaultButton;
        ResumeLayout(true);
        // A finished job with errors is the moment the user needs to see; flash if Windows refuses foreground.
        Activate();
    }
}
