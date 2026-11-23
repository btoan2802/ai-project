using System.Net;
using System.Text.Json.Nodes;
using BtoanAiNet.ViewModels;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

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

        private static bool IsPrivateOrRestrictedIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
                if (ip.IsIPv4MappedToIPv6)
                {
                    ip = ip.MapToIPv4();
                }
                else
                {
                    var bytes = ip.GetAddressBytes();
                    if ((bytes[0] & 0xfe) == 0xfc) return true;
                    return false;
                }
            }

            byte[] b = ip.GetAddressBytes();
            if (b[0] == 10) return true; // 10.0.0.0/8
            if (b[0] == 172 && (b[1] >= 16 && b[1] <= 31)) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true; // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true; // 169.254.0.0/16 (Link-local / Cloud Metadata)
            if (b[0] == 127) return true; // 127.0.0.0/8 (Loopback)
            if (b[0] == 0) return true; // 0.0.0.0/8

            return false;
        }

        public async Task<JsonObject> ExportFileAsync(FileUploadRequestModel model, string webRootPath)
        {
            string content = model.Content?.Trim() ?? "";
            if (string.IsNullOrEmpty(content))
            {
                return new JsonObject { ["error"] = "no content" };
            }

            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "txt", "md", "html", "docx", "doc", "pdf" };
            string ext = model.Ext?.ToLower()?.Trim() ?? "txt";
            if (!allowedExtensions.Contains(ext)) ext = "txt";

            string filename = $"file_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N}";
            string filesDir = Path.Combine(webRootPath, "files");
            if (!Directory.Exists(filesDir)) Directory.CreateDirectory(filesDir);

            // 1. Plain Text (.txt)
            if (ext == "txt")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.txt");
                byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
                byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
                byte[] finalBytes = bom.Concat(contentBytes).ToArray();
                await System.IO.File.WriteAllBytesAsync(targetFilePath, finalBytes);
                return new JsonObject { ["url"] = $"/files/{filename}.txt" };
            }

            // 2. Markdown (.md)
            if (ext == "md")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.md");
                await System.IO.File.WriteAllTextAsync(targetFilePath, content, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.md" };
            }

            // 3. OpenXML Document (.docx / .doc)
            if (ext == "docx" || ext == "doc")
            {
                string docxFilePath = Path.Combine(filesDir, $"{filename}.docx");
                try
                {
                    using (var wordDoc = WordprocessingDocument.Create(docxFilePath, WordprocessingDocumentType.Document))
                    {
                        var mainPart = wordDoc.AddMainDocumentPart();
                        mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body());
                        var body = mainPart.Document.Body!;

                        // Header Paragraph
                        var headerPara = body.AppendChild(new Paragraph());
                        var headerRun = headerPara.AppendChild(new Run(new Text("Btoan AI Workspace - Tài liệu xuất")));
                        headerRun.RunProperties = new RunProperties(new Bold(), new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "4F46E5" }, new FontSize { Val = "32" });

                        // Subtitle
                        var subPara = body.AppendChild(new Paragraph());
                        var subRun = subPara.AppendChild(new Run(new Text($"Xuất bản: {DateTime.Now:dd/MM/yyyy HH:mm:ss}")));
                        subRun.RunProperties = new RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "64748B" }, new FontSize { Val = "20" });

                        // Horizontal separator spacing
                        body.AppendChild(new Paragraph());

                        string plain = Markdig.Markdown.ToPlainText(content);
                        var lines = plain.Split('\n');
                        foreach (var rawLine in lines)
                        {
                            string line = rawLine.TrimEnd('\r');
                            var p = body.AppendChild(new Paragraph());
                            var r = p.AppendChild(new Run(new Text(line)));

                            if (line.StartsWith("#") || line.StartsWith("=="))
                            {
                                r.RunProperties = new RunProperties(new Bold(), new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "1E293B" }, new FontSize { Val = "26" });
                            }
                            else
                            {
                                r.RunProperties = new RunProperties(new FontSize { Val = "24" });
                            }
                        }

                        mainPart.Document.Save();
                    }
                    return new JsonObject { ["url"] = $"/files/{filename}.docx" };
                }
                catch
                {
                    // Fallback to HTML-based doc if needed
                    string fallbackDoc = Path.Combine(filesDir, $"{filename}.doc");
                    await System.IO.File.WriteAllTextAsync(fallbackDoc, content, System.Text.Encoding.UTF8);
                    return new JsonObject { ["url"] = $"/files/{filename}.doc" };
                }
            }

            // 4. PDF Document (.pdf) via QuestPDF
            if (ext == "pdf")
            {
                string pdfFilePath = Path.Combine(filesDir, $"{filename}.pdf");
                string plain = Markdig.Markdown.ToPlainText(content);
                var lines = plain.Split('\n');

                var document = QuestPDF.Fluent.Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(35);
                        page.PageColor(Colors.White);
                        page.DefaultTextStyle(x => x.FontSize(11).FontColor(Colors.Grey.Darken3));

                        page.Header().BorderBottom(1).BorderColor(Colors.Indigo.Lighten2).PaddingBottom(10).Row(row =>
                        {
                            row.RelativeItem().Column(col =>
                            {
                                col.Item().Text("Btoan AI Workspace").SemiBold().FontSize(18).FontColor(Colors.Indigo.Medium);
                                col.Item().Text($"Xuất bản lúc: {DateTime.Now:dd/MM/yyyy HH:mm:ss}").FontSize(9).FontColor(Colors.Grey.Medium);
                            });
                        });

                        page.Content().PaddingVertical(15).Column(col =>
                        {
                            foreach (var rawLine in lines)
                            {
                                string line = rawLine.TrimEnd('\r');
                                if (string.IsNullOrWhiteSpace(line))
                                {
                                    col.Item().PaddingBottom(4);
                                }
                                else if (line.StartsWith("#") || line.StartsWith("=="))
                                {
                                    col.Item().PaddingTop(8).PaddingBottom(4).Text(line.TrimStart('#', '=', ' ')).Bold().FontSize(14).FontColor(Colors.Black);
                                }
                                else
                                {
                                    col.Item().PaddingBottom(2).Text(line).FontSize(11).FontColor(Colors.Grey.Darken4);
                                }
                            }
                        });

                        page.Footer().AlignCenter().Text(x =>
                        {
                            x.CurrentPageNumber();
                            x.Span(" / ");
                            x.TotalPages();
                        });
                    });
                });

                document.GeneratePdf(pdfFilePath);
                return new JsonObject { ["url"] = $"/files/{filename}.pdf" };
            }

            // 5. HTML (.html)
            if (ext == "html")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.html");
                string htmlBody = Markdig.Markdown.ToHtml(content);
                string htmlContent = $@"<!DOCTYPE html>
