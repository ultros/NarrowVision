using System;
using System.IO;
using Microsoft.Win32;

namespace NarrowVision;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NarrowVision";
    public const string TrayArgument = "--tray";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }

    public static void SetRegistered(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string path = Environment.ProcessPath ?? "";
        if (path.Length == 0 || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open the portable NarrowVision executable to enable Windows startup.");

        string command = $"\"{path}\" {TrayArgument}";
        if (command.Length > 260)
            throw new InvalidOperationException("Move NarrowVision to a shorter file path to enable Windows startup.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
