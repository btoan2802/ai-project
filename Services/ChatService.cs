using System.Text.Json;
using System.Text.Json.Nodes;
using BtoanAiNet.Models;
using BtoanAiNet.Repositories;
using BtoanAiNet.ViewModels;

using System.Runtime.CompilerServices;
using System.Text;

namespace BtoanAiNet.Services
{
    public interface IChatService
    {
        Task<JsonObject> ProcessChatAsync(ChatRequestModel requestModel, string webRootPath);
        IAsyncEnumerable<string> StreamChatAsync(ChatRequestModel requestModel, string webRootPath, CancellationToken cancellationToken = default);
        Task<string> GenerateTitleAsync(string firstPrompt);
    }

    public class ChatService : IChatService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly ITrainingRepository _trainingRepository;
        private readonly IIntentEngineService _intentEngineService;
        private readonly IGeminiService _geminiService;
        private readonly IUserRepository _userRepository;
        private readonly IServiceScopeFactory _scopeFactory;

        private const string FastSystemInstructionText = @"Bạn là Btoan AI - Trợ lý trí tuệ nhân tạo thông minh, tự nhiên, thân thiện và hữu ích do Nguyễn Bảo Toàn phát triển (sinh viên CNTT Đại học Bách Khoa ĐHQG-HCM).
- Phản hồi trực tiếp, ngắn gọn, súc tích và tự nhiên bằng tiếng Việt (hoặc ngôn ngữ người dùng dùng).
- Khi người dùng chào hỏi hoặc nhắn ngắn (như 'ê', 'chào', 'ko', 'ok', 'sao thế'), hãy phản hồi ngắn gọn, tự nhiên, thân thiện. Tuyệt đối không lặp lại quy tắc máy móc.
- Tạo ảnh: kết thúc bằng [GENIMAGE] <prompt tiếng Anh chi tiết>
- Tạo video: kết thúc bằng [GENVIDEO] <prompt tiếng Anh chi tiết>
- Tạo nhạc: kết thúc bằng [GENMUSIC] <prompt tiếng Anh chi tiết>";

