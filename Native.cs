using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NarrowVision;

internal static class Native
{
    public const int WmHotkey = 0x0312;
    public const int WmMouseActivate = 0x0021;
    public const int WmQuit = 0x0012;
    public const int WhMouseLl = 14;
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    private const int ExStyle = -20;
    private const int ExTransparent = 0x20, ExToolWindow = 0x80, ExNoActivate = 0x08000000;
    private static readonly nint Topmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Point(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Rect(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseEvent
    {
        public Point Position;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        // The native union is naturally aligned, including padding after Type on x64.
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    public delegate nint MouseHookCallback(int code, nint message, nint data);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint window, StringBuilder title, int length);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, MouseHookCallback callback, nint module, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] public static extern int GetMessage(out Message message, nint window, uint filterMin, uint filterMax);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out Message message, nint window, uint filterMin, uint filterMax, uint remove);
    [DllImport("user32.dll")] public static extern nint DispatchMessage(in Message message);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? moduleName);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);

    public static void MakeNonActivating(nint window, bool transparent)
    {
        int additions = ExToolWindow | ExNoActivate;
        if (transparent)
            additions |= ExTransparent;
        SetWindowLong(window, ExStyle, GetWindowLong(window, ExStyle) | additions);
    }

    public static void PlaceTopmost(nint window, int x, int y, int width, int height)
        => SetWindowPos(window, Topmost, x, y, width, height, 0x0010 | 0x0200);

    public static void UseDarkTitleBar(nint window)
    {
        const int useImmersiveDarkMode = 20;
        int enabled = 1;
        DwmSetWindowAttribute(window, useImmersiveDarkMode, in enabled, sizeof(int));
    }

    public static bool IsDown(int key) => GetAsyncKeyState(key) < 0;

    public static bool HasModifiers()
        => IsDown(0x10) || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);

    public static nint Root(nint window) => GetAncestor(window, 2);
}
