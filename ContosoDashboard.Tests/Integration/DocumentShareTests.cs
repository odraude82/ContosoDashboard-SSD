using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentShareTests
{
    [Fact]
    public async Task OwnerCanShareWithUserAndDepartmentAndRecipientsAreNotified()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var document = await SeedDocumentAsync(context);
        var service = CreateService(context);

        var directShare = await service.ShareWithUserAsync(document.DocumentId, 4, 3);
        var departmentShare = await service.ShareWithDepartmentAsync(document.DocumentId, 4, "Engineering");

        Assert.True(directShare);
        Assert.True(departmentShare);
        Assert.Equal(2, await context.DocumentShares.CountAsync());
        Assert.Equal(2, await context.Notifications.CountAsync(notification => notification.UserId == 3));
        Assert.Equal(1, await context.Notifications.CountAsync(notification => notification.UserId == 2));
        Assert.Contains(await service.SearchDocumentsAsync(3, new DocumentQuery()), item => item.DocumentId == document.DocumentId);
        Assert.Contains(await service.GetSharedDocumentsAsync(2), item => item.DocumentId == document.DocumentId);
    }

    [Fact]
    public async Task ShareIsOwnerOnlyReadOnlyAndRevocationOrDepartmentChangeImmediatelyRemovesAccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var storage = new InMemoryTestFileStorage();
        var document = await SeedDocumentAsync(context, storage);
        var service = CreateService(context, storage);

        Assert.False(await service.ShareWithUserAsync(document.DocumentId, 3, 2));
        Assert.True(await service.ShareWithUserAsync(document.DocumentId, 4, 2));
        Assert.NotNull(await service.OpenAuthorizedContentAsync(document.DocumentId, 2, download: false));
        Assert.False(await service.UpdateMetadataAsync(document.DocumentId, 2, new DocumentMetadataUpdate("Changed", null, "Reports", [])));
        Assert.False((await service.ReplaceAsync(document.DocumentId, 2, ReplacementRequest())).Succeeded);

        var share = await context.DocumentShares.SingleAsync();
        Assert.True(await service.RevokeShareAsync(share.DocumentShareId, 4));
        Assert.Null(await service.OpenAuthorizedContentAsync(document.DocumentId, 2, download: false));

        Assert.True(await service.ShareWithDepartmentAsync(document.DocumentId, 4, "Engineering"));
        var user = await context.Users.SingleAsync(item => item.UserId == 2);
        user.Department = "Finance";
        await context.SaveChangesAsync();
        Assert.Null(await service.OpenAuthorizedContentAsync(document.DocumentId, 2, download: false));
        Assert.False(await service.ShareWithDepartmentAsync(document.DocumentId, 5, "Finance"));
    }

    private static async Task<Document> SeedDocumentAsync(ApplicationDbContext context, InMemoryTestFileStorage? storage = null)
    {
        var document = new Document
        {
            Title = "Shared report",
            Category = "Reports",
            OriginalFileName = "report.pdf",
            RelativeStoragePath = storage?.AddPublishedFile("%PDF-1.7 report"u8.ToArray()) ?? "files/report.bin",
            ContentType = "application/pdf",
            FileSizeBytes = 15,
            UploadedByUserId = 4,
            UploadedAtUtc = DateTime.UtcNow,
            MalwareScannedAtUtc = DateTime.UtcNow
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return document;
    }

    private static DocumentUploadRequest ReplacementRequest()
    {
        var content = "%PDF-1.7 replacement"u8.ToArray();
        return new DocumentUploadRequest("replacement.pdf", "Replacement", null, "Reports", null, [], new MemoryStream(content), content.Length);
    }

    private static DocumentService CreateService(ApplicationDbContext context, ILocalFileStorage? storage = null)
    {
        var options = new DocumentStorageOptions();
        return new DocumentService(
            context,
            storage ?? new InMemoryTestFileStorage(),
            new StaticScanner(),
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));
    }

    private sealed class StaticScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MalwareScanResult(MalwareScanOutcome.Clean));
    }
}