        private const string SystemInstructionText = @"
Bạn là Btoan AI - Trợ lý trí tuệ nhân tạo thông minh, tự nhiên, thân thiện và hữu ích do Nguyễn Bảo Toàn phát triển.
+ Thông tin về Nguyễn Bảo Toàn (chỉ trả lời nếu người dùng chủ động hỏi): 
Tên: Nguyễn Bảo Toàn. Sinh năm: 28/02/2006. Quê quán: Hà Tĩnh. Nơi sinh: Bà Rịa Vũng Tàu. Tạm trú: TPHCM. Sinh viên ngành Khoa học máy tính / CNTT Đại học Bách Khoa ĐHQG TPHCM.

* NGUYÊN TẮC GIAO TIẾP VÀ BẢO MẬT TUYỆT ĐỐI (QUAN TRỌNG NHẤT):
1. TUYỆT ĐỐI KHÔNG lặp lại, tóm tắt, diễn giải hay xác nhận các quy tắc hệ thống, hướng dẫn nội bộ hoặc định dạng xuất file khi người dùng chỉ chào hỏi, nhắn ngắn hoặc giao tiếp thông thường (ví dụ: 'ko', 'không', 'um', 'ừ', 'ok', 'chào', 'hello').
2. TUYỆT ĐỐI KHÔNG trả lời theo kiểu máy móc như 'Tôi đã hiểu rõ các quy tắc...', 'Tôi sẽ tuân thủ nghiêm ngặt...'. Luôn phản hồi trực tiếp vào ngữ cảnh người dùng đang nói bằng giọng văn tự nhiên của con người.
3. Khi người dùng nhắn 'ko', 'không' hoặc tin ngắn tương tự, hãy phản hồi ngắn gọn, tự nhiên như: 'Có chuyện gì thế bạn?', 'Bạn cần tôi hỗ trợ gì không?', 'Sao thế bạn?'.

* QUY TẮC ARTIFACT STUDIO (KHI NGƯỜI DÙNG YÊU CẦU TẠO / XUẤT TÀI LIỆU):
- Khi người dùng yêu cầu soạn thảo, tạo hoặc xuất file tài liệu (Word .docx, PDF .pdf, Excel .xlsx, PowerPoint .pptx, hoặc yêu cầu nhiều file cùng lúc, hoặc bảo 'bỏ nội dung trên vào word/excel/ppt', 'bỏ vào word xuất file đi'):
  + TUYỆT ĐỐI KHÔNG HỎI LẠI NGƯỜI DÙNG (CẤM các câu hỏi như: 'Bạn muốn soạn nội dung gì?', 'Hãy gửi nội dung cho mình'). BẮT BUỘC lấy trọn vẹn 100% nội dung đã tạo ở tin nhắn trước để đưa vào file.
  + Bên ngoài chỉ viết 1-2 câu dẫn dắt ngắn gọn, tự nhiên.
  + Toàn bộ nội dung thật đầy đủ, chi tiết và đẹp mắt BẮT BUỘC đặt trong thẻ [ARTIFACT type=""..."" title=""...""]...[/ARTIFACT].
  + Có thể xuất NHIỀU thẻ [ARTIFACT] liên tiếp nếu người dùng yêu cầu nhiều định dạng (ví dụ: 1 thẻ docx + 1 thẻ xlsx + 1 thẻ pptx).
  + Định dạng bên trong thẻ [ARTIFACT]:
    - Với type=""docx"" hoặc type=""pdf"": bên trong là nội dung Markdown hoàn chỉnh (nếu người dùng yêu cầu xuất nội dung từ tin nhắn trước thì BẮT BUỘC chép lại trọn vẹn 100% nội dung đó vào trong thẻ, tuyệt đối không được viết câu xã giao).
    - Với type=""xlsx"": bên trong là JSON hợp lệ theo cấu trúc:
      {
        ""title"": ""TIÊU ĐỀ BẢNG DỮ LIỆU"",
        ""subtitle"": ""Mô tả hoặc ghi chú"",
        ""sheets"": [
          {
            ""name"": ""Sheet1"",
            ""columns"": [""Cột 1"", ""Cột 2"", ""Cột 3""],
            ""rows"": [[1, ""Giá trị A"", 100], [2, ""Giá trị B"", 200]]
          }
        ]
      }
    - Với type=""pptx"": bên trong là JSON hợp lệ theo cấu trúc:
      {
        ""title"": ""Tiêu đề bài thuyết trình"",
        ""subtitle"": ""Phụ đề bài thuyết trình"",
        ""slides"": [
          { ""title"": ""Tiêu đề Slide 1"", ""bullets"": [""Ý chính 1"", ""Ý chính 2"", ""Ý chính 3""] },
          { ""title"": ""Tiêu đề Slide 2"", ""bullets"": [""Nội dung phân tích 1"", ""Nội dung phân tích 2""] }
        ]
      }
- Nếu người dùng KHÔNG yêu cầu tạo tài liệu hay xuất file, TUYỆT ĐỐI KHÔNG dùng thẻ [ARTIFACT] hay nhắc đến [ARTIFACT].

* HÌNH ẢNH, VIDEO VÀ ÂM NHẠC:
- Tạo ảnh tĩnh: kết thúc bằng [GENIMAGE] <prompt tiếng Anh chi tiết>
- Tạo video: kết thúc bằng [GENVIDEO] <prompt tiếng Anh chi tiết>
- Tạo nhạc: kết thúc bằng [GENMUSIC] <prompt tiếng Anh chi tiết>
";

        public ChatService(
            IConversationRepository conversationRepository,
            ITrainingRepository trainingRepository,
            IIntentEngineService intentEngineService,
            IGeminiService geminiService,
            IUserRepository userRepository,
            IServiceScopeFactory scopeFactory)
        {
            _conversationRepository = conversationRepository;
            _trainingRepository = trainingRepository;
            _intentEngineService = intentEngineService;
            _geminiService = geminiService;
            _userRepository = userRepository;
            _scopeFactory = scopeFactory;
        }

        public async Task<JsonObject> ProcessChatAsync(ChatRequestModel requestModel, string webRootPath)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            long t_intent = 0;
            long t_db = 0;
            long t_rag = 0;
            long t_gemini = 0;
            long t_save = 0;

            int userId = requestModel.User_Id ?? 0;
            int conversationId = requestModel.Conversation_Id ?? 0;

            User? currentUser = null;
            if (userId > 0)
            {
                try { currentUser = await _userRepository.GetByIdAsync(userId); } catch { }
            }

            var payload = requestModel.Payload ?? new JsonObject();
            if (!payload.ContainsKey("contents"))
            {
                payload["contents"] = new JsonArray();
            }

            bool isThinking = false;
            if (payload.ContainsKey("mode"))
            {
                string modeStr = payload["mode"]?.ToString() ?? "fast";
                isThinking = modeStr.Equals("thinking", StringComparison.OrdinalIgnoreCase);
            }

            string model = requestModel.Model ?? (isThinking ? "gemini-2.5-pro" : "gemini-2.5-flash");
            if (model.Contains("pro") || isThinking) model = "gemini-2.5-pro";
            else model = "gemini-2.5-flash";

