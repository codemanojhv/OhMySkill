using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace OhMySkill;

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

    public static byte[] Capture(CaptureMode mode, IntPtr window, int maxDimension = 1600)
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
        Bitmap output = bitmap;
        if (maxDimension > 0 && Math.Max(bitmap.Width, bitmap.Height) > maxDimension)
        {
            var scale = maxDimension / (double)Math.Max(bitmap.Width, bitmap.Height);
            var resized = new Bitmap(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)), PixelFormat.Format32bppArgb);
            using var resizeGraphics = Graphics.FromImage(resized);
            resizeGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            resizeGraphics.DrawImage(bitmap, 0, 0, resized.Width, resized.Height);
            output = resized;
        }
        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);
        if (!ReferenceEquals(output, bitmap)) output.Dispose();
        return stream.ToArray();
    }

    public static bool IsVisuallyStable(byte[] first, byte[] second, double maximumAverageChannelDifference = 3)
    {
        if (first.Length == 0 || second.Length == 0) return false;
        using var firstStream = new MemoryStream(first);
        using var secondStream = new MemoryStream(second);
        using var firstBitmap = new Bitmap(firstStream);
        using var secondBitmap = new Bitmap(secondStream);
        if (firstBitmap.Size != secondBitmap.Size) return false;
        var stepX = Math.Max(1, firstBitmap.Width / 32);
        var stepY = Math.Max(1, firstBitmap.Height / 18);
        long difference = 0;
        long channels = 0;
        for (var y = stepY / 2; y < firstBitmap.Height; y += stepY)
        for (var x = stepX / 2; x < firstBitmap.Width; x += stepX)
        {
            var a = firstBitmap.GetPixel(x, y);
            var b = secondBitmap.GetPixel(x, y);
            difference += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            channels += 3;
        }
        return channels > 0 && difference / (double)channels <= maximumAverageChannelDifference;
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
    private double _level;
    private long _capturedBytes;
    private long _callbackCount;
    private double _peak;
    private int _callbacksInProgress;
    private readonly ManualResetEventSlim _callbacksIdle = new(true);
    public string? FailureReason { get; private set; }

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

    public static bool HasInputDevice => waveInGetNumDevs() > 0;
    public bool IsAvailable => HasInputDevice;
    public double Level => Volatile.Read(ref _level);
    public double Peak => Volatile.Read(ref _peak);
    public long CapturedBytes => Interlocked.Read(ref _capturedBytes);
    public long CallbackCount => Interlocked.Read(ref _callbackCount);
    public bool HasSignal => Peak >= 0.005;
    public long CapturedMilliseconds
    {
        get
        {
            lock (_gate) return _pcm.Length / 32L;
        }
    }

    public bool Start()
    {
        FailureReason = null;
        Interlocked.Exchange(ref _capturedBytes, 0);
        Interlocked.Exchange(ref _callbackCount, 0);
        Volatile.Write(ref _level, 0);
        Volatile.Write(ref _peak, 0);
        if (!IsAvailable)
        {
            FailureReason = "Windows reported no microphone input device.";
            return false;
        }
        var format = new WAVEFORMATEX { FormatTag = 1, Channels = 1, SamplesPerSec = 16000, BitsPerSample = 16, BlockAlign = 2, AvgBytesPerSec = 32000, Size = 0 };
        _callback = OnWaveMessage;
        var openResult = waveInOpen(out _handle, WaveMapper, ref format, _callback, IntPtr.Zero, CallbackFunction);
        if (openResult != 0)
        {
            FailureReason = $"Windows microphone open failed (MMRESULT 0x{openResult:X8}).";
            _handle = IntPtr.Zero;
            return false;
        }
        for (var i = 0; i < 2; i++)
        {
            var data = Marshal.AllocHGlobal(8192);
            var header = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = 8192 }, header, false);
            _buffers.Add(data); _headers.Add(header);
            var prepareResult = waveInPrepareHeader(_handle, header, (uint)Marshal.SizeOf<WAVEHDR>());
            var addResult = prepareResult == 0 ? waveInAddBuffer(_handle, header, (uint)Marshal.SizeOf<WAVEHDR>()) : prepareResult;
            if (addResult != 0)
            {
                FailureReason = $"Windows microphone buffer setup failed (MMRESULT 0x{addResult:X8}).";
                StopAndGetWav();
                return false;
            }
        }
        var startResult = waveInStart(_handle);
        _running = startResult == 0;
        if (!_running) FailureReason = $"Windows microphone start failed (MMRESULT 0x{startResult:X8}).";
        return _running;
    }

    private void OnWaveMessage(IntPtr _, uint message, IntPtr __, IntPtr headerPtr, IntPtr ___)
    {
        if (message != MmWimData || headerPtr == IntPtr.Zero) return;
        Interlocked.Increment(ref _callbacksInProgress);
        _callbacksIdle.Reset();
        try
        {
            var header = Marshal.PtrToStructure<WAVEHDR>(headerPtr);
            if (header.dwBytesRecorded > 0)
            {
                var bytes = new byte[header.dwBytesRecorded];
                Marshal.Copy(header.lpData, bytes, 0, bytes.Length);
                var peak = 0;
                for (var index = 0; index + 1 < bytes.Length; index += 8)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, index)));
                Volatile.Write(ref _level, peak / 32768d);
                if (peak > 0) Interlocked.Increment(ref _callbackCount);
                Interlocked.Add(ref _capturedBytes, bytes.Length);
                var normalizedPeak = peak / 32768d;
                while (true)
                {
                    var current = Volatile.Read(ref _peak);
                    if (normalizedPeak <= current || Interlocked.CompareExchange(ref _peak, normalizedPeak, current) == current) break;
                }
                lock (_gate) _pcm.Write(bytes, 0, bytes.Length);
            }
            if (_running) waveInAddBuffer(_handle, headerPtr, (uint)Marshal.SizeOf<WAVEHDR>());
        }
        catch (Exception ex) { FailureReason ??= "Windows microphone callback failed: " + ex.Message; }
        finally
        {
            if (Interlocked.Decrement(ref _callbacksInProgress) == 0) _callbacksIdle.Set();
        }
    }

    public byte[] StopAndGetWav()
    {
        if (_handle != IntPtr.Zero)
        {
            _running = false;
            waveInStop(_handle);
            waveInReset(_handle);
            _callbacksIdle.Wait(TimeSpan.FromSeconds(1));
            foreach (var header in _headers) waveInUnprepareHeader(_handle, header, (uint)Marshal.SizeOf<WAVEHDR>());
            waveInClose(_handle);
            _handle = IntPtr.Zero;
        }
        byte[] pcm;
        lock (_gate) pcm = _pcm.ToArray();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        foreach (var ptr in _buffers) Marshal.FreeHGlobal(ptr);
        foreach (var ptr in _headers) Marshal.FreeHGlobal(ptr);
        _buffers.Clear(); _headers.Clear();
        lock (_gate) _pcm.SetLength(0);
        return output.ToArray();
    }

    public void Dispose() => StopAndGetWav();
}