<html lang='vi'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
<title>Btoan AI Document</title>
<style>
body {{ font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif; padding: 40px; line-height: 1.6; max-width: 800px; margin: 0 auto; color: #1e293b; background: #f8fafc; }}
.card {{ background: white; padding: 32px; border-radius: 12px; box-shadow: 0 4px 6px -1px rgb(0 0 0 / 0.1); }}
pre, code {{ font-family: Consolas, monospace; background: #f1f5f9; padding: 4px 8px; border-radius: 4px; }}
</style>
</head>
<body>
<div class='card'>
{htmlBody}
</div>
</body>
</html>";
                await System.IO.File.WriteAllTextAsync(targetFilePath, htmlContent, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.html" };
            }

            string genericPath = Path.Combine(filesDir, $"{filename}.{ext}");
            await System.IO.File.WriteAllTextAsync(genericPath, content, System.Text.Encoding.UTF8);
            return new JsonObject { ["url"] = $"/files/{filename}.{ext}" };
        }

        public async Task<(Stream? Stream, string ContentType, string DownloadFilename)> GetDownloadProxyStreamAsync(string url, string filename)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return (null, "text/plain", filename);
            }

            // Enforce HTTP / HTTPS scheme
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return (null, "text/plain", filename);
            }

            // SSRF Protection: Resolve DNS and block private / cloud metadata / loopback IPs
            try
            {
                var hostAddresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost);
                if (hostAddresses == null || hostAddresses.Length == 0 || hostAddresses.Any(IsPrivateOrRestrictedIp))
                {
                    return (null, "text/plain", filename);
                }
            }
            catch
            {
                return (null, "text/plain", filename);
            }

            try
            {
                var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) return (null, "text/plain", filename);

                var stream = await response.Content.ReadAsStreamAsync();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                string safeName = Path.GetFileName(filename);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "download.bin";

                return (stream, contentType, safeName);
            }
            catch
            {
                return (null, "text/plain", filename);
            }
        }
    }
}
