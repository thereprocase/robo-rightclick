namespace RoboRightClick.Core;

/// <summary>The IBM Plex cuts the Gridline UI draws with (docs/gridline.md, Typography).</summary>
public enum FontCut
{
    Sans,
    SansMedium,
    SansSemiBold,
    Mono,
    MonoMedium,
    MonoSemiBold,
}

/// <summary>
/// The family names Windows gives each embedded Plex cut, and the system fallback for each.
/// </summary>
/// <remarks>
/// GDI and GDI+ know a font by its legacy family name (name ID 1), not by the typographic
/// family ("IBM Plex Sans") and style ("Medium") newer software shows. Plex abbreviates the
/// legacy names of its in-between weights ("IBM Plex Sans Medm", "IBM Plex Sans SmBld") and
/// gives each its own family with a single Regular style. A request for "IBM Plex Sans Medium"
/// matches nothing and Windows quietly substitutes another font, so these names are checked
/// against the embedded files by a test.
/// </remarks>
public static class GridlineFonts
{
    public static IReadOnlyList<FontCut> All { get; } = Enum.GetValues<FontCut>();

    /// <summary>The legacy family name (name ID 1) of the embedded file for <paramref name="cut"/>; its style is Regular.</summary>
    public static string FamilyName(FontCut cut) => cut switch
    {
        FontCut.Sans => "IBM Plex Sans",
        FontCut.SansMedium => "IBM Plex Sans Medm",
        FontCut.SansSemiBold => "IBM Plex Sans SmBld",
        FontCut.Mono => "IBM Plex Mono",
        FontCut.MonoMedium => "IBM Plex Mono Medm",
        FontCut.MonoSemiBold => "IBM Plex Mono SmBld",
        _ => throw new ArgumentOutOfRangeException(nameof(cut)),
    };

    public static bool IsMono(FontCut cut) => cut is FontCut.Mono or FontCut.MonoMedium or FontCut.MonoSemiBold;

    /// <summary>The system font used when the Plex cut cannot be loaded: Segoe UI for Sans, Consolas for Mono.</summary>
    public static string FallbackFamily(FontCut cut) => cut switch
    {
        FontCut.SansSemiBold => "Segoe UI Semibold",
        _ when IsMono(cut) => "Consolas",
        _ => "Segoe UI",
    };

    /// <summary>Consolas has no semibold; its bold stands in for Mono SemiBold so captions keep their weight.</summary>
    public static bool FallbackBold(FontCut cut) => cut == FontCut.MonoSemiBold;
}
