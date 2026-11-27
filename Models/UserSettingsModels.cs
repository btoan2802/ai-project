using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BtoanAiNet.Models
{
    [Table("UserPreferences")]
    public class UserPreference
    {
        [Key]
        public int Id { get; set; }
        public int UserId { get; set; }
        public string? CustomInstructions { get; set; }
        public string? SavedMemories { get; set; }
        public string Theme { get; set; } = "dark";
        public string DefaultLocation { get; set; } = "TP. Hồ Chí Minh, Việt Nam";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    [Table("UserGems")]
    public class UserGem
    {
        [Key]
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "💎";
        public string? Description { get; set; }
        public string SystemPrompt { get; set; } = "";
        public int DefaultDocId { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    [Table("UserFeedbacks")]
    public class UserFeedback
    {
        [Key]
        public int Id { get; set; }
        public int? UserId { get; set; }
        public string? UserName { get; set; }
        public string? UserEmail { get; set; }
        public string? Category { get; set; }
        public string Message { get; set; } = "";
        public int Rating { get; set; } = 5;
        public bool IsResolved { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class FeedbackRequestDto
    {
        public string? Category { get; set; }
        public int Rating { get; set; } = 5;
        public string? Comment { get; set; }
        public string? Message { get; set; }
        public string? Content { get; set; }
    }

    [Table("SharedLinks")]
    public class SharedLink
    {
        [Key]
        public int Id { get; set; }
        public int UserId { get; set; }
        public int ConversationId { get; set; }
        public string ShareToken { get; set; } = "";
        public string? Title { get; set; }
        public bool IsRevoked { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
