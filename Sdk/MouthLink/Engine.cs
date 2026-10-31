using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UnifiedPicoModule.Sdk.MouthLink;

/// <summary>Headset address seen by the PICO bridge/streaming sources; MouthLink subscribes there by default.</summary>
internal static class HeadsetHint
{
    private static IPAddress? last;
    public static void Set(IPAddress address) => Volatile.Write(ref last, address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    public static IPAddress? Get() => Volatile.Read(ref last);
}

/// <summary>Built-in MouthLink: mouth-camera frames from the headset → ONNX model → filtered channel values.</summary>
/// <remarks>
/// Same pipeline as tools/mouthlink_live.py in the MouthLink repository: SUBSCRIBE to the headset
/// (PicoET-Enhance-MouthLink), assemble GRAY8 frames, Pillow-exact bilinear resize to the model input,
/// /255, model, clip to 0..1, One Euro (or EMA) filter, then lo/hi stretch for the channels stock does not
/// have. Runs on its own thread; <see cref="TryRead"/> hands the latest values to the VRCFT update.
/// </remarks>
internal sealed class MouthLinkEngine : IDisposable
{
    public sealed record Options(
        string ModelPath, string RuntimePath, string Headset, int CtrlPort, int ListenPort,
        float Lo, float Hi, IReadOnlyDictionary<string, float> HiChannels,
        string Filter, float MinCutoff, float Beta, float Tau, int Threads)
    {
        // Records compare dictionaries by reference; restart decisions need contents.
        public bool SameAs(Options o) => this with { HiChannels = o.HiChannels } == o &&
            HiChannels.Count == o.HiChannels.Count &&
            HiChannels.All(kv => o.HiChannels.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    // The 33 stock mouth channels: predicted on the stock scale, never stretched (as mouthlink_live.py).
    private static readonly HashSet<string> Stock = new(StringComparer.Ordinal)
    {
        "CheekPuff", "CheekSquintL", "CheekSquintR", "JawForward", "JawLeft", "JawOpen", "JawRight", "MouthClose",
        "MouthDimpleL", "MouthDimpleR", "MouthFrownL", "MouthFrownR", "MouthFunnel", "MouthLeft", "MouthLowerDownL",
        "MouthLowerDownR", "MouthPressL", "MouthPressR", "MouthPucker", "MouthRight", "MouthRollLower",
        "MouthRollUpper", "MouthShrugLower", "MouthShrugUpper", "MouthSmileL", "MouthSmileR", "MouthStretchL",
        "MouthStretchR", "MouthUpperUpL", "MouthUpperUpR", "NoseSneerL", "NoseSneerR", "TongueOut"
    };

    private readonly Options options;
    private readonly Action<string> log;
    private readonly Thread thread;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private float[] published = [];
    private long publishedAt;
    private ulong publishedCapture;
    private string[] names = [];

    public byte[] NamesUtf8 { get; private set; } = [];
    public long Frames, Inferences;
    public double InferenceMs;
    public string Status { get; private set; } = "starting";

    public MouthLinkEngine(Options options, Action<string> logger)
    {
        this.options = options;
        log = logger;
        thread = new Thread(Run) { IsBackground = true, Name = "MouthLink model" };
        thread.Start();
    }

    public Options Settings => options;

    /// <summary>Copies the latest values; false until the first frame has gone through the model.</summary>
    public bool TryRead(Span<float> values, out int count, out long timestamp, out ulong captureNs, out byte[] namesUtf8)
    {
        lock (gate)
        {
            count = published.Length; timestamp = publishedAt; captureNs = publishedCapture; namesUtf8 = NamesUtf8;
            if (publishedAt == 0 || count > values.Length) return false;
            published.CopyTo(values);
            return true;
        }
    }

    private sealed class Meta
    {
        public string[]? Channels { get; set; }
        public int Height { get; set; }
        public int Width { get; set; }
    }

    private void Run()
    {
        try { RunCore(stop.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "failed: " + ex.Message;
            log($"MouthLink built-in model stopped: {ex.Message}");
        }
    }

    private void RunCore(CancellationToken token)
    {
        // ---- model ----
        var metaPath = Path.ChangeExtension(options.ModelPath, ".json");
        if (!File.Exists(options.ModelPath)) throw new FileNotFoundException($"no model {options.ModelPath}");
        if (!File.Exists(metaPath)) throw new FileNotFoundException($"no {metaPath} (channel list written by export_onnx.py next to the .onnx)");
        var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (meta?.Channels is not { Length: > 0 and <= 256 } channels || meta.Height <= 0 || meta.Width <= 0)
            throw new InvalidDataException($"{metaPath}: need \"channels\" (1..256), \"height\" and \"width\"");
        if (!File.Exists(options.RuntimePath))
            throw new FileNotFoundException($"no onnxruntime library {options.RuntimePath}");
        string version;
        try { version = OrtModel.LoadRuntime(options.RuntimePath); }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException($"cannot load {options.RuntimePath} ({ex.Message}); on Windows it needs the " +
                "Microsoft Visual C++ 2015-2022 x64 runtime (vc_redist.x64.exe)");
        }
        using var model = new OrtModel(File.ReadAllBytes(options.ModelPath), meta.Height, meta.Width, channels.Length, options.Threads);
        names = channels;
        int k = channels.Length;
        var hi = new float[k];
        var stretch = new bool[k];
        for (int i = 0; i < k; i++)
        {
            stretch[i] = !Stock.Contains(channels[i]);
            hi[i] = options.HiChannels.TryGetValue(channels[i], out var h) ? h : options.Hi;
        }
        foreach (var name in options.HiChannels.Keys.Where(n => Array.IndexOf(channels, n) < 0))
            log($"MouthLink: HiChannels lists {name}, the model has no such channel (ignored).");
        lock (gate) NamesUtf8 = Encoding.UTF8.GetBytes(string.Join(",", channels));
        log($"MouthLink built-in model: {Path.GetFileName(options.ModelPath)} ({k} channels, input {meta.Width}x{meta.Height}), " +
            $"onnxruntime {version}, {options.Threads} thread(s); filter {Describe()}; lo={options.Lo} hi={options.Hi}" +
            (options.HiChannels.Count > 0 ? " (" + string.Join(", ", options.HiChannels.Select(kv => FormattableString.Invariant($"{kv.Key}={kv.Value}"))) + ")" : "") + ".");

        // ---- network ----
        Socket? socket = null;
        var assembler = new FrameAssembler();
        var buffer = new byte[65536];
        var subscribe = Wire.SubscribePacket((ushort)options.ListenPort);
        IPEndPoint? target = null, discovered = null;
        IPAddress? configured = null;
        long nextResolve = 0, nextSubscribe = 0, nextDiscover = 0, nextBind = 0, lastFrame = 0, started = Stopwatch.GetTimestamp();
        long statsAt = started, framesAt = 0, inferAt = 0; double msAt = 0;
        bool hinted = false, warnedFormat = false, warnedRoi = false, first = true;
        PillowBilinear? resize = null;
        var raw = new float[k];
        var filtered = new float[k];
        var dx = new float[k];
        var output = new float[k];
        ulong lastCapture = 0;
        bool haveState = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                if (socket is null)
                {
                    if (now < nextBind) { token.WaitHandle.WaitOne(100); continue; }
                    try { socket = Bind(options.ListenPort); Status = "waiting for the headset"; }
                    catch (SocketException ex)
                    {
                        if (nextBind == 0)
                            log($"MouthLink: UDP {options.ListenPort} is busy ({ex.SocketErrorCode}) — MouthLink.Receiver or " +
                                "mouthlink_live.py running? Retrying every 5 s.");
                        Status = $"port {options.ListenPort} busy";
                        nextBind = now + Stopwatch.Frequency * 5;
                        continue;
                    }
                }

                // Where to subscribe: configured address, else the headset the PICO bridge talks to,
                // else whoever answers a broadcast DISCOVER.
                if (!string.IsNullOrWhiteSpace(options.Headset) && configured is null && now >= nextResolve)
                {
                    configured = Resolve(options.Headset);
                    if (configured is null && nextResolve == 0) log($"MouthLink: cannot resolve Headset \"{options.Headset}\"; retrying.");
                    nextResolve = now + Stopwatch.Frequency * 5;
                }
                var want = configured ?? HeadsetHint.Get() ?? discovered?.Address;
                if (want is not null && (target is null || !target.Address.Equals(want)))
                {
                    if (target is not null) Send(socket, Wire.UnsubscribePacket, target);
                    target = new IPEndPoint(want, options.CtrlPort);
                    log($"MouthLink: subscribing to the headset at {want}:{options.CtrlPort}, frames on UDP {options.ListenPort}.");
                    nextSubscribe = 0;
                }
                if (target is null && now >= nextDiscover)
                {
                    Send(socket, Wire.DiscoverPacket, new IPEndPoint(IPAddress.Broadcast, options.CtrlPort));
                    nextDiscover = now + Stopwatch.Frequency * 2;
                }
                if (target is not null && now >= nextSubscribe)
                {
                    Send(socket, subscribe, target);
                    nextSubscribe = now + Stopwatch.Frequency;
                }

                // Receive; when frames queue up (model slower than the camera), keep only the newest.
                CameraFrame? frame = null;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = socket.ReceiveFrom(buffer, ref from); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset or SocketError.WouldBlock)
                { n = -1; }
                while (n >= 0)
                {
                    var data = buffer.AsSpan(0, n);
                    if (Wire.IsHello(data))
                    {
                        if (discovered is null) log($"MouthLink: headset answered DISCOVER from {((IPEndPoint)from).Address}.");
                        discovered = (IPEndPoint)from;
                    }
                    else if (assembler.Feed(data) is { } f) frame = f;
                    if (socket.Available <= 0) break;
                    try { n = socket.ReceiveFrom(buffer, ref from); }
                    catch (SocketException) { break; }
                }

                now = Stopwatch.GetTimestamp();
                if (frame is null)
                {
                    double idle = Stopwatch.GetElapsedTime(lastFrame == 0 ? started : lastFrame, now).TotalSeconds;
                    if (idle > 5 && !hinted)
                    {
                        log(target is null
                            ? "MouthLink: no headset found yet (no PICO bridge peer, no DISCOVER answer); set \"Headset\" in MouthLink.json."
                            : $"MouthLink: no camera frames from {target.Address} for 5 s — headset on and awake, PicoET-Enhance-MouthLink " +
                              "loaded, nothing else subscribed (MouthLink.Receiver / mouthlink_live.py)?");
                        hinted = true;
                        Status = "no frames";
                    }
                    continue;
                }
                Frames++;
                lastFrame = now;
                if (hinted) { log("MouthLink: camera frames are back."); hinted = false; }

                // ---- frame → model input ----
                if (frame.Compression != 0 || frame.PixFmt is not (Wire.Gray8 or Wire.Gray16Le))
                {
                    if (!warnedFormat) log($"MouthLink: skipping frames in format {frame.PixFmt}/{frame.Compression} (need GRAY8/GRAY16, uncompressed).");
                    warnedFormat = true;
                    continue;
                }
                int pixels = frame.Width * frame.Height, bpp = frame.PixFmt == Wire.Gray8 ? 1 : 2;
                if (pixels <= 0 || frame.Payload.Length < pixels * bpp) continue;
                if ((frame.RoiX != 0 || frame.RoiY != 0) && !warnedRoi)
                {
                    log("MouthLink: the headset sends a cropped ROI; the model was trained on full frames.");
                    warnedRoi = true;
                }
                ReadOnlySpan<byte> gray = frame.Payload.AsSpan(0, pixels);
                if (bpp == 2)
                {
                    var g = new byte[pixels];
                    for (int i = 0; i < pixels; i++) g[i] = frame.Payload[2 * i + 1];      // u16 LE >> 8
                    gray = g;
                }
                if (resize is null || resize.InWidth != frame.Width || resize.InHeight != frame.Height)
                {
                    resize = new PillowBilinear(frame.Width, frame.Height, meta.Width, meta.Height);
                    if (first) log($"MouthLink: first frame {frame.Width}x{frame.Height} → model {meta.Width}x{meta.Height}.");
                    first = false;
                }
                resize.Run(gray, model.Input);
                model.Run(raw, out var ms);
                Inferences++; InferenceMs += ms;
                Status = "running";

                // ---- filter + mapping (mouthlink_live.py) ----
                float dt = lastCapture == 0 ? 1f / 90 : Math.Clamp((float)((long)(frame.CaptureNs - lastCapture) / 1e9), 0f, 0.2f);
                lastCapture = frame.CaptureNs;
                for (int i = 0; i < k; i++) raw[i] = float.IsFinite(raw[i]) ? Math.Clamp(raw[i], 0f, 1f) : 0f;
                if (!haveState) { raw.CopyTo(filtered); haveState = true; }
                else if (options.Filter == "euro")
                {
                    float t = Math.Max(dt, 1e-3f), ad = Alpha(1f, t);
                    for (int i = 0; i < k; i++)
                    {
                        float d = (raw[i] - filtered[i]) / t;
                        dx[i] += (d - dx[i]) * ad;
                        filtered[i] += (raw[i] - filtered[i]) * Alpha(options.MinCutoff + options.Beta * Math.Abs(dx[i]), t);
                    }
                }
                else
                {
                    float a = options.Tau <= 0 ? 1f : 1f - MathF.Exp(-dt / options.Tau);
                    for (int i = 0; i < k; i++) filtered[i] += (raw[i] - filtered[i]) * a;
                }
                for (int i = 0; i < k; i++)
                    output[i] = stretch[i]
                        ? Math.Clamp((filtered[i] - options.Lo) / (hi[i] - options.Lo), 0f, 1f)
                        : Math.Clamp(filtered[i], 0f, 1f);
                lock (gate)
                {
                    if (published.Length != k) published = new float[k];
                    output.CopyTo(published);
                    publishedAt = Stopwatch.GetTimestamp();
                    publishedCapture = frame.CaptureNs;
                }

                if (Stopwatch.GetElapsedTime(statsAt, now).TotalSeconds >= 30)
                {
                    double s = Stopwatch.GetElapsedTime(statsAt, now).TotalSeconds;
                    long inf = Inferences - inferAt;
                    log(FormattableString.Invariant(
                        $"MouthLink built-in: {(Frames - framesAt) / s:F1} frames/s, model {inf / s:F1}/s at {(InferenceMs - msAt) / Math.Max(1, inf):F2} ms, ") +
                        $"dropped {assembler.Dropped}, bad {assembler.Bad}.");
                    statsAt = now; framesAt = Frames; inferAt = Inferences; msAt = InferenceMs;
                }
            }
        }
        finally
        {
            if (socket is not null)
            {
                if (target is not null) Send(socket, Wire.UnsubscribePacket, target);
                socket.Dispose();
            }
        }
    }

