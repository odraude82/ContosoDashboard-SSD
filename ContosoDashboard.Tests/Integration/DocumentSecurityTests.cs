using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentSecurityTests
{
    [Fact]
    public async Task UnrelatedUserCannotDiscoverOpenEditReplaceOrDeleteDocumentById()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryTestFileStorage();
        var path = storage.AddPublishedFile("%PDF-1.7 private"u8.ToArray());
        var document = new Document
        {
            Title = "Private record",
            Category = "Reports",
            OriginalFileName = "private.pdf",
            RelativeStoragePath = path,
            ContentType = "application/pdf",
            FileSizeBytes = 16,
            UploadedByUserId = 4,
            UploadedAtUtc = DateTime.UtcNow,
            MalwareScannedAtUtc = DateTime.UtcNow
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        var options = new DocumentStorageOptions();
        var service = new DocumentService(
            context,
            storage,
            new StaticScanner(),
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));

        Assert.Empty(await service.SearchDocumentsAsync(5, new DocumentQuery()));
        Assert.Null(await service.GetAuthorizedDocumentAsync(document.DocumentId, 5));
        Assert.Null(await service.OpenAuthorizedContentAsync(document.DocumentId, 5, download: false));
        Assert.False(await service.UpdateMetadataAsync(document.DocumentId, 5, new DocumentMetadataUpdate("Changed", null, "Reports", [])));
        Assert.False((await service.ReplaceAsync(document.DocumentId, 5, ReplacementRequest())).Succeeded);
        Assert.False(await service.DeleteAsync(document.DocumentId, 5, confirmed: true));
        Assert.Equal("Private record", await context.Documents.Where(item => item.DocumentId == document.DocumentId).Select(item => item.Title).SingleAsync());
        Assert.Empty(await context.DocumentActivities.ToListAsync());
        await using var storedContent = await storage.OpenReadAsync(path);
        Assert.True(storedContent.CanRead);
    }

    private static DocumentUploadRequest ReplacementRequest()
    {
        var content = "%PDF-1.7 attacker"u8.ToArray();
        return new DocumentUploadRequest("other.pdf", "Other", null, "Reports", null, [], new MemoryStream(content), content.Length);
    }

    private sealed class StaticScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MalwareScanResult(MalwareScanOutcome.Clean));
    }
}