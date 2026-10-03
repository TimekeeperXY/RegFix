using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Security.Principal;
using RegFix.Models;
using RegFix.Services;

namespace RegFix;

public partial class MainWindow : Window
{
    private readonly InventoryService _inventoryService = new();
    private readonly RestoreScanner _restoreScanner = new();
    private MigrationPackage? _capturePackage;
    private MigrationPackage? _restorePackage;
    private string? _capturePackagePath;

    public ObservableCollection<string> CaptureRoots { get; } = [];
    public ObservableCollection<string> RestoreRoots { get; } = [];
    public ObservableCollection<SoftwareRecord> CapturedSoftware { get; } = [];
    public ObservableCollection<RestoreSoftwareItem> RestoredSoftware { get; } = [];
    public ObservableCollection<RestoreUninstallEntryItem> RestoredUninstallEntries { get; } = [];
    public ObservableCollection<RestoreServiceItem> RestoredServices { get; } = [];
    public ObservableCollection<RestoreAssociationItem> RestoredAssociations { get; } = [];
    public ObservableCollection<RestoreShortcutItem> RestoredShortcuts { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private async void CaptureScan_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyOperationAsync(async () =>
        {
            var progress = CreateProgress();
            var package = await Task.Run(() => _inventoryService.Collect(CaptureRoots.ToArray(), progress, CancellationToken.None));
            _capturePackage = package;
            _capturePackagePath = null;
            CapturedSoftware.Clear();
            foreach (var item in package.Software)
            {
                CapturedSoftware.Add(item);
            }

            SavePackageButton.IsEnabled = true;
            CaptureSummaryText.Text = BuildCaptureSummary(package);
            SetStatus($"扫描完成：{package.Software.Count:N0} 个软件记录、{package.Shortcuts.Count:N0} 个快捷方式。");
        });
    }

