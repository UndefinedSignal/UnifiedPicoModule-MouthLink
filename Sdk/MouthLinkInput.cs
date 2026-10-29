using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnifiedPicoModule.Core;
using UnifiedPicoModule.Mapping;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace UnifiedPicoModule.Sdk;

// Mouth values from MouthLink live inference on this PC (tools/mouthlink_live.py in the MouthLink
// repository): a model trained on the user's own mouth-camera frames. Loopback UDP only,
// 127.0.0.1:9050, one datagram per camera frame.
//
// Channels are named by the model, never by position:
// - the 33 stock mouth shapes (CheekPuff, MouthSmileL, JawOpen, TongueOut, ...) use the PICO names and
//   reach VRCFT through the same neutral mapping as stock (NeutralFaceMapper);
// - channels stock does not have go to the VRCFT shape of the same name: TongueLeft/Right/Up/Down,
//   TongueRoll (both gated by the TongueOut finally written), CheekPuffLeft/Right, CheekSuckLeft/Right,
//   MouthTightenerLeft/Right.
// Per-channel mode (model / stock / blend:w / max) comes from MouthLink.json next to this DLL, re-read on
// change. Model values bypass UnifiedPicoModule post-processing rules. Eye channels are never touched.
// Without fresh packets, stock channels keep the UnifiedPicoModule values and new channels are written 0.
//
// MLK2, little-endian:
//   0 "MLK2" | 4 u16 version=2 | 6 u16 count | 8 u32 seq | 12 u64 source_ns | 20 u16 names_len
//   22 names: UTF-8, count names separated by ',' | 22+names_len f32[count], each in [0, 1]
// MLK1 (tongue only, earlier builds of mouthlink_live.py) is still accepted:
//   0 "MLK1" | 4 u16 version=1 | 6 u16 count (>= 5) | 8 u32 seq | 12 u64 source_ns
//   20 f32[count]: TongueLeft, TongueRight, TongueUp, TongueDown, TongueOut (ignored)
internal sealed class MouthLinkInput : IDisposable
{
    public const string FileName = "MouthLink.json";
    private const int MaxChannels = 256;

    public enum ModeKind { Model, Stock, Blend, Max }
    public readonly record struct Mode(ModeKind Kind, float Weight)
    {
        public static bool TryParse(string? text, out Mode mode)
        {
            mode = new(ModeKind.Model, 1);
            var t = (text ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "model": return true;
                case "stock": mode = new(ModeKind.Stock, 0); return true;
                case "max": mode = new(ModeKind.Max, 1); return true;
            }
            if (t.StartsWith("blend:") && float.TryParse(t[6..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w) && w is >= 0 and <= 1)
            {
                mode = new(ModeKind.Blend, w); return true;
            }
            return false;
        }
        public float Apply(float stock, float model) => Kind switch
        {
            ModeKind.Model => model,
            ModeKind.Blend => stock * (1 - Weight) + model * Weight,
            ModeKind.Max => Math.Max(stock, model),
            _ => stock
        };
    }

    public sealed class Settings
    {
        public bool Enabled { get; set; } = true;
        public int Port { get; set; } = 9050;
        public double FreshMs { get; set; } = 150;
        public string DefaultMode { get; set; } = "model";
        public Dictionary<string, string> Modes { get; set; } = new();
        public float TongueGateFrom { get; set; } = 0.05f;
        public float TongueGateFull { get; set; } = 0.20f;
    }

    // One model channel: VRCFT targets; New = stock has no such channel; Gated = needs TongueOut up.
    public sealed record Target(string Name, UnifiedExpressions[] Shapes, bool New, bool Gated);

    public static readonly IReadOnlyDictionary<string, Target> Targets = BuildTargets();
    private static readonly string[] Mlk1Names = ["TongueLeft", "TongueRight", "TongueUp", "TongueDown", ""];

    private static Dictionary<string, Target> BuildTargets()
    {
        var map = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var (input, outputs) in NeutralFaceMapper.Bindings)
        {
            if (RawFace.IsEyeShape(input)) continue;
            map[input.ToString()] = new(input.ToString(),
                outputs.Select(c => Enum.Parse<UnifiedExpressions>(c.ToString(), false)).ToArray(), false, false);
        }
        foreach (var name in new[] { "TongueLeft", "TongueRight", "TongueUp", "TongueDown", "TongueRoll" })
            map[name] = new(name, [Enum.Parse<UnifiedExpressions>(name, false)], true, true);
        foreach (var name in new[] { "CheekPuffLeft", "CheekPuffRight", "CheekSuckLeft", "CheekSuckRight",
                     "MouthTightenerLeft", "MouthTightenerRight" })
            map[name] = new(name, [Enum.Parse<UnifiedExpressions>(name, false)], true, false);
        return map;
    }

