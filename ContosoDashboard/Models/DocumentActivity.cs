using System.ComponentModel.DataAnnotations;

namespace ContosoDashboard.Models;

public class DocumentActivity
{
    [Key]
    public int DocumentActivityId { get; set; }

    public int? DocumentId { get; set; }

    [StringLength(255)]
    public string? DocumentTitleSnapshot { get; set; }

    [Required]
    public int ActorUserId { get; set; }

    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string Action { get; set; } = string.Empty;

    [Required]
    [StringLength(20, MinimumLength = 1)]
    public string Outcome { get; set; } = string.Empty;

    [Required]
    public DateTime OccurredAtUtc { get; set; }

    [StringLength(255)]
    public string? TargetSnapshot { get; set; }
}