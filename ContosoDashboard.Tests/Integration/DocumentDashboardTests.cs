using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentDashboardTests
{
    [Fact]
    public async Task DashboardReportsPersonalDocumentCountAndFiveNewestDocuments()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        for (var index = 1; index <= 6; index++)
        {
            context.Documents.Add(new Document
            {
                Title = $"Personal {index}",
                Category = "Reports",
                OriginalFileName = $"{index}.pdf",
                RelativeStoragePath = $"files/{index}.pdf",
                ContentType = "application/pdf",
                FileSizeBytes = 12,
                UploadedByUserId = 4,
                UploadedAtUtc = DateTime.UtcNow.AddMinutes(index),
                MalwareScannedAtUtc = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
        var summary = await new DashboardService(context).GetDashboardSummaryAsync(4);

        Assert.Equal(6, summary.PersonalDocumentCount);
        Assert.Equal(5, summary.RecentPersonalDocuments.Count);
        Assert.Equal("Personal 6", summary.RecentPersonalDocuments[0].Title);
    }

    [Fact]
    public async Task ProjectUploadNotifiesCurrentProjectMembersOnceAndNotUploader()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var options = new DocumentStorageOptions();
        var service = new DocumentService(
            context,
            new InMemoryTestFileStorage(),
            new StaticScanner(),
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));
        var content = "%PDF-1.7 project upload"u8.ToArray();
        var request = new DocumentUploadRequest("project.pdf", "Project file", null, "Project Documents", 1, [], new MemoryStream(content), content.Length);

        var result = (await service.UploadAsync(4, [request])).Single();

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, await context.Notifications.CountAsync(notification => notification.UserId == 2 && notification.Title == "Document added to project"));
        Assert.Equal(1, await context.Notifications.CountAsync(notification => notification.UserId == 3 && notification.Title == "Document added to project"));
        Assert.Equal(0, await context.Notifications.CountAsync(notification => notification.UserId == 4 && notification.Title == "Document added to project"));
    }

    private sealed class StaticScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MalwareScanResult(MalwareScanOutcome.Clean));
    }
}