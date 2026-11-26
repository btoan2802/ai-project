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
                // 1. Claims from Auth Cookie
                var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(claim, out int id) && id > 0) return id;

                // 2. Session
                if (HttpContext.Session != null)
                {
                    var sId = HttpContext.Session.GetInt32("user_id");
                    if (sId.HasValue && sId.Value > 0) return sId.Value;
                }

                // 3. Header X-User-Id
                if (Request.Headers.TryGetValue("X-User-Id", out var headerUid)
                    && int.TryParse(headerUid, out int hUid) && hUid > 0)
                {
                    return hUid;
                }

                // 4. Query param ?user_id=...
                if (Request.Query.TryGetValue("user_id", out var qUid)
                    && int.TryParse(qUid, out int parsedQUid) && parsedQUid > 0)
                {
                    return parsedQUid;
                }

                // 5. Cookie user_id
                if (Request.Cookies.TryGetValue("user_id", out var cookieUid)
                    && int.TryParse(cookieUid, out int cUid) && cUid > 0)
                {
                    return cUid;
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
            // 1. Strict Port Range Check (5200-5299)
            if (port < RunningAppSessionManager.MinPort || port > RunningAppSessionManager.MaxPort)
            {
                _logger.LogWarning("[Security SSRF Blocked] Yêu cầu cổng không hợp lệ: {Port}", port);
                return StatusCode(StatusCodes.Status403Forbidden, $"Cổng {port} nằm ngoài dải cổng xem trước được phép ({RunningAppSessionManager.MinPort}-{RunningAppSessionManager.MaxPort}).");
            }

            int effectiveUserId = CurrentUserId;
            var activeSession = RunningAppSessionManager.GetSession(port);
            bool isPortListening = RunningAppSessionManager.IsPortInUse(port);
            bool isPortRegistered = activeSession != null;

            // 2. Check if port is active in system or actively listening
            if (!isPortListening && !isPortRegistered)
            {
                _logger.LogWarning("[Live Preview] Cổng {Port} chưa có ứng dụng nào lắng nghe.", port);
                return Content($"<div style='font-family:sans-serif;padding:2rem;background:#1e1e1e;color:#fff;border-radius:8px;max-width:600px;margin:2rem auto;text-align:center;border:1px solid #333;'>" +
                    $"<h3 style='color:#e74c3c;'>⚠️ Ứng dụng chưa sẵn sàng trên cổng {port}</h3>" +
                    $"<p style='color:#aaa;'>Đang kết nối tới <code>http://127.0.0.1:{port}</code> nhưng server chưa phản hồi.</p>" +
                    $"<p style='color:#888;font-size:0.9rem;'>Vui lòng đảm bảo ứng dụng đã hoàn tất khởi động trong Terminal (xem log <code>Now listening on...</code>) rồi tải lại trang.</p>" +
                    $"<button onclick='location.reload()' style='background:#0e639c;color:#fff;border:none;padding:8px 16px;border-radius:4px;cursor:pointer;'>↻ Tải lại</button>" +
                    $"</div>", "text/html", Encoding.UTF8);
            }

            // 3. Auto-register active port session if process was launched via CLI in terminal
            if (!isPortRegistered && isPortListening)
            {
                RunningAppSessionManager.RegisterActiveApp(port, effectiveUserId > 0 ? effectiveUserId : 1, "app", 0);
            }

            // 4. Verify Authorization:
            // If user is authenticated, or if the port is actively running in PortRegistry of the system,
            // allow reverse proxy to forward directly to http://127.0.0.1:{port}
            bool isUserAuth = (User.Identity?.IsAuthenticated == true) || (effectiveUserId > 0);
            if (!isUserAuth && activeSession == null && !isPortListening)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, "Vui lòng đăng nhập để truy cập Live Preview.");
            }

            string relativePath = catchAll ?? string.Empty;
            string queryString = Request.QueryString.Value ?? string.Empty;
            string targetUrl = $"http://127.0.0.1:{port}/{relativePath}{queryString}";

            var client = _httpClientFactory.CreateClient("NoRedirectsClient");
            if (client == null)
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            }

            var requestMessage = new HttpRequestMessage(new HttpMethod(Request.Method), targetUrl);

            // Copy request headers (forward Cookie, Host, rewrite Referer & Origin to inner host to satisfy Antiforgery)
            foreach (var header in Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.TryAddWithoutValidation("Host", $"127.0.0.1:{port}");
                    continue;
                }
                if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                {
                    string refVal = header.Value.ToString();
                    refVal = Regex.Replace(refVal, @"https?://[^/]+/ide-preview/" + port, $"http://127.0.0.1:{port}", RegexOptions.IgnoreCase);
                    requestMessage.Headers.TryAddWithoutValidation("Referer", refVal);
                    continue;
                }
                if (header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.TryAddWithoutValidation("Origin", $"http://127.0.0.1:{port}");
                    continue;
                }
                if (header.Key.StartsWith(":") || 
                    header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    // Content headers handled below
                }
            }

            // Forwarded headers for inner Kestrel
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", Request.Host.Value);
            if (Request.HttpContext.Connection.RemoteIpAddress != null)
            {
                requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-For", Request.HttpContext.Connection.RemoteIpAddress.ToString());
            }

            // Copy body for write methods (buffering into ByteArrayContent to ensure Content-Length is sent and avoid chunked transfer for forms)
            if (HttpMethods.IsPost(Request.Method) || HttpMethods.IsPut(Request.Method) || HttpMethods.IsPatch(Request.Method))
            {
                using var ms = new MemoryStream();
                if (Request.Body != null)
                {
                    await Request.Body.CopyToAsync(ms);
                }
                byte[] bodyBytes = ms.ToArray();
                var byteContent = new ByteArrayContent(bodyBytes);
                if (!string.IsNullOrEmpty(Request.ContentType))
                {
                    byteContent.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
                }
                byteContent.Headers.ContentLength = bodyBytes.Length;
                requestMessage.Content = byteContent;
            }

            try
            {
                var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

                // Set status code
                Response.StatusCode = (int)responseMessage.StatusCode;
                Response.Headers["X-Debug-Cookie-In"] = Request.Headers.ContainsKey("Cookie") ? Request.Headers["Cookie"].ToString() : "NONE";
                Response.Headers["X-Debug-Cookie-Out"] = requestMessage.Headers.Contains("Cookie") ? string.Join("; ", requestMessage.Headers.GetValues("Cookie")) : "NONE";

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
                        header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Response.Headers[header.Key] = header.Value.ToArray();
                }

                // Handle Set-Cookie: Rewrite Path to /ide-preview/{port} and strip internal Domain
                if (responseMessage.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var cookie in setCookies)
                    {
                        string rewrittenCookie = RewriteSetCookiePath(cookie, port);
                        Response.Headers.Append("Set-Cookie", rewrittenCookie);
                    }
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
        private static string RewriteSetCookiePath(string cookie, int port)
        {
            if (string.IsNullOrWhiteSpace(cookie)) return cookie;
            string prefix = $"/ide-preview/{port}";

            // Remove Domain attribute (Domain=localhost, Domain=127.0.0.1, etc.)
            string res = Regex.Replace(cookie, @";\s*Domain=[^;]+", "", RegexOptions.IgnoreCase);

            // Rewrite Path attribute
            if (Regex.IsMatch(res, @";\s*Path=[^;]*", RegexOptions.IgnoreCase))
            {
                res = Regex.Replace(res, @";\s*Path=([^;]*)", m =>
                {
                    string p = m.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(p) || p == "/")
                    {
                        return $"; Path={prefix}";
                    }
                    if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"; Path={p}";
                    }
                    return $"; Path={prefix}/{p.TrimStart('/')}";
                }, RegexOptions.IgnoreCase);
            }
            else
            {
                res += $"; Path={prefix}";
            }

            return res;
        }
    }
}
