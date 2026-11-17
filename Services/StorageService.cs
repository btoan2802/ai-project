using System.Text.Json.Nodes;
using BtoanAiNet.ViewModels;

namespace BtoanAiNet.Services
{
    public interface IStorageService
    {
        Task<JsonObject> ExportFileAsync(FileUploadRequestModel model, string webRootPath);
        Task<(Stream? Stream, string ContentType, string DownloadFilename)> GetDownloadProxyStreamAsync(string url, string filename);
    }

    public class StorageService : IStorageService
    {
        private readonly HttpClient _httpClient;

        public StorageService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<JsonObject> ExportFileAsync(FileUploadRequestModel model, string webRootPath)
        {
            string content = model.Content?.Trim() ?? "";
            if (string.IsNullOrEmpty(content))
            {
                return new JsonObject { ["error"] = "no content" };
            }

            string ext = model.Ext?.ToLower()?.Trim() ?? "txt";
            string filename = $"file_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            string filesDir = Path.Combine(webRootPath, "files");
            if (!Directory.Exists(filesDir)) Directory.CreateDirectory(filesDir);

            if (ext == "txt")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.txt");
                byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
                byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
                byte[] finalBytes = bom.Concat(contentBytes).ToArray();

                await File.WriteAllBytesAsync(targetFilePath, finalBytes);
                return new JsonObject { ["url"] = $"/files/{filename}.txt" };
            }

            if (ext == "md")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.md");
                await File.WriteAllTextAsync(targetFilePath, content, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.md" };
            }

            if (ext == "docx" || ext == "doc")
            {
                // Professional Word-compatible HTML format with styling
                string docFilePath = Path.Combine(filesDir, $"{filename}.doc");
                string escapedHtml = System.Net.WebUtility.HtmlEncode(content).Replace("\n", "<br/>");
                string wordDocContent = $@"<html xmlns:o='urn:schemas-microsoft-com:office:office' xmlns:w='urn:schemas-microsoft-com:office:word' xmlns='http://www.w3.org/TR/REC-html40'>
<head><meta charset='utf-8'><title>Tài liệu Btoan AI</title>
<!--[if gte mso 9]>
<xml><w:WordDocument><w:View>Print</w:View><w:Zoom>100</w:Zoom></w:WordDocument></xml>
<![endif]-->
<style>
body {{ font-family: 'Segoe UI', Calibri, Arial, sans-serif; font-size: 11pt; line-height: 1.6; padding: 40px; color: #1e293b; }}
h1, h2, h3 {{ color: #0f172a; margin-top: 18px; }}
p {{ margin-bottom: 12px; }}
pre, code {{ font-family: Consolas, 'Courier New', monospace; background: #f8fafc; padding: 4px 8px; border: 1px solid #e2e8f0; border-radius: 4px; }}
</style>
</head>
<body>
<div style='max-width: 800px; margin: 0 auto;'>
<div style='border-bottom: 2px solid #6366f1; padding-bottom: 10px; margin-bottom: 20px;'>
  <h2 style='color: #4f46e5; margin: 0;'>Btoan AI Workspace - Tài liệu xuất</h2>
  <small style='color: #64748b;'>Xuất bản lúc: {DateTime.Now:dd/MM/yyyy HH:mm:ss}</small>
</div>
<div>{escapedHtml}</div>
</div>
</body>
</html>";
                await File.WriteAllTextAsync(docFilePath, wordDocContent, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.doc" };
            }

            if (ext == "html")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.html");
                string htmlContent = $@"<!DOCTYPE html>
<html lang='vi'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
<title>Btoan AI Document</title>
<style>
body {{ font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif; padding: 40px; line-height: 1.6; max-width: 800px; margin: 0 auto; color: #1e293b; background: #f8fafc; }}
.card {{ background: white; padding: 32px; border-radius: 12px; box-shadow: 0 4px 6px -1px rgb(0 0 0 / 0.1); }}
</style>
</head>
<body>
<div class='card'>
{System.Net.WebUtility.HtmlEncode(content).Replace("\n", "<br/>")}
</div>
</body>
</html>";
                await File.WriteAllTextAsync(targetFilePath, htmlContent, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.html" };
            }

            string genericPath = Path.Combine(filesDir, $"{filename}.{ext}");
            await File.WriteAllTextAsync(genericPath, content, System.Text.Encoding.UTF8);
            return new JsonObject { ["url"] = $"/files/{filename}.{ext}" };
        }

        public async Task<(Stream? Stream, string ContentType, string DownloadFilename)> GetDownloadProxyStreamAsync(string url, string filename)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                return (null, "text/plain", filename);
            }

            try
            {
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) return (null, "text/plain", filename);

                var stream = await response.Content.ReadAsStreamAsync();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                string downloadName = !string.IsNullOrEmpty(filename) ? filename : Path.GetFileName(url);

                return (stream, contentType, downloadName);
            }
            catch
            {
                return (null, "text/plain", filename);
            }
        }
    }
}
