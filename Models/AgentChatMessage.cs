using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BtoanAiNet.Models
{
    [Table("AgentChatMessages")]
    public class AgentChatMessage
    {
        [Key]
        [Column("Id")]
        public int Id { get; set; }

        [Column("UserId")]
        public int UserId { get; set; }

        [Required]
        [MaxLength(100)]
        [Column("ProjectName")]
        public string ProjectName { get; set; } = string.Empty;

        [Required]
        [MaxLength(20)]
        [Column("Role")]
        public string Role { get; set; } = "user"; // user, assistant, system

        [Column("Content")]
        public string Content { get; set; } = string.Empty;

        [Column("ThoughtText")]
        public string? ThoughtText { get; set; }

        [Column("ThoughtSeconds")]
        public int ThoughtSeconds { get; set; } = 0;

        [Column("PlanJson")]
        public string? PlanJson { get; set; }

        [Column("FilePillsJson")]
        public string? FilePillsJson { get; set; }

        [Column("ToolActionsJson")]
        public string? ToolActionsJson { get; set; }

        [Column("CheckpointId")]
        public string? CheckpointId { get; set; }

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
