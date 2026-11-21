using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NovusVision;

public partial class App : Application
{
    private readonly TargetInspector _inspector = new();
    private readonly DispatcherTimer _pauseTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly HashSet<ShortcutAction> _registeredShortcuts = new();
    private Settings _settings = new();
    private HotkeySettings _shortcuts = new();
    private SettingsWindow _window = null!;
    private FogOverlay _fog = null!;
    private MouseGuard _guard = null!;
    private Forms.NotifyIcon _tray = null!;
    private Forms.ToolStripMenuItem _trayFog = null!, _trayGuard = null!, _trayPause = null!;
    private Forms.ToolStripMenuItem _trayStartup = null!, _trayLoadProfile = null!;
    private Icon? _trayIcon;
    private Mutex? _singleInstance;
    private EventWaitHandle? _showExisting;
    private RegisteredWaitHandle? _showRegistration;
    private HwndSource? _messages;
    private PendingClick? _pending;
    private DecisionWindow? _decision;
    private long _pausedUntil;
    private bool _guardAvailable;
    private bool _shuttingDown;
    private bool _settingsDirty;
    private bool _startWithWindows;
    private bool _startupAvailable = true;
    private bool _changingStartup;
    private bool _editingShortcuts;
    private string? _status;
    private string? _startupNotice;

    private sealed class PendingClick(ClickAttempt attempt)
    {
        public ClickAttempt Attempt { get; } = attempt;
        public TargetDetails Details { get; set; } = TargetDetails.Unknown;
        public bool Approving { get; set; }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains(WindowsStartup.UninstallArgument, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                WindowsStartup.RemoveRegistrationForCurrentExecutable();
                Shutdown();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                Shutdown(1);
            }
            return;
        }

        bool startInTray = e.Args.Contains(WindowsStartup.TrayArgument, StringComparer.OrdinalIgnoreCase) ||
            WindowsStartup.IsLogonActivation();
        // Keep the shared identity so the legacy portable build cannot run a second mouse hook.
        _showExisting = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NarrowVision.Show");
        _singleInstance = new Mutex(false, @"Local\NarrowVision", out bool firstInstance);
        if (!firstInstance)
        {
            if (!startInTray)
                _showExisting.Set();
            Shutdown();
            return;
        }

