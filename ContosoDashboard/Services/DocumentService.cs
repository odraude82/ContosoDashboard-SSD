using System.Net.Sockets;
using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContosoDashboard.Services;

public sealed class DocumentService
{
    private static readonly IReadOnlyList<string> CategoryValues = Array.AsReadOnly(new[]
    {
        "Project Documents",
        "Team Resources",
        "Personal Files",
        "Reports",
        "Presentations",
        "Other"
    });
    private static readonly HashSet<string> AllowedCategories = new(CategoryValues, StringComparer.Ordinal);

    private readonly ApplicationDbContext context;
    private readonly ILocalFileStorage storage;
    private readonly IMalwareScanner scanner;
    private readonly DocumentFileValidator fileValidator;
    private readonly DocumentStorageOptions storageOptions;
    private readonly INotificationService notificationService;

    public DocumentService(
        ApplicationDbContext context,
        ILocalFileStorage storage,
        IMalwareScanner scanner,
        DocumentFileValidator fileValidator,
        IOptions<DocumentStorageOptions> storageOptions,
        INotificationService? notificationService = null)
    {
        this.context = context;
        this.storage = storage;
        this.scanner = scanner;
        this.fileValidator = fileValidator;
        this.storageOptions = storageOptions.Value;
        this.notificationService = notificationService ?? new NotificationService(context);
    }

    public static IReadOnlyList<string> Categories => CategoryValues;

