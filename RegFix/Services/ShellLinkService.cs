using System.Reflection;
using System.Runtime.InteropServices;
using RegFix.Models;

namespace RegFix.Services;

public static class ShellLinkService
{
    public static ShortcutRecord? Read(string shortcutPath, string programsRoot)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut is null)
            {
                return null;
            }

            var target = ReadProperty(shortcut, "TargetPath");
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            return new ShortcutRecord
            {
                Name = Path.GetFileNameWithoutExtension(shortcutPath),
                OriginalProgramsRelativePath = Path.GetRelativePath(programsRoot, shortcutPath),
                TargetPath = Environment.ExpandEnvironmentVariables(target),
                Arguments = ReadProperty(shortcut, "Arguments"),
                WorkingDirectory = Environment.ExpandEnvironmentVariables(ReadProperty(shortcut, "WorkingDirectory")),
                IconLocation = Environment.ExpandEnvironmentVariables(ReadProperty(shortcut, "IconLocation")),
                Source = "StartMenu"
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    public static string CreateRestored(ShortcutRecord record, string targetPath)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var relative = SanitizeRelativePath(record.OriginalProgramsRelativePath);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            relative = Path.Combine("RegFix 恢复", SanitizeFileName(record.Name) + ".lnk");
        }

        var destination = Path.GetFullPath(Path.Combine(programs, "RegFix 恢复", relative));
        var safeRoot = Path.GetFullPath(Path.Combine(programs, "RegFix 恢复")) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("快捷方式目标路径超出开始菜单恢复目录。");
        }

        destination = EnsureUniquePath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件。");
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [destination]);
            if (shortcut is null)
            {
                throw new InvalidOperationException("无法创建快捷方式对象。");
            }

            SetProperty(shortcut, "TargetPath", targetPath);
            SetProperty(shortcut, "Arguments", record.Arguments);
            SetProperty(shortcut, "WorkingDirectory", ResolveWorkingDirectory(record, targetPath));
            if (!string.IsNullOrWhiteSpace(record.IconLocation))
            {
                SetProperty(shortcut, "IconLocation", record.IconLocation);
            }

            SetProperty(shortcut, "Description", $"由 RegFix 从旧系统迁移：{record.Name}");
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return destination;
        }
        catch
        {
            try
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            catch
            {
                // Preserve the original failure; the user can remove a partial link manually.
            }

            throw;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static string ReadProperty(object target, string propertyName)
    {
        try
        {
            return Convert.ToString(target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, target, null)) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static void SetProperty(object target, string propertyName, string value)
    {
        target.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, null, target, [value]);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static string ResolveWorkingDirectory(ShortcutRecord record, string targetPath)
    {
        if (!string.IsNullOrWhiteSpace(record.WorkingDirectory) && Directory.Exists(record.WorkingDirectory))
        {
            return record.WorkingDirectory;
        }

        return Path.GetDirectoryName(targetPath) ?? "";
    }

    private static string SanitizeRelativePath(string value)
    {
        var parts = value.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != "." && part != "..")
            .Select(SanitizeFileName)
            .ToArray();
        return parts.Length == 0 ? "" : Path.Combine(parts);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "恢复的软件" : safe;
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为恢复的快捷方式分配唯一名称。");
    }
}
