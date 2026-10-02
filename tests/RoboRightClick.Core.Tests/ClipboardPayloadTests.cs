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
        Assert.Equal(paths, ClipboardPayload.DecodeDropFiles(bytes));
    }

    [Fact]
    public void Ansi_drop_files_are_left_to_the_host()
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 20);
        Assert.Null(ClipboardPayload.DecodeDropFiles(bytes));
    }

    [Fact]
    public void Truncated_drop_files_do_not_throw()
    {
        Assert.Empty(ClipboardPayload.DecodeDropFiles(new byte[3])!);
        var bytes = ClipboardPayload.EncodeDropFiles([@"C:\a"]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4000);
        Assert.Empty(ClipboardPayload.DecodeDropFiles(bytes)!);
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
}
