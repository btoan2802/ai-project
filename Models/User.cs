using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BtoanAiNet.Models
{
    [Table("users")]
    public class User
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("username")]
        [StringLength(50)]
        public string? Username { get; set; }

        [Column("full_name")]
        [StringLength(255)]
        public string? FullName { get; set; }

        [Column("password")]
        [StringLength(255)]
        public string? Password { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("dob")]
        public DateOnly? Dob { get; set; }

        [Column("email")]
        [StringLength(255)]
        public string? Email { get; set; }

        [Column("google_id")]
        [StringLength(255)]
        public string? GoogleId { get; set; }

        [Column("avatar")]
        public string? Avatar { get; set; }

        [Column("face_enabled")]
        public int? FaceEnabled { get; set; }

        [Column("face_image_url")]
        public string? FaceImageUrl { get; set; }

        [Column("face_descriptor")]
        public string? FaceDescriptor { get; set; }

        [Column("face_updated_at")]
        public DateTime? FaceUpdatedAt { get; set; }

        [Column("bio")]
        [StringLength(500)]
        public string? Bio { get; set; }

        [Column("default_model")]
        [StringLength(50)]
        public string? DefaultModel { get; set; } = "Btoan 2.5 Flash";

        [Column("custom_instructions")]
        public string? CustomInstructions { get; set; }

        [Column("temperature")]
        public double? Temperature { get; set; } = 0.7;

        [Column("long_term_memory")]
        public int? LongTermMemory { get; set; } = 1;

        [Column("rag_enabled")]
        public int? RagEnabled { get; set; } = 1;

        [Column("theme")]
        [StringLength(20)]
        public string? Theme { get; set; } = "dark";

        [Column("chat_font_size")]
        [StringLength(20)]
        public string? ChatFontSize { get; set; } = "15px";

        [Column("tts_speed")]
        public double? TtsSpeed { get; set; } = 1.0;
    }
}
