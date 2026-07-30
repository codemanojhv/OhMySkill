using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SkillMyScreen;

public static class WindowCatalog
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var text = new StringBuilder(512);
            GetWindowText(hWnd, text, text.Capacity);
            if (text.Length == 0) return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                result.Add(new WindowInfo(hWnd, text.ToString(), process.ProcessName, (int)pid));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    public static byte[] Capture(CaptureMode mode, IntPtr window)
    {
        Rectangle bounds;
        if (mode == CaptureMode.Window && window != IntPtr.Zero && GetWindowRect(window, out var rect))
            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        else
            bounds = Rectangle.FromLTRB(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        if (bounds.Width <= 0 || bounds.Height <= 0) return [];
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}

public sealed class MicrophoneRecorder : IDisposable
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackFunction = 0x00030000;
    private const uint MmWimData = 0x3C0;
    private const uint MmWimOpen = 0x3BE;
    private const uint MmWimClose = 0x3BF;
    private IntPtr _handle;
    private readonly List<IntPtr> _buffers = [];
    private readonly List<IntPtr> _headers = [];
    private readonly MemoryStream _pcm = new();
    private readonly object _gate = new();
    private WaveInProc? _callback;
    private bool _running;

    [StructLayout(LayoutKind.Sequential)] private struct WAVEFORMATEX { public ushort FormatTag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, Size; }
    [StructLayout(LayoutKind.Sequential)] private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    private delegate void WaveInProc(IntPtr hwi, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2);

    [DllImport("winmm.dll", SetLastError = true)] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", SetLastError = true)] private static extern uint waveInOpen(out IntPtr handle, uint deviceId, ref WAVEFORMATEX format, WaveInProc callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);

    public bool IsAvailable => waveInGetNumDevs() > 0;

    public bool Start()
    {
        if (!IsAvailable) return false;
        var format = new WAVEFORMATEX { FormatTag = 1, Channels = 1, SamplesPerSec = 16000, BitsPerSample = 16, BlockAlign = 2, AvgBytesPerSec = 32000, Size = 0 };
        _callback = OnWaveMessage;
        if (waveInOpen(out _handle, WaveMapper, ref format, _callback, IntPtr.Zero, CallbackFunction) != 0) return false;
        for (var i = 0; i < 2; i++)
        {
            var data = Marshal.AllocHGlobal(8192);
            var header = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = 8192 }, header, false);
            _buffers.Add(data); _headers.Add(header);
            waveInPrepareHeader(_handle, header, (uint)Marshal.SizeOf<WAVEHDR>());
            waveInAddBuffer(_handle, header, (uint)Marshal.SizeOf<WAVEHDR>());
        }
        _running = waveInStart(_handle) == 0;
        return _running;
    }

    private void OnWaveMessage(IntPtr _, uint message, IntPtr __, IntPtr headerPtr, IntPtr ___)
    {
        if (message != MmWimData || headerPtr == IntPtr.Zero) return;
        try
        {
            var header = Marshal.PtrToStructure<WAVEHDR>(headerPtr);
            if (header.dwBytesRecorded > 0)
            {
                var bytes = new byte[header.dwBytesRecorded];
                Marshal.Copy(header.lpData, bytes, 0, bytes.Length);
                lock (_gate) _pcm.Write(bytes, 0, bytes.Length);
            }
            if (_running) waveInAddBuffer(_handle, headerPtr, (uint)Marshal.SizeOf<WAVEHDR>());
        }
        catch { }
    }

    public byte[] StopAndGetWav()
    {
        if (_handle != IntPtr.Zero)
        {
            _running = false;
            waveInStop(_handle);
            waveInReset(_handle);
            foreach (var header in _headers) waveInUnprepareHeader(_handle, header, (uint)Marshal.SizeOf<WAVEHDR>());
            waveInClose(_handle);
            _handle = IntPtr.Zero;
        }
        var pcm = _pcm.ToArray();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        foreach (var ptr in _buffers) Marshal.FreeHGlobal(ptr);
        foreach (var ptr in _headers) { Marshal.FreeHGlobal(ptr); }
        _buffers.Clear(); _headers.Clear(); _pcm.SetLength(0);
        return output.ToArray();
    }

    public void Dispose() => StopAndGetWav();
}

