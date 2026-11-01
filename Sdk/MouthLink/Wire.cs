using System.Buffers.Binary;

namespace UnifiedPicoModule.Sdk.MouthLink;

// MouthLink camera protocol, PC side (port of MouthLink.Core; PROTOCOL.md in the MouthLink repository).
// Little-endian. Control datagrams go to the headset's ctrl port (9040); FRAGMENT datagrams come back
// to the port named in SUBSCRIBE (9041). The headset serves one subscriber; a SUBSCRIBE lease lasts
// 3 s, so it is repeated every second.
internal static class Wire
{
    public const byte Fragment = 0x01, Subscribe = 0x02, Unsubscribe = 0x03, Discover = 0x06, Hello = 0x07;
    public const byte Version = 1;
    public const byte Gray8 = 0x01, Gray16Le = 0x02;
    public const int HeaderSize = 42;

    /// <summary>SUBSCRIBE: frame-rate limit (0 = every frame), source's pixel format, full frame, no STOCK.</summary>
    /// <remarks>The headset keeps a source-clock grid (ml2+): 60, 45 or 30 from a 90 Hz camera are exact.</remarks>
    public static byte[] SubscribePacket(ushort replyPort, byte fps = 0)
    {
        var p = new byte[16];
        p[0] = Subscribe; p[1] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), replyPort);
        p[4] = fps;
        return p;
    }

    public static readonly byte[] UnsubscribePacket = [Unsubscribe, Version];
    public static readonly byte[] DiscoverPacket = [Discover, Version];

    public static bool IsHello(ReadOnlySpan<byte> p) => p.Length >= 8 && p[0] == Hello;

    public struct Header
    {
        public byte PixFmt, Compression;
        public uint FrameId;
        public ulong CaptureNs;
        public ushort Width, Height, RoiX, RoiY;
        public uint PayloadLen;
        public ushort FragIndex, FragCount;
        public uint FragOffset;
        public ushort FragLen;
    }

    public static bool TryParse(ReadOnlySpan<byte> s, out Header h)
    {
        h = default;
        if (s.Length < HeaderSize || s[0] != Fragment || s[1] != Version) return false;
        if (Crc32(s[..38]) != BinaryPrimitives.ReadUInt32LittleEndian(s[38..])) return false;
        h.PixFmt = s[2]; h.Compression = s[3];
        h.FrameId = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        h.CaptureNs = BinaryPrimitives.ReadUInt64LittleEndian(s[8..]);
        h.Width = BinaryPrimitives.ReadUInt16LittleEndian(s[16..]);
        h.Height = BinaryPrimitives.ReadUInt16LittleEndian(s[18..]);
        h.RoiX = BinaryPrimitives.ReadUInt16LittleEndian(s[20..]);
        h.RoiY = BinaryPrimitives.ReadUInt16LittleEndian(s[22..]);
        h.PayloadLen = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
        h.FragIndex = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
        h.FragCount = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
        h.FragOffset = BinaryPrimitives.ReadUInt32LittleEndian(s[32..]);
        h.FragLen = BinaryPrimitives.ReadUInt16LittleEndian(s[36..]);
        return true;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}

/// <summary>A complete camera frame as sent (payload still in the wire pixel format).</summary>
internal sealed class CameraFrame
{
    public uint FrameId;
    public ulong CaptureNs;
    public byte PixFmt, Compression;
    public int Width, Height, RoiX, RoiY;
    public byte[] Payload = [];
}

/// <summary>Joins FRAGMENT datagrams into frames; keeps a few partial frames, drops older ones.</summary>
internal sealed class FrameAssembler
{
    private sealed class Partial
    {
        public required byte[] Buffer;
        public required bool[] Got;
        public int Remaining;
        public Wire.Header Head;
    }

    private const int Keep = 8;
    private const uint MaxPayload = 4 << 20;   // the headset never sends more (kMaxFrameBytes)
    private readonly Dictionary<uint, Partial> pending = new();
    public long Completed, Dropped, Bad;

    public CameraFrame? Feed(ReadOnlySpan<byte> d)
    {
        if (!Wire.TryParse(d, out var h)) { Bad++; return null; }
        if (h.FragCount == 0 || h.FragIndex >= h.FragCount || h.PayloadLen == 0 || h.PayloadLen > MaxPayload ||
            d.Length < Wire.HeaderSize + h.FragLen || (long)h.FragOffset + h.FragLen > h.PayloadLen)
        { Bad++; return null; }
        if (!pending.TryGetValue(h.FrameId, out var p))
        {
            if (pending.Count >= Keep)
            {
                uint oldest = pending.Keys.MaxBy(k => h.FrameId - k);
                pending.Remove(oldest); Dropped++;
            }
            p = new Partial { Buffer = new byte[h.PayloadLen], Got = new bool[h.FragCount], Remaining = h.FragCount, Head = h };
            pending[h.FrameId] = p;
        }
        else if (p.Head.FragCount != h.FragCount || p.Head.PayloadLen != h.PayloadLen)
        {
            pending.Remove(h.FrameId); Bad++; return null;
        }
        if (p.Got[h.FragIndex]) return null;
        d.Slice(Wire.HeaderSize, h.FragLen).CopyTo(p.Buffer.AsSpan((int)h.FragOffset));
        p.Got[h.FragIndex] = true;
        if (--p.Remaining > 0) return null;
        pending.Remove(h.FrameId);
        // Partial frames older than a completed one are no longer useful.
        foreach (var k in pending.Keys.Where(k => h.FrameId - k < 0x8000_0000u).ToArray()) { pending.Remove(k); Dropped++; }
        Completed++;
        return new CameraFrame
        {
            FrameId = h.FrameId, CaptureNs = h.CaptureNs, PixFmt = h.PixFmt, Compression = h.Compression,
            Width = h.Width, Height = h.Height, RoiX = h.RoiX, RoiY = h.RoiY, Payload = p.Buffer
        };
    }
}
