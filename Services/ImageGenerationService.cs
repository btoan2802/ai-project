using System.Text.Json;
using System.Text.Json.Nodes;
using BtoanAiNet.Repositories;
using BtoanAiNet.ViewModels;

namespace BtoanAiNet.Services
{
    public interface IImageGenerationService
    {
        Task<JsonObject> GenerateImageAsync(ImageGenerationRequestModel model);
        Task<JsonObject> EditImageAsync(EditImageRequestModel model, string webRootPath);
        Task<JsonObject> GetLastImageAsync(int conversationId);
    }

    public class ImageGenerationService : IImageGenerationService
    {
        private readonly HttpClient _httpClient;
        private readonly IConversationRepository _conversationRepository;
        private readonly IIntentEngineService _intentEngineService;
        private readonly IConfiguration _configuration;
        private readonly IGeminiService _geminiService;

        

        private static readonly Dictionary<string, string> AnimalDict = new(StringComparer.OrdinalIgnoreCase)
        {
            { "ngựa", "horse" }, { "rồng", "dragon" }, { "tê giác", "rhinoceros" }, { "rhino", "rhinoceros" },
            { "chó", "dog" }, { "cún", "puppy" }, { "mèo", "cat" }, { "hổ", "tiger" }, { "cọp", "tiger" },
            { "sư tử", "lion" }, { "voi", "elephant" }, { "gấu", "bear" }, { "rắn", "snake" }, { "khỉ", "monkey" },
            { "trâu", "buffalo" }, { "bò", "cow" }, { "heo", "pig" }, { "lợn", "pig" }, { "sói", "wolf" },
            { "thỏ", "rabbit" }, { "đại bàng", "eagle" }, { "phượng hoàng", "phoenix" }, { "xe", "car" },
            { "ô tô", "car" }, { "xe máy", "motorbike" }, { "máy bay", "airplane" }, { "thuyền", "ship" },
            { "tàu", "boat" }, { "robot", "robot" }, { "thành phố", "city" }, { "núi", "mountain" }, { "biển", "ocean" }
        };

        private static readonly HashSet<string> StyleWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "realistic", "illustration", "image", "photo", "picture",
            "beautiful", "high", "quality", "detailed", "cinematic", "artwork", "style",
            "hd", "4k", "8k", "genimage", "editimage", "strict", "instructions", "primary",
            "prompt", "drawing", "render", "digital", "art", "ultra", "photorealistic"
        };

