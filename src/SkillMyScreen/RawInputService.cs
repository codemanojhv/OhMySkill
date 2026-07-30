using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace SkillMyScreen;

public sealed class RawInputObserver : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const uint RimTypeMouse = 0;
    private const uint RieMouseLeftDown = 0x0001;
    private const uint RieMouseRightDown = 0x0004;
    private const uint RieMouseWheel = 0x0400;
    private const uint RieMouseLeftUp = 0x0002;
    private const uint RieMouseRightUp = 0x0008;
    private const ushort VkReturn = 0x0D;
    private const ushort VkEscape = 0x1B;
    private const ushort VkTab = 0x09;
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkAlt = 0x12;
    private HwndSource? _source;
    private RecordingController? _controller;
    private bool _leftDown;
    private bool _rightDown;
    private int _dragDistance;
    private long _lastClickTick;
    private bool _controlDown;
    private bool _altDown;
    private bool _shiftDown;

    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public uint Type, Size; public IntPtr Device, Param; }
    [StructLayout(LayoutKind.Sequential)] private struct RawMouse { public ushort Flags; public uint Buttons; public ushort ButtonData; public ushort Reserved; public int LastX, LastY; public uint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VKey, Message; public uint ExtraInformation; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint numDevices, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);

    public void Attach(HwndSource source, RecordingController controller)
    {
        _source = source; _controller = controller;
        var devices = new[]
        {
            new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevInputSink, Target = source.Handle },
            new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink, Target = source.Handle }
        };
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmInput || _controller is null) return IntPtr.Zero;
        uint size = 0;
        GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RawInputHeader>());
        if (size == 0) return IntPtr.Zero;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, (uint)Marshal.SizeOf<RawInputHeader>()) == uint.MaxValue) return IntPtr.Zero;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            var payload = IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>());
            if (header.Type == RimTypeMouse)
            {
                var mouse = Marshal.PtrToStructure<RawMouse>(payload);
                var buttons = mouse.Buttons;
                if (_leftDown) _dragDistance += Math.Abs(mouse.LastX) + Math.Abs(mouse.LastY);
                if ((buttons & RieMouseLeftDown) != 0 && !_leftDown) { _leftDown = true; _dragDistance = 0; }
                if ((buttons & RieMouseLeftUp) != 0 && _leftDown)
                {
                    _leftDown = false;
                    var target = UiAutomationService.AtCursor();
                    if (_dragDistance > 6)
                        _controller.RecordInput(TraceEventKind.Drag, "dragged with left mouse button", target);
                    else
                    {
                        var now = Environment.TickCount64;
                        var kind = now - _lastClickTick <= 400 ? TraceEventKind.DoubleClick : TraceEventKind.Click;
                        _lastClickTick = now;
                        _controller.RecordInput(kind, kind == TraceEventKind.DoubleClick ? "double-click" : "left click", target);
                    }
                }
                if ((buttons & RieMouseRightDown) != 0 && !_rightDown) { _rightDown = true; _controller.RecordInput(TraceEventKind.RightClick, "right click", UiAutomationService.AtCursor()); }
                if ((buttons & RieMouseRightUp) != 0) _rightDown = false;
                if ((buttons & RieMouseWheel) != 0) _controller.RecordInput(TraceEventKind.Scroll, "scroll", UiAutomationService.AtCursor());
            }
            else
            {
                var keyboard = Marshal.PtrToStructure<RawKeyboard>(payload);
                var released = (keyboard.Flags & 1) != 0;
                if (keyboard.VKey == VkControl) { _controlDown = !released; return IntPtr.Zero; }
                if (keyboard.VKey == VkAlt) { _altDown = !released; return IntPtr.Zero; }
                if (keyboard.VKey == VkShift) { _shiftDown = !released; return IntPtr.Zero; }
                if (released) return IntPtr.Zero;
                var target = UiAutomationService.Focused();
                if (keyboard.VKey is VkReturn or VkEscape or VkTab)
                {
                    var name = keyboard.VKey == VkReturn ? "Enter" : keyboard.VKey == VkEscape ? "Escape" : "Tab";
                    _controller.RecordInput(TraceEventKind.Shortcut, name, target);
                }
                else if (_controlDown || _altDown)
                {
                    var modifiers = string.Join("+", new[] { _controlDown ? "Ctrl" : null, _altDown ? "Alt" : null, _shiftDown ? "Shift" : null }.Where(x => x is not null));
                    _controller.RecordInput(TraceEventKind.Shortcut, $"{modifiers}+{KeyName(keyboard.VKey)}", target);
                }
                else
                {
                    _controller.RecordInput(TraceEventKind.TextEntry, target?.IsPassword == true ? "text entered in protected field" : "text-entry burst", target);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return IntPtr.Zero;
    }

    private static string KeyName(ushort value) =>
        value is >= 0x30 and <= 0x5A ? ((char)value).ToString() : $"VK_{value:X2}";

    public void Dispose()
    {
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            var devices = new[]
            {
                new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevRemove, Target = IntPtr.Zero },
                new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevRemove, Target = IntPtr.Zero }
            };
            RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        _source = null; _controller = null;
    }
}