    public async Task<IReadOnlyList<DocumentListItem>> GetMyDocumentsAsync(
        int userId,
        CancellationToken cancellationToken = default) =>
        await context.Documents
            .AsNoTracking()
            .Where(document => document.UploadedByUserId == userId)
            .OrderByDescending(document => document.UploadedAtUtc)
            .Select(document => new DocumentListItem(
                document.DocumentId,
                document.Title,
                document.Category,
                document.UploadedAtUtc,
                document.FileSizeBytes,
                document.Project == null ? null : document.Project.Name,
                document.ContentType,
                document.UploadedByUserId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DocumentListItem>> SearchDocumentsAsync(
        int userId,
        DocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var requestingUser = await context.Users.AsNoTracking()
            .Where(user => user.UserId == userId)
            .Select(user => new { user.Role, user.Department })
            .SingleOrDefaultAsync(cancellationToken);
        if (requestingUser is null)
        {
            return [];
        }

        var documents = context.Documents.AsNoTracking()
            .Where(document => document.UploadedByUserId == userId
                || requestingUser.Role == UserRole.Administrator
                || (document.ProjectId != null
                    && (document.Project!.ProjectManagerId == userId
                        || document.Project.ProjectMembers.Any(member => member.UserId == userId)))
                || (requestingUser.Role == UserRole.TeamLead
                    && document.UploadedByUser.Department == requestingUser.Department)
                || context.DocumentShares.Any(share => share.DocumentId == document.DocumentId
                    && share.RevokedAtUtc == null
                    && (share.RecipientUserId == userId
                        || (share.RecipientDepartment != null && share.RecipientDepartment == requestingUser.Department))));

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var searchTerm = query.SearchTerm.Trim().ToLowerInvariant();
            documents = documents.Where(document =>
                document.Title.ToLower().Contains(searchTerm)
                || (document.Description != null && document.Description.ToLower().Contains(searchTerm))
                || document.Tags.Any(tag => tag.Value.ToLower().Contains(searchTerm))
                || document.UploadedByUser.DisplayName.ToLower().Contains(searchTerm)
                || document.UploadedByUser.Email.ToLower().Contains(searchTerm)
                || (document.Project != null && document.Project.Name.ToLower().Contains(searchTerm)));
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            documents = documents.Where(document => document.Category == query.Category.Trim());
        }

        if (query.ProjectId is int projectId)
        {
            documents = documents.Where(document => document.ProjectId == projectId);
        }

        if (query.UploadedFromUtc is DateTime fromUtc)
        {
            documents = documents.Where(document => document.UploadedAtUtc >= fromUtc);
        }

        if (query.UploadedToUtc is DateTime toUtc)
        {
            documents = documents.Where(document => document.UploadedAtUtc <= toUtc);
        }

        IOrderedQueryable<Document> orderedDocuments = query.SortBy switch
        {
            DocumentSortField.Title => query.Descending
                ? documents.OrderByDescending(document => document.Title)
                : documents.OrderBy(document => document.Title),
            DocumentSortField.Category => query.Descending
                ? documents.OrderByDescending(document => document.Category)
                : documents.OrderBy(document => document.Category),
            DocumentSortField.FileSizeBytes => query.Descending
                ? documents.OrderByDescending(document => document.FileSizeBytes)
                : documents.OrderBy(document => document.FileSizeBytes),
            _ => query.Descending
                ? documents.OrderByDescending(document => document.UploadedAtUtc)
                : documents.OrderBy(document => document.UploadedAtUtc)
        };

        return await orderedDocuments
            .ThenBy(document => document.DocumentId)
            .Take(500)
            .Select(document => new DocumentListItem(
                document.DocumentId,
                document.Title,
                document.Category,
                document.UploadedAtUtc,
                document.FileSizeBytes,
                document.Project == null ? null : document.Project.Name,
                document.ContentType,
                document.UploadedByUserId))
            .ToListAsync(cancellationToken);
    }

    public async Task<DocumentContent?> OpenAuthorizedContentAsync(
        int documentId,
        int requestingUserId,
        bool download,
        CancellationToken cancellationToken = default)
    {
        if (documentId <= 0)
        {
            return null;
        }

        var document = await context.Documents
            .AsNoTracking()
            .Include(item => item.UploadedByUser)
            .Include(item => item.Project)
                .ThenInclude(project => project!.ProjectMembers)
            .SingleOrDefaultAsync(item => item.DocumentId == documentId, cancellationToken);
        if (document is null || !CanReadDocument(document, requestingUserId))
        {
            return null;
        }

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.RelativeStoragePath, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var activity = CreateActivity(
            requestingUserId,
            document.Title,
            download ? "Download" : "Preview",
            "Success");
        activity.DocumentId = document.DocumentId;
        context.DocumentActivities.Add(activity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            context.Entry(activity).State = EntityState.Detached;
            await content.DisposeAsync();
            throw;
        }

        return new DocumentContent(content, document.ContentType, document.OriginalFileName);
    }

    public async Task<DocumentListItem?> GetAuthorizedDocumentAsync(
        int documentId,
        int requestingUserId,
        CancellationToken cancellationToken = default)
    {
        var document = await context.Documents
            .AsNoTracking()
            .Include(item => item.UploadedByUser)
            .Include(item => item.Project)
                .ThenInclude(project => project!.ProjectMembers)
            .SingleOrDefaultAsync(item => item.DocumentId == documentId, cancellationToken);
        if (document is null || !CanReadDocument(document, requestingUserId))
        {
            return null;
        }

        return new DocumentListItem(
            document.DocumentId,
            document.Title,
            document.Category,
            document.UploadedAtUtc,
            document.FileSizeBytes,
            document.Project?.Name,
            document.ContentType,
            document.UploadedByUserId,
            document.Description,
            document.Tags.Select(tag => tag.Value).ToArray());
    }

    public async Task<IReadOnlyList<DocumentListItem>> GetSharedDocumentsAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var department = await context.Users.AsNoTracking()
            .Where(user => user.UserId == userId)
            .Select(user => user.Department)
            .SingleOrDefaultAsync(cancellationToken);
        if (department is null)
        {
            return [];
        }

        return await context.Documents.AsNoTracking()
            .Where(document => context.DocumentShares.Any(share =>
                share.DocumentId == document.DocumentId
                && share.RevokedAtUtc == null
                && (share.RecipientUserId == userId
                    || (share.RecipientDepartment != null && share.RecipientDepartment == department))))
            .OrderByDescending(document => document.UploadedAtUtc)
            .Select(document => new DocumentListItem(
                document.DocumentId,
                document.Title,
                document.Category,
                document.UploadedAtUtc,
                document.FileSizeBytes,
                document.Project == null ? null : document.Project.Name,
                document.ContentType,
                document.UploadedByUserId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentShareRecipient>> GetShareRecipientOptionsAsync(
        int ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var users = await context.Users.AsNoTracking()
            .Where(user => user.UserId != ownerUserId)
            .OrderBy(user => user.DisplayName)
            .Select(user => new { user.UserId, user.DisplayName, user.Department })
            .ToListAsync(cancellationToken);

        return users.Select(user => new DocumentShareRecipient(user.UserId, user.DisplayName, user.Department))
            .ToArray();
    }

    public async Task<IReadOnlyList<DocumentShareListItem>> GetDocumentSharesAsync(
        int documentId,
        int ownerUserId,
        CancellationToken cancellationToken = default) =>
        await context.DocumentShares.AsNoTracking()
            .Where(share => share.DocumentId == documentId
                && share.RevokedAtUtc == null
                && share.SharedByUserId == ownerUserId)
            .OrderBy(share => share.RecipientDepartment)
            .Select(share => new DocumentShareListItem(
                share.DocumentShareId,
                share.RecipientDepartment ?? context.Users
                    .Where(user => user.UserId == share.RecipientUserId)
                    .Select(user => user.DisplayName)
                    .FirstOrDefault() ?? "User",
                share.RecipientDepartment != null))
            .ToListAsync(cancellationToken);

    public async Task<bool> ShareWithUserAsync(
        int documentId,
        int ownerUserId,
        int recipientUserId,
        CancellationToken cancellationToken = default)
    {
        if (recipientUserId == ownerUserId
            || !await context.Users.AnyAsync(user => user.UserId == recipientUserId, cancellationToken))
        {
            return false;
        }

        return await CreateShareAsync(documentId, ownerUserId, recipientUserId, null, [recipientUserId], cancellationToken);
    }

    public async Task<bool> ShareWithDepartmentAsync(
        int documentId,
        int ownerUserId,
        string department,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(department))
        {
            return false;
        }

        var currentDepartment = department.Trim();
        var recipients = await context.Users.AsNoTracking()
            .Where(user => user.Department == currentDepartment && user.UserId != ownerUserId)
            .Select(user => user.UserId)
            .ToListAsync(cancellationToken);
        if (recipients.Count == 0)
        {
            return false;
        }

        return await CreateShareAsync(documentId, ownerUserId, null, currentDepartment, recipients, cancellationToken);
    }

    public async Task<bool> RevokeShareAsync(
        int shareId,
        int ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var share = await context.DocumentShares
            .SingleOrDefaultAsync(item => item.DocumentShareId == shareId, cancellationToken);
        if (share is null || share.RevokedAtUtc is not null
            || !await context.Documents.AnyAsync(document =>
                document.DocumentId == share.DocumentId && document.UploadedByUserId == ownerUserId,
                cancellationToken))
        {
            return false;
        }

        share.RevokedAtUtc = DateTime.UtcNow;
        var title = await context.Documents.Where(document => document.DocumentId == share.DocumentId)
            .Select(document => document.Title)
            .SingleAsync(cancellationToken);
        var activity = CreateActivity(ownerUserId, title, "ShareRevoked", "Success");
        activity.DocumentId = share.DocumentId;
        activity.TargetSnapshot = share.RecipientDepartment ?? share.RecipientUserId?.ToString();
        context.DocumentActivities.Add(activity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> CreateShareAsync(
        int documentId,
        int ownerUserId,
        int? recipientUserId,
        string? recipientDepartment,
        IReadOnlyCollection<int> recipientIds,
        CancellationToken cancellationToken)
    {
        var document = await context.Documents.SingleOrDefaultAsync(item =>
            item.DocumentId == documentId && item.UploadedByUserId == ownerUserId,
            cancellationToken);
        if (document is null)
        {
            return false;
        }

        var existingShare = await context.DocumentShares.AnyAsync(share =>
            share.DocumentId == documentId && share.RevokedAtUtc == null
            && (recipientUserId != null
                ? share.RecipientUserId == recipientUserId
                : share.RecipientDepartment == recipientDepartment), cancellationToken);
        if (existingShare)
        {
            return false;
        }

        var share = new DocumentShare
        {
            DocumentId = documentId,
            SharedByUserId = ownerUserId,
            RecipientUserId = recipientUserId,
            RecipientDepartment = recipientDepartment,
            SharedAtUtc = DateTime.UtcNow
        };
        var activity = CreateActivity(ownerUserId, document.Title, "Share", "Success");
        activity.DocumentId = document.DocumentId;
        activity.TargetSnapshot = recipientDepartment ?? recipientUserId?.ToString();
        context.DocumentShares.Add(share);
        context.DocumentActivities.Add(activity);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var recipientId in recipientIds.Distinct())
        {
            await notificationService.CreateNotificationAsync(new Notification
            {
                UserId = recipientId,
                Title = "Document shared with you",
                Message = $"A document was shared with you: {document.Title}",
                Type = NotificationType.DocumentShared,
                Priority = NotificationPriority.Informational
            });
        }

        return true;
    }

    private bool CanReadDocument(Document document, int requestingUserId)
    {
        var requester = context.Users.AsNoTracking()
            .Where(user => user.UserId == requestingUserId)
            .Select(user => new { user.Role, user.Department })
            .SingleOrDefault();
        if (requester is null)
        {
            return false;
        }

        return document.UploadedByUserId == requestingUserId
            || requester.Role == UserRole.Administrator
            || document.Project?.ProjectManagerId == requestingUserId
            || document.Project?.ProjectMembers.Any(member => member.UserId == requestingUserId) == true
            || (requester.Role == UserRole.TeamLead
                && !string.IsNullOrWhiteSpace(requester.Department)
                && string.Equals(document.UploadedByUser.Department, requester.Department, StringComparison.Ordinal))
            || context.DocumentShares.Any(share => share.DocumentId == document.DocumentId
                && share.RevokedAtUtc == null
                && (share.RecipientUserId == requestingUserId
                    || (share.RecipientDepartment != null && share.RecipientDepartment == requester.Department)));
    }

        public async Task<IReadOnlyList<DocumentListItem>> GetTaskDocumentsAsync(
            int taskId,
            int requestingUserId,
            CancellationToken cancellationToken = default)
        {
            if (await GetAuthorizedTaskAsync(taskId, requestingUserId, cancellationToken) is null)
            {
                return [];
            }

            var documents = await context.Documents
                .AsNoTracking()
                .Include(document => document.UploadedByUser)
                .Include(document => document.Project)
                    .ThenInclude(project => project!.ProjectMembers)
                .Where(document => context.DocumentTasks.Any(link =>
                    link.DocumentId == document.DocumentId && link.TaskId == taskId))
                .OrderByDescending(document => document.UploadedAtUtc)
                .ToListAsync(cancellationToken);

            return documents
                .Where(document => CanReadDocument(document, requestingUserId))
                .Select(document => new DocumentListItem(
                    document.DocumentId,
                    document.Title,
                    document.Category,
                    document.UploadedAtUtc,
                    document.FileSizeBytes,
                    document.Project?.Name,
                    document.ContentType,
                    document.UploadedByUserId))
                .ToArray();
        }

        public async Task<bool> LinkDocumentToTaskAsync(
            int documentId,
            int taskId,
            int requestingUserId,
            CancellationToken cancellationToken = default)
        {
            var task = await GetAuthorizedTaskAsync(taskId, requestingUserId, cancellationToken);
            var document = await context.Documents
                .Include(item => item.UploadedByUser)
                .Include(item => item.Project)
                    .ThenInclude(project => project!.ProjectMembers)
                .SingleOrDefaultAsync(item => item.DocumentId == documentId, cancellationToken);
            if (task is null || document is null || !CanReadDocument(document, requestingUserId)
                || await context.DocumentTasks.AnyAsync(link => link.DocumentId == documentId && link.TaskId == taskId, cancellationToken))
            {
                return false;
            }

            context.DocumentTasks.Add(new DocumentTask
            {
                DocumentId = documentId,
                TaskId = taskId,
                LinkedByUserId = requestingUserId,
                LinkedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        private async Task<TaskItem?> GetAuthorizedTaskAsync(
            int taskId,
            int requestingUserId,
            CancellationToken cancellationToken)
        {
            var task = await context.Tasks
                .Include(item => item.Project)
                    .ThenInclude(project => project!.ProjectMembers)
                .SingleOrDefaultAsync(item => item.TaskId == taskId, cancellationToken);
            if (task is null)
            {
                return null;
            }

            var isAuthorized = task.AssignedUserId == requestingUserId
                || task.CreatedByUserId == requestingUserId
                || task.Project?.ProjectManagerId == requestingUserId
                || task.Project?.ProjectMembers.Any(member => member.UserId == requestingUserId) == true;
            return isAuthorized ? task : null;
        }

        public async Task<DocumentActivityReport> GetDocumentActivityReportAsync(
            int requestingUserId,
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            CancellationToken cancellationToken = default)
        {
            var requesterRole = await context.Users.AsNoTracking()
                .Where(user => user.UserId == requestingUserId)
                .Select(user => (UserRole?)user.Role)
                .SingleOrDefaultAsync(cancellationToken);
            if (requesterRole != UserRole.Administrator)
            {
                throw new UnauthorizedAccessException("Administrator access is required to review document activity.");
            }

            var activityQuery = context.DocumentActivities.AsNoTracking();
            if (fromUtc is DateTime start)
            {
                activityQuery = activityQuery.Where(activity => activity.OccurredAtUtc >= start);
            }

            if (toUtc is DateTime end)
            {
                activityQuery = activityQuery.Where(activity => activity.OccurredAtUtc <= end);
            }

            var events = await activityQuery
                .OrderByDescending(activity => activity.OccurredAtUtc)
                .Select(activity => new DocumentActivityListItem(
                    activity.DocumentActivityId,
                    activity.DocumentId,
                    activity.DocumentTitleSnapshot,
                    activity.ActorUserId,
                    activity.Action,
                    activity.Outcome,
                    activity.OccurredAtUtc,
                    activity.TargetSnapshot))
                .ToListAsync(cancellationToken);
            var fileTypeRows = await context.Documents.AsNoTracking()
                .GroupBy(document => document.ContentType)
                .Select(group => new { ContentType = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ToListAsync(cancellationToken);
            var uploaderRows = await context.Documents.AsNoTracking()
                .GroupBy(document => new { document.UploadedByUserId, document.UploadedByUser.DisplayName })
                .Select(group => new { UserId = group.Key.UploadedByUserId, group.Key.DisplayName, DocumentCount = group.Count() })
                .OrderByDescending(item => item.DocumentCount)
                .ToListAsync(cancellationToken);
            var fileTypes = fileTypeRows
                .Select(item => new DocumentTypeReportItem(item.ContentType, item.Count))
                .ToArray();
            var uploaders = uploaderRows
                .Select(item => new DocumentUploaderReportItem(item.UserId, item.DisplayName, item.DocumentCount))
                .ToArray();

            return new DocumentActivityReport(
                events,
                fileTypes,
                uploaders,
                events.Count(activity => activity.Action == "Preview"),
                events.Count(activity => activity.Action == "Download"));
        }

    public async Task<bool> UpdateMetadataAsync(
        int documentId,
        int requestingUserId,
        DocumentMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var document = await context.Documents
            .Include(item => item.Tags)
            .SingleOrDefaultAsync(item => item.DocumentId == documentId, cancellationToken);
        if (document is null || document.UploadedByUserId != requestingUserId)
        {
            return false;
        }

        var title = update.Title?.Trim();
        var category = update.Category?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 255
            || update.Description?.Length > 2000
            || category is null || !AllowedCategories.Contains(category)
            || update.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Trim().Length > 100))
        {
            return false;
        }

        document.Title = title;
        document.Description = NormalizeDescription(update.Description);
        document.Category = category;
        context.DocumentTags.RemoveRange(document.Tags);
        document.Tags.Clear();
        foreach (var tag in NormalizeTags(update.Tags))
        {
            document.Tags.Add(new DocumentTag { Value = tag });
        }

        var activity = CreateActivity(requestingUserId, document.Title, "MetadataUpdated", "Success");
        activity.DocumentId = document.DocumentId;
        context.DocumentActivities.Add(activity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<DocumentUploadResult> ReplaceAsync(
        int documentId,
        int requestingUserId,
        DocumentUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = await context.Documents
            .SingleOrDefaultAsync(item => item.DocumentId == documentId, cancellationToken);
        if (document is null || document.UploadedByUserId != requestingUserId)
        {
            return DocumentUploadResult.Failure(GetDisplayFileName(request.FileName), "You are not authorized to replace this document.");
        }

        var fileName = GetDisplayFileName(request.FileName);
        if (fileName.Length is 0 or > 255 || request.FileSizeBytes <= 0 || request.FileSizeBytes > storageOptions.MaxFileSizeBytes)
        {
            return DocumentUploadResult.Failure(fileName, "The replacement file name or size is invalid.");
        }

        StagedFile? stagedFile = null;
        string? replacementPath = null;
        try
        {
            stagedFile = await storage.StageAsync(request.Content, cancellationToken: cancellationToken);
            DocumentFileValidationResult validation;
            await using (var content = await storage.OpenReadAsync(stagedFile.RelativePath, cancellationToken))
            {
                validation = await fileValidator.ValidateAsync(fileName, content, stagedFile.Length, cancellationToken);
            }

            if (!validation.IsValid)
            {
                await RecordDocumentActivityAsync(document, requestingUserId, "ReplacementRejected", "Rejected", cancellationToken);
                return DocumentUploadResult.Failure(fileName, validation.Error ?? "The replacement file was rejected.");
            }

            MalwareScanResult scanResult;
            await using (var content = await storage.OpenReadAsync(stagedFile.RelativePath, cancellationToken))
            {
                scanResult = await scanner.ScanAsync(content, cancellationToken);
            }

            if (scanResult.Outcome != MalwareScanOutcome.Clean)
            {
                await RecordDocumentActivityAsync(document, requestingUserId, "ReplacementRejected", "Rejected", cancellationToken);
                return DocumentUploadResult.Failure(fileName, "The replacement file could not be confirmed safe by the malware scanner.");
            }

            var oldPath = document.RelativeStoragePath;
            var replacementSizeBytes = stagedFile.Length;
            replacementPath = await storage.PublishAsync(stagedFile.RelativePath, cancellationToken);
            stagedFile = null;
            document.RelativeStoragePath = replacementPath;
            document.OriginalFileName = fileName;
            document.ContentType = validation.ContentType!;
            document.FileSizeBytes = replacementSizeBytes;
            document.MalwareScannedAtUtc = DateTime.UtcNow;
            document.ScannerVersion = Truncate(scanResult.ScannerVersion, 255);
            var activity = CreateActivity(requestingUserId, document.Title, "Replacement", "Success");
            activity.DocumentId = document.DocumentId;
            context.DocumentActivities.Add(activity);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                context.Entry(document).State = EntityState.Unchanged;
                context.Entry(activity).State = EntityState.Detached;
                await TryDeleteAsync(replacementPath);
                throw;
            }

            await TryDeleteAsync(oldPath);
            return DocumentUploadResult.Success(fileName, document.DocumentId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SocketException or DbUpdateException)
        {
            if (replacementPath is not null)
            {
                await TryDeleteAsync(replacementPath);
            }

            await RecordDocumentActivityAsync(document, requestingUserId, "ReplacementFailed", "Failed", CancellationToken.None);
            return DocumentUploadResult.Failure(fileName, "The replacement file could not be stored or scanned.");
        }
        finally
        {
            if (stagedFile is not null)
            {
                await TryDeleteAsync(stagedFile.RelativePath);
            }
        }
    }

    public async Task<bool> DeleteAsync(
        int documentId,
        int requestingUserId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return false;
        }

        var document = await context.Documents
            .Include(item => item.Project)
            .SingleOrDefaultAsync(item => item.DocumentId == documentId, cancellationToken);
        var requesterRole = await context.Users.AsNoTracking()
            .Where(user => user.UserId == requestingUserId)
            .Select(user => (UserRole?)user.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null || requesterRole is null
            || (document.UploadedByUserId != requestingUserId
                && requesterRole != UserRole.Administrator
                && document.Project?.ProjectManagerId != requestingUserId))
        {
            return false;
        }

        var oldPath = document.RelativeStoragePath;
        var activity = CreateActivity(requestingUserId, document.Title, "Delete", "Success");
        activity.DocumentId = document.DocumentId;
        context.DocumentActivities.Add(activity);
        context.Documents.Remove(document);
        await context.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(oldPath, cancellationToken);
        return true;
    }

    private async Task RecordDocumentActivityAsync(
        Document document,
        int actorUserId,
        string action,
        string outcome,
        CancellationToken cancellationToken)
    {
        var activity = CreateActivity(actorUserId, document.Title, action, outcome);
        activity.DocumentId = document.DocumentId;
        context.DocumentActivities.Add(activity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentUploadResult>> UploadAsync(
        int uploaderUserId,
        IReadOnlyList<DocumentUploadRequest> requests,
        IProgress<DocumentUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var uploader = await context.Users.SingleOrDefaultAsync(user => user.UserId == uploaderUserId, cancellationToken);
        if (uploader is null)
        {
            throw new UnauthorizedAccessException("The authenticated uploader could not be found.");
        }

        if (requests.Count > storageOptions.MaxFilesPerBatch)
        {
            var rejected = new List<DocumentUploadResult>(requests.Count);
            foreach (var request in requests)
            {
                await RecordActivityAsync(
                    uploaderUserId,
                    request.Title,
                    "UploadRejected",
                    "Rejected",
                    cancellationToken);
                rejected.Add(DocumentUploadResult.Failure(
                    GetDisplayFileName(request.FileName),
                    $"A batch can contain no more than {storageOptions.MaxFilesPerBatch} files."));
            }

            return rejected;
        }

        var results = new List<DocumentUploadResult>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = requests[index];
            if (request.TaskId is int taskId)
            {
                var task = await GetAuthorizedTaskAsync(taskId, uploaderUserId, cancellationToken);
                if (task is null)
                {
                    results.Add(DocumentUploadResult.Failure(GetDisplayFileName(request.FileName), "You are not authorized to add files to this task."));
                    continue;
                }

                request = request with { ProjectId = task.ProjectId };
            }

            var result = await UploadOneAsync(
                uploader,
                request,
                index,
                requests.Count,
                progress,
                cancellationToken);
            results.Add(result);
            if (result.Succeeded && result.DocumentId is int uploadedDocumentId && request.TaskId is int linkedTaskId)
            {
                await LinkDocumentToTaskAsync(uploadedDocumentId, linkedTaskId, uploaderUserId, cancellationToken);
            }
        }

        return results;
    }

    private async Task<DocumentUploadResult> UploadOneAsync(
        User uploader,
        DocumentUploadRequest request,
        int fileIndex,
        int totalFiles,
        IProgress<DocumentUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileName = GetDisplayFileName(request.FileName);
        var metadataError = await ValidateMetadataAsync(uploader, request, fileName, cancellationToken);
        if (metadataError is not null)
        {
            await RecordActivityAsync(uploader.UserId, request.Title, "UploadRejected", "Rejected", cancellationToken);
            return DocumentUploadResult.Failure(fileName, metadataError);
        }

        StagedFile? stagedFile = null;
        string? finalPath = null;
        try
        {
            var copyProgress = new InlineProgress<long>(bytesCopied =>
                progress?.Report(new DocumentUploadProgress(
                    fileIndex,
                    fileIndex,
                    totalFiles,
                    fileName,
                    bytesCopied,
                    request.FileSizeBytes)));
            stagedFile = await storage.StageAsync(request.Content, copyProgress, cancellationToken);

            DocumentFileValidationResult validation;
            await using (var stagedContent = await storage.OpenReadAsync(stagedFile.RelativePath, cancellationToken))
            {
                validation = await fileValidator.ValidateAsync(
                    fileName,
                    stagedContent,
                    stagedFile.Length,
                    cancellationToken);
            }

            if (!validation.IsValid)
            {
                await RecordActivityAsync(uploader.UserId, request.Title, "UploadRejected", "Rejected", cancellationToken);
                return DocumentUploadResult.Failure(fileName, validation.Error ?? "The file was rejected.");
            }

            MalwareScanResult scanResult;
            try
            {
                await using var stagedContent = await storage.OpenReadAsync(stagedFile.RelativePath, cancellationToken);
                scanResult = await scanner.ScanAsync(stagedContent, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                scanResult = new MalwareScanResult(MalwareScanOutcome.Indeterminate);
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException)
            {
                scanResult = new MalwareScanResult(MalwareScanOutcome.Unavailable);
            }

            if (scanResult.Outcome != MalwareScanOutcome.Clean)
            {
                await RecordActivityAsync(uploader.UserId, request.Title, "UploadRejected", "Rejected", cancellationToken);
                return DocumentUploadResult.Failure(fileName, "The file could not be confirmed safe by the malware scanner.");
            }

            var stagedSizeBytes = stagedFile.Length;
            finalPath = await storage.PublishAsync(stagedFile.RelativePath, cancellationToken);
            stagedFile = null;
            var now = DateTime.UtcNow;
            var document = new Document
            {
                Title = request.Title.Trim(),
                Description = NormalizeDescription(request.Description),
                Category = request.Category.Trim(),
                OriginalFileName = fileName,
                RelativeStoragePath = finalPath,
                ContentType = validation.ContentType!,
                FileSizeBytes = stagedSizeBytes,
                UploadedByUserId = uploader.UserId,
                UploadedAtUtc = now,
                ProjectId = request.ProjectId,
                MalwareScannedAtUtc = now,
                ScannerVersion = Truncate(scanResult.ScannerVersion, 255)
            };

            foreach (var tag in NormalizeTags(request.Tags))
            {
                document.Tags.Add(new DocumentTag { Value = tag });
            }

            var activity = CreateActivity(uploader.UserId, document.Title, "Upload", "Success");
            context.Documents.Add(document);
            context.DocumentActivities.Add(activity);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DetachNewDocument(document, activity);
                await TryDeleteAsync(finalPath);
                throw;
            }
            catch (Exception exception) when (exception is DbUpdateException or IOException)
            {
                DetachNewDocument(document, activity);
                await TryDeleteAsync(finalPath);
                await RecordActivityAsync(uploader.UserId, document.Title, "UploadFailed", "Failed", CancellationToken.None);
                return DocumentUploadResult.Failure(fileName, "The document metadata could not be saved.");
            }

            if (document.ProjectId is int projectId)
            {
                await NotifyProjectMembersAsync(projectId, uploader.UserId, document.Title, cancellationToken);
            }

            progress?.Report(new DocumentUploadProgress(
                fileIndex,
                fileIndex + 1,
                totalFiles,
                fileName,
                document.FileSizeBytes,
                document.FileSizeBytes));
            return DocumentUploadResult.Success(fileName, document.DocumentId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DocumentFileTooLargeException)
        {
            await RecordActivityAsync(uploader.UserId, request.Title, "UploadRejected", "Rejected", CancellationToken.None);
            return DocumentUploadResult.Failure(fileName, "The file exceeds the 25 MB per-file limit.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SocketException)
        {
            await RecordActivityAsync(uploader.UserId, request.Title, "UploadFailed", "Failed", CancellationToken.None);
            return DocumentUploadResult.Failure(fileName, "The file could not be stored or scanned.");
        }
        finally
        {
            if (stagedFile is not null)
            {
                await TryDeleteAsync(stagedFile.RelativePath);
            }
        }
    }

    private async Task<string?> ValidateMetadataAsync(
        User uploader,
        DocumentUploadRequest request,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 255)
        {
            return "A title between 1 and 255 characters is required.";
        }

        if (request.Description?.Length > 2000)
        {
            return "The description cannot exceed 2,000 characters.";
        }

        if (!AllowedCategories.Contains(request.Category?.Trim() ?? string.Empty))
        {
            return "Select one of the supported document categories.";
        }

        if (fileName.Length is 0 or > 255)
        {
            return "The original file name must contain between 1 and 255 characters.";
        }

        if (request.FileSizeBytes <= 0 || request.FileSizeBytes > storageOptions.MaxFileSizeBytes)
        {
            return "The file must be greater than zero and no larger than 25 MB.";
        }

        if (request.Tags is not null && request.Tags.Any(tag =>
                string.IsNullOrWhiteSpace(tag) || tag.Trim().Length > 100))
        {
            return "Each tag must contain between 1 and 100 characters.";
        }

        if (request.TaskId is int taskId)
        {
            var task = await GetAuthorizedTaskAsync(taskId, uploader.UserId, cancellationToken);
            return task is not null && task.ProjectId == request.ProjectId
                ? null
                : "The task's project could not be verified.";
        }

        if (request.ProjectId is not int projectId)
        {
            return null;
        }

        var canUploadToProject = await context.Projects.AnyAsync(project =>
            project.ProjectId == projectId
            && (project.ProjectManagerId == uploader.UserId
                || uploader.Role == UserRole.Administrator
                || project.ProjectMembers.Any(member => member.UserId == uploader.UserId)),
            cancellationToken);

        return canUploadToProject ? null : "You are not authorized to add documents to this project.";
    }

    private async Task RecordActivityAsync(
        int actorUserId,
        string? title,
        string action,
        string outcome,
        CancellationToken cancellationToken)
    {
        var activity = CreateActivity(actorUserId, title, action, outcome);
        context.DocumentActivities.Add(activity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or OperationCanceledException)
        {
            context.Entry(activity).State = EntityState.Detached;
        }
    }

    private async Task NotifyProjectMembersAsync(
        int projectId,
        int uploaderUserId,
        string title,
        CancellationToken cancellationToken)
    {
        var project = await context.Projects.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new
            {
                item.ProjectManagerId,
                MemberIds = item.ProjectMembers.Select(member => member.UserId).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return;
        }

        var recipients = project.MemberIds.Append(project.ProjectManagerId)
            .Where(userId => userId != uploaderUserId)
            .Distinct()
            .ToArray();
        foreach (var recipientId in recipients)
        {
            await notificationService.CreateNotificationAsync(new Notification
            {
                UserId = recipientId,
                Title = "Document added to project",
                Message = $"A document was added to your project: {title}",
                Type = NotificationType.ProjectDocumentAdded,
                Priority = NotificationPriority.Informational
            });
        }
    }

    private static DocumentActivity CreateActivity(int actorUserId, string? title, string action, string outcome) => new()
    {
        ActorUserId = actorUserId,
        DocumentTitleSnapshot = Truncate(title?.Trim(), 255),
        Action = action,
        Outcome = outcome,
        OccurredAtUtc = DateTime.UtcNow
    };

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) => tags is null
        ? []
        : tags.Select(tag => tag.Trim().ToLowerInvariant())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeDescription(string? description) => description?.Trim() ?? string.Empty;

    private static string GetDisplayFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return fileName.Replace('\\', '/').Split('/').Last();
    }

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];

    private void DetachNewDocument(Document document, DocumentActivity activity)
    {
        foreach (var tag in document.Tags)
        {
            context.Entry(tag).State = EntityState.Detached;
        }

        context.Entry(document).State = EntityState.Detached;
        context.Entry(activity).State = EntityState.Detached;
    }

    private async Task TryDeleteAsync(string relativePath)
    {
        try
        {
            await storage.DeleteAsync(relativePath, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed record DocumentUploadRequest(
    string FileName,
    string Title,
    string? Description,
    string Category,
    int? ProjectId,
    IReadOnlyCollection<string> Tags,
    Stream Content,
    long FileSizeBytes,
    int? TaskId = null);

public sealed record DocumentMetadataUpdate(
    string Title,
    string? Description,
    string Category,
    IReadOnlyCollection<string> Tags);

public sealed record DocumentShareRecipient(int UserId, string DisplayName, string? Department);

public sealed record DocumentShareListItem(int ShareId, string RecipientName, bool IsDepartmentShare);

public sealed record DocumentActivityListItem(
    int ActivityId,
    int? DocumentId,
    string? DocumentTitle,
    int ActorUserId,
    string Action,
    string Outcome,
    DateTime OccurredAtUtc,
    string? Target);

public sealed record DocumentTypeReportItem(string ContentType, int Count);

public sealed record DocumentUploaderReportItem(int UserId, string DisplayName, int DocumentCount);

public sealed record DocumentActivityReport(
    IReadOnlyList<DocumentActivityListItem> Events,
    IReadOnlyList<DocumentTypeReportItem> FileTypes,
    IReadOnlyList<DocumentUploaderReportItem> Uploaders,
    int PreviewCount,
    int DownloadCount);

public sealed record DocumentUploadResult(bool Succeeded, string FileName, int? DocumentId, string? Error)
{
    public static DocumentUploadResult Success(string fileName, int documentId) => new(true, fileName, documentId, null);

    public static DocumentUploadResult Failure(string fileName, string error) => new(false, fileName, null, error);
}

public sealed record DocumentUploadProgress(
    int FileIndex,
    int FilesCompleted,
    int TotalFiles,
    string FileName,
    long BytesCopied,
    long TotalBytes);

public sealed record DocumentListItem(
    int DocumentId,
    string Title,
    string Category,
    DateTime UploadedAtUtc,
    long FileSizeBytes,
    string? ProjectName,
    string ContentType,
    int UploadedByUserId = 0,
    string? Description = null,
    IReadOnlyList<string>? Tags = null);

public sealed record DocumentContent(Stream Content, string ContentType, string FileName);

public sealed record DocumentQuery(
    string? SearchTerm = null,
    DocumentSortField SortBy = DocumentSortField.UploadedAtUtc,
    bool Descending = true,
    string? Category = null,
    int? ProjectId = null,
    DateTime? UploadedFromUtc = null,
    DateTime? UploadedToUtc = null);

public enum DocumentSortField
{
    Title,
    UploadedAtUtc,
    Category,
    FileSizeBytes
}