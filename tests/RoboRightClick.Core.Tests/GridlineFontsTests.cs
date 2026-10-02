using System.Buffers.Binary;
using System.Text;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// The family names the UI asks Windows for must be the names Windows reads from the embedded
/// fonts; a wrong name makes Windows substitute another font without any error.
/// </summary>
public class GridlineFontsTests
{
    private const ushort PlatformWindows = 3;
    private const ushort LanguageEnUs = 0x0409;
    private const ushort NameFamily = 1;
    private const ushort NameSubfamily = 2;

    private static IReadOnlyList<(string File, string Family, string Subfamily)> EmbeddedFonts() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fonts"), "*.ttf")
            .Select(path =>
            {
                var names = ReadNames(File.ReadAllBytes(path));
                return (Path.GetFileName(path), names[NameFamily], names[NameSubfamily]);
            })
            .ToList();

    [Fact]
    public void The_embedded_fonts_are_present()
    {
        Assert.Equal(7, EmbeddedFonts().Count);
    }

    [Theory]
    [MemberData(nameof(Cuts))]
    public void Each_cut_names_the_regular_style_of_exactly_one_embedded_font(FontCut cut)
    {
        // The UI asks for each family's Regular style; IBM Plex Sans also has a Bold file.
        var matches = EmbeddedFonts().Where(f => f.Family == GridlineFonts.FamilyName(cut) && f.Subfamily == "Regular");
        Assert.Single(matches);
    }

    [Theory]
    [MemberData(nameof(Cuts))]
    public void Each_cut_is_loaded_from_the_file_that_has_its_family(FontCut cut)
    {
        var font = EmbeddedFonts().Single(f => f.File == GridlineFonts.FileName(cut));
        Assert.Equal(GridlineFonts.FamilyName(cut), font.Family);
        Assert.Equal("Regular", font.Subfamily);
    }

    [Fact]
    public void Mono_cuts_fall_back_to_a_monospaced_font_and_sans_cuts_to_segoe()
    {
        foreach (var cut in GridlineFonts.All)
        {
            Assert.Equal(GridlineFonts.IsMono(cut), GridlineFonts.FallbackFamily(cut) == "Consolas");
            Assert.Equal(GridlineFonts.IsMono(cut), GridlineFonts.FamilyName(cut).StartsWith("IBM Plex Mono", StringComparison.Ordinal));
        }
    }

    public static TheoryData<FontCut> Cuts()
    {
        var data = new TheoryData<FontCut>();
        foreach (var cut in GridlineFonts.All)
        {
            data.Add(cut);
        }
        return data;
    }

    /// <summary>Windows-platform, US-English records of the OpenType 'name' table, by name ID.</summary>
    private static Dictionary<ushort, string> ReadNames(byte[] font)
    {
        var span = font.AsSpan();
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        for (var i = 0; i < tableCount; i++)
        {
            var record = span[(12 + 16 * i)..];
            if (Encoding.ASCII.GetString(record[..4]) != "name")
            {
                continue;
            }
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var table = span[offset..];
            var count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
            var strings = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
            var names = new Dictionary<ushort, string>();
            for (var j = 0; j < count; j++)
            {
                var entry = table[(6 + 12 * j)..];
                var platform = BinaryPrimitives.ReadUInt16BigEndian(entry);
                var language = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);
                var nameId = BinaryPrimitives.ReadUInt16BigEndian(entry[6..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
                var start = BinaryPrimitives.ReadUInt16BigEndian(entry[10..]);
                if (platform == PlatformWindows && language == LanguageEnUs)
                {
                    names[nameId] = Encoding.BigEndianUnicode.GetString(table.Slice(strings + start, length));
                }
            }
            return names;
        }
        throw new InvalidDataException("The font has no name table.");
    }
}
