using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BtoanAiNet.Models
{
    [Table("agent_chat_messages")]
    public class AgentChatMessage
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Required]
        [MaxLength(100)]
        [Column("project_name")]
        public string ProjectName { get; set; } = string.Empty;

        [Required]
        [MaxLength(20)]
        [Column("role")]
        public string Role { get; set; } = "user"; // user, assistant, system

        [Required]
        [Column("content")]
        public string Content { get; set; } = string.Empty;

        [Column("plan_json")]
        public string? PlanJson { get; set; }

        [Column("tool_actions_json")]
        public string? ToolActionsJson { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
