using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

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

    private static readonly PrivateFontCollection Collection = new();
    private static readonly List<IntPtr> FontMemory = [];
    private static FontFamily? _sans;
    private static FontFamily? _mono;
    private static bool _loaded;

    /// <summary>
    /// Loads the embedded IBM Plex faces once. If anything fails, the app falls back to
    /// Segoe UI and Consolas rather than refusing to start over a font.
    /// </summary>
    public static void LoadFonts()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        var assembly = typeof(Gridline).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("RoboRightClick.Fonts.", StringComparison.Ordinal)))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    continue;
                }
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                // AddMemoryFont keeps a pointer to the data, so the memory lives for the process.
                var memory = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                Collection.AddMemoryFont(memory, bytes.Length);
                FontMemory.Add(memory);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
            {
                // Fall through to the system fallbacks below.
            }
        }
        _sans = Collection.Families.FirstOrDefault(f => f.Name == "IBM Plex Sans");
        _mono = Collection.Families.FirstOrDefault(f => f.Name == "IBM Plex Mono");
    }

    /// <summary>
    /// GDI+ only exposes the regular and bold styles of a family, so the medium and semibold
    /// Plex cuts load as their own families ("IBM Plex Sans Medium", "IBM Plex Sans SemiBold").
    /// </summary>
    private static FontFamily? Family(string name) => Collection.Families.FirstOrDefault(f => f.Name == name);

    public static Font Sans(float pixelSize, FontStyle style = FontStyle.Regular) =>
        Make(_sans, "Segoe UI", pixelSize, style);

    public static Font SansMedium(float pixelSize) =>
        Make(Family("IBM Plex Sans Medium") ?? _sans, "Segoe UI", pixelSize, FontStyle.Regular);

    public static Font SansSemiBold(float pixelSize) =>
        Make(Family("IBM Plex Sans SemiBold") ?? _sans, "Segoe UI Semibold", pixelSize, FontStyle.Regular);

    public static Font Mono(float pixelSize, FontStyle style = FontStyle.Regular) =>
        Make(_mono, "Consolas", pixelSize, style);

    public static Font MonoMedium(float pixelSize) =>
        Make(Family("IBM Plex Mono Medium") ?? _mono, "Consolas", pixelSize, FontStyle.Regular);

    public static Font MonoSemiBold(float pixelSize) =>
        Make(Family("IBM Plex Mono SemiBold") ?? _mono, "Consolas", pixelSize, FontStyle.Bold);

    private static Font Make(FontFamily? family, string fallback, float pixelSize, FontStyle style) =>
        family is not null
            ? new Font(family, pixelSize, style, GraphicsUnit.Pixel)
            : new Font(fallback, pixelSize, style, GraphicsUnit.Pixel);

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

    /// <summary>Dotted 1 px ink focus rectangle, inset as the tokens specify.</summary>
    public static void DrawFocus(Graphics g, Rectangle r)
    {
        using var pen = new Pen(Ink) { DashStyle = DashStyle.Dot };
        var inset = Rectangle.Inflate(r, -3, -3);
        g.DrawRectangle(pen, inset.Left, inset.Top, inset.Width - 1, inset.Height - 1);
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
