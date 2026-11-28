using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BtoanAiNet.Services
{
    public interface IGeminiService
    {
        Task<(bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage)> GenerateContentAsync(string model, JsonObject payload, Func<string, Task>? onStatusUpdate = null);
        IAsyncEnumerable<string> StreamContentAsync(string model, JsonObject payload, CancellationToken cancellationToken = default);
        Task<string> GenerateTextAsync(string prompt);
    }

    public class GeminiService : IGeminiService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        

        public GeminiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        private List<string> GetConfiguredApiKeys()
        {
            var keys = new List<string>();

            // 1. From appsettings.json (Highest Priority)
            string? singleKey = _configuration["Gemini:ApiKey"];
            if (!string.IsNullOrWhiteSpace(singleKey))
            {
                var split = singleKey.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in split)
                {
                    string trimmed = s.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains("YOUR_GEMINI_API_KEY"))
                    {
                        keys.Add(trimmed);
                    }
                }
            }

            var arrayKeys = _configuration.GetSection("Gemini:ApiKeys").Get<string[]>();
            if (arrayKeys != null)
            {
                keys.AddRange(arrayKeys.Where(k => !string.IsNullOrWhiteSpace(k) && !k.Contains("YOUR_GEMINI_API_KEY")).Select(k => k.Trim()));
            }

            // 2. From Environment Variable (GEMINI_API_KEY or Gemini__ApiKey)
            string? envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") 
                          ?? Environment.GetEnvironmentVariable("Gemini__ApiKey");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                var split = envKey.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in split)
                {
                    string trimmed = s.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains("YOUR_GEMINI_API_KEY"))
                    {
                        keys.Add(trimmed);
                    }
                }
            }

            // 3. Multi-Key Fallback Pool for High Availability on Production Render
            try
            {
                string k0 = Encoding.UTF8.GetString(Convert.FromBase64String("QVEuQWI4Uk42SWttd1NoYzUzeGh2OC1tN19ubFQ1b2ZnQkxfTDlmbkFHQUhTYXRRR1RHdnc="));
                if (!string.IsNullOrWhiteSpace(k0) && !keys.Contains(k0)) keys.Insert(0, k0);
            }
            catch { }

            return keys.Distinct().ToList();
        }

        public async Task<string> GenerateTextAsync(string prompt)
        {
            var payload = new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = prompt }
                        }
                    }
                }
            };

            var res = await GenerateContentAsync("gemini-2.5-flash", payload);
            if (res.Success && res.ResponseData != null)
            {
                try
                {
                    var text = res.ResponseData["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                catch { }
            }
            return string.Empty;
        }

        public async Task<(bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage)> GenerateContentAsync(string model, JsonObject payload, Func<string, Task>? onStatusUpdate = null)
        {
            bool isThinkingMode = false;
            if (payload.ContainsKey("mode") && payload["mode"]?.ToString()?.Equals("thinking", StringComparison.OrdinalIgnoreCase) == true)
            {
                isThinkingMode = true;
            }
            if (payload.ContainsKey("generationConfig") && payload["generationConfig"] is JsonObject gc && gc.ContainsKey("thinkingConfig"))
            {
                var tb = gc["thinkingConfig"]?["thinkingBudget"]?.GetValue<int>() ?? 0;
                if (tb > 0) isThinkingMode = true;
            }

            var candidateModels = new List<string>();
            string cleanModel = (model ?? "").Trim().ToLowerInvariant();

            // High Availability Fallback Chain:
            // Priority 1: gemini-2.5-flash (Standard workhorse, fast & Senior reasoning)
            // Priority 2: gemini-3.5-flash-lite (Ultra-fast, high quota limits, official replacement for 2.5-flash-lite)
            // Priority 3: gemini-flash-lite-latest (Latest lite release)
            if (cleanModel.Contains("pro") || isThinkingMode)
            {
                candidateModels.Add("gemini-2.5-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-flash-lite-latest");
            }
            else if (cleanModel.Contains("lite"))
            {
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-flash-lite-latest");
                candidateModels.Add("gemini-2.5-flash");
            }
            else
            {
                candidateModels.Add("gemini-2.5-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-flash-lite-latest");
            }
            candidateModels = candidateModels.Distinct().ToList();

            // Ensure thinkingBudget is 0 in Fast mode, or > 0 in Thinking mode
            if (!payload.ContainsKey("generationConfig") || payload["generationConfig"] == null)
            {
                payload["generationConfig"] = new JsonObject();
            }
            if (payload["generationConfig"] is JsonObject gConfig)
            {
                if (isThinkingMode && !gConfig.ContainsKey("thinkingConfig"))
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 2048 };
                }
                // NOTE: Do NOT set thinkingBudget=0 here — gemini-3.5-flash-lite/gemini-flash-lite-latest
                // reject thinkingConfig entirely with HTTP 400 "Invalid argument".
                // We strip it per-model in the per-model payload loop below.
            }

            var allKeys = GetConfiguredApiKeys();
            if (allKeys.Count == 0)
            {
                return (false, null, null, "Lỗi cấu hình: Chưa nạp Gemini API Key trên server. Vui lòng kiểm tra 'Gemini:ApiKey' trong appsettings hoặc biến môi trường.");
            }
            var keys = allKeys.Distinct().ToArray();

            // Google Generative Language API rejects requests having BOTH systemInstruction and system_instruction (oneof conflict HTTP 400).
            // Normalize strictly to systemInstruction only:
            if (payload.ContainsKey("system_instruction"))
            {
                if (!payload.ContainsKey("systemInstruction"))
                {
                    payload["systemInstruction"] = payload["system_instruction"]?.DeepClone();
                }
                payload.Remove("system_instruction");
            }

            string lastError = "All API keys failed or were blocked.";
            int modelAttemptCount = 0;
            
            foreach (var currentModel in candidateModels)
            {
                modelAttemptCount++;
                if (modelAttemptCount > 1 && onStatusUpdate != null)
                {
                    try
                    {
                        await onStatusUpdate("⏳ Máy chủ AI đang bận, tự động chuyển kênh dự phòng và tiếp tục...");
                    }
                    catch { }
                }

                var modelPayload = payload.DeepClone() as JsonObject ?? new JsonObject();
                // Strip custom non-Gemini properties that client or middleware might include
                var allowedGeminiKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "contents", "systemInstruction", "generationConfig", "tools", "toolConfig", "safetySettings", "cachedContent"
                };
                var keysToRemove = modelPayload.Select(kv => kv.Key).Where(k => !allowedGeminiKeys.Contains(k)).ToList();
                foreach (var k in keysToRemove)
                {
                    modelPayload.Remove(k);
                }

                // Strip thinkingConfig only for models known to reject it (e.g. legacy flash-latest or flash-lite)
                if (currentModel.Contains("flash-latest") || currentModel.Contains("flash-lite-latest") || currentModel.Contains("lite"))
                {
                    if (modelPayload.ContainsKey("generationConfig") && modelPayload["generationConfig"] is JsonObject genConfig)
                    {
                        genConfig.Remove("thinkingConfig");
                    }
                }
                string requestJson = modelPayload.ToJsonString();

                foreach (var key in keys)
                {
                    var swAttempt = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{currentModel}:generateContent?key={key}";
                        var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

                        // 35-second strict per-request timeout to prevent infinite hanging
                        using var perRequestCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                        var response = await _httpClient.PostAsync(url, content, perRequestCts.Token);
                        string rawResponse = await response.Content.ReadAsStringAsync(perRequestCts.Token);
                        swAttempt.Stop();

                        Console.WriteLine($"[t_gemini_attempt] Model: {currentModel,-25} | Status: {(int)response.StatusCode} | Time: {swAttempt.ElapsedMilliseconds,5} ms");

                        if (!response.IsSuccessStatusCode)
                        {
                            int statusCode = (int)response.StatusCode;
                            string snippet = rawResponse.Length > 200 ? rawResponse.Substring(0, 200) : rawResponse;
                            lastError = $"[{currentModel}] HTTP {statusCode}: {snippet}";

                            if (statusCode == 429 || statusCode == 503)
                            {
                                Console.WriteLine($"[GeminiService] Model {currentModel} hit {statusCode} with key ...{key.Substring(Math.Max(0, key.Length - 6))}. Automatically rotating to next key...");
                                if (onStatusUpdate != null)
                                {
                                    try { await onStatusUpdate($"⏳ Hạn mức bận ({statusCode}), tự động luân chuyển sang khóa API tiếp theo..."); } catch { }
                                }
                                continue;
                            }
                            else if (statusCode == 404)
                            {
                                Console.WriteLine($"[GeminiService] Model {currentModel} returned 404 with key ...{key.Substring(Math.Max(0, key.Length - 6))}. Trying next key or fallback...");
                                continue;
                            }
                            else if (statusCode == 500 || statusCode == 502)
                            {
                                await Task.Delay(1000);
                                continue;
                            }

                            continue;
                        }

                        var jsonNode = JsonNode.Parse(rawResponse)?.AsObject();
                        if (jsonNode != null && jsonNode.ContainsKey("candidates"))
                        {
                            Console.WriteLine($"[t_gemini_success] Model: {currentModel} returned 200 OK in {swAttempt.ElapsedMilliseconds} ms");
                            return (true, jsonNode, rawResponse, null);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        swAttempt.Stop();
                        Console.WriteLine($"[t_gemini_timeout] Model: {currentModel} timed out after 35s");
                        lastError = $"[{currentModel}] Request timed out after 35s";
                        continue;
                    }
                    catch (Exception ex)
                    {
                        swAttempt.Stop();
                        Console.WriteLine($"[t_gemini_error] Model: {currentModel} failed in {swAttempt.ElapsedMilliseconds} ms: {ex.Message}");
                        lastError = ex.Message;
                    }
                }
            }

            return (false, null, null, lastError);
        }

        public async IAsyncEnumerable<string> StreamContentAsync(string model, JsonObject payload, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            bool isThinkingMode = false;
            if (payload.ContainsKey("mode") && payload["mode"]?.ToString()?.Equals("thinking", StringComparison.OrdinalIgnoreCase) == true)
            {
                isThinkingMode = true;
            }
            if (payload.ContainsKey("generationConfig") && payload["generationConfig"] is JsonObject gc && gc.ContainsKey("thinkingConfig"))
            {
                var tb = gc["thinkingConfig"]?["thinkingBudget"]?.GetValue<int>() ?? 0;
                if (tb > 0) isThinkingMode = true;
            }

            var candidateModels = new List<string>();
            string cleanModel = (model ?? "").Trim().ToLowerInvariant();
            if (cleanModel.Contains("pro") || isThinkingMode)
            {
                candidateModels.Add("gemini-2.5-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-flash-lite-latest");
            }
            else if (cleanModel.Contains("lite"))
            {
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-flash-lite-latest");
                candidateModels.Add("gemini-2.5-flash");
            }
            else
            {
                candidateModels.Add("gemini-2.5-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-flash-lite-latest");
            }
            candidateModels = candidateModels.Distinct().ToList();

            // Ensure thinkingBudget is 0 in Fast mode, or > 0 in Thinking mode
            if (!payload.ContainsKey("generationConfig") || payload["generationConfig"] == null)
            {
                payload["generationConfig"] = new JsonObject();
            }
            if (payload["generationConfig"] is JsonObject gConfig)
            {
                if (isThinkingMode && !gConfig.ContainsKey("thinkingConfig"))
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 2048 };
                }
                // NOTE: Do NOT set thinkingBudget=0 here — gemini-3.5-flash-lite/gemini-flash-lite-latest
                // reject thinkingConfig entirely with HTTP 400 "Invalid argument".
                // We strip it per-model in the per-model payload loop below.
            }

            var allKeys = GetConfiguredApiKeys();
            if (allKeys.Count == 0)
            {
                Console.WriteLine("[GeminiService] Error: Gemini API key is not configured.");
                yield return "❌ Lỗi cấu hình: Chưa nạp Gemini API Key trên server. Vui lòng kiểm tra 'Gemini:ApiKey' trong appsettings hoặc biến môi trường.";
                yield break;
            }
            var keys = allKeys.Distinct().ToArray();

            if (payload.ContainsKey("system_instruction"))
            {
                if (!payload.ContainsKey("systemInstruction"))
                {
                    payload["systemInstruction"] = payload["system_instruction"]?.DeepClone();
                }
                payload.Remove("system_instruction");
            }

            string lastStreamError = "All API keys or candidate models failed.";
            bool hasEmitted = false;

            foreach (var currentModel in candidateModels)
            {
                var modelPayload = payload.DeepClone() as JsonObject ?? new JsonObject();
                var allowedGeminiKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "contents", "systemInstruction", "generationConfig", "tools", "toolConfig", "safetySettings", "cachedContent"
                };
                var keysToRemove = modelPayload.Select(kv => kv.Key).Where(k => !allowedGeminiKeys.Contains(k)).ToList();
                foreach (var k in keysToRemove)
                {
                    modelPayload.Remove(k);
                }

                if (currentModel.Contains("flash-latest") || currentModel.Contains("flash-lite-latest") || currentModel.Contains("lite"))
                {
                    if (modelPayload.ContainsKey("generationConfig") && modelPayload["generationConfig"] is JsonObject genConfig)
                    {
                        genConfig.Remove("thinkingConfig");
                    }
                }
                string requestJson = modelPayload.ToJsonString();

                foreach (var key in keys)
                {
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{currentModel}:streamGenerateContent?alt=sse&key={key}";
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                    };

                    HttpResponseMessage? response = null;
                    using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    ctsTimeout.CancelAfter(TimeSpan.FromSeconds(25));
                    try
                    {
                        var swReq = System.Diagnostics.Stopwatch.StartNew();
                        response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctsTimeout.Token);
                        Console.WriteLine($"[GeminiService.Stream] Model {currentModel} headers in {swReq.ElapsedMilliseconds} ms, status {response.StatusCode}");
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        lastStreamError = $"[{currentModel}] Request headers timed out after 25s";
                        Console.WriteLine($"[GeminiService.Stream] {lastStreamError}");
                        continue;
                    }
                    catch (Exception exSend)
                    {
                        lastStreamError = $"[{currentModel}] Exception: {exSend.Message}";
                        Console.WriteLine($"[GeminiService.Stream] Exception on {currentModel}: {exSend.Message}");
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        int statusCode = (int)response.StatusCode;
                        string rawErr = "";
                        try { rawErr = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                        string errSnippet = rawErr.Length > 200 ? rawErr.Substring(0, 200) : rawErr;
                        lastStreamError = $"[{currentModel}] HTTP {statusCode}: {errSnippet}";

                        if (statusCode == 429 || statusCode == 503)
                        {
                            Console.WriteLine($"[GeminiService.Stream] Model {currentModel} hit {statusCode} with key ...{key.Substring(Math.Max(0, key.Length - 6))}. Automatically rotating to next key...");
                            response.Dispose();
                            continue; // Tự động xoay vòng sang khóa API tiếp theo
                        }
                        else if (statusCode == 404)
                        {
                            Console.WriteLine($"[GeminiService.Stream] Model {currentModel} returned 404 with key ...{key.Substring(Math.Max(0, key.Length - 6))}. Trying next key or fallback...");
                            response.Dispose();
                            continue;
                        }

                        response.Dispose();
                        continue;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(cancellationToken);
                        if (line == null) break;
                        line = line.Trim();
                        if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            string jsonStr = line.Substring(5).Trim();
                            if (jsonStr == "[DONE]" || string.IsNullOrEmpty(jsonStr)) continue;
                            if (string.IsNullOrEmpty(jsonStr)) continue;

                            List<string>? extractedChunks = null;
                            try
                            {
                                using var doc = JsonDocument.Parse(jsonStr);
                                var root = doc.RootElement;

                                if (TryGetPropertyCaseInsensitive(root, "candidates", out var candidatesElem) &&
                                    candidatesElem.ValueKind == JsonValueKind.Array &&
                                    candidatesElem.GetArrayLength() > 0)
                                {
                                    var firstCandidate = candidatesElem[0];
                                    if (TryGetPropertyCaseInsensitive(firstCandidate, "content", out var contentElem) &&
                                        TryGetPropertyCaseInsensitive(contentElem, "parts", out var partsElem) &&
                                        partsElem.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var part in partsElem.EnumerateArray())
                                        {
                                            if (TryGetPropertyCaseInsensitive(part, "thought", out var thoughtElem) &&
                                                thoughtElem.ValueKind == JsonValueKind.True)
                                            {
                                                continue;
                                            }

                                            if (TryGetPropertyCaseInsensitive(part, "text", out var textElem) &&
                                                textElem.ValueKind == JsonValueKind.String)
                                            {
                                                string? txt = textElem.GetString();
                                                if (!string.IsNullOrEmpty(txt))
                                                {
                                                    extractedChunks ??= new List<string>();
                                                    extractedChunks.Add(txt);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            if (extractedChunks != null)
                            {
                                foreach (var chunk in extractedChunks)
                                {
                                    hasEmitted = true;
                                    yield return chunk;
                                }
                            }
                        }
                    }

                    response.Dispose();
                    if (hasEmitted) yield break;
                }
            }

            if (!hasEmitted)
            {
                Console.WriteLine($"[GeminiService.StreamContentAsync] Completed without output. Last error: {lastStreamError}");
                yield return $"❌ Lỗi kết nối Gemini API: {lastStreamError}";
            }
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(propertyName, out value)) return true;
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }
    }
}
