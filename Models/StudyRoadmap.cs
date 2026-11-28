using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BtoanAiNet.Models
{
    [Table("StudyRoadmaps")]
    public class StudyRoadmap
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [Required]
        public string Title { get; set; } = "";

        [Required]
        public string Subject { get; set; } = "";

        [Required]
        public string GradeLevel { get; set; } = "";

        [Required]
        public string ExamDate { get; set; } = "";

        [Required]
        public string TargetScore { get; set; } = "";

        public int DailyMinutes { get; set; } = 60;

        public string? SourceDocName { get; set; }

        [Required]
        public string PlanJson { get; set; } = "[]";

        [Required]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [Required]
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    }
}
