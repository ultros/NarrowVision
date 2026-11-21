using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace NovusVision;

internal enum ShortcutAction { Fog = 1, Guard, Settings, Pause, Proceed, Cancel }

internal sealed record Shortcut(Key Key, ModifierKeys Modifiers)
{
    private const ModifierKeys Supported = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    public bool IsValid(ShortcutAction action)
    {
        if ((Modifiers & ~Supported) != 0 || !Enum.IsDefined(Key))
            return false;
        if (Key == Key.Escape && Modifiers == ModifierKeys.None)
            return action == ShortcutAction.Cancel;

        int virtualKey = KeyInterop.VirtualKeyFromKey(Key);
        return (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 &&
            ((virtualKey >= 0x30 && virtualKey <= 0x5A) ||
             (virtualKey >= 0x60 && virtualKey <= 0x87 && virtualKey != 0x7B) ||
             (virtualKey >= 0x21 && virtualKey <= 0x2E) ||
             virtualKey is 0x08 or 0x0D or 0x1B or 0x20 ||
             (virtualKey >= 0xBA && virtualKey <= 0xE2));
    }

    internal uint NativeModifiers => (uint)Modifiers | Native.ModNoRepeat;
    internal uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    internal string Label
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            parts.Add(Key switch
            {
                >= Key.D0 and <= Key.D9 => Key.ToString()[1..],
                Key.Escape => "Esc",
                Key.Enter => "Enter",
                Key.Back => "Backspace",
                Key.Prior => "Page Up",
                Key.Next => "Page Down",
                _ => Key.ToString()
            });
            return string.Join("+", parts);
        }
    }
}

internal sealed record HotkeySettings
{
    private const ModifierKeys DefaultModifiers = ModifierKeys.Control | ModifierKeys.Shift;
    private static readonly string FilePath = Path.Combine(Settings.DirectoryPath, "hotkeys.json");
    public static readonly ShortcutAction[] Actions = Enum.GetValues<ShortcutAction>();
    public Shortcut? Fog { get; init; } = new(Key.F8, DefaultModifiers);
    public Shortcut? Guard { get; init; } = new(Key.F9, DefaultModifiers);
    public Shortcut? OpenSettings { get; init; } = new(Key.F10, DefaultModifiers);
    public Shortcut? Pause { get; init; } = new(Key.F11, DefaultModifiers);
    public Shortcut? Proceed { get; init; } = new(Key.Enter, DefaultModifiers);
    public Shortcut? Cancel { get; init; } = new(Key.Escape, ModifierKeys.None);

    public Shortcut? this[ShortcutAction action] => action switch
    {
        ShortcutAction.Fog => Fog,
        ShortcutAction.Guard => Guard,
        ShortcutAction.Settings => OpenSettings,
        ShortcutAction.Pause => Pause,
        ShortcutAction.Proceed => Proceed,
        ShortcutAction.Cancel => Cancel,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public HotkeySettings With(ShortcutAction action, Shortcut? shortcut) => action switch
    {
        ShortcutAction.Fog => this with { Fog = shortcut },
        ShortcutAction.Guard => this with { Guard = shortcut },
        ShortcutAction.Settings => this with { OpenSettings = shortcut },
        ShortcutAction.Pause => this with { Pause = shortcut },
        ShortcutAction.Proceed => this with { Proceed = shortcut },
        ShortcutAction.Cancel => this with { Cancel = shortcut },
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public static string Name(ShortcutAction action) => action switch
    {
        ShortcutAction.Fog => "Toggle fog overlay",
        ShortcutAction.Guard => "Toggle Click Guard",
        ShortcutAction.Settings => "Open settings",
        ShortcutAction.Pause => "Pause or resume",
        ShortcutAction.Proceed => "Proceed once",
        ShortcutAction.Cancel => "Cancel a pending click",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public string? Validate()
    {
        var assigned = new HashSet<(uint, uint)>();
        foreach (var action in Actions)
        {
            if (this[action] is not { } shortcut)
                continue;
            if (!shortcut.IsValid(action))
                return $"Choose a valid shortcut for {Name(action)}.";
            if (!assigned.Add((shortcut.VirtualKey, shortcut.NativeModifiers)))
                return $"Assign {shortcut.Label} to only one action.";
        }
        return null;
    }

    public static (HotkeySettings Value, string? Notice) Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return (new(), null);
            var value = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(FilePath), Settings.JsonOptions);
            if (value is not null && value.Validate() is null)
                return (value, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return (new(), "Saved hotkeys could not be loaded. Default hotkeys are in use.");
    }

    public void Save() => Settings.Write(FilePath, this);
}
