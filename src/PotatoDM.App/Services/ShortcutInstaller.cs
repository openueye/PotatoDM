using System.IO;
using System.Runtime.InteropServices;

namespace PotatoDM.App.Services;

public static class ShortcutInstaller
{
    public static string CreateDesktopShortcut() => Create(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public static string CreateStartMenuShortcut() => Create(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs));

    private static string Create(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("无法确定快捷方式目录。");
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("无法确定土豆下载器程序位置。");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "土豆下载器.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Windows 快捷方式服务不可用。");
        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic dynamicShell = shell!;
            shortcut = dynamicShell.CreateShortcut(path);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = executable;
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(executable)!;
            dynamicShortcut.Description = "土豆下载器 · 多连接下载加速";
            dynamicShortcut.IconLocation = $"{executable},0";
            dynamicShortcut.Save();
            return path;
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
