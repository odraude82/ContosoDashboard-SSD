using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Data;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Add authentication state provider for Blazor
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();

// Configure Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Configure Mock Authentication (Cookie-based for training purposes)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

// Add authorization
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Employee", policy => policy.RequireRole("Employee", "TeamLead", "ProjectManager", "Administrator"));
    options.AddPolicy("TeamLead", policy => policy.RequireRole("TeamLead", "ProjectManager", "Administrator"));
    options.AddPolicy("ProjectManager", policy => policy.RequireRole("ProjectManager", "Administrator"));
    options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
});

// Register application services
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<SqliteMigrationBootstrapper>();
builder.Services.AddScoped<DocumentFileValidator>(services =>
    new DocumentFileValidator(services.GetRequiredService<IOptions<DocumentStorageOptions>>().Value));
builder.Services.AddScoped<ILocalFileStorage>(services =>
    new LocalFileStorageService(
        services.GetRequiredService<IOptions<DocumentStorageOptions>>().Value,
        services.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
builder.Services.AddScoped<IMalwareScanner>(services =>
    new ClamAvMalwareScanner(services.GetRequiredService<IOptions<MalwareScannerOptions>>().Value));

builder.Services.AddOptions<DocumentStorageOptions>()
    .Bind(builder.Configuration.GetSection(DocumentStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => DocumentStorageOptions.IsStorageRootOutsideWebRoot(
        options.RootPath,
        builder.Environment.ContentRootPath,
        builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot")),
        "Document storage must be outside wwwroot.")
    .ValidateOnStart();
builder.Services.AddOptions<MalwareScannerOptions>()
    .Bind(builder.Configuration.GetSection(MalwareScannerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Add HttpContextAccessor for accessing user claims
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Initialize database
await using (var scope = app.Services.CreateAsyncScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<SqliteMigrationBootstrapper>();
    await bootstrapper.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
else
{
    // Use HSTS even in development for training purposes
    app.UseHsts();
}

// Add security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    
    // Content Security Policy for Blazor Server
    context.Response.Headers["Content-Security-Policy"] = 
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
        "font-src 'self' https://cdn.jsdelivr.net; " +
        "img-src 'self' data: https:; " +
        "connect-src 'self' wss: ws:;";
    
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Enable authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapBlazorHub();
app.MapGet("/documents/{documentId:int}/content", async (
    int documentId,
    bool? download,
    HttpContext httpContext,
    DocumentService documentService,
    CancellationToken cancellationToken) =>
{
    var userIdValue = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (!int.TryParse(userIdValue, out var userId))
    {
        return Results.NotFound();
    }

    var content = await documentService.OpenAuthorizedContentAsync(
        documentId,
        userId,
        download == true,
        cancellationToken);
    if (content is null)
    {
        return Results.NotFound();
    }

    var isPreviewable = content.ContentType is "application/pdf" or "image/jpeg" or "image/png";
    var disposition = download != true && isPreviewable ? "inline" : "attachment";
    var safeFileName = Path.GetFileName(content.FileName).Replace("\r", string.Empty).Replace("\n", string.Empty);
    httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
    httpContext.Response.Headers.ContentDisposition =
        new System.Net.Http.Headers.ContentDispositionHeaderValue(disposition) { FileName = safeFileName }.ToString();

    return Results.Stream(content.Content, content.ContentType);
}).RequireAuthorization();
app.MapFallbackToPage("/_Host");

app.Run();

public partial class Program { }
