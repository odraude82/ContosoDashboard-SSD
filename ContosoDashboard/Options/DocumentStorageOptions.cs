using System.ComponentModel.DataAnnotations;

namespace ContosoDashboard.Options;

public sealed class DocumentStorageOptions
{
    public const string SectionName = "DocumentStorage";
    public const long MaximumAllowedFileSizeBytes = 25 * 1024 * 1024;

    [Required]
    public string RootPath { get; set; } = "AppData/uploads";

    [Range(1, MaximumAllowedFileSizeBytes)]
    public long MaxFileSizeBytes { get; set; } = MaximumAllowedFileSizeBytes;

    [Range(1, 10)]
    public int MaxFilesPerBatch { get; set; } = 10;

    public static bool IsStorageRootOutsideWebRoot(
        string storageRootPath,
        string contentRootPath,
        string webRootPath)
    {
        var storageRoot = Path.GetFullPath(storageRootPath, contentRootPath);
        var webRoot = Path.GetFullPath(webRootPath, contentRootPath);
        var relativePath = Path.GetRelativePath(webRoot, storageRoot);

        return relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}