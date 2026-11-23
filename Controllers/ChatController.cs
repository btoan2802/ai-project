using System.Security.Claims;
using BtoanAiNet.Repositories;
using BtoanAiNet.Services;
using BtoanAiNet.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BtoanAiNet.Controllers
{
    public class ChatController : Controller
    {
        private readonly IChatService _chatService;
        private readonly IConversationRepository _conversationRepository;
        private readonly IMemoryService _memoryService;
        private readonly ITrainingService _trainingService;
        private readonly IWebHostEnvironment _env;

        public ChatController(
            IChatService chatService,
            IConversationRepository conversationRepository,
            IMemoryService memoryService,
            ITrainingService trainingService,
            IWebHostEnvironment env)
        {
            _chatService = chatService;
            _conversationRepository = conversationRepository;
            _memoryService = memoryService;
            _trainingService = trainingService;
            _env = env;
        }

        protected int? CurrentUserId
        {
            get
            {
                var claimId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(claimId, out int uid)) return uid;
                return HttpContext.Session.GetInt32("user_id");
            }
        }


        // POST /ai/api/chat.php
        [HttpPost]
        [Route("ai/api/chat.php")]
        [Route("Chat/Send")]
        public async Task<IActionResult> Chat([FromBody] ChatRequestModel model)
        {
            if (model == null)
            {
                return Json(new { ok = false, error = "Invalid request payload" });
            }

            try
            {
                string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var result = await _chatService.ProcessChatAsync(model, webRootPath);
                return Content(result.ToJsonString(), "application/json");
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // GET /ai/api/get_conversations.php
        [HttpGet]
        [Route("ai/api/get_conversations.php")]
        [Route("Chat/GetConversations")]
        public async Task<IActionResult> GetConversations([FromQuery] int user_id)
        {
            int currentUid = CurrentUserId ?? user_id;
            if (CurrentUserId.HasValue && user_id > 0 && user_id != CurrentUserId.Value)
            {
                return StatusCode(403, new { error = "Không có quyền xem hội thoại của người dùng khác" });
            }

            if (currentUid <= 0) return Json(new List<object>());

            try
            {
                var conversations = await _conversationRepository.GetConversationsByUserIdAsync(currentUid);
                var result = conversations.Select(c => new
                {
                    id = c.Id,
                    title = c.Title
                });

                return Json(result);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // GET /ai/api/get_messages.php
        [HttpGet]
        [Route("ai/api/get_messages.php")]
        [Route("Chat/GetMessages")]
        public async Task<IActionResult> GetMessages([FromQuery] int conversation_id)
        {
            if (conversation_id == 0)
            {
                return Json(new List<object>());
            }

            int? currentUid = CurrentUserId;
            if (currentUid.HasValue)
            {
                var conv = await _conversationRepository.GetConversationByIdAsync(conversation_id, currentUid.Value);
                if (conv == null)
                {
                    return StatusCode(403, new { error = "Không có quyền truy cập hội thoại này" });
                }
            }

            try
            {
                var chats = await _conversationRepository.GetMessagesByConversationIdAsync(conversation_id);
                var data = new List<object>();

                foreach (var chat in chats)
                {
                    if (!string.IsNullOrEmpty(chat.Question))
                    {
                        List<string> images = new List<string>();
                        if (!string.IsNullOrEmpty(chat.Images))
                        {
                            try
                            {
                                var decoded = System.Text.Json.JsonSerializer.Deserialize<List<string>>(chat.Images);
                                if (decoded != null) images = decoded;
                            }
                            catch { }
                        }

                        data.Add(new
                        {
                            role = "user",
                            text = chat.Question,
                            images = images
                        });
                    }

                    if (!string.IsNullOrEmpty(chat.Answer))
                    {
                        data.Add(new
                        {
                            role = "assistant",
                            text = chat.Answer,
                            images = new List<string>(),
                            videos = (object?)null,
                            music_url = (string?)null,
                            music_prompt = (string?)null,
                            music_duration = (int?)null
                        });
                    }
                }

                return Json(data);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // POST /ai/api/rename_conversation.php
        [HttpPost]
        [Route("ai/api/rename_conversation.php")]
        [Route("Chat/RenameConversation")]
        public async Task<IActionResult> RenameConversation([FromBody] RenameConversationRequestModel model)
        {
            if (model == null || model.Id == 0)
            {
                return Json(new { status = "error", message = "missing id" });
            }

            int? currentUid = CurrentUserId;
            if (currentUid.HasValue)
            {
                var conv = await _conversationRepository.GetConversationByIdAsync(model.Id, currentUid.Value);
                if (conv == null)
                {
                    return StatusCode(403, new { status = "error", message = "Không có quyền đổi tên hội thoại này" });
                }
            }

            try
            {
                string title = model.Title?.Trim() ?? "Chat mới";
                await _conversationRepository.UpdateConversationTitleAsync(model.Id, title);

                return Json(new { status = "ok", title = title });
            }
            catch
            {
                return Json(new { status = "error", message = "db error" });
            }
        }

        // POST /ai/api/delete_conversation.php
        [HttpPost]
        [Route("ai/api/delete_conversation.php")]
        [Route("Chat/DeleteConversation")]
        public async Task<IActionResult> DeleteConversation([FromBody] DeleteConversationRequestModel model)
        {
            if (model == null || model.Id == 0)
            {
                return Json(new { error = "no data" });
            }

            int? currentUid = CurrentUserId;
            if (currentUid.HasValue)
            {
                var conv = await _conversationRepository.GetConversationByIdAsync(model.Id, currentUid.Value);
                if (conv == null)
                {
                    return StatusCode(403, new { error = "Không có quyền xoá hội thoại này" });
                }
            }

            try
            {
                await _conversationRepository.DeleteConversationAsync(model.Id);
                return Json(new { status = "deleted" });
            }
            catch
            {
                return Json(new { error = "db error" });
            }
        }

        // GET /ai/api/get_memory.php
        [HttpGet]
        [Route("ai/api/get_memory.php")]
        [Route("Chat/GetMemory")]
        public async Task<IActionResult> GetMemory([FromQuery] int user_id)
        {
            try
            {
                var memories = await _memoryService.GetMemoryByUserIdAsync(user_id);
                var result = memories.Select(m => new
                {
                    id = m.Id,
                    user_id = m.UserId,
                    memory_value = m.MemoryValue
                });

                return Json(result);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // POST /ai/api/save_memory.php
        [HttpPost]
        [Route("ai/api/save_memory.php")]
        [Route("Chat/SaveMemory")]
        public async Task<IActionResult> SaveMemory([FromBody] SaveMemoryRequestModel model)
        {
            if (model == null || model.User_Id == 0 || string.IsNullOrWhiteSpace(model.Memory))
            {
                return Json(new { status = "error", message = "missing data" });
            }

            try
            {
                await _memoryService.SaveMemoryAsync(model.User_Id, model.Memory.Trim());
                return Json(new { status = "ok" });
            }
            catch
            {
                return Json(new { status = "error", message = "db error" });
            }
        }

        // GET /ai/api/get_training.php
        [HttpGet]
        [Route("ai/api/get_training.php")]
        [Route("Chat/GetTraining")]
        public async Task<IActionResult> GetTraining([FromQuery] string q)
        {
            try
            {
                var trainings = await _trainingService.SearchTrainingAsync(q ?? "");
                var result = trainings.Select(t => new
                {
                    id = t.Id,
                    question = t.Question,
                    correct_answer = t.CorrectAnswer
                });

                return Json(result);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // POST /ai/api/save_training.php
        [HttpPost]
        [Route("ai/api/save_training.php")]
        [Route("Chat/SaveTraining")]
        public async Task<IActionResult> SaveTraining([FromBody] SaveTrainingRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Question))
            {
                return Json(new { status = "error", message = "missing data" });
            }

            try
            {
                await _trainingService.SaveTrainingAsync(model.Question, model.Ai_Answer ?? "", model.Correct_Answer ?? "", model.User_Id);
                return Json(new { status = "ok" });
            }
            catch
            {
                return Json(new { status = "error", message = "db error" });
            }
        }
    
        // GET /ai/api/search.php
        [HttpGet]
        [Route("ai/api/search.php")]
        [Route("Chat/Search")]
        public async Task<IActionResult> Search([FromQuery] string? q)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Json(new { results = new List<object>() });
            }

            try
            {
                var results = new List<object>();
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                string ddgUrl = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(q)}&format=json&no_html=1&skip_disambig=1";
                var resp = await client.GetAsync(ddgUrl);
                if (resp.IsSuccessStatusCode)
                {
                    var jsonStr = await resp.Content.ReadAsStringAsync();
                    var doc = System.Text.Json.Nodes.JsonNode.Parse(jsonStr);
                    string heading = doc?["Heading"]?.ToString() ?? q;
                    string abstractText = doc?["AbstractText"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(abstractText))
                    {
                        results.Add(new { title = heading, snippet = abstractText });
                    }
                    var related = doc?["RelatedTopics"]?.AsArray();
                    if (related != null)
                    {
                        foreach (var item in related)
                        {
                            string text = item?["Text"]?.ToString() ?? "";
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                results.Add(new { title = q, snippet = text });
                                if (results.Count >= 5) break;
                            }
                        }
                    }
                }

                if (results.Count == 0)
                {
                    results.Add(new
                    {
                        title = $"Kết quả tìm kiếm cho: {q}",
                        snippet = $"Thông tin tổng hợp liên quan đến chủ đề '{q}' để bổ trợ câu trả lời."
                    });
                }

                return Json(new { results });
            }
            catch
            {
                return Json(new
                {
                    results = new[]
                    {
                        new { title = q, snippet = $"Kết quả tìm kiếm cho: {q}" }
                    }
                });
            }
        }

        // GET /ai/api/search_history.php
        [HttpGet]
        [Route("ai/api/search_history.php")]
        [Route("Chat/SearchHistory")]
        public async Task<IActionResult> SearchHistory([FromQuery] string? q, [FromQuery] int user_id)
        {
            int currentUid = CurrentUserId ?? user_id;
            if (currentUid <= 0) return Json(new List<object>());

            try
            {
                var convs = await _conversationRepository.GetConversationsByUserIdAsync(currentUid);
                if (!string.IsNullOrWhiteSpace(q))
                {
                    convs = convs.Where(c => !string.IsNullOrEmpty(c.Title) && c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                var result = convs.Select(c => new
                {
                    id = c.Id,
                    title = c.Title
                });

                return Json(result);
            }
            catch
            {
                return Json(new List<object>());
            }
        }
    }
}