public sealed class RecordingController : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _clock = new();
    private readonly SecureSessionStore _store;
    private readonly MicrophoneRecorder _microphone = new();
    private readonly CaptureMode _mode;
    private readonly IntPtr _window;
    private int _frame;
    private bool _stopped;
    public RecordingTrace Trace { get; }
    public string TemporaryFolder => _store.Folder;
    public byte[] AudioWav { get; private set; } = [];
    public event Action<TraceEvent>? EventRecorded;

    public RecordingController(string title, CaptureMode mode, IntPtr window)
    {
        _mode = mode; _window = window;
        Trace = new RecordingTrace { Title = string.IsNullOrWhiteSpace(title) ? "Computer workflow" : title, CaptureMode = mode, CaptureTarget = window == IntPtr.Zero ? "display" : window.ToString() };
        _store = new SecureSessionStore(Trace.Id);
        _timer.Tick += (_, _) => CaptureFrame("periodic");
    }

    public void Start()
    {
        Directory.CreateDirectory(AppPaths.Sessions);
        _clock.Start();
        Trace.Events.Add(new TraceEvent(0, TraceEventKind.SessionStarted, "Recording started", null, null, null, null));
        Trace.HasAudio = _microphone.Start();
        _timer.Start();
        CaptureFrame("initial");
    }

    public void Mark(string text = "User marked a meaningful step")
    {
        var elapsed = _clock.ElapsedMilliseconds;
        Trace.Events.Add(new TraceEvent(elapsed, TraceEventKind.Marker, text, null, null, null, null));
        CaptureFrame("marker");
    }

    private void CaptureFrame(string reason)
    {
        try
        {
            var png = ScreenCapture.Capture(_mode, _window);
            if (png.Length == 0) return;
            var name = $"{_frame++:0000}-{reason}";
            _store.WriteFrame(name, png);
            Trace.Events.Add(new TraceEvent(_clock.ElapsedMilliseconds, TraceEventKind.KeyFrame, reason, null, null, null, name));
            EventRecorded?.Invoke(Trace.Events[^1]);
        }
        catch (Exception ex)
        {
            Trace.Notes.Add("Frame capture warning: " + ex.Message);
        }
    }

    public void RecordInput(TraceEventKind kind, string detail, UiTarget? target = null)
    {
        var process = target?.ProcessName;
        var title = target?.WindowTitle;
        var item = new TraceEvent(_clock.ElapsedMilliseconds, kind, detail, process, title, target, null);
        Trace.Events.Add(item);
        EventRecorded?.Invoke(item);
        if (kind is TraceEventKind.Click or TraceEventKind.DoubleClick or TraceEventKind.RightClick) CaptureFrame("after-click");
    }

    public void RedactRecent()
    {
        var threshold = Math.Max(0, _clock.ElapsedMilliseconds - 15000);
        for (var i = 0; i < Trace.Events.Count; i++)
        {
            var item = Trace.Events[i];
            if (item.ElapsedMilliseconds < threshold) continue;
            if (!string.IsNullOrWhiteSpace(item.FramePath))
            {
                var path = Path.Combine(_store.Folder, "frames", item.FramePath + ".enc");
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
            Trace.Events[i] = item with { Detail = "redacted by user", Target = null, FramePath = null, Redacted = true };
        }
        Trace.Notes.Add("User redacted the most recent recording window.");
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        _clock.Stop();
        Trace.EndedAt = DateTimeOffset.Now;
        Trace.Events.Add(new TraceEvent(_clock.ElapsedMilliseconds, TraceEventKind.SessionStopped, "Recording finished", null, null, null, null));
        AudioWav = _microphone.StopAndGetWav();
        _store.WriteAudio(AudioWav);
        _store.WriteTrace(Trace);
    }

    public void DeleteTemporarySession() => _store.DeleteAfterSave();

    public void Dispose() { try { Stop(); } catch { } _microphone.Dispose(); }
}
