using System.Buffers.Binary;
using System.Text;

namespace RoboRightClick.Core;

/// <summary>DROPEFFECT values (oleidl.h).</summary>
[Flags]
public enum DropEffect : uint
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

/// <summary>A clipboard format: a predefined CF_* id, or a name the host registers with RegisterClipboardFormat.</summary>
public readonly record struct ClipboardFormat(uint StandardId, string? RegisteredName)
{
    public static ClipboardFormat Standard(uint id) => new(id, null);

    public static ClipboardFormat Registered(string name) => new(0, name);
}

public sealed record ClipboardEntry(ClipboardFormat Format, byte[] Data);

/// <summary>
/// What Robo-Copy and Robo-Cut put on the Windows clipboard, and how Robo-Paste reads
/// it back. The byte layouts live here so they are tested; the host only moves the
/// bytes in and out of HGLOBALs.
/// </summary>
public static class ClipboardPayload
{
    public const uint CF_HDROP = 15;

    /// <summary>Explorer's copy/cut marker; its absence means copy.</summary>
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    /// <summary>
    /// Ephemeral mode only. Any data under this name keeps the content out of clipboard
    /// history, cloud clipboard and clipboard monitors. The two explicit opt-outs below
    /// say the same thing to readers that check only those.
    /// </summary>
    public const string ExcludeFromMonitoringFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    /// <summary>sizeof(DROPFILES): DWORD pFiles, POINT pt, BOOL fNC, BOOL fWide.</summary>
    public const int DropFilesHeaderSize = 20;

    public static IReadOnlyList<ClipboardEntry> ForFiles(IReadOnlyList<string> paths, TransferVerb verb, LoggingMode mode)
    {
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one path is required.", nameof(paths));
        }
        var entries = new List<ClipboardEntry>
        {
            new(ClipboardFormat.Standard(CF_HDROP), EncodeDropFiles(paths)),
            new(ClipboardFormat.Registered(PreferredDropEffectFormat),
                EncodeDropEffect(verb == TransferVerb.Move ? DropEffect.Move : DropEffect.Copy)),
        };
        if (mode == LoggingMode.Ephemeral)
        {
            var zero = new byte[4];
            entries.Add(new(ClipboardFormat.Registered(ExcludeFromMonitoringFormat), zero));
            entries.Add(new(ClipboardFormat.Registered(CanIncludeInHistoryFormat), zero));
            entries.Add(new(ClipboardFormat.Registered(CanUploadToCloudFormat), zero));
        }
        return entries;
    }

    /// <summary>A DROPFILES block with wide (UTF-16) names, double-null terminated.</summary>
    public static byte[] EncodeDropFiles(IReadOnlyList<string> paths)
    {
        var chars = paths.Sum(p => p.Length + 1) + 1;
        var bytes = new byte[DropFilesHeaderSize + chars * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), DropFilesHeaderSize); // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1); // fWide
        var offset = DropFilesHeaderSize;
        foreach (var path in paths)
        {
            if (path.Length == 0 || path.Contains('\0'))
            {
                throw new ArgumentException("Paths must be non-empty and contain no NUL.", nameof(paths));
            }
            offset += Encoding.Unicode.GetBytes(path, bytes.AsSpan(offset));
            offset += 2; // terminator: the array is already zeroed
        }
        return bytes;
    }

    /// <summary>
    /// Reads a wide DROPFILES block. Returns null for the legacy ANSI form, which the
    /// host decodes with DragQueryFileW instead because it depends on the code page.
    /// </summary>
    public static IReadOnlyList<string>? DecodeDropFiles(ReadOnlySpan<byte> data)
    {
        if (data.Length < DropFilesHeaderSize)
        {
            return [];
        }
        var start = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(data[16..]) != 0;
        if (!wide)
        {
            return null;
        }
        if (start < DropFilesHeaderSize || start >= data.Length)
        {
            return [];
        }

        var text = Encoding.Unicode.GetString(data[(int)start..]);
        var paths = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            var end = text.IndexOf('\0', position);
            if (end < 0 || end == position)
            {
                break; // missing terminator, or the empty string that ends the list
            }
            paths.Add(text[position..end]);
            position = end + 1;
        }
        return paths;
    }

    public static byte[] EncodeDropEffect(DropEffect effect)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)effect);
        return bytes;
    }

    public static DropEffect? DecodeDropEffect(ReadOnlySpan<byte> data) =>
        data.Length >= 4 ? (DropEffect)BinaryPrimitives.ReadUInt32LittleEndian(data) : null;

    /// <summary>
    /// The verb a paste performs. Only a pure move marker means move: anything that
    /// also allows copy, or no marker at all, is a copy, so an ambiguous clipboard can
    /// never cause sources to be deleted.
    /// </summary>
    public static TransferVerb VerbForPaste(DropEffect? preferred) =>
        preferred is { } e && e.HasFlag(DropEffect.Move) && !e.HasFlag(DropEffect.Copy)
            ? TransferVerb.Move
            : TransferVerb.Copy;
}
