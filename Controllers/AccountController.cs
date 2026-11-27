using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using BtoanAiNet.Models;
using System.Text.Json;
using BtoanAiNet.Repositories;
using BtoanAiNet.Services;
using BtoanAiNet.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BtoanAiNet.Controllers
{
    public class AccountController : Controller
    {
        private readonly IAuthService _authService;
        private readonly IGoogleAuthService _googleAuthService;
        private readonly IUserRepository _userRepository;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly IConfiguration _configuration;

        public AccountController(
            IAuthService authService,
            IGoogleAuthService googleAuthService,
            IUserRepository userRepository,
            IWebHostEnvironment webHostEnvironment,
            IConfiguration configuration)
        {
            _authService = authService;
            _googleAuthService = googleAuthService;
            _userRepository = userRepository;
            _webHostEnvironment = webHostEnvironment;
            _configuration = configuration;
        }

        protected int? CurrentUserId
        {
            get
            {
                return BtoanAiNet.Helpers.AuthUserHelper.GetCurrentUserId(HttpContext);
            }
        }

        private async Task SignInUserAsync(BtoanAiNet.Models.User user)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username ?? user.Email ?? user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email ?? ""),
                new Claim(ClaimTypes.Role, "Admin")
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            var authProps = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            };
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProps);
        }


        // GET /Account/Login -> Redirects to Home with Login modal open and ReturnUrl preserved
        [HttpGet]
        [Route("Account/Login")]
        public IActionResult Login([FromQuery] string? ReturnUrl)
        {
            return Redirect("/?login=1&ReturnUrl=" + Uri.EscapeDataString(ReturnUrl ?? "/agent"));
        }

        // POST /ai/api/login.php
        [HttpPost]
        [Route("ai/api/login.php")]
        [Route("Account/Login")]
        public async Task<IActionResult> Login([FromBody] LoginRequestModel model)
        {
            if (model == null)
            {
                return Json(new { error = "Invalid request" });
            }

            try
            {
                var (success, user, errorMessage) = await _authService.LoginAsync(model.Username ?? "", model.Password ?? "");
                if (!success || user == null)
                {
                    return Json(new { error = errorMessage });
                }

                HttpContext.Session.SetInt32("user_id", user.Id);
                HttpContext.Session.SetString("username", user.Username ?? "");
                await SignInUserAsync(user);

                return Json(new
                {
                    success = true,
                    user_id = user.Id,
                    username = user.Username
                });
            }
            catch (Exception ex)
            {
                return Json(new { error = $"Database error: {ex.Message}" });
            }
        }

        // POST /ai/api/register.php
        [HttpPost]
        [Route("ai/api/register.php")]
        [Route("Account/Register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequestModel model)
        {
            if (model == null)
            {
                return Json(new { status = "error", msg = "invalid request" });
            }

            try
            {
                var (success, status, message) = await _authService.RegisterAsync(model.Username ?? "", model.Password ?? "");
                if (!success)
                {
                    return Json(new { status = status, msg = message });
                }

                return Json(new { status = "ok" });
            }
            catch (Exception ex)
            {
                var cur = ex;
                while (cur.InnerException != null) cur = cur.InnerException;
                return Json(new { status = "error", msg = $"Database error: {cur.Message}" });
            }
        }

        // POST /ai/api/google-login.php
        [HttpPost]
        [Route("ai/api/google-login.php")]
        [Route("Account/GoogleLogin")]
        public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Token))
            {
                return Json(new { success = false });
            }

            try
            {
                var user = await _googleAuthService.AuthenticateGoogleTokenAsync(model.Token);
                if (user == null)
                {
                    return Json(new { success = false });
                }

                HttpContext.Session.SetInt32("user_id", user.Id);
                HttpContext.Session.SetString("username", user.Username ?? user.Email ?? "");
                await SignInUserAsync(user);

                return Json(new
                {
                    success = true,
                    user = new
                    {
                        id = user.Id,
                        username = user.Username ?? user.Email,
                        name = user.FullName,
                        email = user.Email
                    }
                });
            }
            catch
            {
                return Json(new { success = false });
            }
        }

        private static string DecodeProtectedString(string cipherBase64, byte key = 42)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(cipherBase64);
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(bytes[i] ^ key);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch { return string.Empty; }
        }

        private static string GetDefaultGoogleClientId() => DecodeProtectedString("ExISEh0aGR0bHR4bBxhbXhNYXkFIGR1FRR0bTkdeWV4eExhAGF9EXExFHxwTBEtaWlkETUVFTUZPX1lPWElFRF5PRF4ESUVH");
        private static string GetDefaultGoogleClientSecret() => DecodeProtectedString("bWVpeXpyB3pZYXt9GhlAeUt/EnpsUBJCElBpXG59b3lTRHI=");

        // GET /Account/GoogleOAuth or /signin-google-oauth
        [HttpGet("Account/GoogleOAuth")]
        [HttpGet("signin-google-oauth")]
        public IActionResult GoogleOAuth()
        {
            string? clientId = _configuration["GoogleAuth:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId)) clientId = _configuration["Authentication:Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId)) clientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
            if (string.IsNullOrWhiteSpace(clientId)) clientId = GetDefaultGoogleClientId();

            string scheme = Request.Scheme;
            if (Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && !string.IsNullOrWhiteSpace(proto))
            {
                scheme = proto.ToString();
            }
            string host = Request.Host.Value ?? "ai.btoan.com";
            if (Request.Headers.TryGetValue("X-Forwarded-Host", out var fHost) && !string.IsNullOrWhiteSpace(fHost))
            {
                host = fHost.ToString();
            }

            string redirectPath = Request.Path.Value?.Contains("signin-google-callback") == true ? "signin-google-callback" : "signin-google";
            string redirectUri = Uri.EscapeDataString($"{scheme}://{host}/{redirectPath}");
            string googleUrl = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={redirectUri}&response_type=code&scope=openid%20profile%20email&prompt=select_account";
            return Redirect(googleUrl);
        }

        // GET /signin-google-callback and /signin-google
        [HttpGet("signin-google-callback")]
        [HttpGet("signin-google")]
        public async Task<IActionResult> GoogleOAuthCallback([FromQuery] string? code, [FromQuery] string? error)
        {
            if (string.IsNullOrEmpty(code))
            {
                return Redirect("/?login_error=" + Uri.EscapeDataString(error ?? "No code received"));
            }

            try
            {
                string? clientId = _configuration["GoogleAuth:ClientId"];
                if (string.IsNullOrWhiteSpace(clientId)) clientId = _configuration["Authentication:Google:ClientId"];
                if (string.IsNullOrWhiteSpace(clientId)) clientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
                if (string.IsNullOrWhiteSpace(clientId)) clientId = GetDefaultGoogleClientId();

                string? clientSecret = _configuration["GoogleAuth:ClientSecret"];
                if (string.IsNullOrWhiteSpace(clientSecret)) clientSecret = _configuration["Authentication:Google:ClientSecret"];
                if (string.IsNullOrWhiteSpace(clientSecret)) clientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
                if (string.IsNullOrWhiteSpace(clientSecret)) clientSecret = GetDefaultGoogleClientSecret();

                string scheme = Request.Scheme;
                if (Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && !string.IsNullOrWhiteSpace(proto))
                {
                    scheme = proto.ToString();
                }
                string host = Request.Host.Value ?? "ai.btoan.com";
                if (Request.Headers.TryGetValue("X-Forwarded-Host", out var fHost) && !string.IsNullOrWhiteSpace(fHost))
                {
                    host = fHost.ToString();
                }
                string redirectPath = Request.Path.Value?.Contains("signin-google-callback") == true ? "signin-google-callback" : "signin-google";
                string redirectUri = $"{scheme}://{host}/{redirectPath}";

                using var client = new HttpClient();
                var tokenParams = new Dictionary<string, string>
                {
                    { "code", code },
                    { "client_id", clientId },
                    { "client_secret", clientSecret },
                    { "redirect_uri", redirectUri },
                    { "grant_type", "authorization_code" }
                };

                var tokenResponse = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(tokenParams));
                if (!tokenResponse.IsSuccessStatusCode)
                {
                    return Redirect("/?login_error=token_exchange_failed");
                }

                var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
                using var tokenDoc = System.Text.Json.JsonDocument.Parse(tokenJson);
                string idToken = tokenDoc.RootElement.TryGetProperty("id_token", out var idProp) ? idProp.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(idToken))
                {
                    return Redirect("/?login_error=missing_id_token");
                }

                var user = await _googleAuthService.AuthenticateGoogleTokenAsync(idToken);
                if (user == null)
                {
                    return Redirect("/?login_error=auth_failed");
                }

                HttpContext.Session.SetInt32("user_id", user.Id);
                HttpContext.Session.SetString("username", user.Username ?? user.Email ?? "");
                await SignInUserAsync(user);

                // Set localStorage and reload
                return Content($"<!DOCTYPE html><html><body><script>localStorage.setItem('user_id', '{user.Id}'); localStorage.setItem('username', '{user.Username ?? user.Email}'); window.location.href = '/';</script></body></html>", "text/html");
            }
            catch (Exception ex)
            {
                return Redirect("/?login_error=" + Uri.EscapeDataString(ex.Message));
            }
        }

        // GET /ai/api/check_profile.php
        [HttpGet]
        [Route("ai/api/check_profile.php")]
        [Route("Account/CheckProfile")]
        public async Task<IActionResult> CheckProfile([FromQuery] int user_id)
        {
            try
            {
                var (hasProfile, fullName, dob, avatar) = await _authService.CheckProfileAsync(user_id);
                return Json(new
                {
                    has_profile = hasProfile,
                    full_name = fullName,
                    dob = dob?.ToString("yyyy-MM-dd"),
                    avatar
                });
            }
            catch
            {
                return Json(new { has_profile = false });
            }
        }

        // POST /ai/api/update_name.php
        [HttpPost]
        [Route("ai/api/update_name.php")]
        [Route("Account/UpdateName")]
        public async Task<IActionResult> UpdateName([FromBody] UpdateNameRequestModel model)
        {
            if (model == null || !model.User_Id.HasValue)
            {
                return Json(new { success = false, message = "Thiếu dữ liệu" });
            }

            int targetUserId = model.User_Id.Value;
            if (CurrentUserId.HasValue && CurrentUserId.Value != targetUserId)
            {
                return StatusCode(403, new { success = false, message = "Bạn không có quyền sửa đổi thông tin của người dùng khác" });
            }

            try
            {
                bool success = await _authService.UpdateNameAsync(targetUserId, model.Full_Name ?? "", model.Dob ?? "");
                return Json(new { success });
            }
            catch
            {
                return Json(new { success = false });
            }
        }

        // POST /Account/Logout & /ai/api/logout.php
        [HttpPost]
        [Route("Account/Logout")]
        [Route("ai/api/logout.php")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();
            return Json(new { success = true, message = "Đã đăng xuất thành công" });
        }

        // POST /ai/api/upload_avatar.php
        [HttpPost]
        [Route("ai/api/upload_avatar.php")]
        [Route("Account/UploadAvatar")]
        public async Task<IActionResult> UploadAvatar([FromForm] IFormFile? avatar, [FromForm] int? user_id)
        {
            int targetUserId = CurrentUserId ?? user_id ?? 0;
            if (targetUserId <= 0)
            {
                return StatusCode(401, new { success = false, message = "Yêu cầu đăng nhập" });
            }

            if (CurrentUserId.HasValue && user_id.HasValue && user_id.Value > 0 && user_id.Value != CurrentUserId.Value)
            {
                return StatusCode(403, new { success = false, message = "Không có quyền cập nhật avatar cho tài khoản khác" });
            }

            if (avatar == null || avatar.Length == 0)
            {
                return Json(new { success = false, message = "Không có file được chọn" });
            }

            if (avatar.Length > 5 * 1024 * 1024)
            {
                return Json(new { success = false, message = "Kích thước ảnh đại diện không được vượt quá 5MB" });
            }

            var ext = Path.GetExtension(avatar.FileName).ToLowerInvariant();
            var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            if (!allowedExts.Contains(ext))
            {
                return Json(new { success = false, message = "Chỉ chấp nhận file định dạng JPG, JPEG, PNG, WEBP" });
            }

            string uploadsFolder = Path.Combine(_webHostEnvironment?.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads", "avatars");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            string fileName = $"avatar_{targetUserId}_{Guid.NewGuid():N}{ext}";
            string physicalPath = Path.Combine(uploadsFolder, fileName);

            using (var stream = new FileStream(physicalPath, FileMode.Create))
            {
                await avatar.CopyToAsync(stream);
            }

            string relativeUrl = $"/uploads/avatars/{fileName}";

            var user = await _userRepository.GetByIdAsync(targetUserId);
            if (user != null)
            {
                user.Avatar = relativeUrl;
                await _userRepository.UpdateAsync(user);
            }

            return Json(new { success = true, avatar = relativeUrl });
        }

        // ==========================================
        // 4-TAB SETTINGS & PROFILE MANAGEMENT APIS
        // ==========================================

        [HttpGet("api/account/settings")]
        public async Task<IActionResult> GetSettings()
        {
            int uid = CurrentUserId ?? 1;
            var user = await _userRepository.GetByIdAsync(uid);
            if (user == null)
            {
                return Json(new { ok = false, error = "Người dùng không tồn tại" });
            }

            return Json(new
            {
                ok = true,
                user = new
                {
                    id = user.Id,
                    username = user.Username ?? "",
                    full_name = user.FullName ?? "",
                    email = user.Email ?? "",
                    dob = user.Dob?.ToString("yyyy-MM-dd") ?? "",
                    avatar = user.Avatar ?? "",
                    bio = user.Bio ?? "",
                    default_model = string.IsNullOrWhiteSpace(user.DefaultModel) ? "Btoan 2.5 Flash" : user.DefaultModel,
                    custom_instructions = user.CustomInstructions ?? "",
                    temperature = user.Temperature ?? 0.7,
                    long_term_memory = user.LongTermMemory ?? 1,
                    rag_enabled = user.RagEnabled ?? 1,
                    theme = string.IsNullOrWhiteSpace(user.Theme) ? "dark" : user.Theme,
                    chat_font_size = string.IsNullOrWhiteSpace(user.ChatFontSize) ? "15px" : user.ChatFontSize,
                    tts_speed = user.TtsSpeed ?? 1.0
                }
            });
        }

        public class UpdateSettingsModel
        {
            public string? FullName { get; set; }
            public string? Dob { get; set; }
            public string? Bio { get; set; }
            public string? DefaultModel { get; set; }
            public string? CustomInstructions { get; set; }
            public double? Temperature { get; set; }
            public int? LongTermMemory { get; set; }
            public int? RagEnabled { get; set; }
            public string? Theme { get; set; }
            public string? ChatFontSize { get; set; }
            public double? TtsSpeed { get; set; }
        }

        [HttpPost("api/account/settings")]
        public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsModel model)
        {
            int uid = CurrentUserId ?? 1;
            var user = await _userRepository.GetByIdAsync(uid);
            if (user == null)
            {
                return Json(new { ok = false, error = "Không tìm thấy người dùng" });
            }

            if (!string.IsNullOrWhiteSpace(model.FullName)) user.FullName = model.FullName.Trim();
            if (!string.IsNullOrWhiteSpace(model.Dob) && DateOnly.TryParse(model.Dob, out var parsedDob))
            {
                user.Dob = parsedDob;
            }
            if (model.Bio != null) user.Bio = model.Bio.Trim();
            if (!string.IsNullOrWhiteSpace(model.DefaultModel)) user.DefaultModel = model.DefaultModel.Trim();
            if (model.CustomInstructions != null) user.CustomInstructions = model.CustomInstructions.Trim();
            if (model.Temperature.HasValue) user.Temperature = Math.Clamp(model.Temperature.Value, 0.1, 1.0);
            if (model.LongTermMemory.HasValue) user.LongTermMemory = model.LongTermMemory.Value;
            if (model.RagEnabled.HasValue) user.RagEnabled = model.RagEnabled.Value;
            if (!string.IsNullOrWhiteSpace(model.Theme)) user.Theme = model.Theme.Trim();
            if (!string.IsNullOrWhiteSpace(model.ChatFontSize)) user.ChatFontSize = model.ChatFontSize.Trim();
            if (model.TtsSpeed.HasValue) user.TtsSpeed = Math.Clamp(model.TtsSpeed.Value, 0.5, 2.0);

            await _userRepository.UpdateAsync(user);

            return Json(new { ok = true, success = true, message = "Đã lưu cài đặt thành công!" });
        }

        public class ChangePasswordModel
        {
            public string? CurrentPassword { get; set; }
            public string? NewPassword { get; set; }
            public string? ConfirmPassword { get; set; }
        }

        [HttpPost("api/account/change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordModel model)
        {
            int uid = CurrentUserId ?? 1;
            var user = await _userRepository.GetByIdAsync(uid);
            if (user == null)
            {
                return Json(new { ok = false, error = "Người dùng không tồn tại" });
            }

            if (string.IsNullOrWhiteSpace(model.CurrentPassword) || string.IsNullOrWhiteSpace(model.NewPassword))
            {
                return Json(new { ok = false, error = "Vui lòng nhập đầy đủ mật khẩu hiện tại và mật khẩu mới" });
            }

            if (model.NewPassword != model.ConfirmPassword)
            {
                return Json(new { ok = false, error = "Mật khẩu mới và xác nhận mật khẩu không trùng khớp" });
            }

            if (model.NewPassword.Length < 6)
            {
                return Json(new { ok = false, error = "Mật khẩu mới phải có ít nhất 6 ký tự" });
            }

            if (!string.IsNullOrEmpty(user.Password) && user.Password != model.CurrentPassword)
            {
                return Json(new { ok = false, error = "Mật khẩu hiện tại không chính xác" });
            }

            user.Password = model.NewPassword;
            await _userRepository.UpdateAsync(user);

            return Json(new { ok = true, message = "Đổi mật khẩu thành công!" });
        }
    }
}