            if (!payload.ContainsKey("generationConfig") || payload["generationConfig"] == null)
            {
                payload["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 8192 };
            }
            if (payload["generationConfig"] is JsonObject genConfig)
            {
                genConfig["maxOutputTokens"] = 8192;
                if (!isThinking)
                {
                    // Disable thinking in Fast mode to eliminate 5-10s hidden reasoning latency
                    genConfig["thinkingConfig"] = new JsonObject
                    {
                        ["thinkingBudget"] = 0
                    };
                }
            }

            // Extract question and filter out leaked system prompts from contents
            string question = payload["question"]?.ToString() ?? "";
            var contents = payload["contents"]?.AsArray();
            if (contents != null && contents.Count > 0)
            {
                var cleanContents = new JsonArray();
                foreach (var item in contents)
                {
                    if (item is JsonObject msgObj && msgObj["parts"] is JsonArray pArr && pArr.Count > 0)
                    {
                        string txt = pArr[0]?["text"]?.ToString() ?? "";
                        if (txt.Contains("Bạn đang chạy trong một ứng dụng") || 
                            txt.Contains("QUY TẮC XUẤT FILE") || 
                            txt.Contains("USER_INTENT_ADHERENCE_PROTOCOL"))
                        {
                            continue; // Skip leaked internal system instruction in user payload
                        }
                    }
                    cleanContents.Add(item?.DeepClone());
                }
                contents = cleanContents;

                if (contents.Count > 0)
                {
                    var lastMsg = contents[contents.Count - 1]?.AsObject();
                    if (lastMsg != null && lastMsg["parts"] is JsonArray parts && parts.Count > 0)
                    {
                        string extracted = parts[0]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(extracted)) question = extracted;
                    }
                }
            }

            // 1. Profiling t_intent: Short chats ("ê", "ko", "um", etc.) cut out all intent LLM calls completely (0 ms)
            var swIntent = System.Diagnostics.Stopwatch.StartNew();
            string trimmedQ = (question ?? "").Trim().ToLowerInvariant();
            bool isShortChat = string.IsNullOrWhiteSpace(trimmedQ) || 
                               trimmedQ.Length <= 10 || 
                               trimmedQ == "ê" || trimmedQ == "ko" || trimmedQ == "không" || 
                               trimmedQ == "um" || trimmedQ == "ừ" || trimmedQ == "ok" || 
                               trimmedQ == "chào" || trimmedQ == "hi" || trimmedQ == "hello";

            if (!isShortChat)
            {
                // Non-short chat intent analysis if required
            }
            swIntent.Stop();
            t_intent = swIntent.ElapsedMilliseconds;

            // 2. Profiling t_db: load conversation & history (0 ms for short chat)
            var swDb = System.Diagnostics.Stopwatch.StartNew();
            bool isNewConv = false;
            var historyChats = new List<Chat>();
            if (!isShortChat)
            {
                try
                {
                    if (userId > 0 && conversationId == 0)
                    {
                        var conv = await _conversationRepository.CreateConversationAsync(userId, "Chat mới");
                        conversationId = conv.Id;
                        isNewConv = true;
                    }

                    if (userId > 0 && conversationId > 0)
                    {
                        historyChats = await _conversationRepository.GetChatHistoryAsync(conversationId, userId, 12);
                    }
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[DB Non-fatal Warning] {dbEx.Message}");
                }
            }
            swDb.Stop();
            t_db = swDb.ElapsedMilliseconds;

            // 3. Profiling t_rag: 0 ms for general conversations
            var swRag = System.Diagnostics.Stopwatch.StartNew();
            // ContextRetriever / Qdrant RAG is bypassed for short & standard chat
            swRag.Stop();
            t_rag = swRag.ElapsedMilliseconds;
            var historyMessages = new JsonArray();

