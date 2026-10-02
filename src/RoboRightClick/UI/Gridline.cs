using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>
/// The Gridline design tokens (docs/gridline.md) as the single source of every colour,
/// font and metric the app draws with. No other file uses a literal colour.
/// </summary>
internal static class Gridline
{
    // Palette, from branding/gridline/tokens/colors.css.
    public static readonly Color Blue = ColorTranslator.FromHtml("#0000A8");
    public static readonly Color BlueDeep = ColorTranslator.FromHtml("#000078");
    public static readonly Color BlueHover = ColorTranslator.FromHtml("#1414B8");
    public static readonly Color Gray = ColorTranslator.FromHtml("#C6C6C6");
    public static readonly Color GrayLight = ColorTranslator.FromHtml("#E8E8E8");
    public static readonly Color GrayLighter = ColorTranslator.FromHtml("#F2F2F2");
    public static readonly Color White = ColorTranslator.FromHtml("#FFFFFF");
    public static readonly Color Ink = ColorTranslator.FromHtml("#101010");
    public static readonly Color TextSecondary = ColorTranslator.FromHtml("#3D3D3D");
    public static readonly Color TextDisabled = ColorTranslator.FromHtml("#7A7A7A");
    public static readonly Color Rule = ColorTranslator.FromHtml("#666666");
    public static readonly Color RuleLight = ColorTranslator.FromHtml("#9A9A9A");
    public static readonly Color Cyan = ColorTranslator.FromHtml("#008EA1");
    public static readonly Color Green = ColorTranslator.FromHtml("#18743A");
    public static readonly Color Amber = ColorTranslator.FromHtml("#B87900");
    public static readonly Color AmberBackground = ColorTranslator.FromHtml("#FFF4DC");
    public static readonly Color Red = ColorTranslator.FromHtml("#B3261E");
    public static readonly Color RedBackground = ColorTranslator.FromHtml("#FBEAE9");
    public static readonly Color BevelLight = ColorTranslator.FromHtml("#FFFFFF");
    public static readonly Color BevelMid = ColorTranslator.FromHtml("#DFDFDF");
    public static readonly Color BevelDark = ColorTranslator.FromHtml("#8A8A8A");
    public static readonly Color BevelDarker = ColorTranslator.FromHtml("#4A4A4A");

    /// <summary>Nothing drawn: the background of icon bitmaps and see-through layout panels.</summary>
    public static readonly Color Transparent = Color.Transparent;

    // Metrics in logical (96 dpi) pixels; scale with Scale().
    public const int RowHeight = 26;
    public const int RowHeightDense = 24;
    public const int TitleStripHeight = 30;
    public const int StatusBarHeight = 24;
    public const int ToolbarHeight = 34;
    public const int ButtonMinHeight = 34;
    public const int Space1 = 4, Space2 = 8, Space3 = 12, Space4 = 16, Space5 = 20, Space6 = 24, Space8 = 32;

    // Type sizes in pixels, as the tokens give them; converted to points for GDI fonts.
    public const float SizeDense = 12f, SizeUi = 13f, SizeHeading = 18f, SizeTitle = 24f, SizeMeasure = 28f;

    public static int Scale(Control control, int logicalPixels) =>
        (int)Math.Round(logicalPixels * control.DeviceDpi / 96f);

    public static int Scale(int dpi, int logicalPixels) => (int)Math.Round(logicalPixels * dpi / 96f);

    /// <summary>The state colour for text, labels and progress fills (docs/gridline.md, semantic colour only).</summary>
    public static Color ToneColor(StateTone tone) => tone switch
    {
        StateTone.Live => Cyan,
        StateTone.Attention => Amber,
        StateTone.Positive => Green,
        StateTone.Danger => Red,
        _ => TextSecondary,
    };

    /// <summary>Progress fill: neutral states (queued, scanning, canceled) fill with the light rule grey.</summary>
    public static Color ToneFill(StateTone tone) => tone == StateTone.Neutral ? RuleLight : ToneColor(tone);

    private static readonly PrivateFontCollection Collection = new();
    private static readonly List<IntPtr> FontMemory = [];
    private static readonly HashSet<FontCut> Resolved = [];
    private static bool _loaded;

