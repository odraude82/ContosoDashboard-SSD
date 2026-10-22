using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Xunit;

namespace ContosoDashboard.Tests.Unit;

public sealed class LocalFileStorageServiceTests
{
    [Fact]
    public async Task StreamsToGeneratedStagingPathAndPublishesPrivately()
    {
        using var root = new TemporaryStorageRoot();
        var service = new LocalFileStorageService(new DocumentStorageOptions { RootPath = "private" }, root.RootPath);
        var progress = new RecordingProgress();
        var content = new byte[] { 1, 2, 3, 4, 5 };

        var staged = await service.StageAsync(new MemoryStream(content), progress);

        Assert.StartsWith("staging/", staged.RelativePath, StringComparison.Ordinal);
        Assert.Equal(content.LongLength, staged.Length);
        Assert.NotEmpty(progress.Values);
        var stagingFullPath = Path.Combine(root.RootPath, "private", staged.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(stagingFullPath));

        var publishedPath = await service.PublishAsync(staged.RelativePath);

        Assert.StartsWith("files/", publishedPath, StringComparison.Ordinal);
        Assert.False(File.Exists(stagingFullPath));
        using (var output = new MemoryStream())
        {
            await using var readStream = await service.OpenReadAsync(publishedPath);
            await readStream.CopyToAsync(output);
            Assert.Equal(content, output.ToArray());
        }

        await service.DeleteAsync(publishedPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.OpenReadAsync(publishedPath));
    }

    [Fact]
    public async Task RejectsPathsOutsideManagedStorageFolders()
    {
        using var root = new TemporaryStorageRoot();
        var service = new LocalFileStorageService(new DocumentStorageOptions { RootPath = "private" }, root.RootPath);

        await Assert.ThrowsAsync<ArgumentException>(() => service.OpenReadAsync("../secret.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync("files/00000000000000000000000000000000.bin"));
    }

    [Fact]
    public async Task OversizedStreamIsDeletedFromStaging()
    {
        using var root = new TemporaryStorageRoot();
        var options = new DocumentStorageOptions { RootPath = "private", MaxFileSizeBytes = 4 };
        var service = new LocalFileStorageService(options, root.RootPath);

        await Assert.ThrowsAsync<DocumentFileTooLargeException>(
            () => service.StageAsync(new MemoryStream([1, 2, 3, 4, 5])));

        var stagingDirectory = Path.Combine(root.RootPath, "private", "staging");
        Assert.Empty(Directory.GetFiles(stagingDirectory));
    }

    private sealed class RecordingProgress : IProgress<long>
    {
        public List<long> Values { get; } = [];

        public void Report(long value) => Values.Add(value);
    }
}