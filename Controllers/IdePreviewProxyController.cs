using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BtoanAiNet.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BtoanAiNet.Controllers
{
    [ApiController]
    public class IdePreviewProxyController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<IdePreviewProxyController> _logger;

        public IdePreviewProxyController(IHttpClientFactory httpClientFactory, ILogger<IdePreviewProxyController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        private int CurrentUserId
        {
            get
            {
                var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(claim, out int id) && id > 0) return id;

                if (HttpContext.Session != null)
                {
                    var sId = HttpContext.Session.GetInt32("user_id");
                    if (sId.HasValue && sId.Value > 0) return sId.Value;
                }

                if (User.Identity?.IsAuthenticated == true) return 1;
                return 0;
            }
        }

        [Route("ide-preview/{port:int}/{**catchAll}")]
        [HttpGet]
        [HttpPost]
        [HttpPut]
        [HttpDelete]
        [HttpPatch]
        [HttpHead]
        [HttpOptions]
        public async Task<IActionResult> ProxyRequest(int port, string? catchAll)
        {
            // 1. Strict Authentication Check
            if (User.Identity?.IsAuthenticated != true && CurrentUserId <= 0)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, "Vui lòng đăng nhập để truy cập Live Preview.");
            }

            // 2. Strict Port Range Check (5200-5299)
            if (port < RunningAppSessionManager.MinPort || port > RunningAppSessionManager.MaxPort)
            {
                _logger.LogWarning("[Security SSRF Blocked] Yêu cầu cổng không hợp lệ: {Port}", port);
                return StatusCode(StatusCodes.Status403Forbidden, $"Cổng {port} nằm ngoài dải cổng xem trước được phép (5200-5299).");
            }

            // 3. Strict Port Authorization Check (Anti-SSRF)
            if (!RunningAppSessionManager.IsPortAuthorized(port, CurrentUserId))
            {
                _logger.LogWarning("[Security SSRF Blocked] Người dùng {UserId} cố truy cập cổng chưa cấp phép {Port}", CurrentUserId, port);
                return StatusCode(StatusCodes.Status403Forbidden, $"Cổng {port} không thuộc phiên ứng dụng của bạn hoặc ứng dụng chưa chạy.");
            }

            string relativePath = catchAll ?? string.Empty;
            string queryString = Request.QueryString.Value ?? string.Empty;
            string targetUrl = $"http://127.0.0.1:{port}/{relativePath}{queryString}";

            var client = _httpClientFactory.CreateClient("NoRedirectsClient");
            if (client == null)
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            }

            var requestMessage = new HttpRequestMessage(new HttpMethod(Request.Method), targetUrl);

            // Copy request headers
            foreach (var header in Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.TryAddWithoutValidation("Host", $"127.0.0.1:{port}");
                    continue;
                }
                if (header.Key.StartsWith(":") || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    // Content headers
                }
            }

            // Copy body for write methods
            if (HttpMethods.IsPost(Request.Method) || HttpMethods.IsPut(Request.Method) || HttpMethods.IsPatch(Request.Method))
            {
                if (Request.Body != null && Request.ContentLength > 0)
                {
                    var streamContent = new StreamContent(Request.Body);
                    if (!string.IsNullOrEmpty(Request.ContentType))
                    {
                        streamContent.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
                    }
                    requestMessage.Content = streamContent;
                }
            }

            try
            {
                var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

                // Set status code
                Response.StatusCode = (int)responseMessage.StatusCode;

                // Handle Redirects (Rewrite Location header)
                if (responseMessage.Headers.Location != null)
                {
                    string loc = responseMessage.Headers.Location.ToString();
                    if (loc.StartsWith($"http://127.0.0.1:{port}/", StringComparison.OrdinalIgnoreCase))
                    {
                        loc = $"/ide-preview/{port}/" + loc.Substring($"http://127.0.0.1:{port}/".Length);
                    }
                    else if (loc.StartsWith($"http://localhost:{port}/", StringComparison.OrdinalIgnoreCase))
                    {
                        loc = $"/ide-preview/{port}/" + loc.Substring($"http://localhost:{port}/".Length);
                    }
                    else if (loc.StartsWith("/"))
                    {
                        loc = $"/ide-preview/{port}" + loc;
                    }
                    Response.Headers["Location"] = loc;
                }

                // Copy response headers
                foreach (var header in responseMessage.Headers)
                {
                    if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Response.Headers[header.Key] = header.Value.ToArray();
                }

                foreach (var header in responseMessage.Content.Headers)
                {
                    if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    Response.Headers[header.Key] = header.Value.ToArray();
                }

                string contentType = responseMessage.Content.Headers.ContentType?.MediaType ?? string.Empty;

                // If HTML response, rewrite absolute URLs to route through /ide-preview/{port}/
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    string html = await responseMessage.Content.ReadAsStringAsync();
                    string prefix = $"/ide-preview/{port}";

                    // Rewrite href="/...", src="/...", action="/..."
                    string rewritten = Regex.Replace(html, @"\b(href|src|action)=[""']/(?!/|ide-preview)([^""']*)[""']",
                        m => $"{m.Groups[1].Value}=\"{prefix}/{m.Groups[2].Value}\"");

                    // Inject base tag if not present
                    if (!rewritten.Contains("<base ", StringComparison.OrdinalIgnoreCase))
                    {
                        rewritten = Regex.Replace(rewritten, @"(<head[^>]*>)",
                            $"$1\n    <base href=\"{prefix}/\">\n", RegexOptions.IgnoreCase);
                    }

                    byte[] modifiedBytes = Encoding.UTF8.GetBytes(rewritten);
                    Response.ContentType = "text/html; charset=utf-8";
                    Response.ContentLength = modifiedBytes.Length;
                    await Response.Body.WriteAsync(modifiedBytes);
                }
                else
                {
                    await responseMessage.Content.CopyToAsync(Response.Body);
                }

                return new EmptyResult();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Reverse proxy connection failed to 127.0.0.1:{Port}", port);
                return Content($"<div style='font-family:sans-serif;padding:2rem;background:#1e1e1e;color:#fff;border-radius:8px;max-width:600px;margin:2rem auto;text-align:center;border:1px solid #333;'>" +
                    $"<h3 style='color:#e74c3c;'>⚠️ Ứng dụng chưa sẵn sàng trên cổng {port}</h3>" +
                    $"<p style='color:#aaa;'>Đang kết nối tới <code>http://127.0.0.1:{port}</code> nhưng server chưa phản hồi.</p>" +
                    $"<p style='color:#888;font-size:0.9rem;'>Vui lòng đảm bảo ứng dụng đã hoàn tất khởi động trong Terminal (xem log <code>Now listening on...</code>) rồi tải lại trang.</p>" +
                    $"<button onclick='location.reload()' style='background:#0e639c;color:#fff;border:none;padding:8px 16px;border-radius:4px;cursor:pointer;'>↻ Tải lại</button>" +
                    $"</div>", "text/html", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reverse proxy internal error on port {Port}", port);
                return StatusCode(StatusCodes.Status502BadGateway, $"Proxy Error: {ex.Message}");
            }
        }
    }
}