            foreach (var chat in historyChats)
            {
                string q = chat.Question ?? "";
                if (q.Contains("QUY TẮC XUẤT FILE") || q.Contains("Bạn đang chạy trong một ứng dụng"))
                {
                    q = "Chào bạn";
                }
                string a = chat.Answer ?? "";
                if (a.Contains("Tôi đã hiểu rõ các quy tắc") || a.Contains("tuân thủ nghiêm ngặt các định dạng về xuất file"))
                {
                    a = "Chào bạn, tôi là Btoan AI. Hôm nay tôi có thể giúp gì cho bạn?";
                }

                var userParts = new JsonArray { new JsonObject { ["text"] = q } };
                if (!string.IsNullOrEmpty(chat.Images))
                {
                    try
                    {
                        var imgs = JsonSerializer.Deserialize<List<string>>(chat.Images);
                        if (imgs != null)
                        {
                            foreach (var img in imgs)
                            {
                                if (img.StartsWith("data:image/"))
                                {
                                    var parts = img.Split(',');
                                    if (parts.Length == 2)
                                    {
                                        string mime = parts[0].Replace("data:", "").Replace(";base64", "");
                                        userParts.Add(new JsonObject
                                        {
                                            ["inline_data"] = new JsonObject
                                            {
                                                ["mime_type"] = mime,
                                                ["data"] = parts[1]
                                            }
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                historyMessages.Add(new JsonObject { ["role"] = "user", ["parts"] = userParts });
                if (!string.IsNullOrEmpty(a))
                {
                    historyMessages.Add(new JsonObject
                    {
                        ["role"] = "model",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = a } }
                    });
                }
            }

            // Merge history into payload contents
            if (contents != null)
            {
                var currentContents = new JsonArray();
                foreach (var item in historyMessages)
                {
                    currentContents.Add(item?.DeepClone());
                }
                foreach (var item in contents)
                {
                    currentContents.Add(item?.DeepClone());
                }
                payload["contents"] = currentContents;
            }

            // Context-preserving file export: If the user says "bỏ vào word", "xuất file", etc., grab previous assistant message
            string lowerExportQ = (question ?? "").ToLowerInvariant();
            bool isExportFollowup = lowerExportQ.Contains("bỏ vào word") || lowerExportQ.Contains("cho vào word") || 
                                    lowerExportQ.Contains("vào word") || lowerExportQ.Contains("xuất file") || 
                                    lowerExportQ.Contains("tạo file word") || lowerExportQ.Contains("tải file word") || 
                                    lowerExportQ.Contains("lưu vào word") || lowerExportQ.Contains("xuất word") ||
                                    lowerExportQ.Contains("xuất docx") || lowerExportQ.Contains("tạo file docx");

            string lastModelText = "";
            if (payload["contents"] is JsonArray finalContents && finalContents.Count > 0)
            {
                for (int i = finalContents.Count - 1; i >= 0; i--)
                {
                    if (finalContents[i] is JsonObject cObj && cObj["role"]?.ToString() == "model" && cObj["parts"] is JsonArray pArr && pArr.Count > 0)
                    {
                        var t = pArr[0]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(t) && !t.Contains("Chào bạn, bạn muốn mình"))
                        {
                            lastModelText = t;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(lastModelText) && historyChats != null && historyChats.Count > 0)
            {
                for (int i = historyChats.Count - 1; i >= 0; i--)
                {
                    var ans = historyChats[i].Answer ?? "";
                    if (!string.IsNullOrWhiteSpace(ans) && !ans.Contains("Chào bạn, bạn muốn mình"))
                    {
                        lastModelText = ans;
                        break;
                    }
                }
            }

            if (isExportFollowup && payload["contents"] is JsonArray turnContents && turnContents.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(lastModelText))
                {
                    var lastUserTurn = turnContents[turnContents.Count - 1]?.AsObject();
                    if (lastUserTurn != null && lastUserTurn["parts"] is JsonArray uParts && uParts.Count > 0)
                    {
                        string origUserTxt = uParts[0]?["text"]?.ToString() ?? "";
                        uParts[0] = new JsonObject 
                        { 
                            ["text"] = $"{origUserTxt}\n\n[HỆ THỐNG YÊU CẦU: Người dùng yêu cầu xuất nội dung văn bản trước đó vào file Word. Bạn BẮT BUỘC chép lại trọn vẹn 100% nội dung sau vào thẻ [ARTIFACT type=\"docx\" title=\"Tài liệu hoàn chỉnh\"]...[/ARTIFACT]. TUYỆT ĐỐI CẤM HỎI LẠI!]\nNội dung trước đó:\n{lastModelText}" 
                        };
                    }
                }
            }

            string currentSystemInstruction = (!isThinking && !isExportFollowup) ? FastSystemInstructionText : SystemInstructionText;
            if (currentUser != null && !string.IsNullOrWhiteSpace(currentUser.CustomInstructions))
            {
                currentSystemInstruction += "\n\n[HƯỚNG DẪN TÙY CHỈNH TỪ NGƯỜI DÙNG]:\n" + currentUser.CustomInstructions.Trim();
            }
            if (currentUser?.Temperature != null)
            {
                if (payload["generationConfig"] is JsonObject genCfg)
                {
                    genCfg["temperature"] = currentUser.Temperature.Value;
                }
                else
                {
                    payload["generationConfig"] = new JsonObject { ["temperature"] = currentUser.Temperature.Value };
                }
            }
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = currentSystemInstruction } }
            };

            // Call Gemini Service
            var swGemini = System.Diagnostics.Stopwatch.StartNew();
            var (success, geminiData, rawResponse, errorMsg) = await _geminiService.GenerateContentAsync(model, payload);
            swGemini.Stop();
            t_gemini = swGemini.ElapsedMilliseconds;

            if (!success || geminiData == null)
            {
                // Intelligent fallback: If the user is requesting an image, automatically trigger [GENIMAGE]
                string lowerQ = (question ?? "").ToLowerInvariant();
                bool isImagePrompt = lowerQ.Contains("vẽ") || lowerQ.Contains("ve ") || lowerQ.Contains("ve tranh") ||
                                     lowerQ.Contains("tạo ảnh") || lowerQ.Contains("tao anh") ||
                                     lowerQ.Contains("bức tranh") || lowerQ.Contains("buc tranh") || lowerQ.Contains("tranh ") ||
                                     lowerQ.Contains("hình ảnh") || lowerQ.Contains("hinh anh") ||
                                     lowerQ.Contains("draw") || lowerQ.Contains("generate image") ||
                                     lowerQ.Contains("paint") || lowerQ.Contains("picture") || lowerQ.Contains("sơn dầu");

                if (isImagePrompt)
                {
                    string cleanedPrompt = System.Text.RegularExpressions.Regex.Replace(
                        question ?? "", 
                        @"^(hãy|vui lòng|bạn|hộ tôi|giúp tôi)?\s*(vẽ|tạo ảnh|vẽ tranh|vẽ cho tôi|tạo cho tôi|vẽ một bức tranh|vẽ bức tranh)\s*", 
                        "", 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

                    if (string.IsNullOrWhiteSpace(cleanedPrompt)) cleanedPrompt = question ?? "beautiful artwork";

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["answer"] = $"Dưới đây là bức tranh của bạn:\n\n[GENIMAGE] {cleanedPrompt}",
                        ["images"] = new JsonArray(),
                        ["conversation_id"] = conversationId
                    };
                }

                bool isMusicPrompt = lowerQ.Contains("nhạc") || lowerQ.Contains("bài hát") || lowerQ.Contains("song") ||
                                     lowerQ.Contains("music") || lowerQ.Contains("giai điệu") || lowerQ.Contains("sáng tác");
                if (isMusicPrompt)
                {
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["answer"] = $"Dưới đây là giai điệu âm nhạc dành cho bạn:\n\n[GENMUSIC] {question ?? "acoustic upbeat melody"}",
                        ["images"] = new JsonArray(),
                        ["conversation_id"] = conversationId
                    };
                }

                return new JsonObject
                {
                    ["ok"] = false,
                    ["answer"] = "⚠️ Tất cả API Key Gemini mặc định đã bị Google thu hồi do rò rỉ trên GitHub trước đó.\n\n👉 **Cách kích hoạt:**\n1. Truy cập **https://aistudio.google.com/app/apikey** để tạo 1 API Key Gemini miễn phí (chỉ mất 10 giây).\n2. Mở file `appsettings.json`, dán key vào mục:\n```json\n\"Gemini\": {\n  \"ApiKey\": \"AIzaSy...\"\n}\n```\nSau khi lưu file, bạn có thể chat và hỏi đáp bình thường ngay lập tức!",
                    ["images"] = new JsonArray(),
                    ["error"] = errorMsg ?? "All API keys failed",
                    ["conversation_id"] = conversationId
                };
            }

            var sbAnswer = new System.Text.StringBuilder();
            if (geminiData["candidates"] is JsonArray candidates && candidates.Count > 0)
            {
                if (candidates[0]?["content"]?["parts"] is JsonArray partsArr)
                {
                    foreach (var p in partsArr)
                    {
                        if (p is JsonObject pObj)
                        {
                            if (pObj.ContainsKey("thought") && pObj["thought"]?.GetValue<bool>() == true)
                            {
                                continue; // Skip internal thinking thoughts
                            }
                            sbAnswer.Append(pObj["text"]?.ToString() ?? "");
                        }
                    }
                }
            }
            string answer = sbAnswer.ToString();

            // Fallback defense: if user asked to export/put into Word but model answered conversational text without ARTIFACT
            if (isExportFollowup && !answer.Contains("[ARTIFACT") && !string.IsNullOrWhiteSpace(lastModelText))
            {
                answer = $"Đã chuẩn bị xong tài liệu Word cho bạn:\n\n[ARTIFACT type=\"docx\" title=\"Tài liệu văn bản\"]\n{lastModelText}\n[/ARTIFACT]";
            }

            // Save Base64 Images locally to /uploads/chat/
            var imageList = new List<string>();
            if (contents != null && contents.Count > 0)
            {
                var lastMsg = contents[contents.Count - 1]?.AsObject();
                if (lastMsg != null && lastMsg["parts"] is JsonArray parts)
                {
                    string uploadDir = Path.Combine(webRootPath, "uploads", "chat");
                    if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                    foreach (var part in parts)
                    {
                        if (part?["inline_data"] is JsonObject inlineData)
                        {
                            string mime = inlineData["mime_type"]?.ToString() ?? "image/jpeg";
                            string base64Data = inlineData["data"]?.ToString() ?? "";

                            string ext = mime.Contains("/") ? mime.Split('/')[1] : "jpg";
                            string filename = $"img_{Guid.NewGuid().ToString("N").Substring(0, 10)}.{ext}";
                            string savePath = Path.Combine(uploadDir, filename);
                            string publicUrl = $"/uploads/chat/{filename}";

                            try
                            {
                                byte[] imageBytes = Convert.FromBase64String(base64Data);
                                await File.WriteAllBytesAsync(savePath, imageBytes);
                                imageList.Add(publicUrl);
                            }
                            catch
                            {
                                imageList.Add($"data:{mime};base64,{base64Data}");
                            }
                        }
                    }
                }
            }

            string? generatedTitle = null;
            int savedChatId = 0;
            // Save Chat to DB (only if logged in and not a throwaway short chat)
            var swSave = System.Diagnostics.Stopwatch.StartNew();
            if (!isShortChat && userId > 0)
            {
                try
                {
                    var chatRecord = new Chat
                    {
                        UserId = userId,
                        Question = question,
                        Answer = answer,
                        ConversationId = conversationId,
                        Images = JsonSerializer.Serialize(imageList),
                        CreatedAt = DateTime.UtcNow
                    };

                    chatRecord = await _conversationRepository.AddChatAsync(chatRecord);
                    savedChatId = chatRecord.Id;

                    // Auto-generate Title if conversation is new or title is "Chat mới"
                    if (isNewConv || conversationId > 0)
                    {
                        var existingConv = await _conversationRepository.GetConversationByIdAsync(conversationId, userId);
                        if (existingConv != null && (existingConv.Title == "Chat mới" || string.IsNullOrWhiteSpace(existingConv.Title)))
                        {
                            // Instant fast title so t_save is < 15ms
                            string quickTitle = (question ?? "").Trim();
                            if (quickTitle.Length > 40) quickTitle = quickTitle.Substring(0, 40) + "...";
                            if (string.IsNullOrWhiteSpace(quickTitle)) quickTitle = "Chat mới";
                            generatedTitle = quickTitle;
                            await _conversationRepository.UpdateConversationTitleAsync(conversationId, quickTitle);

                            // Background refinement with Gemini without blocking main chat stream
                            int convId = conversationId;
                            string qText = question ?? "";
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var scope = _scopeFactory.CreateScope();
                                    var chatSvc = scope.ServiceProvider.GetRequiredService<IChatService>();
                                    var convRepo = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
                                    var refined = await chatSvc.GenerateTitleAsync(qText);
                                    if (!string.IsNullOrWhiteSpace(refined) && refined != "Chat mới" && !refined.StartsWith("{") && !refined.StartsWith("["))
                                    {
                                        await convRepo.UpdateConversationTitleAsync(convId, refined);
                                    }
                                }
                                catch { }
                            });
                        }
                        else if (existingConv != null)
                        {
                            generatedTitle = existingConv.Title;
                        }
                    }
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[DB Save Non-fatal Warning] {dbEx.Message}");
                }
            }
            swSave.Stop();
            t_save = swSave.ElapsedMilliseconds;