    private readonly Action<string> log;
    private readonly string configPath;
    private Settings settings = new();
    private Mode defaultMode = new(ModeKind.Model, 1);
    private Dictionary<string, Mode> modes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime configStamp;
    private long configChecked;
    private UdpClient? socket;
    private int boundPort;

    private byte[] layoutKey = [];
    private Target?[] layout = [];
    private string[] layoutNames = [];
    private Dictionary<string, int> layoutIndex = new(StringComparer.Ordinal);
    private readonly float[] values = new float[MaxChannels];
    private int count;
    private long received, lastReport, packets, rejected, packetsAtReport;
    private uint sequence;
    private float lastGate;
    private bool lastFresh;

    public MouthLinkInput(Action<string> logger, string directory)
    {
        log = logger;
        configPath = Path.Combine(directory, FileName);
        LoadSettings(initial: true);
        Bind();
    }

    private void Bind()
    {
        socket?.Dispose(); socket = null; boundPort = 0;
        if (!settings.Enabled) { log("MouthLink: disabled in " + FileName + "."); return; }
        try
        {
            socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, settings.Port));
            socket.Client.Blocking = false; boundPort = settings.Port;
            log($"MouthLink ml2: listening on 127.0.0.1:{settings.Port}; default mode {settings.DefaultMode}, " +
                $"{modes.Count} per-channel modes; config {configPath}.");
        }
        catch (Exception ex) { log($"MouthLink input unavailable on 127.0.0.1:{settings.Port}: {ex.Message}"); }
    }

    private void LoadSettings(bool initial)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                if (initial)
                {
                    try
                    {
                        using var f = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write);
                        f.Write(Encoding.UTF8.GetBytes(DefaultConfigText));
                        log($"MouthLink: wrote default {configPath}.");
                    }
                    catch (IOException) { }
                }
                configStamp = File.Exists(configPath) ? File.GetLastWriteTimeUtc(configPath) : default;
                return;
            }
            configStamp = File.GetLastWriteTimeUtc(configPath);
            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true
            };
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(configPath), options) ?? new Settings();
            if (!Mode.TryParse(s.DefaultMode, out var dm))
            {
                log($"MouthLink: DefaultMode \"{s.DefaultMode}\" is not model|stock|max|blend:W; using model.");
                dm = new(ModeKind.Model, 1);
            }
            var m = new Dictionary<string, Mode>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, text) in s.Modes ?? new())
            {
                if (!Targets.ContainsKey(name)) log($"MouthLink: unknown channel \"{name}\" in Modes (ignored).");
                else if (!Mode.TryParse(text, out var mode)) log($"MouthLink: mode \"{text}\" for {name} is not model|stock|max|blend:W (ignored).");
                else m[name] = mode;
            }
            if (!(s.TongueGateFrom >= 0 && s.TongueGateFull > s.TongueGateFrom && s.TongueGateFull <= 1))
            {
                log("MouthLink: need 0 <= TongueGateFrom < TongueGateFull <= 1; using 0.05 / 0.20.");
                s.TongueGateFrom = 0.05f; s.TongueGateFull = 0.20f;
            }
            if (!(s.FreshMs > 0)) s.FreshMs = 150;
            settings = s; defaultMode = dm; modes = m;
            if (!initial) log($"MouthLink: reloaded {FileName}: enabled={s.Enabled} default={s.DefaultMode} " +
                              $"modes={string.Join(", ", m.Select(kv => kv.Key + "=" + Describe(kv.Value)))}.");
        }
        catch (Exception ex)
        {
            log($"MouthLink: cannot read {configPath}: {ex.Message}; keeping previous settings.");
        }
    }

    private static string Describe(Mode m) => m.Kind == ModeKind.Blend
        ? FormattableString.Invariant($"blend:{m.Weight:0.##}") : m.Kind.ToString().ToLowerInvariant();

    private void CheckConfig(long now)
    {
        if (configChecked != 0 && Stopwatch.GetElapsedTime(configChecked, now).TotalSeconds < 1) return;
        configChecked = now;
        DateTime stamp;
        try { stamp = File.Exists(configPath) ? File.GetLastWriteTimeUtc(configPath) : default; }
        catch { return; }
        if (stamp == configStamp) return;
        bool wasEnabled = settings.Enabled; int port = settings.Port;
        LoadSettings(initial: false);
        if (settings.Enabled != wasEnabled || settings.Port != port || (settings.Enabled && socket is null)) Bind();
    }

    public Mode ModeFor(string name) => modes.TryGetValue(name, out var m) ? m : defaultMode;

    /// <summary>Parses one datagram into names (only when the layout changed) and values.</summary>
    public static bool TryParse(ReadOnlySpan<byte> p, out uint seq, out ReadOnlySpan<byte> names, out bool v1,
        Span<float> values, out int count)
    {
        seq = 0; names = default; v1 = false; count = 0;
        if (p.Length < 20) return false;
        int offset;
        if (p[..4].SequenceEqual("MLK1"u8))
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) != 1) return false;
            count = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
            if (count < 5 || p.Length != 20 + 4 * count) return false;
            count = 5; v1 = true; offset = 20;
        }
        else if (p[..4].SequenceEqual("MLK2"u8))
        {
            if (p.Length < 22 || BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) != 2) return false;
            count = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
            int namesLen = BinaryPrimitives.ReadUInt16LittleEndian(p[20..]);
            if (count < 1 || count > MaxChannels || count > values.Length || p.Length != 22 + namesLen + 4 * count) return false;
            names = p.Slice(22, namesLen); offset = 22 + namesLen;
        }
        else return false;
        for (int i = 0; i < count; i++)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(p[(offset + 4 * i)..]);
            if (!float.IsFinite(v) || v < -0.001f || v > 1.001f) return false;
            values[i] = Math.Clamp(v, 0f, 1f);
        }
        seq = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        return true;
    }

    private bool SetLayout(ReadOnlySpan<byte> names, bool v1, int n)
    {
        ReadOnlySpan<byte> key = v1 ? "\u0001MLK1"u8 : names;
        if (key.SequenceEqual(layoutKey) && layout.Length == n) return true;
        string[] list = v1 ? Mlk1Names : Encoding.UTF8.GetString(names).Split(',');
        if (list.Length != n) return false;
        layoutKey = key.ToArray(); layoutNames = list;
        layout = list.Select(x => Targets.TryGetValue(x.Trim(), out var t) ? t : null).ToArray();
        layoutIndex = new(StringComparer.Ordinal);
        for (int i = 0; i < layout.Length; i++) if (layout[i] is { } t) layoutIndex.TryAdd(t.Name, i);
        var ignored = list.Where((x, i) => layout[i] is null && x.Length > 0).ToArray();
        log($"MouthLink: layout {(v1 ? "MLK1 (tongue only)" : "MLK2")}: {n} channels, {layout.Count(t => t is not null)} used" +
            (ignored.Length > 0 ? $", ignored: {string.Join(", ", ignored)}" : "") + ".");
        return true;
    }

    private void Receive()
    {
        if (socket is null) return;
        Span<float> tmp = stackalloc float[MaxChannels];
        for (int i = 0; i < 256; i++)
        {
            byte[] p; IPEndPoint? from = null;
            try { if (socket.Available <= 0) break; p = socket.Receive(ref from); }
            catch (SocketException) { break; }
            if (from is null || !IPAddress.IsLoopback(from.Address) ||
                !TryParse(p, out var seq, out var names, out var v1, tmp, out var n) || !SetLayout(names, v1, n))
            { rejected++; continue; }
            tmp[..n].CopyTo(values); count = n;
            sequence = seq; received = Stopwatch.GetTimestamp(); packets++;
        }
    }

    public float GateFor(float tongueOut) => float.IsFinite(tongueOut)
        ? Math.Clamp((tongueOut - settings.TongueGateFrom) / (settings.TongueGateFull - settings.TongueGateFrom), 0f, 1f)
        : 0f;

    /// <summary>Applies the latest model values on top of what UnifiedPicoModule just wrote.</summary>
    public void Apply(UnifiedExpressionShape[] shapes)
    {
        long now = Stopwatch.GetTimestamp();
        CheckConfig(now);
        if (!settings.Enabled) return;
        Receive();
        bool fresh = received != 0 && count == layout.Length &&
                     Stopwatch.GetElapsedTime(received, now).TotalMilliseconds < settings.FreshMs;
        if (fresh)
        {
            // Stock-space channels first, so new channels (CheekPuffLeft/Right) win over the CheekPuff mapping.
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < count; i++)
                {
                    var t = layout[i];
                    if (t is null || t.New != (pass == 1) || t.Gated) continue;
                    var mode = ModeFor(t.Name);
                    if (mode.Kind == ModeKind.Stock) continue;
                    foreach (var s in t.Shapes)
                        shapes[(int)s].Weight = Math.Clamp(mode.Apply(t.New ? 0 : shapes[(int)s].Weight, values[i]), 0f, 1f);
                }
        }
        float gate = GateFor(shapes[(int)UnifiedExpressions.TongueOut].Weight);
        // New channels this input owns: fresh → model (tongue ones gated); stale → 0. Mode "stock" leaves them alone.
        foreach (var t in Targets.Values)
        {
            if (!t.New || ModeFor(t.Name).Kind == ModeKind.Stock) continue;
            bool present = layoutIndex.TryGetValue(t.Name, out int i);
            if (fresh && present && !t.Gated) continue;   // written above
            float v = fresh && present ? ModeFor(t.Name).Apply(0, values[i]) * gate : 0;
            foreach (var s in t.Shapes) shapes[(int)s].Weight = Math.Clamp(v, 0f, 1f);
        }
        lastGate = gate; lastFresh = fresh;
        Report(now, shapes);
    }

    private void Report(long now, UnifiedExpressionShape[] shapes)
    {
        if (lastReport == 0) { lastReport = now; packetsAtReport = packets; return; }
        double seconds = Stopwatch.GetElapsedTime(lastReport, now).TotalSeconds;
        if (seconds < 5) return;
        float W(UnifiedExpressions e) => shapes[(int)e].Weight;
        var top = Enumerable.Range(0, lastFresh ? count : 0).Where(i => layout[i] is not null)
            .OrderByDescending(i => values[i]).Take(4)
            .Select(i => FormattableString.Invariant($"{layoutNames[i]}={values[i]:F2}"));
        log(FormattableString.Invariant(
            $"MouthLink: rx={(packets - packetsAtReport) / seconds:F1}Hz fresh={lastFresh} seq={sequence} ch={count} ") +
            FormattableString.Invariant($"gate={lastGate:F2} L={W(UnifiedExpressions.TongueLeft):F2} R={W(UnifiedExpressions.TongueRight):F2} ") +
            FormattableString.Invariant($"U={W(UnifiedExpressions.TongueUp):F2} D={W(UnifiedExpressions.TongueDown):F2} ") +
            $"top: {string.Join(" ", top)} rejected={rejected}");
        lastReport = now; packetsAtReport = packets;
    }

    public void Dispose() => socket?.Dispose();

    private const string DefaultConfigText = """
// MouthLink input of UnifiedPicoModule-MouthLink.
// Values come from tools/mouthlink_live.py (MouthLink repository) over 127.0.0.1.
// Save this file and the module re-reads it within a second; no VRCFT restart needed.
{
  "Enabled": true,
  "Port": 9050,

  // Model values older than this fall back to stock; channels stock lacks are written 0.
  "FreshMs": 150,

  // Mode for each model channel (names as the model sends them):
  //   "model"      model value replaces stock
  //   "stock"      ignore the model for this channel
  //   "blend:0.5"  stock * (1 - w) + model * w
  //   "max"        larger of stock and model
  // Stock mouth channels: CheekPuff CheekSquintL CheekSquintR JawForward JawLeft JawOpen JawRight
  //   MouthClose MouthDimpleL MouthDimpleR MouthFrownL MouthFrownR MouthFunnel MouthLeft MouthRight
  //   MouthLowerDownL MouthLowerDownR MouthPressL MouthPressR MouthPucker MouthRollLower MouthRollUpper
  //   MouthShrugLower MouthShrugUpper MouthSmileL MouthSmileR MouthStretchL MouthStretchR
  //   MouthUpperUpL MouthUpperUpR NoseSneerL NoseSneerR TongueOut
  // Channels stock lacks (stock value counts as 0): TongueLeft TongueRight TongueUp TongueDown TongueRoll
  //   CheekPuffLeft CheekPuffRight CheekSuckLeft CheekSuckRight MouthTightenerLeft MouthTightenerRight
  "DefaultMode": "model",
  "Modes": {
    // "JawOpen": "stock",
    // "MouthSmileL": "blend:0.5"
  },

  // Tongue directions and TongueRoll show only while TongueOut (as finally written) is up:
  // 0 below TongueGateFrom, full from TongueGateFull.
  "TongueGateFrom": 0.05,
  "TongueGateFull": 0.20
}

""";
}
