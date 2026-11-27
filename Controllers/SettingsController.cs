using System.Security.Claims;
using System.Text.Json;
using BtoanAiNet.Data;
using BtoanAiNet.Models;
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
                var claimId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(claimId, out int uid) && uid > 0) return uid;
                var sessionUid = HttpContext.Session.GetInt32("user_id");
                if (sessionUid.HasValue && sessionUid.Value > 0) return sessionUid.Value;
                return 1; // Default to active user
            }
        }

        // 1. User Preferences (Custom Instructions, Saved Memories, Theme, Location)
        [HttpGet("api/settings/preferences")]
        public async Task<IActionResult> GetPreferences()
        {
            int uid = CurrentUserId;
            var pref = await _context.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
            if (pref == null)
            {
                pref = new UserPreference
                {
                    UserId = uid,
                    CustomInstructions = "",
                    SavedMemories = "",
                    Theme = "dark",
                    DefaultLocation = "TP. Hồ Chí Minh, Việt Nam"
                };
            }
            return Ok(new { ok = true, data = pref });
        }

        [HttpPost("api/settings/preferences")]
        public async Task<IActionResult> SavePreferences([FromBody] UserPreference model)
        {
            int uid = CurrentUserId;
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
            return Ok(new { ok = true, message = "Đã lưu cài đặt cá nhân thành công!", data = pref });
        }

        // 2. Gems Management
        [HttpGet("api/settings/gems")]
        public async Task<IActionResult> GetGems()
        {
            int uid = CurrentUserId;
            var gems = await _context.UserGems
                .Where(g => g.UserId == uid || g.UserId == 1)
                .OrderByDescending(g => g.Id)
                .ToListAsync();
            return Ok(new { ok = true, data = gems });
        }

        [HttpPost("api/settings/gems")]
        public async Task<IActionResult> SaveGem([FromBody] UserGem model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Name))
                return BadRequest(new { ok = false, error = "Tên Gem không được để trống" });

            int uid = CurrentUserId;
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

        [HttpDelete("api/settings/gems/{id}")]
        public async Task<IActionResult> DeleteGem(int id)
        {
            int uid = CurrentUserId;
            var gem = await _context.UserGems.FirstOrDefaultAsync(g => g.Id == id && (g.UserId == uid || g.UserId == 1));
            if (gem != null)
            {
                _context.UserGems.Remove(gem);
                await _context.SaveChangesAsync();
            }
            return Ok(new { ok = true, message = "Đã xóa Gem thành công" });
        }

        // 3. User Feedback
        [HttpPost("api/settings/feedback")]
        public async Task<IActionResult> SubmitFeedback([FromBody] UserFeedback model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Message))
                return BadRequest(new { ok = false, error = "Vui lòng nhập nội dung ý kiến đóng góp" });

            int uid = CurrentUserId;
            var fb = new UserFeedback
            {
                UserId = uid,
                Category = model.Category ?? "Góp ý chung",
                Message = model.Message,
                Rating = model.Rating > 0 ? model.Rating : 5,
                CreatedAt = DateTime.UtcNow
            };
            _context.UserFeedbacks.Add(fb);
            await _context.SaveChangesAsync();
            return Ok(new { ok = true, message = "Cảm ơn bạn đã gửi ý kiến đóng góp cho Btoan AI!" });
        }

        // 4. Usage Quota & Account Info
        [HttpGet("api/settings/usage")]
        public async Task<IActionResult> GetUsage()
        {
            int uid = CurrentUserId;
            var today = DateTime.UtcNow.Date;

            int chatCountToday = await _context.Chats.CountAsync(c => c.UserId == uid && c.CreatedAt >= today);
            int chatCountTotal = await _context.Chats.CountAsync(c => c.UserId == uid);
            int projectCount = await _context.Projects.CountAsync(p => p.UserId == uid);
            int imageCountToday = await _context.Chats.CountAsync(c => c.UserId == uid && c.CreatedAt >= today && (c.Question != null && (c.Question.Contains("vẽ") || c.Question.Contains("tạo ảnh"))));

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
            int uid = CurrentUserId;
            var links = await _context.SharedLinks
                .Where(s => s.UserId == uid && !s.IsRevoked)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();
            return Ok(new { ok = true, data = links });
        }

        [HttpPost("api/settings/revoke-link")]
        public async Task<IActionResult> RevokeLink([FromBody] JsonElement payload)
        {
            string token = payload.TryGetProperty("token", out var tProp) ? tProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(token)) return BadRequest(new { ok = false, error = "Token không hợp lệ" });

            int uid = CurrentUserId;
            var link = await _context.SharedLinks.FirstOrDefaultAsync(s => s.ShareToken == token && s.UserId == uid);
            if (link != null)
            {
                link.IsRevoked = true;
                await _context.SaveChangesAsync();
            }
            return Ok(new { ok = true, message = "Đã thu hồi liên kết công khai thành công" });
        }

        // 6. Import Memory (ChatGPT / Gemini export)
        [HttpPost("api/settings/import-memory")]
        public async Task<IActionResult> ImportMemory([FromBody] JsonElement payload)
        {
            string text = payload.TryGetProperty("content", out var cProp) ? cProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) return BadRequest(new { ok = false, error = "Nội dung tệp rỗng" });

            int uid = CurrentUserId;
            var pref = await _context.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
            if (pref == null)
            {
                pref = new UserPreference
                {
                    UserId = uid,
                    SavedMemories = text.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.UserPreferences.Add(pref);
            }
            else
            {
                pref.SavedMemories = string.IsNullOrWhiteSpace(pref.SavedMemories)
                    ? text.Trim()
                    : pref.SavedMemories + "\n---\n" + text.Trim();
                pref.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return Ok(new { ok = true, message = "Đã trích xuất và nhập bộ nhớ vào Trí thông minh cá nhân thành công!" });
        }
    }
}
