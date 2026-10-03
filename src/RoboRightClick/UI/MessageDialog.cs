using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>How a <see cref="MessageDialog"/> presents its text: plain, or in a caution strip.</summary>
internal enum MessageTone
{
    /// <summary>Plain text in the pane.</summary>
    Neutral,

    /// <summary>Amber caution strip: a qualification or something the user should check.</summary>
    Attention,

    /// <summary>Red strip: something failed.</summary>
    Danger,
}

/// <summary>What a <see cref="MessageDialog"/> says. Only <see cref="Title"/> and <see cref="Text"/> are required.</summary>
/// <param name="Title">Window title, and the pane title unless <see cref="PaneTitle"/> is set.</param>
/// <param name="Text">The body: what happened and what to do.</param>
internal sealed record MessageContent(string Title, string Text)
{
    public string? PaneTitle { get; init; }

    /// <summary>One sentence above the body in the heading size: the answer before the detail.</summary>
    public string? Heading { get; init; }

    /// <summary>Caption/value rows under the body (for example where the app installs), values in Plex Mono.</summary>
    public IReadOnlyList<(string Caption, string Value)> Facts { get; init; } = [];

    public MessageTone Tone { get; init; } = MessageTone.Neutral;
}

/// <param name="IsDefault">Enter presses it, and it has the focus when the dialog opens.</param>
/// <param name="IsCancel">Escape and the title bar's close button press it.</param>
internal sealed record DialogButton(string Text, string AutomationId, DialogResult Result, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// The app's modal message and question dialog in Gridline style: one pane with a blue title
/// strip, an optional heading, the text (plain or in a caution strip), optional fact rows and
/// one extra control, and a right-aligned button row. It sizes itself to its text at a fixed
/// width, so a long message or path is never cut off and a short one leaves no empty space.
/// </summary>
/// <remarks>
/// The height is computed after WinForms has scaled the window to the monitor's DPI (OnLoad)
/// and again after every DPI change, from the controls' own preferred sizes at that DPI, so
/// nothing here assumes 96 dpi.
/// </remarks>
internal sealed class MessageDialog : Gridline.Window
{
    // The text column's width at 96 dpi; about 70 characters of Plex Sans 13.
    private const int TextWidth = 460;

    private readonly Gridline.Pane _pane;
    private readonly TableLayoutPanel _body;
    private readonly FlowLayoutPanel _buttons;
    private readonly List<Label> _wrapped = [];
    private readonly TableLayoutPanel? _facts;
    private readonly List<Label> _factValues = [];
    private readonly List<Label> _factCaptions = [];

    public MessageDialog(MessageContent content, IReadOnlyList<DialogButton> buttons, Control? extra = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(buttons.Count);

        BeginBuild();
        Text = content.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(Gridline.Space3);
        ClientSize = new Size(TextWidth + 64, 200);

        var rows = new List<Control>();
        if (content.Heading is { Length: > 0 } heading)
        {
            var label = Wrapped(Gridline.TextLabel(heading, Gridline.Face.SansSemiBold, Gridline.SizeHeading));
            label.Name = "Heading";
            label.AccessibleName = heading;
            label.Margin = new Padding(0, 0, 0, Gridline.Space2);
            rows.Add(label);
        }

        Label text = content.Tone == MessageTone.Neutral
            ? Gridline.TextLabel(content.Text)
            : new Gridline.CautionStrip(content.Text, "Message", danger: content.Tone == MessageTone.Danger);
        text.Name = "Message";
        text.AccessibleName = content.Text;
        rows.Add(Wrapped(text));

        if (content.Facts.Count > 0)
        {
            _facts = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                BackColor = Gridline.Transparent,
                Margin = new Padding(0, Gridline.Space2, 0, 0),
                Padding = Padding.Empty,
            };
            _facts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var (caption, value) in content.Facts)
            {
                _facts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var captionLabel = Gridline.Caption(caption, Gridline.TextSecondary);
                captionLabel.Margin = new Padding(0, Gridline.Space1, Gridline.Space3, Gridline.Space1);
                var valueLabel = Gridline.TextLabel(value, Gridline.Face.Mono, Gridline.SizeDense);
                valueLabel.Margin = new Padding(0, Gridline.Space1, 0, Gridline.Space1);
                valueLabel.AccessibleName = caption;
                valueLabel.AccessibleDescription = value;
                _factCaptions.Add(captionLabel);
                _factValues.Add(valueLabel);
                _facts.Controls.Add(captionLabel);
                _facts.Controls.Add(valueLabel);
            }
            rows.Add(_facts);
        }

        if (extra is not null)
        {
            extra.Margin = new Padding(0, Gridline.Space2, 0, 0);
            rows.Add(extra);
        }

        _body = Gridline.Stack([.. rows]);
        _pane = new Gridline.Pane(content.PaneTitle ?? content.Title) { Dock = DockStyle.Fill };
        _pane.Controls.Add(_body);

        var made = buttons.Select(b => new Gridline.Button(b.Text, b.AutomationId) { DialogResult = b.Result }).ToArray();
        _buttons = Gridline.ButtonRow(made);
        _buttons.Dock = DockStyle.Bottom;

        Controls.Add(_pane);
        Controls.Add(_buttons);

        var defaultIndex = Math.Max(0, buttons.ToList().FindIndex(b => b.IsDefault));
        var cancelIndex = buttons.ToList().FindIndex(b => b.IsCancel);
        AcceptButton = made[defaultIndex];
        CancelButton = cancelIndex >= 0 ? made[cancelIndex] : made[defaultIndex];
        ActiveControl = made[defaultIndex];
        EndBuild();
    }

    /// <summary>
    /// Shows the dialog and returns the pressed button's result (the cancel button's for Escape
    /// or the close box). Centered on the screen when there is no owner.
    /// </summary>
    public static DialogResult Show(IWin32Window? owner, MessageContent content, IReadOnlyList<DialogButton> buttons, Control? extra = null)
    {
        using var dialog = new MessageDialog(content, buttons, extra);
        if (owner is null)
        {
            dialog.StartPosition = FormStartPosition.CenterScreen;
            // Without an owner nothing else puts it in front of the window the user is in.
            dialog.TopMost = true;
        }
        var result = dialog.ShowDialog(owner);
        if (result == DialogResult.Cancel && buttons.FirstOrDefault(b => b.IsCancel) is { } cancel)
        {
            return cancel.Result;
        }
        return result;
    }

    /// <summary>
    /// A result or problem the user must see: the app's name as the window title and in the
    /// title strip, <paramref name="heading"/> as the wrapped heading line, the text plain or in
    /// a caution strip, one OK button. If the Gridline window cannot be built (the failure being
    /// reported may be why), a plain message box says the same, so the message is never lost.
    /// </summary>
    /// <remarks>
    /// The heading is a sentence such as "RoboRightClick was updated from 1.0.0-beta.0 to
    /// 1.0.0-beta.1." The title strip is one line in capitals: there it was cut off before the
    /// new version and its version strings were upper-cased (docs/testlog.md 2026-10-03).
    /// </remarks>
    public static void Notice(IWin32Window? owner, string heading, string text, MessageTone tone)
    {
        try
        {
            Show(owner, new MessageContent(AppInfo.Name, text) { Heading = heading, Tone = tone },
                [new DialogButton("OK", "OK", DialogResult.OK, IsDefault: true, IsCancel: true)]);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            MessageBox.Show(heading + "\n\n" + text, AppInfo.Name, MessageBoxButtons.OK,
                tone switch
                {
                    MessageTone.Danger => MessageBoxIcon.Error,
                    MessageTone.Attention => MessageBoxIcon.Warning,
                    _ => MessageBoxIcon.Information,
                });
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        // Before base.OnLoad, which centers the window on the size it has at that moment.
        FitToContent();
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToContent();
        KeepOnScreen();
    }

    private Label Wrapped(Label label)
    {
        _wrapped.Add(label);
        return label;
    }

    /// <summary>Fixes the width, wraps every text to it, and sets the height the wrapped text needs.</summary>
    private void FitToContent()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var inner = Gridline.Scale(this, TextWidth);
        if (_facts is not null)
        {
            // Wide enough for the longest fact on one line (a path broken mid-name is hard to
            // read), up to most of the screen; longer values wrap.
            var captionWidth = _factCaptions.Max(c => c.GetPreferredSize(Size.Empty).Width + c.Margin.Horizontal);
            foreach (var value in _factValues)
            {
                value.MaximumSize = Size.Empty;
            }
            var longest = _factValues.Max(v => v.GetPreferredSize(Size.Empty).Width + v.Margin.Horizontal) + 2;
            inner = Math.Clamp(captionWidth + longest, inner, Math.Max(inner, area.Width * 3 / 4));
            foreach (var value in _factValues)
            {
                value.MaximumSize = new Size(Math.Max(Gridline.Scale(this, 120), inner - captionWidth), 0);
            }
        }
        foreach (var label in _wrapped)
        {
            label.MaximumSize = new Size(inner, 0);
        }
        var body = _body.GetPreferredSize(new Size(inner, 0));
        var paneHeight = body.Height + Gridline.Scale(this, Gridline.TitleStripHeight) + 2 + _pane.Padding.Vertical;
        var buttons = _buttons.GetPreferredSize(Size.Empty);
        var width = Math.Max(inner, buttons.Width) + _pane.Padding.Horizontal + 2 + Padding.Horizontal;
        var height = paneHeight + buttons.Height + _buttons.Margin.Vertical + Padding.Vertical;

        // Never taller than the screen; the text column is wide enough that this takes pages of text.
        ClientSize = new Size(width, Math.Min(height, area.Height - Gridline.Scale(this, 80)));
    }
}
