using System.Runtime.InteropServices;

namespace UnifiedPicoModule.Sdk.MouthLink;

// Minimal ONNX Runtime binding over the C API (no Microsoft.ML.OnnxRuntime package): the native
// onnxruntime library is loaded by full path (VRCFaceTracking ships one), and OrtApi function pointers
// are read from the table returned by OrtGetApiBase()->GetApi(version). Table indices are fixed by
// the C API (append-only across versions); the ones used here come from onnxruntime_c_api.h.
// Single-input, single-output float model, CPU only — exactly what MouthLink exports.
internal sealed unsafe class OrtModel : IDisposable
{
    private const uint ApiVersion = 16;                    // needs onnxruntime >= 1.16
    private const int IdxGetErrorMessage = 2, IdxCreateEnv = 3, IdxCreateSessionFromArray = 8, IdxRun = 9,
        IdxCreateSessionOptions = 10, IdxSetGraphOptimizationLevel = 23, IdxSetIntraOpNumThreads = 24,
        IdxSetInterOpNumThreads = 25, IdxSessionGetInputName = 36, IdxSessionGetOutputName = 37,
        IdxCreateTensorWithDataAsOrtValue = 49, IdxGetTensorMutableData = 51, IdxGetTensorShapeElementCount = 64,
        IdxGetTensorTypeAndShape = 65, IdxReleaseTensorTypeAndShapeInfo = 99, IdxCreateCpuMemoryInfo = 69,
        IdxAllocatorFree = 76, IdxGetAllocatorWithDefaultOptions = 78, IdxReleaseEnv = 92, IdxReleaseStatus = 93,
        IdxReleaseMemoryInfo = 94, IdxReleaseSession = 95, IdxReleaseValue = 96, IdxReleaseSessionOptions = 100,
        IdxAddSessionConfigEntry = 130;

    private static nint library;
    private static void** api;
    private static string version = "";

    private nint env, session, memInfo;
    private readonly nint inputName, outputName;            // UTF-8, owned by us
    private readonly long[] shape;
    private readonly float[] input;
    private readonly GCHandle inputPin;
    private nint inputValue;
    private bool checkCount;
    public int Outputs { get; }
    public string Version => version;

    /// <summary>Loads onnxruntime from <paramref name="nativePath"/> once per process.</summary>
    public static string LoadRuntime(string nativePath)
    {
        if (api != null) return version;
        library = NativeLibrary.Load(nativePath);
        var getBase = (delegate* unmanaged<void**>)NativeLibrary.GetExport(library, "OrtGetApiBase");
        void** apiBase = getBase();
        var getApi = (delegate* unmanaged<uint, void**>)apiBase[0];
        var getVersion = (delegate* unmanaged<sbyte*>)apiBase[1];
        version = new string(getVersion());
        api = getApi(ApiVersion);
        if (api == null) throw new InvalidOperationException($"onnxruntime {version} is older than API {ApiVersion}");
        return version;
    }

    private static void Check(nint status, string what)
    {
        if (status == 0) return;
        var message = Marshal.PtrToStringUTF8((nint)((delegate* unmanaged<nint, sbyte*>)api[IdxGetErrorMessage])(status));
        ((delegate* unmanaged<nint, void>)api[IdxReleaseStatus])(status);
        throw new InvalidOperationException($"onnxruntime {what}: {message}");
    }

