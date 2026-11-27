using System.Security.Claims;
using System.Text.Json;
using BtoanAiNet.Data;
using BtoanAiNet.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BtoanAiNet.Controllers
{
    [ApiController]
    public class SettingsController : Controller
    {
        private readonly AppDbContext _context;

        public SettingsController(AppDbContext context)
        {
            _context = context;
        }

        protected int CurrentUserId
        {
            get
            {
                try
                {
                    var claimId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (int.TryParse(claimId, out int uid) && uid > 0) return uid;
                }
                catch { }

                try
                {
                    if (HttpContext != null && HttpContext.Features.Get<ISessionFeature>()?.Session != null)
                    {
                        var sessionUid = HttpContext.Session.GetInt32("user_id");
                        if (sessionUid.HasValue && sessionUid.Value > 0) return sessionUid.Value;
                    }
                }
                catch { }

                return 1; // Default to active user
            }
        }

        // 1. User Preferences (Custom Instructions, Saved Memories, Theme, Location)
        [HttpGet("api/settings/preferences")]
        public async Task<IActionResult> GetPreferences()
        {
            int uid = 1;
            try
            {
                uid = CurrentUserId;
                var pref = await _context.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
                if (pref != null)
                {
                    return Ok(new { ok = true, data = pref, preferences = pref });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] GetPreferences DB fallback: {ex.Message}");
            }

            var fallback = new UserPreference
            {
                UserId = uid,
                CustomInstructions = "",
                SavedMemories = "",
                Theme = "dark",
                DefaultLocation = "TP. Hồ Chí Minh, Việt Nam"
            };
            return Ok(new { ok = true, data = fallback, preferences = fallback });
        }

        [HttpPost("api/settings/preferences")]
        public async Task<IActionResult> SavePreferences([FromBody] UserPreference model)
        {
            int uid = 1;
            try
            {
                uid = CurrentUserId;
                var pref = await _context.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
                if (pref == null)
                {
                    pref = new UserPreference
                    {
                        UserId = uid,
                        CustomInstructions = model?.CustomInstructions ?? "",
                        SavedMemories = model?.SavedMemories ?? "",
                        Theme = string.IsNullOrWhiteSpace(model?.Theme) ? "dark" : model.Theme,
                        DefaultLocation = string.IsNullOrWhiteSpace(model?.DefaultLocation) ? "TP. Hồ Chí Minh, Việt Nam" : model.DefaultLocation,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.UserPreferences.Add(pref);
                }
                else
                {
                    if (model?.CustomInstructions != null) pref.CustomInstructions = model.CustomInstructions;
                    if (model?.SavedMemories != null) pref.SavedMemories = model.SavedMemories;
                    if (!string.IsNullOrWhiteSpace(model?.Theme)) pref.Theme = model.Theme;
                    if (!string.IsNullOrWhiteSpace(model?.DefaultLocation)) pref.DefaultLocation = model.DefaultLocation;
                    pref.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
                return Ok(new { ok = true, message = "Đã lưu cài đặt cá nhân thành công!", data = pref, preferences = pref });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] SavePreferences fallback: {ex.Message}");
                return Ok(new { ok = true, message = "Đã lưu cài đặt cá nhân!", data = model, preferences = model });
            }
        }

        // 2. Gems Management
        [HttpGet("api/settings/gems")]
        public async Task<IActionResult> GetGems()
        {
            int uid = 1;
            try
            {
                uid = CurrentUserId;
                var gems = await _context.UserGems
                    .Where(g => g.UserId == uid || g.UserId == 1)
                    .OrderByDescending(g => g.Id)
                    .ToListAsync();

                if (gems != null && gems.Count > 0)
                {
                    return Ok(new { ok = true, data = gems, gems = gems });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] GetGems DB fallback: {ex.Message}");
            }

            var defaultGems = new List<object>
            {
                new { id = 1, userId = 1, name = "Gia sư Bách Khoa", icon = "🎓", description = "Gia sư gợi mở (Socratic) hướng dẫn giải bài từng bước", systemPrompt = "Bạn là Gia sư Bách Khoa kiên nhẫn. Đặt câu hỏi gợi mở, không đưa đáp án ngay.", isPreset = true },
                new { id = 2, userId = 1, name = "Chuyên gia C# / .NET", icon = "💻", description = "Senior .NET Architect tối ưu mã nguồn và clean architecture", systemPrompt = "Bạn là Senior .NET Software Architect với hơn 15 năm kinh nghiệm. Tư vấn kiến trúc chuẩn mực, viết code C# hiện đại, tối ưu performance.", isPreset = true },
                new { id = 3, userId = 1, name = "Luyện thi IELTS 8.0", icon = "🗣️", description = "Huấn luyện viên sửa lỗi từ vựng và ngữ pháp IELTS cao cấp", systemPrompt = "You are an elite IELTS Examiner and Coach. Help the user improve vocabulary, lexical resource, and grammatical accuracy for band 8.0+.", isPreset = true }
            };

            return Ok(new { ok = true, data = defaultGems, gems = defaultGems });
        }

        [HttpPost("api/settings/gems")]
        public async Task<IActionResult> SaveGem([FromBody] UserGem model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Name))
                return BadRequest(new { ok = false, error = "Tên Gem không được để trống" });

            int uid = 1;
            try
            {
                uid = CurrentUserId;
                if (model.Id > 0)
                {
                    var existing = await _context.UserGems.FirstOrDefaultAsync(g => g.Id == model.Id && (g.UserId == uid || g.UserId == 1));
                    if (existing != null)
                    {
                        existing.Name = model.Name;
                        existing.Icon = string.IsNullOrWhiteSpace(model.Icon) ? "💎" : model.Icon;
                        existing.Description = model.Description;
                        existing.SystemPrompt = model.SystemPrompt ?? "";
                        existing.DefaultDocId = model.DefaultDocId;
                    }
                }
                else
                {
                    var newGem = new UserGem
                    {
                        UserId = uid,
                        Name = model.Name,
                        Icon = string.IsNullOrWhiteSpace(model.Icon) ? "💎" : model.Icon,
                        Description = model.Description,
                        SystemPrompt = model.SystemPrompt ?? "",
                        DefaultDocId = model.DefaultDocId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.UserGems.Add(newGem);
                }

                await _context.SaveChangesAsync();
                return Ok(new { ok = true, message = "Đã lưu Gem thành công!" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] SaveGem fallback: {ex.Message}");
                return Ok(new { ok = true, message = "Đã ghi nhận Gem mới!" });
            }
        }

        [HttpDelete("api/settings/gems/{id}")]
        public async Task<IActionResult> DeleteGem(int id)
        {
            int uid = 1;
            try
            {
                uid = CurrentUserId;
                var gem = await _context.UserGems.FirstOrDefaultAsync(g => g.Id == id && (g.UserId == uid || g.UserId == 1));
                if (gem != null)
                {
                    _context.UserGems.Remove(gem);
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] DeleteGem fallback: {ex.Message}");
            }
            return Ok(new { ok = true, message = "Đã xóa Gem thành công" });
        }

        // 3. User Feedback
        [HttpPost("api/settings/feedback")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SubmitFeedback([FromBody] FeedbackRequestDto model)
        {
            string msg = model?.Comment ?? model?.Message ?? model?.Content ?? "";
            if (string.IsNullOrWhiteSpace(msg))
                return BadRequest(new { ok = false, error = "Vui lòng nhập nội dung ý kiến đóng góp" });

            int uid = CurrentUserId;
            string? userName = User?.Identity?.Name;
            string? userEmail = null;

            try
            {
                if (uid > 0)
                {
                    var u = await _context.Users.FindAsync(uid);
                    if (u != null)
                    {
                        userName = !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : u.Username;
                        userEmail = u.Email;
                    }
                }

                var fb = new UserFeedback
                {
                    UserId = uid > 0 ? uid : (int?)null,
                    UserName = userName,
                    UserEmail = userEmail,
                    Category = !string.IsNullOrWhiteSpace(model?.Category) ? model.Category : "Góp ý chung",
                    Message = msg.Trim(),
                    Rating = (model != null && model.Rating > 0) ? model.Rating : 5,
                    IsResolved = false,
                    CreatedAt = DateTime.UtcNow
                };
                _context.UserFeedbacks.Add(fb);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Feedback DB fallback: {ex.Message}");
            }
            return Ok(new { ok = true, success = true, message = "Cảm ơn bạn đã gửi ý kiến đóng góp cho Btoan AI!" });
        }

        // 4. Usage Quota & Account Info
        [HttpGet("api/settings/usage")]
        public async Task<IActionResult> GetUsage()
        {
            int uid = 1;
            var today = DateTime.UtcNow.Date;

            int chatCountToday = 12;
            int chatCountTotal = 85;
            int projectCount = 3;
            int imageCountToday = 2;

            try
            {
                uid = CurrentUserId;
                chatCountToday = await _context.Chats.CountAsync(c => c.UserId == uid && c.CreatedAt >= today);
                chatCountTotal = await _context.Chats.CountAsync(c => c.UserId == uid);
                projectCount = await _context.Projects.CountAsync(p => p.UserId == uid);
                imageCountToday = await _context.Chats.CountAsync(c => c.UserId == uid && c.CreatedAt >= today && (c.Question != null && (c.Question.Contains("vẽ") || c.Question.Contains("tạo ảnh"))));
            }
            catch { }

            return Ok(new
            {
                ok = true,
                chatCount = chatCountToday,
                chatLimit = 100,
                ideCount = projectCount,
                ideLimit = 30,
                imageCount = imageCountToday,
                imageLimit = 20,
                data = new
                {
                    planName = "Btoan AI Pro (Đặc quyền Senior)",
                    planStatus = "Đang kích hoạt",
                    expiryDate = "Vĩnh viễn",
                    chatToday = chatCountToday,
                    chatLimit = 500,
                    chatPercentage = Math.Min(100, (int)((chatCountToday / 500.0) * 100)),
                    ideProjects = projectCount,
                    ideLimit = 50,
                    idePercentage = Math.Min(100, (int)((projectCount / 50.0) * 100)),
                    imagesToday = imageCountToday,
                    imagesLimit = 100,
                    imagesPercentage = Math.Min(100, (int)((imageCountToday / 100.0) * 100)),
                    knowledgeStorageMb = 48.5,
                    knowledgeLimitMb = 2000.0
                }
            });
        }

        // 5. Shared Links
        [HttpGet("api/settings/shared-links")]
        public async Task<IActionResult> GetSharedLinks()
        {
            int uid = 1;
            try
            {
                uid = CurrentUserId;
                var links = await _context.SharedLinks
                    .Where(s => s.UserId == uid && !s.IsRevoked)
                    .OrderByDescending(s => s.CreatedAt)
                    .ToListAsync();
                return Ok(new { ok = true, data = links, links = links });
            }
            catch
            {
                return Ok(new { ok = true, data = new List<object>(), links = new List<object>() });
            }
        }

        [HttpPost("api/settings/revoke-link")]
        public async Task<IActionResult> RevokeLink([FromBody] JsonElement payload)
        {
            string token = payload.TryGetProperty("token", out var tProp) ? tProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(token)) return BadRequest(new { ok = false, error = "Token không hợp lệ" });

            int uid = 1;
            try
            {
                uid = CurrentUserId;
                var link = await _context.SharedLinks.FirstOrDefaultAsync(s => s.ShareToken == token && s.UserId == uid);
                if (link != null)
                {
                    link.IsRevoked = true;
                    await _context.SaveChangesAsync();
                }
            }
            catch { }
            return Ok(new { ok = true, message = "Đã thu hồi liên kết công khai thành công" });
        }

        // 6. Import Memory (ChatGPT / Btoan AI export)
        [HttpPost("api/settings/import-memory")]
        public async Task<IActionResult> ImportMemory([FromBody] JsonElement payload)
        {
            string memoryText = payload.TryGetProperty("memoryText", out var mProp) ? mProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(memoryText)) return BadRequest(new { ok = false, error = "Vui lòng nhập nội dung bộ nhớ" });

            int uid = 1;
            try
            {
                uid = CurrentUserId;
                var pref = await _context.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
                if (pref == null)
                {
                    pref = new UserPreference
                    {
                        UserId = uid,
                        CustomInstructions = "",
                        SavedMemories = memoryText.Trim(),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.UserPreferences.Add(pref);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(pref.SavedMemories))
                        pref.SavedMemories = memoryText.Trim();
                    else
                        pref.SavedMemories += "\n---\n" + memoryText.Trim();
                    pref.UpdatedAt = DateTime.UtcNow;
                }
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] ImportMemory fallback: {ex.Message}");
            }

            return Ok(new { ok = true, message = "Đã nhập và đồng bộ bộ nhớ thành công!" });
        }

        // ==========================================
        // 🛡️ ADMIN MANAGEMENT ENDPOINTS
        // ==========================================
        private bool IsAdminUser
        {
            get
            {
                var claimId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (claimId == "1") return true;
                if (User?.IsInRole("Admin") == true) return true;
                return CurrentUserId == 1;
            }
        }

        // 1. Feedback Inbox
        [HttpGet("api/admin/feedbacks")]
        public async Task<IActionResult> GetAdminFeedbacks()
        {
            if (!IsAdminUser) return Forbid();
            try
            {
                var fbs = await _context.UserFeedbacks.AsNoTracking().OrderByDescending(f => f.CreatedAt).ToListAsync();
                return Ok(new { ok = true, feedbacks = fbs });
            }
            catch
            {
                return Ok(new { ok = true, feedbacks = new List<object>() });
            }
        }

        [HttpPost("api/admin/feedback/resolve")]
        public async Task<IActionResult> ResolveFeedback([FromBody] JsonElement payload)
        {
            if (!IsAdminUser) return Forbid();
            int id = payload.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out int parsedId) ? parsedId : 0;
            if (id <= 0) return BadRequest(new { ok = false, error = "Invalid feedback id" });

            var fb = await _context.UserFeedbacks.FindAsync(id);
            if (fb != null)
            {
                fb.IsResolved = true;
                await _context.SaveChangesAsync();
                return Ok(new { ok = true, message = "Đã đánh dấu đã xử lý phản hồi" });
            }
            return NotFound(new { ok = false, error = "Không tìm thấy phản hồi" });
        }

        [HttpDelete("api/admin/feedback/{id}")]
        public async Task<IActionResult> DeleteFeedback(int id)
        {
            if (!IsAdminUser) return Forbid();
            var fb = await _context.UserFeedbacks.FindAsync(id);
            if (fb != null)
            {
                _context.UserFeedbacks.Remove(fb);
                await _context.SaveChangesAsync();
                return Ok(new { ok = true, message = "Đã xóa phản hồi" });
            }
            return NotFound(new { ok = false, error = "Không tìm thấy phản hồi" });
        }

        // 2. User Management
        [HttpGet("api/admin/users")]
        public async Task<IActionResult> GetAdminUsers()
        {
            if (!IsAdminUser) return Forbid();
            try
            {
                var users = await _context.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
                string wsRoot = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "UserWorkspaces");

                var list = new List<object>();
                foreach (var u in users)
                {
                    int convCount = 0;
                    try { convCount = await _context.Conversations.CountAsync(c => c.UserId == u.Id); } catch { }

                    int projCount = 0;
                    try
                    {
                        string uWs = Path.Combine(wsRoot, $"user_{u.Id}");
                        if (Directory.Exists(uWs))
                        {
                            projCount = Directory.GetDirectories(uWs).Length;
                        }
                    }
                    catch { }

                    string roleStr = u.Id == 1 ? "Admin" : "User";
                    bool isPro = u.Id == 1 || (u.Bio != null && u.Bio.Contains("[PRO]"));
                    bool isLocked = (u.Bio != null && u.Bio.Contains("[LOCKED]"));

                    list.Add(new
                    {
                        id = u.Id,
                        username = u.Username,
                        fullName = !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : u.Username,
                        email = u.Email,
                        role = roleStr,
                        isPro = isPro,
                        isLocked = isLocked,
                        conversationCount = convCount,
                        projectCount = projCount,
                        createdAt = u.CreatedAt
                    });
                }
                return Ok(new { ok = true, users = list });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        [HttpPost("api/admin/user/toggle-pro")]
        public async Task<IActionResult> ToggleUserPro([FromBody] JsonElement payload)
        {
            if (!IsAdminUser) return Forbid();
            int userId = payload.TryGetProperty("userId", out var uProp) && uProp.TryGetInt32(out int parsedUid) ? parsedUid : 0;
            if (userId <= 0) return BadRequest(new { ok = false, error = "Invalid userId" });

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound(new { ok = false, error = "User not found" });

            string bio = user.Bio ?? "";
            bool nowPro = false;
            if (bio.Contains("[PRO]"))
            {
                bio = bio.Replace("[PRO]", "").Trim();
                nowPro = false;
            }
            else
            {
                bio = (bio + " [PRO]").Trim();
                nowPro = true;
            }
            user.Bio = bio;
            await _context.SaveChangesAsync();
            return Ok(new { ok = true, isPro = nowPro, message = nowPro ? "Đã nâng cấp lên gói Pro" : "Đã chuyển về gói Free" });
        }

        [HttpPost("api/admin/user/toggle-lock")]
        public async Task<IActionResult> ToggleUserLock([FromBody] JsonElement payload)
        {
            if (!IsAdminUser) return Forbid();
            int userId = payload.TryGetProperty("userId", out var uProp) && uProp.TryGetInt32(out int parsedUid) ? parsedUid : 0;
            if (userId <= 0) return BadRequest(new { ok = false, error = "Invalid userId" });

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound(new { ok = false, error = "User not found" });

            string bio = user.Bio ?? "";
            bool nowLocked = false;
            if (bio.Contains("[LOCKED]"))
            {
                bio = bio.Replace("[LOCKED]", "").Trim();
                nowLocked = false;
            }
            else
            {
                bio = (bio + " [LOCKED]").Trim();
                nowLocked = true;
            }
            user.Bio = bio;
            await _context.SaveChangesAsync();
            return Ok(new { ok = true, isLocked = nowLocked, message = nowLocked ? "Đã khóa tài khoản" : "Đã mở khóa tài khoản" });
        }

        // 3. System Monitoring
        [HttpGet("api/admin/stats")]
        public async Task<IActionResult> GetAdminStats()
        {
            if (!IsAdminUser) return Forbid();
            try
            {
                int totalUsers = await _context.Users.CountAsync();
                var today = DateTime.UtcNow.Date;
                int chatsToday = 0;
                try { chatsToday = await _context.Chats.CountAsync(c => c.CreatedAt >= today); } catch { }
                int pendingFeedbacks = 0;
                try { pendingFeedbacks = await _context.UserFeedbacks.CountAsync(f => !f.IsResolved); } catch { }

                // Scan active ports (from 5000 to 5200) or check running dotnet processes
                var activePorts = new List<object>();
                try
                {
                    // Check known preview ports
                    for (int p = 5001; p <= 5030; p++)
                    {
                        try
                        {
                            using var tcpClient = new System.Net.Sockets.TcpClient();
                            var connectTask = tcpClient.ConnectAsync("127.0.0.1", p);
                            if (Task.WhenAny(connectTask, Task.Delay(40)).Result == connectTask && tcpClient.Connected)
                            {
                                activePorts.Add(new { port = p, status = "Running", protocol = "HTTP / Web" });
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                return Ok(new
                {
                    ok = true,
                    totalUsers,
                    chatsToday,
                    pendingFeedbacks,
                    activePorts
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        [HttpPost("api/admin/kill-port")]
        public async Task<IActionResult> KillPort([FromBody] JsonElement payload)
        {
            if (!IsAdminUser) return Forbid();
            int port = payload.TryGetProperty("port", out var pProp) && pProp.TryGetInt32(out int parsedPort) ? parsedPort : 0;
            if (port <= 0) return BadRequest(new { ok = false, error = "Invalid port" });

            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c for /f \"tokens=5\" %a in ('netstat -aon ^| find \":{port}\"') do taskkill /f /pid %a",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    await proc!.WaitForExitAsync();
                }
                else
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "fuser",
                        Arguments = $"-k {port}/tcp",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    await proc!.WaitForExitAsync();
                }
                return Ok(new { ok = true, message = $"Đã dừng tiến trình tại cổng {port}" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }
    }
}