    private string Describe() => options.Filter == "euro"
        ? FormattableString.Invariant($"euro min_cutoff={options.MinCutoff} beta={options.Beta}")
        : FormattableString.Invariant($"ema tau={options.Tau}s");

    // One Euro smoothing factor for a cutoff frequency (Hz) and a step (s).
    private static float Alpha(float cutoff, float dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return (float)(1.0 / (1.0 + tau / dt));
    }

    private static IPAddress? Resolve(string headset)
    {
        if (string.IsNullOrWhiteSpace(headset)) return null;
        if (IPAddress.TryParse(headset.Trim(), out var ip)) return ip;
        try { return Dns.GetHostAddresses(headset.Trim()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
        catch (SocketException) { return null; }
    }

    private static Socket Bind(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // An ICMP "port unreachable" (headset not listening yet) must not fail the next receive.
                const int SioUdpConnReset = -1744830452;
                s.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            s.ReceiveBufferSize = 8 << 20;
            s.EnableBroadcast = true;
            s.ReceiveTimeout = 100;
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private static void Send(Socket s, byte[] packet, IPEndPoint to)
    {
        try { s.SendTo(packet, to); } catch (SocketException) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        if (!thread.Join(TimeSpan.FromSeconds(2)))
            log("MouthLink: model thread did not stop within 2 s.");
        stop.Dispose();
    }
}
