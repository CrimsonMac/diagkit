using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DiagKit.Interop;
using DiagKit.Services;
using DiagKit.ViewModels;

namespace DiagKit;

public partial class MainWindow : Window
{
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateMaximizeState();
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
    }

    public bool Confirm(string message)
        => MessageBox.Show(this, message, "DiagKit", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyFrame();
        ThemeManager.Changed += ApplyFrame;
        Closed += (_, _) => ThemeManager.Changed -= ApplyFrame;
    }

    /// <summary>Matches the parts of the frame Windows draws (dark mode hint, border) to the current palette.</summary>
    private void ApplyFrame()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int dark = ThemeManager.IsDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            if (TryFindResource("Line") is SolidColorBrush line)
            {
                // COLORREF is 0x00BBGGRR.
                int border = line.Color.R | (line.Color.G << 8) | (line.Color.B << 16);
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
        }
        catch
        {
            // Windows 10 ignores the Windows 11 attributes; nothing to do.
        }
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        // The window is on screen before any disk work starts, so the app never looks frozen at launch.
        if (_started || DataContext is not MainViewModel vm)
        {
            return;
        }

        _started = true;
        await vm.StartAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.IsCheckRunning && !Confirm("A check is still running. Close DiagKit anyway? The partial report will be discarded."))
        {
            e.Cancel = true;
            return;
        }

        if (vm.HasRunningTools && !Confirm($"{vm.RunningToolNames} is still running. Close DiagKit anyway?\n\nThe tool keeps running, but its registry entries will not be cleaned up afterwards."))
        {
            e.Cancel = true;
        }
    }

    private void UpdateMaximizeState()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "\uE923" : "\uE922";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";

        // A maximized WindowChrome window extends past the screen by the resize frame; pull content back in.
        Root.Margin = max ? FrameThickness() : new Thickness(0);
    }

    private Thickness FrameThickness()
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            uint dpiValue = (uint)Math.Round(96 * dpi.DpiScaleX);
            int x = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSIZEFRAME, dpiValue) +
                    NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpiValue);
            int y = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYSIZEFRAME, dpiValue) +
                    NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpiValue);
            return new Thickness(x / dpi.DpiScaleX, y / dpi.DpiScaleY, x / dpi.DpiScaleX, y / dpi.DpiScaleY);
        }
        catch
        {
            return new Thickness(8);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
