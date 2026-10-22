using System.Collections.Concurrent;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentUploadTests
{
    [Fact]
    public async Task CleanUploadPersistsDocumentAndSuccessfulActivity()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryFileStorage();
        var scanner = new FakeMalwareScanner(new MalwareScanResult(MalwareScanOutcome.Clean, "test-engine"));
        var service = CreateService(context, storage, scanner);

        var result = Assert.Single(await service.UploadAsync(4, [CreateRequest("report.pdf", "%PDF-1.7\n")]));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("report.pdf", result.FileName);
        var document = await context.Documents.SingleAsync();
        Assert.Equal("Quarterly report", document.Title);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal("test-engine", document.ScannerVersion);
        Assert.Single(storage.PublishedFiles);
        var activity = await context.DocumentActivities.SingleAsync();
        Assert.Equal("Upload", activity.Action);
        Assert.Equal("Success", activity.Outcome);
    }

    [Fact]
    public async Task BatchReturnsPerFileResultsAndReportsCopyProgress()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryFileStorage();
        var scanner = new FakeMalwareScanner(new MalwareScanResult(MalwareScanOutcome.Clean));
        var service = CreateService(context, storage, scanner);
        var progress = new RecordingProgress<DocumentUploadProgress>();

        var results = await service.UploadAsync(4,
        [
            CreateRequest("report.pdf", "%PDF-1.7\n"),
            CreateRequest("notes.exe", "not supported")
        ], progress);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Succeeded, results[0].Error);
        Assert.False(results[1].Succeeded);
        Assert.Contains(progress.Values, item => item.BytesCopied > 0 && item.TotalBytes > 0);
        Assert.All(progress.Values, item => Assert.Equal(2, item.TotalFiles));
        Assert.Single(await context.Documents.ToListAsync());
    }

    [Theory]
    [InlineData(MalwareScanOutcome.Infected)]
    [InlineData(MalwareScanOutcome.Unavailable)]
    [InlineData(MalwareScanOutcome.Indeterminate)]
    public async Task NonCleanScannerVerdictsRejectAndAuditUpload(MalwareScanOutcome outcome)
    {
        await AssertRejectedUploadAsync(new FakeMalwareScanner(new MalwareScanResult(outcome)));
    }

    [Fact]
    public async Task ScannerTimeoutRejectsAndAuditsUpload()
    {
        await AssertRejectedUploadAsync(new FakeMalwareScanner(timeout: true));
    }

    private static async Task AssertRejectedUploadAsync(FakeMalwareScanner scanner)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryFileStorage();
        var service = CreateService(context, storage, scanner);

        var result = Assert.Single(await service.UploadAsync(4, [CreateRequest("report.pdf", "%PDF-1.7\n")]));

        Assert.False(result.Succeeded);
        Assert.Empty(await context.Documents.ToListAsync());
        Assert.Empty(storage.StagedFiles);
        var activity = await context.DocumentActivities.SingleAsync();
        Assert.Equal("UploadRejected", activity.Action);
        Assert.Equal("Rejected", activity.Outcome);
    }

    private static DocumentService CreateService(
        ContosoDashboard.Data.ApplicationDbContext context,
        ILocalFileStorage storage,
        IMalwareScanner scanner)
    {
        var options = new DocumentStorageOptions();
        return new DocumentService(
            context,
            storage,
            scanner,
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));
    }

    private static DocumentUploadRequest CreateRequest(string fileName, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new DocumentUploadRequest(
            fileName,
            "Quarterly report",
            null,
            "Reports",
            null,
            [],
            new MemoryStream(bytes),
            bytes.LongLength);
    }

    private sealed class FakeMalwareScanner : IMalwareScanner
    {
        private readonly MalwareScanResult? result;
        private readonly bool timeout;

        public FakeMalwareScanner(MalwareScanResult? result = null, bool timeout = false)
        {
            this.result = result;
            this.timeout = timeout;
        }

        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            if (timeout)
            {
                throw new TimeoutException("Test scanner timeout.");
            }

            return Task.FromResult(result!);
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class InMemoryFileStorage : ILocalFileStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> staged = new();

        public ConcurrentDictionary<string, byte[]> StagedFiles => staged;
        public ConcurrentDictionary<string, byte[]> PublishedFiles { get; } = new();

        public async Task<StagedFile> StageAsync(
            Stream source,
            IProgress<long>? bytesCopied = null,
            CancellationToken cancellationToken = default)
        {
            var path = $"staging/{Guid.NewGuid():N}";
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

            staged[path] = buffer.ToArray();
            return new StagedFile(path, total);
        }

        public Task<string> PublishAsync(string stagingPath, CancellationToken cancellationToken = default)
        {
            if (!staged.TryRemove(stagingPath, out var content))
            {
                throw new FileNotFoundException("Staging file not found.");
            }

            var finalPath = $"documents/{Guid.NewGuid():N}";
            PublishedFiles[finalPath] = content;
            return Task.FromResult(finalPath);
        }

        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            if (!PublishedFiles.TryGetValue(relativePath, out var content)
                && !staged.TryGetValue(relativePath, out content))
            {
                throw new FileNotFoundException("Stored file not found.");
            }

            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            staged.TryRemove(relativePath, out _);
            PublishedFiles.TryRemove(relativePath, out _);
            return Task.CompletedTask;
        }
    }
}