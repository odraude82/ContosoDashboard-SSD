using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentActivityReportTests
{
    [Fact]
    public async Task AdministratorCanReviewEventsAndDistinctAccessPatterns()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Documents.AddRange(
            CreateDocument("Annual report", "application/pdf", 4),
            CreateDocument("Team notes", "text/plain", 3));
        context.DocumentActivities.AddRange(
            CreateActivity("Upload", 1),
            CreateActivity("Preview", 1),
            CreateActivity("Preview", 1),
            CreateActivity("Download", 1),
            CreateActivity("Share", 2),
            CreateActivity("Delete", 2));
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var report = await service.GetDocumentActivityReportAsync(1);

        Assert.Equal(6, report.Events.Count);
        Assert.Equal(2, report.FileTypes.Count);
        Assert.Contains(report.FileTypes, type => type.ContentType == "application/pdf" && type.Count == 1);
        Assert.Contains(report.Uploaders, uploader => uploader.UserId == 4 && uploader.DocumentCount == 1);
        Assert.Equal(2, report.PreviewCount);
        Assert.Equal(1, report.DownloadCount);
        Assert.Contains(report.Events, activity => activity.Action == "Share");
        Assert.Contains(report.Events, activity => activity.Action == "Delete");
    }

    [Fact]
    public async Task NonAdministratorCannotRequestReportData()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetDocumentActivityReportAsync(4));
    }

    private static Document CreateDocument(string title, string contentType, int uploaderId) => new()
    {
        Title = title,
        Category = "Reports",
        OriginalFileName = title + ".dat",
        RelativeStoragePath = "files/" + Guid.NewGuid().ToString("N"),
        ContentType = contentType,
        FileSizeBytes = 64,
        UploadedByUserId = uploaderId,
        UploadedAtUtc = DateTime.UtcNow,
        MalwareScannedAtUtc = DateTime.UtcNow
    };

    private static DocumentActivity CreateActivity(string action, int actorId) => new()
    {
        Action = action,
        Outcome = "Success",
        ActorUserId = actorId,
        DocumentTitleSnapshot = "Report",
        OccurredAtUtc = DateTime.UtcNow
    };

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