using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace NovusVision;

internal enum MouseButton { Left, Right, Middle }

internal sealed record ClickTarget(Native.Point Position, nint Window, nint Root, uint Process,
    nint Foreground, Native.Rect Bounds)
{
    public static ClickTarget? At(Native.Point point, nint window, uint process)
    {
        nint root = Native.Root(window);
        if (root == 0 || process == 0 || !Native.GetWindowRect(root, out var bounds))
            return null;
        return new(point, window, root, process, Native.GetForegroundWindow(), bounds);
    }

    public bool IsCurrent()
    {
        nint window = Native.WindowFromPoint(Position);
        if (window != Window || Native.Root(window) != Root || Native.GetForegroundWindow() != Foreground)
            return false;
        Native.GetWindowThreadProcessId(window, out uint process);
        return process == Process && Native.GetWindowRect(Root, out var bounds) && bounds == Bounds;
    }
}

internal sealed record ClickAttempt(ClickTarget Target, MouseButton Button, GuardMode Mode);

internal sealed class MouseGuard : IDisposable
{
    private sealed record Press(Native.Point Position, ClickTarget? Target, GuardMode Mode, bool Modified);

    private sealed class Replay(ClickAttempt attempt)
    {
        public ClickAttempt Attempt { get; } = attempt;
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly uint _process = (uint)Environment.ProcessId;
    private readonly nuint _replayTag = (nuint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private readonly Press?[] _presses = new Press?[3];
    private readonly Action<ClickAttempt> _onClick;
    private readonly Action<string> _onStatus;
    private readonly Native.MouseHookCallback _callback;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;
    private nint _hook;
    private Settings? _active;
    private Replay? _replay;
    private Replay? _releaseReplay;
    private int _waiting;
    private bool _reportedError;

    public MouseGuard(Action<ClickAttempt> onClick, Action<string> onStatus)
    {
        _onClick = onClick;
        _onStatus = onStatus;
        _callback = OnMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "NovusVision mouse hook" };
        _thread.Start();
    }

