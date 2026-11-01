using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PotatoDM.Core.Storage;

/// <summary>
/// Marks a working file as sparse so ranges no worker has written yet stay as
/// holes instead of allocated zero clusters. Best effort: FAT32, exFAT and most
/// network shares reject the flag, and the download continues normally.
/// </summary>
public static class SparseFile
{
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 49, METHOD_BUFFERED, FILE_ANY_ACCESS).
    private const uint FsctlSetSparse = 0x000900C4;
    private const uint InvalidFileSize = 0xFFFFFFFF;

    public static bool TryMarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return DeviceIoControl(
                handle,
                FsctlSetSparse,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bytes actually allocated on disk, or <see langword="null"/> when the
    /// platform or filesystem cannot report it. For a sparse file this excludes
    /// the untouched holes.
    /// </summary>
    public static long? GetAllocatedSize(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var low = GetCompressedFileSize(path, out var high);
            if (low == InvalidFileSize && Marshal.GetLastWin32Error() != 0)
            {
                return null;
            }

            return ((long)high << 32) | low;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string lpFileName, out uint lpFileSizeHigh);
}
