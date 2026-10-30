using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiAudio.App;

/// <summary>
/// Captures the audio of ONE process (via WASAPI process-loopback) and fans it out
/// to any number of <see cref="OutputSink"/>s. This is the core routing engine.
///
/// Process-loopback TAPS the app audio; it does not remove it from wherever the app
/// is already playing. Routing to a device the app is NOT on gives a clean copy;
/// routing to the same device duplicates against the native path (audible echo).
/// </summary>
public sealed class ProcessCapture : IDisposable
{
    // Simple, universally-outputtable PCM; the loopback device resamples the app mix to this.
    public static readonly WaveFormat CaptureFormat = new WaveFormat(48000, 16, 2);

    private readonly int _pid;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly List<OutputSink> _sinks = new();

    private Thread? _thread;
    private volatile bool _stop;
    private IntPtr _hEvent = IntPtr.Zero;

    public int ProcessId => _pid;
    public string AppName { get; }
    public int SinkCount { get { lock (_lock) return _sinks.Count; } }

    public ProcessCapture(int pid, string appName, Action<string> log)
    {
        _pid = pid;
        AppName = appName;
        _log = log;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = $"Capture[{_pid}]" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void AddSink(OutputSink sink)
    {
        sink.Start();
        lock (_lock) _sinks.Add(sink);
        _log($"+ {AppName} → {sink.DeviceName}");
    }

    public void RemoveSink(OutputSink sink)
    {
        lock (_lock) _sinks.Remove(sink);
        sink.Dispose();
        _log($"- {AppName} ⊘ {sink.DeviceName}");
    }

    public void Stop()
    {
        _stop = true;
        if (_hEvent != IntPtr.Zero) SetEvent(_hEvent);
        _thread?.Join(2000);
        lock (_lock)
        {
            foreach (var s in _sinks) s.Dispose();
            _sinks.Clear();
        }
    }

    public void Dispose() => Stop();

    private void Run()
    {
        AudioClient? audioClient = null;
        IntPtr pProp = IntPtr.Zero, pParams = IntPtr.Zero;
        try
        {
            // 1. Activate the process-loopback audio client for this PID.
            var apParams = new AudioClientActivationParams
            {
                ActivationType = 1,      // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = (uint)_pid,
                ProcessLoopbackMode = 0, // INCLUDE_TARGET_PROCESS_TREE
            };
            pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            Marshal.StructureToPtr(apParams, pParams, false);

            var pv = new PropVariantBlob { vt = 0x0041 /*VT_BLOB*/, cbSize = Marshal.SizeOf<AudioClientActivationParams>(), pBlobData = pParams };
            pProp = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            Marshal.StructureToPtr(pv, pProp, false);

            var handler = new ActivationHandler();
            var iid = IID_IAudioClient;
            ActivateAudioInterfaceAsync(VirtualLoopbackDevice, ref iid, pProp, handler, out _);
            if (!handler.Done.Wait(3000) || handler.Operation == null)
                throw new Exception("Activation timed out.");
            handler.Operation.GetActivateResult(out int hr, out object activated);
            if (hr != 0) throw new Exception($"ActivateResult HRESULT 0x{hr:X8}");

            // 2. Wrap in NAudio's AudioClient, init in loopback+event mode.
            audioClient = WrapAudioClient(activated);
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                200000, 0, CaptureFormat, Guid.Empty);

            _hEvent = CreateEventW(IntPtr.Zero, false, false, null);
            audioClient.SetEventHandle(_hEvent);
            var capture = audioClient.AudioCaptureClient;
            audioClient.Start();

            // 3. Capture loop -> feed every attached sink.
            int blockAlign = CaptureFormat.BlockAlign;
            while (!_stop)
            {
                WaitForSingleObject(_hEvent, 2000);
                if (_stop) break;

                int packet = capture.GetNextPacketSize();
                while (packet != 0)
                {
                    var ptr = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes = frames * blockAlign;
                    var buf = new byte[bytes];
                    if ((flags & AudioClientBufferFlags.Silent) == 0 && ptr != IntPtr.Zero)
                        Marshal.Copy(ptr, buf, 0, bytes);
                    capture.ReleaseBuffer(frames);

                    lock (_lock)
                        foreach (var s in _sinks) s.Feed(buf, bytes);

                    packet = capture.GetNextPacketSize();
                }
            }
        }
        catch (Exception ex)
        {
            _log($"ERROR capturing {AppName}: {ex.Message}");
        }
        finally
        {
            try { audioClient?.Stop(); } catch { }
            try { audioClient?.Dispose(); } catch { }
            if (_hEvent != IntPtr.Zero) { CloseHandle(_hEvent); _hEvent = IntPtr.Zero; }
            if (pProp != IntPtr.Zero) Marshal.FreeHGlobal(pProp);
            if (pParams != IntPtr.Zero) Marshal.FreeHGlobal(pParams);
        }
    }

    /// <summary>Build NAudio's AudioClient around a raw activated COM IAudioClient.</summary>
    private static AudioClient WrapAudioClient(object activated)
    {
        var ctors = typeof(AudioClient).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType.IsInterface)
            {
                try { return (AudioClient)c.Invoke(new[] { activated }); }
                catch { /* wrong interface, try next */ }
            }
        }
        var sigs = string.Join(" | ", ctors.Select(c =>
            "(" + string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
        throw new Exception("No usable AudioClient ctor. Available: " + sigs);
    }

    // ---------------- native interop ----------------

    private const string VirtualLoopbackDevice = "VAD\\Process_Loopback";
    private static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activateInterface);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new(false);
        public IActivateAudioInterfaceAsyncOperation? Operation;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            Operation = op;
            Done.Set();
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
