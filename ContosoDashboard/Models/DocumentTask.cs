using System.ComponentModel.DataAnnotations;

namespace ContosoDashboard.Models;

public class DocumentTask
{
    public int DocumentId { get; set; }

    public int TaskId { get; set; }

    [Required]
    public int LinkedByUserId { get; set; }

    [Required]
    public DateTime LinkedAtUtc { get; set; }

    public virtual Document Document { get; set; } = null!;

    public virtual TaskItem Task { get; set; } = null!;

    public virtual User LinkedByUser { get; set; } = null!;
}