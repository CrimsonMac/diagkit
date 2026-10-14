using System;
using System.Windows;
using Microsoft.Win32;

namespace DiagKit.Services;

/// <summary>
/// Swaps the colour palette while DiagKit runs. Theme.xaml merges the dark palette as its default; the chosen one is
/// merged after it at application level and wins, and every brush is a DynamicResource, so open pages recolour
/// in place. "System" follows Windows' app mode (Settings > Personalization > Colors) and tracks changes to it.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _palette;
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _listening;

    /// <summary>Raised on the UI thread after the palette changed; windows refresh their frame colours.</summary>
    public static event Action? Changed;

    /// <summary>True when the palette in use is the dark one.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>What "System" currently resolves to, for the Settings page.</summary>
    public static bool SystemIsDark => !SystemUsesLightTheme();

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        if (choice == ThemeChoice.System && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        bool dark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemIsDark,
        };

        if (_palette != null && dark == IsDark)
        {
            return;
        }

        Application? app = Application.Current;
        if (app == null)
        {
            return;
        }

        var palette = new ResourceDictionary
        {
            Source = new Uri($"/DiagKit;component/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        if (_palette != null)
        {
            app.Resources.MergedDictionaries.Remove(_palette);
        }

        app.Resources.MergedDictionaries.Add(palette);
        _palette = palette;
        IsDark = dark;
        Changed?.Invoke();
    }

    /// <summary>Unhooks from SystemEvents; its handlers are static and would otherwise outlive the app.</summary>
    public static void Shutdown()
    {
        if (_listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listening = false;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Switching Windows between light and dark arrives as "General"; SystemEvents may call from its own thread.
        if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(ThemeChoice.System)));
        }
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            // Older Windows or a locked-down profile: stay with the dark default.
            return false;
        }
    }
}