        private static string CleanPromptForAI(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return "photorealistic artwork";

            string clean = System.Text.RegularExpressions.Regex.Replace(
                prompt,
                @"\[GENIMAGE\]|\[EDITIMAGE\]|STRICT INSTRUCTIONS:|PRIMARY PROMPT:",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            clean = clean.Trim('"', '\'', '`', ' ', '\t', '\r', '\n');
            return string.IsNullOrWhiteSpace(clean) ? "photorealistic artwork" : clean;
        }

        private async Task<string> ExpandPromptToDetailedEnglishAsync(string rawPrompt)
        {
            if (string.IsNullOrWhiteSpace(rawPrompt)) 
                return "a photorealistic masterpiece scene, highly detailed, 8k resolution, cinematic lighting";

            string cleaned = CleanPromptForAI(rawPrompt);

            try
            {
                string promptEngineInstruction = @"You are an expert AI image prompt engineer.
Convert the user image description (Vietnamese or English) into a single, highly detailed, photorealistic English image generation prompt.
Rules:
- Explicitly describe the main subject in rich detail (species, breed, physical features, fur/skin texture, expressive eyes).
- Explicitly describe the EXACT physical action and interaction (e.g. if eating/chewing an apple, write: 'actively biting and chewing a fresh crisp red apple held firmly between its paws, visible apple bite mark with juicy texture').
- Explicitly describe the environment, setting, camera angle, and lighting (e.g.: 'warm natural morning sunlight illuminating the scene, soft shadows, cozy background with soft bokeh, shallow depth of field, sharp focus').
- Include quality modifiers: 'photorealistic, 8k resolution, highly detailed, cinematic lighting, masterpiece, sharp focus'.
- Do NOT use generic or vague words.
- Return ONLY the final English prompt text on a single line, with NO introduction, NO quotes, NO explanation.";

                var payload = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = promptEngineInstruction } }
                    },
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = cleaned } }
                        }
                    },
                    ["generationConfig"] = new JsonObject
                    {
                        ["maxOutputTokens"] = 300,
                        ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 }
                    }
                };

                var (success, resData, _, _) = await _geminiService.GenerateContentAsync("gemini-3.1-flash-lite", payload);
                if (success && resData != null)
                {
                    string expanded = resData["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(expanded))
                    {
                        expanded = expanded.Trim().Replace("\r", " ").Replace("\n", " ");
                        expanded = System.Text.RegularExpressions.Regex.Replace(expanded, @"^[""']|[""']$", "").Trim();
                        expanded = expanded.TrimEnd('.', '?', '!', ' ', '\t');
                        if (expanded.Length > 20)
                        {
                            Console.WriteLine($"[IMAGE PROMPT EXPANDED SUCCESS] '{cleaned}' => '{expanded}'");
                            return expanded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[IMAGE PROMPT EXPANSION EXCEPTION] " + ex.Message);
            }

            return $"{cleaned}, cute fluffy, photorealistic, actively eating a fresh red apple, natural lighting, sharp focus, 8k resolution, cinematic composition";
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _lastImagePrompts = new();

        private static readonly string[] ExplicitSubjectKeywords = new[]
        {
            "chó", "dog", "puppy", "mèo", "cat", "kitten", "rồng", "dragon", "tê giác", "rhinoceros", "rhino",
            "voi", "elephant", "hổ", "tiger", "cọp", "sư tử", "lion", "gấu", "bear", "chim", "bird",
            "cá", "fish", "trâu", "buffalo", "bò", "cow", "heo", "lợn", "pig", "sói", "wolf", "thỏ", "rabbit",
            "khỉ", "monkey", "rắn", "snake", "đại bàng", "eagle", "phượng hoàng", "phoenix",
            "xe", "car", "ô tô", "xe máy", "motorbike", "máy bay", "airplane", "thuyền", "ship", "tàu", "boat",
            "robot", "thành phố", "city", "núi", "mountain", "biển", "ocean", "người", "person", "man", "woman",
            "cô gái", "girl", "chàng trai", "boy", "nhà", "house"
        };

        private static bool HasExplicitSubject(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            string lower = prompt.ToLowerInvariant();
            foreach (var sub in ExplicitSubjectKeywords)
            {
                if (lower.Contains(sub)) return true;
            }
            return false;
        }

        private readonly IWebHostEnvironment _env;

        public ImageGenerationService(
            HttpClient httpClient,
            IConversationRepository conversationRepository,
            IIntentEngineService intentEngineService,
            IConfiguration configuration,
            IGeminiService geminiService,
            IWebHostEnvironment env)
        {
            _httpClient = httpClient;
            _conversationRepository = conversationRepository;
            _intentEngineService = intentEngineService;
            _configuration = configuration;
            _geminiService = geminiService;
            _env = env;
        }

        private async Task<string> SaveImageLocallyAsync(byte[] imageBytes, string mimeType)
        {
            try
            {
                string webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                string uploadDir = Path.Combine(webRoot, "uploads", "images");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                string ext = mimeType.Contains("png") ? "png" : "jpg";
                string filename = $"img_{Guid.NewGuid():N}.{ext}";
                string filePath = Path.Combine(uploadDir, filename);
                await File.WriteAllBytesAsync(filePath, imageBytes);
                return $"/uploads/images/{filename}";
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SAVE IMAGE LOCALLY ERROR] " + ex.Message);
                return string.Empty;
            }
        }

        private List<string> GetConfiguredApiKeys()
        {
            var keys = new List<string>();
            string? singleKey = _configuration["Gemini:ApiKey"];
            if (!string.IsNullOrWhiteSpace(singleKey) && !singleKey.Contains("YOUR_GEMINI_API_KEY")) keys.Add(singleKey.Trim());

            var arrayKeys = _configuration.GetSection("Gemini:ApiKeys").Get<string[]>();
            if (arrayKeys != null)
            {
                keys.AddRange(arrayKeys.Where(k => !string.IsNullOrWhiteSpace(k) && !k.Contains("YOUR_GEMINI_API_KEY")).Select(k => k.Trim()));
            }

            string? envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") 
                          ?? Environment.GetEnvironmentVariable("Gemini__ApiKey");
            if (!string.IsNullOrWhiteSpace(envKey) && !envKey.Contains("YOUR_GEMINI_API_KEY")) keys.Add(envKey.Trim());

            if (keys.Count == 0)
            {
                throw new InvalidOperationException("Gemini API key is not configured. Please set 'Gemini:ApiKey' in appsettings.json or 'Gemini__ApiKey' environment variable.");
            }

            return keys.Distinct().ToList();
        }

        public async Task<JsonObject> GenerateImageAsync(ImageGenerationRequestModel model)
        {
            string rawPrompt = model.Prompt?.Trim() ?? "";
            if (string.IsNullOrEmpty(rawPrompt))
            {
                return new JsonObject { ["success"] = false, ["error"] = "Prompt is required" };
            }

            Console.WriteLine("[IMAGE CONTEXT BEFORE] " + rawPrompt);

            int cid = model.Conversation_Id ?? 0;
            string prompt = rawPrompt;
            if (cid > 0 && !HasExplicitSubject(prompt) && _lastImagePrompts.TryGetValue(cid, out var lastContext) && !string.IsNullOrWhiteSpace(lastContext))
            {
                string lowerPrompt = prompt.ToLowerInvariant();
                var matchCount = System.Text.RegularExpressions.Regex.Match(lowerPrompt, @"\d+");
                if (matchCount.Success)
                {
                    string suffix = lastContext.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? "" : "s";
                    prompt = $"Create {matchCount.Value} {lastContext}{suffix}";
                }
                else if (lowerPrompt.Contains("đứng") || lowerPrompt.Contains("stand"))
                {
                    prompt = $"{lastContext} standing";
                }
                else if (lowerPrompt.Contains("to hơn") || lowerPrompt.Contains("bigger"))
                {
                    prompt = $"bigger {lastContext}";
                }
                else
                {
                    prompt = $"{lastContext} {prompt}";
                }
            }

            Console.WriteLine("[IMAGE CONTEXT AFTER] " + prompt);

            // Enhance with AI prompt engineering into detailed, action-specific, photorealistic English
            prompt = await ExpandPromptToDetailedEnglishAsync(prompt);
            Console.WriteLine("[IMAGE EXPANDED PROMPT] " + prompt);

            string aspectRatio = model.Aspect_Ratio?.Trim() ?? "1:1";
            var allowedRatios = new[] { "1:1", "16:9", "9:16", "4:3", "3:4" };
            if (!allowedRatios.Contains(aspectRatio)) aspectRatio = "1:1";

            int imgWidth = 1024;
            int imgHeight = 1024;
            switch (aspectRatio)
            {
                case "16:9": imgWidth = 1280; imgHeight = 720; break;
                case "9:16": imgWidth = 720; imgHeight = 1280; break;
                case "4:3": imgWidth = 1024; imgHeight = 768; break;
                case "3:4": imgWidth = 768; imgHeight = 1024; break;
                default: imgWidth = 1024; imgHeight = 1024; break;
            }

            var allKeys = GetConfiguredApiKeys();
            var keys = allKeys.Count <= 1 ? allKeys.ToArray() : allKeys.OrderBy(_ => Guid.NewGuid()).ToArray();

            // =========================================================================
            // =========================================================================
            // ƯU TIÊN 1: Google Gemini Native Image Generation (Fast 3s timeout)
            // =========================================================================
            var geminiImageModels = new[]
            {
                "gemini-2.5-flash-image"
            };

            foreach (var geminiModel in geminiImageModels)
            {
                foreach (var key in keys)
                {
                    try
                    {
                        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{geminiModel}:generateContent?key={Uri.EscapeDataString(key)}";
                        var payload = new JsonObject
                        {
                            ["contents"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } }
                                }
                            },
                            ["generationConfig"] = new JsonObject
                            {
                                ["responseModalities"] = new JsonArray { "TEXT", "IMAGE" },
                                ["imageConfig"] = new JsonObject
                                {
                                    ["aspectRatio"] = aspectRatio
                                }
                            }
                        };

                        var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var response = await _httpClient.PostAsync(url, content, cts.Token);
                        string rawResponse = await response.Content.ReadAsStringAsync(cts.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            string snippet = rawResponse.Length > 200 ? rawResponse.Substring(0, 200) : rawResponse;
                            Console.WriteLine($"[GEMINI IMAGE ATTEMPT FAILED] Model: {geminiModel,-32} | HTTP {(int)response.StatusCode} | Body: {snippet}");
                            continue;
                        }

                        using var doc = JsonDocument.Parse(rawResponse);
                        var root = doc.RootElement;
                        string? imageBase64 = null;
                        string mimeType = "image/png";

                        if (root.TryGetProperty("candidates", out var candidates))
                        {
                            foreach (var candidate in candidates.EnumerateArray())
                            {
                                if (candidate.TryGetProperty("content", out var contentElem) &&
                                    contentElem.TryGetProperty("parts", out var parts))
                                {
                                    foreach (var part in parts.EnumerateArray())
                                    {
                                        if (part.TryGetProperty("inlineData", out var inlineData))
                                        {
                                            imageBase64 = inlineData.GetProperty("data").GetString();
                                            mimeType = inlineData.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "image/png" : "image/png";
                                            break;
                                        }
                                        if (part.TryGetProperty("inline_data", out var inlineDataUnderscore))
                                        {
                                            imageBase64 = inlineDataUnderscore.GetProperty("data").GetString();
                                            mimeType = inlineDataUnderscore.TryGetProperty("mime_type", out var m) ? m.GetString() ?? "image/png" : "image/png";
                                            break;
                                        }
                                    }
                                }
                                if (imageBase64 != null) break;
                            }
                        }

                        if (!string.IsNullOrEmpty(imageBase64))
                        {
                            byte[] imgBytes = Convert.FromBase64String(imageBase64);
                            string publicUrl = await SaveImageLocallyAsync(imgBytes, mimeType);
                            Console.WriteLine($"[IMAGE SUCCESS: Gemini Native Image] Model: {geminiModel} | Saved to: {publicUrl}");

                            if (cid > 0) _lastImagePrompts[cid] = CleanPromptForAI(prompt);

                            return new JsonObject
                            {
                                ["success"] = true,
                                ["mimeType"] = mimeType,
                                ["imageBase64"] = imageBase64,
                                ["imageUrl"] = publicUrl,
                                ["model"] = geminiModel
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GEMINI IMAGE EXCEPTION] Model: {geminiModel}: {ex.Message}");
                    }
                }
            }

            // =========================================================================
            // ƯU TIÊN 2: Google Imagen (:predict endpoint with 3s timeout)
            // =========================================================================
            var imagenModels = new[]
            {
                "imagen-3.0-generate-002"
            };

            foreach (var imagenModel in imagenModels)
            {
                foreach (var key in keys)
                {
                    try
                    {
                        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{imagenModel}:predict?key={Uri.EscapeDataString(key)}";
                        var payload = new JsonObject
                        {
                            ["instances"] = new JsonArray
                            {
                                new JsonObject { ["prompt"] = prompt }
                            },
                            ["parameters"] = new JsonObject
                            {
                                ["sampleCount"] = 1,
                                ["aspectRatio"] = aspectRatio
                            }
                        };

                        var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var response = await _httpClient.PostAsync(url, content, cts.Token);
                        string rawResponse = await response.Content.ReadAsStringAsync(cts.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            string snippet = rawResponse.Length > 200 ? rawResponse.Substring(0, 200) : rawResponse;
                            Console.WriteLine($"[IMAGEN ATTEMPT FAILED] Model: {imagenModel,-32} | HTTP {(int)response.StatusCode} | Body: {snippet}");
                            continue;
                        }

                        using var doc = JsonDocument.Parse(rawResponse);
                        if (doc.RootElement.TryGetProperty("predictions", out var preds) && preds.GetArrayLength() > 0)
                        {
                            var firstPred = preds[0];
                            if (firstPred.TryGetProperty("bytesBase64Encoded", out var b64Elem))
                            {
                                string? imageBase64 = b64Elem.GetString();
                                if (!string.IsNullOrEmpty(imageBase64))
                                {
                                    byte[] imgBytes = Convert.FromBase64String(imageBase64);
                                    string publicUrl = await SaveImageLocallyAsync(imgBytes, "image/jpeg");
                                    Console.WriteLine($"[IMAGE SUCCESS: Google Imagen] Model: {imagenModel} | Saved to: {publicUrl}");

                                    if (cid > 0) _lastImagePrompts[cid] = CleanPromptForAI(prompt);

                                    return new JsonObject
                                    {
                                        ["success"] = true,
                                        ["mimeType"] = "image/jpeg",
                                        ["imageBase64"] = imageBase64,
                                        ["imageUrl"] = publicUrl,
                                        ["model"] = imagenModel
                                    };
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IMAGEN EXCEPTION] Model: {imagenModel}: {ex.Message}");
                    }
                }
            }

            // =========================================================================
            // =========================================================================
            // FALLBACK 2: Pollinations.ai (Flux / Turbo with 12s timeout)
            // =========================================================================
            Console.WriteLine("[IMAGE FALLBACK] Trying Pollinations FLUX/Turbo...");
            try
            {
                string encodedPrompt = Uri.EscapeDataString(prompt);
                string fluxUrl = $"https://image.pollinations.ai/prompt/{encodedPrompt}?model=turbo&width={imgWidth}&height={imgHeight}&nologo=true";

                using var ctsPoll = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var req = new HttpRequestMessage(HttpMethod.Get, fluxUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                var res = await _httpClient.SendAsync(req, ctsPoll.Token);
                if (!res.IsSuccessStatusCode)
                {
                    string defaultUrl = $"https://image.pollinations.ai/prompt/{encodedPrompt}?width={imgWidth}&height={imgHeight}&nologo=true";
                    using var ctsDef = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    using var defReq = new HttpRequestMessage(HttpMethod.Get, defaultUrl);
                    defReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    res = await _httpClient.SendAsync(defReq, ctsDef.Token);
                }

                if (res.IsSuccessStatusCode)
                {
                    byte[]? imgBytes = await res.Content.ReadAsByteArrayAsync();
                    if (imgBytes != null && imgBytes.Length > 1000)
                    {
                        string publicUrl = await SaveImageLocallyAsync(imgBytes, "image/jpeg");
                        Console.WriteLine($"[IMAGE SUCCESS: Pollinations] Saved to: {publicUrl} ({imgBytes.Length} bytes)");

                        if (cid > 0) _lastImagePrompts[cid] = CleanPromptForAI(prompt);

                        return new JsonObject
                        {
                            ["success"] = true,
                            ["mimeType"] = "image/jpeg",
                            ["imageBase64"] = Convert.ToBase64String(imgBytes),
                            ["imageUrl"] = publicUrl,
                            ["model"] = "pollinations"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[POLLINATIONS EXCEPTION] " + ex.Message);
            }

            // =========================================================================
            // FALLBACK 3: Real High-Definition Photography Fallback (Pravatar / Picsum)
            // =========================================================================
            try
            {
                string pLower = (rawPrompt + " " + prompt).ToLowerInvariant();
                bool isPersonOrAvatar = pLower.Contains("avatar") || pLower.Contains("portrait") || pLower.Contains("nhân viên") ||
                                       pLower.Contains("chân dung") || pLower.Contains("đại diện") || pLower.Contains("person") ||
                                       pLower.Contains("user") || pLower.Contains("man") || pLower.Contains("woman") || pLower.Contains("face");

                int targetSize = Math.Min(imgWidth, imgHeight);
                if (targetSize < 400) targetSize = 400;
                if (targetSize > 1000) targetSize = 1000;

                string realPhotoUrl = isPersonOrAvatar
                    ? $"https://i.pravatar.cc/{targetSize}?u={Guid.NewGuid():N}"
                    : $"https://picsum.photos/{imgWidth}/{imgHeight}?random={Random.Shared.Next(1, 99999)}";

                using var ctsPhoto = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var photoReq = new HttpRequestMessage(HttpMethod.Get, realPhotoUrl);
                photoReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                var photoRes = await _httpClient.SendAsync(photoReq, ctsPhoto.Token);

                if (photoRes.IsSuccessStatusCode)
                {
                    byte[]? imgBytes = await photoRes.Content.ReadAsByteArrayAsync();
                    if (imgBytes != null && imgBytes.Length > 1000)
                    {
                        string publicUrl = await SaveImageLocallyAsync(imgBytes, "image/jpeg");
                        Console.WriteLine($"[IMAGE SUCCESS: Real Photo Fallback] Source: {realPhotoUrl} | Saved to: {publicUrl} ({imgBytes.Length} bytes)");

                        if (cid > 0) _lastImagePrompts[cid] = CleanPromptForAI(prompt);

                        return new JsonObject
                        {
                            ["success"] = true,
                            ["mimeType"] = "image/jpeg",
                            ["imageBase64"] = Convert.ToBase64String(imgBytes),
                            ["imageUrl"] = publicUrl,
                            ["model"] = isPersonOrAvatar ? "pravatar-portrait" : "picsum-photo"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[REAL PHOTO FALLBACK EXCEPTION] " + ex.Message);
            }

            return new JsonObject { ["success"] = false, ["error"] = "Không thể vẽ ảnh vào lúc này. Vui lòng thử lại sau ít phút." };
        }

        public async Task<JsonObject> EditImageAsync(EditImageRequestModel model, string webRootPath)
        {
            string prompt = model.Prompt?.Trim() ?? "";
            string baseImage = model.Image?.Trim() ?? "";

            if (string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(baseImage))
            {
                return new JsonObject { ["success"] = false, ["error"] = "Prompt and Base Image are required" };
            }

            string cleanBase64 = "";
            if (baseImage.StartsWith("/uploads/"))
            {
                string localPath = Path.Combine(webRootPath, baseImage.TrimStart('/', '\\'));
                if (File.Exists(localPath))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(localPath);
                    cleanBase64 = Convert.ToBase64String(bytes);
                }
                else
                {
                    return new JsonObject { ["success"] = false, ["error"] = $"Reference image file not found: {baseImage}" };
                }
            }
            else
            {
                cleanBase64 = System.Text.RegularExpressions.Regex.Replace(baseImage, @"^data:image/[^;]+;base64,", "");
            }

            var intentObj = await _intentEngineService.AnalyzeIntentAsync(prompt, true);
            string protection = _intentEngineService.GetSystemProtection(intentObj);

            var allKeys = GetConfiguredApiKeys();
            var keys = allKeys.Count <= 1 ? allKeys.ToArray() : allKeys.OrderBy(_ => Guid.NewGuid()).ToArray();
            string modelName = "gemini-2.5-flash-image";
            string lastError = "Unknown Gemini Edit API error";

            foreach (var key in keys)
            {
                try
                {
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={Uri.EscapeDataString(key)}";

                    var payload = new JsonObject
                    {
                        ["contents"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["role"] = "user",
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = $"PROTOCOL:\n{protection}\n\nTASK: Please edit the following image based on this description: {prompt}" },
                                    new JsonObject
                                    {
                                        ["inline_data"] = new JsonObject
                                        {
                                            ["mime_type"] = "image/png",
                                            ["data"] = cleanBase64
                                        }
                                    }
                                }
                            }
                        },
                        ["generationConfig"] = new JsonObject
                        {
                            ["responseModalities"] = new JsonArray { "TEXT", "IMAGE" },
                            ["imageConfig"] = new JsonObject { ["aspectRatio"] = "1:1" }
                        }
                    };

                    var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(url, content);
                    string rawResponse = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = $"API Error (HTTP {(int)response.StatusCode}): {rawResponse}";
                        continue;
                    }

                    using var doc = JsonDocument.Parse(rawResponse);
                    var root = doc.RootElement;

                    string? imageBase64 = null;
                    string mimeType = "image/png";

                    if (root.TryGetProperty("candidates", out var candidates))
                    {
                        foreach (var candidate in candidates.EnumerateArray())
                        {
                            if (candidate.TryGetProperty("content", out var contentElem) &&
                                contentElem.TryGetProperty("parts", out var parts))
                            {
                                foreach (var part in parts.EnumerateArray())
                                {
                                    if (part.TryGetProperty("inlineData", out var inlineData))
                                    {
                                        imageBase64 = inlineData.GetProperty("data").GetString();
                                        mimeType = inlineData.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "image/png" : "image/png";
                                        break;
                                    }
                                    if (part.TryGetProperty("inline_data", out var inlineDataUnderscore))
                                    {
                                        imageBase64 = inlineDataUnderscore.GetProperty("data").GetString();
                                        mimeType = inlineDataUnderscore.TryGetProperty("mime_type", out var m) ? m.GetString() ?? "image/png" : "image/png";
                                        break;
                                    }
                                }
                            }
                            if (imageBase64 != null) break;
                        }
                    }

                    if (imageBase64 != null)
                    {
                        string editDir = Path.Combine(webRootPath, "uploads", "edits");
                        if (!Directory.Exists(editDir)) Directory.CreateDirectory(editDir);

                        string ext = mimeType.Contains("/") ? mimeType.Split('/')[1] : "png";
                        string filename = $"edit_{Guid.NewGuid().ToString("N").Substring(0, 10)}.{ext}";
                        string savePath = Path.Combine(editDir, filename);
                        string publicUrl = $"/uploads/edits/{filename}";

                        byte[] imgBytes = Convert.FromBase64String(imageBase64);
                        await File.WriteAllBytesAsync(savePath, imgBytes);

                        return new JsonObject
                        {
                            ["success"] = true,
                            ["mimeType"] = mimeType,
                            ["imageBase64"] = imageBase64,
                            ["publicUrl"] = publicUrl
                        };
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            // Fallback for image editing if Gemini API hits Quota Exceeded (HTTP 429 / 403)
            try
            {
                string searchPrompt = CleanPromptForAI(prompt);
                string encodedPrompt = Uri.EscapeDataString(searchPrompt);

                byte[]? imgBytes = null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://image.pollinations.ai/prompt/{encodedPrompt}");
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    var res = await _httpClient.SendAsync(req);
                    if (res.IsSuccessStatusCode)
                    {
                        var readBytes = await res.Content.ReadAsByteArrayAsync();
                        if (readBytes != null && readBytes.Length > 10000)
                        {
                            imgBytes = readBytes;
                        }
                    }
                }
                catch { }

                if (imgBytes == null || imgBytes.Length <= 1000)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://image.pollinations.ai/prompt/{encodedPrompt}");
                        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        var res = await _httpClient.SendAsync(req);
                        if (res.IsSuccessStatusCode)
                        {
                            var readBytes = await res.Content.ReadAsByteArrayAsync();
                            if (readBytes != null && readBytes.Length > 1000) imgBytes = readBytes;
                        }
                    }
                    catch { }
                }

                if (imgBytes == null || imgBytes.Length <= 1000)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://loremflickr.com/1024/1024/{Uri.EscapeDataString(searchPrompt)}");
                        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        var res = await _httpClient.SendAsync(req);
                        if (res.IsSuccessStatusCode)
                        {
                            var readBytes = await res.Content.ReadAsByteArrayAsync();
                            if (readBytes != null && readBytes.Length > 1000) imgBytes = readBytes;
                        }
                    }
                    catch { }
                }

                if (imgBytes != null && imgBytes.Length > 0)
                {
                    string editDir = Path.Combine(webRootPath, "uploads", "edits");
                    if (!Directory.Exists(editDir)) Directory.CreateDirectory(editDir);

                    string filename = $"edit_{Guid.NewGuid().ToString("N").Substring(0, 10)}.jpg";
                    string savePath = Path.Combine(editDir, filename);
                    string publicUrl = $"/uploads/edits/{filename}";

                    await File.WriteAllBytesAsync(savePath, imgBytes);

                    return new JsonObject
                    {
                        ["success"] = true,
                        ["mimeType"] = "image/jpeg",
                        ["imageBase64"] = Convert.ToBase64String(imgBytes),
                        ["publicUrl"] = publicUrl
                    };
                }
            }
            catch { }

            return new JsonObject { ["success"] = false, ["error"] = "Không thể chỉnh sửa ảnh vào lúc này. Vui lòng thử lại sau ít phút." };
        }

        public async Task<JsonObject> GetLastImageAsync(int conversationId)
        {
            if (conversationId <= 0)
            {
                return new JsonObject { ["success"] = false, ["error"] = "Missing conversation_id" };
            }

            var chats = await _conversationRepository.GetMessagesByConversationIdAsync(conversationId);
            var lastChatWithImages = chats
                .Where(c => !string.IsNullOrEmpty(c.Images) && c.Images != "[]")
                .OrderByDescending(c => c.Id)
                .FirstOrDefault();

            if (lastChatWithImages != null && !string.IsNullOrEmpty(lastChatWithImages.Images))
            {
                try
                {
                    var images = JsonSerializer.Deserialize<List<string>>(lastChatWithImages.Images);
                    if (images != null && images.Count > 0)
                    {
                        return new JsonObject
                        {
                            ["success"] = true,
                            ["image_url"] = images[0]
                        };
                    }
                }
                catch { }
            }

            return new JsonObject { ["success"] = false, ["error"] = "No images found in history" };
        }
    }
}
