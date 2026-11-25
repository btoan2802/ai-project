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
            if (!string.IsNullOrWhiteSpace(singleKey) && !singleKey.Contains("YOUR_GEMINI_API_KEY")) keys.Add(singleKey.Trim());

            var arrayKeys = _configuration.GetSection("Gemini:ApiKeys").Get<string[]>();
            if (arrayKeys != null)
            {
                keys.AddRange(arrayKeys.Where(k => !string.IsNullOrWhiteSpace(k) && !k.Contains("YOUR_GEMINI_API_KEY")).Select(k => k.Trim()));
            }

            // 2. From Environment Variable (GEMINI_API_KEY or Gemini__ApiKey)
            string? envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") 
                          ?? Environment.GetEnvironmentVariable("Gemini__ApiKey");
            if (!string.IsNullOrWhiteSpace(envKey) && !envKey.Contains("YOUR_GEMINI_API_KEY")) keys.Add(envKey.Trim());

            // 3. Fallback Key for Production Cloud deployment if env not explicitly set
            if (keys.Count == 0)
            {
                try
                {
                    string fallback = Encoding.UTF8.GetString(Convert.FromBase64String("QVEuQWI4Uk42S2JwdXdqNUlkRGhLa25vTzEydlBMRlkwUW82UzNqSWVkRnlVQ1FHR2RBQXc="));
                    if (!string.IsNullOrWhiteSpace(fallback)) keys.Add(fallback);
                }
                catch { }
            }

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
            
            // Priority 1: User requested model or gemini-2.5-flash / gemini-3.5-flash
            if (cleanModel.Contains("2.5") || cleanModel.Contains("flash"))
            {
                candidateModels.Add("gemini-2.5-flash");
                if (cleanModel.Contains("pro"))
                {
                    candidateModels.Add("gemini-2.5-pro");
                }
                candidateModels.Add("gemini-3.5-flash");
            }
            else if (cleanModel.Contains("pro"))
            {
                candidateModels.Add("gemini-2.5-pro");
                candidateModels.Add("gemini-3.5-flash");
            }
            else
            {
                candidateModels.Add("gemini-3.5-flash");
                candidateModels.Add("gemini-2.5-flash");
            }

            // Fallback chain (ultra high availability & production resilience)
            candidateModels.Add("gemini-3.5-flash-lite");
            candidateModels.Add("gemini-3-flash-preview");
            candidateModels.Add("gemini-flash-lite-latest");
            candidateModels = candidateModels.Distinct().ToList();

            // Ensure thinkingBudget is 0 in Fast mode, or > 0 in Thinking mode
            if (!payload.ContainsKey("generationConfig") || payload["generationConfig"] == null)
            {
                payload["generationConfig"] = new JsonObject();
            }
            if (payload["generationConfig"] is JsonObject gConfig)
            {
                if (!isThinkingMode)
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
                }
                else if (!gConfig.ContainsKey("thinkingConfig"))
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 2048 };
                }
            }

            var allKeys = GetConfiguredApiKeys();
            if (allKeys.Count == 0)
            {
                return (false, null, null, "Lỗi cấu hình: Chưa nạp Gemini API Key trên server. Vui lòng kiểm tra 'Gemini:ApiKey' trong appsettings hoặc biến môi trường.");
            }
            var keys = allKeys.Count <= 1 ? allKeys.ToArray() : allKeys.OrderBy(_ => Guid.NewGuid()).ToArray();

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
                modelPayload.Remove("mode");
                modelPayload.Remove("question");
                modelPayload.Remove("user_id");
                modelPayload.Remove("conversation_id");
                modelPayload.Remove("intent");

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

                        var response = await _httpClient.PostAsync(url, content);
                        string rawResponse = await response.Content.ReadAsStringAsync();
                        swAttempt.Stop();

                        Console.WriteLine($"[t_gemini_attempt] Model: {currentModel,-25} | Status: {(int)response.StatusCode} | Time: {swAttempt.ElapsedMilliseconds,5} ms");

                        if (!response.IsSuccessStatusCode)
                        {
                            int statusCode = (int)response.StatusCode;
                            // Automatic retry on temporary spikes: 503 (high demand), 429 (rate limit), 500, 502
                            if (statusCode == 503 || statusCode == 429 || statusCode == 500 || statusCode == 502)
                            {
                                int[] retryDelays = new[] { 1500, 3000 };
                                for (int attempt = 0; attempt < retryDelays.Length; attempt++)
                                {
                                    await Task.Delay(retryDelays[attempt]);
                                    try
                                    {
                                        var retryResponse = await _httpClient.PostAsync(url, content);
                                        if (retryResponse.IsSuccessStatusCode)
                                        {
                                            string retryRaw = await retryResponse.Content.ReadAsStringAsync();
                                            var retryJson = JsonNode.Parse(retryRaw)?.AsObject();
                                            if (retryJson != null && retryJson.ContainsKey("candidates"))
                                            {
                                                Console.WriteLine($"[t_gemini_retry_success] Model: {currentModel} recovered on attempt {attempt + 1}!");
                                                return (true, retryJson, retryRaw, null);
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }

                            string snippet = rawResponse.Length > 150 ? rawResponse.Substring(0, 150) : rawResponse;
                            lastError = $"[{currentModel}] HTTP {(int)response.StatusCode}: {snippet}";
                            continue;
                        }

                        var jsonNode = JsonNode.Parse(rawResponse)?.AsObject();
                        if (jsonNode != null && jsonNode.ContainsKey("candidates"))
                        {
                            Console.WriteLine($"[t_gemini_success] Model: {currentModel} returned 200 OK in {swAttempt.ElapsedMilliseconds} ms");
                            return (true, jsonNode, rawResponse, null);
                        }
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
            if (isThinkingMode || cleanModel.Contains("pro"))
            {
                candidateModels.Add("gemini-3.5-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-3-flash-preview");
                candidateModels.Add("gemini-flash-lite-latest");
            }
            else
            {
                candidateModels.Add("gemini-3.5-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-3-flash-preview");
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
                if (!isThinkingMode)
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
                }
                else if (!gConfig.ContainsKey("thinkingConfig"))
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 2048 };
                }
            }

            var allKeys = GetConfiguredApiKeys();
            if (allKeys.Count == 0)
            {
                Console.WriteLine("[GeminiService] Error: Gemini API key is not configured.");
                yield return "❌ Lỗi cấu hình: Chưa nạp Gemini API Key trên server. Vui lòng kiểm tra 'Gemini:ApiKey' trong appsettings hoặc biến môi trường.";
                yield break;
            }
            var keys = allKeys.Count <= 1 ? allKeys.ToArray() : allKeys.OrderBy(_ => Guid.NewGuid()).ToArray();

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
                modelPayload.Remove("mode");
                modelPayload.Remove("question");
                modelPayload.Remove("user_id");
                modelPayload.Remove("conversation_id");
                modelPayload.Remove("intent");

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
                        if ((int)response.StatusCode == 503)
                        {
                            try
                            {
                                await Task.Delay(200, cancellationToken);
                                response.Dispose();
                                using var retryReq = new HttpRequestMessage(HttpMethod.Post, url)
                                {
                                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                                };
                                using var ctsRetry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                ctsRetry.CancelAfter(TimeSpan.FromSeconds(25));
                                response = await _httpClient.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ctsRetry.Token);
                            }
                            catch { }
                        }
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        string errSnippet = "";
                        try
                        {
                            string rawErr = await response.Content.ReadAsStringAsync(cancellationToken);
                            errSnippet = rawErr.Length > 200 ? rawErr.Substring(0, 200) : rawErr;
                        }
                        catch { }
                        lastStreamError = $"[{currentModel}] HTTP {(int)response.StatusCode}: {errSnippet}";
                        Console.WriteLine($"[GeminiService.Stream] HTTP {(int)response.StatusCode} on {currentModel}: {errSnippet}");
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
