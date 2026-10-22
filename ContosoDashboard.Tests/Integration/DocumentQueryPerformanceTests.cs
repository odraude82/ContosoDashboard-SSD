using System.Diagnostics;
using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentQueryPerformanceTests
{
    [Fact]
    public async Task ListAndSearchFiveHundredDocumentsAndPreviewWithinTargets()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryTestFileStorage();
        var contentPath = storage.AddPublishedFile("%PDF-1.7 performance"u8.ToArray());
        var now = DateTime.UtcNow;
        context.Documents.AddRange(Enumerable.Range(1, 500).Select(index => new Document
        {
            Title = $"Performance document {index:D3}",
            Description = index % 2 == 0 ? "Quarterly search target" : null,
            Category = "Reports",
            OriginalFileName = $"{index}.pdf",
            RelativeStoragePath = index == 1 ? contentPath : $"files/{index}.bin",
            ContentType = "application/pdf",
            FileSizeBytes = 22,
            UploadedByUserId = 4,
            UploadedAtUtc = now.AddSeconds(index),
            MalwareScannedAtUtc = now
        }));
        await context.SaveChangesAsync();
        var options = new DocumentStorageOptions();
        var service = new DocumentService(
            context,
            storage,
            new StaticScanner(),
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));

        var listTimer = Stopwatch.StartNew();
        var list = await service.SearchDocumentsAsync(4, new DocumentQuery());
        listTimer.Stop();
        var searchTimer = Stopwatch.StartNew();
        var search = await service.SearchDocumentsAsync(4, new DocumentQuery(SearchTerm: "quarterly"));
        searchTimer.Stop();
        var previewTimer = Stopwatch.StartNew();
        var preview = await service.OpenAuthorizedContentAsync(list[^1].DocumentId, 4, download: false);
        previewTimer.Stop();
        if (preview is not null)
        {
            await preview.Content.DisposeAsync();
        }

        Assert.Equal(500, list.Count);
        Assert.Equal(250, search.Count);
        Assert.NotNull(preview);
        Assert.True(listTimer.Elapsed < TimeSpan.FromSeconds(2), $"List took {listTimer.Elapsed}.");
        Assert.True(searchTimer.Elapsed < TimeSpan.FromSeconds(2), $"Search took {searchTimer.Elapsed}.");
        Assert.True(previewTimer.Elapsed < TimeSpan.FromSeconds(3), $"Preview took {previewTimer.Elapsed}.");
    }

    private sealed class StaticScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MalwareScanResult(MalwareScanOutcome.Clean));
    }
}