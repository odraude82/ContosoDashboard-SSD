using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentLifecycleTests
{
    [Fact]
    public async Task RejectedReplacementPreservesExistingContentAndSuccessfulReplacementIsAudited()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryTestFileStorage();
        var oldContent = "%PDF-1.7 original"u8.ToArray();
        var oldPath = storage.AddPublishedFile(oldContent);
        var document = await SeedDocumentAsync(context, oldPath);
        var rejectingService = CreateService(context, storage, MalwareScanOutcome.Infected);

        var rejected = await rejectingService.ReplaceAsync(document.DocumentId, 4, CreateReplacementRequest());

        Assert.False(rejected.Succeeded);
        await using (var oldStream = await storage.OpenReadAsync(oldPath))
        using (var output = new MemoryStream())
        {
            await oldStream.CopyToAsync(output);
            Assert.Equal(oldContent, output.ToArray());
        }

        var acceptingService = CreateService(context, storage, MalwareScanOutcome.Clean);
        var replaced = await acceptingService.ReplaceAsync(document.DocumentId, 4, CreateReplacementRequest());

        Assert.True(replaced.Succeeded, replaced.Error);
        Assert.NotEqual(oldPath, document.RelativeStoragePath);
        Assert.Contains("Replacement", await context.DocumentActivities.Select(activity => activity.Action).ToListAsync());
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(oldPath));
    }

    [Fact]
    public async Task DeleteRequiresConfirmationAndAllowsOnlyOwnerProjectManagerOrAdministrator()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryTestFileStorage();
        var ownerPath = storage.AddPublishedFile("owner"u8.ToArray());
        var projectPath = storage.AddPublishedFile("project"u8.ToArray());
        var adminPath = storage.AddPublishedFile("admin"u8.ToArray());
        var ownedDocument = await SeedDocumentAsync(context, ownerPath, ownerId: 4);
        var projectDocument = await SeedDocumentAsync(context, projectPath, ownerId: 3, projectId: 1);
        var adminDocument = await SeedDocumentAsync(context, adminPath, ownerId: 3);
        var service = CreateService(context, storage, MalwareScanOutcome.Clean);

        Assert.False(await service.DeleteAsync(ownedDocument.DocumentId, 4, confirmed: false));
        Assert.False(await service.DeleteAsync(ownedDocument.DocumentId, 5, confirmed: true));
        Assert.True(await service.DeleteAsync(ownedDocument.DocumentId, 4, confirmed: true));
        Assert.True(await service.DeleteAsync(projectDocument.DocumentId, 2, confirmed: true));
        Assert.True(await service.DeleteAsync(adminDocument.DocumentId, 1, confirmed: true));
        Assert.Empty(await context.Documents.ToListAsync());
        Assert.Equal(3, await context.DocumentActivities.CountAsync(activity => activity.Action == "Delete"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(ownerPath));
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(projectPath));
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(adminPath));
    }

    private static async Task<Document> SeedDocumentAsync(
        ApplicationDbContext context,
        string storagePath,
        int ownerId = 4,
        int? projectId = null)
    {
        var document = new Document
        {
            Title = "Original title",
            Category = "Reports",
            OriginalFileName = "original.pdf",
            RelativeStoragePath = storagePath,
            ContentType = "application/pdf",
            FileSizeBytes = 10,
            UploadedByUserId = ownerId,
            UploadedAtUtc = DateTime.UtcNow,
            MalwareScannedAtUtc = DateTime.UtcNow,
            ProjectId = projectId
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return document;
    }

    private static DocumentUploadRequest CreateReplacementRequest()
    {
        var content = "%PDF-1.7 replacement"u8.ToArray();
        return new DocumentUploadRequest(
            "replacement.pdf",
            "Replacement title",
            null,
            "Reports",
            null,
            [],
            new MemoryStream(content),
            content.LongLength);
    }

    private static DocumentService CreateService(
        ApplicationDbContext context,
        ILocalFileStorage storage,
        MalwareScanOutcome outcome)
    {
        var options = new DocumentStorageOptions();
        return new DocumentService(
            context,
            storage,
            new StaticScanner(outcome),
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));
    }

    private sealed class StaticScanner(MalwareScanOutcome outcome) : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MalwareScanResult(outcome));
    }
}