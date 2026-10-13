using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DiagKit.Controls;

/// <summary>Gives a window with the standard title bar the dark caption and theme border of the app.</summary>
public static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
                if (window.TryFindResource("Rail") is SolidColorBrush caption)
                {
                    // COLORREF is 0x00BBGGRR.
                    int color = caption.Color.R | (caption.Color.G << 8) | (caption.Color.B << 16);
                    DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref color, sizeof(int));
                }

                if (window.TryFindResource("Line") is SolidColorBrush line)
                {
                    int border = line.Color.R | (line.Color.G << 8) | (line.Color.B << 16);
                    DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
                }
            }
            catch
            {
                // Older Windows ignores these attributes.
            }
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