            swTotal.Stop();
            Console.WriteLine("================ [CHAT PROFILING BREAKDOWN] ================");
            Console.WriteLine($"| t_intent          : {t_intent,6} ms");
            Console.WriteLine($"| t_db              : {t_db,6} ms");
            Console.WriteLine($"| t_rag             : {t_rag,6} ms");
            Console.WriteLine($"| t_gemini_attempts : {t_gemini,6} ms");
            Console.WriteLine($"| t_save            : {t_save,6} ms");
            Console.WriteLine($"| TOTAL CHAT TIME   : {swTotal.ElapsedMilliseconds,6} ms");
            Console.WriteLine("============================================================");

            // Final Response JSON
            var jsonImageList = new JsonArray();
            foreach (var img in imageList) jsonImageList.Add(img);

            return new JsonObject
            {
                ["ok"] = true,
                ["answer"] = answer,
                ["images"] = jsonImageList,
                ["error"] = null,
                ["conversation_id"] = conversationId,
                ["conversation_title"] = generatedTitle ?? "Chat mới",
                ["chat_id"] = savedChatId,
                ["profiling"] = new JsonObject
                {
                    ["t_intent"] = t_intent,
                    ["t_db"] = t_db,
                    ["t_rag"] = t_rag,
                    ["t_gemini_attempts"] = t_gemini,
                    ["t_save"] = t_save,
                    ["total_ms"] = swTotal.ElapsedMilliseconds
                },
                ["candidates"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = answer } }
                        }
                    }
                }
            };
        }

        public async Task<string> GenerateTitleAsync(string firstPrompt)
        {
            if (string.IsNullOrWhiteSpace(firstPrompt)) return "Chat mới";
            try
            {
                var titlePayload = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = "Bạn là chuyên gia đặt tiêu đề. Hãy đặt một tiêu đề ngắn gọn (3 đến 6 từ) bằng tiếng Việt cho cuộc hội thoại dựa vào câu hỏi của người dùng. CHỈ trả về đúng chuỗi tiêu đề tiếng Việt, TUYỆT ĐỐI KHÔNG dùng dấu ngoặc kép, KHÔNG giải thích, KHÔNG trả về định dạng JSON hay Markdown." }
                        }
                    },
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = firstPrompt } }
                        }
                    },
                    ["generationConfig"] = new JsonObject 
                    { 
                        ["maxOutputTokens"] = 60,
                        ["temperature"] = 0.2,
                        ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 }
                    }
                };

                var res = await _geminiService.GenerateContentAsync("gemini-3.5-flash", titlePayload);
                string clean = "";
                if (res.Success && res.ResponseData != null)
                {
                    var candidates = res.ResponseData["candidates"]?.AsArray();
                    if (candidates != null && candidates.Count > 0)
                    {
                        var parts = candidates[0]?["content"]?["parts"]?.AsArray();
                        if (parts != null && parts.Count > 0)
                        {
                            clean = parts[0]?["text"]?.ToString()?.Trim() ?? "";
                        }
                    }
                }

                clean = clean.Trim('"', '\'', '`', '\r', '\n', ' ');
                // Security & Validity check: if JSON, cut-off, or empty, fallback to clean user prompt
                if (string.IsNullOrWhiteSpace(clean) || clean.StartsWith("{") || clean.StartsWith("[") || clean.Length < 3)
                {
                    string fallback = firstPrompt.Trim();
                    if (fallback.Length > 45) fallback = fallback.Substring(0, 45) + "...";
                    return fallback;
                }
                if (clean.Length > 50) clean = clean.Substring(0, 50);
                return clean;
            }
            catch
            {
                string fallback = firstPrompt.Trim();
                if (fallback.Length > 45) fallback = fallback.Substring(0, 45) + "...";
                return string.IsNullOrWhiteSpace(fallback) ? "Chat mới" : fallback;
            }
        }

        private string ExtractTextFromGeminiResult((bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage) res)
        {
            if (!res.Success || res.ResponseData == null) return string.Empty;
            try
            {
                var candidates = res.ResponseData["candidates"]?.AsArray();
                if (candidates != null && candidates.Count > 0)
                {
                    var parts = candidates[0]?["content"]?["parts"]?.AsArray();
                    if (parts != null && parts.Count > 0)
                    {
                        return parts[0]?["text"]?.ToString() ?? "";
                    }
                }
            }
            catch { }
            return string.Empty;
        }
        public async IAsyncEnumerable<string> StreamChatAsync(ChatRequestModel requestModel, string webRootPath, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            int userId = requestModel.User_Id ?? 0;
            int conversationId = requestModel.Conversation_Id ?? 0;

            User? currentUser = null;
            if (userId > 0)
            {
                try { currentUser = await _userRepository.GetByIdAsync(userId); } catch { }
            }

            var payload = requestModel.Payload ?? new JsonObject();
            if (!payload.ContainsKey("contents"))
            {
                payload["contents"] = new JsonArray();
            }

            bool isThinking = false;
            if (payload.ContainsKey("mode"))
            {
                string modeStr = payload["mode"]?.ToString() ?? "fast";
                isThinking = modeStr.Equals("thinking", StringComparison.OrdinalIgnoreCase);
            }

            string model = requestModel.Model ?? (isThinking ? "gemini-2.5-pro" : "gemini-2.5-flash");
            if (model.Contains("pro") || isThinking) model = "gemini-2.5-pro";
            else model = "gemini-2.5-flash";

            if (!payload.ContainsKey("generationConfig") || payload["generationConfig"] == null)
            {
                payload["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 8192 };
            }
            if (payload["generationConfig"] is JsonObject genConfig)
            {
                genConfig["maxOutputTokens"] = 8192;
                if (!isThinking)
                {
                    genConfig.Remove("thinkingConfig");
                }
                if (currentUser?.Temperature != null)
                {
                    genConfig["temperature"] = currentUser.Temperature.Value;
                }
            }

            string question = payload["question"]?.ToString() ?? "";
            var contents = payload["contents"]?.AsArray();
            if (contents != null && contents.Count > 0)
            {
                var cleanContents = new JsonArray();
                foreach (var item in contents)
                {
                    if (item is JsonObject msgObj && msgObj["parts"] is JsonArray pArr && pArr.Count > 0)
                    {
                        string txt = pArr[0]?["text"]?.ToString() ?? "";
                        if (txt.Contains("Bạn đang chạy trong một ứng dụng") || 
                            txt.Contains("QUY TẮC XUẤT FILE") || 
                            txt.Contains("USER_INTENT_ADHERENCE_PROTOCOL"))
                        {
                            continue;
                        }
                    }
                    cleanContents.Add(item?.DeepClone());
                }
                contents = cleanContents;

                if (contents.Count > 0)
                {
                    var lastMsg = contents[contents.Count - 1]?.AsObject();
                    if (lastMsg != null && lastMsg["parts"] is JsonArray parts && parts.Count > 0)
                    {
                        string extracted = parts[0]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(extracted)) question = extracted;
                    }
                }
            }

            string trimmedQ = (question ?? "").Trim().ToLowerInvariant();
            bool isShortChat = string.IsNullOrWhiteSpace(trimmedQ) || 
                               trimmedQ.Length <= 10 || 
                               trimmedQ == "ê" || trimmedQ == "ko" || trimmedQ == "không" || 
                               trimmedQ == "um" || trimmedQ == "ừ" || trimmedQ == "ok" || 
                               trimmedQ == "chào" || trimmedQ == "hi" || trimmedQ == "hello";

            var historyChats = new List<Chat>();
            if (!isShortChat && userId > 0 && conversationId > 0)
            {
                try
                {
                    historyChats = await _conversationRepository.GetChatHistoryAsync(conversationId, userId, 6);
                }
                catch { }
            }

            var historyMessages = new JsonArray();
            foreach (var chat in historyChats)
            {
                string q = chat.Question ?? "";
                string a = chat.Answer ?? "";
                historyMessages.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = q } } });
                if (!string.IsNullOrEmpty(a))
                {
                    historyMessages.Add(new JsonObject { ["role"] = "model", ["parts"] = new JsonArray { new JsonObject { ["text"] = a } } });
                }
            }

            if (contents != null)
            {
                var currentContents = new JsonArray();
                foreach (var item in historyMessages) currentContents.Add(item?.DeepClone());
                foreach (var item in contents) currentContents.Add(item?.DeepClone());
                payload["contents"] = currentContents;
            }

            string lowerExportQ = (question ?? "").ToLowerInvariant();
            bool isExportFollowup = lowerExportQ.Contains("bỏ vào word") || lowerExportQ.Contains("cho vào word") || 
                                    lowerExportQ.Contains("vào word") || lowerExportQ.Contains("xuất file") || 
                                    lowerExportQ.Contains("tạo file word") || lowerExportQ.Contains("tải file word") || 
                                    lowerExportQ.Contains("lưu vào word") || lowerExportQ.Contains("xuất word") ||
                                    lowerExportQ.Contains("xuất docx") || lowerExportQ.Contains("tạo file docx");

            string currentSystemInstruction = (!isThinking && !isExportFollowup) ? FastSystemInstructionText : SystemInstructionText;
            if (currentUser != null && !string.IsNullOrWhiteSpace(currentUser.CustomInstructions))
            {
                currentSystemInstruction += "\n\n[HƯỚNG DẪN TÙY CHỈNH TỪ NGƯỜI DÙNG]:\n" + currentUser.CustomInstructions.Trim();
            }
            if (currentUser?.Temperature != null)
            {
                if (payload["generationConfig"] is JsonObject genCfg)
                {
                    genCfg["temperature"] = currentUser.Temperature.Value;
                }
                else
                {
                    payload["generationConfig"] = new JsonObject { ["temperature"] = currentUser.Temperature.Value };
                }
            }
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = currentSystemInstruction } }
            };

            var sbFull = new StringBuilder();
            await foreach (var chunk in _geminiService.StreamContentAsync(model, payload, cancellationToken))
            {
                sbFull.Append(chunk);
                yield return chunk;
            }
        }
    }
}
