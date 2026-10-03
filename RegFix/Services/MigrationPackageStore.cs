using System.Text.Json;
using System.Text.Json.Serialization;
using RegFix.Models;

namespace RegFix.Services;

public static class MigrationPackageStore
{
    private const long MaximumPackageBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task SaveAsync(MigrationPackage package, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定迁移包所在目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, package, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<MigrationPackage> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到迁移包。", path);
        }

        if (info.Length > MaximumPackageBytes)
        {
            throw new InvalidDataException("迁移包超过 64 MB，已停止读取。");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var package = await JsonSerializer.DeserializeAsync<MigrationPackage>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("迁移包内容为空或格式无效。");

        if (package.SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持迁移包版本 {package.SchemaVersion}。");
        }

        package.Software ??= [];
        package.Shortcuts ??= [];
        package.Services ??= [];
        package.FileAssociations ??= [];
        package.CollectionNotes ??= [];
        return package;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
