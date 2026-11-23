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
        private readonly IFaceAuthService _faceAuthService;
        private readonly IGoogleAuthService _googleAuthService;
        private readonly IUserRepository _userRepository;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public AccountController(
            IAuthService authService,
            IFaceAuthService faceAuthService,
            IGoogleAuthService googleAuthService,
            IUserRepository userRepository,
            IWebHostEnvironment webHostEnvironment)
        {
            _authService = authService;
            _faceAuthService = faceAuthService;
            _googleAuthService = googleAuthService;
            _userRepository = userRepository;
            _webHostEnvironment = webHostEnvironment;
        }

        protected int? CurrentUserId
        {
            get
            {
                var claimId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(claimId, out int uid)) return uid;
                return HttpContext.Session.GetInt32("user_id");
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
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            };
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProps);
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
                return Json(new { status = "error", msg = $"Database error: {ex.Message}" });
            }
        }

        // POST /ai/api/login_face.php
        [HttpPost]
        [Route("ai/api/login_face.php")]
        [Route("Account/LoginFace")]
        public async Task<IActionResult> LoginFace([FromForm] string username, [FromForm] string descriptor)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(descriptor))
            {
                return Json(new { success = false, message = "Thiếu dữ liệu xác thực (username hoặc descriptor)" });
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var userTask = _userRepository.GetByUsernameAsync(username.Trim());
                if (await Task.WhenAny(userTask, Task.Delay(2000, cts.Token)) != userTask)
                {
                    // Fast timeout when local DB is offline
                    return Json(new { success = true, has_face_id = false, message = "Database offline (mock/guest profile)" });
                }
                var user = await userTask;
                if (user == null)
                {
                    return Json(new { success = false, message = "Tài khoản không tồn tại" });
                }

                if (user.FaceEnabled != 1 || string.IsNullOrEmpty(user.FaceDescriptor))
                {
                    return Json(new { success = false, message = "Tài khoản chưa thiết lập FaceID" });
                }

                var (isMatch, distance, message) = _faceAuthService.VerifyFace(descriptor, user.FaceDescriptor);
                if (!isMatch)
                {
                    return Json(new { success = false, message, distance });
                }

                HttpContext.Session.SetInt32("user_id", user.Id);
                HttpContext.Session.SetString("username", user.Username ?? "");
                await SignInUserAsync(user);
                await SignInUserAsync(user);

                return Json(new
                {
                    success = true,
                    message = "Đăng nhập khuôn mặt thành công",
                    username = user.Username,
                    user_id = user.Id,
                    distance
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Database error: {ex.Message}" });
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

        // GET /ai/api/get_face_profile.php
        [HttpGet]
        [Route("ai/api/get_face_profile.php")]
        [Route("Account/GetFaceProfile")]
        public async Task<IActionResult> GetFaceProfile([FromQuery] string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return Json(new { success = false, message = "Thiếu username" });
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var userTask = _userRepository.GetByUsernameAsync(username.Trim());
                if (await Task.WhenAny(userTask, Task.Delay(2000, cts.Token)) != userTask)
                {
                    // Fast timeout when local DB is offline
                    return Json(new { success = true, has_face_id = false, message = "Database offline (mock/guest profile)" });
                }
                var user = await userTask;
                if (user == null || user.FaceEnabled != 1 || string.IsNullOrEmpty(user.FaceDescriptor))
                {
                    return Json(new { success = true, has_face_id = false, message = "Chưa đăng ký FaceID" });
                }

                // SECURITY: Never return raw face_descriptor in public unauthenticated queries
                return Json(new
                {
                    success = true,
                    has_face_id = true,
                    username = user.Username,
                    face_image_url = user.FaceImageUrl
                });
            }
            catch
            {
                return Json(new { success = false, message = "Lỗi truy vấn dữ liệu" });
            }
        }

        // POST /ai/api/save_face.php
        [HttpPost]
        [Route("ai/api/save_face.php")]
        [Route("Account/SaveFace")]
        public async Task<IActionResult> SaveFace([FromForm] string? username, [FromForm] string? descriptor, [FromForm] IFormFile? image)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
            {
                return Json(new { success = false, message = "Thiếu dữ liệu sinh trắc học khuôn mặt" });
            }

            // Validate descriptor JSON array of 128 floats
            try
            {
                var descriptorArray = JsonSerializer.Deserialize<double[]>(descriptor);
                if (descriptorArray == null || descriptorArray.Length != 128)
                {
                    return Json(new { success = false, message = "Dữ liệu khuôn mặt không hợp lệ (yêu cầu 128 chiều đặc trưng)" });
                }
            }
            catch
            {
                return Json(new { success = false, message = "Định dạng vector khuôn mặt không hợp lệ" });
            }

            // Resolve target user
            BtoanAiNet.Models.User? targetUser = null;
            int? sessionUserId = HttpContext.Session.GetInt32("user_id");
            if (!sessionUserId.HasValue && User.Identity?.IsAuthenticated == true)
            {
                var claimId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(claimId, out int parsedId)) sessionUserId = parsedId;
            }

            if (sessionUserId.HasValue)
            {
                targetUser = await _userRepository.GetByIdAsync(sessionUserId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(username))
            {
                targetUser = await _userRepository.GetByUsernameAsync(username.Trim());
                if (targetUser != null && targetUser.FaceEnabled == 1 && !string.IsNullOrEmpty(targetUser.FaceDescriptor))
                {
                    return Json(new { success = false, message = "Tài khoản này đã được kích hoạt FaceID. Vui lòng đăng nhập trước khi cập nhật khuôn mặt mới." });
                }
            }

            if (targetUser == null)
            {
                return Json(new { success = false, message = "Không tìm thấy thông tin tài khoản người dùng" });
            }

            // Process & save face image safely to wwwroot/uploads/faces/
            string relativeFaceUrl = targetUser.FaceImageUrl ?? "";
            string uploadsFolder = Path.Combine(_webHostEnvironment?.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads", "faces");
            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            byte[]? imageBytes = null;
            string extension = ".jpg";

            if (image != null && image.Length > 0)
            {
                if (image.Length > 10 * 1024 * 1024)
                {
                    return Json(new { success = false, message = "Ảnh khuôn mặt không được vượt quá 10MB" });
                }
                var ext = Path.GetExtension(image.FileName).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png")
                {
                    return Json(new { success = false, message = "Chỉ chấp nhận định dạng ảnh JPG, JPEG, PNG" });
                }
                extension = ext;
                using var ms = new MemoryStream();
                await image.CopyToAsync(ms);
                imageBytes = ms.ToArray();
            }
            else if (Request.Form.ContainsKey("image"))
            {
                string rawImageStr = Request.Form["image"].ToString();
                if (!string.IsNullOrWhiteSpace(rawImageStr) && rawImageStr.Contains("base64,"))
                {
                    var base64Part = rawImageStr.Substring(rawImageStr.IndexOf("base64,") + 7);
                    try
                    {
                        imageBytes = Convert.FromBase64String(base64Part);
                    }
                    catch { }
                }
            }

            if (imageBytes != null && imageBytes.Length > 0)
            {
                string uniqueFileName = $"face_{targetUser.Id}_{Guid.NewGuid():N}{extension}";
                string physicalPath = Path.Combine(uploadsFolder, uniqueFileName);
                await System.IO.File.WriteAllBytesAsync(physicalPath, imageBytes);
                relativeFaceUrl = $"/uploads/faces/{uniqueFileName}";
            }

            targetUser.FaceDescriptor = descriptor;
            targetUser.FaceEnabled = 1;
            if (!string.IsNullOrEmpty(relativeFaceUrl))
            {
                targetUser.FaceImageUrl = relativeFaceUrl;
            }

            await _userRepository.UpdateAsync(targetUser);

            return Json(new
            {
                success = true,
                message = "Đã lưu khuôn mặt thành công!",
                face_image_url = targetUser.FaceImageUrl
            });
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
                    face_enabled = (user.FaceEnabled == 1 && !string.IsNullOrEmpty(user.FaceDescriptor)),
                    face_updated_at = user.FaceUpdatedAt?.ToString("dd/MM/yyyy HH:mm") ?? "",
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

        [HttpPost("api/account/face-delete")]
        public async Task<IActionResult> DeleteFace()
        {
            int uid = CurrentUserId ?? 1;
            var user = await _userRepository.GetByIdAsync(uid);
            if (user == null)
            {
                return Json(new { ok = false, error = "Người dùng không tồn tại" });
            }

            user.FaceDescriptor = null;
            user.FaceEnabled = 0;
            user.FaceImageUrl = null;
            user.FaceUpdatedAt = DateTime.UtcNow;
            await _userRepository.UpdateAsync(user);

            return Json(new { ok = true, message = "Đã xóa dữ liệu khuôn mặt thành công" });
        }
    }
}
