using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using RegFix.Models;

namespace RegFix.Services;

public static class AssociationRegistrationService
{
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    private const string ClassesPath = @"Software\Classes";
    private const string CapabilitiesRoot = @"Software\RegFix\Capabilities";

    public static string RegisterCandidate(RestoreAssociationItem item, IEnumerable<string> selectedRoots)
    {
        var applicationName = NormalizeDisplayName(item.ApplicationName);
        var executable = Path.GetFullPath(item.ExecutablePath);
        if (string.IsNullOrWhiteSpace(applicationName) || applicationName.Contains('\0'))
        {
            throw new InvalidOperationException("应用名称无效。");
        }

        if (!File.Exists(executable)
            || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)
            || !IsInsideSelectedRoot(executable, selectedRoots)
            || IsWindowsBinary(executable))
        {
            throw new InvalidOperationException("请选择扫描目录中的有效第三方 .exe 程序文件。");
        }

        var associations = item.Records
            .Where(RestoreScanner.IsSupportedAssociationIdentifier)
            .GroupBy(record => record.Identifier.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(128)
            .ToArray();
        if (associations.Length == 0)
        {
            throw new InvalidOperationException("没有可恢复的文件类型或协议。");
        }

        var appId = ShortHash(applicationName + "|" + executable);
        var capabilityPath = $@"{CapabilitiesRoot}\{appId}";
        var registeredName = MakeUniqueRegisteredName(applicationName, capabilityPath);
        var createdProgIds = new List<string>();
        var capabilityCreated = false;
        var registeredValueWritten = false;

        try
        {
            using (var capabilitiesRoot = Registry.CurrentUser.CreateSubKey(CapabilitiesRoot, writable: true)
                ?? throw new InvalidOperationException("无法创建当前用户的应用能力注册表目录。"))
            using (var existing = capabilitiesRoot.OpenSubKey(appId, writable: false))
            {
                if (existing is not null)
                {
                    throw new InvalidOperationException("该应用的 RegFix 关联候选已存在，不会覆盖。");
                }
            }

            using (var classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户的文件类型注册表目录。"))
            {
                foreach (var association in associations)
                {
                    var identifier = association.Identifier.Trim();
                    var progId = $"RegFix.Restored.{appId}.{ShortHash(identifier)[..10]}";
                    using (var existing = classes.OpenSubKey(progId, writable: false))
                    {
                        if (existing is not null)
                        {
                            throw new InvalidOperationException($"关联标识 {identifier} 已有同名 ProgID；没有覆盖它。");
                        }
                    }

                    using var progIdKey = classes.CreateSubKey(progId, writable: true)
                        ?? throw new InvalidOperationException($"无法为 {identifier} 创建 ProgID。");
                    createdProgIds.Add(progId);
                    progIdKey.SetValue("", $"{applicationName} ({identifier})", RegistryValueKind.String);
                    if (identifier[0] != '.')
                    {
                        progIdKey.SetValue("URL Protocol", "", RegistryValueKind.String);
                    }

                    using var command = progIdKey.CreateSubKey(@"shellopencommand", writable: true)
                        ?? throw new InvalidOperationException($"无法为 {identifier} 创建打开命令。");
                    command.SetValue("", $"\"{executable}\" \"%1\"", RegistryValueKind.String);
                }
            }

            using (var capabilities = Registry.CurrentUser.CreateSubKey(capabilityPath, writable: true)
                ?? throw new InvalidOperationException("无法创建应用关联能力信息。"))
            {
                capabilityCreated = true;
                capabilities.SetValue("ApplicationName", registeredName, RegistryValueKind.String);
                capabilities.SetValue("ApplicationDescription", $"{applicationName} · 从 RegFix 迁移包恢复的关联候选；默认应用需由用户确认。", RegistryValueKind.String);
                capabilities.SetValue("ApplicationIcon", $"\"{executable}\",0", RegistryValueKind.String);
                capabilities.SetValue("RegFixOwned", 1, RegistryValueKind.DWord);
                capabilities.SetValue("RegFixApplicationName", applicationName, RegistryValueKind.String);
                capabilities.SetValue("RegFixTargetSha256", HashFile(executable), RegistryValueKind.String);

                using var fileAssociations = capabilities.CreateSubKey("FileAssociations", writable: true);
                using var urlAssociations = capabilities.CreateSubKey("UrlAssociations", writable: true);
                if (fileAssociations is null || urlAssociations is null)
                {
                    throw new InvalidOperationException("无法写入应用关联声明。");
                }

                for (var index = 0; index < associations.Length; index++)
                {
                    var identifier = associations[index].Identifier.Trim();
                    var progId = $"RegFix.Restored.{appId}.{ShortHash(identifier)[..10]}";
                    if (identifier[0] == '.')
                    {
                        fileAssociations.SetValue(identifier, progId, RegistryValueKind.String);
                    }
                    else
                    {
                        urlAssociations.SetValue(identifier, progId, RegistryValueKind.String);
                    }
                }
            }

            using (var registeredApplications = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsPath, writable: true)
                ?? throw new InvalidOperationException("无法注册当前用户的默认应用候选。"))
            {
                registeredApplications.SetValue(registeredName, capabilityPath, RegistryValueKind.String);
                registeredValueWritten = true;
            }

            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
            return registeredName;
        }
        catch
        {
            if (registeredValueWritten)
            {
                try
                {
                    using var registeredApplications = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: true);
                    registeredApplications?.DeleteValue(registeredName, throwOnMissingValue: false);
                }
                catch { }
            }

            if (capabilityCreated)
            {
                try
                {
                    using var capabilitiesRoot = Registry.CurrentUser.OpenSubKey(CapabilitiesRoot, writable: true);
                    capabilitiesRoot?.DeleteSubKeyTree(appId, throwOnMissingSubKey: false);
                }
                catch { }
            }

            foreach (var progId in createdProgIds)
            {
                try
                {
                    using var classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true);
                    classes?.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
                }
                catch { }
            }

