using System.Text.Json;
using System.Text.Json.Nodes;

namespace BtoanAiNet.Services
{
    public interface IGeminiService
    {
        Task<(bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage)> GenerateContentAsync(string model, JsonObject payload);
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

            var res = await GenerateContentAsync("gemini-3-flash-preview", payload);
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
            if (isThinkingMode)
            {
                if (!string.IsNullOrWhiteSpace(model)) candidateModels.Add(model);
                candidateModels.Add("gemini-3.5-flash");
                candidateModels.Add("gemini-3-flash-preview");
            }
            else
            {
                // Fast mode: prioritize ultra-fast models with thinkingBudget: 0 (1-shot 200 OK under 1.5s)
                if (!string.IsNullOrWhiteSpace(model) &&
                    !model.Equals("gemini-3.5-flash", StringComparison.OrdinalIgnoreCase) &&
                    !model.Equals("gemini-3.5-flash-lite", StringComparison.OrdinalIgnoreCase) &&
                    !model.Equals("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) &&
                    !model.Equals("gemini-2.5-flash-lite", StringComparison.OrdinalIgnoreCase) &&
                    !model.Equals("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase))
                {
                    candidateModels.Add(model);
                }
                candidateModels.Add("gemini-3.1-flash-lite");
                candidateModels.Add("gemini-3.1-flash-lite-preview");
                candidateModels.Add("gemini-3-flash-preview");
            }
            candidateModels = candidateModels.Distinct().ToList();

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

                // Strip thinkingConfig only for models known to reject it (e.g. legacy flash-latest)
                if (currentModel.Contains("flash-latest") || currentModel.Contains("flash-lite-latest"))
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
    }
}
