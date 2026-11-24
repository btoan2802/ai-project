using System.Security.Claims;
using BtoanAiNet.Models;
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
        private readonly IServiceScopeFactory _scopeFactory;

        public ChatController(
            IChatService chatService,
            IConversationRepository conversationRepository,
            IMemoryService memoryService,
            ITrainingService trainingService,
            IWebHostEnvironment env,
            IServiceScopeFactory scopeFactory)
        {
            _chatService = chatService;
            _conversationRepository = conversationRepository;
            _memoryService = memoryService;
            _trainingService = trainingService;
            _env = env;
            _scopeFactory = scopeFactory;
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


        // POST /ai/api/chat.php & /api/chat
        [HttpPost]
        [Route("api/chat")]
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

        // POST /api/chat/stream - Realtime SSE Streaming under 1 second TTFT
        [HttpPost]
        [Route("api/chat/stream")]
        public async Task StreamChat([FromBody] ChatRequestModel model)
        {
            if (model == null)
            {
                Response.StatusCode = 400;
                return;
            }

            HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            Response.ContentType = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache, no-transform";
            Response.Headers["X-Accel-Buffering"] = "no";

            var ct = HttpContext.RequestAborted;
            string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");

            int currentUid = CurrentUserId ?? model.UserId;
            if (model.UserId == 0 && currentUid > 0)
            {
                model.UserId = currentUid;
            }

            int conversationId = model.ConversationId;
            bool isNewConv = false;
            string? convTitle = null;

            if (currentUid > 0 && conversationId == 0)
            {
                try
                {
                    var newConv = await _conversationRepository.CreateConversationAsync(currentUid, "Chat mới");
                    conversationId = newConv.Id;
                    model.ConversationId = conversationId;
                    isNewConv = true;
                }
                catch { }
            }

            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            long ttftMs = 0;
            bool firstChunk = true;

            var sbFull = new System.Text.StringBuilder();
            try
            {
                await foreach (var chunk in _chatService.StreamChatAsync(model, webRootPath, ct))
                {
                    if (firstChunk)
                    {
                        ttftMs = swTotal.ElapsedMilliseconds;
                        firstChunk = false;
                    }
                    sbFull.Append(chunk);
                    var payload = System.Text.Json.JsonSerializer.Serialize(new { chunk, ttft_ms = ttftMs });
                    await Response.WriteAsync($"data: {payload}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }

                if (ttftMs == 0) ttftMs = swTotal.ElapsedMilliseconds;

                if (sbFull.Length == 0)
                {
                    try
                    {
                        var jsonRes = await _chatService.ProcessChatAsync(model, webRootPath);
                        string fallbackAnswer = jsonRes["answer"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                        {
                            sbFull.Append(fallbackAnswer);
                            var payload = System.Text.Json.JsonSerializer.Serialize(new { chunk = fallbackAnswer, ttft_ms = swTotal.ElapsedMilliseconds });
                            await Response.WriteAsync($"data: {payload}\n\n", ct);
                            await Response.Body.FlushAsync(ct);
                        }
                        else
                        {
                            string errMsg = jsonRes["error"]?.ToString() ?? "Không nhận được phản hồi từ AI";
                            sbFull.Append($"❌ {errMsg}");
                            var payload = System.Text.Json.JsonSerializer.Serialize(new { chunk = $"❌ {errMsg}", ttft_ms = swTotal.ElapsedMilliseconds });
                            await Response.WriteAsync($"data: {payload}\n\n", ct);
                            await Response.Body.FlushAsync(ct);
                        }
                    }
                    catch (Exception exFall)
                    {
                        string errMsg = $"❌ Lỗi kết nối AI: {exFall.Message}";
                        sbFull.Append(errMsg);
                        var payload = System.Text.Json.JsonSerializer.Serialize(new { chunk = errMsg, ttft_ms = swTotal.ElapsedMilliseconds });
                        await Response.WriteAsync($"data: {payload}\n\n", ct);
                        await Response.Body.FlushAsync(ct);
                    }
                }

                // Save to DB asynchronously upon stream completion
                if (currentUid > 0 && conversationId > 0)
                {
                    try
                    {
                        string qText = model.Question ?? (model.Payload?["question"]?.ToString() ?? "");
                        if (string.IsNullOrWhiteSpace(qText) && model.Payload?["contents"] is System.Text.Json.Nodes.JsonArray cArr && cArr.Count > 0)
                        {
                            var lastU = cArr.LastOrDefault(c => c?["role"]?.ToString() == "user");
                            if (lastU?["parts"] is System.Text.Json.Nodes.JsonArray pArr && pArr.Count > 0)
                            {
                                qText = pArr[0]?["text"]?.ToString() ?? "";
                            }
                        }
                        await _conversationRepository.AddChatAsync(new Chat
                        {
                            UserId = currentUid,
                            ConversationId = conversationId,
                            Question = qText,
                            Answer = sbFull.ToString(),
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                    catch { }
                }

                // Auto-generate title: only if new conversation or title is empty / "Chat mới"
                if (currentUid > 0 && conversationId > 0)
                {
                    try
                    {
                        var existing = await _conversationRepository.GetConversationByIdAsync(conversationId, currentUid);
                        bool needsTitle = isNewConv || existing == null || string.IsNullOrWhiteSpace(existing.Title) || existing.Title == "Chat mới";
                        if (needsTitle)
                        {
                            string qCandidate = model.Question ?? (model.Payload?["question"]?.ToString() ?? "");
                            if (string.IsNullOrWhiteSpace(qCandidate) && model.Payload?["contents"] is System.Text.Json.Nodes.JsonArray cArr && cArr.Count > 0)
                            {
                                var lastU = cArr.LastOrDefault(c => c?["role"]?.ToString() == "user");
                                if (lastU?["parts"] is System.Text.Json.Nodes.JsonArray pArr && pArr.Count > 0)
                                {
                                    qCandidate = pArr[0]?["text"]?.ToString() ?? "";
                                }
                            }

                            string meaningfulQ = "";
                            string lowerQC = (qCandidate ?? "").Trim().ToLowerInvariant();
                            bool isFiller = lowerQC == "có" || lowerQC == "co" || lowerQC == "ok" || lowerQC == "ừ" || lowerQC == "u" || 
                                           lowerQC == "được" || lowerQC == "chào" || lowerQC == "ê" || lowerQC == "e" ||
                                           lowerQC == "đc chưa" || lowerQC == "dc chua" || lowerQC == "được chưa" || lowerQC == "duoc chua" ||
                                           lowerQC == "xong chưa" || lowerQC == "xong chua" || lowerQC == "sao rồi" || lowerQC == "sao roi" ||
                                           lowerQC == "đâu rồi" || lowerQC == "dau roi" || lowerQC == "thêm vào đi" || lowerQC == "làm đi" ||
                                           lowerQC.Length <= 1;
                            if (!isFiller && !string.IsNullOrWhiteSpace(qCandidate))
                            {
                                meaningfulQ = qCandidate.Trim();
                            }
                            else
                            {
                                var history = await _conversationRepository.GetChatHistoryAsync(conversationId, currentUid, 15);
                                var found = history.FirstOrDefault(h => {
                                    string t = (h.Question ?? "").Trim().ToLowerInvariant();
                                    return t != "có" && t != "co" && t != "ok" && t != "ừ" && t != "chào" && t != "ê" && 
                                           t != "đc chưa" && t != "được chưa" && t != "xong chưa" && t.Length > 2;
                                });
                                if (found != null && !string.IsNullOrWhiteSpace(found.Question))
                                {
                                    meaningfulQ = found.Question.Trim();
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(meaningfulQ))
                            {
                                string quickTitle = meaningfulQ;
                                if (quickTitle.Length > 35) quickTitle = quickTitle.Substring(0, 35) + "...";
                                convTitle = quickTitle;
                                await _conversationRepository.UpdateConversationTitleAsync(conversationId, quickTitle);

                                // Refine asynchronously in background without delaying done event
                                int cId = conversationId;
                                string mq = meaningfulQ;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using var scope = _scopeFactory.CreateScope();
                                        var cs = scope.ServiceProvider.GetRequiredService<IChatService>();
                                        var cr = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
                                        var refined = await cs.GenerateTitleAsync(mq);
                                        if (!string.IsNullOrWhiteSpace(refined) && refined != "Chat mới" && refined.ToLowerInvariant() != "có" && refined.ToLowerInvariant() != "ok" && !refined.StartsWith("{") && !refined.StartsWith("["))
                                        {
                                            await cr.UpdateConversationTitleAsync(cId, refined);
                                        }
                                    }
                                    catch { }
                                });
                            }
                            else if (existing != null)
                            {
                                convTitle = existing.Title;
                            }
                        }
                        else if (existing != null)
                        {
                            convTitle = existing.Title;
                        }
                    }
                    catch { }
                }

                var donePayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    done = true,
                    answer = sbFull.ToString(),
                    conversation_id = conversationId,
                    conversation_title = convTitle ?? "Chat mới",
                    ttft_ms = ttftMs,
                    total_ms = swTotal.ElapsedMilliseconds
                });
                await Response.WriteAsync($"data: {donePayload}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamChat EXCEPTION] {ex}");
                var errPayload = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message, done = true });
                await Response.WriteAsync($"data: {errPayload}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }

        [HttpGet]
        [Route("api/chat/export-all")]
        public async Task<IActionResult> ExportAllConversations()
        {
            int? uid = CurrentUserId;
            if (!uid.HasValue || uid.Value <= 0)
            {
                return Unauthorized(new { error = "Vui lòng đăng nhập" });
            }

            try
            {
                var data = await _conversationRepository.GetAllWithChatsByUserIdAsync(uid.Value);
                var jsonBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                return File(jsonBytes, "application/json", $"btoan_ai_conversations_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost]
        [Route("api/chat/delete-all")]
        public async Task<IActionResult> DeleteAllConversations()
        {
            int? uid = CurrentUserId;
            if (!uid.HasValue || uid.Value <= 0)
            {
                return Unauthorized(new { error = "Vui lòng đăng nhập" });
            }

            try
            {
                await _conversationRepository.DeleteAllByUserIdAsync(uid.Value);
                return Ok(new { success = true, message = "Đã xóa toàn bộ lịch sử trò chuyện" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET /ai/api/get_conversations.php
        [HttpGet]
        [Route("ai/api/get_conversations.php")]
        [Route("ai/api/get_history.php")]
        [Route("api/chat/history")]
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
