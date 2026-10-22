using System.Collections.Concurrent;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests.Infrastructure;

public sealed class InMemoryTestFileStorage : ILocalFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> files = new();

    public string AddPublishedFile(byte[] content)
    {
        var path = $"files/{Guid.NewGuid():N}.bin";
        files[path] = content.ToArray();
        return path;
    }

    public async Task<StagedFile> StageAsync(
        Stream source,
        IProgress<long>? bytesCopied = null,
        CancellationToken cancellationToken = default)
    {
        await using var buffer = new MemoryStream();
        var block = new byte[4096];
        long total = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(block, cancellationToken)) > 0)
        {
            await buffer.WriteAsync(block.AsMemory(0, bytesRead), cancellationToken);
            total += bytesRead;
            bytesCopied?.Report(total);
        }

        var path = $"staging/{Guid.NewGuid():N}.tmp";
        files[path] = buffer.ToArray();
        return new StagedFile(path, total);
    }

    public Task<string> PublishAsync(string stagingPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!files.TryRemove(stagingPath, out var content))
        {
            throw new FileNotFoundException("Test staging file not found.");
        }

        return Task.FromResult(AddPublishedFile(content));
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!files.TryGetValue(relativePath, out var content))
        {
            throw new FileNotFoundException("Test file not found.");
        }

        return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        files.TryRemove(relativePath, out _);
        return Task.CompletedTask;
    }
}