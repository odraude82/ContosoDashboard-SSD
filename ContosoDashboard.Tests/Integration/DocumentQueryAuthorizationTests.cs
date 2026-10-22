using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentQueryAuthorizationTests
{
    [Fact]
    public async Task SearchCoversTitleDescriptionTagsUploaderAndProjectButExcludesUnauthorizedDocuments()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        await SeedSearchDocumentsAsync(context);
        var service = CreateService(context);

        foreach (var term in new[] { "Alpha", "crimson briefing", "release-tag", "Floris Kregel", "ContosoDashboard" })
        {
            var results = await service.SearchDocumentsAsync(4, new DocumentQuery(SearchTerm: term));

            Assert.NotEmpty(results);
            Assert.DoesNotContain(results, document => document.Title == "Confidential HR plan");
        }
    }

    [Fact]
    public async Task AppliesCategoryProjectDateFiltersAndRequestedSortOrders()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        await SeedSearchDocumentsAsync(context);
        var service = CreateService(context);

        var byTitle = await service.SearchDocumentsAsync(4, new DocumentQuery(SortBy: DocumentSortField.Title, Descending: false));
        var byDate = await service.SearchDocumentsAsync(4, new DocumentQuery(SortBy: DocumentSortField.UploadedAtUtc, Descending: false));
        var byCategory = await service.SearchDocumentsAsync(4, new DocumentQuery(SortBy: DocumentSortField.Category, Descending: false));
        var bySize = await service.SearchDocumentsAsync(4, new DocumentQuery(SortBy: DocumentSortField.FileSizeBytes, Descending: false));

        Assert.Equal(new[] { "Alpha plan", "Zebra plan" }, byTitle.Select(document => document.Title));
        Assert.Equal(new[] { 1, 2 }, byDate.Select(document => document.DocumentId));
        Assert.Equal(new[] { "Personal Files", "Reports" }, byCategory.Select(document => document.Category));
        Assert.Equal(new long[] { 100, 200 }, bySize.Select(document => document.FileSizeBytes));

        var categoryFilter = await service.SearchDocumentsAsync(4, new DocumentQuery(Category: "Personal Files"));
        var projectFilter = await service.SearchDocumentsAsync(4, new DocumentQuery(ProjectId: 1));
        var dateFilter = await service.SearchDocumentsAsync(4, new DocumentQuery(
            UploadedFromUtc: new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            UploadedToUtc: new DateTime(2025, 1, 4, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Single(categoryFilter);
        Assert.All(projectFilter, document => Assert.NotNull(document.ProjectName));
        Assert.Equal(new[] { "Zebra plan" }, dateFilter.Select(document => document.Title));
    }

    private static async Task SeedSearchDocumentsAsync(ApplicationDbContext context)
    {
        context.Users.Add(new User
        {
            UserId = 5,
            Email = "hr.user@contoso.com",
            DisplayName = "Harper HR",
            Department = "Human Resources",
            JobTitle = "Analyst",
            Role = UserRole.Employee,
            CreatedDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        context.Documents.AddRange(
            CreateDocument(1, "Alpha plan", "crimson briefing", "Reports", 4, 1, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), 200),
            CreateDocument(2, "Zebra plan", "project overview", "Personal Files", 3, 1, new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc), 100),
            CreateDocument(3, "Confidential HR plan", "crimson briefing", "Reports", 5, null, new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), 300));
        context.DocumentTags.Add(new DocumentTag { DocumentId = 2, Value = "release-tag" });
        await context.SaveChangesAsync();
    }

    private static Document CreateDocument(
        int id,
        string title,
        string description,
        string category,
        int uploaderId,
        int? projectId,
        DateTime uploadedAtUtc,
        long size) => new()
    {
        DocumentId = id,
        Title = title,
        Description = description,
        Category = category,
        OriginalFileName = $"{title}.pdf",
        RelativeStoragePath = $"files/{Guid.NewGuid():N}.bin",
        ContentType = "application/pdf",
        FileSizeBytes = size,
        UploadedByUserId = uploaderId,
        UploadedAtUtc = uploadedAtUtc,
        MalwareScannedAtUtc = uploadedAtUtc,
        ProjectId = projectId
    };

    private static DocumentService CreateService(ApplicationDbContext context)
    {
        var options = new DocumentStorageOptions();
        return new DocumentService(
            context,
            new UnusedStorage(),
            new UnusedScanner(),
            new DocumentFileValidator(options),
            Microsoft.Extensions.Options.Options.Create(options));
    }

    private sealed class UnusedStorage : ILocalFileStorage
    {
        public Task<StagedFile> StageAsync(Stream source, IProgress<long>? bytesCopied = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> PublishAsync(string stagingPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}