    /// <summary>
    /// Loads the embedded IBM Plex faces once, for GDI+ (Font objects) and for GDI (TextRenderer
    /// and standard controls, which never see a PrivateFontCollection). Each cut is then checked
    /// through GDI; a cut GDI does not resolve to its own face falls back to Segoe UI or Consolas,
    /// so a loading problem shows the documented fallback rather than an arbitrary substitute.
    /// </summary>
    public static void LoadFonts()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        var problems = new List<string>();
        var memory = new Dictionary<string, (IntPtr Data, int Length)>(StringComparer.Ordinal);
        var assembly = typeof(Gridline).Assembly;
        foreach (var cut in GridlineFonts.All)
        {
            var file = GridlineFonts.FileName(cut);
            try
            {
                using var stream = assembly.GetManifestResourceStream("RoboRightClick.Fonts." + file);
                if (stream is null)
                {
                    problems.Add(file + ": missing");
                    continue;
                }
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                // Both APIs keep using the data, so the memory lives for the process.
                var data = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, data, bytes.Length);
                FontMemory.Add(data);
                memory[file] = (data, bytes.Length);
                uint count = 0;
                if (App.AppNative.AddFontMemResourceEx(data, (uint)bytes.Length, 0, ref count) == 0)
                {
                    problems.Add(file + ": GDI refused it");
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
            {
                problems.Add(file + ": " + ex.GetType().Name);
            }
        }

        // On Windows 11 build 26200, GDI+ now and then left the last font added out of the
        // collection with no error (about one start in four). Each family is checked and added
        // again until it is there.
        foreach (var cut in GridlineFonts.All)
        {
            var file = GridlineFonts.FileName(cut);
            if (!memory.TryGetValue(file, out var font))
            {
                continue;
            }
            for (var attempt = 1; attempt <= 3 && Family(GridlineFonts.FamilyName(cut)) is null; attempt++)
            {
                try
                {
                    Collection.AddMemoryFont(font.Data, font.Length);
                }
                catch (Exception ex) when (ex is ArgumentException or ExternalException)
                {
                    problems.Add(file + ": " + ex.GetType().Name);
                }
                if (attempt > 1)
                {
                    problems.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{file}: GDI+ attempt {attempt}"));
                }
            }
        }

        foreach (var cut in GridlineFonts.All)
        {
            if (Family(GridlineFonts.FamilyName(cut)) is { } family && GdiResolves(family))
            {
                Resolved.Add(cut);
            }
        }
        // Path-free, like the app's other debug lines; read with a debug-output viewer.
        System.Diagnostics.Trace.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"RoboRightClick fonts: {Resolved.Count} of {GridlineFonts.All.Count} Plex cuts resolved by GDI; fallback for: {string.Join(",", GridlineFonts.All.Except(Resolved))}; notes: {string.Join("; ", problems)}"));
    }

    private static FontFamily? Family(string name) => Collection.Families.FirstOrDefault(f => f.Name == name);

    /// <summary>True when GDI, asked for this family, selects a face of that name and not a substitute.</summary>
    private static unsafe bool GdiResolves(FontFamily family)
    {
        try
        {
            using var font = new Font(family, 13f, FontStyle.Regular, GraphicsUnit.Pixel);
            var hfont = font.ToHfont();
            var dc = App.AppNative.CreateCompatibleDC(0);
            try
            {
                var previous = App.AppNative.SelectObject(dc, hfont);
                var buffer = stackalloc char[64];
                var length = App.AppNative.GetTextFace(dc, 64, buffer);
                App.AppNative.SelectObject(dc, previous);
                return length > 0 && new string(buffer, 0, length - 1) == family.Name;
            }
            finally
            {
                App.AppNative.DeleteDC(dc);
                App.AppNative.DeleteObject(hfont);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return false;
        }
    }

    private static FontCut CutOf(Face face) => face switch
    {
        Face.SansMedium => FontCut.SansMedium,
        Face.SansSemiBold => FontCut.SansSemiBold,
        Face.Mono => FontCut.Mono,
        Face.MonoMedium => FontCut.MonoMedium,
        Face.MonoSemiBold => FontCut.MonoSemiBold,
        _ => FontCut.Sans,
    };

    /// <summary>A new font of the cut at <paramref name="pixelSize"/>; the caller owns it. Prefer the cached <see cref="FontAt"/>.</summary>
    public static Font Create(Face face, float pixelSize)
    {
        LoadFonts();
        var cut = CutOf(face);
        // Every Plex cut is its own family with one Regular style; asking for Bold would make
        // GDI embolden an already heavy face.
        return Resolved.Contains(cut) && Family(GridlineFonts.FamilyName(cut)) is { } family
            ? new Font(family, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font(GridlineFonts.FallbackFamily(cut), pixelSize,
                GridlineFonts.FallbackBold(cut) ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>Raised 1 px bevel (button face) inside <paramref name="r"/>.</summary>
    public static void DrawRaised(Graphics g, Rectangle r)
    {
        using var light = new Pen(BevelLight);
        using var dark = new Pen(BevelDark);
        g.DrawLine(light, r.Left, r.Top, r.Right - 1, r.Top);
        g.DrawLine(light, r.Left, r.Top, r.Left, r.Bottom - 1);
        g.DrawLine(dark, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
        g.DrawLine(dark, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
    }

    /// <summary>Inset 1 px bevel (pressed control or editable field) inside <paramref name="r"/>.</summary>
    public static void DrawInset(Graphics g, Rectangle r)
    {
        using var light = new Pen(BevelLight);
        using var dark = new Pen(BevelDark);
        g.DrawLine(dark, r.Left, r.Top, r.Right - 1, r.Top);
        g.DrawLine(dark, r.Left, r.Top, r.Left, r.Bottom - 1);
        g.DrawLine(light, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
        g.DrawLine(light, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
    }

    /// <summary>
    /// The frame of an editable field: the inset bevel's dark top and left with a darker inner
    /// line, and a mid-grey bottom and right. The inset's white edges vanish where a field sits
    /// on a white pane, which left text fields drawn as open corners.
    /// </summary>
    public static void DrawField(Graphics g, Rectangle r)
    {
        using var dark = new Pen(BevelDark);
        using var darker = new Pen(BevelDarker);
        using var mid = new Pen(BevelMid);
        g.DrawLine(dark, r.Left, r.Top, r.Right - 1, r.Top);
        g.DrawLine(dark, r.Left, r.Top, r.Left, r.Bottom - 1);
        g.DrawLine(darker, r.Left + 1, r.Top + 1, r.Right - 2, r.Top + 1);
        g.DrawLine(darker, r.Left + 1, r.Top + 1, r.Left + 1, r.Bottom - 2);
        g.DrawLine(mid, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
        g.DrawLine(mid, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
    }

    /// <summary>Dotted 1 px ink focus rectangle, inset as the tokens specify.</summary>
    public static void DrawFocus(Graphics g, Rectangle r)
    {
        using var pen = new Pen(Ink) { DashStyle = DashStyle.Dot };
        var inset = Rectangle.Inflate(r, -3, -3);
        g.DrawRectangle(pen, inset.Left, inset.Top, inset.Width - 1, inset.Height - 1);
    }

    /// <summary>The Plex cuts the app uses; each maps to one of the factory methods above.</summary>
    public enum Face
    {
        Sans,
        SansMedium,
        SansSemiBold,
        Mono,
        MonoMedium,
        MonoSemiBold,
    }

    private sealed record FontRole(Face Face, float LogicalPixels);

    // Fonts live for the process: a handful of roles times the DPIs the user's monitors
    // have, so caching avoids leaking a GDI font per paint without tracking ownership.
    private static readonly Dictionary<(Face Face, float Size, int Dpi), Font> FontCache = [];
    private static readonly ConditionalWeakTable<Control, FontRole> FontRoles = [];

    /// <summary>A cached font of <paramref name="logicalPixels"/> at 96 dpi, scaled to <paramref name="dpi"/>. UI thread only.</summary>
    public static Font FontAt(Face face, float logicalPixels, int dpi)
    {
        LoadFonts();
        var key = (face, logicalPixels, dpi);
        if (!FontCache.TryGetValue(key, out var font))
        {
            font = Create(face, logicalPixels * dpi / 96f);
            FontCache[key] = font;
        }
        return font;
    }

    public static Font FontFor(Control control, Face face, float logicalPixels) => FontAt(face, logicalPixels, control.DeviceDpi);

    /// <summary>
    /// Gives a standard control a Gridline font and remembers the role, so
    /// <see cref="RescaleFonts"/> can rebuild it for a new DPI. The fonts are pixel-sized and
    /// scaled here, never by WinForms, so a control's text is never scaled twice.
    /// </summary>
    public static T UseFont<T>(T control, Face face, float logicalPixels)
        where T : Control
    {
        FontRoles.AddOrUpdate(control, new FontRole(face, logicalPixels));
        control.Font = FontFor(control, face, logicalPixels);
        return control;
    }

    /// <summary>
    /// Re-applies every remembered font role under <paramref name="root"/> at <paramref name="dpi"/>.
    /// Also needed when hidden controls are first shown after a DPI change (a window dragged to
    /// another monitor): WinForms scales their already scaled font again as their handles are
    /// created, which doubled the text size of the conflict list's summary at 150%.
    /// </summary>
    public static void RescaleFonts(Control root, int dpi)
    {
        if (FontRoles.TryGetValue(root, out var role))
        {
            root.Font = FontAt(role.Face, role.LogicalPixels, dpi);
        }
        foreach (Control child in root.Controls)
        {
            RescaleFonts(child, dpi);
        }
    }

    /// <summary>
    /// Base for every app window: System Gray client area, Plex Sans 13, ink text. Derived
    /// constructors build their controls between <see cref="BeginBuild"/> and
    /// <see cref="EndBuild"/>, so WinForms' DPI auto-scaling (AutoScaleMode.Dpi from 96) sees
    /// every control at once, the way designer code arranges it.
    /// </summary>
    public class Window : Form
    {
        public Window()
        {
            BackColor = Gray;
            ForeColor = Ink;
            Font = FontFor(this, Face.Sans, SizeUi);
            KeyPreview = true;
            ShowIcon = false;
        }

        protected void BeginBuild()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
        }

        protected void EndBuild()
        {
            ResumeLayout(false);
            PerformLayout();
            ApplyDpi();
        }

        /// <summary>
        /// A window sized in logical pixels can be taller than the screen at 150% or more on a
        /// small display (760 x 640 becomes 1140 x 960), which hid the Settings window's Save
        /// button below the taskbar. Every window is kept inside the working area of its screen.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            KeepOnScreen();
        }

        protected void KeepOnScreen()
        {
            var area = Screen.FromControl(this).WorkingArea;
            var size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            var location = new Point(
                Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - size.Width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
            if (size != Size || location != Location)
            {
                Bounds = new Rectangle(location, size);
            }
        }

        /// <summary>Sets pixel metrics WinForms does not scale itself (list rows, grid columns). Called after build and after every DPI change.</summary>
        protected virtual void ApplyDpi()
        {
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Font = FontAt(Face.Sans, SizeUi, e.DeviceDpiNew);
            RescaleFonts(this, e.DeviceDpiNew);
            ApplyDpi();
            KeepOnScreen();
            Invalidate(invalidateChildren: true);
        }
    }

    /// <summary>
    /// Square, owner-drawn push button: gray face, 1 px rule border, raised bevel (inset while
    /// pressed), Plex Sans Medium 13, dotted focus rectangle, blue outer border when it is the
    /// default button. <see cref="Control.Name"/> and <see cref="Control.AccessibleName"/> carry
    /// a stable automation id that UI Automation scripts use to find the button.
    /// </summary>
    public sealed class Button : System.Windows.Forms.Button
    {
        private bool _pressed;
        private bool _isDefault;
        private string? _note;

        public Button(string text, string automationId)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Name = automationId;
            AccessibleName = automationId;
            AccessibleRole = AccessibleRole.PushButton;
            BackColor = Gray;
            ForeColor = Ink;
            UseVisualStyleBackColor = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(Space2 / 2, Space1, Space2 / 2, Space1);
        }

        /// <summary>A second, smaller line under the text (the conflict dialog's big choices); left-aligns the button's text.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? Note
        {
            get => _note;
            set
            {
                _note = value;
                Invalidate();
                PerformLayout();
            }
        }

        public override void NotifyDefault(bool value)
        {
            base.NotifyDefault(value);
            _isDefault = value;
            Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var flags = TextFormatFlags.SingleLine;
            var text = TextRenderer.MeasureText(Text, FontFor(this, Face.SansMedium, SizeUi), Size.Empty, flags);
            var width = text.Width;
            var height = text.Height;
            if (!string.IsNullOrEmpty(_note))
            {
                var note = TextRenderer.MeasureText(_note, FontFor(this, Face.Sans, SizeDense), Size.Empty, flags);
                width = Math.Max(width, note.Width);
                height += note.Height;
            }
            return new Size(
                Math.Max(Gridline.Scale(this, 88), width + Gridline.Scale(this, Space3) * 2 + 4),
                Math.Max(Gridline.Scale(this, ButtonMinHeight), height + Gridline.Scale(this, 6) * 2 + 4));
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            base.OnMouseDown(mevent);
            if (mevent.Button == MouseButtons.Left)
            {
                SetPressed(true);
            }
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            base.OnMouseUp(mevent);
            SetPressed(false);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetPressed(false);
        }

        protected override void OnKeyDown(KeyEventArgs kevent)
        {
            base.OnKeyDown(kevent);
            if (kevent.KeyCode == Keys.Space)
            {
                SetPressed(true);
            }
        }

        protected override void OnKeyUp(KeyEventArgs kevent)
        {
            base.OnKeyUp(kevent);
            SetPressed(false);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            SetPressed(false);
        }

        private void SetPressed(bool pressed)
        {
            if (_pressed != pressed)
            {
                _pressed = pressed;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            var r = ClientRectangle;
            using (var face = new SolidBrush(Gray))
            {
                g.FillRectangle(face, r);
            }
            if (_isDefault)
            {
                using var blue = new Pen(Blue);
                g.DrawRectangle(blue, r.X, r.Y, r.Width - 1, r.Height - 1);
                r = Rectangle.Inflate(r, -1, -1);
            }
            using (var rule = new Pen(Rule))
            {
                g.DrawRectangle(rule, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            var inner = Rectangle.Inflate(r, -1, -1);
            if (_pressed && Enabled)
            {
                DrawInset(g, inner);
            }
            else
            {
                DrawRaised(g, inner);
            }

            var offset = _pressed && Enabled ? 1 : 0;
            var color = Enabled ? Ink : TextDisabled;
            var padX = Gridline.Scale(this, Space3);
            var content = new Rectangle(inner.X + padX + offset, inner.Y + offset, inner.Width - padX * 2, inner.Height);
            var prefix = ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix;
            if (string.IsNullOrEmpty(_note))
            {
                TextRenderer.DrawText(g, Text, FontFor(this, Face.SansMedium, SizeUi), content, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | prefix);
            }
            else
            {
                var title = FontFor(this, Face.SansMedium, SizeUi);
                var noteFont = FontFor(this, Face.Sans, SizeDense);
                var total = title.Height + noteFont.Height;
                var top = content.Y + (content.Height - total) / 2;
                TextRenderer.DrawText(g, Text, title, new Rectangle(content.X, top, content.Width, title.Height), color,
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | prefix);
                TextRenderer.DrawText(g, _note, noteFont, new Rectangle(content.X, top + title.Height, content.Width, noteFont.Height),
                    Enabled ? TextSecondary : TextDisabled, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            if (Focused && ShowFocusCues)
            {
                DrawFocus(g, inner);
            }
        }
    }

    /// <summary>Square owner-drawn check box: white inset field, ink check made of straight strokes, Plex Sans 13.</summary>
    public sealed class CheckBox : System.Windows.Forms.CheckBox
    {
        private const int BoxSize = 13;
        private const int Gap = 8;

        public CheckBox(string text, string automationId)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Name = automationId;
            AccessibleName = automationId;
            AutoSize = true;
            ForeColor = Ink;
            Margin = new Padding(0, Space1, Space3, Space1);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var box = Gridline.Scale(this, BoxSize);
            var text = TextRenderer.MeasureText(Text, FontFor(this, Face.Sans, SizeUi));
            return new Size(box + Gridline.Scale(this, Gap) + text.Width + 4, Math.Max(box, text.Height) + 4);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            using (var back = new SolidBrush(OpaqueBackground(this)))
            {
                g.FillRectangle(back, ClientRectangle);
            }
            var size = Gridline.Scale(this, BoxSize);
            var box = new Rectangle(1, (Height - size) / 2, size, size);
            DrawCheckBox(g, box, Checked, Enabled);
            var textLeft = box.Right + Gridline.Scale(this, Gap);
            var textRect = new Rectangle(textLeft, 0, Width - textLeft, Height);
            TextRenderer.DrawText(g, Text, FontFor(this, Face.Sans, SizeUi), textRect, Enabled ? Ink : TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
            if (Focused && ShowFocusCues)
            {
                DrawFocus(g, Rectangle.Inflate(textRect, 2, 1));
            }
        }
    }

    /// <summary>
    /// The colour actually behind a control: its nearest ancestor with an opaque background.
    /// Layout panels are transparent, so the immediate parent's colour is often not what shows.
    /// </summary>
    public static Color OpaqueBackground(Control control)
    {
        for (var c = control.Parent; c is not null; c = c.Parent)
        {
            if (c.BackColor.A == 255)
            {
                return c.BackColor;
            }
        }
        return Gray;
    }

    /// <summary>A ruled list row: white, or Active Blue when selected, with a light rule along the bottom.</summary>
    public static void DrawRow(Graphics g, Rectangle r, bool selected)
    {
        using (var back = new SolidBrush(selected ? Blue : White))
        {
            g.FillRectangle(back, r);
        }
        using var rule = new Pen(RuleLight);
        g.DrawLine(rule, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
    }

    /// <summary>Text colour on a row: white on the blue selection, otherwise the given colour.</summary>
    public static Color RowText(bool selected, Color normal) => selected ? White : normal;

    /// <summary>A list header cell: gray, Plex Mono 12 UPPERCASE caption, rule bottom border and a rule between cells.</summary>
    public static void DrawHeader(Graphics g, Rectangle r, string caption, Font font)
    {
        using (var back = new SolidBrush(Gray))
        {
            g.FillRectangle(back, r);
        }
        using (var rule = new Pen(Rule))
        {
            g.DrawLine(rule, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            g.DrawLine(rule, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
        }
        var text = Rectangle.Inflate(r, -6, 0);
        TextRenderer.DrawText(g, caption.ToUpperInvariant(), font, text, Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>One line of cell text, cut with an ellipsis (paths cut in the middle so both ends stay readable).</summary>
    public static void DrawCellText(Graphics g, string text, Font font, Rectangle r, Color color, bool path = false, bool right = false)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
            | (path ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis)
            | (right ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(g, text, font, Rectangle.Inflate(r, -6, 0), color, flags);
    }

    /// <summary>The square check field, shared with the conflict list's checkbox cells.</summary>
    public static void DrawCheckBox(Graphics g, Rectangle box, bool isChecked, bool enabled)
    {
        using (var field = new SolidBrush(enabled ? White : GrayLight))
        {
            g.FillRectangle(field, box);
        }
        DrawInset(g, box);
        // The inset's light edges vanish on a white row, which left an open corner; a dark
        // bottom and right edge keeps the box closed on white and gray alike.
        using (var edge = new Pen(BevelDark))
        {
            g.DrawLine(edge, box.Left, box.Bottom - 1, box.Right - 1, box.Bottom - 1);
            g.DrawLine(edge, box.Right - 1, box.Top, box.Right - 1, box.Bottom - 1);
        }
        using (var deep = new Pen(BevelDarker))
        {
            g.DrawLine(deep, box.Left + 1, box.Top + 1, box.Right - 2, box.Top + 1);
            g.DrawLine(deep, box.Left + 1, box.Top + 1, box.Left + 1, box.Bottom - 2);
        }
        if (isChecked)
        {
            var previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using var pen = new Pen(enabled ? Ink : TextDisabled, Math.Max(2, box.Width / 7));
            var u = box.Width / 13f;
            g.DrawLines(pen,
            [
                new PointF(box.X + 3 * u, box.Y + 6.5f * u),
                new PointF(box.X + 5.5f * u, box.Y + 9 * u),
                new PointF(box.X + 10 * u, box.Y + 3.5f * u),
            ]);
            g.SmoothingMode = previous;
        }
    }

    /// <summary>
    /// A pane: 1 px rule frame, 30 px title strip (Active Blue with white Plex Mono SemiBold 13
    /// UPPERCASE, or cyan with ink text for live records) and a white body. Children are laid
    /// out in the body only.
    /// </summary>
    public sealed class Pane : Panel
    {
        private string _title;
        private bool _live;

        public Pane(string title, bool live = false)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _title = title;
            _live = live;
            BackColor = White;
            ForeColor = Ink;
            Padding = new Padding(Space3, Space2, Space3, Space2);
            Margin = new Padding(0, 0, 0, Space2);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    AccessibleName = value;
                    Invalidate();
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Live
        {
            get => _live;
            set
            {
                if (_live != value)
                {
                    _live = value;
                    Invalidate();
                }
            }
        }

        // The strip and frame are drawn, not controls, so the body is the client area minus
        // them and minus Padding. Layout takes this rectangle as it is (the base class is
        // where Padding is normally subtracted), so leaving Padding out put text against the frame.
        public override Rectangle DisplayRectangle
        {
            get
            {
                var strip = Gridline.Scale(this, TitleStripHeight);
                var r = ClientRectangle;
                var p = Padding;
                return new Rectangle(
                    r.X + 1 + p.Left,
                    r.Y + strip + 1 + p.Top,
                    Math.Max(0, r.Width - 2 - p.Horizontal),
                    Math.Max(0, r.Height - strip - 2 - p.Vertical));
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var inner = base.GetPreferredSize(proposedSize);
            return new Size(inner.Width + 2, inner.Height + Gridline.Scale(this, TitleStripHeight) + 2);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            PerformLayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(White);
            var strip = new Rectangle(0, 0, Width, Gridline.Scale(this, TitleStripHeight));
            using (var fill = new SolidBrush(_live ? Cyan : Blue))
            {
                g.FillRectangle(fill, strip);
            }
            var pad = Gridline.Scale(this, Space3);
            TextRenderer.DrawText(g, _title.ToUpperInvariant(), FontFor(this, Face.MonoSemiBold, SizeUi),
                new Rectangle(pad, 0, Width - pad * 2, strip.Height), _live ? Ink : White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var rule = new Pen(Rule);
            g.DrawRectangle(rule, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Square progress bar: white track, 1 px rule frame, fill in the state's colour. No animation beyond value changes.</summary>
    public sealed class ProgressBar : Control
    {
        private int _value;
        private StateTone _tone;

        public ProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Height = 16;
            AccessibleRole = AccessibleRole.ProgressBar;
            AccessibleName = "Progress";
            Margin = new Padding(0, Space2, 0, Space2);
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        }

        /// <summary>0 to 100.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (_value != clamped)
                {
                    _value = clamped;
                    AccessibleDescription = clamped.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
                    Invalidate();
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public StateTone Tone
        {
            get => _tone;
            set
            {
                if (_tone != value)
                {
                    _tone = value;
                    Invalidate();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e) => DrawBar(e.Graphics, ClientRectangle, _value, _tone);

        /// <summary>Shared with owner-drawn list rows, so a bar looks the same everywhere.</summary>
        public static void DrawBar(Graphics g, Rectangle r, int value, StateTone tone)
        {
            using (var track = new SolidBrush(White))
            {
                g.FillRectangle(track, r);
            }
            var inner = Rectangle.Inflate(r, -2, -2);
            var width = (int)Math.Round(inner.Width * Math.Clamp(value, 0, 100) / 100.0);
            if (width > 0)
            {
                using var fill = new SolidBrush(ToneFill(tone));
                g.FillRectangle(fill, inner.X, inner.Y, width, inner.Height);
            }
            using var rule = new Pen(Rule);
            g.DrawRectangle(rule, r.X, r.Y, r.Width - 1, r.Height - 1);
        }
    }

    /// <summary>
    /// Caution strip for warnings and the ephemeral notice: pale amber background, 4 px amber
    /// left border, Plex Sans 13, wrapped to the width it is given (inside a TableLayoutPanel,
    /// anchored left and right). The danger variant uses the red pair for failure reasons.
    /// </summary>
    public sealed class CautionStrip : Label
    {
        private readonly bool _danger;

        public CautionStrip(string text, string automationId, bool danger = false)
        {
            _danger = danger;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Name = automationId;
            AccessibleName = automationId;
            AutoSize = true;
            UseMnemonic = false;
            BackColor = danger ? RedBackground : AmberBackground;
            ForeColor = Ink;
            Padding = new Padding(4 + Space3, Space2, Space3, Space2);
            Margin = new Padding(0, 0, 0, Space2);
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            UseFont(this, Face.Sans, SizeUi);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(_danger ? RedBackground : AmberBackground))
            {
                g.FillRectangle(back, ClientRectangle);
            }
            using (var border = new SolidBrush(_danger ? Red : Amber))
            {
                g.FillRectangle(border, 0, 0, Gridline.Scale(this, 4), Height);
            }
            var r = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
            TextRenderer.DrawText(g, Text, Font, r, Ink, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Status bar: gray, rule top border, Plex Mono 12 UPPERCASE cells separated by 1 px rules.</summary>
    public sealed class StatusBar : Control
    {
        private IReadOnlyList<string> _cells = [];

        public StatusBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Dock = DockStyle.Bottom;
            Height = StatusBarHeight;
            BackColor = Gray;
            AccessibleRole = AccessibleRole.StatusBar;
            AccessibleName = "Status";
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public IReadOnlyList<string> Cells
        {
            get => _cells;
            set
            {
                if (!_cells.SequenceEqual(value))
                {
                    _cells = value;
                    AccessibleDescription = string.Join(" | ", value);
                    Invalidate();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Gray);
            using var rule = new Pen(Rule);
            g.DrawLine(rule, 0, 0, Width, 0);
            var font = FontFor(this, Face.Mono, SizeDense);
            var pad = Gridline.Scale(this, Space2);
            var x = 0;
            foreach (var cell in _cells)
            {
                var text = cell.ToUpperInvariant();
                var width = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + pad * 2;
                TextRenderer.DrawText(g, text, font, new Rectangle(x, 1, width, Height - 1), Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                x += width;
                g.DrawLine(rule, x, 1, x, Height);
                x += 1;
            }
        }
    }

    /// <summary>A white field with an inset bevel around a borderless text box, <paramref name="logicalWidth"/> wide at 96 dpi.</summary>
    public sealed class TextField : Panel
    {
        private readonly int _logicalWidth;

        public TextField(string automationId, bool mono, int logicalWidth = 160)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _logicalWidth = logicalWidth;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = White;
            Padding = new Padding(4, 3, 4, 3);
            Margin = new Padding(0, Space1 / 2, Space2, Space1 / 2);
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = White,
                ForeColor = Ink,
                Name = automationId,
                AccessibleName = automationId,
                Dock = DockStyle.Fill,
            };
            UseFont(Box, mono ? Face.Mono : Face.Sans, SizeUi);
            Controls.Add(Box);
        }

        public TextBox Box { get; }

        public override Size GetPreferredSize(Size proposedSize) =>
            new(Gridline.Scale(this, _logicalWidth), Box.PreferredHeight + Padding.Vertical);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Box.Enabled ? White : GrayLight);
            DrawField(e.Graphics, ClientRectangle);
        }
    }

    /// <summary>A drop-down list in a white field with the inset bevel text fields have, <paramref name="logicalWidth"/> wide at 96 dpi.</summary>
    public sealed class ComboField : Panel
    {
        private readonly int _logicalWidth;

        public ComboField(ComboBox box, int logicalWidth)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _logicalWidth = logicalWidth;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = White;
            Padding = new Padding(2);
            Margin = box.Margin;
            box.Margin = Padding.Empty;
            box.Dock = DockStyle.Fill;
            Box = box;
            Controls.Add(box);
        }

        public ComboBox Box { get; }

        public override Size GetPreferredSize(Size proposedSize) =>
            new(Gridline.Scale(this, _logicalWidth), Box.PreferredHeight + Padding.Vertical);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Box.Enabled ? White : GrayLight);
            DrawField(e.Graphics, ClientRectangle);
        }
    }

    /// <summary>A label in the Gridline caption style: Plex Mono SemiBold 12 UPPERCASE, in ink or a state colour.</summary>
    public static Label Caption(string text, Color? color = null)
    {
        var label = new Label
        {
            Text = text.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = color ?? Ink,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            Margin = new Padding(0, Space1, Space2, Space1),
        };
        return UseFont(label, Face.MonoSemiBold, SizeDense);
    }

    /// <summary>A plain text label, wrapped to its column when anchored left and right in a TableLayoutPanel.</summary>
    public static Label TextLabel(string text, Face face = Face.Sans, float size = SizeUi, Color? color = null)
    {
        var label = new System.Windows.Forms.Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = color ?? Ink,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, Space1 / 2, 0, Space1 / 2),
        };
        return UseFont(label, face, size);
    }

    /// <summary>A one-column TableLayoutPanel whose rows size to their content: the layout of every pane body and dialog.</summary>
    public static TableLayoutPanel Stack(params Control[] rows)
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(row);
        }
        return stack;
    }

    /// <summary>A right-aligned row of buttons, given left to right.</summary>
    public static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, Space1, 0, 0),
            Padding = Padding.Empty,
        };
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            row.Controls.Add(buttons[i]);
        }
        return row;
    }

    /// <summary>
    /// A modal Gridline confirmation: the question and two buttons, the safe one focused and
    /// default, so Enter or Escape never does the destructive thing. Returns true for
    /// <paramref name="confirm"/>.
    /// </summary>
    public static bool Confirm(IWin32Window? owner, string title, string question, string confirm, string confirmId, string decline, string declineId) =>
        MessageDialog.Show(owner, new MessageContent(title, question),
        [
            new DialogButton(confirm, confirmId, DialogResult.Yes),
            new DialogButton(decline, declineId, DialogResult.No, IsDefault: true, IsCancel: true),
        ]) == DialogResult.Yes;

    /// <summary>A modal Gridline notice with one OK button, for outcomes the user must not miss.</summary>
    public static void Inform(IWin32Window? owner, string title, string text, MessageTone tone = MessageTone.Neutral, string? heading = null) =>
        MessageDialog.Show(owner, new MessageContent(title, text) { Tone = tone, Heading = heading },
        [
            new DialogButton("OK", "OK", DialogResult.OK, IsDefault: true, IsCancel: true),
        ]);

    /// <summary>
    /// An app-owned context menu in Gridline style: the square renderer below, a check margin
    /// instead of the wide image margin, and no drop shadow (Gridline has no shadows; Windows adds
    /// one to every menu window unless its class style is removed).
    /// </summary>
    public sealed class ContextMenu : ContextMenuStrip
    {
        private const int CsDropShadow = 0x00020000;

        public ContextMenu()
        {
            Renderer = new MenuRenderer();
            ShowImageMargin = false;
            ShowCheckMargin = true;
            BackColor = Gray;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle &= ~CsDropShadow;
                return cp;
            }
        }
    }

    /// <summary>Square, flat menu rendering for app-owned menus (tray, context menus).</summary>
    public sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer()
            : base(new MenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? TextDisabled : e.Item.Selected ? White : Ink;
            base.OnRenderItemText(e);
        }

        /// <summary>The Gridline check box instead of the professional renderer's, which framed it in the system's light blue.</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var size = Gridline.Scale(e.ToolStrip?.DeviceDpi ?? 96, 13);
            var r = e.ImageRectangle;
            var box = new Rectangle(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
            DrawCheckBox(e.Graphics, box, isChecked: true, e.Item.Enabled);
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Blue;
            public override Color MenuItemSelectedGradientBegin => Blue;
            public override Color MenuItemSelectedGradientEnd => Blue;
            public override Color MenuItemBorder => Blue;
            public override Color MenuBorder => Rule;
            public override Color ToolStripDropDownBackground => Gray;
            public override Color ImageMarginGradientBegin => Gray;
            public override Color ImageMarginGradientMiddle => Gray;
            public override Color ImageMarginGradientEnd => Gray;
            public override Color SeparatorDark => Rule;
            public override Color SeparatorLight => Gray;
            public override Color CheckBackground => GrayLight;
            public override Color CheckSelectedBackground => BlueDeep;
            public override Color CheckPressedBackground => BlueDeep;
        }
    }
}
