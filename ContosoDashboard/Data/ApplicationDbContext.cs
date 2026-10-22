using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Models;

namespace ContosoDashboard.Data;

public class ApplicationDbContext : DbContext
{
    private static readonly DateTime SeedAnchorUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<TaskItem> Tasks { get; set; } = null!;
    public DbSet<Project> Projects { get; set; } = null!;
    public DbSet<TaskComment> TaskComments { get; set; } = null!;
    public DbSet<Notification> Notifications { get; set; } = null!;
    public DbSet<ProjectMember> ProjectMembers { get; set; } = null!;
    public DbSet<Announcement> Announcements { get; set; } = null!;
    public DbSet<Document> Documents { get; set; } = null!;
    public DbSet<DocumentTag> DocumentTags { get; set; } = null!;
    public DbSet<DocumentActivity> DocumentActivities { get; set; } = null!;
    public DbSet<DocumentShare> DocumentShares { get; set; } = null!;
    public DbSet<DocumentTask> DocumentTasks { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure User relationships
        modelBuilder.Entity<User>()
            .HasMany(u => u.AssignedTasks)
            .WithOne(t => t.AssignedUser)
            .HasForeignKey(t => t.AssignedUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<User>()
            .HasMany(u => u.CreatedTasks)
            .WithOne(t => t.CreatedByUser)
            .HasForeignKey(t => t.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<User>()
            .HasMany(u => u.ManagedProjects)
            .WithOne(p => p.ProjectManager)
            .HasForeignKey(p => p.ProjectManagerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Configure indexes for performance
        modelBuilder.Entity<TaskItem>()
            .HasIndex(t => t.AssignedUserId);

        modelBuilder.Entity<TaskItem>()
            .HasIndex(t => t.Status);

        modelBuilder.Entity<TaskItem>()
            .HasIndex(t => t.DueDate);

        modelBuilder.Entity<Project>()
            .HasIndex(p => p.ProjectManagerId);

        modelBuilder.Entity<Project>()
            .HasIndex(p => p.Status);

        modelBuilder.Entity<Notification>()
            .HasIndex(n => new { n.UserId, n.IsRead });

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasKey(document => document.DocumentId);
            entity.Property(document => document.Title).IsRequired().HasMaxLength(255);
            entity.Property(document => document.Description).HasMaxLength(2000);
            entity.Property(document => document.Category).IsRequired().HasMaxLength(50);
            entity.Property(document => document.OriginalFileName).IsRequired().HasMaxLength(255);
            entity.Property(document => document.RelativeStoragePath).IsRequired().HasMaxLength(1024);
            entity.Property(document => document.ContentType).IsRequired().HasMaxLength(255);
            entity.Property(document => document.ScannerVersion).HasMaxLength(255);
            entity.HasOne(document => document.UploadedByUser)
                .WithMany()
                .HasForeignKey(document => document.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(document => document.Project)
                .WithMany()
                .HasForeignKey(document => document.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(document => new { document.UploadedByUserId, document.UploadedAtUtc });
            entity.HasIndex(document => new { document.ProjectId, document.UploadedAtUtc });
            entity.HasIndex(document => new { document.Category, document.UploadedAtUtc });
            entity.HasIndex(document => document.Title);
        });

        modelBuilder.Entity<DocumentTag>(entity =>
        {
            entity.HasKey(tag => tag.DocumentTagId);
            entity.Property(tag => tag.Value).IsRequired().HasMaxLength(100);
            entity.HasOne<Document>()
                .WithMany(document => document.Tags)
                .HasForeignKey(tag => tag.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(tag => new { tag.DocumentId, tag.Value }).IsUnique();
            entity.HasIndex(tag => tag.Value);
        });

        modelBuilder.Entity<DocumentActivity>(entity =>
        {
            entity.HasKey(activity => activity.DocumentActivityId);
            entity.Property(activity => activity.DocumentTitleSnapshot).HasMaxLength(255);
            entity.Property(activity => activity.Action).IsRequired().HasMaxLength(50);
            entity.Property(activity => activity.Outcome).IsRequired().HasMaxLength(20);
            entity.Property(activity => activity.TargetSnapshot).HasMaxLength(255);
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(activity => activity.ActorUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(activity => new { activity.DocumentId, activity.OccurredAtUtc });
            entity.HasIndex(activity => new { activity.ActorUserId, activity.OccurredAtUtc });
        });

        modelBuilder.Entity<DocumentShare>(entity =>
        {
            entity.HasKey(share => share.DocumentShareId);
            entity.Property(share => share.RecipientDepartment).HasMaxLength(100);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_DocumentShares_RecipientTarget",
                "(\"RecipientUserId\" IS NOT NULL AND \"RecipientDepartment\" IS NULL) OR (\"RecipientUserId\" IS NULL AND \"RecipientDepartment\" IS NOT NULL)"));
            entity.HasOne<Document>()
                .WithMany()
                .HasForeignKey(share => share.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(share => share.SharedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(share => share.RecipientUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(share => new { share.DocumentId, share.RevokedAtUtc });
            entity.HasIndex(share => new { share.RecipientUserId, share.RevokedAtUtc });
            entity.HasIndex(share => new { share.RecipientDepartment, share.RevokedAtUtc });
        });

        modelBuilder.Entity<DocumentTask>(entity =>
        {
            entity.HasKey(link => new { link.DocumentId, link.TaskId });
            entity.HasOne(link => link.Document)
                .WithMany(document => document.DocumentTasks)
                .HasForeignKey(link => link.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.Task)
                .WithMany(task => task.DocumentTasks)
                .HasForeignKey(link => link.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.LinkedByUser)
                .WithMany()
                .HasForeignKey(link => link.LinkedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(link => link.LinkedByUserId);
        });

        // Seed initial data
        SeedData(modelBuilder);
    }

    private void SeedData(ModelBuilder modelBuilder)
    {
        // Seed an admin user
        modelBuilder.Entity<User>().HasData(
            new User
            {
                UserId = 1,
                Email = "admin@contoso.com",
                DisplayName = "System Administrator",
                Department = "IT",
                JobTitle = "Administrator",
                Role = UserRole.Administrator,
                AvailabilityStatus = AvailabilityStatus.Available,
                CreatedDate = SeedAnchorUtc,
                EmailNotificationsEnabled = true,
                InAppNotificationsEnabled = true
            },
            new User
            {
                UserId = 2,
                Email = "camille.nicole@contoso.com",
                DisplayName = "Camille Nicole",
                Department = "Engineering",
                JobTitle = "Project Manager",
                Role = UserRole.ProjectManager,
                AvailabilityStatus = AvailabilityStatus.Available,
                CreatedDate = SeedAnchorUtc,
                EmailNotificationsEnabled = true,
                InAppNotificationsEnabled = true
            },
            new User
            {
                UserId = 3,
                Email = "floris.kregel@contoso.com",
                DisplayName = "Floris Kregel",
                Department = "Engineering",
                JobTitle = "Team Lead",
                Role = UserRole.TeamLead,
                AvailabilityStatus = AvailabilityStatus.Available,
                CreatedDate = SeedAnchorUtc,
                EmailNotificationsEnabled = true,
                InAppNotificationsEnabled = true
            },
            new User
            {
                UserId = 4,
                Email = "ni.kang@contoso.com",
                DisplayName = "Ni Kang",
                Department = "Engineering",
                JobTitle = "Software Engineer",
                Role = UserRole.Employee,
                AvailabilityStatus = AvailabilityStatus.Available,
                CreatedDate = SeedAnchorUtc,
                EmailNotificationsEnabled = true,
                InAppNotificationsEnabled = true
            }
        );

        // Seed a sample project
        modelBuilder.Entity<Project>().HasData(
            new Project
            {
                ProjectId = 1,
                Name = "ContosoDashboard Development",
                Description = "Internal employee productivity dashboard",
                ProjectManagerId = 2,
                StartDate = SeedAnchorUtc.AddDays(-30),
                TargetCompletionDate = SeedAnchorUtc.AddDays(60),
                Status = ProjectStatus.Active,
                CreatedDate = SeedAnchorUtc.AddDays(-30),
                UpdatedDate = SeedAnchorUtc
            }
        );

        // Seed sample tasks
        modelBuilder.Entity<TaskItem>().HasData(
            new TaskItem
            {
                TaskId = 1,
                Title = "Design database schema",
                Description = "Create entity relationship diagram and database design",
                Priority = TaskPriority.High,
                Status = Models.TaskStatus.Completed,
                DueDate = SeedAnchorUtc.AddDays(-20),
                AssignedUserId = 4,
                CreatedByUserId = 2,
                ProjectId = 1,
                CreatedDate = SeedAnchorUtc.AddDays(-30),
                UpdatedDate = SeedAnchorUtc.AddDays(-20)
            },
            new TaskItem
            {
                TaskId = 2,
                Title = "Implement authentication",
                Description = "Set up Microsoft Entra ID authentication",
                Priority = TaskPriority.Critical,
                Status = Models.TaskStatus.InProgress,
                DueDate = SeedAnchorUtc.AddDays(5),
                AssignedUserId = 4,
                CreatedByUserId = 2,
                ProjectId = 1,
                CreatedDate = SeedAnchorUtc.AddDays(-25),
                UpdatedDate = SeedAnchorUtc
            },
            new TaskItem
            {
                TaskId = 3,
                Title = "Create UI mockups",
                Description = "Design user interface mockups for all main pages",
                Priority = TaskPriority.Medium,
                Status = Models.TaskStatus.NotStarted,
                DueDate = SeedAnchorUtc.AddDays(10),
                AssignedUserId = 4,
                CreatedByUserId = 2,
                ProjectId = 1,
                CreatedDate = SeedAnchorUtc.AddDays(-20),
                UpdatedDate = SeedAnchorUtc.AddDays(-20)
            }
        );

        // Seed project members
        modelBuilder.Entity<ProjectMember>().HasData(
            new ProjectMember
            {
                ProjectMemberId = 1,
                ProjectId = 1,
                UserId = 3,
                Role = "TeamLead",
                AssignedDate = SeedAnchorUtc.AddDays(-30)
            },
            new ProjectMember
            {
                ProjectMemberId = 2,
                ProjectId = 1,
                UserId = 4,
                Role = "Developer",
                AssignedDate = SeedAnchorUtc.AddDays(-30)
            }
        );

        // Seed announcement
        modelBuilder.Entity<Announcement>().HasData(
            new Announcement
            {
                AnnouncementId = 1,
                Title = "Welcome to ContosoDashboard",
                Content = "Welcome to the new ContosoDashboard application. This platform will help you manage your tasks and projects more efficiently.",
                CreatedByUserId = 1,
                PublishDate = SeedAnchorUtc,
                ExpiryDate = SeedAnchorUtc.AddDays(30),
                IsActive = true
            }
        );
    }
}