        string? migrationNotice = MigrateLegacyInstallation();
        (_settings, _startupNotice) = Settings.Load();
        (_shortcuts, var hotkeyNotice) = HotkeySettings.Load();
        var (defaultProfile, profileNotice) = Settings.LoadDefault();
        _settings = defaultProfile ?? _settings;
        _startupNotice = profileNotice ?? hotkeyNotice ?? migrationNotice ??
            (defaultProfile is null ? _startupNotice : null);
        _window = new SettingsWindow();
        MainWindow = _window;
        _window.Closing += HideToTray;
        _window.StateChanged += (_, _) =>
        {
            if (!_shuttingDown && _window.WindowState == WindowState.Minimized)
                _window.Hide();
        };
        _window.LoadSettings(_settings);
        _window.SettingsChanged += UpdateSettings;
        _window.PauseRequested += TogglePause;
        _window.StartupRequested += SetWindowsStartup;
        _window.SaveProfileRequested += SaveDefaultProfile;
        _window.LoadProfileRequested += LoadDefaultProfile;
        _window.ClearProfileRequested += ClearDefaultProfile;
        _window.EditShortcutsRequested += EditShortcuts;
        _window.StatusChanged += SetStatus;
        await RefreshStartupPreference();
        _window.SetProfile(Settings.HasDefault);
        _fog = new FogOverlay(Dispatcher);
        _guard = new MouseGuard(click => Dispatcher.BeginInvoke(() => InspectClick(click)),
            status => Dispatcher.BeginInvoke(() => SetStatus(status)));
        _pauseTimer.Tick += (_, _) => UpdatePause();
        _saveTimer.Tick += (_, _) => SaveSettings();
        _focusTimer.Tick += (_, _) => CheckForeground();
        CreateHotkeyWindow();
        CreateTray();
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showExisting, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);
        ApplySettings();
        if (!startInTray)
            ShowSettings();

        _guardAvailable = await _guard.Started;
        if (_shuttingDown)
            return;
        if (!_guardAvailable)
            _startupNotice = "Click Guard could not start. Close NovusVision from the tray and reopen it.";
        ApplySettings();
    }

    private void CreateHotkeyWindow()
    {
        _messages = new HwndSource(new HwndSourceParameters("NovusVision shortcuts") { ParentWindow = new nint(-3) });
        _messages.AddHook(OnWindowMessage);
        RegisterGlobalShortcuts();
    }

    private void RegisterGlobalShortcuts()
    {
        foreach (var action in HotkeySettings.Actions)
        {
            if (action < ShortcutAction.Proceed)
                Register(action, _shortcuts[action]);
        }
    }

    private bool Register(ShortcutAction action, Shortcut? shortcut)
    {
        if (shortcut is null)
            return true;
        if (_messages is not { } source ||
            !Native.RegisterHotKey(source.Handle, (int)action, shortcut.NativeModifiers, shortcut.VirtualKey))
            return false;
        _registeredShortcuts.Add(action);
        return true;
    }

    private void Unregister(ShortcutAction action)
    {
        if (_registeredShortcuts.Remove(action) && _messages is { } source)
            Native.UnregisterHotKey(source.Handle, (int)action);
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Native.WmHotkey)
            return 0;
        handled = true;
        var action = (ShortcutAction)(int)wParam;
        if (_editingShortcuts || !_registeredShortcuts.Contains(action) || _shortcuts[action] is not { } shortcut)
            return 0;
        uint messageGesture = unchecked((uint)(nuint)lParam) & 0xFFFF000F;
        if (messageGesture != ((shortcut.VirtualKey << 16) | (uint)shortcut.Modifiers))
            return 0;
        switch (action)
        {
            case ShortcutAction.Fog: ToggleFog(); break;
            case ShortcutAction.Guard: ToggleGuard(); break;
            case ShortcutAction.Settings: ShowSettings(); break;
            case ShortcutAction.Pause: TogglePause(); break;
            case ShortcutAction.Proceed when _decision?.CanProceed == true: _ = ApproveClick(); break;
            case ShortcutAction.Cancel: CancelClick("Click canceled."); break;
        }
        return 0;
    }

    private void CreateTray()
    {
        _trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new IceMenuColors()),
            BackColor = Color.FromArgb(20, 35, 56),
            ForeColor = Color.FromArgb(239, 247, 255)
        };
        menu.Items.Add("Open NovusVision", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _trayFog = new Forms.ToolStripMenuItem("Fog overlay", null, (_, _) => ToggleFog());
        _trayGuard = new Forms.ToolStripMenuItem("Click Guard", null, (_, _) => ToggleGuard());
        _trayPause = new Forms.ToolStripMenuItem("Pause for 5 min", null, (_, _) => TogglePause());
        _trayLoadProfile = new Forms.ToolStripMenuItem("Load default profile", null, (_, _) => LoadDefaultProfile());
        _trayStartup = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => SetWindowsStartup(!_startWithWindows));
        menu.Items.AddRange([_trayFog, _trayGuard, _trayPause, new Forms.ToolStripSeparator(), _trayLoadProfile, _trayStartup]);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit NovusVision", null, (_, _) => ExitApplication());
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
        menu.Opening += async (_, _) =>
        {
            await RefreshStartupPreference();
            if (!_shuttingDown)
                RefreshStatus();
        };
        _tray.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                ShowSettings();
        };
    }

    private sealed class IceMenuColors : Forms.ProfessionalColorTable
    {
        private static readonly Color Panel = Color.FromArgb(20, 35, 56);
        private static readonly Color Selection = Color.FromArgb(52, 87, 121);
        private static readonly Color Border = Color.FromArgb(67, 103, 134);
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color CheckBackground => Selection;
        public override Color CheckSelectedBackground => Selection;
        public override Color SeparatorLight => Panel;
        public override Color SeparatorDark => Border;
    }

    private void ShowSettings()
    {
        if (_shuttingDown)
            return;
        _ = RefreshStartupPreference();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        if (!_window.IsVisible)
            _window.Show();
        _window.Topmost = false;
        _window.Topmost = true;
        _window.Activate();
    }

    private void HideToTray(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown)
            return;
        e.Cancel = true;
        _window.Hide();
    }

    private bool IsPaused => _pausedUntil != 0;
    private bool GuardActive => _settings.GuardEnabled && _guardAvailable && !IsPaused;

    private void TogglePause()
    {
        _pausedUntil = IsPaused ? 0 : Environment.TickCount64 + 5 * 60 * 1000;
        if (IsPaused)
            _pauseTimer.Start();
        else
            _pauseTimer.Stop();
        _status = null;
        ApplySettings();
    }

    private void UpdatePause()
    {
        if (_pausedUntil <= Environment.TickCount64)
        {
            _pauseTimer.Stop();
            _pausedUntil = 0;
            ApplySettings();
        }
        else
            RefreshStatus();
    }

    private void ToggleFog() => ReplaceSettings(_settings with { FogEnabled = !_settings.FogEnabled });
    private void ToggleGuard()
    {
        if (_guardAvailable)
            ReplaceSettings(_settings with { GuardEnabled = !_settings.GuardEnabled });
    }

    private void ReplaceSettings(Settings settings)
    {
        _window.LoadSettings(settings);
        UpdateSettings(settings);
    }

    private void UpdateSettings(Settings settings)
    {
        bool coverageChanged = settings.GuardMode != _settings.GuardMode ||
            settings.GuardMiddleButton != _settings.GuardMiddleButton;
        _settings = settings;
        _status = null;
        if (coverageChanged)
            CancelClick(null);
        _settingsDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        ApplySettings();
    }

    private void ApplySettings()
    {
        _guard.Apply(GuardActive ? _settings : null);
        if (!GuardActive)
            CancelClick(null);
        _fog.Apply(_settings, _settings.FogEnabled && !IsPaused);
        if (_window.IsVisible && !_window.IsActive)
        {
            _window.Topmost = false;
            _window.Topmost = true;
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        bool fogActive = _settings.FogEnabled && !IsPaused;
        string state = $"Fog {(fogActive ? "on" : "off")}  ·  Click Guard {(GuardActive ? "on" : "off")}";
        if (IsPaused)
        {
            var remaining = TimeSpan.FromMilliseconds(Math.Max(0, _pausedUntil - Environment.TickCount64));
            state = $"Paused for {remaining:mm\\:ss}";
        }
        _tray.Text = $"NovusVision · {state}";
        _trayFog.Checked = _settings.FogEnabled;
        _trayGuard.Checked = _settings.GuardEnabled;
        _trayGuard.Enabled = _guardAvailable;
        _trayPause.Text = IsPaused ? "Resume now" : "Pause for 5 min";
        _trayStartup.Checked = _startWithWindows;
        _trayStartup.Enabled = _startupAvailable && !_changingStartup;
        _trayLoadProfile.Enabled = Settings.HasDefault;
        _window.SetState(_status ?? _startupNotice ?? state, IsPaused, _guardAvailable,
            ShortcutLabel(ShortcutAction.Fog), ShortcutLabel(ShortcutAction.Guard),
            ShortcutLabel(ShortcutAction.Settings), ShortcutLabel(ShortcutAction.Pause));
    }

    private string ShortcutLabel(ShortcutAction action)
        => _shortcuts[action] is null ? "Disabled" : RegisteredShortcutLabel(action) ?? "Unavailable";

    private string? RegisteredShortcutLabel(ShortcutAction action)
        => _registeredShortcuts.Contains(action) ? _shortcuts[action]?.Label : null;

    private void EditShortcuts()
    {
        CancelClick(null);
        _editingShortcuts = true;
        foreach (var action in HotkeySettings.Actions)
            Unregister(action);
        try
        {
            var editor = new HotkeyWindow(_shortcuts, SaveShortcuts) { Owner = _window };
            if (editor.ShowDialog() == true)
                _status = "Keyboard shortcuts saved.";
        }
        finally
        {
            _editingShortcuts = false;
            if (!_shuttingDown)
            {
                RegisterGlobalShortcuts();
                RefreshStatus();
            }
        }
    }

    private string? SaveShortcuts(HotkeySettings candidate)
    {
        if (candidate.Validate() is { } error)
            return error;
        try
        {
            foreach (var action in HotkeySettings.Actions)
            {
                if (!Register(action, candidate[action]))
                    return $"{candidate[action]?.Label} is unavailable. Choose a different shortcut.";
            }
            candidate.Save();
            _shortcuts = candidate;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Keyboard shortcuts could not be saved.";
        }
        finally
        {
            foreach (var action in HotkeySettings.Actions)
                Unregister(action);
        }
    }

    private static string? MigrateLegacyInstallation()
    {
        try
        {
            Settings.MigrateLegacySettings();
            WindowsStartup.MigrateLegacyRegistration();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return "Some settings from the previous version could not be migrated.";
        }
    }

    private void SetStatus(string text)
    {
        if (_shuttingDown)
            return;
        _status = text;
        RefreshStatus();
    }

    private async Task RefreshStartupPreference()
    {
        if (_changingStartup)
            return;
        try
        {
            bool registered = await WindowsStartup.IsRegisteredAsync();
            if (!_changingStartup)
                _startWithWindows = registered;
            _startupAvailable = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or COMException)
        {
            _startupAvailable = false;
            _startupNotice ??= "Windows startup settings are unavailable.";
        }
        if (!_shuttingDown)
            _window.SetStartup(_startWithWindows, _startupAvailable && !_changingStartup);
    }

    private async void SetWindowsStartup(bool enabled)
    {
        if (_changingStartup)
            return;
        _changingStartup = true;
        _window.SetStartup(_startWithWindows, available: false);
        _trayStartup.Enabled = false;
        string status;
        try
        {
            await WindowsStartup.SetRegisteredAsync(enabled);
            status = enabled ? "NovusVision will start in the tray when you sign in." : "Start with Windows is off.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or COMException)
        {
            status = exception is InvalidOperationException ? exception.Message : "Windows startup settings could not be updated.";
        }
        finally
        {
            _changingStartup = false;
        }
        await RefreshStartupPreference();
        SetStatus(status);
    }

    private void SaveDefaultProfile()
    {
        try
        {
            _settings.SaveDefault();
            _window.SetProfile(saved: true);
            SetStatus("Saved as the default startup profile.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus("The default profile could not be saved.");
        }
    }

    private void LoadDefaultProfile()
    {
        var (profile, notice) = Settings.LoadDefault();
        if (profile is null)
        {
            _window.SetProfile(Settings.HasDefault);
            SetStatus(notice ?? "No default profile has been saved.");
            return;
        }

        ReplaceSettings(profile);
        SetStatus("Default profile loaded.");
    }

    private void ClearDefaultProfile()
    {
        try
        {
            Settings.ClearDefault();
            _window.SetProfile(saved: false);
            SetStatus("Default profile cleared. Your current settings will load next time.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus("The default profile could not be cleared.");
        }
    }

    private async void InspectClick(ClickAttempt attempt)
    {
        if (!GuardActive || attempt.Mode != _settings.GuardMode ||
            (attempt.Button == MouseButton.Middle && !_settings.GuardMiddleButton))
        {
            _guard.Complete();
            return;
        }

        var pending = new PendingClick(attempt);
        _pending = pending;
        var details = await _inspector.InspectAsync(attempt.Target);
        if (_pending != pending)
            return;
        if (!attempt.Target.IsCurrent())
        {
            CancelClick("Click canceled because its window changed.");
            return;
        }

        pending.Details = details;
        if (attempt.Mode == GuardMode.Controls && details.Kind == TargetKind.Background)
        {
            pending.Approving = true;
            await ReplayClick(pending, showFeedback: false);
            return;
        }

        if (!_editingShortcuts)
        {
            Register(ShortcutAction.Proceed, _shortcuts.Proceed);
            Register(ShortcutAction.Cancel, _shortcuts.Cancel);
        }
        _decision = new DecisionWindow();
        _decision.Decided += proceed =>
        {
            if (proceed)
                _ = ApproveClick();
            else
                CancelClick("Click canceled.");
        };
        _decision.Expired += () => CancelClick("Click canceled after the time limit.");
        _decision.Present(attempt, details, DescribeWindow(attempt.Target), _settings.DecisionSeconds,
            RegisteredShortcutLabel(ShortcutAction.Proceed), RegisteredShortcutLabel(ShortcutAction.Cancel));
        _focusTimer.Start();
    }

    private static string DescribeWindow(ClickTarget target)
    {
        var builder = new StringBuilder(260);
        Native.GetWindowText(target.Root, builder, builder.Capacity);
        string title = TargetInspector.SafeLabel(builder.ToString());
        try
        {
            using var process = Process.GetProcessById((int)target.Process);
            string app = TargetInspector.SafeLabel(process.ProcessName, 40);
            return title.Length == 0 ? app : $"{app} · {title}";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return title.Length > 0 ? title : "Desktop window";
        }
    }

    private void CheckForeground()
    {
        if (_pending is { } pending && Native.GetForegroundWindow() != pending.Attempt.Target.Foreground)
            CancelClick("Click canceled because focus moved to another window.");
    }

    private async Task ApproveClick()
    {
        if (_pending is not { Approving: false } pending || _decision?.CanProceed != true)
            return;

        pending.Approving = true;
        CloseDecision();
        long deadline = Environment.TickCount64 + 1600;
        while (Native.HasModifiers() && _pending == pending && Environment.TickCount64 < deadline)
            await Task.Delay(20);
        if (_pending != pending)
            return;

        if (pending.Details.Kind != TargetKind.Unknown)
        {
            var current = await _inspector.InspectAsync(pending.Attempt.Target);
            if (_pending != pending)
                return;
            if (!pending.Details.Matches(current))
            {
                CancelClick("Click canceled because the target changed or could not be verified.");
                NotifyCanceled();
                return;
            }
        }
        await ReplayClick(pending, showFeedback: true);
    }

    private async Task ReplayClick(PendingClick pending, bool showFeedback)
    {
        bool approved = await _guard.ApproveAsync(pending.Attempt);
        if (_pending != pending)
            return;
        if (approved)
            CancelClick(showFeedback ? "Click allowed once." : null);
        else
        {
            CancelClick("Click canceled. Windows could not deliver it to the original window.");
            if (showFeedback)
                NotifyCanceled();
        }
    }

    private void NotifyCanceled()
    {
        if (!_window.IsActive && _status is { } status)
            _tray.ShowBalloonTip(3500, "NovusVision", status, Forms.ToolTipIcon.Info);
    }

    private void CloseDecision()
    {
        _focusTimer.Stop();
        Unregister(ShortcutAction.Proceed);
        Unregister(ShortcutAction.Cancel);
        _decision?.Close();
        _decision = null;
    }

    private void CancelClick(string? status)
    {
        if (_pending is null)
            return;
        _pending = null;
        CloseDecision();
        _guard.Complete();
        if (status is not null)
            SetStatus(status);
    }

    private void SaveSettings()
    {
        _saveTimer.Stop();
        if (!_settingsDirty)
            return;
        try
        {
            _settings.Save();
            _settingsDirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!_shuttingDown)
                SetStatus("Preferences could not be saved. The current settings are still in use.");
        }
    }

    private void ExitApplication()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;
        CancelClick(null);
        SaveSettings();
        _pauseTimer.Stop();
        _guard.Dispose();
        _fog.Dispose();
        _showRegistration?.Unregister(null);
        foreach (var action in HotkeySettings.Actions)
            Unregister(action);
        _messages?.Dispose();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showExisting?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
