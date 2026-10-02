using RoboRightClick.Core;

namespace RoboRightClick.App;

/// <summary>
/// Tray icons drawn at runtime with GDI+, so the repository needs no binary assets:
/// one glyph per <see cref="TrayIconState"/> (idle, running, paused, attention), each in
/// a normal and a distinct ephemeral tint, at the size the shell asks for
/// (SystemInformation.SmallIconSize, which follows DPI). Icons are created once and
/// cached; Dispose destroys their HICONs (Icon.FromHandle does not own them).
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    public Icon For(TrayIconState state, bool ephemeral) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
