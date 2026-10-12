using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiagKit.Interop;

public static class IconLoader
{
    /// <summary>
    /// Extracts the exe's own icon at 48 px (crisp at 150% scaling for a 32 px slot).
    /// Safe to call from a background thread: the result is frozen.
    /// </summary>
    public static object? Load(string exePath)
    {
        IntPtr large = IntPtr.Zero;
        IntPtr small = IntPtr.Zero;
        try
        {
            const uint size = 48 | (16u << 16);
            int hr = NativeMethods.SHDefExtractIconW(exePath, 0, 0, out large, out small, size);
            if (hr != 0 || large == IntPtr.Zero)
            {
                return null;
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(large);
            }

            if (small != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(small);
            }
        }
    }
}
