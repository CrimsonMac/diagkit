using System;
using System.Runtime.InteropServices;

namespace DiagKit.Interop;

internal static partial class NativeMethods
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_ROUND = 2;
    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHDefExtractIconW(string iconFile, int index, uint flags, out IntPtr large, out IntPtr small, uint iconSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr handle);

    // ---- Performance Data Helper (the API behind Task Manager's CPU "Speed")
    public const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Explicit)]
    public struct PdhCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhCounterValue value);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(IntPtr query);
}
