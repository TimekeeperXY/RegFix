using System.Text.Json.Serialization;

namespace RegFix.Models;

public sealed class MigrationPackage
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = "0.1.0";
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string SourceWindowsName { get; set; } = "";
    public string SourceWindowsVersion { get; set; } = "";
    public List<SoftwareRecord> Software { get; set; } = [];
    public List<ShortcutRecord> Shortcuts { get; set; } = [];
    public List<ServiceRecord> Services { get; set; } = [];
    public List<FileAssociationRecord> FileAssociations { get; set; } = [];
    public List<string> CollectionNotes { get; set; } = [];
}

public sealed class SoftwareRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string UninstallKeyName { get; set; } = "";
    public bool IsWindowsInstaller { get; set; }
    public bool NoRemove { get; set; }
    public string UninstallTarget { get; set; } = "";
    public string UninstallArguments { get; set; } = "";
    public string UninstallSha256 { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string DisplayIcon { get; set; } = "";
    public string HelpLink { get; set; } = "";
    public string UrlInfoAbout { get; set; } = "";
    public string Comments { get; set; } = "";
    public string LaunchTarget { get; set; } = "";
    public string LaunchArguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string LaunchSha256 { get; set; } = "";
    public string Source { get; set; } = "UninstallRegistry";
}

public sealed class ShortcutRecord
{
    public string Name { get; set; } = "";
    public string OriginalProgramsRelativePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string IconLocation { get; set; } = "";
    public string TargetSha256 { get; set; } = "";
    public string Source { get; set; } = "UserStartMenu";
}

public sealed class ServiceRecord
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string ImageExecutablePath { get; set; } = "";
    public string ImageArguments { get; set; } = "";
    public string ImageSha256 { get; set; } = "";
    public int StartType { get; set; }
    public int ServiceType { get; set; }
    public string AccountName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Dependencies { get; set; } = [];
}

public sealed class FileAssociationRecord
{
    public string Identifier { get; set; } = "";
    public string ProgId { get; set; } = "";
    public string ApplicationName { get; set; } = "";
}

public sealed class RestoreShortcutItem
{
    public bool IsSelected { get; set; }
    public string Name { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Status { get; set; } = "";
    public ShortcutRecord Original { get; set; } = new();
}

public sealed class RestoreSoftwareItem
{
    public string DisplayName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string ResolvedPath { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class RestoreScanResult
{
    public List<RestoreSoftwareItem> Software { get; set; } = [];
    public List<RestoreUninstallEntryItem> UninstallEntries { get; set; } = [];
    public List<RestoreServiceItem> Services { get; set; } = [];
    public List<RestoreAssociationItem> Associations { get; set; } = [];
    public List<RestoreShortcutItem> Shortcuts { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public sealed class RestoreAssociationItem
{
    public bool IsSelected { get; set; }
    public string ApplicationName { get; set; } = "";
    public string Identifiers { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Status { get; set; } = "";
    public string RegisteredAppName { get; set; } = "";
    public string RegisteredCapabilityPath { get; set; } = "";
    public List<FileAssociationRecord> Records { get; set; } = [];
}

public sealed class RestoreServiceItem
{
    public bool IsSelected { get; set; }
    public bool IsRegFixOwned { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string StartupType { get; set; } = "";
    public string Status { get; set; } = "";
    public ServiceRecord Original { get; set; } = new();
}

public sealed class RestoreUninstallEntryItem
{
    public bool IsSelected { get; set; }
    public bool IsRegFixEntry { get; set; }
    public string RegistrationKeyName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallerPath { get; set; } = "";
    public string UninstallArguments { get; set; } = "";
    public string Status { get; set; } = "";
    public SoftwareRecord Original { get; set; } = new();
}