    public OrtModel(byte[] model, int height, int width, int outputs, int threads)
    {
        Outputs = outputs;
        if (api == null) throw new InvalidOperationException("onnxruntime is not loaded");
        try
        {
            nint e, opts, s, mi;
            fixed (byte* logid = "mouthlink\0"u8)
                Check(((delegate* unmanaged<int, byte*, nint*, nint>)api[IdxCreateEnv])(3 /*ERROR*/, logid, &e), "CreateEnv");
            env = e;
            Check(((delegate* unmanaged<nint*, nint>)api[IdxCreateSessionOptions])(&opts), "CreateSessionOptions");
            try
            {
                Check(((delegate* unmanaged<nint, int, nint>)api[IdxSetIntraOpNumThreads])(opts, Math.Max(1, threads)), "SetIntraOpNumThreads");
                Check(((delegate* unmanaged<nint, int, nint>)api[IdxSetInterOpNumThreads])(opts, 1), "SetInterOpNumThreads");
                Check(((delegate* unmanaged<nint, int, nint>)api[IdxSetGraphOptimizationLevel])(opts, 99), "SetGraphOptimizationLevel");
                // Worker threads must not spin between frames: VR shares the CPU.
                fixed (byte* key = "session.intra_op.allow_spinning\0"u8) fixed (byte* val = "0\0"u8)
                    Check(((delegate* unmanaged<nint, byte*, byte*, nint>)api[IdxAddSessionConfigEntry])(opts, key, val), "AddSessionConfigEntry");
                fixed (byte* data = model)
                    Check(((delegate* unmanaged<nint, byte*, nuint, nint, nint*, nint>)api[IdxCreateSessionFromArray])(
                        env, data, (nuint)model.Length, opts, &s), "CreateSessionFromArray");
                session = s;
            }
            finally { ((delegate* unmanaged<nint, void>)api[IdxReleaseSessionOptions])(opts); }

            nint alloc; sbyte* name;
            Check(((delegate* unmanaged<nint*, nint>)api[IdxGetAllocatorWithDefaultOptions])(&alloc), "GetAllocator");
            Check(((delegate* unmanaged<nint, nuint, nint, sbyte**, nint>)api[IdxSessionGetInputName])(session, 0, alloc, &name), "SessionGetInputName");
            inputName = Copy(name); ((delegate* unmanaged<nint, void*, nint>)api[IdxAllocatorFree])(alloc, name);
            Check(((delegate* unmanaged<nint, nuint, nint, sbyte**, nint>)api[IdxSessionGetOutputName])(session, 0, alloc, &name), "SessionGetOutputName");
            outputName = Copy(name); ((delegate* unmanaged<nint, void*, nint>)api[IdxAllocatorFree])(alloc, name);

            Check(((delegate* unmanaged<int, int, nint*, nint>)api[IdxCreateCpuMemoryInfo])(0 /*device*/, 0 /*default*/, &mi), "CreateCpuMemoryInfo");
            memInfo = mi;
            shape = [1, 1, height, width];
            input = new float[height * width];
            inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
            nint v;
            fixed (long* sh = shape)
                Check(((delegate* unmanaged<nint, void*, nuint, long*, nuint, int, nint*, nint>)api[IdxCreateTensorWithDataAsOrtValue])(
                    memInfo, (void*)inputPin.AddrOfPinnedObject(), (nuint)(input.Length * sizeof(float)), sh, 4, 1 /*FLOAT*/, &v), "CreateTensor");
            inputValue = v;
            checkCount = true;
            Run(new float[outputs], out _);                 // warm-up; also validates the model end to end
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static nint Copy(sbyte* s)
    {
        int n = 0; while (s[n] != 0) n++;
        nint p = Marshal.AllocHGlobal(n + 1);
        Buffer.MemoryCopy(s, (void*)p, n + 1, n + 1);
        return p;
    }

    /// <summary>The model input (1×1×H×W, values 0..1); fill, then call <see cref="Run"/>.</summary>
    public Span<float> Input => input;

    /// <summary>Runs the model on <see cref="Input"/>; copies the first <see cref="Outputs"/> values.</summary>
    public void Run(float[] output, out double milliseconds)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        nint inName = inputName, outName = outputName, inVal = inputValue, outVal = 0;
        Check(((delegate* unmanaged<nint, nint, nint*, nint*, nuint, nint*, nuint, nint*, nint>)api[IdxRun])(
            session, 0, &inName, &inVal, 1, &outName, 1, &outVal), "Run");
        try
        {
            float* data;
            if (checkCount)
            {
                nint info; nuint count;
                Check(((delegate* unmanaged<nint, nint*, nint>)api[IdxGetTensorTypeAndShape])(outVal, &info), "GetTensorTypeAndShape");
                try { Check(((delegate* unmanaged<nint, nuint*, nint>)api[IdxGetTensorShapeElementCount])(info, &count), "GetTensorShapeElementCount"); }
                finally { ((delegate* unmanaged<nint, void>)api[IdxReleaseTensorTypeAndShapeInfo])(info); }
                if ((int)count != Outputs)
                    throw new InvalidOperationException($"model outputs {count} values, its sidecar lists {Outputs} channels");
                checkCount = false;
            }
            Check(((delegate* unmanaged<nint, float**, nint>)api[IdxGetTensorMutableData])(outVal, &data), "GetTensorMutableData");
            new ReadOnlySpan<float>(data, Math.Min(Outputs, output.Length)).CopyTo(output);
        }
        finally { ((delegate* unmanaged<nint, void>)api[IdxReleaseValue])(outVal); }
        milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    public void Dispose()
    {
        if (api == null) return;
        if (inputValue != 0) { ((delegate* unmanaged<nint, void>)api[IdxReleaseValue])(inputValue); inputValue = 0; }
        if (inputPin.IsAllocated) inputPin.Free();
        if (memInfo != 0) { ((delegate* unmanaged<nint, void>)api[IdxReleaseMemoryInfo])(memInfo); memInfo = 0; }
        if (session != 0) { ((delegate* unmanaged<nint, void>)api[IdxReleaseSession])(session); session = 0; }
        if (env != 0) { ((delegate* unmanaged<nint, void>)api[IdxReleaseEnv])(env); env = 0; }
        if (inputName != 0) Marshal.FreeHGlobal(inputName);
        if (outputName != 0) Marshal.FreeHGlobal(outputName);
    }
}
