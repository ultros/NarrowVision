using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace NovusVision;

public partial class HotkeyWindow : Window
{
    private sealed record ShortcutRow(ShortcutAction Action, string Name, string Gesture)
    {
        public string AutomationLabel => $"{Name}, {Gesture}";
    }

    private readonly Func<HotkeySettings, string?> _save;
    private HotkeySettings _draft;
    private ShortcutAction? _recording;

    internal HotkeyWindow(HotkeySettings current, Func<HotkeySettings, string?> save)
    {
        _draft = current;
        _save = save;
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        PreviewKeyDown += HandleRecording;
        RefreshRows();
    }

    private void RefreshRows()
    {
        Rows.ItemsSource = HotkeySettings.Actions.Select(action => new ShortcutRow(
            action, HotkeySettings.Name(action), _draft[action]?.Label ?? "Disabled")).ToArray();
    }

    private void RecordClicked(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        _recording = (ShortcutAction)button.Tag;
        Feedback.Text = $"Press a shortcut for {HotkeySettings.Name(_recording.Value).ToLowerInvariant()}.";
        SaveButton.IsEnabled = false;
    }

    private void HandleRecording(object sender, KeyEventArgs args)
    {
        if (_recording is not { } action)
            return;
        args.Handled = true;
        if (args.IsRepeat)
            return;

        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
            return;

        if (key == Key.Escape && modifiers == ModifierKeys.None && action != ShortcutAction.Cancel)
        {
            FinishRecording("Recording canceled.");
            return;
        }

        bool disable = key == Key.Back && modifiers == ModifierKeys.None;
        var shortcut = new Shortcut(key, modifiers);
        if (!disable && !shortcut.IsValid(action))
        {
            Feedback.Text = "Use Ctrl or Alt with another key. F12 is reserved by Windows.";
            return;
        }

        var candidate = _draft.With(action, disable ? null : shortcut);
        if (candidate.Validate() is { } error)
        {
            Feedback.Text = error;
            return;
        }
        _draft = candidate;
        FinishRecording(disable ? "Shortcut disabled." : $"Recorded {shortcut.Label}.");
    }

    private void FinishRecording(string status)
    {
        _recording = null;
        RefreshRows();
        Feedback.Text = status;
        SaveButton.IsEnabled = true;
        SaveButton.Focus();
    }

    private void RestoreClicked(object sender, RoutedEventArgs args)
    {
        _draft = new();
        FinishRecording("Defaults restored. Save to apply them.");
    }

    private void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (_save(_draft) is { } error)
        {
            Feedback.Text = error;
            return;
        }
        DialogResult = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs args) => DialogResult = false;
}