            throw;
        }
    }

    public static string GetCapabilityPath(string applicationName, string executable)
        => $@"{CapabilitiesRoot}\{ShortHash(NormalizeDisplayName(applicationName) + "|" + Path.GetFullPath(executable))}";

    public static bool TryFindRegisteredName(RestoreAssociationItem item, out string registeredName, out string capabilityPath)
    {
        registeredName = "";
        capabilityPath = "";
        if (string.IsNullOrWhiteSpace(item.ExecutablePath) || !File.Exists(item.ExecutablePath))
        {
            return false;
        }

        var targetHash = HashFile(item.ExecutablePath);
        try
        {
            using var registeredApplications = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: false);
            if (registeredApplications is null)
            {
                return false;
            }

            foreach (var valueName in registeredApplications.GetValueNames().Where(name => name.StartsWith("RegFix · ", StringComparison.OrdinalIgnoreCase)))
            {
                var path = Convert.ToString(registeredApplications.GetValue(valueName)) ?? "";
                if (!path.StartsWith(CapabilitiesRoot + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var capabilities = Registry.CurrentUser.OpenSubKey(path, writable: false);
                if (capabilities is null
                    || Convert.ToInt32(capabilities.GetValue("RegFixOwned", 0)) != 1
                    || !string.Equals(Convert.ToString(capabilities.GetValue("RegFixApplicationName")), item.ApplicationName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Convert.ToString(capabilities.GetValue("RegFixTargetSha256")), targetHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                registeredName = valueName;
                capabilityPath = path;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static void RemoveCandidate(RestoreAssociationItem item)
    {
        if (string.IsNullOrWhiteSpace(item.RegisteredAppName)
            || string.IsNullOrWhiteSpace(item.RegisteredCapabilityPath)
            || !item.RegisteredCapabilityPath.StartsWith(CapabilitiesRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("此应用没有可撤销的 RegFix 关联注册。");
        }

        (string Identifier, string ProgId, bool IsUrl)[] associations;
        using (var registeredApplications = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: false))
        using (var capabilities = Registry.CurrentUser.OpenSubKey(item.RegisteredCapabilityPath, writable: false))
        {
            var registeredPath = Convert.ToString(registeredApplications?.GetValue(item.RegisteredAppName)) ?? "";
            if (!string.Equals(registeredPath, item.RegisteredCapabilityPath, StringComparison.OrdinalIgnoreCase)
                || capabilities is null
                || Convert.ToInt32(capabilities.GetValue("RegFixOwned", 0)) != 1
                || !string.Equals(Convert.ToString(capabilities.GetValue("RegFixApplicationName")), item.ApplicationName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("关联注册所有权验证失败，未删除。");
            }

            var appId = item.RegisteredCapabilityPath[(item.RegisteredCapabilityPath.LastIndexOf('\\') + 1)..];
            associations = new[] { (Name: "FileAssociations", IsUrl: false), (Name: "UrlAssociations", IsUrl: true) }
                .SelectMany(section =>
                {
                    using var key = capabilities.OpenSubKey(section.Name, writable: false);
                    if (key is null)
                    {
                        return Array.Empty<(string Identifier, string ProgId, bool IsUrl)>();
                    }

                    return key.GetValueNames()
                        .Select(identifier => (Identifier: identifier, ProgId: Convert.ToString(key.GetValue(identifier)) ?? "", IsUrl: section.IsUrl))
                        .Where(value => value.ProgId.StartsWith($"RegFix.Restored.{appId}.", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                })
                .ToArray();
        }

        foreach (var association in associations)
        {
            var userChoicePath = association.IsUrl
                ? $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{association.Identifier}\UserChoice"
                : $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{association.Identifier}\UserChoice";
            using var userChoice = Registry.CurrentUser.OpenSubKey(userChoicePath, writable: false);
            if (string.Equals(Convert.ToString(userChoice?.GetValue("ProgId")), association.ProgId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{association.Identifier} 当前正使用该候选作为默认应用。请先在 Windows 设置中改选，再撤销候选。");
            }
        }

        using (var registeredApplications = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: true))
        {
            registeredApplications?.DeleteValue(item.RegisteredAppName, throwOnMissingValue: false);
        }

        var capabilityKeyName = item.RegisteredCapabilityPath[(@"Software\RegFix\".Length)..];
        using (var regFixRoot = Registry.CurrentUser.OpenSubKey(@"Software\RegFix", writable: true))
        {
            regFixRoot?.DeleteSubKeyTree(capabilityKeyName, throwOnMissingSubKey: false);
        }

        using (var classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true))
        {
            foreach (var progId in associations.Select(item => item.ProgId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                classes?.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            }
        }

        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    private static string MakeUniqueRegisteredName(string applicationName, string capabilityPath)
    {
        var baseName = $"RegFix · {applicationName}";
        if (baseName.Length > 180)
        {
            baseName = baseName[..180];
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: false);
        var valueName = baseName;
        var suffix = 1;
        while (key?.GetValue(valueName) is string existingPath)
        {
            if (string.Equals(existingPath, capabilityPath, StringComparison.OrdinalIgnoreCase))
            {
                return valueName;
            }

            valueName = $"{baseName} ({suffix++})";
        }

        return valueName;
    }

    private static string NormalizeDisplayName(string value)
        => new string((value ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];

    private static string HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "";
        }
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

    private static bool IsWindowsBinary(string path)
    {
        var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
