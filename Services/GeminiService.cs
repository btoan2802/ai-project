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

        private static readonly string[] DefaultFallbackKeys = new[]
        {
            "YOUR_GEMINI_API_KEY_HERE"
        };

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

            // 2. From Environment Variable
            string? envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey) && !envKey.Contains("YOUR_GEMINI_API_KEY")) keys.Add(envKey.Trim());

            // 3. Fallback to placeholder if configured
            if (keys.Count == 0)
            {
                keys.AddRange(DefaultFallbackKeys.Where(k => !string.IsNullOrWhiteSpace(k) && !k.Contains("YOUR_GEMINI_API_KEY")));
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

            var res = await GenerateContentAsync("gemini-3.5-flash", payload);
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
            if (string.IsNullOrWhiteSpace(model) || model == "gemini-2.5-flash") 
            {
                model = "gemini-3.5-flash";
            }

            var allKeys = GetConfiguredApiKeys();
            var keys = allKeys.Count <= 1 ? allKeys.ToArray() : allKeys.OrderBy(_ => Guid.NewGuid()).ToArray();

            string requestJson = payload.ToJsonString();
            string lastError = "All API keys failed or were blocked.";
            
            foreach (var key in keys)
            {
                try
                {
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}";
                    var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync(url, content);
                    string rawResponse = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = $"HTTP {(int)response.StatusCode}: {rawResponse}";
                        continue;
                    }

                    var jsonNode = JsonNode.Parse(rawResponse)?.AsObject();
                    if (jsonNode != null && jsonNode.ContainsKey("candidates"))
                    {
                        return (true, jsonNode, rawResponse, null);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            return (false, null, null, lastError);
        }
    }
}
