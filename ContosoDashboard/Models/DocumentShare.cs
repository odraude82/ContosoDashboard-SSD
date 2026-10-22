using System.ComponentModel.DataAnnotations;

namespace ContosoDashboard.Models;

public class DocumentShare
{
    [Key]
    public int DocumentShareId { get; set; }

    [Required]
    public int DocumentId { get; set; }

    [Required]
    public int SharedByUserId { get; set; }

    public int? RecipientUserId { get; set; }

    [StringLength(100)]
    public string? RecipientDepartment { get; set; }

    [Required]
    public DateTime SharedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
}