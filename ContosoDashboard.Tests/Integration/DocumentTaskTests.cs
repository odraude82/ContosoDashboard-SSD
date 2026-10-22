using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentTaskTests
{
    [Fact]
    public async Task AuthorizedUsersCanLinkAndViewTaskDocumentsButUnrelatedUsersCannot()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var document = await SeedDocumentAsync(context);
        var service = CreateService(context);

        Assert.True(await service.LinkDocumentToTaskAsync(document.DocumentId, 1, 4));
        Assert.Contains(await service.GetTaskDocumentsAsync(1, 4), item => item.DocumentId == document.DocumentId);
        Assert.Contains(await service.GetTaskDocumentsAsync(1, 2), item => item.DocumentId == document.DocumentId);
        Assert.Empty(await service.GetTaskDocumentsAsync(1, 5));
        Assert.False(await service.LinkDocumentToTaskAsync(document.DocumentId, 1, 5));
    }

    [Fact]
    public async Task TaskUploadInheritsProjectAndLinksTheDocument()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context);
        var content = "%PDF-1.7 task upload"u8.ToArray();
        var request = new DocumentUploadRequest(
            "task.pdf", "Task file", null, "Project Documents", null, [], new MemoryStream(content), content.Length, TaskId: 1);

        var result = (await service.UploadAsync(4, [request])).Single();

        Assert.True(result.Succeeded, result.Error);
        var uploaded = await context.Documents.SingleAsync(document => document.DocumentId == result.DocumentId);
        Assert.Equal(1, uploaded.ProjectId);
        Assert.True(await context.DocumentTasks.AnyAsync(link => link.DocumentId == uploaded.DocumentId && link.TaskId == 1));
    }

    private static async Task<Document> SeedDocumentAsync(ApplicationDbContext context)
    {
        var document = new Document
        {
            Title = "Task attachment",
            Category = "Project Documents",
            OriginalFileName = "task.pdf",
            RelativeStoragePath = "files/task.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 16,
            UploadedByUserId = 4,
            UploadedAtUtc = DateTime.UtcNow,
            MalwareScannedAtUtc = DateTime.UtcNow,
            ProjectId = 1
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