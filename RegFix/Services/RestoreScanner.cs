using System.Diagnostics;
using System.Security.Cryptography;
using RegFix.Models;

namespace RegFix.Services;

public sealed class RestoreScanner
{
    private const int MaximumExecutables = 20_000;
    private static readonly string[] IgnoredFolderNames =
    [
        "$RECYCLE.BIN", "System Volume Information", "Windows", "WindowsApps", "node_modules",
        ".git", ".svn", "Cache", "Caches", "Temp", "Temporary Internet Files"
    ];

    public RestoreScanResult Scan(MigrationPackage package, IEnumerable<string> roots, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var result = new RestoreScanResult();
        var candidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var normalizedRoots = roots.Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directTargets = package.Software.Select(item => item.LaunchTarget)
            .Concat(package.Software.Select(item => item.UninstallTarget))
            .Concat(package.Services.Select(item => item.ImageExecutablePath))
            .Concat(package.Shortcuts.Select(item => item.TargetPath))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Where(path => IsInsideSelectedRoot(path, normalizedRoots))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var executablePaths = new HashSet<string>(directTargets, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var root in normalizedRoots)
        {
            progress?.Report($"正在扫描恢复目录：{root}");
            foreach (var path in EnumerateExecutables(root, result.Notes, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!executablePaths.Add(path))
                {
                    continue;
                }

                count++;
                if (count > MaximumExecutables)
                {
                    result.Notes.Add($"可执行文件扫描达到 {MaximumExecutables:N0} 个上限，后续目录未继续扫描。");
                    break;
                }

                progress?.Report($"已检查 {count:N0} 个可执行文件…");
            }

            if (count > MaximumExecutables)
            {
                break;
            }
        }

        var hashToPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var hashIndex = 0;
        foreach (var path in executablePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashIndex++;
            if (hashIndex == 1 || hashIndex % 100 == 0 || hashIndex == executablePaths.Count)
            {
                progress?.Report($"正在计算文件指纹 {hashIndex:N0}/{executablePaths.Count:N0}…");
            }

            var hash = GetSha256(path);
            if (string.IsNullOrWhiteSpace(hash))
            {
                continue;
            }

            if (!hashToPaths.TryGetValue(hash, out var paths))
            {
                paths = [];
                hashToPaths.Add(hash, paths);
            }

            paths.Add(path);
        }

        foreach (var software in package.Software)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, status) = Resolve(software.LaunchTarget, software.LaunchSha256, hashToPaths);
            result.Software.Add(new RestoreSoftwareItem
            {
                DisplayName = software.DisplayName,
                Publisher = software.Publisher,
                DisplayVersion = software.DisplayVersion,
                OriginalPath = software.LaunchTarget,
                ResolvedPath = path,
                Status = status
            });

            var (uninstallerPath, uninstallStatus) = Resolve(software.UninstallTarget, software.UninstallSha256, hashToPaths);
            var entryStatus = software.IsWindowsInstaller
                ? "MSI 项目需运行原安装包修复，不重建注册状态"
                : software.NoRemove
                    ? "原软件标记为不可卸载，已跳过"
                    : string.IsNullOrWhiteSpace(software.UninstallTarget)
                        ? "未采集到可确认的卸载程序"
                        : uninstallStatus;
            var canRestoreEntry = !software.IsWindowsInstaller
                && !software.NoRemove
                && File.Exists(uninstallerPath)
                && IsExactMatchStatus(uninstallStatus);
            var isRegFixEntry = UninstallRegistrationService.IsOwnedEntry(software, out var registrationKeyName);
            if (isRegFixEntry)
            {
                entryStatus = "已由 RegFix 创建，可撤销";
            }
            var installLocation = !string.IsNullOrWhiteSpace(path)
                ? Path.GetDirectoryName(path) ?? software.InstallLocation
                : Directory.Exists(software.InstallLocation) && IsInsideSelectedRoot(software.InstallLocation, normalizedRoots)
                    ? software.InstallLocation
                    : "";

