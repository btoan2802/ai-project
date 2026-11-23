using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BtoanAiNet.Services
{
    public interface IGeminiService
    {
        Task<(bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage)> GenerateContentAsync(string model, JsonObject payload);
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

        public async Task<(bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage)> GenerateContentAsync(string model, JsonObject payload)
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
                candidateModels.Add("gemini-3.6-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
            }
            else
            {
                // Fast mode: prioritize active sub-second models with thinkingBudget: 0
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-3-flash-preview");
                candidateModels.Add("gemini-3.5-flash");
                candidateModels.Add("gemini-3.6-flash");
                candidateModels.Add("gemini-2.5-flash");
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
                    gConfig.Remove("thinkingConfig");
                }
                else if (!gConfig.ContainsKey("thinkingConfig"))
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 2048 };
                }
            }

            var allKeys = GetConfiguredApiKeys();
            if (allKeys.Count == 0)
            {
                return (false, null, null, "Gemini API key is not configured. Please set 'Gemini:ApiKey' in appsettings.Development.json or environment variables.");
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
            
            foreach (var currentModel in candidateModels)
            {
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
                            if ((int)response.StatusCode == 503)
                            {
                                await Task.Delay(300);
                                var retryResponse = await _httpClient.PostAsync(url, content);
                                if (retryResponse.IsSuccessStatusCode)
                                {
                                    string retryRaw = await retryResponse.Content.ReadAsStringAsync();
                                    var retryJson = JsonNode.Parse(retryRaw)?.AsObject();
                                    if (retryJson != null && retryJson.ContainsKey("candidates"))
                                    {
                                        return (true, retryJson, retryRaw, null);
                                    }
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
                candidateModels.Add("gemini-3.6-flash");
                candidateModels.Add("gemini-3.5-flash-lite");
            }
            else
            {
                // Fast mode: prioritize active sub-second models with thinkingBudget: 0
                candidateModels.Add("gemini-3.5-flash-lite");
                candidateModels.Add("gemini-3-flash-preview");
                candidateModels.Add("gemini-3.5-flash");
                candidateModels.Add("gemini-3.6-flash");
                candidateModels.Add("gemini-2.5-flash");
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
                    gConfig.Remove("thinkingConfig");
                }
                else if (!gConfig.ContainsKey("thinkingConfig"))
                {
                    gConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 2048 };
                }
            }

            var allKeys = GetConfiguredApiKeys();
            if (allKeys.Count == 0) yield break;
            var keys = allKeys.Count <= 1 ? allKeys.ToArray() : allKeys.OrderBy(_ => Guid.NewGuid()).ToArray();

            if (payload.ContainsKey("system_instruction"))
            {
                if (!payload.ContainsKey("systemInstruction"))
                {
                    payload["systemInstruction"] = payload["system_instruction"]?.DeepClone();
                }
                payload.Remove("system_instruction");
            }

            foreach (var currentModel in candidateModels)
            {
                var modelPayload = payload.DeepClone() as JsonObject ?? new JsonObject();
                modelPayload.Remove("mode");
                modelPayload.Remove("question");
                modelPayload.Remove("user_id");
                modelPayload.Remove("conversation_id");
                modelPayload.Remove("intent");
                if (!isThinkingMode)
                {
                    modelPayload.Remove("tools");
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
                    try
                    {
                        response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        response.Dispose();
                        continue;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    bool hasEmitted = false;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(cancellationToken);
                        if (line == null) break;
                        line = line.Trim();
                        if (line.StartsWith("data: "))
                        {
                            string jsonStr = line.Substring(6).Trim();
                            if (string.IsNullOrEmpty(jsonStr)) continue;

                            JsonNode? node = null;
                            try { node = JsonNode.Parse(jsonStr); } catch { }

                            if (node is JsonObject obj && obj["candidates"] is JsonArray candidates && candidates.Count > 0)
                            {
                                if (candidates[0]?["content"]?["parts"] is JsonArray parts)
                                {
                                    foreach (var part in parts)
                                    {
                                        if (part is JsonObject pObj)
                                        {
                                            if (pObj.ContainsKey("thought") && pObj["thought"]?.GetValue<bool>() == true)
                                            {
                                                continue;
                                            }
                                            string? txt = pObj["text"]?.ToString();
                                            if (!string.IsNullOrEmpty(txt))
                                            {
                                                hasEmitted = true;
                                                yield return txt;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    response.Dispose();
                    if (hasEmitted) yield break;
                }
            }
        }
    }
}
