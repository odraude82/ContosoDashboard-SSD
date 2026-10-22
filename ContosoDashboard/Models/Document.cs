using System.ComponentModel.DataAnnotations;
using ContosoDashboard.Options;

namespace ContosoDashboard.Models;

public class Document
{
    [Key]
    public int DocumentId { get; set; }

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(50)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    [StringLength(1024)]
    public string RelativeStoragePath { get; set; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string ContentType { get; set; } = string.Empty;

    [Range(1, DocumentStorageOptions.MaximumAllowedFileSizeBytes)]
    public long FileSizeBytes { get; set; }

    [Required]
    public int UploadedByUserId { get; set; }

    public virtual User UploadedByUser { get; set; } = null!;

    [Required]
    public DateTime UploadedAtUtc { get; set; }

    public int? ProjectId { get; set; }

    [Required]
    public DateTime MalwareScannedAtUtc { get; set; }

    [StringLength(255)]
    public string? ScannerVersion { get; set; }

    public virtual Project? Project { get; set; }
    public virtual ICollection<DocumentTag> Tags { get; set; } = new List<DocumentTag>();
    public virtual ICollection<DocumentTask> DocumentTasks { get; set; } = new List<DocumentTask>();
}