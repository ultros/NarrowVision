using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NarrowVision;

internal enum FogMode { Reveal, Dim }
internal enum ApertureShape { Ellipse, RoundedRectangle }
internal enum MonitorScope { All, Cursor }
internal enum GuardMode { All, Controls }

internal sealed record Settings
{
    public bool FogEnabled { get; init; } = true;
    public FogMode FogMode { get; init; } = FogMode.Reveal;
    public ApertureShape Shape { get; init; } = ApertureShape.Ellipse;
    public MonitorScope Monitors { get; init; } = MonitorScope.All;
    public int Width { get; init; } = 500;
    public int Height { get; init; } = 340;
    public int Darkness { get; init; } = 100;
    public int Feather { get; init; }
    public bool Outline { get; init; } = true;
    public bool GuardEnabled { get; init; }
    public GuardMode GuardMode { get; init; } = GuardMode.All;
    public bool GuardMiddleButton { get; init; } = true;
    public int DecisionSeconds { get; init; } = 15;

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NarrowVision");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly string DefaultPath = Path.Combine(DirectoryPath, "default-profile.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static (Settings Value, string? Notice) Load()
    {
        var (settings, notice) = Read(FilePath, "Preferences could not be loaded. Factory defaults are in use.");
        return (settings ?? new Settings(), notice);
    }

    public static (Settings? Value, string? Notice) LoadDefault()
        => Read(DefaultPath, "The saved default profile could not be loaded.");

    public static bool HasDefault => File.Exists(DefaultPath);

    private static (Settings? Value, string? Notice) Read(string filePath, string errorMessage)
    {
        try
        {
            if (!File.Exists(filePath))
                return (null, null);
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(filePath), JsonOptions)
                ?? throw new JsonException();
            return (settings with
            {
                Width = Math.Clamp(settings.Width, 100, 1800),
                Height = Math.Clamp(settings.Height, 100, 1400),
                Darkness = Math.Clamp(settings.Darkness, 0, 100),
                Feather = Math.Clamp(settings.Feather, 0, 180),
                DecisionSeconds = Math.Clamp(settings.DecisionSeconds, 5, 45)
            }, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, errorMessage);
        }
    }

    public void Save() => Write(FilePath);
    public void SaveDefault() => Write(DefaultPath);
    public static void ClearDefault() => File.Delete(DefaultPath);

    private void Write(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryFile = filePath + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryFile, filePath, overwrite: true);
    }
}
