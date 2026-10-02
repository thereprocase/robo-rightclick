using System.Drawing.Drawing2D;
using RoboRightClick.Core;
using RoboRightClick.UI;

namespace RoboRightClick.App;

/// <summary>
/// Tray icons drawn at runtime with GDI+, so the repository needs no binary assets:
/// one glyph per <see cref="TrayIconState"/> (idle, running, paused, attention), each in
/// a normal and a distinct ephemeral tint, at the size the shell asks for
/// (SystemInformation.SmallIconSize, which follows DPI). Icons are created once and
/// cached; Dispose destroys their HICONs (Icon.FromHandle does not own them).
/// </summary>
/// <remarks>
/// <para>The glyph follows docs/gridline.md: a square tile with a two-page copy mark. Normal
/// mode: the tile is the state colour (idle gray, running cyan, paused amber, attention red)
/// with a 1 px ink frame and white pages outlined in ink. Ephemeral mode inverts it: an ink
/// tile with a gray frame, white page outlines and the front page filled in the state colour,
/// so the mode is recognisable at a glance and the tile edge stays visible on both light and
/// dark taskbars.</para>
/// <para>State is also carried by a mark on the front page, so it never depends on colour
/// alone: none for idle, an arrow for running, two bars for paused, '!' for attention.
/// Everything is drawn on whole pixels with anti-aliasing off: no gradients, no blur.</para>
/// </remarks>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<(TrayIconState State, bool Ephemeral, int Size), (Icon Icon, nint Handle)> _cache = [];
    private bool _disposed;

    public Icon For(TrayIconState state, bool ephemeral)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var key = (state, ephemeral, size);
        if (!_cache.TryGetValue(key, out var entry))
        {
            using var bitmap = Draw(state, ephemeral, size);
            var handle = bitmap.GetHicon();
            entry = (Icon.FromHandle(handle), handle);
            _cache[key] = entry;
        }
        return entry.Icon;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var (icon, handle) in _cache.Values)
        {
            icon.Dispose();
            AppNative.DestroyIcon(handle);
        }
        _cache.Clear();
    }

    private static Color StateColor(TrayIconState state) => state switch
    {
        TrayIconState.Running => Gridline.Cyan,
        TrayIconState.Paused => Gridline.Amber,
        TrayIconState.Attention => Gridline.Red,
        _ => Gridline.Gray,
    };

    /// <summary>Draws on a 16-unit grid scaled to <paramref name="size"/>, every edge on a whole pixel.</summary>
    private static Bitmap Draw(TrayIconState state, bool ephemeral, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.Clear(Gridline.Transparent);

        var u = size / 16f;
        int P(float units) => (int)Math.Round(units * u);
        Rectangle R(float x, float y, float w, float h) => new(P(x), P(y), Math.Max(1, P(x + w) - P(x)), Math.Max(1, P(y + h) - P(y)));
        var line = Math.Max(1, P(1));

        var stateColor = StateColor(state);
        var tile = ephemeral ? Gridline.Ink : stateColor;
        var frame = ephemeral ? Gridline.Gray : Gridline.Ink;
        var pageFill = ephemeral ? stateColor : Gridline.White;
        var pageLine = ephemeral ? Gridline.White : Gridline.Ink;
        // Ink on cyan, amber and gray, white on red: the pairs with readable contrast.
        var mark = ephemeral && state == TrayIconState.Attention ? Gridline.White : Gridline.Ink;

        Fill(g, tile, new Rectangle(0, 0, size, size));
        Frame(g, frame, new Rectangle(0, 0, size, size), line);

        var back = R(2.5f, 2.5f, 7, 8.5f);
        var front = R(6, 5.5f, 7.5f, 8.5f);
        Fill(g, pageFill, back);
        Frame(g, pageLine, back, line);
        Fill(g, pageFill, front);
        Frame(g, pageLine, front, line);

        switch (state)
        {
            case TrayIconState.Running:
                // Arrow pointing right: shaft and a stepped head.
                Fill(g, mark, R(7.5f, 9.5f, 3.5f, 1));
                Fill(g, mark, R(10, 8, 1, 4));
                Fill(g, mark, R(11, 9, 1, 2));
                break;
            case TrayIconState.Paused:
                Fill(g, mark, R(8, 7.5f, 1.25f, 5));
                Fill(g, mark, R(10.25f, 7.5f, 1.25f, 5));
                break;
            case TrayIconState.Attention:
                Fill(g, mark, R(9.25f, 7, 1.25f, 3.75f));
                Fill(g, mark, R(9.25f, 11.5f, 1.25f, 1.25f));
                break;
            default:
                // Idle: two text lines, so the front page still reads as a document.
                Fill(g, pageLine, R(8, 8.5f, 4, 1));
                Fill(g, pageLine, R(8, 11, 3, 1));
                break;
        }
        return bitmap;
    }

    private static void Fill(Graphics g, Color color, Rectangle r)
    {
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, r);
    }

    /// <summary>A frame drawn as filled strips, so its width is exact at every size.</summary>
    private static void Frame(Graphics g, Color color, Rectangle r, int width)
    {
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, r.X, r.Y, r.Width, width);
        g.FillRectangle(brush, r.X, r.Bottom - width, r.Width, width);
        g.FillRectangle(brush, r.X, r.Y, width, r.Height);
        g.FillRectangle(brush, r.Right - width, r.Y, width, r.Height);
    }
}
