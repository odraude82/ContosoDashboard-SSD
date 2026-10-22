using ContosoDashboard.Options;

namespace ContosoDashboard.Services;

public sealed class LocalFileStorageService : ILocalFileStorage
{
    private const int CopyBufferSize = 81920;
    private readonly string storageRoot;
    private readonly long maximumFileSizeBytes;

    public LocalFileStorageService(DocumentStorageOptions options, string contentRootPath)
    {
        storageRoot = Path.GetFullPath(options.RootPath, contentRootPath);
        maximumFileSizeBytes = options.MaxFileSizeBytes;
    }

    public async Task<StagedFile> StageAsync(
        Stream source,
        IProgress<long>? bytesCopied = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        var relativePath = $"staging/{Guid.NewGuid():N}.tmp";
        var fullPath = ResolveManagedPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var completed = false;

        try
        {
            await using var destination = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[CopyBufferSize];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytes += bytesRead;
                if (totalBytes > maximumFileSizeBytes)
                {
                    throw new DocumentFileTooLargeException(maximumFileSizeBytes);
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesCopied?.Report(totalBytes);
            }

            await destination.FlushAsync(cancellationToken);
            completed = true;
            return new StagedFile(relativePath, totalBytes);
        }
        finally
        {
            if (!completed)
            {
                TryDelete(fullPath);
            }
        }
    }

    public Task<string> PublishAsync(string stagingPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stagingFullPath = ResolveManagedPath(stagingPath);
        if (!string.Equals(GetManagedFolder(stagingPath), "staging", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only a staged file can be published.", nameof(stagingPath));
        }

        var finalPath = $"files/{Guid.NewGuid():N}.bin";
        var finalFullPath = ResolveManagedPath(finalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalFullPath)!);
        File.Move(stagingFullPath, finalFullPath);
        return Task.FromResult(finalPath);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolveManagedPath(relativePath);
        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolveManagedPath(relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    private string ResolveManagedPath(string relativePath)
    {
        var folder = GetManagedFolder(relativePath);
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(storageRoot, normalizedPath));
        var relativeToRoot = Path.GetRelativePath(storageRoot, fullPath);
        if (relativeToRoot == "."
            || relativeToRoot == ".."
            || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relativeToRoot))
        {
            throw new ArgumentException("The storage path must remain inside the configured root.", nameof(relativePath));
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Length != 2
            || !string.Equals(folder, segments[0], StringComparison.Ordinal)
            || !HasGeneratedFileName(segments[1], folder))
        {
            throw new ArgumentException("The storage path is not a generated managed-file path.", nameof(relativePath));
        }

        return fullPath;
    }

    private static string GetManagedFolder(string relativePath)
    {
        var separatorIndex = relativePath.IndexOfAny(['/', '\\']);
        return separatorIndex < 0 ? string.Empty : relativePath[..separatorIndex];
    }

    private static bool HasGeneratedFileName(string fileName, string folder)
    {
        var expectedExtension = folder switch
        {
            "staging" => ".tmp",
            "files" => ".bin",
            _ => string.Empty
        };

        return expectedExtension.Length > 0
            && fileName.EndsWith(expectedExtension, StringComparison.Ordinal)
            && Guid.TryParseExact(fileName[..^expectedExtension.Length], "N", out _);
    }

    private static void TryDelete(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class DocumentFileTooLargeException(long maximumSizeBytes)
    : IOException($"The file exceeds the configured {maximumSizeBytes}-byte limit.");