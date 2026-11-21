using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace NovusVision;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NovusVision";
    private const string LegacyValueName = "NarrowVision";
    private const string StartupTaskId = "NovusVisionStartup";
    public const string TrayArgument = "--tray";
    public const string UninstallArgument = "--uninstall-cleanup";

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint nameLength, nint name);

    private static bool HasPackageIdentity
    {
        get
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, 0) == 122; // ERROR_INSUFFICIENT_BUFFER
        }
    }

    public static bool IsLogonActivation()
        => HasPackageIdentity && AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;

    public static async Task<bool> IsRegisteredAsync()
    {
        if (HasPackageIdentity)
            return Enabled((await StartupTask.GetAsync(StartupTaskId)).State);

        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }

    public static async Task SetRegisteredAsync(bool enabled)
    {
        if (!HasPackageIdentity)
        {
            SetRegistryStartup(enabled);
            return;
        }

        var task = await StartupTask.GetAsync(StartupTaskId);
        if (!enabled)
        {
            task.Disable();
            return;
        }

        var state = await task.RequestEnableAsync();
        if (!Enabled(state))
            throw new InvalidOperationException(state == StartupTaskState.DisabledByUser
                ? "Startup was disabled in Windows. Open Settings → Apps → Startup to enable NovusVision."
                : "Windows did not enable NovusVision at sign-in.");
    }

    private static bool Enabled(StartupTaskState state)
        => state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    private static void SetRegistryStartup(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, CurrentCommand(), RegistryValueKind.String);
    }

    public static void MigrateLegacyRegistration()
    {
        if (HasPackageIdentity)
            return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is not string { Length: > 0 })
            return;
        if (key.GetValue(ValueName) is null)
            key.SetValue(ValueName, CurrentCommand(), RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static void RemoveRegistrationForCurrentExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string command &&
            command.Equals(CurrentCommand(), StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string CurrentCommand()
    {
        string path = Environment.ProcessPath ?? "";
        if (path.Length == 0 || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open NovusVision.exe directly to enable Windows startup.");

        string command = $"\"{path}\" {TrayArgument}";
        if (command.Length > 260)
            throw new InvalidOperationException("Move NovusVision to a shorter file path to enable Windows startup.");

        return command;
    }
}
