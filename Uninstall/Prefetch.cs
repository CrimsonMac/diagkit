using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DiagKit.Uninstall;

/// <summary>Reads the paths stored in a Windows Prefetch (.pf) file.</summary>
public static class Prefetch
{
    private const ushort COMPRESSION_FORMAT_XPRESS_HUFF = 4;

    /// <summary>
    /// The file's contents as UTF-16 text, decompressed first when needed (Windows 10 and 11 store Prefetch files
    /// as "MAM\x04" + size + XPRESS Huffman data). Only used to search for paths, so non-text bytes don't matter.
    /// </summary>
    public static string ReadStrings(byte[] file)
    {
        byte[] data = file;
        if (file.Length > 8 && file[0] == (byte)'M' && file[1] == (byte)'A' && file[2] == (byte)'M' && file[3] == 4)
        {
            data = Decompress(file);
        }

        // Paths are UTF-16; decode at both byte alignments so an odd string offset cannot hide one.
        return Encoding.Unicode.GetString(data) + "\n" + (data.Length > 1 ? Encoding.Unicode.GetString(data, 1, data.Length - 1) : "");
    }

    internal static byte[] Decompress(byte[] file)
    {
        int size = BitConverter.ToInt32(file, 4);
        if (size <= 0 || size > 64 * 1024 * 1024)
        {
            throw new InvalidOperationException("Unexpected Prefetch size.");
        }

        int status = RtlGetCompressionWorkSpaceSize(COMPRESSION_FORMAT_XPRESS_HUFF, out uint workspaceSize, out _);
        if (status != 0)
        {
            throw new InvalidOperationException("RtlGetCompressionWorkSpaceSize failed: 0x" + status.ToString("X"));
        }

        var output = new byte[size];
        var compressed = new byte[file.Length - 8];
        Buffer.BlockCopy(file, 8, compressed, 0, compressed.Length);
        var workspace = new byte[workspaceSize];
        status = RtlDecompressBufferEx(COMPRESSION_FORMAT_XPRESS_HUFF, output, (uint)output.Length, compressed, (uint)compressed.Length, out uint final, workspace);
        if (status != 0)
        {
            throw new InvalidOperationException("RtlDecompressBufferEx failed: 0x" + status.ToString("X"));
        }

        if (final != output.Length)
        {
            Array.Resize(ref output, (int)final);
        }

        return output;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkspaceSize, out uint fragmentWorkspaceSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlDecompressBufferEx(ushort format, byte[] uncompressed, uint uncompressedSize, byte[] compressed,
        uint compressedSize, out uint finalUncompressedSize, byte[] workspace);
}

/// <summary>Reads the target of a .lnk shortcut through the shell's own ShellLink object.</summary>
public static class ShellLink
{
    public static string? TargetOf(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link != null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    }
}