public sealed class RecordingController : IDisposable
{
    private sealed record BufferedFrame(long ElapsedMilliseconds, byte[] Png);
    private readonly System.Threading.Timer _captureTimer;
    private readonly Stopwatch _clock = new();
    private readonly SecureSessionStore _store;
    private readonly MicrophoneRecorder _microphone = new();
    private readonly CaptureMode _mode;
    private readonly IntPtr _window;
    private readonly Queue<BufferedFrame> _rollingFrames = new();
    private readonly List<Task> _pendingActions = [];
    private readonly List<(long Start, long End)> _redactedRanges = [];
    private readonly object _gate = new();
    private int _captureInProgress;
    private int _frame;
    private long _lastTrajectoryMilliseconds = -5000;
    private byte[]? _lastTrajectoryFrame;
    private int _trajectoryFrames;
    private bool _stopped;
    public RecordingTrace Trace { get; }
    public string TemporaryFolder => _store.Folder;
    public byte[] AudioWav { get; private set; } = [];
    public double MicrophoneLevel => _microphone.Level;
    public double MicrophonePeak => _microphone.Peak;
    public bool MicrophoneHasSignal => _microphone.HasSignal;
    public long CapturedAudioBytes => _microphone.CapturedBytes;
    public long CapturedAudioMilliseconds => _microphone.CapturedMilliseconds;
    public string? MicrophoneFailureReason => _microphone.FailureReason;
    public int ActionCount => Trace.Actions.Count;
    public int BufferedFrameCount { get { lock (_gate) return _rollingFrames.Count; } }
    public event Action<TraceEvent>? EventRecorded;

