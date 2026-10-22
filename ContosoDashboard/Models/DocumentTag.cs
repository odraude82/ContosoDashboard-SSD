using System.ComponentModel.DataAnnotations;

namespace ContosoDashboard.Models;

public class DocumentTag
{
    [Key]
    public int DocumentTagId { get; set; }

    [Required]
    public int DocumentId { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Value { get; set; } = string.Empty;
}