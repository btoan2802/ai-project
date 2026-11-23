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
            "dotnet", "npm", "node", "python", "git", "dir", "ls", "echo", "pwd", "whoami", "cat", "type"
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
                return 0; // Strict: ignore X-User-Id
            }
        }

        private string? CurrentUsername
        {
            get
            {
                var name = User.FindFirst(ClaimTypes.Name)?.Value;
                if (!string.IsNullOrWhiteSpace(name)) return name;
                return HttpContext.Session.GetString("username");
            }
        }

        private bool IsAuthenticated => CurrentUserId > 0;

        private string GetUserRootPath()
        {
            if (!IsAuthenticated) return string.Empty;
            string root = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", $"user_{CurrentUserId}");
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }
            return root;
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
        public IActionResult CreateProject([FromBody] IdeCreateProjectRequestModel model)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập tài khoản để tạo dự án.", needLogin = true });
            }

            string cleanName = SanitizeProjectName(model?.Name);
            string userRoot = GetUserRootPath();
            string projectPath = Path.Combine(userRoot, cleanName);

            if (Directory.Exists(projectPath))
            {
                return BadRequest(new { ok = false, error = $"Dự án '{cleanName}' đã tồn tại trong không gian của bạn. Hãy chọn tên khác (ví dụ: {cleanName}-1)." });
            }

            try
            {
                Directory.CreateDirectory(projectPath);

                // Optional starter template if requested
                if (string.Equals(model?.Template, "html5", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(model?.Template, "web", StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.WriteAllText(Path.Combine(projectPath, "index.html"), "<!DOCTYPE html>\n<html>\n<head>\n    <title>My Web Project</title>\n</head>\n<body>\n    <h1>Welcome to My Web Project</h1>\n</body>\n</html>", Encoding.UTF8);
                }

                return Ok(new
                {
                    ok = true,
                    project = cleanName,
                    message = $"Tạo dự án '{cleanName}' thành công!"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating project {Name}", cleanName);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // DELETE /api/ide/projects/{name} and POST /api/ide/projects/delete
        [HttpDelete("projects/{name}")]
        [HttpPost("projects/delete")]
        public IActionResult DeleteProject(string? name, [FromBody] DeleteProjectModel? bodyModel)
        {
            if (!IsAuthenticated)
            {
                return StatusCode(401, new { ok = false, error = "Vui lòng đăng nhập để thực hiện thao tác này.", needLogin = true });
            }

            string targetName = !string.IsNullOrWhiteSpace(name) ? name : bodyModel?.Name ?? "";
            string cleanName = SanitizeProjectName(targetName);
            string projectPath = ResolveWorkspacePath(cleanName);

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
            if (!IsAuthenticated)
            {
                return Content("<h3>Vui lòng đăng nhập để xem trước dự án.</h3>", "text/html; charset=utf-8");
            }

            string cleanProj = SanitizeProjectName(project);
            string projectPath = ResolveWorkspacePath(cleanProj);

            if (!Directory.Exists(projectPath))
            {
                return NotFound($"Không tìm thấy dự án '{cleanProj}'.");
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

        // GET /api/ide/git-status?project=...
        [HttpGet("git-status")]
        public async Task<IActionResult> GetGitStatus([FromQuery] string? project)
        {
            if (!IsAuthenticated)
            {
                return Ok(new { ok = true, branch = "main", count = 0, files = Array.Empty<object>() });
            }

            string root = ResolveWorkspacePath(project);
            if (!Directory.Exists(root))
            {
                return Ok(new { ok = true, branch = "main", count = 0, files = Array.Empty<object>() });
            }

            string branch = "main";
            var modifiedFiles = new List<object>();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "status --porcelain",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var l in lines)
                        {
                            if (l.Length >= 4)
                            {
                                string status = l.Substring(0, 2).Trim();
                                string filePath = l.Substring(3).Trim();
                                modifiedFiles.Add(new { path = filePath, status = status });
                            }
                        }
                    }
                }

                var psiBranch = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse --abbrev-ref HEAD",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var procB = Process.Start(psiBranch);
                if (procB != null)
                {
                    string bOut = await procB.StandardOutput.ReadToEndAsync();
                    await procB.WaitForExitAsync();
                    if (!string.IsNullOrWhiteSpace(bOut)) branch = bOut.Trim();
                }
            }
            catch { }

            return Ok(new
            {
                ok = true,
                branch,
                count = modifiedFiles.Count,
                files = modifiedFiles
            });
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
  <text x=""50%"" y=""{height/2 + 70}"" font-family=""'Inter',-apple-system,sans-serif"" font-size=""12"" fill=""#94a3b8"" text-anchor=""middle"">Generated with Antigravity AI</text>
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
                rawCmd.Contains("&&") || rawCmd.Contains(";") || rawCmd.Contains("|") || rawCmd.Contains(">") || rawCmd.Contains("<"))
            {
                return BadRequest(new
                {
                    ok = false,
                    error = "Lệnh chứa ký tự điều hướng hoặc ống dẫn không được phép trong sandbox."
                });
            }

            string binary = rawCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

            if (!WhitelistedCommands.Contains(binary))
            {
                return BadRequest(new
                {
                    ok = false,
                    error = $"Lệnh '{binary}' không nằm trong danh sách an toàn (dotnet, node, python, npm, git, dir, ls, echo, pwd, whoami, cat, type)."
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
                    FileName = isWindows ? "cmd.exe" : "/bin/bash",
                    Arguments = isWindows ? $"/c {rawCmd}" : $"-c \"{rawCmd.Replace("\"", "\\\"")}\"",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

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
        // 5. ANTIGRAVITY AGENT FULL-PROJECT CHAT & RUN
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

            if (string.IsNullOrWhiteSpace(model.OpenFilePath) && !string.IsNullOrWhiteSpace(model.ActiveFile))
            {
                model.OpenFilePath = model.ActiveFile;
            }

            if (string.IsNullOrWhiteSpace(model.OpenFileContent) && !string.IsNullOrWhiteSpace(model.OpenFilePath))
            {
                try
                {
                    string root = ResolveWorkspacePath(model.Project);
                    string fPath = Path.Combine(root, model.OpenFilePath.TrimStart('/', '\\'));
                    if (System.IO.File.Exists(fPath))
                    {
                        model.OpenFileContent = await System.IO.File.ReadAllTextAsync(fPath);
                    }
                }
                catch { }
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
            if (!string.IsNullOrWhiteSpace(model.OpenFilePath) && !string.IsNullOrWhiteSpace(model.OpenFileContent))
            {
                sbContext.AppendLine($"--- CURRENT OPEN FILE: {model.OpenFilePath} ---");
                sbContext.AppendLine(model.OpenFileContent);
                sbContext.AppendLine("--- END FILE ---");
            }

            if (model.ContextFiles != null && model.ContextFiles.Count > 0)
            {
                foreach (var cf in model.ContextFiles)
                {
                    if (cf != null && !string.IsNullOrWhiteSpace(cf.Path))
                    {
                        sbContext.AppendLine($"--- CONTEXT FILE: {cf.Path} ---");
                        sbContext.AppendLine(cf.Content ?? "");
                        sbContext.AppendLine("--- END FILE ---");
                    }
                }
            }

            string systemPrompt = @"
Bạn là Antigravity Coding Agent - Trợ lý lập trình siêu cấp trong Btoan Antigravity Web IDE.
Nhiệm vụ của bạn:
1. Nếu người dùng yêu cầu sửa đổi/giải thích một file hiện tại: Phân tích file và trả về các thay đổi (action: 'edit').
2. Nếu người dùng yêu cầu TẠO NGUYÊN MỘT DỰ ÁN MỚI:
   - Đặt một tên dự án ngắn gọn bằng tiếng Anh/không dấu (ví dụ: 'shop-fullstack', 'my-webapi', 'love-letter', 'admin-dashboard').
   - QUY TẮC DÙNG CLI SCAFFOLDING ('setupCommands'):
     + Khi người dùng yêu cầu ASP.NET Core MVC: Sử dụng 'dotnet new mvc' trong 'setupCommands' (không tự chế tay file .csproj sơ sài).
     + Khi người dùng yêu cầu ASP.NET Core Web API: Sử dụng 'dotnet new webapi --use-controllers' trong 'setupCommands'.
     + Khi người dùng yêu cầu Angular: Sử dụng 'npx -y @angular/cli new frontend --routing --style=css --ssr=false --skip-git --skip-install' trong 'setupCommands' (luôn gắn cờ non-interactive & --skip-install để tạo bộ file mã nguồn siêu nhanh trong 2-3s mà không bị treo tải node_modules).
     + Khi người dùng yêu cầu Fullstack (.NET Web API + Angular): Đưa 2 lệnh vào 'setupCommands':
       'dotnet new webapi --use-controllers -n backend'
       'npx -y @angular/cli new frontend --routing --style=css --ssr=false --skip-git --skip-install'
     + Sau đó mới đưa code nghiệp vụ tùy chỉnh (Controllers, Models, Components, v.v.) vào mảng 'operations'.
     + Nếu là web tĩnh thuần HTML/CSS/JS: để 'setupCommands': [] và tạo trực tiếp index.html, css/style.css, js/app.js qua 'operations'.
   - KHÔNG ĐƯỢC để nội dung giả kiểu '// code here', viết code hoàn chỉnh, responsive, đẹp mắt và chạy thật 100%!
   - ĐỊNH DẠNG CODE BẮT BUỘC (QUY TẮC CHỐNG DỒN DÒNG / MINIFIED):
     + TUYỆT ĐỐI KHÔNG nén/minify code lên 1 dòng duy nhất!
     + Mọi file code (.html, .css, .js, .cs, .json, .py, v.v.) BẮT BUỘC phải xuống dòng đầy đủ (\n) rõ ràng giữa các thẻ, các selector, các hàm và câu lệnh.
     + Thụt lề (indentation) chuẩn 2 hoặc 4 khoảng trắng sạch đẹp, cấu trúc mở đóng thẻ/dấu ngoặc rõ ràng, chuyên nghiệp như lập trình viên viết tay.
   - QUY TẮC THIẾT KẾ ĐẲNG CẤP & TỰ ĐỘNG TẠO ẢNH:
     + Khi dự án cần hình ảnh: Liệt kê vào mảng 'images'. Mỗi ảnh có 'path' (ví dụ 'images/hero-banner.png') và 'prompt' tiếng Anh cực kỳ chi tiết, sống động, ánh sáng tự nhiên, góc chụp sắc nét (8k, photorealistic). Hệ thống Antigravity sẽ TỰ ĐỘNG gọi AI sinh ảnh thật vào đúng thư mục!

CẤU TRÚC JSON BẮT BUỘC TRẢ VỀ DUY NHẤT:
{
  ""projectName"": ""ten-du-an (vi du: my-webapi)"",
  ""plan"": [
    ""Bước 1: Chạy lệnh CLI scaffolding khởi tạo khung dự án chuẩn"",
    ""Bước 2: Viết mã nguồn nghiệp vụ Controllers, Models và giao diện hoàn chỉnh"",
    ""Bước 3: Tự động tổng hợp và kiểm tra cấu trúc thư mục dự án""
  ],
  ""explanation"": ""Tóm tắt chi tiết cấu trúc dự án và các tính năng vừa được tạo."",
  ""setupCommands"": [
    ""dotnet new webapi --use-controllers -n backend""
  ],
  ""images"": [
    {
      ""path"": ""images/hero-banner.png"",
      ""prompt"": ""An ultra-sharp photorealistic modern banner, clean lighting, 8k resolution""
    }
  ],
  ""operations"": [
    {
      ""action"": ""create"",
      ""path"": ""backend/Controllers/ProductController.cs"",
      ""content"": ""Toàn bộ mã nguồn 100% của file..."",
      ""summary"": ""Controller nghiệp vụ sản phẩm""
    }
  ],
  ""suggestedCommand"": ""dotnet build""
}

QUY TẮC QUAN TRỌNG:
1. TUYỆT ĐỐI CHỈ TRẢ VỀ DUY NHẤT MỘT KHỐI JSON HỢP LỆ. Không thêm lời mở đầu hay kết luận ngoài JSON.
2. Mã nguồn trong 'content' phải là 100% code hoàn chỉnh, sẵn sàng chạy ngay.
";

            var payload = new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = $"{systemPrompt}\n\n[CONTEXT FILES]:\n{sbContext}\n\n[CURRENT PROJECT]: {model.Project ?? "(none)"}\n\n[USER REQUEST]: {model.Prompt}" }
                        }
                    }
                },
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = isFastMode ? 0.3 : 0.5,
                    ["maxOutputTokens"] = 16384
                }
            };

            if (isFastMode)
            {
                payload["generationConfig"]!["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
            }

            try
            {
                var (success, resData, rawResponse, error) = await _geminiService.GenerateContentAsync(selectedModel, payload);

                if (!success || resData == null)
                {
                    return Ok(new
                    {
                        ok = false,
                        error = error ?? "Failed to get response from Gemini"
                    });
                }

                string text = "";
                var candidates = resData["candidates"]?.AsArray();
                if (candidates != null && candidates.Count > 0)
                {
                    var parts = candidates[0]?["content"]?["parts"]?.AsArray();
                    if (parts != null && parts.Count > 0)
                    {
                        text = parts[0]?["text"]?.ToString() ?? "";
                    }
                }

                // Clean markdown code fence if present
                string cleanJson = text.Trim();
                if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                {
                    cleanJson = cleanJson.Substring(7);
                }
                else if (cleanJson.StartsWith("```"))
                {
                    cleanJson = cleanJson.Substring(3);
                }
                if (cleanJson.EndsWith("```"))
                {
                    cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
                }
                cleanJson = cleanJson.Trim();

                try
                {
                    var parsed = JsonNode.Parse(cleanJson)?.AsObject();
                    if (parsed != null)
                    {
                        string projectName = parsed["projectName"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(model.Project))
                        {
                            projectName = model.Project;
                        }

                        // Support both 'operations' and legacy 'edits'
                        var opsList = new List<object>();
                        var rawOps = parsed["operations"] as JsonArray ?? parsed["edits"] as JsonArray;

                        string firstFilePath = "";
                        string firstFileContent = "";
                        string firstFileOriginal = "";

                        int additions = 0;
                        int deletions = 0;

                        if (rawOps != null && rawOps.Count > 0)
                        {
                            string root = ResolveWorkspacePath(projectName);

                            foreach (var node in rawOps)
                            {
                                if (node is JsonObject opObj)
                                {
                                    string path = opObj["path"]?.ToString() ?? "";
                                    string content = opObj["content"]?.ToString() ?? opObj["newContent"]?.ToString() ?? "";
                                    string action = opObj["action"]?.ToString() ?? "create";
                                    string summary = opObj["summary"]?.ToString() ?? "";

                                    if (string.IsNullOrWhiteSpace(path)) continue;

                                    if (string.IsNullOrEmpty(firstFilePath))
                                    {
                                        firstFilePath = path;
                                        firstFileContent = content;

                                        if (Directory.Exists(root) && TryGetSafeFilePath(root, path, out string fOrig) && System.IO.File.Exists(fOrig))
                                        {
                                            try { firstFileOriginal = System.IO.File.ReadAllText(fOrig); } catch { }
                                        }
                                    }

                                    int linesCount = content.Split('\n').Length;
                                    additions += linesCount;

                                    opsList.Add(new
                                    {
                                        action = action,
                                        path = path,
                                        content = content,
                                        summary = summary
                                    });
                                }
                            }
                        }

                        // Parse generated images list if any
                        var imagesList = new List<object>();
                        if (parsed["images"] is JsonArray rawImgs && rawImgs.Count > 0)
                        {
                            foreach (var imgNode in rawImgs)
                            {
                                if (imgNode is JsonObject imgObj)
                                {
                                    string imgPath = imgObj["path"]?.ToString() ?? "";
                                    string imgPrompt = imgObj["prompt"]?.ToString() ?? "";
                                    if (!string.IsNullOrWhiteSpace(imgPath) && !string.IsNullOrWhiteSpace(imgPrompt))
                                    {
                                        imagesList.Add(new
                                        {
                                            path = imgPath,
                                            prompt = imgPrompt
                                        });
                                    }
                                }
                            }
                        }

                        return Ok(new
                        {
                            ok = true,
                            projectName = projectName,
                            plan = parsed["plan"],
                            explanation = parsed["explanation"]?.ToString() ?? "",
                            operations = opsList,
                            edits = opsList,
                            images = imagesList,
                            suggestedCommand = parsed["suggestedCommand"]?.ToString() ?? "",
                            proposedFile = firstFilePath,
                            proposedContent = firstFileContent,
                            originalContent = firstFileOriginal,
                            additions = additions,
                            deletions = deletions
                        });
                    }
                }
                catch (Exception pEx)
                {
                    _logger.LogWarning("JSON parse error from agent: {Error}. Raw: {Text}", pEx.Message, cleanJson);
                }

                // Fallback if parsing failed
                return Ok(new
                {
                    ok = true,
                    plan = new string[] { "Hoàn tất xử lý yêu cầu" },
                    explanation = text,
                    operations = new object[] { },
                    edits = new object[] { },
                    images = new object[] { },
                    suggestedCommand = "",
                    proposedFile = "",
                    proposedContent = "",
                    originalContent = "",
                    additions = 0,
                    deletions = 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent chat execution error");
                return StatusCode(500, new { ok = false, error = ex.Message });
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
    }

    // ==========================================
    // MODELS
    // ==========================================

    public class IdeCreateProjectRequestModel
    {
        public string? Name { get; set; }
        public string? Template { get; set; }
    }

    public class DeleteProjectModel
    {
        public string? Name { get; set; }
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
}
