using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BtoanAiNet.Models
{
    [Table("LearningTopicStats")]
    public class LearningTopicStat
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [Required]
        public string Subject { get; set; } = "";

        [Required]
        public string GradeLevel { get; set; } = "";

        [Required]
        public string TopicName { get; set; } = "";

        public int TotalQuestions { get; set; } = 0;

        public int CorrectQuestions { get; set; } = 0;

        public double MasteryPercent { get; set; } = 0;

        [Required]
        public string LastPracticedAt { get; set; } = DateTime.UtcNow.ToString("o");
    }
}