            result.UninstallEntries.Add(new RestoreUninstallEntryItem
            {
                IsSelected = canRestoreEntry && !isRegFixEntry,
                IsRegFixEntry = isRegFixEntry,
                RegistrationKeyName = registrationKeyName,
                DisplayName = software.DisplayName,
                Publisher = software.Publisher,
                InstallLocation = installLocation,
                UninstallerPath = uninstallerPath,
                UninstallArguments = software.UninstallArguments,
                Status = entryStatus,
                Original = software
            });
        }

        foreach (var service in package.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, matchStatus) = Resolve(service.ImageExecutablePath, service.ImageSha256, hashToPaths);
            var isRegFixOwned = ServiceRegistrationService.IsOwnedService(service);
            var status = !IsSupportedServiceType(service.ServiceType)
                ? "驱动或非 Win32 服务不自动重建"
                : service.StartType is not (2 or 3 or 4)
                    ? "启动类型不受支持，需安装器修复"
                    : !IsBuiltInServiceAccount(service.AccountName, service.Name)
                        ? "原服务账户需密码或本机账户，无法安全迁移"
                        : string.IsNullOrWhiteSpace(service.ImageSha256)
                            ? "缺少程序指纹，不能安全匹配"
                                : IsExactMatchStatus(matchStatus)
                                ? "基本配置可重建（服务保持停止）"
                                : matchStatus;
            if (isRegFixOwned)
            {
                status = "已由 RegFix 创建，可撤销";
            }

            result.Services.Add(new RestoreServiceItem
            {
                IsSelected = false,
                IsRegFixOwned = isRegFixOwned,
                Name = service.Name,
                DisplayName = service.DisplayName,
                ExecutablePath = path,
                Arguments = service.ImageArguments,
                AccountName = service.AccountName,
                StartupType = service.StartType switch
                {
                    2 => "自动",
                    3 => "手动",
                    4 => "已禁用",
                    _ => $"类型 {service.StartType}"
                },
                Status = status,
                Original = service
            });
        }

        foreach (var shortcut in package.Shortcuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, status) = Resolve(shortcut.TargetPath, shortcut.TargetSha256, hashToPaths);
            result.Shortcuts.Add(new RestoreShortcutItem
            {
                IsSelected = IsExactMatchStatus(status),
                Name = shortcut.Name,
                TargetPath = path,
                Status = status,
                Original = shortcut
            });
        }

        foreach (var group in package.FileAssociations
                     .Where(item => !string.IsNullOrWhiteSpace(item.ApplicationName))
                     .GroupBy(item => item.ApplicationName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = group
                .GroupBy(item => $"{item.Identifier}|{item.ProgId}", StringComparer.OrdinalIgnoreCase)
                .Select(item => item.First())
                .ToList();
            var matchingSoftware = FindSoftwareByName(package.Software, group.Key);
            var executablePath = "";
            if (matchingSoftware.Count == 1)
            {
                var matching = matchingSoftware[0];
                executablePath = Resolve(matching.LaunchTarget, matching.LaunchSha256, hashToPaths).Path;
            }

            var supportedCount = records.Count(IsSupportedAssociationIdentifier);
            var status = supportedCount == 0
                ? "没有可重建的文件类型或协议"
                : !string.IsNullOrWhiteSpace(executablePath)
                    ? $"找到候选程序；{supportedCount} 个关联需用户确认"
                    : matchingSoftware.Count > 1
                        ? "有多个同名软件；请手动指定程序路径"
                        : "未找到候选程序；可手动指定程序路径";

            var associationItem = new RestoreAssociationItem
            {
                ApplicationName = group.Key,
                Identifiers = string.Join(", ", records.Select(item => item.Identifier).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)),
                ExecutablePath = executablePath,
                Status = status,
                Records = records
            };
            if (AssociationRegistrationService.TryFindRegisteredName(associationItem, out var registeredName, out var capabilityPath))
            {
                associationItem.RegisteredAppName = registeredName;
                associationItem.RegisteredCapabilityPath = capabilityPath;
                associationItem.Status = $"已登记 RegFix 候选：{registeredName}；默认值未更改，可撤销";
            }

            result.Associations.Add(associationItem);
        }

        return result;
    }

    private static List<SoftwareRecord> FindSoftwareByName(IEnumerable<SoftwareRecord> software, string applicationName)
    {
        var normalizedAppName = NormalizeName(applicationName);
        var records = software.Where(item => NormalizeName(item.DisplayName) == normalizedAppName).ToList();
        if (records.Count > 0)
        {
            return records;
        }

        return software.Where(item =>
        {
            var normalizedName = NormalizeName(item.DisplayName);
            return normalizedName.Length >= 4
                && normalizedAppName.Length >= 4
                && (normalizedName.Contains(normalizedAppName, StringComparison.OrdinalIgnoreCase)
                    || normalizedAppName.Contains(normalizedName, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    private static string NormalizeName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static bool IsSupportedAssociationIdentifier(FileAssociationRecord association)
    {
        var identifier = (association.Identifier ?? "").Trim();
        if (identifier.Length is < 2 or > 64)
        {
            return false;
        }

        if (identifier[0] == '.')
        {
            return identifier.Skip(1).All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
        }

        return identifier.All(character => char.IsLetterOrDigit(character) || character is '+' or '.' or '-');
    }

    public static bool IsExactMatchStatus(string status)
        => status is "指纹匹配" or "原路径和指纹匹配" or "在扫描目录中找到指纹匹配";

    private static (string Path, string Status) Resolve(string originalPath, string originalHash, Dictionary<string, List<string>> hashToPaths)
    {
        if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
        {
            var currentHash = GetSha256(originalPath);
            if (!string.IsNullOrWhiteSpace(originalHash) && string.Equals(currentHash, originalHash, StringComparison.OrdinalIgnoreCase))
            {
                return (Path.GetFullPath(originalPath), "原路径和指纹匹配");
            }

            if (!string.IsNullOrWhiteSpace(originalHash)
                && hashToPaths.TryGetValue(originalHash, out var relocatedMatches))
            {
                var elsewhere = relocatedMatches.Where(path => !string.Equals(path, originalPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (elsewhere.Length == 1)
                {
                    return (elsewhere[0], "在扫描目录中找到指纹匹配");
                }

                if (elsewhere.Length > 1)
                {
                    return ("", $"找到 {elsewhere.Length} 个指纹匹配，需手动指定");
                }
            }

            if (string.IsNullOrWhiteSpace(originalHash))
            {
                return (Path.GetFullPath(originalPath), "原路径存在，缺少指纹；需确认");
            }

            return (Path.GetFullPath(originalPath), "原路径存在但文件已变化；需确认");
        }

        if (string.IsNullOrWhiteSpace(originalHash) || !hashToPaths.TryGetValue(originalHash, out var matches) || matches.Count == 0)
        {
            return ("", "未找到匹配文件");
        }

        if (matches.Count == 1)
        {
            return (matches[0], "指纹匹配");
        }

        return ("", $"找到 {matches.Count} 个相同文件，需手动指定");
    }

    private static bool IsInsideSelectedRoot(string filePath, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedFile = Path.GetFullPath(filePath);
            if (normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSupportedServiceType(int serviceType)
        => serviceType is 0x10 or 0x20;

    public static bool IsBuiltInServiceAccount(string accountName, string serviceName)
    {
        var normalized = (accountName ?? "").Trim().Replace('/', '\\');
        if (normalized.StartsWith(".\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("System", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\LocalService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NetworkService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\NetworkService", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Equals($"NT SERVICE\\{serviceName}", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateExecutables(string root, List<string> notes, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var scanned = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(current, "*.exe", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                notes.Add($"无法扫描目录“{current}”：{exception.Message}");
                continue;
            }

            foreach (var path in files)
            {
                scanned++;
                if (scanned > MaximumExecutables + 100_000)
                {
                    notes.Add($"目录“{root}”包含过多文件，已停止扫描。");
                    yield break;
                }

                yield return Path.GetFullPath(path);
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IgnoredFolderNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(directory);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(directory);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                notes.Add($"无法枚举目录“{current}”的子目录：{exception.Message}");
            }
        }
    }

    private static string GetSha256(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }
}