    public Task<bool> Started => _started.Task;
    public void Apply(Settings? settings) => Volatile.Write(ref _active, settings);
    public void Complete() => Interlocked.Exchange(ref _waiting, 0);

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, 0, 0, 0, 0);
        _hook = Native.SetWindowsHookEx(Native.WhMouseLl, _callback, Native.GetModuleHandle(null), 0);
        _started.SetResult(_hook != 0);
        if (_hook == 0)
            return;
        try
        {
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
                Native.DispatchMessage(in message);
        }
        finally
        {
            Native.UnhookWindowsHookEx(_hook);
        }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code == 0)
        {
            try
            {
                if (HandleButton((int)message, data))
                    return 1;
            }
            catch (Exception)
            {
                if (!_reportedError)
                {
                    _reportedError = true;
                    _onStatus("Click Guard hit an input error. Restart NovusVision to restore its checks.");
                }
            }
        }
        return Native.CallNextHookEx(_hook, code, message, data);
    }

    private bool HandleButton(int message, nint data)
    {
        MouseButton button;
        bool down;
        switch (message)
        {
            case 0x0201: button = MouseButton.Left; down = true; break;
            case 0x0202: button = MouseButton.Left; down = false; break;
            case 0x0204: button = MouseButton.Right; down = true; break;
            case 0x0205: button = MouseButton.Right; down = false; break;
            case 0x0207: button = MouseButton.Middle; down = true; break;
            case 0x0208: button = MouseButton.Middle; down = false; break;
            default: return false;
        }

        var input = Marshal.PtrToStructure<Native.MouseEvent>(data);
        if ((input.Flags & 1) != 0 && input.ExtraInfo == _replayTag)
            return FilterReplay(button, down, input.Position);

        var settings = Volatile.Read(ref _active);
        if (!down)
        {
            Press? press = _presses[(int)button];
            _presses[(int)button] = null;
            if (press is null)
                return false;
            if (settings is not null && press.Target is { } target && Volatile.Read(ref _waiting) == 0)
                FinishPress(input, press, target, button);
            return true;
        }

        if (settings is null || !IsGuarded(button, settings))
            return false;

        nint window = Native.WindowFromPoint(input.Position);
        Native.GetWindowThreadProcessId(window, out uint process);
        if (process == _process)
            return false;

        var clicked = Volatile.Read(ref _waiting) == 0 ? ClickTarget.At(input.Position, window, process) : null;
        _presses[(int)button] = new(input.Position, clicked, settings.GuardMode, Native.HasModifiers());
        return true;
    }

    private void FinishPress(Native.MouseEvent input, Press press, ClickTarget target, MouseButton button)
    {
        if (Math.Abs(input.Position.X - press.Position.X) > Native.GetSystemMetrics(68) ||
            Math.Abs(input.Position.Y - press.Position.Y) > Native.GetSystemMetrics(69))
        {
            _onStatus("Drag canceled. Temporarily turn off Click Guard to drag or select text.");
            return;
        }
        if (press.Modified || Native.HasModifiers())
        {
            _onStatus("Modified click canceled. Release the modifier keys and click again.");
            return;
        }
        if (Interlocked.CompareExchange(ref _waiting, 1, 0) == 0)
            _onClick(new(target, button, press.Mode));
    }

    private static bool IsGuarded(MouseButton button, Settings settings)
    {
        if (button == MouseButton.Middle)
            return settings.GuardMiddleButton;
        MouseButton primary = Native.GetSystemMetrics(23) == 0 ? MouseButton.Left : MouseButton.Right;
        return button == primary;
    }

    private bool FilterReplay(MouseButton button, bool down, Native.Point point)
    {
        if (!down && _releaseReplay is { } release && release.Attempt.Button == button)
        {
            _releaseReplay = null;
            release.Result.TrySetResult(true);
            return false;
        }
        var replay = Volatile.Read(ref _replay);
        if (down && replay is not null && replay.Attempt.Button == button)
        {
            var target = replay.Attempt.Target;
            if (point == target.Position && !Native.HasModifiers() && target.IsCurrent())
            {
                _releaseReplay = replay;
                return false;
            }
            replay.Result.TrySetResult(false);
        }
        return true;
    }

    public async Task<bool> ApproveAsync(ClickAttempt attempt)
    {
        if (!attempt.Target.IsCurrent() || Native.HasModifiers())
            return false;

        var point = attempt.Target.Position;
        Rectangle desktop = Forms.SystemInformation.VirtualScreen;
        if (!desktop.Contains(point.X, point.Y))
            return false;

        (uint down, uint up) = attempt.Button switch
        {
            MouseButton.Left => (0x0002u, 0x0004u),
            MouseButton.Right => (0x0008u, 0x0010u),
            _ => (0x0020u, 0x0040u)
        };
        var replay = new Replay(attempt);
        Volatile.Write(ref _replay, replay);
        try
        {
            Native.Input[] inputs =
            [
                new() { Mouse = new Native.MouseInput
                    {
                        X = AbsoluteCoordinate(point.X - desktop.Left, desktop.Width),
                        Y = AbsoluteCoordinate(point.Y - desktop.Top, desktop.Height),
                        Flags = 0x8000 | 0x4000 | 0x0001,
                        ExtraInfo = _replayTag
                    }
                },
                new() { Mouse = new Native.MouseInput { Flags = down, ExtraInfo = _replayTag } },
                new() { Mouse = new Native.MouseInput { Flags = up, ExtraInfo = _replayTag } }
            ];
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
            if (sent == 2)
                Native.SendInput(1, [inputs[2]], Marshal.SizeOf<Native.Input>());
            if (sent < 2)
                return false;

            try
            {
                return await replay.Result.Task.WaitAsync(TimeSpan.FromMilliseconds(800));
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            Volatile.Write(ref _replay, null);
        }
    }

    private static int AbsoluteCoordinate(int pixel, int size)
        => (int)((pixel * 65536L + 32768) / size);

    public void Dispose()
    {
        if (_threadId != 0)
            Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0);
    }
}
