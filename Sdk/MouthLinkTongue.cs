using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UnifiedPicoModule.Sdk;

// Tongue direction from MouthLink live inference running on this PC (tools/mouthlink_live.py in
// the MouthLink repository). Loopback UDP only: 127.0.0.1:9050, one "MLK1" datagram per camera
// frame. Without packets, or with stale ones, the four direction channels are written as 0 and
// every other channel is left exactly as UnifiedPicoModule produced it.
//
// MLK1 v1, little-endian:
//   0 "MLK1" | 4 u16 version=1 | 6 u16 count (>= 5) | 8 u32 seq | 12 u64 source_ns
//   20 f32[count]: TongueLeft, TongueRight, TongueUp, TongueDown, TongueOut (model); all in [0, 1]
internal sealed class MouthLinkTongue : IDisposable
{
    public const int DefaultPort = 9050;
    public const double FreshMs = 150;
    public const int MinValues = 5;

    public readonly record struct Directions(bool Fresh, float Left, float Right, float Up, float Down, float Gate);

    private readonly UdpClient socket;
    private readonly Action<string> log;
    private readonly float[] values = new float[MinValues];
    private long received, lastReport, packets, rejected, packetsAtReport;
    private uint sequence;
    private Directions last;

    public MouthLinkTongue(Action<string> logger, int port = DefaultPort)
    {
        log = logger;
        socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        socket.Client.Blocking = false;
        log($"MouthLink tongue: listening on 127.0.0.1:{port} (TongueLeft/Right/Up/Down, gated by stock TongueOut).");
    }

    /// <summary>Parses one datagram. values receives the first five floats.</summary>
    public static bool TryParse(ReadOnlySpan<byte> p, out uint seq, out ulong sourceNs, Span<float> values)
    {
        seq = 0; sourceNs = 0;
        if (p.Length < 20 || !p[..4].SequenceEqual("MLK1"u8)) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) != 1) return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
        if (count < MinValues || p.Length != 20 + 4 * count) return false;
        for (int i = 0; i < MinValues; i++)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(p[(20 + 4 * i)..]);
            if (!float.IsFinite(v) || v < 0 || v > 1) return false;
            values[i] = v;
        }
        seq = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        sourceNs = BinaryPrimitives.ReadUInt64LittleEndian(p[12..]);
        return true;
    }

    /// <summary>Directions only appear while stock tracking sees the tongue out: 0 below 0.05,
    /// full from 0.20. The image model never creates tongue extension on its own.</summary>
    public static float GateFor(float stockTongueOut) =>
        float.IsFinite(stockTongueOut) ? Math.Clamp((stockTongueOut - 0.05f) / 0.15f, 0f, 1f) : 0f;

    public Directions Poll(float stockTongueOut)
    {
        for (int i = 0; i < 256; i++)
        {
            byte[] p; IPEndPoint? from = null;
            try { if (socket.Available <= 0) break; p = socket.Receive(ref from); }
            catch (SocketException) { break; }
            if (from is null || !IPAddress.IsLoopback(from.Address) || !TryParse(p, out var seq, out _, values))
            { rejected++; continue; }
            sequence = seq; received = Stopwatch.GetTimestamp(); packets++;
        }
        long now = Stopwatch.GetTimestamp();
        bool fresh = received != 0 && Stopwatch.GetElapsedTime(received, now).TotalMilliseconds < FreshMs;
        float gate = GateFor(stockTongueOut);
        last = fresh
            ? new Directions(true, values[0] * gate, values[1] * gate, values[2] * gate, values[3] * gate, gate)
            : new Directions(false, 0, 0, 0, 0, gate);
        if (lastReport == 0) { lastReport = now; packetsAtReport = packets; }
        else if (Stopwatch.GetElapsedTime(lastReport, now).TotalSeconds >= 5)
        {
            double seconds = Stopwatch.GetElapsedTime(lastReport, now).TotalSeconds;
            log($"MouthLink tongue: rx={(packets - packetsAtReport) / seconds:F1}Hz fresh={last.Fresh} seq={sequence} " +
                $"gate={gate:F2} L={last.Left:F2} R={last.Right:F2} U={last.Up:F2} D={last.Down:F2} rejected={rejected}");
            lastReport = now; packetsAtReport = packets;
        }
        return last;
    }

    public void Dispose() => socket.Dispose();
}
