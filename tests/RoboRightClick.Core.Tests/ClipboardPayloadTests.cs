using System.Buffers.Binary;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class ClipboardPayloadTests
{
    [Fact]
    public void Drop_files_round_trip_with_non_ascii_names()
    {
        string[] paths = [@"C:\src\日本語 📁", @"\\srv\share\ü.txt"];
        var bytes = ClipboardPayload.EncodeDropFiles(paths);

        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)));
        // Double-NUL terminated list.
        Assert.Equal([0, 0, 0, 0], bytes[^4..]);
        var decoded = ClipboardPayload.DecodeDropFiles(bytes);
        Assert.Equal(DropFilesStatus.Ok, decoded.Status);
        Assert.Equal(paths, decoded.Paths);
    }

    [Fact]
    public void Ansi_drop_files_are_left_to_the_host()
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 20);
        Assert.Equal(DropFilesStatus.Ansi, ClipboardPayload.DecodeDropFiles(bytes).Status);
    }

    [Fact]
    public void Truncated_drop_files_do_not_throw()
    {
        Assert.Empty(ClipboardPayload.DecodeDropFiles(new byte[3]).Paths);
        var bytes = ClipboardPayload.EncodeDropFiles([@"C:\a"]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4000);
        Assert.Empty(ClipboardPayload.DecodeDropFiles(bytes).Paths);
    }

    [Fact]
    public void A_final_name_without_its_terminator_refuses_the_whole_block()
    {
        // Two names, then the second one's terminator and the list terminator cut off: the
        // first name alone must not be pasted as if it were the whole selection.
        var full = ClipboardPayload.EncodeDropFiles([@"C:\a", @"C:\bb"]);
        var cut = full[..^4];
        var result = ClipboardPayload.DecodeDropFiles(cut);
        Assert.Equal(DropFilesStatus.Ok, result.Status);
        Assert.Empty(result.Paths);
    }

    [Fact]
    public void A_list_missing_only_its_final_empty_string_still_decodes()
    {
        var full = ClipboardPayload.EncodeDropFiles([@"C:\a", @"C:\bb"]);
        Assert.Equal([@"C:\a", @"C:\bb"], ClipboardPayload.DecodeDropFiles(full[..^2]).Paths);
    }

    [Theory]
    [InlineData(null, TransferVerb.Copy)]
    [InlineData(DropEffect.Copy, TransferVerb.Copy)]
    [InlineData(DropEffect.Copy | DropEffect.Link, TransferVerb.Copy)]
    [InlineData(DropEffect.Move, TransferVerb.Move)]
    [InlineData(DropEffect.Copy | DropEffect.Move, TransferVerb.Copy)]
    [InlineData(DropEffect.None, TransferVerb.Copy)]
    public void Only_a_pure_move_marker_pastes_as_a_move(DropEffect? effect, TransferVerb expected)
    {
        Assert.Equal(expected, ClipboardPayload.VerbForPaste(effect));
    }

    [Fact]
    public void Drop_effect_is_a_little_endian_dword()
    {
        Assert.Equal([2, 0, 0, 0], ClipboardPayload.EncodeDropEffect(DropEffect.Move));
        Assert.Equal(DropEffect.Move, ClipboardPayload.DecodeDropEffect([2, 0, 0, 0]));
        Assert.Null(ClipboardPayload.DecodeDropEffect([2]));
    }

    [Fact]
    public void Ephemeral_writes_add_the_history_exclusions_and_normal_writes_do_not()
    {
        static IReadOnlyList<string?> Names(LoggingMode mode) =>
            ClipboardPayload.ForFiles([@"C:\a"], TransferVerb.Move, mode).Select(e => e.Format.RegisteredName).ToList();

        Assert.DoesNotContain(ClipboardPayload.ExcludeFromMonitoringFormat, Names(LoggingMode.Normal));
        Assert.Contains(ClipboardPayload.ExcludeFromMonitoringFormat, Names(LoggingMode.Ephemeral));
        Assert.Contains(ClipboardPayload.CanIncludeInHistoryFormat, Names(LoggingMode.Ephemeral));
        Assert.Contains(ClipboardPayload.CanUploadToCloudFormat, Names(LoggingMode.Ephemeral));
    }

    [Fact]
    public void Cut_writes_move_and_copy_writes_copy()
    {
        DropEffect? EffectFor(TransferVerb verb) => ClipboardPayload.DecodeDropEffect(
            ClipboardPayload.ForFiles([@"C:\a"], verb, LoggingMode.Normal)
                .Single(e => e.Format.RegisteredName == ClipboardPayload.PreferredDropEffectFormat).Data);

        Assert.Equal(DropEffect.Move, EffectFor(TransferVerb.Move));
        Assert.Equal(DropEffect.Copy, EffectFor(TransferVerb.Copy));
        Assert.Equal(
            ClipboardPayload.CF_HDROP,
            ClipboardPayload.ForFiles([@"C:\a"], TransferVerb.Copy, LoggingMode.Normal)[0].Format.StandardId);
    }

    /// <summary>A CIDA: cidl, offsets, the parent list, then each item list (one ID each).</summary>
    private static byte[] Cida(int items, int? declared = null)
    {
        var lists = new List<byte[]> { new byte[] { 0, 0 } }; // parent: the desktop, an empty list
        for (var i = 0; i < items; i++)
        {
            lists.Add([5, 0, (byte)'a', (byte)'b', (byte)i, 0, 0]);
        }
        var header = 4 + 4 * lists.Count;
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((uint)(declared ?? items)));
        var offset = header;
        foreach (var list in lists)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)offset));
            offset += list.Length;
        }
        foreach (var list in lists)
        {
            bytes.AddRange(list);
        }
        return [.. bytes];
    }

    [Fact]
    public void A_well_formed_shell_id_list_for_the_same_items_is_written_after_the_drop_list()
    {
        var entries = ClipboardPayload.ForFiles([@"C:", @"C:"], TransferVerb.Copy, LoggingMode.Normal, Cida(2));
        Assert.Equal(ClipboardPayload.CF_HDROP, entries[0].Format.StandardId);
        Assert.Equal(Cida(2), entries.Single(e => e.Format.RegisteredName == ClipboardPayload.ShellIdListFormat).Data);
    }

    [Fact]
    public void No_shell_id_list_is_written_without_one()
    {
        Assert.DoesNotContain(
            ClipboardPayload.ForFiles([@"C:"], TransferVerb.Copy, LoggingMode.Normal),
            e => e.Format.RegisteredName == ClipboardPayload.ShellIdListFormat);
    }

    [Fact]
    public void A_shell_id_list_for_a_different_number_of_items_is_not_written()
    {
        Assert.False(ClipboardPayload.IsShellIdListFor(Cida(3), 2));
        Assert.DoesNotContain(
            ClipboardPayload.ForFiles([@"C:", @"C:"], TransferVerb.Copy, LoggingMode.Normal, Cida(3)),
            e => e.Format.RegisteredName == ClipboardPayload.ShellIdListFormat);
    }

    [Fact]
    public void A_shell_id_list_whose_count_claims_more_offsets_than_fit_is_refused()
    {
        Assert.False(ClipboardPayload.IsShellIdListFor(Cida(1, declared: 100_000), 100_000));
    }

    [Fact]
    public void A_shell_id_list_with_an_offset_outside_the_block_or_inside_the_table_is_refused()
    {
        var outside = Cida(1);
        BitConverter.GetBytes((uint)outside.Length).CopyTo(outside, 8);
        Assert.False(ClipboardPayload.IsShellIdListFor(outside, 1));

        // Offset 2 lands on the zero high bytes of cidl, which read as an empty list: only
        // the rule that lists start after the offset table refuses it.
        var intoTable = Cida(1);
        BitConverter.GetBytes(2u).CopyTo(intoTable, 8);
        Assert.False(ClipboardPayload.IsShellIdListFor(intoTable, 1));
    }

    [Fact]
    public void A_shell_id_list_with_an_unterminated_or_non_advancing_id_is_refused()
    {
        Assert.True(ClipboardPayload.IsShellIdListFor(Cida(1), 1));

        var unterminated = Cida(1)[..^2];
        Assert.False(ClipboardPayload.IsShellIdListFor(unterminated, 1));

        // The item list is { cb = 1, 0 }: a 1-byte ID cannot hold its own size. Stepping one
        // byte would land on a zero cb and accept it, so only the cb >= 2 rule refuses it.
        var header = 4 + 4 * 2;
        var stuck = new List<byte>();
        stuck.AddRange(BitConverter.GetBytes(1u));
        stuck.AddRange(BitConverter.GetBytes((uint)header));
        stuck.AddRange(BitConverter.GetBytes((uint)header + 2));
        stuck.AddRange(new byte[] { 0, 0, 1, 0, 0 });
        Assert.False(ClipboardPayload.IsShellIdListFor(stuck.ToArray(), 1));
    }

    [Fact]
    public void A_shell_id_list_with_too_many_ids_in_one_list_is_refused()
    {
        var ids = new List<byte>();
        for (var i = 0; i <= ClipboardPayload.MaxItemIdsPerList; i++)
        {
            ids.AddRange(new byte[] { 2, 0 });
        }
        ids.AddRange(new byte[] { 0, 0 });
        var header = 4 + 4 * 2;
        var block = new List<byte>();
        block.AddRange(BitConverter.GetBytes(1u));
        block.AddRange(BitConverter.GetBytes((uint)header));
        block.AddRange(BitConverter.GetBytes((uint)header));
        block.AddRange(ids);
        Assert.False(ClipboardPayload.IsShellIdListFor(block.ToArray(), 1));
    }

    [Fact]
    public void Oversized_clipboard_data_is_refused_not_truncated()
    {
        var huge = new byte[ClipboardPayload.MaxDropFilesBytes + 1];
        Assert.Equal(DropFilesStatus.TooLarge, ClipboardPayload.DecodeDropFiles(huge).Status);

        var tooMany = ClipboardPayload.EncodeDropFiles(Enumerable.Repeat(@"C:\a", ClipboardPayload.MaxDropFilesPaths + 1).ToList());
        var result = ClipboardPayload.DecodeDropFiles(tooMany);
        Assert.Equal(DropFilesStatus.TooLarge, result.Status);
        Assert.Empty(result.Paths);

        var atLimit = ClipboardPayload.EncodeDropFiles(Enumerable.Repeat(@"C:\a", ClipboardPayload.MaxDropFilesPaths).ToList());
        Assert.Equal(ClipboardPayload.MaxDropFilesPaths, ClipboardPayload.DecodeDropFiles(atLimit).Paths.Count);
    }
}
