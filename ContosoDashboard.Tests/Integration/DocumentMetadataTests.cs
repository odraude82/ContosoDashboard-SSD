using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentMetadataTests
{
    [Fact]
    public async Task OwnerCanUpdateMetadataAndTags()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var document = await SeedDocumentAsync(context, 4);
        var service = CreateService(context);

        var updated = await service.UpdateMetadataAsync(
            document.DocumentId,
            4,
            new DocumentMetadataUpdate("Updated title", "Updated description", "Project Documents", ["Release", " release "]));

        Assert.True(updated);
        Assert.Equal("Updated title", document.Title);
        Assert.Equal("Updated description", document.Description);
        Assert.Equal(new[] { "release" }, document.Tags.Select(tag => tag.Value));
        Assert.Equal("MetadataUpdated", await context.DocumentActivities.Select(activity => activity.Action).SingleAsync());
    }

    [Fact]
    public async Task NonOwnerCannotUpdateMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var document = await SeedDocumentAsync(context, 4);
        var service = CreateService(context);

        var updated = await service.UpdateMetadataAsync(
            document.DocumentId,
            3,
            new DocumentMetadataUpdate("Changed", null, "Reports", []));

        Assert.False(updated);
        Assert.Equal("Original title", document.Title);
        Assert.Empty(await context.DocumentActivities.ToListAsync());
    }

    private static async Task<Document> SeedDocumentAsync(ApplicationDbContext context, int ownerId)
    {
        var document = new Document
        {
            Title = "Original title",
            Category = "Reports",
            OriginalFileName = "original.pdf",
            RelativeStoragePath = "files/original.bin",
            ContentType = "application/pdf",
            FileSizeBytes = 10,
            UploadedByUserId = ownerId,
            UploadedAtUtc = DateTime.UtcNow,
            MalwareScannedAtUtc = DateTime.UtcNow
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return document;
    }

    private static DocumentService CreateService(ApplicationDbContext context)
    {
        var options = new DocumentStorageOptions();
        return new DocumentService(
            context,
            new InMemoryTestFileStorage(),
            new StaticScanner(new MalwareScanResult(MalwareScanOutcome.Clean)),
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));
    }

    private sealed class StaticScanner(MalwareScanResult result) : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}