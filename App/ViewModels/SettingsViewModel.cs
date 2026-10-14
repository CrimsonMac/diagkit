using System;
using System.Reflection;
using System.Windows.Input;
using DiagKit.Services;

namespace DiagKit.ViewModels;

/// <summary>The Settings page. Every change applies at once and is saved to settings.json in the kit.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private bool _saveFailed;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        ResetCommand = new RelayCommand(Reset);
        ThemeManager.Changed += () => OnPropertyChanged(nameof(ThemeHint));
    }

    /// <summary>Raised after any setting changed, so other pages can pick up new defaults.</summary>
    public event Action? Changed;

    public ICommand ResetCommand { get; }

    public ThemeChoice Theme
    {
        get => _settings.Theme;
        set => Set(_settings.Theme, value, v => _settings.Theme = v, () => ThemeManager.Apply(value));
    }

    public AppPage StartPage
    {
        get => _settings.StartPage;
        set => Set(_settings.StartPage, value, v => _settings.StartPage = v);
    }

    public int StressSeconds
    {
        get => _settings.StressSeconds;
        set => Set(_settings.StressSeconds, value, v => _settings.StressSeconds = v);
    }

    public bool ScanStartup
    {
        get => _settings.ScanStartup;
        set => Set(_settings.ScanStartup, value, v => _settings.ScanStartup = v);
    }

    public bool OfferPawnIo
    {
        get => _settings.OfferPawnIo;
        set => Set(_settings.OfferPawnIo, value, v => _settings.OfferPawnIo = v);
    }

    public bool OpenHtmlAfterCheck
    {
        get => _settings.OpenHtmlAfterCheck;
        set => Set(_settings.OpenHtmlAfterCheck, value, v => _settings.OpenHtmlAfterCheck = v);
    }

    public string ThemeHint => Theme == ThemeChoice.System
        ? $"Follows Windows' app mode, which is {(ThemeManager.SystemIsDark ? "dark" : "light")} right now."
        : "Stays the same whatever Windows is set to.";

    public bool SaveFailed
    {
        get => _saveFailed;
        private set => SetProperty(ref _saveFailed, value);
    }

    public string LocationText => $"Saved in the kit as {Kit.KitLayout.SettingsFile}, so they travel with the USB stick. Nothing is written to this PC.";

    public string VersionText => "DiagKit " + AppInfo.Version;

    /// <summary>"© 2026 CrimsonMac", from the Copyright set in Directory.Build.props.</summary>
    public string CopyrightText { get; } =
        (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "")
        .Replace("Copyright (C)", "©").Replace("Copyright", "©").Trim();

    public string LicenseText => "Free software under the GNU General Public License v3.0. The bundled tools belong to their publishers.";

    private void Set<T>(T current, T value, Action<T> assign, Action? after = null, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Equals(current, value))
        {
            return;
        }

        assign(value);
        OnPropertyChanged(name);
        after?.Invoke();
        Save();
    }

    private void Reset()
    {
        var defaults = new AppSettings();
        _settings.Theme = defaults.Theme;
        _settings.StartPage = defaults.StartPage;
        _settings.StressSeconds = defaults.StressSeconds;
        _settings.ScanStartup = defaults.ScanStartup;
        _settings.OfferPawnIo = defaults.OfferPawnIo;
        _settings.OpenHtmlAfterCheck = defaults.OpenHtmlAfterCheck;
        ThemeManager.Apply(_settings.Theme);
        OnPropertyChanged(string.Empty);
        Save();
    }

    private void Save()
    {
        SaveFailed = !_settings.Save();
        OnPropertyChanged(nameof(ThemeHint));
        Changed?.Invoke();
    }
}
