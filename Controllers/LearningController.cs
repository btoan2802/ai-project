using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BtoanAiNet.Data;
using BtoanAiNet.Models;
using BtoanAiNet.Helpers;
using BtoanAiNet.Services;

namespace BtoanAiNet.Controllers
{
    [ApiController]
    [Route("api/learning")]
    public class LearningController : Controller
    {
        private readonly AppDbContext _dbContext;
        private readonly IChatService _chatService;
        private readonly IGeminiService _geminiService;

        public LearningController(AppDbContext dbContext, IChatService chatService, IGeminiService geminiService)
        {
            _dbContext = dbContext;
            _chatService = chatService;
            _geminiService = geminiService;
        }

        protected int CurrentUserId => AuthUserHelper.GetCurrentUserId(HttpContext);

        // 1. POST /api/learning/record-result
        [HttpPost("record-result")]
        public async Task<IActionResult> RecordResult([FromBody] RecordResultRequest? request)
        {
            int userId = CurrentUserId;
            if (userId <= 0)
            {
                return Json(new { ok = true, guest = true, message = "Lưu kết quả tạm thời (Chưa đăng nhập)" });
            }

            if (request == null || request.Items == null || request.Items.Count == 0)
            {
                return Json(new { ok = false, error = "Dữ liệu kết quả không hợp lệ" });
            }

            string subject = string.IsNullOrWhiteSpace(request.Subject) ? "Toán" : request.Subject.Trim();
            string gradeLevel = string.IsNullOrWhiteSpace(request.GradeLevel) ? "THPT" : request.GradeLevel.Trim();

            try
            {
                int count = 0;
                foreach (var item in request.Items)
                {
                    string topic = string.IsNullOrWhiteSpace(item.Topic) ? subject : item.Topic.Trim();
                    if (topic.Length > 120) topic = topic.Substring(0, 120);

                    var stat = await _dbContext.LearningTopicStats.FirstOrDefaultAsync(s =>
                        s.UserId == userId &&
                        s.Subject.ToLower() == subject.ToLower() &&
                        s.TopicName.ToLower() == topic.ToLower());

                    if (stat == null)
                    {
                        stat = new LearningTopicStat
                        {
                            UserId = userId,
                            Subject = subject,
                            GradeLevel = gradeLevel,
                            TopicName = topic,
                            TotalQuestions = 1,
                            CorrectQuestions = item.IsCorrect ? 1 : 0,
                            MasteryPercent = item.IsCorrect ? 100.0 : 0.0,
                            LastPracticedAt = DateTime.UtcNow.ToString("o")
                        };
                        _dbContext.LearningTopicStats.Add(stat);
                    }
                    else
                    {
                        stat.TotalQuestions += 1;
                        if (item.IsCorrect) stat.CorrectQuestions += 1;
                        stat.MasteryPercent = stat.TotalQuestions > 0
                            ? Math.Round((double)stat.CorrectQuestions / stat.TotalQuestions * 100.0, 1)
                            : 0;
                        stat.GradeLevel = gradeLevel;
                        stat.LastPracticedAt = DateTime.UtcNow.ToString("o");
                    }
                    count++;
                }

                await _dbContext.SaveChangesAsync();
                return Json(new { ok = true, updatedCount = count });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // 2. GET /api/learning/radar
        [HttpGet("radar")]
        public async Task<IActionResult> GetRadar([FromQuery] string? subject = null)
        {
            int userId = CurrentUserId;
            if (userId <= 0)
            {
                return Json(new
                {
                    ok = true,
                    summary = new
                    {
                        totalPracticed = 0,
                        totalCorrect = 0,
                        overallAccuracy = 0.0,
                        weakTopicsCount = 0,
                        strongTopicsCount = 0
                    },
                    topics = new List<object>(),
                    weakestTopic = (object?)null
                });
            }

            try
            {
                var query = _dbContext.LearningTopicStats.Where(s => s.UserId == userId);
                if (!string.IsNullOrWhiteSpace(subject) && !subject.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(s => s.Subject.ToLower() == subject.Trim().ToLower());
                }

                var stats = await query.ToListAsync();
                var topics = stats
                    .OrderBy(x => x.MasteryPercent)
                    .ThenByDescending(x => x.TotalQuestions)
                    .ToList();

                int totalPracticed = stats.Sum(x => x.TotalQuestions);
                int totalCorrect = stats.Sum(x => x.CorrectQuestions);
                double overallAccuracy = totalPracticed > 0
                    ? Math.Round((double)totalCorrect / totalPracticed * 100.0, 1)
                    : 0.0;
                int weakTopicsCount = stats.Count(x => x.MasteryPercent < 65.0);
                int strongTopicsCount = stats.Count(x => x.MasteryPercent >= 80.0);
                var weakestTopic = topics.FirstOrDefault(x => x.MasteryPercent < 70.0);

                return Json(new
                {
                    ok = true,
                    summary = new
                    {
                        totalPracticed,
                        totalCorrect,
                        overallAccuracy,
                        weakTopicsCount,
                        strongTopicsCount
                    },
                    topics = topics.Select(t => new
                    {
                        id = t.Id,
                        subject = t.Subject,
                        gradeLevel = t.GradeLevel,
                        topicName = t.TopicName,
                        totalQuestions = t.TotalQuestions,
                        correctQuestions = t.CorrectQuestions,
                        masteryPercent = t.MasteryPercent,
                        lastPracticedAt = t.LastPracticedAt
                    }),
                    weakestTopic = weakestTopic != null ? new
                    {
                        id = weakestTopic.Id,
                        subject = weakestTopic.Subject,
                        gradeLevel = weakestTopic.GradeLevel,
                        topicName = weakestTopic.TopicName,
                        masteryPercent = weakestTopic.MasteryPercent,
                        totalQuestions = weakestTopic.TotalQuestions
                    } : null
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // 3. GET /api/learning/roadmaps
        [HttpGet("roadmaps")]
        public async Task<IActionResult> GetRoadmaps()
        {
            int userId = CurrentUserId;
            if (userId <= 0)
            {
                return Json(new { ok = true, roadmaps = new List<object>() });
            }

            try
            {
                var list = await _dbContext.StudyRoadmaps
                    .Where(r => r.UserId == userId)
                    .OrderByDescending(r => r.UpdatedAt)
                    .ToListAsync();

                var results = list.Select(r =>
                {
                    int daysRemaining = 0;
                    if (DateTime.TryParse(r.ExamDate, out var examDt))
                    {
                        daysRemaining = (int)Math.Max(0, Math.Ceiling((examDt.Date - DateTime.Today).TotalDays));
                    }

                    int totalTasks = 0;
                    int completedTasks = 0;
                    List<RoadmapTaskItem>? tasks = null;
                    try
                    {
                        tasks = JsonSerializer.Deserialize<List<RoadmapTaskItem>>(r.PlanJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (tasks != null)
                        {
                            totalTasks = tasks.Count;
                            completedTasks = tasks.Count(t => t.Completed);
                        }
                    }
                    catch { }

                    int progressPercent = totalTasks > 0 ? (int)Math.Round((double)completedTasks / totalTasks * 100.0) : 0;

                    return new
                    {
                        id = r.Id,
                        title = r.Title,
                        subject = r.Subject,
                        gradeLevel = r.GradeLevel,
                        examDate = r.ExamDate,
                        targetScore = r.TargetScore,
                        dailyMinutes = r.DailyMinutes,
                        sourceDocName = r.SourceDocName,
                        daysRemaining,
                        progressPercent,
                        totalTasks,
                        completedTasks,
                        plan = tasks ?? new List<RoadmapTaskItem>(),
                        createdAt = r.CreatedAt,
                        updatedAt = r.UpdatedAt
                    };
                });

                return Json(new { ok = true, roadmaps = results });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // 4. POST /api/learning/roadmaps/generate
        [HttpPost("roadmaps/generate")]
        public async Task<IActionResult> GenerateRoadmap([FromBody] GenerateRoadmapRequest? request)
        {
            int userId = CurrentUserId;
            if (userId <= 0)
            {
                return Json(new { ok = false, error = "Vui lòng đăng nhập để lưu trữ lộ trình ôn thi cá nhân." });
            }

            if (request == null)
            {
                return Json(new { ok = false, error = "Thông tin tạo lộ trình không hợp lệ." });
            }

            string subject = string.IsNullOrWhiteSpace(request.Subject) ? "Toán" : request.Subject.Trim();
            string gradeLevel = string.IsNullOrWhiteSpace(request.GradeLevel) ? "THPT (Lớp 12)" : request.GradeLevel.Trim();
            string targetScore = string.IsNullOrWhiteSpace(request.TargetScore) ? "8.0+" : request.TargetScore.Trim();
            int dailyMinutes = request.DailyMinutes > 0 ? request.DailyMinutes : 60;

            DateTime targetExamDate = DateTime.Today.AddDays(14);
            if (DateTime.TryParse(request.ExamDate, out var parsedDate) && parsedDate.Date >= DateTime.Today)
            {
                targetExamDate = parsedDate.Date;
            }

            int totalDays = Math.Min(30, Math.Max(1, (int)(targetExamDate - DateTime.Today).TotalDays));
            if (totalDays <= 0) totalDays = 14;

            try
            {
                // Query user's weak topics to prioritize in roadmap
                var weakTopics = await _dbContext.LearningTopicStats
                    .Where(s => s.UserId == userId && s.Subject.ToLower() == subject.ToLower() && s.MasteryPercent < 75.0)
                    .OrderBy(s => s.MasteryPercent)
                    .Select(s => s.TopicName)
                    .Take(5)
                    .ToListAsync();

                var tasks = BuildCurriculumTasks(subject, gradeLevel, targetScore, dailyMinutes, totalDays, weakTopics, request.SyllabusText);

                string title = $"Lộ trình {subject} ({gradeLevel}) — Mục tiêu {targetScore}";
                var roadmap = new StudyRoadmap
                {
                    UserId = userId,
                    Title = title,
                    Subject = subject,
                    GradeLevel = gradeLevel,
                    ExamDate = targetExamDate.ToString("yyyy-MM-dd"),
                    TargetScore = targetScore,
                    DailyMinutes = dailyMinutes,
                    SourceDocName = request.SourceDocName,
                    PlanJson = JsonSerializer.Serialize(tasks),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    UpdatedAt = DateTime.UtcNow.ToString("o")
                };

                _dbContext.StudyRoadmaps.Add(roadmap);
                await _dbContext.SaveChangesAsync();

                return Json(new
                {
                    ok = true,
                    roadmap = new
                    {
                        id = roadmap.Id,
                        title = roadmap.Title,
                        subject = roadmap.Subject,
                        gradeLevel = roadmap.GradeLevel,
                        examDate = roadmap.ExamDate,
                        targetScore = roadmap.TargetScore,
                        dailyMinutes = roadmap.DailyMinutes,
                        sourceDocName = roadmap.SourceDocName,
                        daysRemaining = totalDays,
                        progressPercent = 0,
                        totalTasks = tasks.Count,
                        completedTasks = 0,
                        plan = tasks,
                        createdAt = roadmap.CreatedAt,
                        updatedAt = roadmap.UpdatedAt
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // 5. POST /api/learning/roadmaps/{id}/toggle-task
        [HttpPost("roadmaps/{id}/toggle-task")]
        public async Task<IActionResult> ToggleRoadmapTask(int id, [FromBody] ToggleTaskRequest? request)
        {
            int userId = CurrentUserId;
            if (userId <= 0)
            {
                return Json(new { ok = false, error = "Vui lòng đăng nhập để cập nhật tiến độ." });
            }

            if (request == null || string.IsNullOrWhiteSpace(request.TaskId))
            {
                return Json(new { ok = false, error = "Mã nhiệm vụ không hợp lệ." });
            }

            try
            {
                var roadmap = await _dbContext.StudyRoadmaps.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
                if (roadmap == null)
                {
                    return NotFound(new { ok = false, error = "Không tìm thấy lộ trình ôn thi." });
                }

                var tasks = JsonSerializer.Deserialize<List<RoadmapTaskItem>>(roadmap.PlanJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<RoadmapTaskItem>();

                var targetTask = tasks.FirstOrDefault(t => t.Id == request.TaskId);
                if (targetTask != null)
                {
                    targetTask.Completed = request.Completed;
                }

                int totalTasks = tasks.Count;
                int completedTasks = tasks.Count(t => t.Completed);
                int progressPercent = totalTasks > 0 ? (int)Math.Round((double)completedTasks / totalTasks * 100.0) : 0;

                roadmap.PlanJson = JsonSerializer.Serialize(tasks);
                roadmap.UpdatedAt = DateTime.UtcNow.ToString("o");
                await _dbContext.SaveChangesAsync();

                return Json(new
                {
                    ok = true,
                    progressPercent,
                    completedTasks,
                    totalTasks,
                    taskId = request.TaskId,
                    completed = request.Completed
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // 6. DELETE /api/learning/roadmaps/{id}
        [HttpDelete("roadmaps/{id}")]
        public async Task<IActionResult> DeleteRoadmap(int id)
        {
            int userId = CurrentUserId;
            if (userId <= 0)
            {
                return Json(new { ok = false, error = "Vui lòng đăng nhập để thực hiện thao tác." });
            }

            try
            {
                var roadmap = await _dbContext.StudyRoadmaps.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
                if (roadmap == null)
                {
                    return NotFound(new { ok = false, error = "Không tìm thấy lộ trình cần xóa." });
                }

                _dbContext.StudyRoadmaps.Remove(roadmap);
                await _dbContext.SaveChangesAsync();

                return Json(new { ok = true, deletedId = id });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // Helper: Generate science-backed curriculum milestones
        private List<RoadmapTaskItem> BuildCurriculumTasks(
            string subject,
            string gradeLevel,
            string targetScore,
            int dailyMinutes,
            int totalDays,
            List<string> weakTopics,
            string? syllabusText)
        {
            var taskList = new List<RoadmapTaskItem>();
            string subLower = subject.ToLowerInvariant();

            // Curated syllabus per subject for high-impact exam prep
            string[] topics;
            if (subLower.Contains("toán") || subLower.Contains("math"))
            {
                topics = new[]
                {
                    "Ứng dụng đạo hàm khảo sát hàm số & Tính đơn điệu",
                    "Cực trị hàm số và Giá trị lớn nhất - nhỏ nhất (GTLN-GTNN)",
                    "Tiệm cận đứng, tiệm cận ngang và tiệm cận xiên của đồ thị",
                    "Hàm số mũ, hàm số lũy thừa & Phương trình logarit",
                    "Nguyên hàm và Các phương pháp tính tích phân cơ bản",
                    "Ứng dụng tích phân tính diện tích hình phẳng & Thể tích vật thể",
                    "Phương pháp tọa độ trong không gian Oxyz: Vectơ & Tọa độ điểm",
                    "Phương trình mặt phẳng và Vị trí tương đối trong không gian",
                    "Phương trình đường thẳng & Mặt cầu trong không gian Oxyz",
                    "Góc và khoảng cách trong hình học không gian cổ điển",
                    "Thống kê mẫu số liệu ghép nhóm: Phương sai & Độ lệch chuẩn",
                    "Xác suất có điều kiện, Công thức xác suất toàn phần & Định lý Bayes",
                    "Bài toán tối ưu thực tế (kinh tế, chi phí, vật lý chuyển động)",
                    "Luyện đề chuẩn THPTQG 2025–2026 Phần I, II, III bấm giờ",
                    "Tổng ôn các bẫy điểm liệt và Chiến thuật phân bổ thời gian thi"
                };
            }
            else if (subLower.Contains("lý") || subLower.Contains("vật lý") || subLower.Contains("physic"))
            {
                topics = new[]
                {
                    "Dao động điều hòa: Phương trình x, v, a và Vòng tròn lượng giác",
                    "Con lắc lò xo: Chu kỳ, Năng lượng & Lực đàn hồi/hồi phục",
                    "Con lắc đơn và Dao động tắt dần, Dao động cưỡng bức",
                    "Sóng cơ & Sự truyền sóng: Bước sóng, Phương trình sóng",
                    "Giao thoa sóng cơ: Vị trí cực đại, cực tiểu và Số điểm dao động",
                    "Sóng dừng trên sợi dây & Đặc trưng sinh lý của âm thanh",
                    "Đại cương dòng điện xoay chiều & Các mạch điện R, L, C nối tiếp",
                    "Hiện tượng cộng hưởng điện & Công suất tiêu thụ của mạch RLC",
                    "Máy phát điện xoay chiều, Động cơ không đồng bộ & Máy biến áp",
                    "Mạch dao động LC: Năng lượng điện từ và Sóng điện từ",
                    "Tán sắc ánh sáng, Giao thoa ánh sáng đơn sắc và đa sắc qua khe Y-âng",
                    "Hiện tượng quang điện ngoài, Thuyết lượng tử & Mẫu nguyên tử Bo",
                    "Cấu tạo hạt nhân, Năng lượng liên kết & Phóng xạ hạt nhân",
                    "Luyện đề chuẩn Vật lý THPTQG bấm giờ kiểm soát tốc độ",
                    "Tổng kết công thức cốt lõi và Rà soát toàn bộ các câu hỏi lý thuyết"
                };
            }
            else if (subLower.Contains("hóa") || subLower.Contains("chem"))
            {
                topics = new[]
                {
                    "Este - Lipit: Danh pháp, Đồng phân, Phản ứng xà phòng hóa",
                    "Cacbohidrat: Glucozơ, Saccarozơ, Tinh bột và Xenlulozơ",
                    "Amin - Amino axit - Peptit & Protein: Tính chất lưỡng tính",
                    "Polime và Vật liệu polime: Cao su, Tơ nhân tạo, Chất dẻo",
                    "Đại cương kim loại: Dãy điện hóa, Ăn mòn kim loại & Điều chế",
                    "Kim loại kiềm, Kiềm thổ và Hợp chất của Nhôm",
                    "Sắt, Crom và Các hợp chất quan trọng của Sắt (Fe²⁺, Fe³⁺)",
                    "Phân biệt một số chất vô cơ và Hóa học với môi trường",
                    "Luyện tập bài toán đồ thị và Tối ưu hóa phản ứng định lượng",
                    "Luyện đề thi chuẩn Hóa học THPTQG bấm giờ 50 phút"
                };
            }
            else if (subLower.Contains("anh") || subLower.Contains("tiếng anh") || subLower.Contains("english"))
            {
                topics = new[]
                {
                    "Ngữ âm: Trọng âm 2-3 âm tiết và Phát âm đuôi -s/es, -ed",
                    "Hệ thống 12 thì cơ bản và Sự hòa hợp giữa Chủ ngữ - Động từ",
                    "Mệnh đề quan hệ (Relative Clauses) & Rút gọn mệnh đề quan hệ",
                    "Câu điều kiện (Conditionals 0, 1, 2, 3, hỗn hợp) và Đảo ngữ",
                    "Câu bị động đặc biệt (Passive Voice) & Thể sai khiến",
                    "Câu gián tiếp và Động từ tường thuật đặc biệt",
                    "Cụm động từ phổ biến (Phrasal Verbs) theo chủ điểm kỳ thi",
                    "Cụm từ cố định (Collocations & Idioms) hay gặp trong đề thi",
                    "Kỹ năng Đọc hiểu (Reading Comprehension): Skimming & Scanning",
                    "Dạng bài Đọc điền từ vào đoạn văn (Cloze Test) & Từ chỉ lượng",
                    "Dạng bài Tìm từ đồng nghĩa - trái nghĩa trong ngữ cảnh",
                    "Dạng bài Chức năng giao tiếp và Tìm lỗi sai ngữ pháp",
                    "Dạng bài Kết hợp câu và Biến đổi câu cùng nghĩa",
                    "Luyện đề thi thử chuẩn Tiếng Anh THPTQG bấm giờ 60 phút",
                    "Tổng rà soát danh sách 200 từ vựng và Cụm từ dễ mất điểm"
                };
            }
            else
            {
                topics = new[]
                {
                    $"Tổng quan cấu trúc và Đề cương trọng tâm môn {subject}",
                    $"Khái niệm nền tảng và Lý thuyết then chốt chuyên đề 1",
                    $"Bài tập vận dụng và Phương pháp giải dạng toán chuyên đề 1",
                    $"Chuyên đề 2: Phân tích bản chất và Các công thức cốt lõi",
                    $"Luyện tập trắc nghiệm củng cố kiến thức chuyên đề 2",
                    $"Chuyên đề 3: Các ứng dụng thực tế và Bài toán liên môn",
                    $"Hệ thống hóa sơ đồ tư duy và Bảng so sánh các hiện tượng",
                    $"Luyện đề trắc nghiệm tổng hợp giai đoạn 1",
                    $"Chuyên đề 4: Nâng cao tư duy và Giải quyết câu hỏi phân hóa",
                    $"Luyện tập bộ Flashcards phản xạ nhanh toàn bộ thuật ngữ",
                    $"Thực hành đề thi thử bấm giờ theo cấu trúc chuẩn",
                    $"Phân tích lỗi sai, Vá lỗ hổng kiến thức và Ôn tập nước rút"
                };
            }

            // Methods rotation
            string[] methods = new[] { "Giảng bài", "Trắc nghiệm", "Flashcards", "Thi thử THPTQG" };

            // Weave weak topics at the very beginning of the roadmap
            var combinedTopics = new List<string>();
            foreach (var wt in weakTopics)
            {
                if (!string.IsNullOrWhiteSpace(wt)) combinedTopics.Add($"[Lỗ hổng cần vá]: {wt}");
            }
            foreach (var t in topics)
            {
                if (!combinedTopics.Contains(t)) combinedTopics.Add(t);
            }

            for (int i = 0; i < totalDays; i++)
            {
                int dayNum = i + 1;
                string topicTitle = combinedTopics[i % combinedTopics.Count];
                string method = methods[i % methods.Length];
                if (dayNum == totalDays) method = "Thi thử THPTQG";

                var targetDate = DateTime.Today.AddDays(i);

                taskList.Add(new RoadmapTaskItem
                {
                    Id = $"task_{dayNum}",
                    Day = dayNum,
                    Date = targetDate.ToString("yyyy-MM-dd"),
                    Title = topicTitle,
                    Focus = $"Tập trung nắm chắc bản chất, giải quyết từ 10–20 câu bài tập và kiểm tra độ hiểu sâu.",
                    Method = method,
                    EstimatedMinutes = dailyMinutes,
                    Completed = false
                });
            }

            return taskList;
        }
    }

    public class RecordResultRequest
    {
        public string Subject { get; set; } = "Toán";
        public string GradeLevel { get; set; } = "THPT";
        public List<QuestionResultItem> Items { get; set; } = new();
    }

    public class QuestionResultItem
    {
        public string Topic { get; set; } = "";
        public bool IsCorrect { get; set; }
    }

    public class GenerateRoadmapRequest
    {
        public string Subject { get; set; } = "Toán";
        public string GradeLevel { get; set; } = "THPT";
        public string ExamDate { get; set; } = "";
        public string TargetScore { get; set; } = "8.0+";
        public int DailyMinutes { get; set; } = 60;
        public string? SyllabusText { get; set; }
        public string? SourceDocName { get; set; }
    }

    public class ToggleTaskRequest
    {
        public string TaskId { get; set; } = "";
        public bool Completed { get; set; }
    }

    public class RoadmapTaskItem
    {
        public string Id { get; set; } = "";
        public int Day { get; set; }
        public string Date { get; set; } = "";
        public string Title { get; set; } = "";
        public string Focus { get; set; } = "";
        public string Method { get; set; } = "Giảng bài";
        public int EstimatedMinutes { get; set; } = 60;
        public bool Completed { get; set; }
    }
}