    public RecordingController(string title, CaptureMode mode, IntPtr window)
    {
        _mode = mode; _window = window;
        Trace = new RecordingTrace { Title = string.IsNullOrWhiteSpace(title) ? "Computer workflow" : title, CaptureMode = mode, CaptureTarget = window == IntPtr.Zero ? "display" : window.ToString() };
        _store = new SecureSessionStore(Trace.Id);
        _captureTimer = new System.Threading.Timer(_ => CaptureRollingFrame(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        Directory.CreateDirectory(AppPaths.Sessions);
        _clock.Start();
        Trace.Events.Add(new TraceEvent(0, TraceEventKind.SessionStarted, "Recording started", null, null, null, null));
        Trace.HasAudio = _microphone.Start();
        if (!Trace.HasAudio) Trace.Notes.Add(_microphone.FailureReason ?? "No microphone was available; narration cannot be transcribed.");
        else Trace.Notes.Add("Microphone opened at 16 kHz, 16-bit mono; narration timing is anchored to captured sample position.");
        CaptureAndStoreFrame("initial", FrameRole.Initial);
        _captureTimer.Change(250, 250);
    }

    public void Mark(string text = "User marked a meaningful step")
    {
        RecordInput(TraceEventKind.Marker, text, UiAutomationService.Focused());
        CaptureAndStoreFrame("marker", FrameRole.Marker);
    }

    private void CaptureRollingFrame()
    {
        if (Interlocked.Exchange(ref _captureInProgress, 1) == 1) return;
        try
        {
            var bytes = ScreenCapture.Capture(_mode, _window, 1280);
            if (bytes.Length == 0) return;
            var now = _clock.ElapsedMilliseconds;
            lock (_gate)
            {
                _rollingFrames.Enqueue(new BufferedFrame(now, bytes));
                while (_rollingFrames.Count > 12) _rollingFrames.Dequeue();
            }
            if (now - _lastTrajectoryMilliseconds >= 5000 && _trajectoryFrames < 360 &&
                (_lastTrajectoryFrame is null || !ScreenCapture.IsVisuallyStable(_lastTrajectoryFrame, bytes)))
            {
                var name = NextFrameName("trajectory");
                _store.WriteFrame(name, bytes);
                Trace.Events.Add(new TraceEvent(now, TraceEventKind.KeyFrame, "trajectory context", null, null, null, name));
                _lastTrajectoryMilliseconds = now;
                _lastTrajectoryFrame = bytes;
                _trajectoryFrames++;
            }
        }
        catch (Exception ex) { Trace.Notes.Add("Frame buffer warning: " + ex.Message); }
        finally { Volatile.Write(ref _captureInProgress, 0); }
    }

    private FrameEvidence? LatestBefore(long elapsed, Guid actionId)
    {
        BufferedFrame? selected = null;
        lock (_gate)
        {
            foreach (var frame in _rollingFrames.Where(f => f.ElapsedMilliseconds <= elapsed)) selected = frame;
        }
        if (selected is null) return null;
        var name = NextFrameName($"before-{actionId:N}");
        _store.WriteFrame(name, selected.Png);
        return new FrameEvidence(selected.ElapsedMilliseconds, "before action", selected.Png, name, FrameRole.Before, actionId);
    }

    private void CaptureAndStoreFrame(string reason, FrameRole role)
    {
        try
        {
            var bytes = ScreenCapture.Capture(_mode, _window, 1280);
            if (bytes.Length == 0) return;
            var name = NextFrameName(reason);
            _store.WriteFrame(name, bytes);
            var item = new TraceEvent(_clock.ElapsedMilliseconds, TraceEventKind.KeyFrame, reason, null, null, null, name);
            Trace.Events.Add(item);
            EventRecorded?.Invoke(item);
        }
        catch (Exception ex) { Trace.Notes.Add("Frame capture warning: " + ex.Message); }
    }

    public void RecordInput(TraceEventKind kind, string detail, UiTarget? target = null)
    {
        if (_stopped) return;
        var start = _clock.ElapsedMilliseconds;
        if (Trace.Actions.LastOrDefault() is { } previous &&
            ((kind == TraceEventKind.Scroll && previous.Kind == TraceEventKind.Scroll && start - previous.EndMilliseconds <= 600) ||
             (kind == TraceEventKind.TextEntry && previous.Kind == TraceEventKind.TextEntry && start - previous.EndMilliseconds <= 750 && SameTarget(previous.Target, target))))
        {
            var merged = previous with { EndMilliseconds = start, Detail = kind == TraceEventKind.Scroll ? "scroll burst" : detail };
            Trace.Actions[^1] = merged;
            return;
        }
        if (kind == TraceEventKind.DoubleClick && Trace.Actions.LastOrDefault() is { Kind: TraceEventKind.Click } click && start - click.StartMilliseconds <= 500)
        {
            Trace.Actions[^1] = click with { Kind = TraceEventKind.DoubleClick, Detail = "double-click", EndMilliseconds = start };
            var eventIndex = Trace.Events.FindLastIndex(item => item.Kind == TraceEventKind.Click && item.ElapsedMilliseconds == click.StartMilliseconds);
            if (eventIndex >= 0) Trace.Events[eventIndex] = Trace.Events[eventIndex] with { Kind = TraceEventKind.DoubleClick, Detail = "double-click" };
            return;
        }
        var id = Guid.NewGuid();
        var before = LatestBefore(start, id);
        var process = target?.ProcessName;
        var title = target?.WindowTitle;
        var item = new TraceEvent(start, kind, detail, process, title, target, null);
        Trace.Events.Add(item);
        var action = new ActionEvidence(id, Trace.Actions.Count + 1, start, start, kind, detail, target, before, null, [], false, true, target is null ? 0.55 : 0.85);
        Trace.Actions.Add(action);
        Trace.Evidence.TotalActions = Trace.Actions.Count;
        EventRecorded?.Invoke(item);
        var pending = CaptureAfterActionAsync(id);
        _pendingActions.Add(pending);
    }

    private async Task CaptureAfterActionAsync(Guid actionId)
    {
        try
        {
            await Task.Delay(250).ConfigureAwait(false);
            byte[]? previous = null;
            byte[]? stable = null;
            long stableTime = _clock.ElapsedMilliseconds;
            for (var attempt = 0; attempt < 8 && !_stopped; attempt++)
            {
                var bytes = ScreenCapture.Capture(_mode, _window, 1280);
                var same = previous is not null && ScreenCapture.IsVisuallyStable(previous, bytes);
                previous = bytes;
                stable = bytes;
                stableTime = _clock.ElapsedMilliseconds;
                if (same && bytes.Length > 0) break;
                await Task.Delay(250).ConfigureAwait(false);
            }
            if (stable is null || stable.Length == 0) return;
            var name = NextFrameName($"after-{actionId:N}");
            _store.WriteFrame(name, stable);
            var after = new FrameEvidence(stableTime, "after action (settled)", stable, name, FrameRole.After, actionId);
            var index = Trace.Actions.FindIndex(a => a.Id == actionId);
            if (index < 0) return;
            var current = Trace.Actions[index];
            if (current.Redacted)
            {
                DeleteFrame(after);
                return;
            }
            Trace.Actions[index] = current with { EndMilliseconds = stableTime, After = after };
            Trace.Evidence.ActionsWithFramePairs = Trace.Actions.Count(a => a.Before is not null && a.After is not null && !a.Redacted);
        }
        catch (Exception ex) { Trace.Notes.Add("Post-action capture warning: " + ex.Message); }
    }

    public void RedactRecent()
    {
        var end = _clock.ElapsedMilliseconds;
        var start = Math.Max(0, end - 15000);
        _redactedRanges.Add((start, end));
        for (var i = 0; i < Trace.Events.Count; i++)
        {
            var item = Trace.Events[i];
            if (item.ElapsedMilliseconds < start) continue;
            if (item.FramePath is not null)
            {
                try { File.Delete(Path.Combine(_store.Folder, "frames", item.FramePath + ".enc")); } catch { }
            }
            Trace.Events[i] = item with { Detail = "redacted by user", Target = null, FramePath = null, Redacted = true };
        }
        for (var i = 0; i < Trace.Actions.Count; i++)
        {
            var action = Trace.Actions[i];
            if (action.EndMilliseconds < start) continue;
            DeleteFrame(action.Before); DeleteFrame(action.After);
            Trace.Actions[i] = action with { Detail = "redacted by user", Target = null, Before = null, After = null, NearbyNarration = [], Redacted = true, IncludeInSkill = false };
        }
        Trace.Notes.Add("User redacted the most recent 15 seconds of audio, frames, and interactions.");
    }

    private void DeleteFrame(FrameEvidence? frame)
    {
        if (frame?.Id is null) return;
        try { File.Delete(Path.Combine(_store.Folder, "frames", frame.Id + ".enc")); } catch { }
    }

    public void AttachTranscript(IReadOnlyList<TranscriptSegment> segments)
    {
        var annotated = segments
            .Select(segment => segment with
            {
                ReferenceFrameIds = segment.ReferenceFrameIds is { Count: > 0 }
                    ? segment.ReferenceFrameIds
                    : ReferenceFramesFor(segment.StartMilliseconds, segment.EndMilliseconds)
            })
            .ToArray();
        Trace.Transcript.Clear();
        Trace.Transcript.AddRange(annotated);
        Trace.Evidence.ActionsWithNarration = 0;
        for (var i = 0; i < Trace.Actions.Count; i++)
        {
            var action = Trace.Actions[i];
            if (action.Redacted) continue;
            var nearby = annotated.Where(s => s.EndMilliseconds >= action.StartMilliseconds - 5000 && s.StartMilliseconds <= action.EndMilliseconds + 5000).ToArray();
            Trace.Actions[i] = action with { NearbyNarration = nearby };
            if (nearby.Length > 0) Trace.Evidence.ActionsWithNarration++;
        }
    }

    private IReadOnlyList<string> ReferenceFramesFor(long startMilliseconds, long endMilliseconds)
    {
        var midpoint = startMilliseconds + Math.Max(0, endMilliseconds - startMilliseconds) / 2;
        var candidates = Trace.Actions
            .Where(action => !action.Redacted)
            .SelectMany(action => new[] { action.Before, action.After })
            .Where(frame => frame?.Id is not null)
            .Select(frame => (Id: frame!.Id!, Time: frame.ElapsedMilliseconds))
            .Concat(Trace.Events
                .Where(item => !item.Redacted && item.Kind == TraceEventKind.KeyFrame && item.FramePath is not null)
                .Select(item => (Id: item.FramePath!, Time: item.ElapsedMilliseconds)))
            .GroupBy(frame => frame.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(frame => Math.Abs(frame.Time - midpoint))
            .Take(4)
            .Select(frame => frame.Id)
            .ToArray();
        return candidates;
    }

    public IReadOnlyList<FrameEvidence> ReadFrameEvidence(int maxFrames = int.MaxValue)
    {
        var result = new List<FrameEvidence>();
        foreach (var action in Trace.Actions.Where(a => !a.Redacted).OrderBy(a => a.StartMilliseconds))
        {
            if (action.Before is not null) result.Add(ReadFrame(action.Before));
            if (action.After is not null) result.Add(ReadFrame(action.After));
        }
        foreach (var item in Trace.Events.Where(e => !e.Redacted && e.Kind == TraceEventKind.KeyFrame && e.FramePath is not null).OrderBy(e => e.ElapsedMilliseconds))
        {
            try { result.Add(new FrameEvidence(item.ElapsedMilliseconds, item.Detail ?? "key frame", _store.ReadFrame(item.FramePath!), item.FramePath, RoleFor(item.Detail))); } catch { }
        }
        return result.Where(r => r.Png.Length > 0).DistinctBy(r => r.Id).Take(maxFrames).ToArray();
    }

    public IReadOnlyList<ActionEvidence> ReadActionEvidence()
    {
        return Trace.Actions.Where(a => !a.Redacted).OrderBy(a => a.Order)
            .Select(a => a with { Before = a.Before is null ? null : ReadFrame(a.Before), After = a.After is null ? null : ReadFrame(a.After) })
            .ToArray();
    }

    public IReadOnlyList<AudioWindowEvidence> ReadAudioWindows(int windowMilliseconds = 30000)
    {
        if (Trace.AudioChunks.Count == 0) return AudioWav.Length > 44 ? [new AudioWindowEvidence(0, (AudioWav.Length - 44) / 32L, AudioWav)] : [];
        var result = new List<AudioWindowEvidence>();
        var windowBytes = Math.Max(32000, windowMilliseconds * 32);
        var pcm = new MemoryStream();
        long windowStart = 0;
        foreach (var chunk in Trace.AudioChunks.OrderBy(c => c.StartMilliseconds))
        {
            if (pcm.Length == 0) windowStart = chunk.StartMilliseconds;
            var data = _store.ReadAudioChunk(chunk.Path);
            if (data.Length > 44) pcm.Write(data, 44, data.Length - 44);
            if (pcm.Length >= windowBytes)
            {
                result.Add(new AudioWindowEvidence(windowStart, windowStart + pcm.Length / 32L, BuildWav(pcm.ToArray())));
                pcm.SetLength(0);
            }
        }
        if (pcm.Length > 0) result.Add(new AudioWindowEvidence(windowStart, windowStart + pcm.Length / 32L, BuildWav(pcm.ToArray())));
        return result;
    }

    private string NextFrameName(string suffix) => $"{Interlocked.Increment(ref _frame) - 1:0000}-{suffix}";

    private static bool SameTarget(UiTarget? first, UiTarget? second) =>
        first is null && second is null ||
        first is not null && second is not null &&
        string.Equals(first.ProcessName, second.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.WindowTitle, second.WindowTitle, StringComparison.Ordinal) &&
        string.Equals(first.AutomationId ?? first.Name, second.AutomationId ?? second.Name, StringComparison.Ordinal);

    private static FrameRole RoleFor(string? reason) => reason switch
    {
        "initial" => FrameRole.Initial,
        "final" => FrameRole.Final,
        "marker" => FrameRole.Marker,
        "trajectory context" => FrameRole.Periodic,
        _ => FrameRole.Periodic
    };

    private FrameEvidence ReadFrame(FrameEvidence frame)
    {
        try { return frame with { Png = frame.Id is null ? frame.Png : _store.ReadFrame(frame.Id) }; }
        catch (Exception ex) { Trace.Notes.Add("Frame evidence warning: " + ex.Message); return frame with { Png = [] }; }
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _captureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        for (var wait = 0; wait < 20 && Volatile.Read(ref _captureInProgress) != 0; wait++)
            await Task.Delay(10).ConfigureAwait(false);
        try { await Task.WhenAll(_pendingActions).ConfigureAwait(false); } catch { }
        _stopped = true;
        _clock.Stop();
        Trace.EndedAt = DateTimeOffset.Now;
        CaptureAndStoreFrame("final", FrameRole.Final);
        Trace.Events.Add(new TraceEvent(_clock.ElapsedMilliseconds, TraceEventKind.SessionStopped, "Recording finished", null, null, null, null));
        AudioWav = _microphone.StopAndGetWav();
        if (Trace.HasAudio && AudioWav.Length <= 44)
            Trace.Notes.Add("The microphone opened but returned no PCM samples. Check Windows microphone privacy access and the selected input device.");
        else if (Trace.HasAudio && !MicrophoneHasSignal)
            Trace.Notes.Add("Microphone samples were captured but the measured peak was near silence; narration may be too quiet to transcribe.");
        if (AudioWav.Length > 44) BuildAudioChunks(AudioWav);
        _store.WriteAudio(AudioWav);
        _store.WriteTrace(Trace);
    }

    private void BuildAudioChunks(byte[] wav)
    {
        var pcm = wav.AsSpan(44).ToArray();
        foreach (var range in _redactedRanges)
        {
            var start = Math.Clamp((int)(range.Start * 32), 0, pcm.Length);
            var end = Math.Clamp((int)(range.End * 32), start, pcm.Length);
            Array.Clear(pcm, start, end - start);
        }
        AudioWav = BuildWav(pcm);
        var chunkBytes = 160000; // five seconds at 16 kHz, 16-bit mono
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, pcm.Length - offset);
            var start = offset / 32L;
            var end = (offset + length) / 32L;
            var redacted = _redactedRanges.Any(r => r.Start < end && r.End > start);
            var data = new byte[length];
            Array.Copy(pcm, offset, data, 0, length);
            if (redacted) Array.Clear(data);
            var path = _store.WriteAudioChunk($"{start:0000000}", BuildWav(data));
            Trace.AudioChunks.Add(new AudioChunkEvidence(start, end, path, redacted));
        }
    }

    private static byte[] BuildWav(byte[] pcm)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        return output.ToArray();
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();
    public void DeleteTemporarySession() => _store.DeleteAfterSave();
    public void Dispose() { try { Stop(); } catch { } _captureTimer.Dispose(); _microphone.Dispose(); }
}
