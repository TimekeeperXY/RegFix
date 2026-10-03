using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using RegFix.Models;

namespace RegFix.Services;

public static class UninstallRegistrationService
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static string RestoreUserEntry(RestoreUninstallEntryItem item, IEnumerable<string> selectedRoots)
    {
        var record = item.Original;
        if (record.IsWindowsInstaller || IsMsiCommand(record.UninstallString ?? ""))
        {
            throw new InvalidOperationException("Windows Installer 项目不能通过重建显示项来恢复。");
        }

        if (record.NoRemove)
        {
            throw new InvalidOperationException("该软件在原系统中标记为不可卸载。");
        }

        if (string.IsNullOrWhiteSpace(record.DisplayName))
        {
            throw new InvalidOperationException("缺少软件名称。");
        }

        var executable = Path.GetFullPath(item.UninstallerPath);
        if (!File.Exists(executable) || !IsInsideSelectedRoot(executable, selectedRoots))
        {
            throw new FileNotFoundException("卸载程序必须存在于你明确选择的扫描目录中。", executable);
        }

        if (IsWindowsBinary(executable) || IsCommandInterpreter(executable))
        {
            throw new InvalidOperationException("为避免建立危险或系统级命令，不能把 Windows 系统程序登记为软件卸载器。");
        }

        var uninstallArguments = record.UninstallArguments ?? "";
        if (uninstallArguments.Length > 2048
            || uninstallArguments.Any(character => char.IsControl(character) || character is '\r' or '\n' or '&' or '|' or '<' or '>' or '^'))
        {
            throw new InvalidOperationException("卸载参数包含不安全字符或超过长度限制，已拒绝恢复该条目。");
        }

        if (IsDuplicate(record.DisplayName, record.Publisher))
        {
            throw new InvalidOperationException("当前系统已存在同名软件条目，已跳过以避免重复显示。");
        }

        var keyName = GetKeyName(record);
        using var root = Registry.CurrentUser.CreateSubKey(UninstallRoot, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的卸载列表注册表项。");

        using (var existing = root.OpenSubKey(keyName, writable: false))
        {
            if (existing is not null)
            {
                throw new InvalidOperationException("该迁移条目已存在，不会重复创建。");
            }
        }

        using var key = root.CreateSubKey(keyName, writable: true)
            ?? throw new InvalidOperationException("无法创建当前用户卸载列表项。");
        try
        {
            var installLocation = Directory.Exists(item.InstallLocation)
                ? Path.GetFullPath(item.InstallLocation)
                : Path.GetDirectoryName(executable) ?? "";
            key.SetValue("DisplayName", record.DisplayName, RegistryValueKind.String);
            key.SetValue("RegFixOwned", 1, RegistryValueKind.DWord);
            if (!string.IsNullOrWhiteSpace(record.Publisher))
            {
                key.SetValue("Publisher", record.Publisher, RegistryValueKind.String);
            }

            if (!string.IsNullOrWhiteSpace(record.DisplayVersion))
            {
                key.SetValue("DisplayVersion", record.DisplayVersion, RegistryValueKind.String);
            }

            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                key.SetValue("InstallLocation", installLocation, RegistryValueKind.String);
            }

            var uninstallCommand = Quote(executable);
            if (!string.IsNullOrWhiteSpace(uninstallArguments))
            {
                uninstallCommand += " " + uninstallArguments;
            }

            key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
            key.SetValue("DisplayIcon", Quote(executable) + ",0", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            if (!string.IsNullOrWhiteSpace(record.HelpLink))
            {
                key.SetValue("HelpLink", record.HelpLink, RegistryValueKind.String);
            }

            if (!string.IsNullOrWhiteSpace(record.UrlInfoAbout))
            {
                key.SetValue("URLInfoAbout", record.UrlInfoAbout, RegistryValueKind.String);
            }

            if (!string.IsNullOrWhiteSpace(record.Comments))
            {
                key.SetValue("Comments", record.Comments, RegistryValueKind.String);
            }

            return keyName;
        }
        catch
        {
            key.Dispose();
            try
            {
                root.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
            }
            catch
            {
                // Keep the original write failure visible; manual cleanup can inspect the named key.
            }

            throw;
        }
    }

    public static string GetKeyName(SoftwareRecord record)
        => "RegFix-" + CreateStableSuffix(record);

    public static bool IsOwnedEntry(SoftwareRecord record, out string keyName)
    {
        keyName = GetKeyName(record);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(UninstallRoot, writable: false);
            using var key = root?.OpenSubKey(keyName, writable: false);
            return key is not null
                && Convert.ToInt32(key.GetValue("RegFixOwned", 0)) == 1
                && string.Equals(Convert.ToString(key.GetValue("DisplayName")), record.DisplayName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void RemoveOwnedEntry(RestoreUninstallEntryItem item)
    {
        var expectedKey = GetKeyName(item.Original);
        if (!item.IsRegFixEntry || !string.Equals(expectedKey, item.RegistrationKeyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只允许撤销由 RegFix 创建的当前用户卸载列表项。");
        }

        using var root = Registry.CurrentUser.OpenSubKey(UninstallRoot, writable: true)
            ?? throw new InvalidOperationException("当前用户卸载列表不存在。");
        using (var key = root.OpenSubKey(expectedKey, writable: false))
        {
            if (key is null
                || Convert.ToInt32(key.GetValue("RegFixOwned", 0)) != 1
                || !string.Equals(Convert.ToString(key.GetValue("DisplayName")), item.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("注册表项所有权验证失败，未删除。");
            }
        }

        root.DeleteSubKeyTree(expectedKey, throwOnMissingSubKey: false);
    }

    private static bool IsDuplicate(string displayName, string publisher)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallRoot = baseKey.OpenSubKey(UninstallRoot, writable: false);
                    if (uninstallRoot is null)
                    {
                        continue;
                    }

                    foreach (var keyName in uninstallRoot.GetSubKeyNames())
                    {
                        using var key = uninstallRoot.OpenSubKey(keyName, writable: false);
                        if (key is null)
                        {
                            continue;
                        }

                        var existingName = Convert.ToString(key.GetValue("DisplayName")) ?? "";
                        if (!string.Equals(existingName.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var existingPublisher = Convert.ToString(key.GetValue("Publisher")) ?? "";
                        if (string.IsNullOrWhiteSpace(publisher)
                            || string.IsNullOrWhiteSpace(existingPublisher)
                            || string.Equals(existingPublisher.Trim(), publisher.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // The registration will still be protected from exact-key collisions below.
                }
            }
        }

        return false;
    }

    private static string CreateStableSuffix(SoftwareRecord record)
    {
        var identity = string.Join("|", record.UninstallKeyName, record.DisplayName, record.Publisher, record.DisplayVersion);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(bytes)[..20];
    }

    private static bool IsMsiCommand(string value)
        => (value ?? "").Contains("msiexec", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsBinary(string path)
    {
        var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandInterpreter(string path)
    {
        var name = Path.GetFileName(path);
        return new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "reg.exe", "regedit.exe", "sc.exe", "schtasks.exe" }
            .Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsInsideSelectedRoot(string filePath, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(filePath).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Quote(string value)
        => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
