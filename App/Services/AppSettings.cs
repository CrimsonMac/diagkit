using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiagKit.ViewModels;

namespace DiagKit.Services;

public enum ThemeChoice
{
    System,
    Dark,
    Light,
}

/// <summary>
/// The user's preferences. They are kept in settings.json in the kit folder, next to DiagKit.exe, so they travel
/// with the USB stick and nothing is written to the PC being checked. On a read-only kit they last for the session.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>The page DiagKit opens on.</summary>
    public AppPage StartPage { get; set; } = AppPage.Tools;

    /// <summary>Default CPU load test for a full health check: 0 (off), 30 or 120 seconds.</summary>
    public int StressSeconds { get; set; }

    public bool ScanStartup { get; set; } = true;

    /// <summary>Tick "Install the CPU temperature driver" by default when PawnIO is missing.</summary>
    public bool OfferPawnIo { get; set; } = true;

    /// <summary>Open report.html in the browser when a check finishes.</summary>
    public bool OpenHtmlAfterCheck { get; set; }

    public static string FilePath => Path.Combine(AppPaths.BaseDir, Kit.KitLayout.SettingsFile);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (s != null)
                {
                    s.StressSeconds = s.StressSeconds is 0 or 30 or 120 ? s.StressSeconds : 0;
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            // A damaged file falls back to the defaults; the next save replaces it.
            AppPaths.LogError("Could not read " + FilePath, ex);
        }

        return new AppSettings();
    }

    /// <summary>Writes settings.json. Returns false when the kit is read-only.</summary>
    public bool Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