    private async void SavePackage_Click(object sender, RoutedEventArgs e)
    {
        if (_capturePackage is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 RegFix 迁移包",
            Filter = "RegFix 迁移包 (*.regfix.json)|*.regfix.json|JSON 文件 (*.json)|*.json",
            DefaultExt = ".regfix.json",
            AddExtension = true,
            FileName = $"RegFix-迁移包-{DateTime.Now:yyyyMMdd}.regfix.json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyOperationAsync(async () =>
        {
            await MigrationPackageStore.SaveAsync(_capturePackage, dialog.FileName);
            _capturePackagePath = dialog.FileName;
            SetStatus($"迁移包已保存：{dialog.FileName}");
            MessageBox.Show(this,
                "迁移包已保存。请确认它位于不会被重装的分区或移动硬盘上。文件是可读 JSON，包含软件路径和服务账户名称，不包含密码。",
                "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 RegFix 迁移包",
            Filter = "RegFix 迁移包 (*.regfix.json;*.json)|*.regfix.json;*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyOperationAsync(async () =>
        {
            var package = await MigrationPackageStore.LoadAsync(dialog.FileName);
            _restorePackage = package;
            RestoredSoftware.Clear();
            RestoredUninstallEntries.Clear();
            RestoredServices.Clear();
            RestoredAssociations.Clear();
            RestoredShortcuts.Clear();
            ScanRestoreButton.IsEnabled = true;
            RestoreUninstallEntriesButton.IsEnabled = false;
            DeleteUninstallEntriesButton.IsEnabled = false;
            RestoreServicesButton.IsEnabled = false;
            DeleteServicesButton.IsEnabled = false;
            RegisterAssociationButton.IsEnabled = false;
            DeleteAssociationButton.IsEnabled = false;
            RestoreShortcutsButton.IsEnabled = false;
            var captured = package.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            LoadedPackageText.Text = $"{Path.GetFileName(dialog.FileName)} · 采集于 {captured} · {package.SourceWindowsName} {package.SourceWindowsVersion}";
            RestoreSummaryText.Text = BuildPackageSummary(package);
            SetStatus("迁移包已读取。请选择软件目录，然后扫描匹配。");
        });
    }

    private async void ScanRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_restorePackage is null)
        {
            return;
        }

        if (RestoreRoots.Count == 0)
        {
            MessageBox.Show(this, "请先添加软件所在目录，例如 D:\\Program Files 或软件的根目录。", "需要扫描目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyOperationAsync(async () =>
        {
            var progress = CreateProgress();
            var result = await Task.Run(() => _restoreScanner.Scan(_restorePackage, RestoreRoots.ToArray(), progress, CancellationToken.None));
            RestoredSoftware.Clear();
            foreach (var item in result.Software)
            {
                RestoredSoftware.Add(item);
            }

            RestoredUninstallEntries.Clear();
            foreach (var item in result.UninstallEntries)
            {
                RestoredUninstallEntries.Add(item);
            }

            RestoredServices.Clear();
            foreach (var item in result.Services)
            {
                RestoredServices.Add(item);
            }

            RestoredAssociations.Clear();
            foreach (var item in result.Associations)
            {
                RestoredAssociations.Add(item);
            }

            RestoredShortcuts.Clear();
            foreach (var item in result.Shortcuts)
            {
                RestoredShortcuts.Add(item);
            }

            RestoreShortcutsButton.IsEnabled = result.Shortcuts.Any(item => item.IsSelected);
            RestoreUninstallEntriesButton.IsEnabled = result.UninstallEntries.Any(item => item.IsSelected);
            DeleteUninstallEntriesButton.IsEnabled = result.UninstallEntries.Any(item => item.IsRegFixEntry);
            RestoreServicesButton.IsEnabled = result.Services.Count > 0;
            DeleteServicesButton.IsEnabled = result.Services.Any(item => item.IsRegFixOwned);
            RegisterAssociationButton.IsEnabled = result.Associations.Count > 0;
            DeleteAssociationButton.IsEnabled = result.Associations.Any(item => !string.IsNullOrWhiteSpace(item.RegisteredAppName));
            var exact = result.Shortcuts.Count(item => RestoreScanner.IsExactMatchStatus(item.Status));
            var softwareFound = result.Software.Count(item => RestoreScanner.IsExactMatchStatus(item.Status));
            var notes = result.Notes.Count == 0 ? "" : $" 扫描提示 {result.Notes.Count} 条。";
            var safeUninstall = result.UninstallEntries.Count(item => item.IsSelected);
            var serviceMatches = result.Services.Count(item => item.Status == "基本配置可重建（服务保持停止）");
            var associationGroups = result.Associations.Count(item => item.Records.Any(RestoreScanner.IsSupportedAssociationIdentifier));
            RestoreSummaryText.Text = $"软件记录 {result.Software.Count:N0} 项，指纹匹配 {softwareFound:N0} 项；卸载列表候选 {safeUninstall:N0} 项；服务配置候选 {serviceMatches:N0} 项；文件关联候选 {associationGroups:N0} 组；快捷方式 {result.Shortcuts.Count:N0} 项，可直接恢复 {exact:N0} 项。匹配仅依据文件 SHA-256，不会启动程序。{notes}";
            SetStatus("扫描匹配完成。请检查路径与匹配状态后再恢复快捷方式。");
            if (result.Notes.Count > 0)
            {
                var detail = string.Join(Environment.NewLine, result.Notes.Take(8));
                if (result.Notes.Count > 8)
                {
                    detail += Environment.NewLine + $"…另有 {result.Notes.Count - 8} 条提示。";
                }

                MessageBox.Show(this, detail, "扫描提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
    }

    private void RestoreShortcuts_Click(object sender, RoutedEventArgs e)
    {
        RestoreShortcutGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RestoreShortcutGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var selected = RestoredShortcuts.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请勾选至少一条快捷方式。", "没有选中项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var valid = selected.Where(item => !string.IsNullOrWhiteSpace(item.TargetPath) && File.Exists(item.TargetPath)).ToArray();
        var invalidCount = selected.Length - valid.Length;
        if (valid.Length == 0)
        {
            MessageBox.Show(this, "选中项目的目标文件不存在。请编辑路径，或重新添加正确的软件目录并扫描。", "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prompt = $"将在当前用户的开始菜单“RegFix 恢复”目录创建 {valid.Length} 条快捷方式。";
        if (invalidCount > 0)
        {
            prompt += $"另有 {invalidCount} 条路径无效的项目会跳过。";
        }

        prompt += Environment.NewLine + "确认继续？";
        if (MessageBox.Show(this, prompt, "确认恢复快捷方式", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();
        foreach (var item in valid)
        {
            try
            {
                var destination = ShellLinkService.CreateRestored(item.Original, Path.GetFullPath(item.TargetPath));
                item.IsSelected = false;
                item.Status = $"已创建：{destination}";
                succeeded++;
            }
            catch (Exception exception)
            {
                item.Status = $"创建失败：{exception.Message}";
                failures.Add($"{item.Name}: {exception.Message}");
            }
        }

        RestoreShortcutGrid.Items.Refresh();
        RestoreShortcutsButton.IsEnabled = RestoredShortcuts.Any(item => item.IsSelected);
        SetStatus($"快捷方式恢复完成：成功 {succeeded} 项，失败 {failures.Count} 项。");
        var message = $"已在当前用户开始菜单创建 {succeeded} 条快捷方式。";
        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(6));
        }

        MessageBox.Show(this, message, "恢复结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void RestoreUninstallEntries_Click(object sender, RoutedEventArgs e)
    {
        RestoreUninstallGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RestoreUninstallGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var selected = RestoredUninstallEntries.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请先勾选匹配可靠的卸载列表项。", "没有选中项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var valid = selected.Where(item => !string.IsNullOrWhiteSpace(item.UninstallerPath) && File.Exists(item.UninstallerPath)).ToArray();
        if (valid.Length == 0)
        {
            MessageBox.Show(this, "所选卸载程序路径不存在。请确认你指定的目录或修正路径。", "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prompt = $"将为 {valid.Length} 个软件写入当前用户的“程序和功能”卸载列表。" + Environment.NewLine
            + "此操作不会创建 MSI 修复记录或启动卸载程序；原卸载器若依赖丢失的注册信息，仍可能无法卸载。继续吗？";
        if (MessageBox.Show(this, prompt, "确认添加卸载列表项", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();
        foreach (var item in valid)
        {
            try
            {
                var keyName = UninstallRegistrationService.RestoreUserEntry(item, RestoreRoots.ToArray());
                item.IsSelected = false;
                item.IsRegFixEntry = true;
                item.RegistrationKeyName = keyName;
                item.Status = $"已写入当前用户列表（{keyName}）";
                succeeded++;
            }
            catch (Exception exception)
            {
                item.Status = $"未添加：{exception.Message}";
                failures.Add($"{item.DisplayName}: {exception.Message}");
            }
        }

        RestoreUninstallGrid.Items.Refresh();
        RestoreUninstallEntriesButton.IsEnabled = RestoredUninstallEntries.Any(item => item.IsSelected);
        DeleteUninstallEntriesButton.IsEnabled = RestoredUninstallEntries.Any(item => item.IsRegFixEntry);
        SetStatus($"卸载列表恢复完成：成功 {succeeded} 项，跳过或失败 {failures.Count} 项。");
        var message = $"已添加 {succeeded} 项到当前用户的卸载列表。";
        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(6));
        }

        MessageBox.Show(this, message, "恢复结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void DeleteUninstallEntries_Click(object sender, RoutedEventArgs e)
    {
        RestoreUninstallGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RestoreUninstallGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = RestoredUninstallEntries.Where(item => item.IsSelected && item.IsRegFixEntry).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请勾选由 RegFix 创建的卸载列表项。", "没有可撤销项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"将删除 {selected.Length} 个 RegFix 在当前用户卸载列表中创建的条目，不会卸载软件。继续吗？", "确认撤销", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = 0;
        var failures = new List<string>();
        foreach (var item in selected)
        {
            try
            {
                UninstallRegistrationService.RemoveOwnedEntry(item);
                item.IsSelected = false;
                item.IsRegFixEntry = false;
                item.Status = "已撤销 RegFix 卸载列表项";
                removed++;
            }
            catch (Exception exception)
            {
                item.Status = $"未删除：{exception.Message}";
                failures.Add($"{item.DisplayName}: {exception.Message}");
            }
        }

        RestoreUninstallGrid.Items.Refresh();
        DeleteUninstallEntriesButton.IsEnabled = RestoredUninstallEntries.Any(item => item.IsRegFixEntry);
        SetStatus($"已撤销 {removed} 个卸载列表项。");
        var message = $"已撤销 {removed} 个 RegFix 卸载列表项；软件文件未改动。";
        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(6));
        }

        MessageBox.Show(this, message, "撤销结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void RestoreServices_Click(object sender, RoutedEventArgs e)
    {
        RestoreServicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RestoreServicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = RestoredServices.Where(item => item.IsSelected && !item.IsRegFixOwned).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请勾选尚未由 RegFix 创建的服务。已有 RegFix 服务请使用撤销按钮管理。", "没有可恢复服务", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!IsAdministrator())
        {
            var answer = MessageBox.Show(this,
                "恢复 Windows 服务需要管理员权限。现在可以重新以管理员身份打开 RegFix；打开后请重新载入迁移包并扫描目录。",
                "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                RestartAsAdministrator();
            }

            return;
        }

        var autoCount = selected.Count(item => item.Original.StartType == 2);
        var prompt = $"将向 Windows 服务管理器添加 {selected.Length} 个服务配置。工具不会立即启动它们；其中 {autoCount} 个会按原设置在系统启动时自动启动。" + Environment.NewLine
            + "服务专有注册项或依赖若未采集，服务仍可能无法运行。只选择你信任的软件。继续吗？";
        if (MessageBox.Show(this, prompt, "确认恢复服务配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();
        foreach (var item in selected)
        {
            try
            {
                item.Status = ServiceRegistrationService.Restore(item, RestoreRoots.ToArray());
                item.IsSelected = false;
                item.IsRegFixOwned = true;
                succeeded++;
            }
            catch (Exception exception)
            {
                item.Status = $"未创建：{exception.Message}";
                failures.Add($"{item.DisplayName}: {exception.Message}");
            }
        }

        RestoreServicesGrid.Items.Refresh();
        RestoreServicesButton.IsEnabled = RestoredServices.Any(item => item.IsSelected);
        DeleteServicesButton.IsEnabled = RestoredServices.Any(item => item.IsRegFixOwned);
        SetStatus($"服务配置恢复完成：创建 {succeeded} 项，跳过或失败 {failures.Count} 项。");
        var message = $"已创建 {succeeded} 个服务配置；它们当前均保持停止。";
        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(6));
        }

        MessageBox.Show(this, message, "恢复结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void DeleteServices_Click(object sender, RoutedEventArgs e)
    {
        RestoreServicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RestoreServicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = RestoredServices.Where(item => item.IsSelected && item.IsRegFixOwned).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请勾选已由 RegFix 创建的服务。", "没有可撤销服务", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!IsAdministrator())
        {
            var answer = MessageBox.Show(this,
                "删除 Windows 服务需要管理员权限。现在可以重新以管理员身份打开 RegFix；打开后请重新载入迁移包并扫描目录。",
                "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                RestartAsAdministrator();
            }

            return;
        }

        if (MessageBox.Show(this, $"将删除 {selected.Length} 个由 RegFix 创建且当前已停止的服务配置。运行中的服务会保留；软件文件不会改动。继续吗？", "确认撤销服务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = 0;
        var failures = new List<string>();
        foreach (var item in selected)
        {
            try
            {
                ServiceRegistrationService.RemoveOwnedService(item);
                item.IsSelected = false;
                item.IsRegFixOwned = false;
                item.Status = "已撤销 RegFix 服务配置";
                removed++;
            }
            catch (Exception exception)
            {
                item.Status = $"未撤销：{exception.Message}";
                failures.Add($"{item.DisplayName}: {exception.Message}");
            }
        }

        RestoreServicesGrid.Items.Refresh();
        RestoreServicesButton.IsEnabled = RestoredServices.Any(item => item.IsSelected && !item.IsRegFixOwned);
        DeleteServicesButton.IsEnabled = RestoredServices.Any(item => item.IsRegFixOwned);
        SetStatus($"已撤销 {removed} 个 RegFix 服务配置。");
        var message = $"已撤销 {removed} 个 RegFix 服务配置；相关程序文件未改动。";
        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(6));
        }

        MessageBox.Show(this, message, "撤销结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void RegisterAssociations_Click(object sender, RoutedEventArgs e)
    {
        RestoreAssociationsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RestoreAssociationsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = RestoredAssociations.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请先勾选要登记的应用关联候选。", "没有选中项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var valid = selected.Where(item => !string.IsNullOrWhiteSpace(item.ExecutablePath) && File.Exists(item.ExecutablePath)).ToArray();
        if (valid.Length == 0)
        {
            MessageBox.Show(this, "所选程序路径不存在。请编辑路径，或添加正确的软件目录后重新扫描。", "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prompt = $"将为 {valid.Length} 个应用创建当前用户的文件类型/协议候选注册。" + Environment.NewLine
            + "不会改动现有默认应用。注册完成后，请在 Windows 设置中检查并由你选择默认应用。继续吗？";
        if (MessageBox.Show(this, prompt, "确认注册关联候选", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();
        foreach (var item in valid)
        {
            try
            {
                item.RegisteredAppName = AssociationRegistrationService.RegisterCandidate(item, RestoreRoots.ToArray());
                item.RegisteredCapabilityPath = AssociationRegistrationService.GetCapabilityPath(item.ApplicationName, item.ExecutablePath);
                item.IsSelected = false;
                item.Status = $"已登记候选：{item.RegisteredAppName}；默认值未更改";
                succeeded++;
            }
            catch (Exception exception)
            {
                item.Status = $"未登记：{exception.Message}";
                failures.Add($"{item.ApplicationName}: {exception.Message}");
            }
        }

        RestoreAssociationsGrid.Items.Refresh();
        RegisterAssociationButton.IsEnabled = RestoredAssociations.Any(item => item.IsSelected);
        DeleteAssociationButton.IsEnabled = RestoredAssociations.Any(item => !string.IsNullOrWhiteSpace(item.RegisteredAppName));
        SetStatus($"关联候选登记完成：成功 {succeeded} 组，跳过或失败 {failures.Count} 组。");
        var message = $"已登记 {succeeded} 组关联候选；Windows 默认应用没有被更改。请使用“打开默认应用设置”完成用户确认。";
        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(6));
        }

        MessageBox.Show(this, message, "登记结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void DeleteAssociations_Click(object sender, RoutedEventArgs e)
    {
        RestoreAssociationsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RestoreAssociationsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = RestoredAssociations
            .Where(item => item.IsSelected && !string.IsNullOrWhiteSpace(item.RegisteredAppName))
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请勾选已登记的 RegFix 关联候选。", "没有可撤销项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"将撤销 {selected.Length} 组由 RegFix 创建的当前用户关联候选，不会更改 Windows 默认应用选择。继续吗？", "确认撤销关联候选", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = 0;
        var failures = new List<string>();
        foreach (var item in selected)
        {
            try
            {
                AssociationRegistrationService.RemoveCandidate(item);
                item.IsSelected = false;
                item.RegisteredAppName = "";
                item.RegisteredCapabilityPath = "";
                item.Status = "已撤销 RegFix 关联候选";
                removed++;
            }
            catch (Exception exception)
            {
                item.Status = $"未撤销：{exception.Message}";
                failures.Add($"{item.ApplicationName}: {exception.Message}");
            }
        }

        RestoreAssociationsGrid.Items.Refresh();
        DeleteAssociationButton.IsEnabled = RestoredAssociations.Any(item => !string.IsNullOrWhiteSpace(item.RegisteredAppName));
        SetStatus($"已撤销 {removed} 组关联候选。");
        var message = $"已撤销 {removed} 组 RegFix 关联候选；默认应用选择未更改。";
        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(6));
        }

        MessageBox.Show(this, message, "撤销结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void AddCaptureRoot_Click(object sender, RoutedEventArgs e)
        => AddFolderRoots(CaptureRoots, "选择需要补充扫描的软件目录");

    private void AddRestoreRoot_Click(object sender, RoutedEventArgs e)
        => AddFolderRoots(RestoreRoots, "选择重装后软件所在目录");

    private void RemoveCaptureRoot_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureRootsList.SelectedItem is string path)
        {
            CaptureRoots.Remove(path);
        }
    }

    private void RemoveRestoreRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RestoreRootsList.SelectedItem is string path)
        {
            RestoreRoots.Remove(path);
        }
    }

    private void OpenDefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法打开默认应用设置：{exception.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddFolderRoots(ObservableCollection<string> collection, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var folder in dialog.FolderNames)
        {
            var fullPath = Path.GetFullPath(folder);
            if (!collection.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                collection.Add(fullPath);
            }
        }
    }

    private async Task RunBusyOperationAsync(Func<Task> operation)
    {
        SetBusy(true);
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消。");
        }
        catch (Exception exception)
        {
            SetStatus($"操作失败：{exception.Message}");
            MessageBox.Show(this, exception.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private IProgress<string> CreateProgress()
        => new Progress<string>(message => SetStatus(message));

    private void SetBusy(bool busy)
    {
        CaptureScanButton.IsEnabled = !busy;
        AddCaptureRootButton.IsEnabled = !busy;
        RemoveCaptureRootButton.IsEnabled = !busy;
        SavePackageButton.IsEnabled = !busy && _capturePackage is not null && _capturePackagePath is null;
        OpenPackageButton.IsEnabled = !busy;
        AddRestoreRootButton.IsEnabled = !busy;
        RemoveRestoreRootButton.IsEnabled = !busy;
        ScanRestoreButton.IsEnabled = !busy && _restorePackage is not null;
        RestoreUninstallEntriesButton.IsEnabled = !busy && RestoredUninstallEntries.Any(item => item.IsSelected);
        DeleteUninstallEntriesButton.IsEnabled = !busy && RestoredUninstallEntries.Any(item => item.IsRegFixEntry);
        RestoreServicesButton.IsEnabled = !busy && RestoredServices.Count > 0;
        DeleteServicesButton.IsEnabled = !busy && RestoredServices.Any(item => item.IsRegFixOwned);
        RegisterAssociationButton.IsEnabled = !busy && RestoredAssociations.Count > 0;
        DeleteAssociationButton.IsEnabled = !busy && RestoredAssociations.Any(item => !string.IsNullOrWhiteSpace(item.RegisteredAppName));
        RestoreShortcutsButton.IsEnabled = !busy && RestoredShortcuts.Any(item => item.IsSelected);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void RestartAsAdministrator()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                throw new InvalidOperationException("无法确定 RegFix 程序路径，请手动右键选择“以管理员身份运行”。");
            }

            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"未能以管理员身份重新打开：{exception.Message}", "操作未完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SetStatus(string text)
    {
        if (StatusText.Dispatcher.CheckAccess())
        {
            StatusText.Text = text;
        }
        else
        {
            StatusText.Dispatcher.BeginInvoke(() => StatusText.Text = text);
        }
    }

    private static string BuildCaptureSummary(MigrationPackage package)
    {
        var summary = $"采集完成：软件 {package.Software.Count:N0} 项；开始菜单快捷方式 {package.Shortcuts.Count:N0} 项；第三方服务配置 {package.Services.Count:N0} 项；默认应用关联 {package.FileAssociations.Count:N0} 项。";
        if (package.CollectionNotes.Count > 0)
        {
            summary += $" 另有 {package.CollectionNotes.Count:N0} 条采集提示。";
        }

        return summary;
    }

    private static string BuildPackageSummary(MigrationPackage package)
    {
        var summary = $"软件 {package.Software.Count:N0} 项 · 快捷方式 {package.Shortcuts.Count:N0} 项 · 服务 {package.Services.Count:N0} 项 · 默认关联 {package.FileAssociations.Count:N0} 项。";
        if (package.CollectionNotes.Count > 0)
        {
            summary += $" 重装前采集提示 {package.CollectionNotes.Count:N0} 条。";
        }

        return summary;
    }
}
