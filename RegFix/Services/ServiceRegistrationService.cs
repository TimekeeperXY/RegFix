using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using RegFix.Models;

namespace RegFix.Services;

public static class ServiceRegistrationService
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceDelete = 0x00010000;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceStopped = 1;
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";

    public static string Restore(RestoreServiceItem item, IEnumerable<string> selectedRoots)
    {
        var record = item.Original;
        if (!RestoreScanner.IsSupportedServiceType(record.ServiceType))
        {
            throw new InvalidOperationException("只支持 Win32 自有进程或共享进程服务；驱动和其他类型必须由安装器恢复。");
        }

        if (record.StartType is not (2 or 3 or 4))
        {
            throw new InvalidOperationException("只支持自动、手动或禁用的服务启动类型。");
        }

        if (!RestoreScanner.IsBuiltInServiceAccount(record.AccountName, record.Name))
        {
            throw new InvalidOperationException("原服务使用自定义账户，无法在没有密码的情况下安全恢复。");
        }

        var executable = Path.GetFullPath(item.ExecutablePath);
        if (!File.Exists(executable) || !IsInsideSelectedRoot(executable, selectedRoots))
        {
            throw new FileNotFoundException("服务程序必须存在于你明确选择的扫描目录中。", executable);
        }

        if (string.IsNullOrWhiteSpace(record.ImageSha256) || !string.Equals(HashFile(executable), record.ImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("服务程序指纹与重装前不一致；请运行原安装器修复服务。");
        }

        var imageArguments = record.ImageArguments ?? "";
        if (imageArguments.Length > 2048 || imageArguments.Any(char.IsControl))
        {
            throw new InvalidOperationException("服务启动参数无效或过长。");
        }

        if (string.IsNullOrWhiteSpace(record.Name) || record.Name.Contains('\0'))
        {
            throw new InvalidOperationException("服务名称无效。");
        }

        var dependencies = record.Dependencies ?? [];
        if (dependencies.Any(string.IsNullOrWhiteSpace) || dependencies.Any(value => value.Contains('\0')))
        {
            throw new InvalidOperationException("服务依赖项清单无效。");
        }

        var commandLine = Quote(executable);
        if (!string.IsNullOrWhiteSpace(imageArguments))
        {
            commandLine += " " + imageArguments;
        }

        var dependencyPointer = IntPtr.Zero;
        try
        {
            if (dependencies.Count > 0)
            {
                dependencyPointer = Marshal.StringToCoTaskMemUni(string.Join('\0', dependencies) + "\0\0");
            }

            using var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
            if (manager.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接 Windows 服务管理器；请以管理员身份运行 RegFix。");
            }

            var accountName = NormalizeAccountName(record.AccountName);
            using var service = CreateService(
                manager,
                record.Name,
                string.IsNullOrWhiteSpace(record.DisplayName) ? record.Name : record.DisplayName,
                ServiceQueryStatus | ServiceChangeConfig | ServiceDelete,
                (uint)record.ServiceType,
                (uint)record.StartType,
                ServiceErrorNormal,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                dependencyPointer,
                accountName,
                null);

            if (service.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1073)
                {
                    throw new InvalidOperationException("同名服务已存在；RegFix 不会覆盖现有服务。");
                }

                throw new Win32Exception(error, "Windows 未能创建该服务。");
            }

            try
            {
                using var serviceKey = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{record.Name}", writable: true)
                    ?? throw new InvalidOperationException("服务已创建，但无法写入 RegFix 所有权标记。请检查管理员权限；服务将尝试回滚。");
                serviceKey.SetValue("RegFixOwned", 1, RegistryValueKind.DWord);
                serviceKey.SetValue("RegFixTargetSha256", record.ImageSha256, RegistryValueKind.String);
            }
            catch
            {
                _ = DeleteService(service);
                throw;
            }

            return record.StartType == 2
                ? "服务已创建并设为自动启动；当前保持停止状态。"
                : record.StartType == 3
                    ? "服务已创建并设为手动启动；当前保持停止状态。"
                    : "服务已创建并保持禁用状态。";
        }
        finally
        {
            if (dependencyPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(dependencyPointer);
            }
        }
    }

    public static bool IsOwnedService(ServiceRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.ImageSha256))
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{record.Name}", writable: false);
            return key is not null
                && Convert.ToInt32(key.GetValue("RegFixOwned", 0)) == 1
                && string.Equals(Convert.ToString(key.GetValue("RegFixTargetSha256")), record.ImageSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void RemoveOwnedService(RestoreServiceItem item)
    {
        var record = item.Original;
        if (!item.IsRegFixOwned || !IsOwnedService(record))
        {
            throw new InvalidOperationException("只允许撤销由 RegFix 创建且所有权标记匹配的服务。");
        }

        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接服务管理器；请以管理员身份运行 RegFix。");
        }

        using var service = OpenService(manager, record.Name, ServiceQueryStatus | ServiceDelete);
        if (service.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开该服务；请确认它仍存在并以管理员身份运行 RegFix。");
        }

        if (!QueryServiceStatus(service, out var status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取服务状态。");
        }

        if (status.CurrentState != ServiceStopped)
        {
            throw new InvalidOperationException("该服务当前正在运行。请先在 services.msc 中停止它，再撤销 RegFix 服务配置。");
        }

        if (!DeleteService(service))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能删除该服务配置。");
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
            var normalizedFile = Path.GetFullPath(filePath);
            if (normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? NormalizeAccountName(string accountName)
    {
        var value = (accountName ?? "").Trim().Replace('/', '\\');
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("System", StringComparison.OrdinalIgnoreCase)
            || value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.StartsWith(".\\", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return value;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateServiceW", SetLastError = true)]
    private static extern ServiceHandle CreateService(
        ServiceHandle manager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        IntPtr loadOrderGroup,
        IntPtr tagId,
        IntPtr dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW", SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(ServiceHandle serviceHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(ServiceHandle serviceHandle, out ServiceStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
}
