using System.Net;
using System.Net.Http.Headers;
using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class DocumentContentRouteTests : IClassFixture<ContosoWebApplicationFactory>
{
    private readonly ContosoWebApplicationFactory factory;

    public DocumentContentRouteTests(ContosoWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void ContentEndpointIsRegistered()
    {
        var endpointSources = factory.Services.GetServices<EndpointDataSource>();
        var routePatterns = endpointSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/documents/{documentId:int}/content", routePatterns);
    }

    [Fact]
    public async Task RequiresAuthenticationAndReturnsNotFoundForUnauthorizedOrMissingDocuments()
    {
        var documentId = await AddDocumentAsync("application/pdf", "%PDF-1.7\n"u8.ToArray(), 4);
        await EnsureUserExistsAsync(5);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var anonymousResponse = await client.GetAsync($"/documents/{documentId}/content");
        client.DefaultRequestHeaders.Add("X-Test-User", "5");
        var unauthorizedResponse = await client.GetAsync($"/documents/{documentId}/content");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "4");
        var missingResponse = await client.GetAsync("/documents/99999/content");

        Assert.Equal(HttpStatusCode.Redirect, anonymousResponse.StatusCode);
        Assert.Equal("/login", anonymousResponse.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, unauthorizedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Theory]
    [InlineData("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 })]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    public async Task SupportedPreviewTypesAreInlineAndSetNosniff(string contentType, byte[] content)
    {
        var documentId = await AddDocumentAsync(contentType, content, 4);
        using var client = CreateAuthenticatedClient(4);

        using var response = await client.GetAsync($"/documents/{documentId}/content?download=false");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            contentType == response.Content.Headers.ContentType?.MediaType,
            await response.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("text/plain", "notes.txt")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "report.docx")]
    public async Task NonPreviewFormatsAndExplicitDownloadsAreAttachments(string contentType, string fileName)
    {
        var documentId = await AddDocumentAsync(contentType, "content"u8.ToArray(), 4, fileName);
        using var client = CreateAuthenticatedClient(4);

        using var response = await client.GetAsync($"/documents/{documentId}/content?download=false");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task PreviewAndDownloadRecordDistinctActivityActions()
    {
        var documentId = await AddDocumentAsync("application/pdf", "%PDF-1.7\n"u8.ToArray(), 4);
        using var client = CreateAuthenticatedClient(4);

        using var preview = await client.GetAsync($"/documents/{documentId}/content?download=false");
        using var download = await client.GetAsync($"/documents/{documentId}/content?download=true");

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actions = await context.DocumentActivities
            .Where(activity => activity.DocumentId == documentId)
            .Select(activity => activity.Action)
            .ToListAsync();
        Assert.Contains("Preview", actions);
        Assert.Contains("Download", actions);
    }

    private HttpClient CreateAuthenticatedClient(int userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        return client;
    }

    private async Task<int> AddDocumentAsync(string contentType, byte[] content, int uploaderId, string? fileName = null)
    {
        var storagePath = factory.Storage.AddPublishedFile(content);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var document = new Document
        {
            Title = fileName ?? "Test document",
            Category = "Reports",
            OriginalFileName = fileName ?? "test.pdf",
            RelativeStoragePath = storagePath,
            ContentType = contentType,
            FileSizeBytes = content.LongLength,
            UploadedByUserId = uploaderId,
            UploadedAtUtc = DateTime.UtcNow,
            MalwareScannedAtUtc = DateTime.UtcNow
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return document.DocumentId;
    }

    private async Task EnsureUserExistsAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await context.Users.AnyAsync(user => user.UserId == userId))
        {
            context.Users.Add(new User
            {
                UserId = userId,
                Email = "unauthorized@contoso.com",
                DisplayName = "Unrelated User",
                Department = "Human Resources",
                JobTitle = "Analyst",
                Role = UserRole.Employee,
                CreatedDate = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
    }
}