using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BtoanAiNet.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;

namespace BtoanAiNet.Controllers
{
    [ApiController]
    [Route("api/ide")]
    public class IdeController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IGeminiService _geminiService;
        private readonly IImageGenerationService _imageGenerationService;
        private readonly ILogger<IdeController> _logger;
        private static readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

        private static readonly HashSet<string> WhitelistedCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "dotnet", "npm", "node", "npx", "ng", "yarn", "pnpm", "vite", "tsc", "python", "py", "pip",
            "git", "dir", "ls", "echo", "pwd", "whoami", "cat", "type", "cls", "clear", "where",
            "if", "powershell", "pwsh", "test-path", "get-childitem", "write-output"
        };

        private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".idea", "node_modules", "dist", ".gemini"
        };

        public IdeController(
            IWebHostEnvironment env,
            IGeminiService geminiService,
            IImageGenerationService imageGenerationService,
            ILogger<IdeController> logger)
        {
            _env = env;
            _geminiService = geminiService;
            _imageGenerationService = imageGenerationService;
            _logger = logger;
        }

        private int CurrentUserId
        {
            get
            {
                var claimId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(claimId, out int uid) && uid > 0) return uid;

                var sessionUid = HttpContext.Session.GetInt32("user_id");
                if (sessionUid.HasValue && sessionUid.Value > 0) return sessionUid.Value;

                // Support X-User-Id header from client (Web IDE / Frontend API headers)
                if (Request.Headers.TryGetValue("X-User-Id", out var headerUid)
                    && int.TryParse(headerUid, out int hUid) && hUid > 0)
                {
                    return hUid;
                }

                // Support query parameter user_id
                if (Request.Query.TryGetValue("user_id", out var qUid)
                    && int.TryParse(qUid, out int parsedQUid) && parsedQUid > 0)
                {
                    return parsedQUid;
                }

                // Support cookie user_id
                if (Request.Cookies.TryGetValue("user_id", out var cookieUid)
                    && int.TryParse(cookieUid, out int cUid) && cUid > 0)
                {
                    return cUid;
                }

                if (User?.Identity?.IsAuthenticated == true)
                {
                    return 1;
                }

                // In Web IDE context, default to user 1 (primary workspace) to allow seamless project creation and execution
                return 1;
            }
        }

        private string? CurrentUsername
        {
            get
            {
                var name = User.FindFirst(ClaimTypes.Name)?.Value;
                if (!string.IsNullOrWhiteSpace(name)) return name;

                string? sessionName = HttpContext.Session.GetString("username");
                if (!string.IsNullOrWhiteSpace(sessionName)) return sessionName;

                if (Request.Headers.TryGetValue("X-Username", out var headerName) && !string.IsNullOrWhiteSpace(headerName))
                {
                    try { return Uri.UnescapeDataString(headerName.ToString()); } catch { return headerName.ToString(); }
                }

                if (Request.Cookies.TryGetValue("username", out var cookieName) && !string.IsNullOrWhiteSpace(cookieName))
                {
                    try { return Uri.UnescapeDataString(cookieName); } catch { return cookieName; }
                }

                int uid = CurrentUserId;
                return uid > 0 ? $"User_{uid}" : null;
            }
        }

        private bool IsAuthenticated => CurrentUserId > 0;

        private string GetUserRootPath()
        {
            return GetUserRootPath(CurrentUserId);
        }

        private string GetUserRootPath(int userId)
        {
            if (userId <= 0) return string.Empty;
            string root = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", $"user_{userId}");
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }
            EnsureDirectoryBuildProps(root);
            return root;
        }

        public static void EnsureDirectoryBuildProps(string workspaceRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workspaceRoot)) return;
                if (!Directory.Exists(workspaceRoot)) Directory.CreateDirectory(workspaceRoot);
                string propsPath = Path.Combine(workspaceRoot, "Directory.Build.props");
                if (!System.IO.File.Exists(propsPath) || !System.IO.File.ReadAllText(propsPath).Contains("PackageReference Update"))
                {
                    string content = @"<Project>
  <PropertyGroup>
    <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Update=""Microsoft.EntityFrameworkCore.Sqlite"" Version=""10.0.0"" />
    <PackageReference Update=""Microsoft.EntityFrameworkCore.SqlServer"" Version=""10.0.0"" />
    <PackageReference Update=""Microsoft.AspNetCore.OpenApi"" Version=""10.0.0"" />
  </ItemGroup>
</Project>
";
                    System.IO.File.WriteAllText(propsPath, content, Encoding.UTF8);
                }
            }
            catch { }
        }

        private static string SanitizeProjectName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "project-1";
            string clean = Regex.Replace(name.Trim(), @"[^a-zA-Z0-9_\-\.]", "-").Trim('-');
            return string.IsNullOrWhiteSpace(clean) ? "project-1" : clean;
        }

        private string ResolveWorkspacePath(string? projectId)
        {
            if (!IsAuthenticated) return string.Empty;
            string userRoot = GetUserRootPath();
            if (string.IsNullOrWhiteSpace(userRoot)) return string.Empty;

            if (string.IsNullOrWhiteSpace(projectId))
            {
                // Return user root
                return userRoot;
            }

            string cleanProj = SanitizeProjectName(projectId);
            string projectPath = Path.Combine(userRoot, cleanProj);
            return projectPath;
        }

        private bool TryGetSafeFilePath(string workspacePath, string relativePath, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(relativePath)) return false;

            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string combined = Path.GetFullPath(Path.Combine(workspacePath, normalized));
            string rootFullPath = Path.GetFullPath(workspacePath);

            if (!combined.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false; // Path traversal detected
            }

            fullPath = combined;
            return true;
        }

        // ==========================================
        // 1. PROJECT MANAGEMENT APIS
        // ==========================================

        // GET /api/ide/projects
        [HttpGet("projects")]
        public IActionResult GetProjects()
        {
            if (!IsAuthenticated)
            {
                return Ok(new
                {
                    ok = true,
                    isAuthenticated = false,
                    currentUserId = 0,
                    username = (string?)null,
                    projects = Array.Empty<object>()
                });
            }

            string userRoot = GetUserRootPath();
            var projectsList = new List<object>();

            if (Directory.Exists(userRoot))
            {
                var dirs = new DirectoryInfo(userRoot).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc);
                foreach (var d in dirs)
                {
                    int fileCount = 0;
                    try
                    {
                        fileCount = d.GetFiles("*", SearchOption.AllDirectories)
                            .Count(f => !IgnoredDirs.Any(ig => f.FullName.Contains(Path.DirectorySeparatorChar + ig + Path.DirectorySeparatorChar)));
                    }
                    catch { }

                    projectsList.Add(new
                    {
                        id = d.Name,
                        name = d.Name,
                        path = d.Name,
                        fileCount = fileCount,
                        createdAt = d.CreationTimeUtc,
                        lastModified = d.LastWriteTimeUtc,
                        hasIndexHtml = System.IO.File.Exists(Path.Combine(d.FullName, "index.html"))
                    });
                }
            }

            return Ok(new
            {
                ok = true,
                isAuthenticated = true,
                currentUserId = CurrentUserId,
                username = CurrentUsername,
                projects = projectsList
            });
        }

        // POST /api/ide/projects/create and POST /api/ide/create-project
        [HttpPost("projects/create")]
        [HttpPost("create-project")]
        public async Task<IActionResult> CreateProject([FromBody] IdeCreateProjectRequestModel model)
        {
            int effectiveUserId = CurrentUserId;
            if (effectiveUserId <= 0 && model?.UserId.HasValue == true && model.UserId.Value > 0)
            {
                effectiveUserId = model.UserId.Value;
            }

            if (effectiveUserId <= 0)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập tài khoản để tạo dự án.", needLogin = true });
            }

            string cleanName = SanitizeProjectName(model?.Name);
            string userRoot = GetUserRootPath(effectiveUserId);
            string projectPath = Path.Combine(userRoot, cleanName);

            if (Directory.Exists(projectPath))
            {
                return BadRequest(new { ok = false, error = $"Dự án '{cleanName}' đã tồn tại trong không gian của bạn. Hãy chọn tên khác (ví dụ: {cleanName}-1)." });
            }

            try
            {
                Directory.CreateDirectory(projectPath);
                string template = model?.Template?.ToLowerInvariant() ?? "empty";
                string primaryFile = string.Empty;

                if (template == "dotnet-mvc" || template == "mvc")
                {
                    // 1. ASP.NET Core Web App (Model-View-Controller) via official CLI
                    var res = await RunCliProcessAsync(userRoot, "dotnet", $"new mvc -n \"{cleanName}\" -o \"{projectPath}\" --no-https", 90);
                    if (!res.ok)
                    {
                        _logger.LogWarning("dotnet new mvc output: {Output}", res.output);
                    }
                    primaryFile = "Program.cs";
                }
                else if (template == "dotnet-webapi" || template == "webapi")
                {
                    // 2. ASP.NET Core Web API via official CLI
                    var res = await RunCliProcessAsync(userRoot, "dotnet", $"new webapi -n \"{cleanName}\" -o \"{projectPath}\" --use-controllers --no-https", 90);
                    if (!res.ok)
                    {
                        _logger.LogWarning("dotnet new webapi output: {Output}", res.output);
                    }
                    primaryFile = "Program.cs";
                }
                else if (template == "angular")
                {
                    // 3. Angular App via official Angular CLI
                    var res = await RunCliProcessAsync(userRoot, "npx", $"-y @angular/cli@latest new \"{cleanName}\" --directory \"{projectPath}\" --skip-git --style=css --ssr=false --defaults", 120);
                    if (!res.ok)
                    {
                        _logger.LogWarning("ng new output: {Output}", res.output);
                    }
                    primaryFile = System.IO.File.Exists(Path.Combine(projectPath, "src", "app", "app.component.ts")) 
                        ? "src/app/app.component.ts" 
                        : (System.IO.File.Exists(Path.Combine(projectPath, "src", "app", "app.ts")) ? "src/app/app.ts" : "package.json");
                }
                else if (template == "fullstack")
                {
                    // 4. Fullstack (ASP.NET Core Web API + Angular)
                    string bePath = Path.Combine(projectPath, "backend");
                    string fePath = Path.Combine(projectPath, "frontend");
                    Directory.CreateDirectory(bePath);
                    Directory.CreateDirectory(fePath);

                    await RunCliProcessAsync(userRoot, "dotnet", $"new webapi -n \"Backend\" -o \"{bePath}\" --use-controllers --no-https", 90);
                    await RunCliProcessAsync(userRoot, "npx", $"-y @angular/cli@latest new \"frontend\" --directory \"{fePath}\" --skip-git --style=css --ssr=false --defaults", 120);
                    primaryFile = "backend/Program.cs";
                }
                else if (template == "static-web" || template == "html5" || template == "web")
                {
                    // 5. HTML5 / CSS3 / JS Static Web
                    string html = @"<!DOCTYPE html>
<html lang=""vi"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>" + cleanName + @"</title>
    <link rel=""stylesheet"" href=""style.css"">
</head>
<body>
    <div class=""container"">
        <h1>Xin chào!</h1>
        <p>Dự án HTML5 / CSS3 / JS đã sẵn sàng phát triển.</p>
    </div>
    <script src=""script.js""></script>
</body>
</html>";
                    string css = @"* {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
}

body {
    font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, sans-serif;
    background-color: #0b0f19;
    color: #f1f5f9;
    display: flex;
    justify-content: center;
    align-items: center;
    min-height: 100vh;
}

.container {
    text-align: center;
    padding: 2rem;
}

h1 {
    color: #38bdf8;
    margin-bottom: 1rem;
}";
                    string js = @"// Khởi chạy khi DOM đã sẵn sàng
document.addEventListener(""DOMContentLoaded"", () => {
    console.log(""Dự án Web đã sẵn sàng!"");
});";
                    System.IO.File.WriteAllText(Path.Combine(projectPath, "index.html"), html, Encoding.UTF8);
                    System.IO.File.WriteAllText(Path.Combine(projectPath, "style.css"), css, Encoding.UTF8);
                    System.IO.File.WriteAllText(Path.Combine(projectPath, "script.js"), js, Encoding.UTF8);
                    primaryFile = "index.html";
                }
                else // empty
                {
                    // 6. Empty Project (Thư mục trống hoàn toàn)
                    primaryFile = "";
                }

                return Ok(new
                {
                    ok = true,
                    project = cleanName,
                    primaryFile = primaryFile,
                    message = $"Tạo dự án '{cleanName}' thành công!"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating project {Name}", cleanName);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        private static async Task<(bool ok, string output)> RunCliProcessAsync(string workingDir, string executable, string arguments, int timeoutSeconds = 120)
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c {executable} {arguments}";
            }
            else
            {
                psi.FileName = "/bin/bash";
                psi.Arguments = $"-c \"{executable} {arguments}\"";
                psi.Environment["DOTNET_ROOT"] = "/usr/share/dotnet";
                string curPath = psi.Environment.ContainsKey("PATH") ? (psi.Environment["PATH"] ?? "") : (Environment.GetEnvironmentVariable("PATH") ?? "");
                if (!curPath.Contains("/usr/share/dotnet"))
                {
                    psi.Environment["PATH"] = $"/usr/share/dotnet:{curPath}";
                }
            }

            var sb = new StringBuilder();
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                await proc.WaitForExitAsync(cts.Token);
                return (proc.ExitCode == 0, sb.ToString());
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return (false, $"Quá thời gian thực thi ({timeoutSeconds}s).\nOutput:\n{sb}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // DELETE /api/ide/projects/{name} and POST /api/ide/projects/delete and POST /api/ide/delete-project
        [HttpDelete("projects/{name}")]
        [HttpPost("projects/delete")]
        [HttpPost("delete-project")]
        public IActionResult DeleteProject(string? name, [FromBody] DeleteProjectModel? bodyModel)
        {
            int effectiveUserId = CurrentUserId;
            if (effectiveUserId <= 0 && bodyModel?.UserId.HasValue == true && bodyModel.UserId.Value > 0)
            {
                effectiveUserId = bodyModel.UserId.Value;
            }

            if (effectiveUserId <= 0)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để thực hiện thao tác này.", needLogin = true });
            }

            string targetName = !string.IsNullOrWhiteSpace(name) ? name : bodyModel?.Name ?? "";
            string cleanName = SanitizeProjectName(targetName);
            string userRoot = GetUserRootPath(effectiveUserId);
            string projectPath = Path.Combine(userRoot, cleanName);

            if (!Directory.Exists(projectPath))
            {
                return NotFound(new { ok = false, error = "Dự án không tồn tại." });
            }

            try
            {
                Directory.Delete(projectPath, true);
                return Ok(new { ok = true, message = $"Đã xóa dự án '{cleanName}' thành công." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting project {Name}", cleanName);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/projects/rename and POST /api/ide/rename-project
        [HttpPost("projects/rename")]
        [HttpPost("rename-project")]
        public IActionResult RenameProject([FromBody] RenameProjectModel model)
        {
            int effectiveUserId = CurrentUserId;
            if (effectiveUserId <= 0 && model?.UserId.HasValue == true && model.UserId.Value > 0)
            {
                effectiveUserId = model.UserId.Value;
            }

            if (effectiveUserId <= 0)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để thực hiện thao tác này.", needLogin = true });
            }

            string oldClean = SanitizeProjectName(model?.OldName);
            string newClean = SanitizeProjectName(model?.NewName);

            if (string.IsNullOrWhiteSpace(oldClean) || string.IsNullOrWhiteSpace(newClean))
            {
                return BadRequest(new { ok = false, error = "Tên dự án không hợp lệ." });
            }

            string userRoot = GetUserRootPath(effectiveUserId);
            string oldPath = Path.Combine(userRoot, oldClean);
            string newPath = Path.Combine(userRoot, newClean);

            if (!Directory.Exists(oldPath))
            {
                return NotFound(new { ok = false, error = $"Dự án '{oldClean}' không tồn tại." });
            }

            if (Directory.Exists(newPath))
            {
                return BadRequest(new { ok = false, error = $"Dự án '{newClean}' đã tồn tại. Vui lòng chọn tên khác." });
            }

            try
            {
                Directory.Move(oldPath, newPath);
                return Ok(new { ok = true, message = $"Đã đổi tên dự án thành '{newClean}'." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renaming project from {Old} to {New}", oldClean, newClean);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // GET /api/ide/projects/download?project=...
        [HttpGet("projects/download")]
        public IActionResult DownloadProjectZip([FromQuery] string project)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để tải về dự án.", needLogin = true });
            }

            string cleanProj = SanitizeProjectName(project);
            string projectPath = ResolveWorkspacePath(cleanProj);

            if (!Directory.Exists(projectPath))
            {
                return NotFound(new { ok = false, error = "Dự án không tồn tại." });
            }

            try
            {
                var memoryStream = new MemoryStream();
                using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                {
                    var files = Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        string relPath = Path.GetRelativePath(projectPath, f).Replace('\\', '/');
                        var parts = relPath.Split('/');
                        if (parts.Any(p => IgnoredDirs.Contains(p))) continue;

                        archive.CreateEntryFromFile(f, relPath);
                    }
                }

                memoryStream.Position = 0;
                return File(memoryStream, "application/zip", $"{cleanProj}.zip");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error zipping project {Name}", cleanProj);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/upload-folder
        [HttpPost("upload-folder")]
        [RequestSizeLimit(100_000_000)] // 100MB
        public async Task<IActionResult> UploadFolder()
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để tải lên thư mục.", needLogin = true });
            }

            string? project = Request.Form["project"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(project))
            {
                // Generate a project name from first folder name or default
                project = "uploaded-project";
            }
            string cleanProj = SanitizeProjectName(project);
            string projectPath = ResolveWorkspacePath(cleanProj);

            if (!Directory.Exists(projectPath))
            {
                Directory.CreateDirectory(projectPath);
            }

            var files = Request.Form.Files;
            if (files.Count == 0)
            {
                return BadRequest(new { ok = false, error = "Không có tệp nào được gửi lên." });
            }

            int savedCount = 0;
            try
            {
                var relativePaths = Request.Form["relativePaths"].ToArray();

                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    string relPath = (i < relativePaths.Length && !string.IsNullOrWhiteSpace(relativePaths[i]))
                        ? (relativePaths[i] ?? file.FileName)
                        : file.FileName;

                    // Skip ignored directories
                    var parts = (relPath ?? "").Replace('\\', '/').Split('/');
                    if (parts.Any(p => IgnoredDirs.Contains(p))) continue;

                    // Remove leading folder name if present (e.g. folderName/file.txt -> file.txt)
                    if (parts.Length > 1 && string.Equals(parts[0], project, StringComparison.OrdinalIgnoreCase))
                    {
                        relPath = string.Join('/', parts.Skip(1));
                    }

                    if (!TryGetSafeFilePath(projectPath, relPath ?? "", out string destPath))
                    {
                        continue; // Path traversal detected
                    }

                    string? parentDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    using (var stream = new FileStream(destPath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }
                    savedCount++;
                }

                return Ok(new
                {
                    ok = true,
                    project = cleanProj,
                    savedFiles = savedCount,
                    message = $"Đã tải lên thành công {savedCount} tệp vào dự án '{cleanProj}'."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading folder to project {Name}", cleanProj);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/upload-files
        [HttpPost("upload-files")]
        [RequestSizeLimit(50_000_000)]
        public async Task<IActionResult> UploadFiles()
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để tải lên tệp tin.", needLogin = true });
            }

            string? project = Request.Form["project"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(project))
            {
                project = "my-project";
            }
            string cleanProj = SanitizeProjectName(project);
            string projectPath = ResolveWorkspacePath(cleanProj);

            if (!Directory.Exists(projectPath))
            {
                Directory.CreateDirectory(projectPath);
            }

            var files = Request.Form.Files;
            if (files.Count == 0)
            {
                return BadRequest(new { ok = false, error = "Không có tệp nào được gửi lên." });
            }

            int count = 0;
            foreach (var file in files)
            {
                string safeName = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(safeName)) continue;

                if (TryGetSafeFilePath(projectPath, safeName, out string destPath))
                {
                    using (var stream = new FileStream(destPath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }
                    count++;
                }
            }

            return Ok(new
            {
                ok = true,
                project = cleanProj,
                savedFiles = count,
                message = $"Đã tải lên {count} tệp tin."
            });
        }

        // ==========================================
        // 2. LIVE STATIC PREVIEW APIS
        // ==========================================

        // GET /api/ide/preview/{project}/{*filePath}
        [HttpGet("preview/{project}/{*filePath}")]
        public IActionResult LivePreview(string project, string? filePath)
        {
            int userId = CurrentUserId;
            string cleanProj = SanitizeProjectName(project);
            string projectPath = string.Empty;

            if (userId > 0)
            {
                projectPath = Path.Combine(GetUserRootPath(userId), cleanProj);
            }

            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                // Fallback to user_1 if project exists in default workspace
                string user1Path = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", "user_1", cleanProj);
                if (Directory.Exists(user1Path))
                {
                    projectPath = user1Path;
                }
                else
                {
                    // Search if there is any folder matching or starting with cleanProj in user_1 or any user workspace
                    string workspacesRoot = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces");
                    if (Directory.Exists(workspacesRoot))
                    {
                        var matchingDir = Directory.GetDirectories(workspacesRoot, "*", SearchOption.AllDirectories)
                            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), cleanProj, StringComparison.OrdinalIgnoreCase)
                                              || Path.GetFileName(d).StartsWith(cleanProj + "-", StringComparison.OrdinalIgnoreCase));
                        if (matchingDir != null)
                        {
                            projectPath = matchingDir;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                return Content($"<div style='font-family:-apple-system,sans-serif;padding:30px;color:#cbd5e1;background:#0f172a;border-radius:12px;text-align:center;'><h3>Không tìm thấy thư mục dự án '{cleanProj}'</h3><p>Vui lòng tạo hoặc mở dự án từ danh sách cây thư mục bên trái.</p></div>", "text/html; charset=utf-8");
            }

            string targetFile = string.IsNullOrWhiteSpace(filePath) ? "index.html" : filePath;
            if (!TryGetSafeFilePath(projectPath, targetFile, out string fullPath) || !System.IO.File.Exists(fullPath))
            {
                // Fallback to index.html if available
                string indexFallback = Path.Combine(projectPath, "index.html");
                if (System.IO.File.Exists(indexFallback))
                {
                    fullPath = indexFallback;
                }
                else
                {
                    // Check if this is an ASP.NET Core project (.csproj exists)
                    var csprojFiles = Directory.GetFiles(projectPath, "*.csproj");
                    if (csprojFiles.Length > 0)
                    {
                        return Content($@"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0b0f19; color: #f1f5f9; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; text-align: center; }}
.card {{ background: #1e293b; border: 1px solid #334155; border-radius: 16px; padding: 36px; max-width: 500px; box-shadow: 0 20px 40px rgba(0,0,0,0.5); }}
.icon {{ font-size: 44px; margin-bottom: 14px; }}
h2 {{ margin: 0 0 10px; color: #38bdf8; font-size: 20px; }}
p {{ color: #94a3b8; line-height: 1.6; font-size: 13.5px; margin-bottom: 20px; }}
.badge {{ background: #0f172a; border: 1px solid #38bdf8; color: #38bdf8; padding: 4px 10px; border-radius: 6px; font-family: monospace; font-size: 12px; }}
</style>
</head>
<body>
<div class='card'>
  <div class='icon'>⚡</div>
  <h2>Dự án ASP.NET Core '{cleanProj}'</h2>
  <p>Dự án này là ứng dụng ASP.NET Core MVC / Web API đã sẵn sàng chạy.<br><br>Hãy nhấn nút <strong style='color:#10b981;'>▶ Chạy (Run)</strong> màu xanh trên thanh công cụ IDE để khởi động server trên cổng động và mở Live Preview tương tác thời gian thực!</p>
  <div><span class='badge'>Btoan Studio IDE &bull; Live Dynamic Port</span></div>
</div>
</body>
</html>", "text/html; charset=utf-8");
                    }
                    return NotFound($"Không tìm thấy tệp '{targetFile}' trong dự án '{cleanProj}'.");
                }
            }

            if (!_contentTypeProvider.TryGetContentType(fullPath, out string? contentType))
            {
                contentType = "application/octet-stream";
            }

            // Ensure proper charset for text/html, js, css
            if (contentType.StartsWith("text/") || contentType.Contains("javascript"))
            {
                contentType += "; charset=utf-8";
            }

            return PhysicalFile(fullPath, contentType);
        }

        // ==========================================
        // 3. FILE SYSTEM & EXPLORER APIS
        // ==========================================

        // GET /api/ide/files?project=...
        [HttpGet("files")]
        public IActionResult GetFiles([FromQuery] string? project)
        {
            if (!IsAuthenticated)
            {
                return Ok(new { ok = true, project = "", files = Array.Empty<object>() });
            }

            string root = ResolveWorkspacePath(project);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Ok(new { ok = true, project = project ?? "", files = Array.Empty<object>() });
            }

            try
            {
                var filesList = new List<object>();
                var allEntries = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
                foreach (var entry in allEntries)
                {
                    string relPath = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    var parts = relPath.Split('/');
                    if (parts.Any(p => IgnoredDirs.Contains(p))) continue;

                    bool isDir = Directory.Exists(entry);
                    filesList.Add(new
                    {
                        path = relPath,
                        name = Path.GetFileName(entry),
                        isDir = isDir
                    });
                }

                return Ok(new { ok = true, project, files = filesList });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting workspace files list");
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // GET /api/ide/tree?project=...
        [HttpGet("tree")]
        public IActionResult GetTree([FromQuery] string? project)
        {
            if (!IsAuthenticated)
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }

            string root = ResolveWorkspacePath(project);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Ok(new { ok = true, project = project ?? "", tree = (object?)null });
            }

            try
            {
                var rootNode = BuildDirectoryTree(new DirectoryInfo(root), root);
                return Ok(new { ok = true, project, tree = rootNode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading workspace tree");
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        private object BuildDirectoryTree(DirectoryInfo dir, string rootPath)
        {
            var children = new List<object>();

            foreach (var subDir in dir.GetDirectories().OrderBy(d => d.Name))
            {
                if (IgnoredDirs.Contains(subDir.Name)) continue;
                children.Add(BuildDirectoryTree(subDir, rootPath));
            }

            foreach (var file in dir.GetFiles().OrderBy(f => f.Name))
            {
                string relPath = Path.GetRelativePath(rootPath, file.FullName).Replace('\\', '/');
                children.Add(new
                {
                    name = file.Name,
                    path = relPath,
                    isDirectory = false,
                    size = file.Length,
                    extension = file.Extension.ToLowerInvariant()
                });
            }

            string dirRelPath = dir.FullName.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                ? ""
                : Path.GetRelativePath(rootPath, dir.FullName).Replace('\\', '/');

            return new
            {
                name = dir.Name,
                path = dirRelPath,
                isDirectory = true,
                children
            };
        }

        // GET /api/ide/search?project=...&q=...
        [HttpGet("search")]
        public IActionResult Search([FromQuery] string q, [FromQuery] string? project)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Ok(new { ok = true, query = "", results = Array.Empty<object>() });
            }

            if (!IsAuthenticated)
            {
                return Ok(new { ok = true, query = q, count = 0, results = Array.Empty<object>() });
            }

            string root = ResolveWorkspacePath(project);
            if (!Directory.Exists(root))
            {
                return Ok(new { ok = true, query = q, count = 0, results = Array.Empty<object>() });
            }

            var results = new List<object>();

            void ScanDir(DirectoryInfo dir)
            {
                foreach (var sub in dir.GetDirectories())
                {
                    if (IgnoredDirs.Contains(sub.Name)) continue;
                    ScanDir(sub);
                }

                foreach (var file in dir.GetFiles())
                {
                    if (file.Length > 1024 * 512) continue;
                    string ext = file.Extension.ToLowerInvariant();
                    if (ext is ".dll" or ".exe" or ".pdb" or ".zip" or ".png" or ".jpg" or ".ico") continue;

                    try
                    {
                        var lines = System.IO.File.ReadAllLines(file.FullName);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            int col = lines[i].IndexOf(q, StringComparison.OrdinalIgnoreCase);
                            if (col >= 0)
                            {
                                string rel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                                results.Add(new
                                {
                                    file = rel,
                                    fileName = file.Name,
                                    line = i + 1,
                                    col = col + 1,
                                    text = lines[i].Trim()
                                });

                                if (results.Count >= 200) return;
                            }
                        }
                    }
                    catch { }
                }
            }

            ScanDir(new DirectoryInfo(root));

            return Ok(new
            {
                ok = true,
                query = q,
                count = results.Count,
                results
            });
        }

        // ==========================================
        // GIT SOURCE CONTROL APIS
        // ==========================================

        private static async Task<(int exitCode, string stdout, string stderr)> RunGitCommandAsync(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "", "Failed to start git process");

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode, stdout.Trim(), stderr.Trim());
        }

        // GET /api/ide/git/status and GET /api/ide/git-status
        [HttpGet("git/status")]
        [HttpGet("git-status")]
        public async Task<IActionResult> GetGitStatus([FromQuery] string? project)
        {
            if (!IsAuthenticated)
            {
                return Ok(new { ok = true, isRepo = false, branch = "main", staged = Array.Empty<object>(), unstaged = Array.Empty<object>(), count = 0, files = Array.Empty<object>() });
            }

            string root = ResolveWorkspacePath(project);
            if (!Directory.Exists(root) || !Directory.Exists(Path.Combine(root, ".git")))
            {
                return Ok(new { ok = true, isRepo = false, branch = "", staged = Array.Empty<object>(), unstaged = Array.Empty<object>(), count = 0, files = Array.Empty<object>() });
            }

            var (_, branchOut, _) = await RunGitCommandAsync(root, "rev-parse", "--abbrev-ref", "HEAD");
            string branch = string.IsNullOrWhiteSpace(branchOut) ? "main" : branchOut;

            var (_, statusOut, _) = await RunGitCommandAsync(root, "status", "--porcelain=v1", "-uall");
            var staged = new List<object>();
            var unstaged = new List<object>();
            var allModified = new List<object>();

            if (!string.IsNullOrWhiteSpace(statusOut))
            {
                var lines = statusOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var l in lines)
                {
                    if (l.Length < 3) continue;
                    char col0 = l[0];
                    char col1 = l[1];
                    string filePath = l.Substring(3).Trim(' ', '"');

                    if (col0 != ' ' && col0 != '?')
                    {
                        staged.Add(new { path = filePath, status = col0.ToString() });
                    }
                    if (col1 != ' ')
                    {
                        unstaged.Add(new { path = filePath, status = col1 == '?' ? "U" : col1.ToString() });
                    }
                    allModified.Add(new { path = filePath, status = (col0 != ' ' ? col0 : col1).ToString() });
                }
            }

            return Ok(new
            {
                ok = true,
                isRepo = true,
                branch,
                staged,
                unstaged,
                count = staged.Count + unstaged.Count,
                files = allModified
            });
        }

        [HttpPost("git/init")]
        public async Task<IActionResult> GitInit([FromBody] GitProjectRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(model?.Project);
            if (!Directory.Exists(root)) return NotFound("Workspace not found");

            await RunGitCommandAsync(root, "init", "-b", "main");
            await RunGitCommandAsync(root, "config", "user.name", "Btoan AI Developer");
            await RunGitCommandAsync(root, "config", "user.email", "dev@ai.btoan.com");

            return Ok(new { ok = true, message = "Initialized Git repository successfully." });
        }

        [HttpPost("git/stage")]
        public async Task<IActionResult> GitStage([FromBody] GitStageRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(model?.Project);
            if (!Directory.Exists(root)) return NotFound("Workspace not found");

            if (model?.All == true)
            {
                await RunGitCommandAsync(root, "add", "-A");
            }
            else if (!string.IsNullOrWhiteSpace(model?.Path))
            {
                await RunGitCommandAsync(root, "add", "--", model.Path);
            }

            return Ok(new { ok = true });
        }

        [HttpPost("git/unstage")]
        public async Task<IActionResult> GitUnstage([FromBody] GitStageRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(model?.Project);
            if (!Directory.Exists(root)) return NotFound("Workspace not found");

            if (model?.All == true)
            {
                await RunGitCommandAsync(root, "restore", "--staged", ".");
            }
            else if (!string.IsNullOrWhiteSpace(model?.Path))
            {
                await RunGitCommandAsync(root, "restore", "--staged", "--", model.Path);
            }

            return Ok(new { ok = true });
        }

        [HttpPost("git/discard")]
        public async Task<IActionResult> GitDiscard([FromBody] GitStageRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(model?.Project);
            if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(model?.Path)) return BadRequest();

            string fullPath = Path.Combine(root, model.Path);
            if (System.IO.File.Exists(fullPath))
            {
                var (code, _, _) = await RunGitCommandAsync(root, "restore", "--", model.Path);
                if (code != 0)
                {
                    await RunGitCommandAsync(root, "clean", "-f", "--", model.Path);
                }
            }

            return Ok(new { ok = true });
        }

        [HttpGet("git/diff")]
        public async Task<IActionResult> GitDiff([FromQuery] string? project, [FromQuery] string? path, [FromQuery] bool staged)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(project);
            if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(path)) return BadRequest();

            string original = "";
            string current = "";

            if (staged)
            {
                var (_, origOut, _) = await RunGitCommandAsync(root, "show", $"HEAD:{path}");
                var (_, curOut, _) = await RunGitCommandAsync(root, "show", $":{path}");
                original = origOut;
                current = curOut;
            }
            else
            {
                var (_, origOut, _) = await RunGitCommandAsync(root, "show", $":{path}");
                if (string.IsNullOrEmpty(origOut))
                {
                    var (_, headOut, _) = await RunGitCommandAsync(root, "show", $"HEAD:{path}");
                    origOut = headOut;
                }
                original = origOut;

                string fullPath = Path.Combine(root, path);
                if (System.IO.File.Exists(fullPath))
                {
                    current = await System.IO.File.ReadAllTextAsync(fullPath);
                }
            }

            return Ok(new { ok = true, path, original, current });
        }

        [HttpPost("git/commit")]
        public async Task<IActionResult> GitCommit([FromBody] GitCommitRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(model?.Project);
            if (!Directory.Exists(root)) return NotFound("Workspace not found");

            string msg = model?.Message?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(msg)) return BadRequest(new { ok = false, error = "Vui lòng nhập commit message." });

            var (code, stdout, stderr) = await RunGitCommandAsync(root, "commit", "-m", msg);
            if (code == 0)
            {
                return Ok(new { ok = true, message = stdout });
            }
            return BadRequest(new { ok = false, error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr });
        }

        [HttpGet("git/branches")]
        public async Task<IActionResult> GitBranches([FromQuery] string? project)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(project);
            if (!Directory.Exists(root)) return NotFound();

            var (_, bOut, _) = await RunGitCommandAsync(root, "branch", "--list");
            var branches = new List<string>();
            string current = "main";

            if (!string.IsNullOrWhiteSpace(bOut))
            {
                var lines = bOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var l in lines)
                {
                    string trimmed = l.Trim();
                    if (trimmed.StartsWith("*"))
                    {
                        current = trimmed.TrimStart('*', ' ');
                        branches.Add(current);
                    }
                    else if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        branches.Add(trimmed);
                    }
                }
            }

            return Ok(new { ok = true, current, branches });
        }

        [HttpPost("git/checkout")]
        public async Task<IActionResult> GitCheckout([FromBody] GitCheckoutRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(model?.Project);
            if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(model?.Branch)) return BadRequest();

            (int code, string stdout, string stderr) res;
            if (model.CreateNew)
            {
                res = await RunGitCommandAsync(root, "checkout", "-b", model.Branch.Trim());
            }
            else
            {
                res = await RunGitCommandAsync(root, "checkout", model.Branch.Trim());
            }

            if (res.code == 0)
            {
                return Ok(new { ok = true, message = $"Switched to branch '{model.Branch}'." });
            }
            return BadRequest(new { ok = false, error = res.stderr });
        }

        [HttpPost("git/push")]
        public async Task<IActionResult> GitPush([FromBody] GitPushRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string root = ResolveWorkspacePath(model?.Project);
            if (!Directory.Exists(root)) return NotFound();

            string remoteUrl = model?.RemoteUrl?.Trim() ?? "";
            string token = model?.Token?.Trim() ?? "";
            string branch = string.IsNullOrWhiteSpace(model?.Branch) ? "main" : model.Branch.Trim();

            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                string targetRemote = remoteUrl;
                if (!string.IsNullOrWhiteSpace(token) && targetRemote.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    string hostAndPath = targetRemote.Substring(8);
                    if (hostAndPath.Contains('@')) hostAndPath = hostAndPath.Substring(hostAndPath.IndexOf('@') + 1);
                    targetRemote = $"https://{Uri.EscapeDataString(token)}@{hostAndPath}";
                }

                var (codeCheck, _, _) = await RunGitCommandAsync(root, "remote", "get-url", "origin");
                if (codeCheck == 0)
                {
                    await RunGitCommandAsync(root, "remote", "set-url", "origin", targetRemote);
                }
                else
                {
                    await RunGitCommandAsync(root, "remote", "add", "origin", targetRemote);
                }
            }

            var (pushCode, pushOut, pushErr) = await RunGitCommandAsync(root, "push", "-u", "origin", branch);
            if (pushCode == 0)
            {
                return Ok(new { ok = true, message = "Đã đẩy mã nguồn lên GitHub thành công!" });
            }
            return BadRequest(new { ok = false, error = string.IsNullOrWhiteSpace(pushErr) ? pushOut : pushErr });
        }

        // ==========================================
        // GLOBAL SEARCH & REPLACE API
        // ==========================================

        [HttpPost("replace")]
        public async Task<IActionResult> GlobalReplace([FromBody] GlobalReplaceRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            if (model == null || string.IsNullOrWhiteSpace(model.Query))
            {
                return BadRequest(new { ok = false, error = "Cần cung cấp từ khóa tìm kiếm." });
            }

            string root = ResolveWorkspacePath(model.Project);
            if (!Directory.Exists(root)) return NotFound("Workspace not found");

            int filesChanged = 0;
            int replacementsCount = 0;
            string replaceWith = model.ReplaceWith ?? string.Empty;

            var allFiles = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => !IgnoredDirs.Any(d => f.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar) || f.EndsWith(Path.DirectorySeparatorChar + d)))
                .ToList();

            foreach (var f in allFiles)
            {
                try
                {
                    string content = await System.IO.File.ReadAllTextAsync(f);
                    string newContent;
                    int count = 0;

                    if (model.IsRegex)
                    {
                        var regexOptions = model.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                        var reg = new Regex(model.Query, regexOptions);
                        count = reg.Matches(content).Count;
                        newContent = reg.Replace(content, replaceWith);
                    }
                    else
                    {
                        var comparison = model.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        int idx = 0;
                        while ((idx = content.IndexOf(model.Query, idx, comparison)) >= 0)
                        {
                            count++;
                            idx += model.Query.Length;
                        }
                        newContent = model.MatchCase ? content.Replace(model.Query, replaceWith) : Regex.Replace(content, Regex.Escape(model.Query), replaceWith, RegexOptions.IgnoreCase);
                    }

                    if (count > 0 && newContent != content)
                    {
                        await System.IO.File.WriteAllTextAsync(f, newContent, Encoding.UTF8);
                        filesChanged++;
                        replacementsCount += count;
                    }
                }
                catch { }
            }

            return Ok(new
            {
                ok = true,
                filesChanged,
                replacementsCount,
                message = $"Đã thay thế {replacementsCount} vị trí trong {filesChanged} tệp tin."
            });
        }

        // ==========================================
        // SECRETS MANAGEMENT APIS
        // ==========================================

        private string GetProjectSecretsFilePath(string projectName)
        {
            string cleanProj = SanitizeProjectName(projectName);
            string userRoot = GetUserRootPath();
            string secretsDir = Path.Combine(userRoot, ".secrets");
            if (!Directory.Exists(secretsDir)) Directory.CreateDirectory(secretsDir);
            return Path.Combine(secretsDir, $"{cleanProj}.json");
        }

        public static Dictionary<string, string> LoadProjectSecrets(string userRoot, string projectName)
        {
            try
            {
                string cleanProj = SanitizeProjectName(projectName);
                string path = Path.Combine(userRoot, ".secrets", $"{cleanProj}.json");
                if (System.IO.File.Exists(path))
                {
                    string json = System.IO.File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<List<SecretPairModel>>(json);
                    if (list != null)
                    {
                        return list.Where(s => !string.IsNullOrWhiteSpace(s.Key))
                                   .ToDictionary(s => s.Key.Trim(), s => s.Value ?? "", StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
            return new Dictionary<string, string>();
        }

        [HttpGet("secrets")]
        public async Task<IActionResult> GetProjectSecrets([FromQuery] string? project)
        {
            if (!IsAuthenticated) return Unauthorized();
            string filePath = GetProjectSecretsFilePath(project ?? "default");
            var list = new List<SecretPairModel>();
            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    string json = await System.IO.File.ReadAllTextAsync(filePath);
                    list = JsonSerializer.Deserialize<List<SecretPairModel>>(json) ?? new();
                }
                catch { }
            }
            return Ok(new { ok = true, secrets = list });
        }

        [HttpPost("secrets")]
        public async Task<IActionResult> SaveProjectSecrets([FromBody] ProjectSecretsRequestModel model)
        {
            if (!IsAuthenticated) return Unauthorized();
            string filePath = GetProjectSecretsFilePath(model?.Project ?? "default");
            var list = model?.Secrets ?? new();
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
            return Ok(new { ok = true, message = "Đã lưu bí mật dự án (.env secrets) an toàn." });
        }

        // ==========================================
        // ACTIVE PORTS LIST API
        // ==========================================

        [HttpGet("ports")]
        public IActionResult GetActivePorts([FromQuery] string? project)
        {
            if (!IsAuthenticated) return Unauthorized();
            var sessions = RunningAppSessionManager.GetUserSessions(CurrentUserId);
            return Ok(new { ok = true, ports = sessions });
        }

        // GET /api/ide/file?project=...&path=...
        // GET /api/ide/file-content?project=...&file=...
        [HttpGet("file")]
        [HttpGet("file-content")]
        public async Task<IActionResult> GetFile([FromQuery] string? path, [FromQuery] string? file, [FromQuery] string? project)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để xem tệp tin.", needLogin = true });
            }

            string targetPath = !string.IsNullOrWhiteSpace(path) ? path : (!string.IsNullOrWhiteSpace(file) ? file : "");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return BadRequest(new { ok = false, error = "Path or file is required" });
            }

            string root = ResolveWorkspacePath(project);
            if (!TryGetSafeFilePath(root, targetPath, out string fullPath) || !System.IO.File.Exists(fullPath))
            {
                return NotFound(new { ok = false, error = "Không tìm thấy tệp tin hoặc đường dẫn không hợp lệ." });
            }

            try
            {
                string content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                string language = DetectLanguage(targetPath);

                return Ok(new
                {
                    ok = true,
                    path = targetPath,
                    content,
                    language,
                    size = new FileInfo(fullPath).Length
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading file {Path}", targetPath);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/file/save
        // POST /api/ide/save
        [HttpPost("file/save")]
        [HttpPost("save")]
        public async Task<IActionResult> SaveFile([FromBody] SaveFileModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Chức năng lưu tệp yêu cầu đăng nhập tài khoản.", needLogin = true });
            }

            if (model == null)
            {
                return BadRequest(new { ok = false, error = "Invalid model" });
            }

            string targetPath = !string.IsNullOrWhiteSpace(model.Path) ? model.Path : (!string.IsNullOrWhiteSpace(model.File) ? model.File : "");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return BadRequest(new { ok = false, error = "Path is required" });
            }

            string root = ResolveWorkspacePath(model.Project);
            if (!TryGetSafeFilePath(root, targetPath, out string fullPath))
            {
                return BadRequest(new { ok = false, error = "Invalid file path" });
            }

            try
            {
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await System.IO.File.WriteAllTextAsync(fullPath, model.Content ?? "", Encoding.UTF8);
                return Ok(new { ok = true, message = "File saved successfully", path = targetPath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving file {Path}", targetPath);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        private static string GenerateSvgPlaceholder(string title, int width = 800, int height = 500)
        {
            string cleanTitle = System.Web.HttpUtility.HtmlEncode(title);
            return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{width}"" height=""{height}"" viewBox=""0 0 {width} {height}"">
  <defs>
    <linearGradient id=""grad"" x1=""0%"" y1=""0%"" x2=""100%"" y2=""100%"">
      <stop offset=""0%"" style=""stop-color:#1e293b;stop-opacity:1"" />
      <stop offset=""100%"" style=""stop-color:#0f172a;stop-opacity:1"" />
    </linearGradient>
  </defs>
  <rect width=""100%"" height=""100%"" fill=""url(#grad)""/>
  <circle cx=""{width/2}"" cy=""{height/2 - 30}"" r=""40"" fill=""#3b82f6"" fill-opacity=""0.2""/>
  <path d=""M{width/2 - 18} {height/2 - 18}h36v24h-36z"" fill=""none"" stroke=""#60a5fa"" stroke-width=""2"" stroke-linejoin=""round""/>
  <circle cx=""{width/2 - 8}"" cy=""{height/2 - 8}"" r=""3"" fill=""#60a5fa""/>
  <path d=""M{width/2 - 18} {height/2 + 2}l10-10 8 8 6-6 12 12"" fill=""none"" stroke=""#60a5fa"" stroke-width=""2""/>
  <text x=""50%"" y=""{height/2 + 45}"" font-family=""'Inter',-apple-system,sans-serif"" font-size=""16"" font-weight=""600"" fill=""#e2e8f0"" text-anchor=""middle"">{cleanTitle}</text>
  <text x=""50%"" y=""{height/2 + 70}"" font-family=""'Inter',-apple-system,sans-serif"" font-size=""12"" fill=""#94a3b8"" text-anchor=""middle"">Generated with Btoan AI</text>
</svg>";
        }

        // POST /api/ide/files/apply-batch
        [HttpPost("files/apply-batch")]
        public async Task<IActionResult> ApplyBatchOperations([FromBody] BatchOperationsModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để áp dụng các thay đổi vào dự án.", needLogin = true });
            }

            if (model == null || ((model.Operations == null || model.Operations.Count == 0) && (model.Images == null || model.Images.Count == 0)))
            {
                return BadRequest(new { ok = false, error = "Không có thao tác nào được chỉ định." });
            }

            string cleanProj = SanitizeProjectName(model.Project);
            string projectPath = ResolveWorkspacePath(cleanProj);

            if (!Directory.Exists(projectPath))
            {
                Directory.CreateDirectory(projectPath);
            }

            int successCount = 0;
            var appliedFiles = new List<string>();

            try
            {
                if (model.Operations != null)
                {
                    foreach (var op in model.Operations)
                    {
                        if (string.IsNullOrWhiteSpace(op.Path)) continue;

                        if (!TryGetSafeFilePath(projectPath, op.Path, out string fullPath))
                        {
                            continue; // Skip dangerous paths
                        }

                        if (string.Equals(op.Action, "delete", StringComparison.OrdinalIgnoreCase))
                        {
                            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
                            else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                            successCount++;
                        }
                        else // create or edit
                        {
                            string? parent = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                            {
                                Directory.CreateDirectory(parent);
                            }

                            await System.IO.File.WriteAllTextAsync(fullPath, op.Content ?? "", Encoding.UTF8);
                            successCount++;
                            appliedFiles.Add(op.Path.Replace('\\', '/'));
                        }
                    }
                }

                // Auto-generate project images if requested
                if (model.Images != null && model.Images.Count > 0)
                {
                    foreach (var img in model.Images)
                    {
                        if (string.IsNullOrWhiteSpace(img.Path) || string.IsNullOrWhiteSpace(img.Prompt)) continue;

                        if (!TryGetSafeFilePath(projectPath, img.Path, out string fullImgPath)) continue;

                        string? parent = Path.GetDirectoryName(fullImgPath);
                        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        bool imageSaved = false;
                        try
                        {
                            var genRes = await _imageGenerationService.GenerateImageAsync(new ViewModels.ImageGenerationRequestModel
                            {
                                Prompt = img.Prompt,
                                Aspect_Ratio = "16:9"
                            });

                            if (genRes != null && genRes["success"]?.GetValue<bool>() == true)
                            {
                                string? base64 = genRes["imageBase64"]?.ToString();
                                string? imgUrl = genRes["imageUrl"]?.ToString();

                                if (!string.IsNullOrEmpty(base64))
                                {
                                    byte[] imgBytes = Convert.FromBase64String(base64);
                                    await System.IO.File.WriteAllBytesAsync(fullImgPath, imgBytes);
                                    imageSaved = true;
                                }
                                else if (!string.IsNullOrEmpty(imgUrl) && imgUrl.StartsWith("/uploads/"))
                                {
                                    string localPath = Path.Combine(_env.WebRootPath, imgUrl.TrimStart('/'));
                                    if (System.IO.File.Exists(localPath))
                                    {
                                        System.IO.File.Copy(localPath, fullImgPath, true);
                                        imageSaved = true;
                                    }
                                }
                            }
                        }
                        catch (Exception imgEx)
                        {
                            _logger.LogWarning("Failed to generate image for {Path}: {Error}", img.Path, imgEx.Message);
                        }

                        // Fallback: If image generation failed, write a beautiful SVG placeholder so HTML never breaks
                        if (!imageSaved)
                        {
                            string svgContent = GenerateSvgPlaceholder(Path.GetFileNameWithoutExtension(img.Path));
                            await System.IO.File.WriteAllTextAsync(fullImgPath, svgContent, Encoding.UTF8);
                        }

                        successCount++;
                        appliedFiles.Add(img.Path.Replace('\\', '/'));
                    }
                }

                return Ok(new
                {
                    ok = true,
                    project = cleanProj,
                    appliedCount = successCount,
                    appliedFiles = appliedFiles,
                    message = $"Đã áp dụng thành công {successCount} tệp và hình ảnh vào dự án '{cleanProj}'."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying batch operations to {Project}", cleanProj);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/images/generate
        [HttpPost("images/generate")]
        public async Task<IActionResult> GenerateProjectImage([FromBody] GenerateProjectImageRequest model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để tạo hình ảnh." });
            }

            if (model == null || string.IsNullOrWhiteSpace(model.Project) || string.IsNullOrWhiteSpace(model.Prompt))
            {
                return BadRequest(new { ok = false, error = "Thiếu thông tin dự án hoặc prompt." });
            }

            string cleanProj = SanitizeProjectName(model.Project);
            string projectPath = ResolveWorkspacePath(cleanProj);
            if (!Directory.Exists(projectPath)) Directory.CreateDirectory(projectPath);

            string targetPath = string.IsNullOrWhiteSpace(model.Path) ? $"images/img_{Guid.NewGuid():N}.png" : model.Path;
            if (!TryGetSafeFilePath(projectPath, targetPath, out string fullPath))
            {
                return BadRequest(new { ok = false, error = "Đường dẫn tệp tin không hợp lệ." });
            }

            string? parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

            try
            {
                var genRes = await _imageGenerationService.GenerateImageAsync(new ViewModels.ImageGenerationRequestModel
                {
                    Prompt = model.Prompt,
                    Aspect_Ratio = "16:9"
                });

                if (genRes != null && genRes["success"]?.GetValue<bool>() == true)
                {
                    string? base64 = genRes["imageBase64"]?.ToString();
                    string? imgUrl = genRes["imageUrl"]?.ToString();

                    if (!string.IsNullOrEmpty(base64))
                    {
                        byte[] imgBytes = Convert.FromBase64String(base64);
                        await System.IO.File.WriteAllBytesAsync(fullPath, imgBytes);
                    }
                    else if (!string.IsNullOrEmpty(imgUrl) && imgUrl.StartsWith("/uploads/"))
                    {
                        string localPath = Path.Combine(_env.WebRootPath, imgUrl.TrimStart('/'));
                        if (System.IO.File.Exists(localPath)) System.IO.File.Copy(localPath, fullPath, true);
                    }

                    return Ok(new
                    {
                        ok = true,
                        path = targetPath,
                        url = $"/api/ide/preview/{cleanProj}/{targetPath}",
                        message = "Đã sinh ảnh thành công."
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("GenerateProjectImage error: {Message}", ex.Message);
            }

            // Fallback SVG
            string svg = GenerateSvgPlaceholder(Path.GetFileNameWithoutExtension(targetPath));
            await System.IO.File.WriteAllTextAsync(fullPath, svg, Encoding.UTF8);

            return Ok(new
            {
                ok = true,
                path = targetPath,
                url = $"/api/ide/preview/{cleanProj}/{targetPath}",
                message = "Đã tạo ảnh minh họa SVG cho dự án."
            });
        }

        // POST /api/ide/file/create and POST /api/ide/create-file
        [HttpPost("file/create")]
        [HttpPost("create-file")]
        public async Task<IActionResult> CreateItem([FromBody] CreateItemModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để tạo tệp tin.", needLogin = true });
            }

            if (model == null || string.IsNullOrWhiteSpace(model.Path))
            {
                return BadRequest(new { ok = false, error = "Path is required" });
            }

            string root = ResolveWorkspacePath(model.Project);
            if (!TryGetSafeFilePath(root, model.Path, out string fullPath))
            {
                return BadRequest(new { ok = false, error = "Invalid path" });
            }

            try
            {
                if (model.IsDirectory)
                {
                    if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
                }
                else
                {
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    if (!System.IO.File.Exists(fullPath))
                    {
                        await System.IO.File.WriteAllTextAsync(fullPath, model.Content ?? "", Encoding.UTF8);
                    }
                }

                return Ok(new { ok = true, message = "Created successfully", path = model.Path });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating item {Path}", model.Path);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/create-folder and /api/ide/folder/create
        [HttpPost("create-folder")]
        [HttpPost("folder/create")]
        public IActionResult CreateFolder([FromBody] CreateFolderModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để tạo thư mục.", needLogin = true });
            }

            string targetFolder = model?.Folder ?? model?.Path ?? "";
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                return BadRequest(new { ok = false, error = "Folder path is required" });
            }

            string root = ResolveWorkspacePath(model?.Project);
            if (!TryGetSafeFilePath(root, targetFolder, out string fullPath))
            {
                return BadRequest(new { ok = false, error = "Invalid path" });
            }

            try
            {
                if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
                return Ok(new { ok = true, message = "Folder created successfully", folder = targetFolder });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating folder {Folder}", targetFolder);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // DELETE /api/ide/file
        [HttpDelete("file")]
        public IActionResult DeleteItem([FromQuery] string path, [FromQuery] string? project)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để xóa tệp/thư mục.", needLogin = true });
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest(new { ok = false, error = "Path is required" });
            }

            string root = ResolveWorkspacePath(project);
            if (!TryGetSafeFilePath(root, path, out string fullPath))
            {
                return BadRequest(new { ok = false, error = "Invalid path" });
            }

            try
            {
                if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                }
                else if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                }

                return Ok(new { ok = true, message = "Deleted successfully", path });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting item {Path}", path);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/delete-file and /api/ide/item/delete
        [HttpPost("delete-file")]
        [HttpPost("item/delete")]
        public IActionResult DeleteFilePost([FromBody] DeleteFileModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để xóa tệp/thư mục.", needLogin = true });
            }

            string targetPath = model?.File ?? model?.Path ?? "";
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return BadRequest(new { ok = false, error = "Path is required" });
            }

            string root = ResolveWorkspacePath(model?.Project);
            if (!TryGetSafeFilePath(root, targetPath, out string fullPath))
            {
                return BadRequest(new { ok = false, error = "Invalid path" });
            }

            try
            {
                if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                }
                else if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                }

                return Ok(new { ok = true, message = "Deleted successfully", path = targetPath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting item {Path}", targetPath);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/rename and /api/ide/item/rename
        [HttpPost("rename")]
        [HttpPost("item/rename")]
        public IActionResult RenameItem([FromBody] IdeRenameRequestModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để đổi tên tệp/thư mục.", needLogin = true });
            }

            if (string.IsNullOrWhiteSpace(model?.OldPath) || string.IsNullOrWhiteSpace(model?.NewPath))
            {
                return BadRequest(new { ok = false, error = "Cả đường dẫn cũ và mới đều bắt buộc." });
            }

            string root = ResolveWorkspacePath(model.Project);
            if (!TryGetSafeFilePath(root, model.OldPath, out string fullOldPath) ||
                !TryGetSafeFilePath(root, model.NewPath, out string fullNewPath))
            {
                return BadRequest(new { ok = false, error = "Đường dẫn không hợp lệ." });
            }

            try
            {
                if (System.IO.File.Exists(fullOldPath))
                {
                    var parent = Path.GetDirectoryName(fullNewPath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    System.IO.File.Move(fullOldPath, fullNewPath, true);
                    return Ok(new { ok = true, message = "Đổi tên file thành công." });
                }
                else if (Directory.Exists(fullOldPath))
                {
                    Directory.Move(fullOldPath, fullNewPath);
                    return Ok(new { ok = true, message = "Đổi tên thư mục thành công." });
                }
                else
                {
                    return NotFound(new { ok = false, error = "Không tìm thấy file hoặc thư mục nguồn." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renaming {Old} to {New}", model.OldPath, model.NewPath);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/item/copy
        [HttpPost("item/copy")]
        public IActionResult CopyItem([FromBody] IdeCopyItemRequestModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để sao chép.", needLogin = true });
            }

            if (string.IsNullOrWhiteSpace(model?.SourcePath))
            {
                return BadRequest(new { ok = false, error = "Đường dẫn nguồn là bắt buộc." });
            }

            string root = ResolveWorkspacePath(model.Project);
            if (!TryGetSafeFilePath(root, model.SourcePath, out string fullSourcePath))
            {
                return BadRequest(new { ok = false, error = "Đường dẫn nguồn không hợp lệ." });
            }

            string targetFolder = model.TargetFolderPath ?? string.Empty;
            if (!TryGetSafeFilePath(root, targetFolder, out string fullTargetFolder))
            {
                return BadRequest(new { ok = false, error = "Thư mục đích không hợp lệ." });
            }

            if (!Directory.Exists(fullTargetFolder))
            {
                Directory.CreateDirectory(fullTargetFolder);
            }

            try
            {
                string itemName = Path.GetFileName(fullSourcePath);
                bool isDir = Directory.Exists(fullSourcePath);
                bool isFile = System.IO.File.Exists(fullSourcePath);

                if (!isDir && !isFile)
                {
                    return NotFound(new { ok = false, error = "Không tìm thấy file hoặc thư mục nguồn." });
                }

                string candidateDest = Path.Combine(fullTargetFolder, itemName);
                string finalDest = GetUniqueCopyPath(candidateDest, isDir);

                if (isFile)
                {
                    System.IO.File.Copy(fullSourcePath, finalDest, false);
                }
                else
                {
                    CopyDirectoryRecursively(fullSourcePath, finalDest);
                }

                string relativeCopiedPath = Path.GetRelativePath(root, finalDest).Replace('\\', '/');
                return Ok(new { ok = true, message = "Sao chép thành công.", path = relativeCopiedPath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error copying item {Source}", model.SourcePath);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // GET /api/ide/file/download?project=...&path=...
        [HttpGet("file/download")]
        public IActionResult DownloadSingleFile([FromQuery] string? project, [FromQuery] string? path)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, "Vui lòng đăng nhập.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("Đường dẫn file là bắt buộc.");
            }

            string root = ResolveWorkspacePath(project);
            if (!TryGetSafeFilePath(root, path, out string fullPath) || !System.IO.File.Exists(fullPath))
            {
                return NotFound("File không tồn tại.");
            }

            string fileName = Path.GetFileName(fullPath);
            return PhysicalFile(fullPath, "application/octet-stream", fileName);
        }

        private static void CopyDirectoryRecursively(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                System.IO.File.Copy(file, dest, true);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(dir));
                CopyDirectoryRecursively(dir, dest);
            }
        }

        private static string GetUniqueCopyPath(string targetPath, bool isDir)
        {
            if (isDir)
            {
                if (!Directory.Exists(targetPath)) return targetPath;
                string dirName = Path.GetFileName(targetPath);
                string parent = Path.GetDirectoryName(targetPath) ?? "";
                int counter = 1;
                while (true)
                {
                    string candidate = Path.Combine(parent, counter == 1 ? $"{dirName}-copy" : $"{dirName}-copy-{counter}");
                    if (!Directory.Exists(candidate)) return candidate;
                    counter++;
                }
            }
            else
            {
                if (!System.IO.File.Exists(targetPath)) return targetPath;
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(targetPath);
                string ext = Path.GetExtension(targetPath);
                string parent = Path.GetDirectoryName(targetPath) ?? "";
                int counter = 1;
                while (true)
                {
                    string candidate = Path.Combine(parent, counter == 1 ? $"{fileNameWithoutExt}-copy{ext}" : $"{fileNameWithoutExt}-copy-{counter}{ext}");
                    if (!System.IO.File.Exists(candidate)) return candidate;
                    counter++;
                }
            }
        }

        // ==========================================
        // 4. TERMINAL EXECUTION (SANDBOXED)
        // ==========================================

        // POST /api/ide/terminal
        [HttpPost("terminal")]
        public async Task<IActionResult> ExecuteTerminal([FromBody] TerminalCommandModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new
                {
                    ok = false,
                    error = "Terminal bị khóa. Vui lòng đăng nhập tài khoản để thực thi lệnh.",
                    needLogin = true
                });
            }

            if (model == null || string.IsNullOrWhiteSpace(model.Command))
            {
                return BadRequest(new { ok = false, error = "Command is required" });
            }

            string rawCmd = model.Command.Trim();

            // Strict sandbox & command injection prevention
            if (rawCmd.Contains("..") || rawCmd.Contains("cd /") || rawCmd.Contains("cd \\") ||
                rawCmd.Contains("rm -rf /") || rawCmd.Contains("del /s") || rawCmd.Contains("format "))
            {
                return BadRequest(new
                {
                    ok = false,
                    error = "Lệnh chứa ký tự điều hướng hoặc thao tác nguy hiểm không được phép trong sandbox."
                });
            }

            string binary = rawCmd.Split(new[] { ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant().Trim('(', '{', '$', '"', '\'');

            if (!WhitelistedCommands.Contains(binary))
            {
                return BadRequest(new
                {
                    ok = false,
                    error = $"Lệnh '{binary}' không nằm trong danh sách an toàn."
                });
            }

            string root = ResolveWorkspacePath(model.Project);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return BadRequest(new { ok = false, error = "Thư mục dự án chưa được tạo hoặc không tồn tại." });
            }

            try
            {
                bool isWindows = OperatingSystem.IsWindows();
                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (isWindows)
                {
                    string script = $"$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8;\n{rawCmd}";
                    byte[] bytes = Encoding.Unicode.GetBytes(script);
                    string encoded = Convert.ToBase64String(bytes);

                    psi.FileName = "powershell.exe";
                    psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";
                }
                else
                {
                    psi.FileName = "/bin/bash";
                    psi.Arguments = $"-c \"{rawCmd.Replace("\"", "\\\"")}\"";
                }

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return StatusCode(500, new { ok = false, error = "Không thể khởi chạy tiến trình." });
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                string stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);

                string combined = (stdout + "\n" + stderr).Trim();

                return Ok(new
                {
                    ok = true,
                    exitCode = proc.ExitCode,
                    output = combined,
                    command = rawCmd
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(408, new { ok = false, error = "Lệnh chạy vượt quá thời gian cho phép (30s timeout)." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Terminal execution error");
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // ==========================================
        // 5. BTOAN AGENT FULL-PROJECT CHAT & RUN
        // ==========================================

        // POST /api/ide/agent-chat and /api/ide/agent-run
        [HttpPost("agent-chat")]
        [HttpPost("agent-run")]
        public async Task<IActionResult> AgentChat([FromBody] AgentChatRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Prompt))
            {
                return BadRequest(new { ok = false, error = "Prompt is required" });
            }

            int userId = CurrentUserId > 0 ? CurrentUserId : 1;
            string cleanProj = SanitizeProjectName(model.Project);
            string projectPath = ResolveWorkspacePath(cleanProj);
            if (!Directory.Exists(projectPath))
            {
                string user1Path = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", "user_1", cleanProj);
                if (Directory.Exists(user1Path)) projectPath = user1Path;
            }
            if (!Directory.Exists(projectPath))
            {
                return BadRequest(new { ok = false, error = $"Không tìm thấy thư mục dự án '{cleanProj}'." });
            }

            string selectedModel = !string.IsNullOrWhiteSpace(model.Model) ? model.Model : "gemini-2.5-flash";
            if (string.Equals(selectedModel, "btoan-ai-pro-2.5", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(selectedModel, "gemini-2.5-pro", StringComparison.OrdinalIgnoreCase))
            {
                selectedModel = "gemini-2.5-pro";
            }
            else
            {
                selectedModel = "gemini-2.5-flash";
            }
            bool isFastMode = string.Equals(model.Mode, "fast", StringComparison.OrdinalIgnoreCase);

            var sbContext = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(model.OpenFilePath) || !string.IsNullOrWhiteSpace(model.ActiveFile))
            {
                string openPath = !string.IsNullOrWhiteSpace(model.OpenFilePath) ? model.OpenFilePath : model.ActiveFile!;
                string fPath = Path.Combine(projectPath, openPath.TrimStart('/', '\\'));
                if (System.IO.File.Exists(fPath))
                {
                    try
                    {
                        string openContent = await System.IO.File.ReadAllTextAsync(fPath, Encoding.UTF8);
                        sbContext.AppendLine($"--- CURRENT OPEN FILE: {openPath} ---");
                        sbContext.AppendLine(openContent);
                        sbContext.AppendLine("--- END FILE ---");
                    }
                    catch { }
                }
            }

            string agentSystemInstruction = $@"Bạn là Btoan AI Autonomous Coding Agent trong Btoan Studio IDE.
Bạn đang làm việc trực tiếp trên thư mục dự án: '{cleanProj}'.

CÁC CÔNG CỤ THỰC THI THẬT BẠN ĐƯỢC CẤP:
- list_files(directory): Xem danh sách tệp/thư mục.
- read_file(path): Đọc mã nguồn tệp.
- write_file(path, content): Tạo mới hoặc ghi đè tệp với code hoàn chỉnh 100%.
- edit_file(path, target, replacement): Sửa đoạn code trong tệp.
- delete_file(path): Xóa tệp.
- run_command(command): Chạy lệnh shell (dotnet build, dotnet add package, v.v.) tại thư mục dự án.

QUY TẮC BẮT BUỘC THỰC THI (AUTONOMOUS TOOL LOOP):
1. BẮT BUỘC PHẢI DÙNG CÔNG CỤ:
   - Khi người dùng yêu cầu sửa đổi, thêm chức năng, sửa cơ sở dữ liệu: Bạn BẮT BUỘC phải dùng read_file, write_file, edit_file hoặc run_command để trực tiếp đọc và sửa file thật trên ổ đĩa.
   - TUYỆT ĐỐI KHÔNG ĐƯỢC CHỈ TRẢ LỜI BẰNG CHỮ HAY IN RA JSON PLAN mà không gọi tool! Mọi kế hoạch phải được bạn TỰ ĐỘNG THỰC THI NGAY LẬP TỨC.
2. NẾU CẦN THÊM DATABASE HAY PACKAGE NUGET:
   - Dùng run_command chạy: dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.0
   - Tạo Model trong thư mục Models/, tạo AppDbContext trong Data/, cập nhật Program.cs và appsettings.json.
3. SAU KHI SỬA HOẶC TẠO FILE XONG:
   - Nếu dự án có file .csproj (.NET): Bạn BẮT BUỘC phải gọi tool run_command chạy 'dotnet build' để kiểm tra kết quả biên dịch.
   - Nếu kết quả build có lỗi (Error), đọc kỹ lỗi và tự động dùng edit_file / write_file để sửa code cho đến khi build đạt 0 Error.
4. CÂU TRẢ LỜI CUỐI CÙNG:
   - Khi đã thực hiện xong mọi công cụ và build thành công, hãy trả lời bằng tiếng Việt tự nhiên, tóm tắt rõ ràng các file bạn đã tạo/sửa và kết quả build.
   - TUYỆT ĐỐI KHÔNG in ra chuỗi JSON kế hoạch thô vào câu trả lời!";

            var toolsDefinition = new JsonArray
            {
                new JsonObject
                {
                    ["functionDeclarations"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "list_files",
                            ["description"] = "Liệt kê danh sách các tệp và thư mục trong dự án.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["directory"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn tương đối trong dự án (ví dụ: '.' hoặc 'Controllers' hoặc 'Views')."
                                    }
                                }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "read_file",
                            ["description"] = "Đọc toàn bộ nội dung của một tệp trong dự án.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn tương đối của tệp trong dự án (ví dụ: 'Program.cs', 'Controllers/HomeController.cs')."
                                    }
                                },
                                ["required"] = new JsonArray { "path" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "write_file",
                            ["description"] = "Tạo mới hoặc ghi đè toàn bộ nội dung của một tệp trong dự án. Tự động tạo các thư mục cha nếu chưa tồn tại.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn tương đối của tệp trong dự án (ví dụ: 'Controllers/HomeController.cs', 'Views/Home/About.cshtml')."
                                    },
                                    ["content"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Toàn bộ nội dung hoàn chỉnh của tệp."
                                    }
                                },
                                ["required"] = new JsonArray { "path", "content" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "edit_file",
                            ["description"] = "Chỉnh sửa một đoạn mã nguồn trong tệp bằng cách tìm chính xác đoạn target và thay thế bằng replacement.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn tương đối của tệp cần sửa."
                                    },
                                    ["target"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đoạn mã nguồn hiện tại trong tệp cần được thay thế."
                                    },
                                    ["replacement"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đoạn mã nguồn mới thay thế vào."
                                    }
                                },
                                ["required"] = new JsonArray { "path", "target", "replacement" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "delete_file",
                            ["description"] = "Xóa một tệp trong dự án.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn tương đối của tệp cần xóa."
                                    }
                                },
                                ["required"] = new JsonArray { "path" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "run_command",
                            ["description"] = "Thực thi lệnh shell (PowerShell/Bash) ngay tại thư mục dự án (ví dụ: 'dotnet build', 'dotnet add package ... --version 10.0.0').",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["command"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Câu lệnh shell cần chạy."
                                    }
                                },
                                ["required"] = new JsonArray { "command" }
                            }
                        }
                    }
                }
            };

            var messages = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["text"] = $"{agentSystemInstruction}\n\n[CONTEXT FILES]:\n{sbContext}\n\n[USER REQUEST]: {model.Prompt}"
                        }
                    }
                }
            };

            var modifiedFiles = new List<string>();
            var executedCommands = new List<string>();
            var toolOperations = new List<object>();
            var planSteps = new List<string>();
            bool buildPassed = false;
            string finalExplanation = "";

            int maxTurns = 15;
            int currentTurn = 0;

            try
            {
                while (currentTurn < maxTurns)
                {
                    currentTurn++;

                    var payload = new JsonObject
                    {
                        ["contents"] = messages.DeepClone(),
                        ["tools"] = toolsDefinition.DeepClone(),
                        ["generationConfig"] = new JsonObject
                        {
                            ["temperature"] = isFastMode ? 0.2 : 0.4,
                            ["maxOutputTokens"] = 8192
                        }
                    };
                    if (isFastMode)
                    {
                        payload["generationConfig"]!["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
                    }

                    var (success, resData, _, error) = await _geminiService.GenerateContentAsync(selectedModel, payload);
                    if (!success || resData == null)
                    {
                        finalExplanation = $"Lỗi kết nối AI: {error}";
                        break;
                    }

                    var candidate = resData["candidates"]?[0]?["content"];
                    if (candidate == null)
                    {
                        finalExplanation = "Không nhận được phản hồi từ AI.";
                        break;
                    }

                    var parts = candidate["parts"]?.AsArray();
                    if (parts == null || parts.Count == 0)
                    {
                        break;
                    }

                    // Check if there is a functionCall in any part
                    JsonObject? functionCallPart = null;
                    string? candidateText = null;

                    foreach (var p in parts)
                    {
                        if (p is JsonObject pObj)
                        {
                            if (pObj.ContainsKey("functionCall"))
                            {
                                functionCallPart = pObj["functionCall"]?.AsObject();
                                break;
                            }
                            if (pObj.ContainsKey("text") && string.IsNullOrWhiteSpace(candidateText))
                            {
                                candidateText = pObj["text"]?.ToString();
                            }
                        }
                    }

                    if (functionCallPart != null)
                    {
                        string toolName = functionCallPart["name"]?.ToString() ?? "";
                        var toolArgs = functionCallPart["args"]?.AsObject();

                        _logger.LogInformation("Agent executed Tool [{Turn}/{Max}]: {ToolName}", currentTurn, maxTurns, toolName);

                        // Execute the tool on the real workspace filesystem
                        string toolResult = await ExecuteAgentToolAsync(projectPath, toolName, toolArgs, modifiedFiles, executedCommands, b => buildPassed = b);

                        // Record plan step and tool operation
                        if (toolName == "write_file" || toolName == "edit_file")
                        {
                            string fPath = toolArgs?["path"]?.ToString() ?? "";
                            toolOperations.Add(new
                            {
                                action = toolName == "write_file" ? "create" : "edit",
                                path = fPath,
                                summary = $"Cập nhật tệp {fPath}"
                            });
                            planSteps.Add($"Cập nhật tệp '{fPath}'");
                        }
                        else if (toolName == "run_command")
                        {
                            string cmd = toolArgs?["command"]?.ToString() ?? "";
                            planSteps.Add($"Thực thi lệnh '{cmd}'");
                        }
                        else if (toolName == "read_file")
                        {
                            string fPath = toolArgs?["path"]?.ToString() ?? "";
                            planSteps.Add($"Đọc nội dung tệp '{fPath}'");
                        }

                        // Append assistant message with functionCall
                        messages.Add(candidate.DeepClone());

                        // Append functionResponse message
                        messages.Add(new JsonObject
                        {
                            ["role"] = "function",
                            ["parts"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["functionResponse"] = new JsonObject
                                    {
                                        ["name"] = toolName,
                                        ["response"] = new JsonObject
                                        {
                                            ["output"] = toolResult
                                        }
                                    }
                                }
                            }
                        });
                    }
                    else
                    {
                        // Gemini returned text without functionCall -> finished!
                        finalExplanation = candidateText ?? "";
                        break;
                    }
                }

                // Clean raw JSON if AI leaked JSON Plan into explanation
                if (!string.IsNullOrWhiteSpace(finalExplanation))
                {
                    string trimmed = finalExplanation.Trim();
                    if (trimmed.StartsWith("{") && trimmed.Contains("\"plan\""))
                    {
                        try
                        {
                            var parsedJson = JsonNode.Parse(trimmed);
                            string exp = parsedJson?["explanation"]?.ToString() ?? "";
                            if (!string.IsNullOrWhiteSpace(exp))
                            {
                                finalExplanation = exp;
                            }
                            else
                            {
                                finalExplanation = $"Đã hoàn tất cập nhật dự án '{cleanProj}' theo yêu cầu.";
                            }
                        }
                        catch { }
                    }
                }

                if (planSteps.Count == 0)
                {
                    planSteps.Add("Kiểm tra và cập nhật mã nguồn dự án");
                    if (buildPassed) planSteps.Add("Biên dịch kiểm tra dotnet build: Thành công");
                }

                if (string.IsNullOrWhiteSpace(finalExplanation))
                {
                    finalExplanation = modifiedFiles.Count > 0 
                        ? $"Đã thực hiện xong các chỉnh sửa trên {modifiedFiles.Count} tệp trong dự án '{cleanProj}'."
                        : $"Đã kiểm tra dự án '{cleanProj}'.";
                }

                return Ok(new
                {
                    ok = true,
                    projectName = cleanProj,
                    plan = planSteps,
                    explanation = finalExplanation,
                    modifiedFiles = modifiedFiles,
                    operations = toolOperations,
                    buildPassed = buildPassed,
                    executedCommands = executedCommands
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent chat execution error");
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        private static async Task<string> ExecuteAgentToolAsync(string projectPath, string toolName, JsonObject? args, List<string> modifiedFiles, List<string> executedCommands, Action<bool> setBuildPassed)
        {
            try
            {
                switch (toolName)
                {
                    case "list_files":
                    {
                        string dir = args?["directory"]?.ToString() ?? ".";
                        string targetDir = Path.GetFullPath(Path.Combine(projectPath, dir.TrimStart('/', '\\')));
                        if (!targetDir.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "Error: Cannot access outside project directory";
                        }
                        if (!Directory.Exists(targetDir)) return $"Directory '{dir}' does not exist.";
                        var entries = Directory.GetFileSystemEntries(targetDir)
                            .Select(e => {
                                string rel = Path.GetRelativePath(projectPath, e).Replace('\\', '/');
                                return Directory.Exists(e) ? rel + "/" : rel;
                            })
                            .Take(100);
                        return string.Join("\n", entries);
                    }
                    case "read_file":
                    {
                        string path = args?["path"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(path)) return "Error: path is required";
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path.TrimStart('/', '\\')));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "Error: Cannot access outside project directory";
                        }
                        if (!System.IO.File.Exists(fullPath)) return $"Error: File '{path}' does not exist.";
                        return await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                    }
                    case "write_file":
                    {
                        string path = args?["path"]?.ToString() ?? "";
                        string content = args?["content"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(path)) return "Error: path is required";
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path.TrimStart('/', '\\')));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "Error: Cannot access outside project directory";
                        }
                        string dir = Path.GetDirectoryName(fullPath)!;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        await System.IO.File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
                        string relPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                        if (!modifiedFiles.Contains(relPath)) modifiedFiles.Add(relPath);
                        return $"Successfully written file: {relPath} ({content.Length} bytes)";
                    }
                    case "edit_file":
                    {
                        string path = args?["path"]?.ToString() ?? "";
                        string target = args?["target"]?.ToString() ?? "";
                        string replacement = args?["replacement"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(path)) return "Error: path is required";
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path.TrimStart('/', '\\')));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "Error: Cannot access outside project directory";
                        }
                        if (!System.IO.File.Exists(fullPath)) return $"Error: File '{path}' does not exist.";
                        string existing = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                        if (!existing.Contains(target))
                        {
                            return $"Error: target string not found in '{path}'. Use read_file to check exact text.";
                        }
                        string updated = existing.Replace(target, replacement);
                        await System.IO.File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8);
                        string relPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                        if (!modifiedFiles.Contains(relPath)) modifiedFiles.Add(relPath);
                        return $"Successfully edited file: {relPath}";
                    }
                    case "delete_file":
                    {
                        string path = args?["path"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(path)) return "Error: path is required";
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path.TrimStart('/', '\\')));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "Error: Cannot access outside project directory";
                        }
                        if (System.IO.File.Exists(fullPath))
                        {
                            System.IO.File.Delete(fullPath);
                            string relPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                            if (!modifiedFiles.Contains(relPath)) modifiedFiles.Add(relPath);
                            return $"Successfully deleted: {relPath}";
                        }
                        return $"File '{path}' does not exist.";
                    }
                    case "run_command":
                    {
                        string command = args?["command"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(command)) return "Error: command is required";
                        executedCommands.Add(command);
                        var (ok, output) = await RunCliProcessAsync(projectPath, command, "", 60);
                        if (command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) && ok)
                        {
                            setBuildPassed(true);
                        }
                        return $"ExitCode: {(ok ? 0 : 1)}\nOutput:\n{output}";
                    }
                    default:
                        return $"Unknown tool: {toolName}";
                }
            }
            catch (Exception ex)
            {
                return $"Tool execution error: {ex.Message}";
            }
        }

        private static string DetectLanguage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".cs" => "csharp",
                ".js" => "javascript",
                ".ts" => "typescript",
                ".html" or ".htm" => "html",
                ".css" => "css",
                ".json" => "json",
                ".py" => "python",
                ".md" => "markdown",
                ".sql" => "sql",
                ".xml" or ".csproj" => "xml",
                ".sh" => "shell",
                _ => "plaintext"
            };
        }

        // ==========================================
        // 6. SQLITE DATABASE VIEWER & QUERY APIS
        // ==========================================

        // GET /api/ide/db/tables?project=...&path=...
        [HttpGet("db/tables")]
        public async Task<IActionResult> GetDbTables([FromQuery] string? project, [FromQuery] string? path)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để thao tác cơ sở dữ liệu.", needLogin = true });
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest(new { ok = false, error = "Đường dẫn file database là bắt buộc." });
            }

            string wsPath = ResolveWorkspacePath(project);
            if (!TryGetSafeFilePath(wsPath, path, out string fullDbPath) || !System.IO.File.Exists(fullDbPath))
            {
                return NotFound(new { ok = false, error = "Không tìm thấy file cơ sở dữ liệu." });
            }

            try
            {
                var tables = new List<object>();
                using var conn = new SqliteConnection($"Data Source={fullDbPath};Pooling=False");
                await conn.OpenAsync();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                    using var reader = await cmd.ExecuteReaderAsync();
                    var tableNames = new List<string>();
                    while (await reader.ReadAsync())
                    {
                        tableNames.Add(reader.GetString(0));
                    }
                    reader.Close();

                    foreach (var tName in tableNames)
                    {
                        var cols = new List<object>();
                        using var pragmaCmd = conn.CreateCommand();
                        pragmaCmd.CommandText = $"PRAGMA table_info(\"{tName.Replace("\"", "\"\"")}\");";
                        using var pReader = await pragmaCmd.ExecuteReaderAsync();
                        while (await pReader.ReadAsync())
                        {
                            cols.Add(new
                            {
                                name = pReader["name"]?.ToString() ?? "",
                                type = pReader["type"]?.ToString() ?? "",
                                notNull = Convert.ToInt32(pReader["notnull"]) == 1,
                                pk = Convert.ToInt32(pReader["pk"]) == 1
                            });
                        }
                        pReader.Close();

                        long count = 0;
                        try
                        {
                            using var countCmd = conn.CreateCommand();
                            countCmd.CommandText = $"SELECT COUNT(*) FROM \"{tName.Replace("\"", "\"\"")}\";";
                            var obj = await countCmd.ExecuteScalarAsync();
                            if (obj != null && long.TryParse(obj.ToString(), out long c)) count = c;
                        }
                        catch { }

                        tables.Add(new { name = tName, columns = cols, rowCount = count });
                    }
                }

                return Ok(new { ok = true, tables });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi đọc metadata SQLite: {Path}", path);
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/db/query
        [HttpPost("db/query")]
        public async Task<IActionResult> ExecuteDbQuery([FromBody] DbQueryRequestModel req)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để truy vấn cơ sở dữ liệu.", needLogin = true });
            }

            if (req == null || string.IsNullOrWhiteSpace(req.Path))
            {
                return BadRequest(new { ok = false, error = "Đường dẫn file database là bắt buộc." });
            }

            if (string.IsNullOrWhiteSpace(req.Sql))
            {
                return BadRequest(new { ok = false, error = "Câu lệnh SQL không được trống." });
            }

            string wsPath = ResolveWorkspacePath(req.Project);
            if (!TryGetSafeFilePath(wsPath, req.Path, out string fullDbPath) || !System.IO.File.Exists(fullDbPath))
            {
                return NotFound(new { ok = false, error = "Không tìm thấy file cơ sở dữ liệu." });
            }

            string trimmedSql = req.Sql.Trim();
            bool isRead = Regex.IsMatch(trimmedSql, @"^\s*(SELECT|PRAGMA|WITH|EXPLAIN)\b", RegexOptions.IgnoreCase);
            var sw = Stopwatch.StartNew();

            try
            {
                using var conn = new SqliteConnection($"Data Source={fullDbPath};Pooling=False");
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = trimmedSql;
                cmd.CommandTimeout = 10;

                if (isRead)
                {
                    using var reader = await cmd.ExecuteReaderAsync();
                    var columns = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        columns.Add(reader.GetName(i));
                    }

                    var rows = new List<Dictionary<string, object?>>();
                    int count = 0;
                    while (await reader.ReadAsync() && count < 500)
                    {
                        var row = new Dictionary<string, object?>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            object val = reader.GetValue(i);
                            row[columns[i]] = val == DBNull.Value ? null : val;
                        }
                        rows.Add(row);
                        count++;
                    }

                    sw.Stop();
                    return Ok(new
                    {
                        ok = true,
                        isQuery = true,
                        columns,
                        rows,
                        rowCount = rows.Count,
                        elapsedMs = sw.ElapsedMilliseconds
                    });
                }
                else
                {
                    using var tx = conn.BeginTransaction();
                    cmd.Transaction = tx;
                    int affected = await cmd.ExecuteNonQueryAsync();
                    tx.Commit();
                    sw.Stop();

                    return Ok(new
                    {
                        ok = true,
                        isQuery = false,
                        rowsAffected = affected,
                        elapsedMs = sw.ElapsedMilliseconds,
                        message = $"Thực thi thành công. {affected} dòng bị ảnh hưởng."
                    });
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return BadRequest(new { ok = false, error = ex.Message, elapsedMs = sw.ElapsedMilliseconds });
            }
        }

        // ==========================================
        // 7. DYNAMIC PORT ALLOCATION API
        // ==========================================

        // GET /api/ide/allocate-port?project=...
        [HttpGet("allocate-port")]
        public IActionResult AllocateDynamicPort([FromQuery] string? project)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập.", needLogin = true });
            }

            try
            {
                int port = RunningAppSessionManager.AllocatePort(CurrentUserId, project ?? "app");
                return Ok(new { ok = true, port });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }
    }

    // ==========================================
    // MODELS
    // ==========================================

    public class IdeCreateProjectRequestModel
    {
        public string? Name { get; set; }
        public string? Template { get; set; }
        public int? UserId { get; set; }
        public int? User_Id { get => UserId; set => UserId = value; }
    }

    public class DeleteProjectModel
    {
        public string? Name { get; set; }
        public int? UserId { get; set; }
        public int? User_Id { get => UserId; set => UserId = value; }
    }

    public class RenameProjectModel
    {
        public string? OldName { get; set; }
        public string? NewName { get; set; }
        public int? UserId { get; set; }
        public int? User_Id { get => UserId; set => UserId = value; }
    }

    public class SaveFileModel
    {
        public string? Project { get; set; }
        public string Path { get; set; } = string.Empty;
        public string? File { get; set; }
        public string? Content { get; set; }
    }

    public class CreateItemModel
    {
        public string? Project { get; set; }
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public string? Content { get; set; }
    }

    public class CreateFolderModel
    {
        public string? Project { get; set; }
        public string? Folder { get; set; }
        public string? Path { get; set; }
    }

    public class TerminalCommandModel
    {
        public string? Project { get; set; }
        public string Command { get; set; } = string.Empty;
    }

    public class BatchOperationsModel
    {
        public string Project { get; set; } = string.Empty;
        public List<FileOperationItem> Operations { get; set; } = new();
        public List<ImageGenerationItem>? Images { get; set; }
    }

    public class ImageGenerationItem
    {
        public string Path { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
    }

    public class GenerateProjectImageRequest
    {
        public string Project { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string Prompt { get; set; } = string.Empty;
    }

    public class FileOperationItem
    {
        public string Action { get; set; } = "create";
        public string Path { get; set; } = string.Empty;
        public string? Content { get; set; }
        public string? Summary { get; set; }
    }

    public class AgentChatRequestModel
    {
        public string Prompt { get; set; } = string.Empty;
        public string? Mode { get; set; } = "fast";
        public string? Model { get; set; } = "gemini-2.5-flash";
        public string? Project { get; set; }
        public string? OpenFilePath { get; set; }
        public string? ActiveFile { get; set; }
        public string? OpenFileContent { get; set; }
        public List<ContextFileModel>? ContextFiles { get; set; }
    }

    public class ContextFileModel
    {
        public string Path { get; set; } = string.Empty;
        public string? Content { get; set; }
    }

    public class DeleteFileModel
    {
        public string? Project { get; set; }
        public string? File { get; set; }
        public string? Path { get; set; }
    }

    public class IdeRenameRequestModel
    {
        public string? Project { get; set; }
        public string? OldPath { get; set; }
        public string? NewPath { get; set; }
    }

    public class IdeCopyItemRequestModel
    {
        public string? Project { get; set; }
        public string? SourcePath { get; set; }
        public string? TargetFolderPath { get; set; }
    }

    public class DbQueryRequestModel
    {
        public string? Project { get; set; }
        public string? Path { get; set; }
        public string? Sql { get; set; }
    }

    public class GitProjectRequestModel
    {
        public string? Project { get; set; }
    }

    public class GitStageRequestModel
    {
        public string? Project { get; set; }
        public string? Path { get; set; }
        public bool All { get; set; }
    }

    public class GitCommitRequestModel
    {
        public string? Project { get; set; }
        public string? Message { get; set; }
    }

    public class GitCheckoutRequestModel
    {
        public string? Project { get; set; }
        public string? Branch { get; set; }
        public bool CreateNew { get; set; }
    }

    public class GitPushRequestModel
    {
        public string? Project { get; set; }
        public string? RemoteUrl { get; set; }
        public string? Branch { get; set; }
        public string? Token { get; set; }
    }

    public class GlobalReplaceRequestModel
    {
        public string? Project { get; set; }
        public string Query { get; set; } = string.Empty;
        public string? ReplaceWith { get; set; }
        public bool IsRegex { get; set; }
        public bool MatchCase { get; set; }
    }

    public class SecretPairModel
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class ProjectSecretsRequestModel
    {
        public string? Project { get; set; }
        public List<SecretPairModel> Secrets { get; set; } = new();
    }
}
