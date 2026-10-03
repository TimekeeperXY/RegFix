using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using RegFix.Models;

namespace RegFix.Services;

public sealed class InventoryService
{
    private const int MaximumPortableExecutables = 12_000;
    private static readonly string[] IgnoredFolderNames =
    [
        "$RECYCLE.BIN", "System Volume Information", "Windows", "WindowsApps", "node_modules",
        ".git", ".svn", "Cache", "Caches", "Temp", "Temporary Internet Files"
    ];

    public MigrationPackage Collect(IEnumerable<string> additionalRoots, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var package = new MigrationPackage();
        ReadWindowsVersion(package);

        progress?.Report("正在读取已安装软件清单…");
        package.Software = ReadInstalledSoftware(package.CollectionNotes, cancellationToken);

        var hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var software in package.Software)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(software.LaunchTarget))
            {
                software.LaunchSha256 = GetSha256(software.LaunchTarget, hashCache);
            }

            if (!string.IsNullOrWhiteSpace(software.UninstallTarget))
            {
                software.UninstallSha256 = GetSha256(software.UninstallTarget, hashCache);
            }
        }

        progress?.Report("正在读取开始菜单快捷方式…");
        package.Shortcuts = ReadStartMenuShortcuts(package.CollectionNotes, hashCache, cancellationToken);

        progress?.Report("正在读取第三方服务配置…");
        package.Services = ReadThirdPartyServices(package.CollectionNotes);
        foreach (var service in package.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(service.ImageExecutablePath))
            {
                service.ImageSha256 = GetSha256(service.ImageExecutablePath, hashCache);
            }
        }

        progress?.Report("正在导出默认应用关联信息…");
        package.FileAssociations = ReadDefaultAssociations(package.CollectionNotes, cancellationToken);

        var validRoots = additionalRoots
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var executablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var software in package.Software.Where(item => !string.IsNullOrWhiteSpace(item.LaunchTarget)))
        {
            executablePaths.Add(Path.GetFullPath(software.LaunchTarget));
        }

        foreach (var shortcut in package.Shortcuts.Where(item => !string.IsNullOrWhiteSpace(item.TargetPath)))
        {
            executablePaths.Add(Path.GetFullPath(shortcut.TargetPath));
        }

        var count = 0;
        foreach (var root in validRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在扫描补充目录：{root}");
            foreach (var executable in EnumerateExecutables(root, package.CollectionNotes, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!executablePaths.Add(executable))
                {
                    continue;
                }

                count++;
                if (count > MaximumPortableExecutables)
                {
                    package.CollectionNotes.Add($"补充目录扫描达到 {MaximumPortableExecutables:N0} 个可执行文件上限，已停止继续扫描。");
                    break;
                }

                var info = new FileInfo(executable);
                var version = FileVersionInfo.GetVersionInfo(executable);
                var name = FirstNonEmpty(version.ProductName, version.FileDescription, Path.GetFileNameWithoutExtension(executable));
                package.Software.Add(new SoftwareRecord
                {
                    DisplayName = name,
                    Publisher = version.CompanyName ?? "",
                    DisplayVersion = version.ProductVersion ?? version.FileVersion ?? "",
                    InstallLocation = Path.GetDirectoryName(executable) ?? "",
                    LaunchTarget = executable,
                    LaunchSha256 = GetSha256(executable, hashCache),
                    Source = "AdditionalFolderScan"
                });
            }

            if (count > MaximumPortableExecutables)
            {
                break;
            }
        }

        package.CapturedAtUtc = DateTimeOffset.UtcNow;
        return package;
    }

    private static List<SoftwareRecord> ReadInstalledSoftware(List<string> notes, CancellationToken cancellationToken)
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var entries = new List<SoftwareRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(uninstallPath, writable: false);
                    if (uninstallKey is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                            if (appKey is null || IsHiddenOrNestedProduct(appKey))
                            {
                                continue;
                            }

                            var displayName = ReadString(appKey, "DisplayName");
                            if (string.IsNullOrWhiteSpace(displayName))
                            {
                                continue;
                            }

                            var installLocation = ExpandPath(ReadString(appKey, "InstallLocation"));
                            var displayIcon = ReadString(appKey, "DisplayIcon");
                    var launchTarget = FindLaunchTarget(displayIcon, installLocation, displayName);
                            var uninstallString = ReadString(appKey, "UninstallString");
                            var (uninstallTarget, uninstallArguments) = ParseCommandLine(uninstallString, installLocation);
                            var record = new SoftwareRecord
                            {
                                DisplayName = displayName.Trim(),
                                Publisher = ReadString(appKey, "Publisher"),
                                DisplayVersion = ReadString(appKey, "DisplayVersion"),
                                InstallLocation = installLocation,
                                UninstallString = uninstallString,
                                UninstallKeyName = subKeyName,
                                IsWindowsInstaller = ReadInt(appKey, "WindowsInstaller", 0) == 1 || uninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase),
                                NoRemove = ReadInt(appKey, "NoRemove", 0) == 1,
                                UninstallTarget = uninstallTarget,
                                UninstallArguments = uninstallArguments,
                                QuietUninstallString = ReadString(appKey, "QuietUninstallString"),
                                DisplayIcon = displayIcon,
                                HelpLink = ReadString(appKey, "HelpLink"),
                                UrlInfoAbout = ReadString(appKey, "URLInfoAbout"),
                                Comments = ReadString(appKey, "Comments"),
                                LaunchTarget = launchTarget,
                                WorkingDirectory = string.IsNullOrWhiteSpace(launchTarget) ? "" : Path.GetDirectoryName(launchTarget) ?? "",
                                Source = $"{hive}/{view}"
                            };
                            var identity = string.Join("|", record.DisplayName, record.Publisher, record.DisplayVersion, record.InstallLocation);
                            if (seen.Add(identity))
                            {
                                entries.Add(record);
                            }
                        }
                        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
                        {
                            notes.Add($"读取软件项“{subKeyName}”时跳过：{exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    notes.Add($"无法读取 {hive}/{view} 的软件清单：{exception.Message}");
                }
            }
        }

        return entries.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsHiddenOrNestedProduct(RegistryKey appKey)
    {
        if (!string.IsNullOrWhiteSpace(ReadString(appKey, "ReleaseType")))
        {
            return true;
        }

        try
        {
            var systemComponent = appKey.GetValue("SystemComponent");
            if (systemComponent is not null && Convert.ToInt32(systemComponent) == 1)
            {
                return true;
            }
        }
        catch
        {
            // Ignore malformed optional metadata and continue with visible values.
        }

        return !string.IsNullOrWhiteSpace(ReadString(appKey, "ParentKeyName"));
    }

    private static List<ShortcutRecord> ReadStartMenuShortcuts(List<string> notes, Dictionary<string, string> hashCache, CancellationToken cancellationToken)
    {
        var shortcuts = new List<ShortcutRecord>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        }
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var path in EnumerateFiles(root, "*.lnk", notes, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = ShellLinkService.Read(path, root);
                if (record is null)
                {
                    continue;
                }

                if (File.Exists(record.TargetPath))
                {
                    record.TargetSha256 = GetSha256(record.TargetPath, hashCache);
                }

                shortcuts.Add(record);
            }
        }

        return shortcuts
            .GroupBy(item => $"{item.OriginalProgramsRelativePath}|{item.TargetPath}|{item.Arguments}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<ServiceRecord> ReadThirdPartyServices(List<string> notes)
    {
        var services = new List<ServiceRecord>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false);
            if (root is null)
            {
                notes.Add("无法读取 Windows 服务配置。");
                return services;
            }

            foreach (var serviceName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(serviceName, writable: false);
                    if (key is null)
                    {
                        continue;
                    }

                    var imagePath = ReadString(key, "ImagePath");
                    var (executable, arguments) = ParseCommandLine(imagePath, "");
                    if (string.IsNullOrWhiteSpace(executable)
                        || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)
                        || IsWindowsPath(executable)
                        || !Path.IsPathRooted(executable))
                    {
                        continue;
                    }

                    services.Add(new ServiceRecord
                    {
                        Name = serviceName,
                        DisplayName = ReadString(key, "DisplayName"),
                        ImagePath = imagePath,
                        ImageExecutablePath = executable,
                        ImageArguments = arguments,
                        StartType = ReadInt(key, "Start", -1),
                        ServiceType = ReadInt(key, "Type", -1),
                        AccountName = ReadString(key, "ObjectName"),
                        Description = ReadString(key, "Description"),
                        Dependencies = ReadStringArray(key, "DependOnService")
                    });
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
                {
                    notes.Add($"读取服务“{serviceName}”时跳过：{exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            notes.Add($"无法完整读取服务列表：{exception.Message}");
        }

        return services.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<FileAssociationRecord> ReadDefaultAssociations(List<string> notes, CancellationToken cancellationToken)
    {
        var result = new List<FileAssociationRecord>();
        var tempFile = Path.Combine(Path.GetTempPath(), $"regfix-associations-{Guid.NewGuid():N}.xml");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "dism.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("/Online");
            process.StartInfo.ArgumentList.Add($"/Export-DefaultAppAssociations:{tempFile}");
            if (!process.Start())
            {
                notes.Add("DISM 未能启动，未采集默认应用关联信息。");
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            while (!process.WaitForExit(250))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed > TimeSpan.FromMinutes(1))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }

                        process.WaitForExit();
                    }
                    catch
                    {
                        // The process may have exited while the timeout was being handled.
                    }

                    notes.Add("默认应用关联导出超时，已停止等待；其他采集内容不受影响。");
                    return result;
                }
            }

            if (process.ExitCode != 0 || !File.Exists(tempFile))
            {
                notes.Add($"默认应用关联导出未成功（DISM 代码 {process.ExitCode}）；其他采集内容不受影响。");
                return result;
            }

            var document = XDocument.Load(tempFile);
            result = document.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Association", StringComparison.OrdinalIgnoreCase))
                .Select(element => new FileAssociationRecord
                {
                    Identifier = (string?)element.Attribute("Identifier") ?? "",
                    ProgId = (string?)element.Attribute("ProgId") ?? "",
                    ApplicationName = (string?)element.Attribute("ApplicationName") ?? ""
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Identifier))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            notes.Add($"默认应用关联信息未能导出：{exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // A temporary export is nonessential; leave a cleanup error out of the user report.
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateExecutables(string root, List<string> notes, CancellationToken cancellationToken)
    {
        var paths = EnumerateFiles(root, "*.exe", notes, cancellationToken).ToArray();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("setup", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("update", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isLargeEnough = false;
            try
            {
                isLargeEnough = new FileInfo(path).Length >= 32 * 1024;
            }
            catch (IOException)
            {
                // Skip files that disappeared during the scan.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip files that cannot be inspected.
            }

            if (isLargeEnough)
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern, List<string> notes, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var found = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                notes.Add($"无法扫描目录“{current}”：{exception.Message}");
                continue;
            }

            foreach (var file in files)
            {
                found++;
                if (found > MaximumPortableExecutables + 100_000)
                {
                    notes.Add($"目录“{root}”的文件数量超过扫描上限，已停止继续扫描。");
                    yield break;
                }

                yield return Path.GetFullPath(file);
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(directory);
                    if (IgnoredFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
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

    private static void ReadWindowsVersion(MigrationPackage package)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            if (key is null)
            {
                return;
            }

            package.SourceWindowsName = ReadString(key, "ProductName");
            var displayVersion = ReadString(key, "DisplayVersion");
            var build = ReadString(key, "CurrentBuildNumber");
            var ubr = ReadString(key, "UBR");
            package.SourceWindowsVersion = string.Join(".", new[] { displayVersion, build, ubr }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
            package.CollectionNotes.Add("无法读取 Windows 版本信息。");
        }
    }

    private static string FindLaunchTarget(string displayIcon, string installLocation, string displayName)
    {
        var iconPath = ExtractExecutablePath(displayIcon);
        if (!string.IsNullOrWhiteSpace(iconPath)
            && string.Equals(Path.GetExtension(iconPath), ".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(iconPath))
        {
            return iconPath;
        }

        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return "";
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(path).StartsWith("uninstall", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(path).StartsWith("setup", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            return "";
        }

        var normalizedName = NormalizeName(displayName);
        var exactMatches = candidates.Where(path =>
        {
            var fileName = NormalizeName(Path.GetFileNameWithoutExtension(path));
            if (fileName.Length < 3 || normalizedName.Length < 3)
            {
                return false;
            }

            return string.Equals(fileName, normalizedName, StringComparison.OrdinalIgnoreCase)
                || normalizedName.Contains(fileName, StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(normalizedName, StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        return exactMatches.Length == 1 ? exactMatches[0] : "";
    }

    private static string ExtractExecutablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            candidate = closingQuote > 1 ? expanded[1..closingQuote] : expanded.Trim('"');
        }
        else
        {
            var comma = expanded.LastIndexOf(',');
            var withIconIndexRemoved = comma > 2 ? expanded[..comma].Trim() : expanded.Trim();
            if (File.Exists(withIconIndexRemoved))
            {
                candidate = withIconIndexRemoved;
            }
            else
            {
                var tokens = expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                candidate = "";
                for (var tokenCount = 1; tokenCount <= tokens.Length; tokenCount++)
                {
                    var prefix = string.Join(' ', tokens.Take(tokenCount)).Trim();
                    if (File.Exists(prefix) && string.Equals(Path.GetExtension(prefix), ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = prefix;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(candidate) && tokens.Length == 1 && Path.HasExtension(tokens[0]))
                {
                    candidate = tokens[0];
                }
            }
        }

        candidate = candidate.Replace("\\??\\", "", StringComparison.OrdinalIgnoreCase);
        return Path.IsPathRooted(candidate) ? candidate : "";
    }

    private static (string Executable, string Arguments) ParseCommandLine(string commandLine, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return ("", "");
        }

        var command = Environment.ExpandEnvironmentVariables(commandLine.Trim()).Replace("\\??\\", "", StringComparison.OrdinalIgnoreCase);
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return ("", "");
            }

            var candidate = command[1..closingQuote];
            return File.Exists(candidate)
                ? (Path.GetFullPath(candidate), command[(closingQuote + 1)..].Trim())
                : ("", "");
        }

        var splitPositions = Enumerable.Range(0, command.Length).Where(index => char.IsWhiteSpace(command[index])).ToArray();
        foreach (var splitPosition in splitPositions)
        {
            var candidate = command[..splitPosition].Trim();
            if (File.Exists(candidate))
            {
                return (Path.GetFullPath(candidate), command[splitPosition..].Trim());
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory) && !Path.IsPathRooted(candidate))
            {
                var relativeCandidate = Path.Combine(workingDirectory, candidate);
                if (File.Exists(relativeCandidate))
                {
                    return (Path.GetFullPath(relativeCandidate), command[splitPosition..].Trim());
                }
            }
        }

        if (File.Exists(command))
        {
            return (Path.GetFullPath(command), "");
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Path.IsPathRooted(command))
        {
            var relativeCandidate = Path.Combine(workingDirectory, command);
            if (File.Exists(relativeCandidate))
            {
                return (Path.GetFullPath(relativeCandidate), "");
            }
        }

        return ("", "");
    }

    private static bool IsWindowsPath(string path)
    {
        if (path.StartsWith(@"\SystemRoot", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSha256(string path, Dictionary<string, string> cache)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "";
        }

        var fullPath = Path.GetFullPath(path);
        if (cache.TryGetValue(fullPath, out var cached))
        {
            return cached;
        }

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            cache[fullPath] = hash;
            return hash;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }

    private static string ReadString(RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value switch
            {
                string text => text,
                string[] array => string.Join(";", array),
                _ => Convert.ToString(value) ?? ""
            };
        }
        catch
        {
            return "";
        }
    }

    private static List<string> ReadStringArray(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                string[] values => values.ToList(),
                string text when !string.IsNullOrWhiteSpace(text) => [text],
                _ => []
            };
        }
        catch
        {
            return [];
        }
    }

    private static int ReadInt(RegistryKey key, string name, int defaultValue)
    {
        try
        {
            return Convert.ToInt32(key.GetValue(name, defaultValue));
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string ExpandPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : expanded;
    }

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
