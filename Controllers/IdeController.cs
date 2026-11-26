using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BtoanAiNet.Data;
using BtoanAiNet.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace BtoanAiNet.Controllers
{
    [ApiController]
    [Route("api/ide")]
    public class IdeController : ControllerBase
    {
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

        private readonly IWebHostEnvironment _env;
        private readonly IGeminiService _geminiService;
        private readonly IImageGenerationService _imageGenerationService;
        private readonly ILogger<IdeController> _logger;
        private readonly AppDbContext _dbContext;

        public IdeController(
            IWebHostEnvironment env,
            IGeminiService geminiService,
            IImageGenerationService imageGenerationService,
            ILogger<IdeController> logger,
            AppDbContext dbContext)
        {
            _env = env;
            _geminiService = geminiService;
            _imageGenerationService = imageGenerationService;
            _logger = logger;
            _dbContext = dbContext;
        }

        private int CurrentUserId
        {
            get
            {
                return BtoanAiNet.Helpers.AuthUserHelper.GetCurrentUserId(HttpContext);
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
                string content = @"<Project>
  <PropertyGroup>
    <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn>
  </PropertyGroup>
</Project>
";
                System.IO.File.WriteAllText(propsPath, content, Encoding.UTF8);
            }
            catch { }
        }

        public static void AutoFixMapStaticAssets(string projectPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;
                var progFiles = Directory.GetFiles(projectPath, "Program.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

                foreach (var progPath in progFiles)
                {
                    string pContent = System.IO.File.ReadAllText(progPath, Encoding.UTF8);
                    if (pContent.Contains("MapStaticAssets") || pContent.Contains("WithStaticAssets"))
                    {
                        pContent = pContent.Replace("app.MapStaticAssets();", "app.UseStaticFiles();")
                                           .Replace("MapStaticAssets()", "UseStaticFiles()")
                                           .Replace(".WithStaticAssets()", "");
                        System.IO.File.WriteAllText(progPath, pContent, Encoding.UTF8);
                    }
                }
            }
            catch { }

            AutoFixSqliteConnectionStrings(projectPath);
            AutoFixSessionExtensions(projectPath);
            AutoFixAntiforgery(projectPath);
        }

        public static void AutoFixAntiforgery(string projectPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;

                // 1. Configure Antiforgery in child Program.cs for loopback proxy
                var progFiles = Directory.GetFiles(projectPath, "Program.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

                foreach (var progPath in progFiles)
                {
                    string pContent = System.IO.File.ReadAllText(progPath, Encoding.UTF8);
                    bool modified = false;

                    if (!pContent.Contains("AddAntiforgery") && pContent.Contains("builder.Services.AddControllersWithViews();"))
                    {
                        pContent = pContent.Replace("builder.Services.AddControllersWithViews();",
                            "builder.Services.AddControllersWithViews();\nbuilder.Services.AddAntiforgery(options => {\n    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;\n    options.SuppressXFrameOptionsHeader = true;\n});");
                        modified = true;
                    }

                    if (modified)
                    {
                        System.IO.File.WriteAllText(progPath, pContent, Encoding.UTF8);
                    }
                }

                // 2. Replace [ValidateAntiForgeryToken] with [IgnoreAntiforgeryToken] in child controllers for loopback proxy form submission
                var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

                foreach (var csFile in csFiles)
                {
                    string text = System.IO.File.ReadAllText(csFile, Encoding.UTF8);
                    if (text.Contains("[ValidateAntiForgeryToken]"))
                    {
                        text = text.Replace("[ValidateAntiForgeryToken]", "[IgnoreAntiforgeryToken]");
                        System.IO.File.WriteAllText(csFile, text, Encoding.UTF8);
                    }
                }
            }
            catch { }
        }

        public static void AutoFixSqliteConnectionStrings(string projectPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;

                // Check if project references Sqlite
                var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly);
                bool usesSqlite = false;
                foreach (var csproj in csprojFiles)
                {
                    string content = System.IO.File.ReadAllText(csproj, Encoding.UTF8);
                    if (content.Contains("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        usesSqlite = true;
                        break;
                    }
                }

                if (!usesSqlite)
                {
                    var progFile = Path.Combine(projectPath, "Program.cs");
                    if (System.IO.File.Exists(progFile))
                    {
                        string pContent = System.IO.File.ReadAllText(progFile, Encoding.UTF8);
                        if (pContent.Contains("UseSqlite", StringComparison.OrdinalIgnoreCase))
                        {
                            usesSqlite = true;
                        }
                    }
                }

                if (usesSqlite)
                {
                    string[] jsonFiles = new[] { "appsettings.json", "appsettings.Development.json" };
                    foreach (var jf in jsonFiles)
                    {
                        string jPath = Path.Combine(projectPath, jf);
                        if (System.IO.File.Exists(jPath))
                        {
                            string jContent = System.IO.File.ReadAllText(jPath, Encoding.UTF8);
                            if (jContent.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                                jContent.Contains("Trusted_Connection=", StringComparison.OrdinalIgnoreCase) ||
                                !jContent.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var node = System.Text.Json.Nodes.JsonNode.Parse(jContent);
                                    if (node != null)
                                    {
                                        if (node["ConnectionStrings"] == null)
                                        {
                                            node["ConnectionStrings"] = new System.Text.Json.Nodes.JsonObject();
                                        }
                                        node["ConnectionStrings"]!["DefaultConnection"] = "Data Source=shop_senior.db";
                                        System.IO.File.WriteAllText(jPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                                    }
                                }
                                catch
                                {
                                    string replaced = System.Text.RegularExpressions.Regex.Replace(
                                        jContent,
                                        @"""DefaultConnection""\s*:\s*""[^""]*""",
                                        @"""DefaultConnection"": ""Data Source=shop_senior.db"""
                                    );
                                    System.IO.File.WriteAllText(jPath, replaced, Encoding.UTF8);
                                }
                            }
                        }
                    }

                    // Also sanitize Program.cs connection string retrieval if present
                    var pFile = Path.Combine(projectPath, "Program.cs");
                    if (System.IO.File.Exists(pFile))
                    {
                        string pSrc = System.IO.File.ReadAllText(pFile, Encoding.UTF8);
                        if (pSrc.Contains("builder.Configuration.GetConnectionString(\"DefaultConnection\")") &&
                            !pSrc.Contains("rawConn.Contains(\"Server=\")"))
                        {
                            pSrc = pSrc.Replace(
                                "builder.Services.AddDbContext<AppDbContext>(options =>\n    options.UseSqlite(builder.Configuration.GetConnectionString(\"DefaultConnection\") ?? \"Data Source=shop_senior.db\"));",
                                "string rawConn = builder.Configuration.GetConnectionString(\"DefaultConnection\") ?? \"Data Source=shop_senior.db\";\nif (rawConn.Contains(\"Server=\", StringComparison.OrdinalIgnoreCase) || !rawConn.Contains(\"Data Source=\", StringComparison.OrdinalIgnoreCase))\n{\n    rawConn = \"Data Source=shop_senior.db\";\n}\nbuilder.Services.AddDbContext<AppDbContext>(options =>\n    options.UseSqlite(rawConn));"
                            );
                            System.IO.File.WriteAllText(pFile, pSrc, Encoding.UTF8);
                        }
                    }
                }
            }
            catch { }
        }

        public static void AutoFixSessionExtensions(string projectPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;

                var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly);
                if (csprojFiles.Length == 0) return;

                string projName = Path.GetFileNameWithoutExtension(csprojFiles[0]);
                string cleanNamespace = Regex.Replace(projName, @"[^a-zA-Z0-9_]", "_");

                var allCodeFiles = Directory.GetFiles(projectPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => (f.EndsWith(".cs") || f.EndsWith(".cshtml")) &&
                                !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    .ToList();

                bool callsIsUserLoggedIn = allCodeFiles.Any(f => {
                    try { return System.IO.File.ReadAllText(f).Contains("IsUserLoggedIn"); }
                    catch { return false; }
                });

                if (callsIsUserLoggedIn)
                {
                    string helpersDir = Path.Combine(projectPath, "Helpers");
                    if (!Directory.Exists(helpersDir)) Directory.CreateDirectory(helpersDir);

                    string sessionExtPath = Path.Combine(helpersDir, "SessionExtensions.cs");
                    if (!System.IO.File.Exists(sessionExtPath))
                    {
                        string defaultExt = $@"#nullable enable
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace {cleanNamespace}.Helpers
{{
    public static class SessionExtensions
    {{
        public static void SetObjectAsJson(this ISession session, string key, object value)
        {{
            session.SetString(key, JsonConvert.SerializeObject(value));
        }}

        public static T? GetObjectFromJson<T>(this ISession session, string key)
        {{
            var value = session.GetString(key);
            return value == null ? default : JsonConvert.DeserializeObject<T>(value);
        }}

        public static bool IsUserLoggedIn(this ISession session)
        {{
            return !string.IsNullOrEmpty(session.GetString(""UserId"")) || 
                   !string.IsNullOrEmpty(session.GetString(""user_id"")) ||
                   !string.IsNullOrEmpty(session.GetString(""UserEmail""));
        }}

        public static string? GetUserId(this ISession session)
        {{
            return session.GetString(""UserId"") ?? session.GetString(""user_id"");
        }}

        public static string? GetUsername(this ISession session)
        {{
            return session.GetString(""Username"") ?? session.GetString(""username"") ?? ""Khách hàng"";
        }}

        public static void SetUserLoggedIn(this ISession session, string userId, string username)
        {{
            session.SetString(""UserId"", userId);
            session.SetString(""user_id"", userId);
            session.SetString(""Username"", username);
            session.SetString(""username"", username);
        }}
    }}
}}";
                        System.IO.File.WriteAllText(sessionExtPath, defaultExt, Encoding.UTF8);
                    }
                    else
                    {
                        string content = System.IO.File.ReadAllText(sessionExtPath, Encoding.UTF8);
                        if (!content.Contains("IsUserLoggedIn"))
                        {
                            string injectMethods = @"
        public static bool IsUserLoggedIn(this ISession session)
        {
            return !string.IsNullOrEmpty(session.GetString(""UserId"")) || 
                   !string.IsNullOrEmpty(session.GetString(""user_id"")) ||
                   !string.IsNullOrEmpty(session.GetString(""UserEmail""));
        }

        public static string? GetUserId(this ISession session)
        {
            return session.GetString(""UserId"") ?? session.GetString(""user_id"");
        }

        public static string? GetUsername(this ISession session)
        {
            return session.GetString(""Username"") ?? session.GetString(""username"") ?? ""Khách hàng"";
        }

        public static void SetUserLoggedIn(this ISession session, string userId, string username)
        {
            session.SetString(""UserId"", userId);
            session.SetString(""user_id"", userId);
            session.SetString(""Username"", username);
            session.SetString(""username"", username);
        }
";
                            int lastBrace = content.LastIndexOf('}');
                            if (lastBrace > 0)
                            {
                                int classBrace = content.LastIndexOf('}', lastBrace - 1);
                                if (classBrace > 0)
                                {
                                    content = content.Insert(classBrace, injectMethods);
                                    System.IO.File.WriteAllText(sessionExtPath, content, Encoding.UTF8);
                                }
                            }
                        }
                    }

                    // Auto-ensure _ViewImports.cshtml has @using ...Helpers
                    string viewImports = Path.Combine(projectPath, "Views", "_ViewImports.cshtml");
                    if (System.IO.File.Exists(viewImports))
                    {
                        string viContent = System.IO.File.ReadAllText(viewImports, Encoding.UTF8);
                        if (!viContent.Contains(".Helpers"))
                        {
                            viContent += $"\n@using {cleanNamespace}.Helpers\n";
                            System.IO.File.WriteAllText(viewImports, viContent, Encoding.UTF8);
                        }
                    }

                    // Auto-ensure ALL .cs files calling IsUserLoggedIn have using Helpers
                    var allCsFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                    !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                    !f.EndsWith("SessionExtensions.cs"));
                    foreach (var cf in allCsFiles)
                    {
                        string cText = System.IO.File.ReadAllText(cf, Encoding.UTF8);
                        if (cText.Contains("IsUserLoggedIn") && !cText.Contains(".Helpers"))
                        {
                            cText = $"using {cleanNamespace}.Helpers;\n" + cText;
                            System.IO.File.WriteAllText(cf, cText, Encoding.UTF8);
                        }
                    }

                    // Auto-ensure ALL .cshtml views calling IsUserLoggedIn have @using Helpers
                    var allCshtmlFiles = Directory.GetFiles(projectPath, "*.cshtml", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                    !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
                    foreach (var vf in allCshtmlFiles)
                    {
                        string vText = System.IO.File.ReadAllText(vf, Encoding.UTF8);
                        if (vText.Contains("IsUserLoggedIn") && !vText.Contains(".Helpers"))
                        {
                            vText = $"@using {cleanNamespace}.Helpers\n" + vText;
                            System.IO.File.WriteAllText(vf, vText, Encoding.UTF8);
                        }
                    }
                }
            }
            catch { }
        }

        public static void EnforceWebSeparation(string projectPath, List<string> modifiedFiles)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;

                // Never run web separation on compiled (.NET / .csproj) or Angular projects
                if (Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly).Any() ||
                    System.IO.File.Exists(Path.Combine(projectPath, "angular.json")))
                {
                    return;
                }

                string indexPath = Path.Combine(projectPath, "index.html");
                if (!System.IO.File.Exists(indexPath)) return;

                string html = System.IO.File.ReadAllText(indexPath, Encoding.UTF8);
                bool modifiedHtml = false;

                string cssPath = Path.Combine(projectPath, "style.css");
                string jsPath = Path.Combine(projectPath, "script.js");

                // 1. Extract CSS if style.css does not exist or is empty
                var styleMatches = System.Text.RegularExpressions.Regex.Matches(
                    html,
                    @"<style[^>]*>(.*?)</style>",
                    System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                if (styleMatches.Count > 0 && (!System.IO.File.Exists(cssPath) || System.IO.File.ReadAllText(cssPath).Trim().Length == 0))
                {
                    var sbCss = new StringBuilder();
                    foreach (System.Text.RegularExpressions.Match m in styleMatches)
                    {
                        sbCss.AppendLine(m.Groups[1].Value.Trim());
                    }
                    string extractedCss = sbCss.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(extractedCss))
                    {
                        System.IO.File.WriteAllText(cssPath, extractedCss, Encoding.UTF8);
                        if (!modifiedFiles.Contains("style.css")) modifiedFiles.Add("style.css");

                        html = System.Text.RegularExpressions.Regex.Replace(
                            html,
                            @"<style[^>]*>.*?</style>",
                            "",
                            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        );
                        modifiedHtml = true;
                    }
                }

                // 2. Extract JS if script.js does not exist or is empty
                var scriptMatches = System.Text.RegularExpressions.Regex.Matches(
                    html,
                    @"<script(?![^>]*src=)[^>]*>(.*?)</script>",
                    System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                if (scriptMatches.Count > 0 && (!System.IO.File.Exists(jsPath) || System.IO.File.ReadAllText(jsPath).Trim().Length == 0))
                {
                    var sbJs = new StringBuilder();
                    foreach (System.Text.RegularExpressions.Match m in scriptMatches)
                    {
                        sbJs.AppendLine(m.Groups[1].Value.Trim());
                    }
                    string extractedJs = sbJs.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(extractedJs))
                    {
                        System.IO.File.WriteAllText(jsPath, extractedJs, Encoding.UTF8);
                        if (!modifiedFiles.Contains("script.js")) modifiedFiles.Add("script.js");

                        html = System.Text.RegularExpressions.Regex.Replace(
                            html,
                            @"<script(?![^>]*src=)[^>]*>.*?</script>",
                            "",
                            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        );
                        modifiedHtml = true;
                    }
                }

                // 3. Ensure <link rel="stylesheet" href="style.css"> in <head>
                if (System.IO.File.Exists(cssPath) && !html.Contains("style.css", StringComparison.OrdinalIgnoreCase))
                {
                    if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                    {
                        html = html.Replace("</head>", "    <link rel=\"stylesheet\" href=\"style.css\">\n</head>", StringComparison.OrdinalIgnoreCase);
                        modifiedHtml = true;
                    }
                }

                // 4. Ensure <script src="script.js"></script> before </body>
                if (System.IO.File.Exists(jsPath) && !html.Contains("script.js", StringComparison.OrdinalIgnoreCase))
                {
                    if (html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                    {
                        html = html.Replace("</body>", "    <script src=\"script.js\"></script>\n</body>", StringComparison.OrdinalIgnoreCase);
                        modifiedHtml = true;
                    }
                }

                if (modifiedHtml)
                {
                    System.IO.File.WriteAllText(indexPath, html, Encoding.UTF8);
                    if (!modifiedFiles.Contains("index.html")) modifiedFiles.Add("index.html");
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

        public static string NormalizeWorkspaceRelativePath(string relativePath, string? projectName = null)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
            string clean = relativePath.Trim().Replace('\\', '/');

            while (clean.StartsWith("./") || clean.StartsWith("/"))
            {
                clean = clean.StartsWith("./") ? clean.Substring(2) : clean.Substring(1);
            }

            if (!string.IsNullOrWhiteSpace(projectName))
            {
                string cleanProj = SanitizeProjectName(projectName);
                if (clean.StartsWith(cleanProj + "/", StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean.Substring(cleanProj.Length + 1);
                }
            }

            return clean.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        }

        private string ResolveWorkspacePath(string? projectId)
        {
            int uid = CurrentUserId > 0 ? CurrentUserId : 1;
            string userRoot = GetUserRootPath(uid);
            if (string.IsNullOrWhiteSpace(userRoot)) return string.Empty;

            if (string.IsNullOrWhiteSpace(projectId))
            {
                return userRoot;
            }

            string cleanProj = SanitizeProjectName(projectId);
            string projectPath = Path.Combine(userRoot, cleanProj);

            if (!Directory.Exists(projectPath))
            {
                string user1Path = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", "user_1", cleanProj);
                if (Directory.Exists(user1Path))
                {
                    return user1Path;
                }

                string workspacesRoot = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces");
                if (Directory.Exists(workspacesRoot))
                {
                    try
                    {
                        var matchingDir = Directory.GetDirectories(workspacesRoot, cleanProj, SearchOption.AllDirectories).FirstOrDefault();
                        if (matchingDir != null) return matchingDir;
                    }
                    catch { }
                }

                Directory.CreateDirectory(projectPath);
            }

            return projectPath;
        }

        public static bool ResolveSafeProjectPath(string workspacePath, string relativePath, out string fullPath)
        {
            return TryGetSafeFilePathStatic(workspacePath, relativePath, out fullPath);
        }

        public static bool TryGetSafeFilePathStatic(string workspacePath, string relativePath, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(relativePath)) return false;

            string projectName = Path.GetFileName(workspacePath);
            string normalized = NormalizeWorkspaceRelativePath(relativePath, projectName);
            string combined = Path.GetFullPath(Path.Combine(workspacePath, normalized));
            string rootFullPath = Path.GetFullPath(workspacePath);

            if (!combined.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false; // Path traversal detected
            }

            fullPath = combined;
            return true;
        }

        private bool TryGetSafeFilePath(string workspacePath, string relativePath, out string fullPath)
        {
            return TryGetSafeFilePathStatic(workspacePath, relativePath, out fullPath);
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

            int effectiveUserId = CurrentUserId > 0 ? CurrentUserId : 1;
            string userRoot = GetUserRootPath(effectiveUserId);

            // Auto-merge / seed from user_1 if effectiveUserId > 1 and userRoot has no projects or is missing shop-senior-mvc
            if (effectiveUserId > 1)
            {
                string user1Root = GetUserRootPath(1);
                if (Directory.Exists(user1Root))
                {
                    var user1Dirs = new DirectoryInfo(user1Root).GetDirectories();
                    foreach (var d in user1Dirs)
                    {
                        string targetProjPath = Path.Combine(userRoot, d.Name);
                        if (!Directory.Exists(targetProjPath))
                        {
                            try
                            {
                                CopyDirectoryRecursively(d.FullName, targetProjPath);
                            }
                            catch { }
                        }
                    }
                }
            }

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

            // If user still has no projects, seed shop-senior-mvc from WorkspaceTemplates if available
            if (projectsList.Count == 0)
            {
                string templatePath = Path.Combine(_env.ContentRootPath, "WorkspaceTemplates", "shop-senior-mvc");
                if (Directory.Exists(templatePath))
                {
                    string targetShop = Path.Combine(userRoot, "shop-senior-mvc");
                    try
                    {
                        CopyDirectoryRecursively(templatePath, targetShop);
                        var d = new DirectoryInfo(targetShop);
                        int fileCount = d.GetFiles("*", SearchOption.AllDirectories).Count();
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
                    catch { }
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
                    // 1. ASP.NET Core Web App (Model-View-Controller) via official CLI (Instant scaffold with --no-restore)
                    var res = await RunCliProcessAsync(userRoot, "dotnet", $"new mvc -n \"{cleanName}\" -o \"{projectPath}\" --no-restore --no-https", 25);
                    if (!res.ok)
                    {
                        _logger.LogWarning("dotnet new mvc output: {Output}", res.output);
                        CleanupEmptyDir(projectPath);
                        return StatusCode(400, new { ok = false, error = $"Khởi tạo dự án ASP.NET Core MVC thất bại: {res.output}" });
                    }

                    // Tự động thay thế MapStaticAssets() bằng UseStaticFiles() để tránh lỗi thiếu staticwebassets manifest
                    AutoFixMapStaticAssets(projectPath);

                    primaryFile = "Program.cs";
                }
                else if (template == "dotnet-webapi" || template == "webapi")
                {
                    // 2. ASP.NET Core Web API via official CLI (Instant scaffold with --no-restore)
                    var res = await RunCliProcessAsync(userRoot, "dotnet", $"new webapi -n \"{cleanName}\" -o \"{projectPath}\" --use-controllers --no-restore --no-https", 25);
                    if (!res.ok)
                    {
                        _logger.LogWarning("dotnet new webapi output: {Output}", res.output);
                        CleanupEmptyDir(projectPath);
                        return StatusCode(400, new { ok = false, error = $"Khởi tạo dự án ASP.NET Core Web API thất bại: {res.output}" });
                    }
                    primaryFile = "Program.cs";
                }
                else if (template == "angular")
                {
                    // 3. Angular App via official Angular CLI (instant scaffold with --skip-install)
                    var res = await RunCliProcessAsync(userRoot, "ng", $"new \"{cleanName}\" --directory \"{projectPath}\" --skip-git --skip-install --style=css --ssr=false --defaults", 30);
                    if (!res.ok)
                    {
                        _logger.LogWarning("ng new failed, falling back to npx: {Output}", res.output);
                        res = await RunCliProcessAsync(userRoot, "npx", $"-y @angular/cli@latest new \"{cleanName}\" --directory \"{projectPath}\" --skip-git --skip-install --style=css --ssr=false --defaults", 30);
                    }
                    if (!res.ok)
                    {
                        CleanupEmptyDir(projectPath);
                        return StatusCode(400, new { ok = false, error = $"Khởi tạo dự án Angular thất bại: {res.output}" });
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

                    var resBe = await RunCliProcessAsync(userRoot, "dotnet", $"new webapi -n \"Backend\" -o \"{bePath}\" --use-controllers --no-restore --no-https", 25);
                    if (!resBe.ok)
                    {
                        CleanupEmptyDir(projectPath);
                        return StatusCode(400, new { ok = false, error = $"Khởi tạo backend Web API thất bại: {resBe.output}" });
                    }

                    var resFe = await RunCliProcessAsync(userRoot, "ng", $"new \"frontend\" --directory \"{fePath}\" --skip-git --skip-install --style=css --ssr=false --defaults", 30);
                    if (!resFe.ok)
                    {
                        resFe = await RunCliProcessAsync(userRoot, "npx", $"-y @angular/cli@latest new \"frontend\" --directory \"{fePath}\" --skip-git --skip-install --style=css --ssr=false --defaults", 30);
                    }
                    if (!resFe.ok)
                    {
                        CleanupEmptyDir(projectPath);
                        return StatusCode(400, new { ok = false, error = $"Khởi tạo frontend Angular thất bại: {resFe.output}" });
                    }
                    primaryFile = "backend/Program.cs";
                }
                else if (template == "static-web" || template == "html5" || template == "web")
                {
                    // 5. HTML5 / CSS3 / JS Static Web (Đầy đủ 3 file)
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

                // Guard: If not empty project, make sure project directory is not empty
                if (template != "empty" && Directory.Exists(projectPath) && Directory.GetFileSystemEntries(projectPath).Length == 0)
                {
                    CleanupEmptyDir(projectPath);
                    return StatusCode(400, new { ok = false, error = $"Khởi tạo dự án '{cleanName}' thất bại: Thư mục dự án rỗng." });
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
                CleanupEmptyDir(projectPath);
                _logger.LogError(ex, "Error creating project {Name}", cleanName);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        private static void CleanupEmptyDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { }
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

            string actualExe = executable.Trim();
            string actualArgs = arguments ?? "";
            int firstSpace = actualExe.IndexOf(' ');
            if (firstSpace > 0)
            {
                actualArgs = $"{actualExe.Substring(firstSpace + 1).Trim()} {actualArgs}".Trim();
                actualExe = actualExe.Substring(0, firstSpace).Trim();
            }

            // Docker/Linux container optimization: prevent Roslyn compiler server socket deadlock, MSBuild Server hang, and MSBuild node reuse
            psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["UseSharedCompilation"] = "false";

            if (actualExe.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                if (actualArgs.Contains("build", StringComparison.OrdinalIgnoreCase))
                {
                    if (!actualArgs.Contains("disable-build-servers", StringComparison.OrdinalIgnoreCase))
                    {
                        actualArgs += " --disable-build-servers";
                    }
                    if (!actualArgs.Contains("UseSharedCompilation", StringComparison.OrdinalIgnoreCase))
                    {
                        actualArgs += " -nodeReuse:false -p:UseSharedCompilation=false -maxCpuCount:1";
                    }
                }
                else if (actualArgs.Contains("restore", StringComparison.OrdinalIgnoreCase))
                {
                    if (!actualArgs.Contains("disable-build-servers", StringComparison.OrdinalIgnoreCase))
                    {
                        actualArgs += " --disable-build-servers --disable-parallel --nologo";
                    }
                }
            }

            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c {actualExe} {actualArgs}";
            }
            else
            {
                psi.FileName = actualExe;
                psi.Arguments = actualArgs;
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

            // Allow embedding inside IDE iframe
            Response.Headers.Remove("X-Frame-Options");
            Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self' https://ai.btoan.com http://localhost:* http://127.0.0.1:*";

            // If HTML, inject base tag for relative assets so css, js, and images resolve reliably
            if (contentType.StartsWith("text/html") || fullPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || fullPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                string htmlContent = System.IO.File.ReadAllText(fullPath);
                string baseHref = $"/api/ide/preview/{cleanProj}/";
                if (!htmlContent.Contains("<base ", StringComparison.OrdinalIgnoreCase) && !htmlContent.Contains("<base>", StringComparison.OrdinalIgnoreCase))
                {
                    if (htmlContent.Contains("<head>", StringComparison.OrdinalIgnoreCase) || htmlContent.Contains("<head ", StringComparison.OrdinalIgnoreCase))
                    {
                        htmlContent = System.Text.RegularExpressions.Regex.Replace(
                            htmlContent,
                            "(<head(\\s+[^>]*)?>)",
                            $"$1\n    <base href=\"{baseHref}\">",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }
                    else
                    {
                        htmlContent = $"<base href=\"{baseHref}\">\n" + htmlContent;
                    }
                }
                return Content(htmlContent, "text/html; charset=utf-8");
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

            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", files = Array.Empty<object>() });
            }
            string root = ResolveWorkspacePath(cleanProj);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                string user1Path = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", "user_1", cleanProj);
                if (Directory.Exists(user1Path)) root = user1Path;
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                try { Directory.CreateDirectory(root); } catch { }
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Ok(new { ok = true, project = cleanProj, files = Array.Empty<object>() });
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

            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }
            string root = ResolveWorkspacePath(cleanProj);
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

            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }
            string root = ResolveWorkspacePath(cleanProj);
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

            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }
            string root = ResolveWorkspacePath(cleanProj);
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
            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }
            string root = ResolveWorkspacePath(cleanProj);
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
            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }
            string root = ResolveWorkspacePath(cleanProj);
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

        // POST /api/ide/ports/stop and /api/ide/stop-port
        [HttpPost("ports/stop")]
        [HttpPost("stop-port")]
        public async Task<IActionResult> StopPort([FromBody] StopPortRequestModel model)
        {
            int port = model?.Port ?? 0;
            if (port < 1024 || port > 65535)
            {
                return BadRequest(new { ok = false, error = "Cổng không hợp lệ." });
            }

            await RunningAppSessionManager.StopPortAsync(port, CurrentUserId);
            return Ok(new
            {
                ok = true,
                port = port,
                message = $"Đã dừng và giải phóng hoàn toàn cổng :{port}."
            });
        }

        public static async Task<string> CreateProjectCheckpointInternalAsync(string projectPath)
        {
            string checkpointId = "cp_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
            try
            {
                string cpDir = Path.Combine(projectPath, ".btoan_checkpoints", checkpointId);
                Directory.CreateDirectory(cpDir);

                var allFiles = Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + ".btoan_checkpoints" + Path.DirectorySeparatorChar) &&
                                !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar));

                foreach (var file in allFiles)
                {
                    string rel = Path.GetRelativePath(projectPath, file);
                    string dest = Path.Combine(cpDir, rel);
                    string? destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    System.IO.File.Copy(file, dest, true);
                }
            }
            catch { }
            return checkpointId;
        }

        public static async Task<bool> RestoreProjectCheckpointInternalAsync(string projectPath, string checkpointId)
        {
            if (string.IsNullOrWhiteSpace(checkpointId)) return false;
            string cpDir = Path.Combine(projectPath, ".btoan_checkpoints", checkpointId);
            if (!Directory.Exists(cpDir)) return false;

            try
            {
                var cpFiles = Directory.GetFiles(cpDir, "*", SearchOption.AllDirectories);
                foreach (var file in cpFiles)
                {
                    string rel = Path.GetRelativePath(cpDir, file);
                    string dest = Path.Combine(projectPath, rel);
                    string? destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    System.IO.File.Copy(file, dest, true);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // POST /api/ide/checkpoint/create
        [HttpPost("checkpoint/create")]
        public async Task<IActionResult> CreateCheckpoint([FromBody] JsonObject model)
        {
            string proj = model?["project"]?.ToString() ?? "";
            string cleanProj = SanitizeProjectName(proj);
            string root = ResolveWorkspacePath(cleanProj);
            if (!Directory.Exists(root)) return BadRequest(new { ok = false, error = "Project not found" });
            string cpId = await CreateProjectCheckpointInternalAsync(root);
            return Ok(new { ok = true, checkpointId = cpId });
        }

        // POST /api/ide/checkpoint/restore
        [HttpPost("checkpoint/restore")]
        public async Task<IActionResult> RestoreCheckpoint([FromBody] JsonObject model)
        {
            string proj = model?["project"]?.ToString() ?? "";
            string cpId = model?["checkpointId"]?.ToString() ?? "";
            string cleanProj = SanitizeProjectName(proj);
            string root = ResolveWorkspacePath(cleanProj);
            if (!Directory.Exists(root)) return BadRequest(new { ok = false, error = "Project not found" });
            bool restored = await RestoreProjectCheckpointInternalAsync(root, cpId);
            return Ok(new { ok = restored, message = restored ? "Đã khôi phục checkpoint thành công." : "Không tìm thấy checkpoint hoặc khôi phục thất bại." });
        }

        // GET /api/ide/file?project=...&path=...
        // GET /api/ide/file-content?project=...&file=...
        [HttpGet("file")]
        [HttpGet("file-content")]
        public async Task<IActionResult> GetFile([FromQuery] string? path, [FromQuery] string? file, [FromQuery] string? project)
        {
            string targetPath = !string.IsNullOrWhiteSpace(path) ? path : (!string.IsNullOrWhiteSpace(file) ? file : "");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return BadRequest(new { ok = false, error = "Path or file is required", message = "Path or file is required" });
            }

            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", files = Array.Empty<object>() });
            }
            string root = ResolveWorkspacePath(cleanProj);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                string user1Path = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", "user_1", cleanProj);
                if (Directory.Exists(user1Path)) root = user1Path;
            }

            string safeRelPath = NormalizeWorkspaceRelativePath(targetPath, cleanProj);

            if (!TryGetSafeFilePath(root, safeRelPath, out string fullPath) || !System.IO.File.Exists(fullPath))
            {
                // Fallback 1: check if original targetPath exists in root
                if (!string.IsNullOrWhiteSpace(root) && System.IO.File.Exists(Path.Combine(root, targetPath)))
                {
                    fullPath = Path.Combine(root, targetPath);
                }
                else
                {
                    // Fallback 2: check in userRoot
                    string userRoot = GetUserRootPath();
                    if (!string.IsNullOrWhiteSpace(userRoot) && System.IO.File.Exists(Path.Combine(userRoot, targetPath)))
                    {
                        fullPath = Path.Combine(userRoot, targetPath);
                    }
                    else
                    {
                        return NotFound(new { ok = false, error = "Không tìm thấy tệp tin hoặc đường dẫn không hợp lệ.", message = "Không tìm thấy tệp tin hoặc đường dẫn không hợp lệ." });
                    }
                }
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

        // GET /api/ide/raw?project=...&file=...
        [HttpGet("raw")]
        public IActionResult GetRawFile([FromQuery] string? project, [FromQuery] string? file)
        {
            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", files = Array.Empty<object>() });
            }
            string root = ResolveWorkspacePath(cleanProj);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                string user1Path = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", "user_1", cleanProj);
                if (Directory.Exists(user1Path)) root = user1Path;
            }
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return NotFound("Project not found");
            }

            string targetFile = NormalizeWorkspaceRelativePath(file ?? "", cleanProj);
            if (!TryGetSafeFilePath(root, targetFile, out string fullPath) || !System.IO.File.Exists(fullPath))
            {
                return NotFound("File not found");
            }

            if (!_contentTypeProvider.TryGetContentType(fullPath, out string? contentType))
            {
                contentType = "application/octet-stream";
            }

            return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
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

                            // SAFETY GUARD: Do NOT wipe existing file if op.Content is null or empty
                            if (string.IsNullOrWhiteSpace(op.Content))
                            {
                                if (System.IO.File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
                                {
                                    // File already exists with real content on disk (e.g. written by autonomous agent)
                                    // Preserve existing content!
                                    successCount++;
                                    appliedFiles.Add(op.Path.Replace('\\', '/'));
                                    continue;
                                }
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

                        // Fallback: If image generation failed, write a beautiful binary image or SVG placeholder
                        if (!imageSaved)
                        {
                            string imgExt = Path.GetExtension(fullImgPath).ToLowerInvariant();
                            if (imgExt == ".svg")
                            {
                                string svgContent = GenerateSvgPlaceholder(Path.GetFileNameWithoutExtension(img.Path));
                                await System.IO.File.WriteAllTextAsync(fullImgPath, svgContent, Encoding.UTF8);
                            }
                            else
                            {
                                GenerateArtisticBinaryImage(fullImgPath, img.Prompt ?? Path.GetFileNameWithoutExtension(img.Path), img.Path, "16:9");
                            }
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

            // Fallback: Real binary image or SVG
            string fallbackExt = Path.GetExtension(fullPath).ToLowerInvariant();
            if (fallbackExt == ".svg")
            {
                string svg = GenerateSvgPlaceholder(Path.GetFileNameWithoutExtension(targetPath));
                await System.IO.File.WriteAllTextAsync(fullPath, svg, Encoding.UTF8);
            }
            else
            {
                GenerateArtisticBinaryImage(fullPath, model.Prompt, targetPath, "16:9");
            }

            return Ok(new
            {
                ok = true,
                path = targetPath,
                url = $"/api/ide/preview/{cleanProj}/{targetPath}",
                message = "Đã tạo ảnh minh họa cho dự án thành công."
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

            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }
            string root = ResolveWorkspacePath(cleanProj);
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

            string cleanProj = SanitizeProjectName(project);
            if (string.IsNullOrWhiteSpace(cleanProj))
            {
                return Ok(new { ok = true, project = "", tree = (object?)null });
            }
            string root = ResolveWorkspacePath(cleanProj);
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

                psi.Environment["CI"] = "false";
                psi.Environment["FORCE_COLOR"] = "0";
                psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
                psi.Environment["DOTNET_NOLOGO"] = "1";
                if (!isWindows)
                {
                    psi.Environment["DOTNET_ROOT"] = "/usr/share/dotnet";
                    string curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!curPath.Contains("/usr/share/dotnet"))
                    {
                        psi.Environment["PATH"] = $"/usr/share/dotnet:{curPath}";
                    }
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

        // POST /api/ide/run-command (Fallback REST endpoint for Terminal when SignalR disconnects)
        [HttpPost("run-command")]
        public async Task<IActionResult> RunCommandEndpoint([FromBody] RunCommandRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Command))
            {
                return BadRequest(new { exitCode = 1, output = "", error = "Command is required" });
            }

            int uid = (model.UserId.HasValue && model.UserId.Value > 0) ? model.UserId.Value : (CurrentUserId > 0 ? CurrentUserId : 1);
            string cleanProj = SanitizeProjectName(model.Project);
            string root = Path.Combine(AppContext.BaseDirectory, "App_Data", "UserWorkspaces", $"user_{uid}", cleanProj);
            if (!Directory.Exists(root))
            {
                root = ResolveWorkspacePath(model.Project);
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                try { Directory.CreateDirectory(root); } catch { }
            }

            string rawCmd = model.Command.Trim();
            // Strict sandbox & command injection prevention
            if (rawCmd.Contains("..") || rawCmd.Contains("cd /") || rawCmd.Contains("cd \\") ||
                rawCmd.Contains("rm -rf /") || rawCmd.Contains("del /s") || rawCmd.Contains("format "))
            {
                return BadRequest(new
                {
                    exitCode = 1,
                    output = "",
                    error = "Lệnh chứa ký tự điều hướng hoặc thao tác nguy hiểm không được phép trong sandbox."
                });
            }

            try
            {
                bool isWindows = OperatingSystem.IsWindows();
                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = Directory.Exists(root) ? root : AppContext.BaseDirectory,
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

                psi.Environment["CI"] = "false";
                psi.Environment["FORCE_COLOR"] = "0";
                psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
                psi.Environment["DOTNET_NOLOGO"] = "1";
                if (!isWindows)
                {
                    psi.Environment["DOTNET_ROOT"] = "/usr/share/dotnet";
                    string curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!curPath.Contains("/usr/share/dotnet"))
                    {
                        psi.Environment["PATH"] = $"/usr/share/dotnet:{curPath}";
                    }
                }

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return StatusCode(500, new { exitCode = 1, output = "", error = "Không thể khởi chạy tiến trình." });
                }

                var stdoutSb = new StringBuilder();
                var stderrSb = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await proc.WaitForExitAsync(cts.Token);

                return Ok(new
                {
                    exitCode = proc.ExitCode,
                    output = stdoutSb.ToString(),
                    error = stderrSb.ToString()
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(408, new { exitCode = 124, output = "", error = "Lệnh chạy vượt quá thời gian cho phép (45s timeout)." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RunCommand execution error");
                return StatusCode(500, new { exitCode = 1, output = "", error = ex.Message });
            }
        }

        // ==========================================
        // 5. BTOAN AGENT FULL-PROJECT CHAT & RUN
        // ==========================================

        // GET /api/ide/agent/history?project=... and /api/agent/history?project=...
        [HttpGet("agent/history")]
        [HttpGet("/api/agent/history")]
        public async Task<IActionResult> GetAgentHistory([FromQuery] string? project)
        {
            int userId = CurrentUserId > 0 ? CurrentUserId : 1;
            string cleanProj = SanitizeProjectName(project);

            try
            {
                var query = _dbContext.AgentChatMessages
                    .Where(m => m.UserId == userId && m.ProjectName == cleanProj);

                if (userId > 1 && !await query.AnyAsync())
                {
                    query = _dbContext.AgentChatMessages
                        .Where(m => m.UserId == 1 && m.ProjectName == cleanProj);
                }

                var messages = await query
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new
                    {
                        id = m.Id,
                        role = m.Role,
                        content = m.Content,
                        thoughtText = m.ThoughtText,
                        thoughtSeconds = m.ThoughtSeconds,
                        checkpointId = m.CheckpointId,
                        planJson = m.PlanJson,
                        filePillsJson = m.FilePillsJson,
                        toolActionsJson = m.ToolActionsJson,
                        createdAt = m.CreatedAt
                    })
                    .ToListAsync();

                if (messages.Count == 0)
                {
                    string userRoot = GetUserRootPath(userId);
                    string projectPath = Path.Combine(userRoot, cleanProj);
                    string chatFile = Path.Combine(projectPath, ".btoan_chat.json");
                    if (!System.IO.File.Exists(chatFile) && userId > 1)
                    {
                        string user1Root = GetUserRootPath(1);
                        chatFile = Path.Combine(user1Root, cleanProj, ".btoan_chat.json");
                    }
                    if (System.IO.File.Exists(chatFile))
                    {
                        try
                        {
                            string json = await System.IO.File.ReadAllTextAsync(chatFile, Encoding.UTF8);
                            var diskMsgs = JsonSerializer.Deserialize<List<object>>(json);
                            if (diskMsgs != null && diskMsgs.Count > 0)
                            {
                                return Ok(new { ok = true, project = cleanProj, messages = diskMsgs });
                            }
                        }
                        catch { }
                    }
                }

                return Ok(new { ok = true, project = cleanProj, messages });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load agent history for {Project}", cleanProj);
                return Ok(new { ok = true, project = cleanProj, messages = Array.Empty<object>() });
            }
        }

        private static void AppendProjectChatJson(string projectPath, object msg)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;
                string chatFile = Path.Combine(projectPath, ".btoan_chat.json");
                var list = new List<object>();
                if (System.IO.File.Exists(chatFile))
                {
                    try
                    {
                        string existing = System.IO.File.ReadAllText(chatFile, Encoding.UTF8);
                        var parsed = JsonSerializer.Deserialize<List<object>>(existing);
                        if (parsed != null) list = parsed;
                    }
                    catch { }
                }
                list.Add(msg);
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(chatFile, json, Encoding.UTF8);
            }
            catch { }
        }

        // DELETE /api/ide/agent/history?project=... or POST /api/ide/agent/clear-history
        [HttpDelete("agent/history")]
        [HttpDelete("/api/agent/history")]
        [HttpPost("agent/clear-history")]
        [HttpPost("/api/agent/clear-history")]
        public async Task<IActionResult> ClearAgentHistory([FromQuery] string? project, [FromBody] JsonObject? body)
        {
            int userId = CurrentUserId > 0 ? CurrentUserId : 1;
            string? proj = project ?? body?["project"]?.ToString();
            string cleanProj = SanitizeProjectName(proj);

            try
            {
                var oldMsgs = await _dbContext.AgentChatMessages
                    .Where(m => m.UserId == userId && m.ProjectName == cleanProj)
                    .ToListAsync();

                if (oldMsgs.Count > 0)
                {
                    _dbContext.AgentChatMessages.RemoveRange(oldMsgs);
                    await _dbContext.SaveChangesAsync();
                }

                string userRoot = GetUserRootPath(userId);
                string projectPath = Path.Combine(userRoot, cleanProj);
                string chatFile = Path.Combine(projectPath, ".btoan_chat.json");
                if (System.IO.File.Exists(chatFile))
                {
                    try { System.IO.File.Delete(chatFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear agent history for {Project}", cleanProj);
            }

            return Ok(new { ok = true, project = cleanProj, message = "Đã khởi tạo phiên trò chuyện mới thành công." });
        }

        private static bool IsCasualGreeting(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            string clean = prompt.Trim().ToLowerInvariant().TrimEnd('!', '?', '.', '~', ' ', ',');
            return clean is "ê" or "e" or "ê bạn" or "alo" or "alô" or "hi" or "hello" or "chào" or "chào bạn" or "bạn ơi" or "test" or "hey" or "yo" or "ơi" or "oi";
        }

        // POST /api/ide/agent-chat and /api/ide/agent-run
        [HttpPost("agent-chat")]
        [HttpPost("agent-run")]
        [HttpPost("/api/agent/run")]
        [HttpPost("/api/agent/chat")]
        [HttpPost("/api/agent/agent-run")]
        [HttpPost("/api/agent/agent-chat")]
        public async Task AgentChat([FromBody] AgentChatRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Prompt))
            {
                Response.StatusCode = 400;
                await Response.WriteAsJsonAsync(new { ok = false, error = "Prompt is required" });
                return;
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
                try { Directory.CreateDirectory(projectPath); } catch { }
            }

            bool isStream = model.Stream || Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

            if (isStream)
            {
                HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
                Response.ContentType = "text/event-stream; charset=utf-8";
                Response.Headers["Cache-Control"] = "no-cache, no-transform";
                Response.Headers["X-Accel-Buffering"] = "no";
                Response.Headers["Connection"] = "keep-alive";
            }

            var sseLock = new SemaphoreSlim(1, 1);

            async Task SendSseEventAsync(string eventType, object data)
            {
                if (!isStream || HttpContext.RequestAborted.IsCancellationRequested) return;
                if (!await sseLock.WaitAsync(TimeSpan.FromSeconds(3))) return;
                try
                {
                    string json = JsonSerializer.Serialize(data);
                    string sse = $"event: {eventType}\ndata: {json}\n\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(sse);
                    using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                    writeCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await Response.Body.WriteAsync(bytes, 0, bytes.Length, writeCts.Token);
                    await Response.Body.FlushAsync(writeCts.Token);
                }
                catch { }
                finally
                {
                    try { sseLock.Release(); } catch { }
                }
            }

            async Task SendKeepaliveCommentAsync()
            {
                if (!isStream || HttpContext.RequestAborted.IsCancellationRequested) return;
                bool lockTaken = false;
                try
                {
                    lockTaken = await sseLock.WaitAsync(TimeSpan.FromSeconds(3), HttpContext.RequestAborted);
                    if (!lockTaken) return;

                    byte[] bytes = Encoding.UTF8.GetBytes(": keep-alive\n\n");
                    using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                    writeCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await Response.Body.WriteAsync(bytes, 0, bytes.Length, writeCts.Token);
                    await Response.Body.FlushAsync(writeCts.Token);
                }
                catch { }
                finally
                {
                    if (lockTaken)
                    {
                        try { sseLock.Release(); } catch { }
                    }
                }
            }

            using var keepaliveCts = new CancellationTokenSource();
            var keepaliveTask = isStream ? Task.Run(async () =>
            {
                while (!keepaliveCts.Token.IsCancellationRequested && !HttpContext.RequestAborted.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(10000, keepaliveCts.Token);
                        if (keepaliveCts.Token.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested) break;
                        await SendKeepaliveCommentAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "SSE keepalive error ignored");
                    }
                }
            }) : Task.CompletedTask;

            string selectedModel = "gemini-2.5-flash";
            if (!string.IsNullOrWhiteSpace(model.Model))
            {
                string mLower = model.Model.Trim().ToLowerInvariant();
                if (mLower.Contains("pro"))
                {
                    selectedModel = "gemini-2.5-flash";
                }
                else if (mLower.Contains("lite"))
                {
                    selectedModel = "gemini-3.5-flash-lite";
                }
                else
                {
                    // Both Btoan 3.5 Flash and Btoan 2.5 Flash route to gemini-2.5-flash
                    selectedModel = "gemini-2.5-flash";
                }
            }
            bool isFastMode = string.Equals(model.Mode, "fast", StringComparison.OrdinalIgnoreCase);

            await SendSseEventAsync("status", new { stage = "init", message = $"Btoan Agent ({selectedModel}) đang phân tích yêu cầu...", project = cleanProj });

            string checkpointId = await CreateProjectCheckpointInternalAsync(projectPath);
            var swAgent = System.Diagnostics.Stopwatch.StartNew();

            // 1. Lưu tin nhắn người dùng vào SQLite bền vững
            try
            {
                var userChatMsg = new Models.AgentChatMessage
                {
                    UserId = userId,
                    ProjectName = cleanProj,
                    Role = "user",
                    Content = model.Prompt,
                    CheckpointId = checkpointId,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.AgentChatMessages.Add(userChatMsg);
                await _dbContext.SaveChangesAsync();
                AppendProjectChatJson(projectPath, new
                {
                    id = userChatMsg.Id,
                    role = "user",
                    content = userChatMsg.Content,
                    checkpointId = userChatMsg.CheckpointId,
                    createdAt = userChatMsg.CreatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist user agent chat message");
            }

            // Fast Intent Gate: Trả lời chào hỏi / hội thoại thông thường ngay lập tức (không chạy tool hay quét dự án)
            if (IsCasualGreeting(model.Prompt))
            {
                string greetingResponse = "Chào bạn! Mình là Btoan AI Agent. Dự án của bạn đã sẵn sàng, bạn cần mình hỗ trợ viết code, giải thích logic hay sửa lỗi phần nào không?";
                await SendSseEventAsync("status", new { stage = "chat", message = "Btoan Agent sẵn sàng hỗ trợ..." });
                await Task.Delay(150);
                await SendSseEventAsync("done", new
                {
                    explanation = greetingResponse,
                    plan = Array.Empty<string>(),
                    modifiedFiles = Array.Empty<string>(),
                    operations = Array.Empty<object>(),
                    checkpointId
                });
                try
                {
                    var botChatMsg = new Models.AgentChatMessage
                    {
                        UserId = userId,
                        ProjectName = cleanProj,
                        Role = "assistant",
                        Content = greetingResponse,
                        CheckpointId = checkpointId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.AgentChatMessages.Add(botChatMsg);
                    await _dbContext.SaveChangesAsync();
                }
                catch { }
                return;
            }

            var sbContext = new StringBuilder();
            AutoFixMapStaticAssets(projectPath);

            bool hasCsproj = false;
            try
            {
                var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
                hasCsproj = csprojFiles.Length > 0;
            }
            catch { }

            // Pre-populate project file tree so AI knows structure immediately without wasting turns on list_files
            try
            {
                var allEntries = Directory.GetFileSystemEntries(projectPath, "*", SearchOption.AllDirectories)
                    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                !p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
                    .Select(p => Path.GetRelativePath(projectPath, p).Replace('\\', '/'))
                    .Take(120);
                sbContext.AppendLine("[PROJECT FILE TREE]:");
                sbContext.AppendLine(string.Join("\n", allEntries));
            }
            catch { }

            if (hasCsproj)
            {
                sbContext.AppendLine("[PROJECT ENVIRONMENT]: Dự án C# / ASP.NET Core (đã phát hiện tệp .csproj). Áp dụng tiêu chuẩn .NET 10 (net10.0), EF Core SQLite, chia tách Controllers/Models/Views.");
            }
            else
            {
                sbContext.AppendLine("[PROJECT ENVIRONMENT]: Thư mục hiện tại KHÔNG CÓ tệp .csproj. Nếu yêu cầu là làm trang web (web bệnh viện, bán hàng, đặt lịch, giới thiệu, blog, landing page, dashboard...) mà người dùng không yêu cầu rõ .NET hay C#, bạn BẮT BUỘC tạo dự án Web Tĩnh hoàn chỉnh (index.html, styles.css, app.js với dữ liệu mẫu và lưu vào localStorage) để người dùng xem trực tiếp trên Live Preview ngay lập tức; TUYỆT ĐỐI KHÔNG tự tiện chạy 'dotnet new' hay biến thành dự án .NET nặng!");
            }

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

            if (model.ContextFiles != null && model.ContextFiles.Count > 0)
            {
                foreach (var cf in model.ContextFiles)
                {
                    if (string.IsNullOrWhiteSpace(cf.Path)) continue;
                    string cPath = Path.Combine(projectPath, cf.Path.TrimStart('/', '\\'));
                    if (System.IO.File.Exists(cPath))
                    {
                        try
                        {
                            string cContent = !string.IsNullOrEmpty(cf.Content) ? cf.Content : await System.IO.File.ReadAllTextAsync(cPath, Encoding.UTF8);
                            if (cContent.Length > 6000) cContent = cContent.Substring(0, 6000) + "\n...[truncated]";
                            sbContext.AppendLine($"--- CONTEXT FILE: {cf.Path} ---");
                            sbContext.AppendLine(cContent);
                            sbContext.AppendLine("--- END FILE ---");
                        }
                        catch { }
                    }
                }
            }

            string agentSystemInstruction = $@"Bạn là Btoan AI Autonomous Coding Agent trong Btoan Studio IDE (tương đương chuẩn Antigravity IDE & Senior Software Architect).
Bạn đang làm việc trực tiếp trên thư mục dự án: '{cleanProj}'.

QUY TẮC PHÂN LOẠI Ý ĐỊNH (INTENT GATE) BẮT BUỘC (QUAN TRỌNG NHẤT):
- Nhánh 1 — Trò chuyện, chào hỏi & hỏi đáp thông thường (Conversational / Q&A Mode):
  + Kích hoạt khi người dùng chỉ chào hỏi, gọi thử (""ê"", ""alo"", ""hi"", ""hello"", ""bạn ơi"", ""test"", ""hey"", ""ơi""), trò chuyện phiếm, hoặc hỏi giải thích/tư vấn (""file này làm gì"", ""tại sao bị lỗi này"", ""nên dùng cách nào"", ""cấu trúc dự án ra sao"") mà KHÔNG yêu cầu tạo, sửa, xóa file hay chạy lệnh.
  + BẮT BUỘC: Trả lời ngắn gọn, tự nhiên, thân thiện bằng tiếng Việt như một cộng sự lập trình. TUYỆT ĐỐI KHÔNG quét toàn bộ dự án, KHÔNG xuất thẻ Plan/kế hoạch, và TUYỆT ĐỐI KHÔNG gọi bất kỳ công cụ chỉnh sửa/thực thi nào (list_dir, write_file, patch_file, edit_file, delete_file, run_command, generate_image).
  + ĐẶC BIỆT: Nếu ô chat có gắn thẻ chip file đính kèm (@file, ví dụ shop-senior-mvc.csproj) nhưng người dùng chỉ nhắn tin chào hỏi thông thường (""ê"", ""hi""), bạn CHỈ CHÀO LẠI BÌNH THƯỜNG chứ TUYỆT ĐỐI KHÔNG tự động sửa hay can thiệp vào file đính kèm đó. Chỉ đọc và phân tích file đính kèm khi người dùng hỏi cụ thể về nội dung file đó.
- Nhánh 2 — Thực thi kỹ thuật (Action / Coding Mode):
  + Chỉ kích hoạt khi người dùng đưa ra yêu cầu hành động rõ ràng (ví dụ: ""tạo..."", ""sửa..."", ""thêm..."", ""xóa..."", ""fix lỗi..."", ""build..."", ""viết code..."", ""chạy lệnh...""). Khi đó mới lập kế hoạch và gọi các công cụ thực thi mã nguồn.

CÁC CÔNG CỤ THỰC THI (FUNCTION CALLING) BẠN ĐƯỢC CẤP:
- list_dir(path): Liệt kê cấu trúc cây thư mục kèm dung lượng (bytes) và số dòng của từng tệp.
- read_file(path, start_line, end_line): Đọc mã nguồn tệp trong dự án kèm số thứ tự từng dòng (1: ..., 2: ...) để định vị chính xác vùng code.
- grep_search(query, include_pattern): Tìm kiếm chuỗi, tên hàm, class trên toàn bộ mã nguồn của dự án hiện tại.
- write_file(path, content): Tạo mới hoặc ghi đè tệp với code hoàn chỉnh 100%. Tham số 'content' BẮT BUỘC chứa toàn bộ mã nguồn, TUYỆT ĐỐI KHÔNG được để trống (0 byte).
- patch_file(path, target_block, replacement_block): Sửa chính xác một khối code trong tệp mà không cần ghi lại toàn bộ file.
- edit_file(path, target, replacement): Sửa đoạn code trong tệp.
- delete_file(path): Xóa tệp khi người dùng yêu cầu tường minh trong tin nhắn. TUYỆT ĐỐI CẤM gọi delete_file để rollback hoặc xóa file vừa tạo khi tự sửa lỗi/biên dịch thất bại.
- run_command(command): Chạy lệnh shell (ví dụ: 'dotnet build', 'dotnet add package Microsoft.EntityFrameworkCore.Sqlite'...) tại thư mục dự án.
- generate_image(prompt, output_path, aspect_ratio): Tạo hình ảnh AI chất lượng cao lưu thẳng vào thư mục dự án (ví dụ 'wwwroot/images/hero.jpg' cho MVC hoặc 'images/hero.jpg' cho web tĩnh).

QUY TẮC NHẬN DIỆN CÔNG NGHỆ & LỰA CHỌN FRAMEWORK (QUAN TRỌNG HÀNG ĐẦU):
1. ƯU TIÊN WEB TĨNH (HTML5 / CSS3 / JAVASCRIPT THUẦN) CHO MỌI YÊU CẦU WEB THÔNG THƯỜNG:
   - KÍCH HOẠT KHI: Người dùng yêu cầu làm web (ví dụ: ""tạo web bệnh viện"", ""làm web bán hàng"", ""tạo web đặt lịch"", ""làm landing page"", ""web giới thiệu"", ""portfolio"", ""dashboard""...) MÀ KHÔNG ĐỀ CẬP RÕ ""C#"", "".NET"", ""ASP.NET"", ""MVC"" VÀ trong thư mục hiện tại CHƯA CÓ tệp *.csproj.
   - NGUYÊN TẮC: TUYỆT ĐỐI CẤM tự ý chạy 'dotnet new' hay cài đặt gói NuGet!
   - CẤU TRÚC: Tạo trực tiếp các tệp web chuẩn:
     + index.html: Giao diện hoàn chỉnh, hiện đại (sử dụng Bootstrap 5 hoặc Tailwind CSS qua CDN, FontAwesome, Google Fonts).
     + styles.css: Bố cục đẹp mắt, responsive đa thiết bị, animation mượt mà.
     + app.js: Xử lý toàn bộ logic tương tác (form đặt lịch, validation, giỏ hàng, tìm kiếm, lọc danh mục, modal).
     + LƯU TRỮ LOCALSTORAGE: Với các tính năng cần lưu trữ dữ liệu (đặt lịch khám bệnh, danh sách đơn hàng, quản lý), BẮT BUỘC dùng localStorage để lưu bền vững dữ liệu mẫu và dữ liệu người dùng nhập, để khi F5 trang vẫn giữ nguyên.
   - LỢI ÍCH: Người dùng chỉ cần bấm nút ""Live Preview ▶"" trên IDE là trang web hiển thị ngay lập tức trên cổng preview mà KHÔNG CẦN biên dịch hay chờ 'dotnet build'!

2. CHỈ DÙNG ASP.NET CORE MVC (.NET 10) KHI:
   - Thư mục hiện tại ĐÃ CÓ tệp *.csproj (ví dụ shop-senior-mvc), HOẶC
   - Người dùng yêu cầu cụ thể: ""C#"", "".NET"", ""ASP.NET"", ""MVC"", ""Web API"", ""Blazor"".
   - KHI ĐÓ: Mới áp dụng tiêu chuẩn Senior Architect net10.0, EF Core SQLite, chia tách Controllers/Models/Views/Data, và chạy 'dotnet build'.

3. DỰ ÁN NODE.JS / REACT / ANGULAR / PYTHON:
   - Kích hoạt khi thư mục đã có package.json, requirements.txt hoặc người dùng yêu cầu rõ ràng.

QUY TRÌNH & TIÊU CHUẨN KIẾN TRÚC SENIOR ARCHITECT BẮT BUỘC:

0. BẮT BUỘC TARGET FRAMEWORK net10.0 CHO DỰ ÁN C# / .NET (CẤM DÙNG net8.0):
   - Container production CHỈ CÀI ĐẶT .NET 10 SDK (net10.0) và KHÔNG CÓ .NET 8 Runtime.
   - Mọi tệp *.csproj khi tạo mới hoặc chỉnh sửa BẮT BUỘC phải đặt <TargetFramework>net10.0</TargetFramework> và dùng các gói NuGet tương thích 10.0.* (ví dụ Microsoft.EntityFrameworkCore.Sqlite phiên bản 10.0.*).
   - TUYỆT ĐỐI KHÔNG DÙNG net8.0 hoặc các phiên bản cũ hơn.
   - Trong file .csproj cho dự án .NET, BẮT BUỘC thêm thẻ <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn> bên trong <PropertyGroup> để loại trừ triệt để cảnh báo bảo mật NuGet Audit khi build.

1. NGUYÊN TẮC PHÂN TÁCH CONTROLLER & THƯ MỤC CHUẨN ASP.NET CORE MVC:
   - CẤM TUYỆT ĐỐI dồn code vào HomeController.cs. HomeController.cs chỉ dùng cho trang chủ tổng quan (Index), giới thiệu (Privacy/About) và trang lỗi (Error).
   - BẮT BUỘC chia nhỏ thành các Domain Controller chuyên biệt theo đúng chuẩn MVC:
     + Controllers/ProductsController.cs (hoặc ProductController.cs): Danh sách sản phẩm, lọc/tìm kiếm theo danh mục, trang chi tiết (Details), kèm bộ action quản trị CRUD (Manage, Create, Edit, Delete) thao tác trực tiếp với Database.
     + Controllers/CartController.cs: Xem giỏ hàng, thêm vào giỏ, tăng/giảm số lượng, xóa sản phẩm khỏi giỏ (lưu qua Session hoặc DB).
     + Controllers/OrdersController.cs: Trang thanh toán (Checkout), xử lý lưu đơn hàng + chi tiết đơn hàng vào Database, trang xác nhận (Confirmation) và tra cứu lịch sử đơn hàng (Index/Details).
     + Controllers/AccountController.cs (khi ứng dụng có đăng nhập/đăng ký): Đăng ký, Đăng nhập, Đăng xuất, Quản lý tài khoản.
   - Tổ chức Views/ khớp 1-1 với Controller: Views/Home/, Views/Products/, Views/Cart/, Views/Orders/, Views/Account/, Views/Shared/ (kèm _ValidationScriptsPartial.cshtml cho form nhập liệu).

2. BẮT BUỘC DÙNG DATABASE THẬT (EF CORE + SQLITE) & SEED DATA (KHÔNG DÙNG DỮ LIỆU GIẢ):
   - CẤM TUYỆT ĐỐI hardcode mảng dữ liệu giả tĩnh trong Controller.
   - Với mọi dự án ASP.NET Core MVC hoặc Web API có lưu trữ dữ liệu (bán hàng, quản lý, blog, đặt lịch, thư viện...), BẮT BUỘC thực hiện đủ 5 bước:
     1) Cài đặt gói Microsoft.EntityFrameworkCore.Sqlite (qua run_command(""dotnet add package Microsoft.EntityFrameworkCore.Sqlite"") hoặc khai báo chuẩn trong *.csproj).
     2) Tạo các Entity hoàn chỉnh trong Models/ (Product.cs, Category.cs, Order.cs, OrderItem.cs, User.cs...) có đầy đủ khóa chính, khóa ngoại, navigation properties và Data Annotations ([Required], [StringLength], [Range], [Display]).
     3) Tạo Data/AppDbContext.cs kế thừa DbContext với đầy đủ các DbSet<...> và cấu hình quan hệ bảng trong OnModelCreating.
     4) Tạo Data/SeedData.cs kiểm tra nếu bảng trống thì tự động nạp dữ liệu mẫu tiếng Việt phong phú, thực tế (tối thiểu 6-8 sản phẩm thuộc nhiều danh mục).
      5) CẤU HÌNH SQLITE ĐỒNG BỘ: BẮT BUỘC cập nhật đồng bộ ""DefaultConnection"": ""Data Source=app.db"" ở CẢ appsettings.json, appsettings.Development.json và chuỗi fallback trong Program.cs (builder.Configuration.GetConnectionString(""DefaultConnection"") ?? ""Data Source=app.db""). TUYỆT ĐỐI KHÔNG để sót chuỗi ""Server=..."" của SQL Server hay LocalDB.
      6) Trong Program.cs đăng ký AddDbContext<AppDbContext>(opts => opts.UseSqlite(...)), AddSession() (kèm app.UseSession()), và gọi context.Database.EnsureCreated() + SeedData.Initialize(serviceProvider) ngay khi khởi động ứng dụng.
      7) TÀI NGUYÊN WWWROOT BẮT BUỘC: Mọi dự án ASP.NET Core MVC bắt buộc phải tạo đầy đủ thư mục và tệp wwwroot/css/site.css, wwwroot/js/site.js và wwwroot/images/ khớp với _Layout.cshtml để trang web hiển thị chuẩn đẹp, không bị lỗi 404 khi tải css/js/ảnh.

3. CHUẨN HÓA VIEWMODELS, HELPERS & TẠO ẢNH THẬT TRONG DỰ ÁN:
   - Tách riêng thư mục Models/ViewModels/ (CartItemViewModel.cs, CheckoutViewModel.cs, HomeViewModel.cs...) để không truyền dữ liệu rời rạc qua ViewBag tràn lan.
   - Tạo Helpers/SessionExtensions.cs (SetObjectAsJson / GetObjectFromJson) khi sử dụng giỏ hàng Session.
   - Khi làm web bán hàng/landing page có hình ảnh sản phẩm hoặc banner, ưu tiên gọi công cụ generate_image tạo file ảnh thật lưu vào wwwroot/images/ (với MVC) hoặc images/ (với web tĩnh) và trỏ đường dẫn nội bộ /images/... để không phụ thuộc link ngoài dễ chết. Giới hạn tối đa 2-3 ảnh thiết yếu (hero banner + 1-2 sản phẩm tiêu biểu) để tiết kiệm thời gian.

4. TIÊU CHUẨN KIẾN TRÚC CHO CÁC NỀN TẢNG KHÁC:
   - ASP.NET Core Web API: Tách Controller theo từng tài nguyên (ProductsController, OrdersController, AuthController), có DTOs/, Data/AppDbContext.cs (SQLite) + SeedData.cs, bật Swagger UI.
   - Angular / Fullstack: Tách rõ thư mục src/app/models/, src/app/services/, src/app/components/, src/app/pages/ thay vì viết dồn vào app.component.ts.
   - HTML5 / CSS3 / JS thuần: Viết JS theo cấu trúc module rõ ràng và lưu bền vững dữ liệu (giỏ hàng, đơn hàng, danh sách) vào localStorage để tải lại trang không bị mất.
   - Với ứng dụng công cụ/tính toán (tính lương, điểm tốt nghiệp, máy tính, dashboard): TUYỆT ĐỐI KHÔNG tạo ảnh thừa; tập trung 100% vào mã nguồn chất lượng cao, logic tính toán chính xác theo quy chuẩn Việt Nam.

5. CÚ PHÁP C# & .NET CHUẨN XÁC 100%:
   - Trong file .csproj cho dự án .NET, BẮT BUỘC thêm thẻ <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn> bên trong <PropertyGroup> để loại trừ triệt để cảnh báo bảo mật NuGet Audit khi build.
   - Khởi tạo Collection/List: Dùng 'new List<T>() {{ ... }};' hoặc 'new() {{ ... }};' (MỞ NGOẶC NHỌN '{{' THÌ BẮT BUỘC ĐÓNG BẰNG '}};' - TUYỆT ĐỐI KHÔNG ĐƯỢC ĐÓNG BẰNG '];').
   - Nếu dùng C# 12 collection expressions: '[ ... ];' (mở ngoặc vuông thì đóng ngoặc vuông). TUYỆT ĐỐI KHÔNG nhầm lẫn đóng ngoặc.

6. PHA VERIFY & SELF-HEAL (TỐI ĐA 2 LẦN BUILD - BẢO TOÀN MÃ NGUỒN):
   - Với dự án C# / .NET (có tệp *.csproj): Trước khi kết thúc, chạy 'run_command(""dotnet build -p:UseSharedCompilation=false"")'.
   - Với dự án Web Tĩnh (HTML/CSS/JS): TUYỆT ĐỐI KHÔNG CHẠY LỆNH 'dotnet build' HAY BẤT KỲ LỆNH DOTNET NÀO! Khi đã tạo xong index.html, styles.css, app.js là hoàn tất nhiệm vụ.
   - Nếu ExitCode == 0 (hoặc 0 Error(s)), coi như THÀNH CÔNG HOÀN TOÀN (bỏ qua Warning), dừng gọi tool ngay và báo cáo kết quả cho người dùng.
   - Nếu có lỗi compiler, đọc lỗi kỹ và gọi patch_file sửa lỗi tối đa 2 lần.
   - TUYỆT ĐỐI CẤM gọi delete_file để xóa hoặc rollback file vừa tạo khi build gặp lỗi. Phải giữ nguyên toàn bộ file đã tạo để người dùng tiếp tục thao tác.

NĂNG LỰC THỊ GIÁC (MULTIMODAL VISION):
- Nếu người dùng có đính kèm ảnh chụp màn hình (screenshot) hoặc ảnh giao diện: Bạn BẮT BUỘC phải quan sát kỹ bức ảnh được gửi kèm trong lượt chat (giao diện, hình ảnh hiển thị, bảng số liệu, màu sắc, bố cục hoặc chi tiết bị sai lệch).
- Hãy đối chiếu những gì bạn nhìn thấy trên ảnh với mã nguồn dự án, sau đó gọi read_file / patch_file / write_file hoặc generate_image để sửa dứt điểm lỗi hiển thị mà người dùng phản ánh.

TUYỆT ĐỐI KHÔNG XUẤT RA CHUỖI JSON {{ ""plan"": [...] }} DẠNG TEXT:
- Bạn BẮT BUỘC PHẢI GỌI FUNCTION CALLING (write_file / patch_file / run_command / generate_image) ngay từ lượt đầu tiên.
- Khi đã hoàn thành tất cả các file và xác thực build 0 Error(s), dừng gọi tool và trả lời tóm tắt bằng tiếng Việt.";

            var toolsDefinition = new JsonArray
            {
                new JsonObject
                {
                    ["functionDeclarations"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "list_dir",
                            ["description"] = "Liệt kê cấu trúc cây thư mục kèm dung lượng (bytes) và số dòng của từng tệp.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Thư mục tương đối cần xem (mặc định '.' là thư mục gốc dự án)."
                                    }
                                }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "list_files",
                            ["description"] = "Liệt kê danh sách tệp và thư mục con trong dự án.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["directory"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Thư mục tương đối cần xem (mặc định '.' là thư mục gốc)."
                                    }
                                }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "read_file",
                            ["description"] = "Đọc nội dung của một tệp trong dự án kèm số thứ tự từng dòng (1: ..., 2: ...) để định vị chính xác vùng code cần sửa.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn tương đối của tệp trong dự án (ví dụ: 'Program.cs', 'index.html')."
                                    },
                                    ["start_line"] = new JsonObject
                                    {
                                        ["type"] = "INTEGER",
                                        ["description"] = "Dòng bắt đầu đọc (tùy chọn, mặc định 1)."
                                    },
                                    ["end_line"] = new JsonObject
                                    {
                                        ["type"] = "INTEGER",
                                        ["description"] = "Dòng kết thúc đọc (tùy chọn, mặc định đọc hết tệp)."
                                    }
                                },
                                ["required"] = new JsonArray { "path" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "grep_search",
                            ["description"] = "Tìm kiếm chuỗi, tên hàm, class trên toàn bộ mã nguồn của dự án hiện tại.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["query"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Chuỗi văn bản hoặc từ khóa cần tìm kiếm."
                                    },
                                    ["include_pattern"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Mẫu tệp tìm kiếm (ví dụ: '*.cs', '*.html', '*.js', '*.*')."
                                    }
                                },
                                ["required"] = new JsonArray { "query" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "write_file",
                            ["description"] = "Tạo mới hoặc ghi đè toàn bộ nội dung của một tệp trong dự án với mã nguồn hoàn chỉnh 100%. Tham số 'content' BẮT BUỘC chứa đầy đủ mã nguồn, TUYỆT ĐỐI KHÔNG được để trống.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn tương đối của tệp trong dự án (ví dụ: 'index.html', 'style.css', 'script.js')."
                                    },
                                    ["content"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Toàn bộ mã nguồn hoàn chỉnh và đầy đủ của tệp (không để trống)."
                                    }
                                },
                                ["required"] = new JsonArray { "path", "content" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "patch_file",
                            ["description"] = "Sửa chính xác một khối mã nguồn bên trong tệp bằng cách tìm đúng đoạn target_block và thay bằng replacement_block.",
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
                                    ["target_block"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Khối mã nguồn hiện tại trong tệp cần thay thế (bao gồm thụt đầu dòng)."
                                    },
                                    ["replacement_block"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Khối mã nguồn mới thay thế vào (không để trống)."
                                    }
                                },
                                ["required"] = new JsonArray { "path", "target_block", "replacement_block" }
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
                            ["description"] = "Thực thi lệnh shell (PowerShell/Bash) ngay tại thư mục dự án (ví dụ: 'dotnet build', 'dotnet add package ...').",
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
                        },
                        new JsonObject
                        {
                            ["name"] = "generate_image",
                            ["description"] = "Tạo hình ảnh minh họa thật bằng AI và lưu trực tiếp vào thư mục dự án (ví dụ: 'images/banner.jpg', 'images/product1.jpg'). BẮT BUỘC gọi tool này để tạo hình ảnh trực quan cho website thay vì dùng ảnh rỗng.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["prompt"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Mô tả chi tiết hình ảnh cần tạo bằng tiếng Anh hoặc tiếng Việt."
                                    },
                                    ["output_path"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Đường dẫn lưu ảnh tương đối trong dự án (ví dụ: 'images/hero.jpg', 'images/item1.jpg')."
                                    },
                                    ["aspect_ratio"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Tỉ lệ khung hình: '16:9' (banner/hero), '1:1' (sản phẩm/avatar), '4:3', '9:16'. Mặc định: '16:9'."
                                    }
                                },
                                ["required"] = new JsonArray { "prompt", "output_path" }
                            }
                        }
                    }
                }
            };

            var messages = new JsonArray();

            // Preload recent chat history (up to 8 messages) for conversational multi-turn context
            try
            {
                var historyMessages = await _dbContext.AgentChatMessages
                    .Where(m => m.UserId == userId && m.ProjectName == cleanProj)
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(8)
                    .OrderBy(m => m.CreatedAt)
                    .ToListAsync();

                foreach (var h in historyMessages)
                {
                    if (h.Role == "user" && h.Content == model.Prompt) continue;
                    string r = h.Role == "assistant" ? "model" : "user";
                    messages.Add(new JsonObject
                    {
                        ["role"] = r,
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = h.Content } }
                    });
                }
            }
            catch { }

            var userParts = new JsonArray
            {
                new JsonObject
                {
                    ["text"] = $"{agentSystemInstruction}\n\n[CONTEXT FILES]:\n{sbContext}\n\n[USER REQUEST]: {model.Prompt}"
                }
            };

            if (!string.IsNullOrWhiteSpace(model.ImageBase64))
            {
                try
                {
                    string cleanB64 = System.Text.RegularExpressions.Regex.Replace(model.ImageBase64, @"^data:image\/[^;]+;base64,", "").Trim();
                    string mime = !string.IsNullOrWhiteSpace(model.ImageMimeType) ? model.ImageMimeType : "image/jpeg";
                    userParts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = mime,
                            ["data"] = cleanB64
                        }
                    });
                }
                catch { }
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = userParts
            });

            var modifiedFiles = new List<string>();
            var executedCommands = new List<string>();
            var toolOperations = new List<object>();
            var planSteps = new List<string>();
            var accumulatedThought = new StringBuilder();
            bool buildPassed = false;
            string finalExplanation = "";

            int maxTurns = 25;
            int currentTurn = 0;
            int buildAttempts = 0;

            try
            {
                while (currentTurn < maxTurns)
                {
                    if (currentTurn > 0)
                    {
                        await Task.Delay(800);
                    }
                    currentTurn++;
                    await SendSseEventAsync("turn_start", new { turn = currentTurn, message = $"Btoan Agent đang phân tích & lên phương án thực thi (Lượt {currentTurn}/{maxTurns})..." });

                    var payload = new JsonObject
                    {
                        ["systemInstruction"] = new JsonObject
                        {
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = agentSystemInstruction } }
                        },
                        ["contents"] = messages.DeepClone(),
                        ["tools"] = toolsDefinition.DeepClone(),
                        ["toolConfig"] = new JsonObject
                        {
                            ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" }
                        },
                        ["generationConfig"] = new JsonObject
                        {
                            ["temperature"] = isFastMode ? 0.2 : 0.4,
                            ["maxOutputTokens"] = 16384
                        }
                    };
                    if (isFastMode)
                    {
                        payload["generationConfig"]!["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
                    }

                    var (success, resData, _, error) = await _geminiService.GenerateContentAsync(selectedModel, payload, async msg =>
                    {
                        await SendSseEventAsync("status", new { stage = "fallback", message = msg });
                    });
                    if (!success || resData == null)
                    {
                        finalExplanation = $"Lỗi kết nối AI: {error}";
                        if (isStream)
                        {
                            await SendSseEventAsync("error", new { ok = false, error = finalExplanation });
                        }
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

                    // Check if there are functionCalls in any part (support parallel tool calls)
                    var functionCalls = new List<(string Name, JsonObject? Args)>();
                    var sbCandidateText = new System.Text.StringBuilder();

                    foreach (var p in parts)
                    {
                        if (p is JsonObject pObj)
                        {
                            if (pObj.ContainsKey("functionCall") && pObj["functionCall"] is JsonObject fCall)
                            {
                                string fName = fCall["name"]?.ToString() ?? "";
                                var fArgs = fCall["args"]?.AsObject();
                                if (!string.IsNullOrWhiteSpace(fName))
                                {
                                    functionCalls.Add((fName, fArgs));
                                }
                            }
                            
                            bool isThought = false;
                            if (pObj.ContainsKey("thought"))
                            {
                                try { isThought = pObj["thought"]?.GetValue<bool>() == true; } catch { }
                            }

                            if (pObj.ContainsKey("text"))
                            {
                                string? tVal = pObj["text"]?.ToString();
                                if (!string.IsNullOrEmpty(tVal))
                                {
                                    if (isThought)
                                    {
                                        accumulatedThought.AppendLine(tVal);
                                    }
                                    else
                                    {
                                        sbCandidateText.Append(tVal);
                                    }
                                }
                            }
                        }
                    }

                    string? candidateText = sbCandidateText.Length > 0 ? sbCandidateText.ToString() : null;

                    if (functionCalls.Count > 0)
                    {
                        var functionResponseParts = new JsonArray();

                        foreach (var (toolName, toolArgs) in functionCalls)
                        {
                            string toolAction = toolName == "write_file" ? "create" : toolName == "edit_file" ? "edit" : toolName == "generate_image" ? "image" : "cmd";
                            string toolTarget = ExtractStringArg(toolArgs, "path", "file_path", "filepath", "file", "filename", "output_path", "outputPath", "command", "cmd");
                            await SendSseEventAsync("tool_start", new { tool = toolName, action = toolAction, target = toolTarget, message = $"Đang thực thi: {toolName} ({toolTarget})..." });

                            _logger.LogInformation("Agent executed Tool [{Turn}/{Max}]: {ToolName}", currentTurn, maxTurns, toolName);

                            // Execute the tool on the real workspace filesystem with safety timeout (60s for CLI commands, 30s for file/image ops)
                            string toolResult;
                            try
                            {
                                int toolWaitMs = toolName == "run_command" ? 60000 : 30000;
                                var toolTask = ExecuteAgentToolAsync(projectPath, toolName, toolArgs, modifiedFiles, executedCommands, b => buildPassed = b, _imageGenerationService, _env.WebRootPath ?? "wwwroot", model.Prompt ?? "");
                                if (await Task.WhenAny(toolTask, Task.Delay(toolWaitMs)) == toolTask)
                                {
                                    toolResult = await toolTask;
                                }
                                else
                                {
                                    toolResult = $"ERROR: Thao tác thực thi công cụ '{toolName}' bị quá thời gian cho phép ({toolWaitMs / 1000}s timeout). Bỏ qua và chuyển sang bước kế tiếp.";
                                    _logger.LogWarning("Tool execution timed out after {Sec}s: {ToolName}", toolWaitMs / 1000, toolName);
                                }
                            }
                            catch (Exception exTool)
                            {
                                toolResult = $"ERROR: {exTool.Message}";
                            }

                            int lines = 0;
                            if ((toolName == "write_file" || toolName == "edit_file" || toolName == "patch_file") &&
                                TryGetSafeFilePath(projectPath, toolTarget, out string fLineCheck) && System.IO.File.Exists(fLineCheck))
                            {
                                try { lines = System.IO.File.ReadLines(fLineCheck).Count(); } catch { }
                            }
                            await SendSseEventAsync("tool_done", new { tool = toolName, action = toolAction, target = toolTarget, lines = lines, message = $"Đã xong: {toolName} ({toolTarget})" });

                            if (toolName == "run_command")
                            {
                                bool cmdSuccess = toolResult.Contains("ExitCode: 0");
                                await SendSseEventAsync("command_run", new { command = toolTarget, exitCode = cmdSuccess ? 0 : 1, output = toolResult });
                            }

                            // Record plan step and tool operation
                            if (toolName == "write_file" || toolName == "edit_file" || toolName == "patch_file")
                            {
                                string fPath = ExtractStringArg(toolArgs, "path", "file_path", "filepath", "file", "filename");
                                if (!string.IsNullOrWhiteSpace(fPath))
                                {
                                    string contentOnDisk = "";
                                    if (TryGetSafeFilePath(projectPath, fPath, out string fullSafePath) && System.IO.File.Exists(fullSafePath))
                                    {
                                        try { contentOnDisk = await System.IO.File.ReadAllTextAsync(fullSafePath, Encoding.UTF8); } catch {}
                                    }
                                    if (string.IsNullOrEmpty(contentOnDisk))
                                    {
                                        contentOnDisk = ExtractStringArg(toolArgs, "content", "code", "text", "body") ?? "";
                                    }

                                    toolOperations.Add(new
                                    {
                                        action = toolName == "write_file" ? "create" : (toolName == "patch_file" ? "patch" : "edit"),
                                        path = fPath,
                                        content = contentOnDisk,
                                        summary = $"Cập nhật tệp {fPath}",
                                        alreadyApplied = true
                                    });
                                    planSteps.Add(toolName == "write_file" ? $"Tạo tệp '{fPath}'" : $"Sửa tệp '{fPath}'");
                                }
                            }
                            else if (toolName == "grep_search")
                            {
                                string q = ExtractStringArg(toolArgs, "query", "search", "pattern");
                                planSteps.Add($"Tìm kiếm mã nguồn '{q}'");
                            }
                            else if (toolName == "generate_image")
                            {
                                string outPath = ExtractStringArg(toolArgs, "output_path", "outputPath", "path");
                                string pPrompt = ExtractStringArg(toolArgs, "prompt", "description");
                                toolOperations.Add(new
                                {
                                    action = "create",
                                    path = outPath,
                                    summary = $"Tạo hình ảnh: {pPrompt}",
                                    alreadyApplied = true
                                });
                                planSteps.Add($"Tạo hình ảnh minh họa '{outPath}'");
                            }
                            else if (toolName == "run_command")
                            {
                                string cmd = ExtractStringArg(toolArgs, "command", "cmd", "script", "exec");
                                planSteps.Add($"Thực thi lệnh '{cmd}'");
                            }
                            else if (toolName == "read_file")
                            {
                                string fPath = ExtractStringArg(toolArgs, "path", "file_path", "filepath", "file", "filename");
                                planSteps.Add($"Đọc nội dung tệp '{fPath}'");
                            }
                            else if (toolName == "list_files")
                            {
                                planSteps.Add("Khảo sát cấu trúc tệp dự án");
                            }

                            functionResponseParts.Add(new JsonObject
                            {
                                ["functionResponse"] = new JsonObject
                                {
                                    ["name"] = toolName,
                                    ["response"] = new JsonObject
                                    {
                                        ["output"] = toolResult
                                    }
                                }
                            });
                        }

                        // Append assistant message with candidate (containing all functionCalls)
                        messages.Add(candidate.DeepClone());

                        // Append matching functionResponse parts for ALL executed tools (crucial for Gemini protocol)
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = functionResponseParts
                        });
                    }
                    else
                    {
                        string cText = candidateText ?? "";
                        // If Gemini returned a plan in text/JSON without calling tools yet, FORCE IT to execute the tools!
                        if (modifiedFiles.Count == 0 && currentTurn < maxTurns && (cText.Contains("\"plan\"") || cText.Contains("Kế hoạch") || cText.Contains("Bước 1:")))
                        {
                            _logger.LogInformation("Agent returned plan text without tool call. Forcing tool execution...");
                            // Append assistant message
                            messages.Add(candidate.DeepClone());
                            // Add prompt to execute
                            messages.Add(new JsonObject
                            {
                                ["role"] = "user",
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["text"] = "Bạn đã lập kế hoạch. BÂY GIỜ BẮT BUỘC GỌI TOOL 'write_file' hoặc 'edit_file' ngay lập tức để trực tiếp tạo/sửa các tệp code trong dự án theo kế hoạch. TUYỆT ĐỐI KHÔNG xuất văn bản thuần hoặc JSON nữa, hãy gọi functionCall ngay!"
                                    }
                                }
                            });
                            continue;
                        }

                        // Check project type: compiled (.NET / .csproj) or Angular vs static web
                        hasCsproj = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly).Any();
                        bool isAngular = System.IO.File.Exists(Path.Combine(projectPath, "angular.json")) ||
                                         Directory.Exists(Path.Combine(projectPath, "src", "app"));
                        bool isDotNetOrAngular = hasCsproj || isAngular ||
                                                 Directory.Exists(Path.Combine(projectPath, "Controllers")) ||
                                                 Directory.Exists(Path.Combine(projectPath, "Views"));

                        // Self-healing: If user requested separate static web files (ONLY FOR NON-COMPILED WEB PROJECTS)
                        if (!isDotNetOrAngular)
                        {
                            string pPrompt = model.Prompt ?? "";
                            bool asksForSeparateWebFiles = pPrompt.Contains("style.css", StringComparison.OrdinalIgnoreCase) ||
                                                           pPrompt.Contains("script.js", StringComparison.OrdinalIgnoreCase) ||
                                                           pPrompt.Contains("tách thành 3 tệp", StringComparison.OrdinalIgnoreCase) ||
                                                           pPrompt.Contains("tách riêng", StringComparison.OrdinalIgnoreCase);

                            if (asksForSeparateWebFiles && currentTurn < maxTurns)
                            {
                                bool hasHtml = modifiedFiles.Any(f => f.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
                                bool hasCss = modifiedFiles.Any(f => f.EndsWith("style.css", StringComparison.OrdinalIgnoreCase));
                                bool hasJs = modifiedFiles.Any(f => f.EndsWith("script.js", StringComparison.OrdinalIgnoreCase));

                                var missing = new List<string>();
                                if (!hasHtml) missing.Add("index.html");
                                if (!hasCss) missing.Add("style.css");
                                if (!hasJs) missing.Add("script.js");

                                if (missing.Count > 0)
                                {
                                    _logger.LogInformation("Agent missed requested files: {Missing}. Forcing tool execution...", string.Join(", ", missing));
                                    messages.Add(candidate.DeepClone());
                                    messages.Add(new JsonObject
                                    {
                                        ["role"] = "user",
                                        ["parts"] = new JsonArray
                                        {
                                            new JsonObject
                                            {
                                                ["text"] = $"Người dùng yêu cầu BẮT BUỘC tách thành các tệp riêng biệt. Bạn vẫn chưa tạo: {string.Join(", ", missing)}. Hãy gọi 'write_file' ngay bây giờ để tạo các tệp còn thiếu với mã nguồn đầy đủ 100%!"
                                            }
                                        }
                                    });
                                    continue;
                                }
                            }
                        }

                        // Automated Compiler Verification & Self-Healing for .NET Projects (*.csproj)
                        if (hasCsproj && !buildPassed && currentTurn < maxTurns)
                        {
                            buildAttempts++;
                            if (buildAttempts > 2)
                            {
                                _logger.LogWarning("Reached max build self-heal attempts (2) for {Project}. Stopping loop and retaining all files.", cleanProj);
                                await SendSseEventAsync("status", new { stage = "verify_stop", message = "⚠️ Đã kiểm tra biên dịch tối đa 2 lần. Giữ nguyên toàn bộ mã nguồn." });
                                finalExplanation = $"[THÔNG BÁO BIÊN DỊCH]: Toàn bộ tệp mã nguồn đã được tạo/cập nhật thành công và được bảo toàn 100% trong dự án.";
                                break;
                            }

                            _logger.LogInformation("Verifying dotnet build automatically for project {Project} (Attempt {Attempt}/2)...", cleanProj, buildAttempts);
                            await SendSseEventAsync("status", new { stage = "verify", message = $"Đang tự động biên dịch kiểm tra mã nguồn (dotnet build - Lần {buildAttempts}/2)..." });
                            AutoFixMapStaticAssets(projectPath);

                            bool hasAssets = System.IO.File.Exists(Path.Combine(projectPath, "obj", "project.assets.json"));
                            string buildArgs = hasAssets ? "build --no-restore -p:UseSharedCompilation=false" : "build -p:UseSharedCompilation=false";
                            var (bOk, bOut) = await RunCliProcessAsync(projectPath, "dotnet", buildArgs, 25);
                            if (!bOk && (bOut.Contains("assets file") || bOut.Contains("project.assets.json") || bOut.Contains("restore")))
                            {
                                (bOk, bOut) = await RunCliProcessAsync(projectPath, "dotnet", "build -p:UseSharedCompilation=false", 35);
                            }

                            bool zeroErrors = bOk || bOut.Contains("0 Error(s)");
                            if (zeroErrors)
                            {
                                buildPassed = true;
                                _logger.LogInformation("Automated dotnet build PASSED with 0 errors for {Project}!", cleanProj);
                                await SendSseEventAsync("status", new { stage = "verify_done", message = "✅ Biên dịch thành công 100% (0 Error, Warning được bỏ qua)!" });
                            }
                            else
                            {
                                if (buildAttempts >= 2)
                                {
                                    _logger.LogWarning("Self-heal attempt limit reached after build error. Stopping loop and retaining files.");
                                    finalExplanation = $"[THÔNG BÁO BIÊN DỊCH]: Toàn bộ file đã được tạo và lưu thành công. Phát hiện lỗi compiler khi build:\n```\n{bOut}\n```\nMã nguồn được giữ nguyên 100% để bạn kiểm tra.";
                                    break;
                                }

                                _logger.LogWarning("Automated dotnet build FAILED with errors:\n{Output}", bOut);
                                await SendSseEventAsync("status", new { stage = "self_heal", message = $"Phát hiện lỗi compiler! Btoan Agent đang tự động sửa lỗi cú pháp (Lần {buildAttempts}/2)..." });
                                messages.Add(candidate.DeepClone());
                                messages.Add(new JsonObject
                                {
                                    ["role"] = "user",
                                    ["parts"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["text"] = $"[LỖI BIÊN DỊCH COMPILER 'dotnet build' THẤT BẠI - LẦN {buildAttempts}/2]:\n{bOut}\n\nYÊU CẦU TỰ ĐỘNG SỬA LỖI (SELF-HEAL):\n1. Đọc file bị lỗi bằng 'read_file' ở đúng số dòng báo lỗi (ví dụ: CS1003, CS1513, CS0246).\n2. Gọi 'patch_file' hoặc 'write_file' để sửa triệt để các lỗi cú pháp.\n3. TUYỆT ĐỐI CẤM gọi 'delete_file' hoặc xóa bất kỳ file nào vừa tạo!\n4. Bạn bắt buộc phải giữ nguyên toàn bộ file mã nguồn trong dự án."
                                        }
                                    }
                                });
                                continue;
                            }
                        }

                        // Gemini returned final explanation -> finished!
                        finalExplanation = cText;
                        break;
                    }
                }

                // Enforce 3-file web separation (index.html, style.css, script.js)
                EnforceWebSeparation(projectPath, modifiedFiles);

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

                // 2. Lưu tin nhắn trợ lý và kế hoạch/hành động vào SQLite bền vững
                try
                {
                    var assistantChatMsg = new Models.AgentChatMessage
                    {
                        UserId = userId,
                        ProjectName = cleanProj,
                        Role = "assistant",
                        Content = finalExplanation,
                        ThoughtText = accumulatedThought.Length > 0 ? accumulatedThought.ToString() : (finalExplanation ?? ""),
                        ThoughtSeconds = (int)Math.Max(1, Math.Round(swAgent.Elapsed.TotalSeconds)),
                        PlanJson = JsonSerializer.Serialize(planSteps),
                        FilePillsJson = JsonSerializer.Serialize(modifiedFiles),
                        ToolActionsJson = JsonSerializer.Serialize(toolOperations),
                        CheckpointId = checkpointId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.AgentChatMessages.Add(assistantChatMsg);
                    await _dbContext.SaveChangesAsync();
                    AppendProjectChatJson(projectPath, new
                    {
                        id = assistantChatMsg.Id,
                        role = "assistant",
                        content = assistantChatMsg.Content,
                        thoughtText = assistantChatMsg.ThoughtText,
                        thoughtSeconds = assistantChatMsg.ThoughtSeconds,
                        planJson = assistantChatMsg.PlanJson,
                        filePillsJson = assistantChatMsg.FilePillsJson,
                        toolActionsJson = assistantChatMsg.ToolActionsJson,
                        checkpointId = assistantChatMsg.CheckpointId,
                        createdAt = assistantChatMsg.CreatedAt
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist assistant agent chat message");
                }

                if (isStream)
                {
                    await SendSseEventAsync("done", new
                    {
                        ok = true,
                        projectName = cleanProj,
                        plan = planSteps,
                        explanation = finalExplanation,
                        modifiedFiles = modifiedFiles,
                        operations = toolOperations,
                        buildPassed = buildPassed,
                        executedCommands = executedCommands,
                        checkpointId = checkpointId,
                        alreadyApplied = true
                    });
                    return;
                }
                else
                {
                    Response.ContentType = "application/json; charset=utf-8";
                    await Response.WriteAsJsonAsync(new
                    {
                        ok = true,
                        projectName = cleanProj,
                        plan = planSteps,
                        explanation = finalExplanation,
                        modifiedFiles = modifiedFiles,
                        operations = toolOperations,
                        buildPassed = buildPassed,
                        executedCommands = executedCommands,
                        checkpointId = checkpointId,
                        alreadyApplied = true
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent chat execution error");
                string errorMsg = $"Lỗi xử lý Agent: {ex.Message}";
                if (isStream)
                {
                    await SendSseEventAsync("error", new { ok = false, type = "error", message = errorMsg, error = errorMsg });
                }
                else
                {
                    Response.StatusCode = 500;
                    await Response.WriteAsJsonAsync(new { ok = false, error = errorMsg });
                }

                // Lưu bản ghi Role = assistant chứa thông báo lỗi vào SQLite AgentChatMessages
                try
                {
                    var errorChatMsg = new Models.AgentChatMessage
                    {
                        UserId = userId,
                        ProjectName = cleanProj,
                        Role = "assistant",
                        Content = $"❌ {errorMsg}",
                        CheckpointId = checkpointId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.AgentChatMessages.Add(errorChatMsg);
                    await _dbContext.SaveChangesAsync();

                    AppendProjectChatJson(projectPath, new
                    {
                        id = errorChatMsg.Id,
                        role = "assistant",
                        content = errorChatMsg.Content,
                        checkpointId = errorChatMsg.CheckpointId,
                        createdAt = errorChatMsg.CreatedAt
                    });
                }
                catch (Exception dbEx)
                {
                    _logger.LogWarning(dbEx, "Failed to persist error assistant chat message");
                }
            }
            finally
            {
                keepaliveCts.Cancel();
                try { await keepaliveTask; } catch { }
                sseLock.Dispose();
            }
        }

        private static string ExtractStringArg(JsonObject? args, params string[] candidateKeys)
        {
            if (args == null) return string.Empty;
            foreach (var key in candidateKeys)
            {
                if (args.TryGetPropertyValue(key, out var node) && node != null)
                {
                    try
                    {
                        if (node is JsonValue jVal && jVal.TryGetValue<string>(out var strVal))
                        {
                            return strVal ?? string.Empty;
                        }
                        return node.ToString();
                    }
                    catch
                    {
                        return node.ToString();
                    }
                }
            }
            return string.Empty;
        }

        private static async Task<string> ExecuteAgentToolAsync(
            string projectPath, 
            string toolName, 
            JsonObject? args, 
            List<string> modifiedFiles, 
            List<string> executedCommands, 
            Action<bool> setBuildPassed,
            IImageGenerationService? imageGenService = null,
            string? webRootPath = null,
            string userPrompt = "")
        {
            try
            {
                switch (toolName)
                {
                    case "list_dir":
                    case "list_files":
                    {
                        string dir = ExtractStringArg(args, "directory", "dir", "path");
                        if (string.IsNullOrWhiteSpace(dir)) dir = ".";
                        string targetDir = Path.GetFullPath(Path.Combine(projectPath, dir.TrimStart('/', '\\')));
                        if (!targetDir.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "ERROR: Không được phép truy cập ra ngoài thư mục dự án.";
                        }
                        if (!Directory.Exists(targetDir)) return $"Directory '{dir}' does not exist.";
                        var entries = Directory.GetFileSystemEntries(targetDir, "*", SearchOption.AllDirectories)
                            .Where(e => !e.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                        !e.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                        !e.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) &&
                                        !e.Contains(Path.DirectorySeparatorChar + ".btoan_checkpoints" + Path.DirectorySeparatorChar) &&
                                        !e.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar))
                            .Select(e => {
                                string rel = Path.GetRelativePath(projectPath, e).Replace('\\', '/');
                                if (Directory.Exists(e)) return $"{rel}/";
                                var fi = new FileInfo(e);
                                int lineCount = 0;
                                try { lineCount = System.IO.File.ReadLines(e).Count(); } catch { }
                                return $"{rel} ({fi.Length} bytes, {lineCount} lines)";
                            })
                            .Take(120);
                        return string.Join("\n", entries);
                    }
                    case "grep_search":
                    {
                        string query = ExtractStringArg(args, "query", "search", "pattern", "term");
                        if (string.IsNullOrWhiteSpace(query)) return "ERROR: Tham số 'query' không được để trống khi gọi grep_search.";
                        string includePattern = ExtractStringArg(args, "include_pattern", "pattern", "glob");
                        if (string.IsNullOrWhiteSpace(includePattern)) includePattern = "*.*";

                        var matches = new List<string>();
                        var searchFiles = Directory.GetFiles(projectPath, includePattern, SearchOption.AllDirectories)
                            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                        !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) &&
                                        !f.Contains(Path.DirectorySeparatorChar + ".btoan_checkpoints" + Path.DirectorySeparatorChar) &&
                                        !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar) &&
                                        !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && 
                                        !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && 
                                        !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && 
                                        !f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && 
                                        !f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                            .Take(150);

                        foreach (var f in searchFiles)
                        {
                            string rel = Path.GetRelativePath(projectPath, f).Replace('\\', '/');
                            try
                            {
                                var lines = System.IO.File.ReadAllLines(f);
                                for (int i = 0; i < lines.Length; i++)
                                {
                                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                                    {
                                        matches.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                                        if (matches.Count >= 50) break;
                                    }
                                }
                            }
                            catch { }
                            if (matches.Count >= 50) break;
                        }

                        if (matches.Count == 0) return $"Không tìm thấy kết quả nào khớp với '{query}'.";
                        return string.Join("\n", matches);
                    }
                    case "generate_image":
                    {
                        string prompt = ExtractStringArg(args, "prompt", "description", "image_prompt");
                        string outputPath = ExtractStringArg(args, "output_path", "outputPath", "path", "filename");
                        string aspectRatio = ExtractStringArg(args, "aspect_ratio", "aspectRatio", "ratio");
                        string projName = Path.GetFileName(projectPath);
                        outputPath = NormalizeWorkspaceRelativePath(outputPath, projName);
                        if (string.IsNullOrWhiteSpace(outputPath))
                        {
                            outputPath = "images/image_" + Guid.NewGuid().ToString("N")[..8] + ".jpg";
                        }
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, outputPath));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "ERROR: Đường dẫn lưu ảnh phải nằm trong thư mục dự án.";
                        }
                        var dir = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                        int existingImgCount = modifiedFiles.Count(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || 
                                                                       f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || 
                                                                       f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                        if (existingImgCount >= 2)
                        {
                            return "Đã có đủ 2 hình ảnh minh họa trong dự án. BÂY GIỜ BẮT BUỘC PHẢI GỌI 'write_file' ĐỂ TẠO CÁC TỆP MÃ NGUỒN (HTML, CSS, JS hoặc C#/Views) NGAY!";
                        }

                        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        string format = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".svg" ? "image/svg+xml" : "image/jpeg";
                        bool generated = false;

                        // High-Speed Real Photography Fetcher (Unsplash HD / Picsum) - 300ms, zero token burn
                        try
                        {
                                string outLower = (outputPath ?? "").ToLowerInvariant();
                                string pLower = (prompt ?? "").ToLowerInvariant();

                                bool isBanner = outLower.Contains("banner") || outLower.Contains("hero") || outLower.Contains("cover") ||
                                                outLower.Contains("header") || outLower.Contains("bg") || outLower.Contains("background") ||
                                                aspectRatio == "16:9" || pLower.Contains("banner") || pLower.Contains("hero banner");

                                bool isAvatar = !isBanner && (outLower.Contains("avatar") || outLower.Contains("emp") || outLower.Contains("user") ||
                                                outLower.Contains("profile") || outLower.Contains("chân dung") || pLower.Contains("avatar") ||
                                                pLower.Contains("thẻ nhân viên") || pLower.Contains("ảnh đại diện"));

                                string photoUrl;
                                if (isBanner)
                                {
                                    var bannerPool = new[]
                                    {
                                        "https://images.unsplash.com/photo-1522071820081-009f0129c71c?auto=format&fit=crop&w=1200&h=500&q=80",
                                        "https://images.unsplash.com/photo-1497366216548-37526070297c?auto=format&fit=crop&w=1200&h=500&q=80",
                                        "https://images.unsplash.com/photo-1486406146926-c627a92ad1ab?auto=format&fit=crop&w=1200&h=500&q=80",
                                        "https://images.unsplash.com/photo-1460925895917-afdab827c52f?auto=format&fit=crop&w=1200&h=500&q=80",
                                        "https://images.unsplash.com/photo-1542744173-8e7e53415bb0?auto=format&fit=crop&w=1200&h=500&q=80"
                                    };
                                    int bIdx = Math.Abs((prompt + outputPath).GetHashCode()) % bannerPool.Length;
                                    photoUrl = bannerPool[bIdx];
                                }
                                else if (isAvatar)
                                {
                                    bool isFemale = outLower.Contains("female") || outLower.Contains("woman") || outLower.Contains("girl") ||
                                                    outLower.Contains("2") || pLower.Contains("nữ") || pLower.Contains("bình") || pLower.Contains("hoa") || pLower.Contains("lan");

                                    var femaleAvatars = new[]
                                    {
                                        "https://images.unsplash.com/photo-1573496359142-b8d87734a5a2?auto=format&fit=crop&w=600&h=600&q=80",
                                        "https://images.unsplash.com/photo-1580489944761-15a19d654956?auto=format&fit=crop&w=600&h=600&q=80",
                                        "https://images.unsplash.com/photo-1534528741775-53994a69daeb?auto=format&fit=crop&w=600&h=600&q=80"
                                    };
                                    var maleAvatars = new[]
                                    {
                                        "https://images.unsplash.com/photo-1560250097-0b93528c311a?auto=format&fit=crop&w=600&h=600&q=80",
                                        "https://images.unsplash.com/photo-1519085360753-af0119f7cbe7?auto=format&fit=crop&w=600&h=600&q=80",
                                        "https://images.unsplash.com/photo-1507003211169-0a1dd7228f2d?auto=format&fit=crop&w=600&h=600&q=80"
                                    };
                                    var list = isFemale ? femaleAvatars : maleAvatars;
                                    int aIdx = Math.Abs((prompt + outputPath).GetHashCode()) % list.Length;
                                    photoUrl = list[aIdx];
                                }
                                else
                                {
                                    int w = aspectRatio == "1:1" ? 600 : aspectRatio == "4:3" ? 800 : 800;
                                    int h = aspectRatio == "1:1" ? 600 : aspectRatio == "4:3" ? 600 : 500;
                                    photoUrl = $"https://picsum.photos/{w}/{h}?random={Random.Shared.Next(1, 99999)}";
                                }

                                using var httpCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                                using var hReq = new HttpRequestMessage(HttpMethod.Get, photoUrl);
                                hReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                                using var hRes = await new HttpClient().SendAsync(hReq, httpCts.Token);
                                if (hRes.IsSuccessStatusCode)
                                {
                                    byte[] bytes = await hRes.Content.ReadAsByteArrayAsync();
                                    if (bytes.Length > 1000)
                                    {
                                        await System.IO.File.WriteAllBytesAsync(fullPath, bytes);
                                        generated = true;
                                    }
                                }
                            }
                            catch { }

                        if (!generated)
                        {
                            if (ext == ".svg")
                            {
                                string svg = GenerateSvgPlaceholder(prompt ?? "", outputPath ?? "");
                                await System.IO.File.WriteAllTextAsync(fullPath, svg, Encoding.UTF8);
                            }
                            else
                            {
                                GenerateArtisticBinaryImage(fullPath, prompt ?? "", outputPath ?? "", aspectRatio);
                            }
                        }

                        string relPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                        if (!modifiedFiles.Contains(relPath)) modifiedFiles.Add(relPath);
                        return JsonSerializer.Serialize(new
                        {
                            status = "success",
                            saved_path = relPath,
                            format = format,
                            message = $"Đã tạo và lưu hình ảnh minh họa thật vào '{relPath}'. Bạn hãy dùng chính xác <img src=\"{relPath}\"> trong mã HTML/CSS."
                        });
                    }
                    case "read_file":
                    {
                        string path = ExtractStringArg(args, "path", "file_path", "filepath", "file", "filename");
                        if (string.IsNullOrWhiteSpace(path)) return "ERROR: Tham số 'path' không được để trống khi gọi read_file.";
                        string projName = Path.GetFileName(projectPath);
                        path = NormalizeWorkspaceRelativePath(path, projName);
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "ERROR: Không được phép truy cập ra ngoài thư mục dự án.";
                        }
                        if (!System.IO.File.Exists(fullPath)) return $"ERROR: Tệp '{path}' không tồn tại trong dự án.";
                        
                        string[] lines = await System.IO.File.ReadAllLinesAsync(fullPath, Encoding.UTF8);
                        int startLine = 1;
                        int endLine = lines.Length;
                        if (args?.ContainsKey("start_line") == true && int.TryParse(args["start_line"]?.ToString(), out int sl) && sl > 0)
                        {
                            startLine = Math.Min(sl, lines.Length);
                        }
                        if (args?.ContainsKey("end_line") == true && int.TryParse(args["end_line"]?.ToString(), out int el) && el >= startLine)
                        {
                            endLine = Math.Min(el, lines.Length);
                        }
                        var sb = new StringBuilder();
                        for (int i = startLine; i <= endLine; i++)
                        {
                            sb.AppendLine($"{i}: {lines[i - 1]}");
                        }
                        return sb.ToString();
                    }
                    case "write_file":
                    {
                        string path = ExtractStringArg(args, "path", "file_path", "filepath", "file", "filename", "name");
                        string content = ExtractStringArg(args, "content", "code", "new_content", "file_content", "body", "text");
                        if (string.IsNullOrWhiteSpace(path)) return "ERROR: Tham số 'path' không được để trống khi gọi write_file.";

                        // Chặn tuyệt đối ghi file rỗng 0 byte & Bắt AI truyền source code đầy đủ
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            return $"ERROR: Tham số 'content' cho tệp '{path}' đang bị trống (0 byte). Bạn BẮT BUỘC phải truyền toàn bộ mã nguồn hoàn chỉnh vào tham số 'content' khi gọi write_file. Tuyệt đối không tạo file rỗng.";
                        }

                        string projName = Path.GetFileName(projectPath);
                        path = NormalizeWorkspaceRelativePath(path, projName);
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "ERROR: Không được phép truy cập ra ngoài thư mục dự án.";
                        }
                        string dir = Path.GetDirectoryName(fullPath)!;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        await System.IO.File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
                        string relPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                        if (!modifiedFiles.Contains(relPath)) modifiedFiles.Add(relPath);
                        return $"Successfully written file: {relPath} ({content.Length} ký tự / {Encoding.UTF8.GetByteCount(content)} bytes).";
                    }
                    case "patch_file":
                    {
                        string path = ExtractStringArg(args, "path", "file_path", "filepath", "file", "filename");
                        string targetBlock = ExtractStringArg(args, "target_block", "target", "old_content", "search", "existing_code");
                        string replacementBlock = ExtractStringArg(args, "replacement_block", "replacement", "new_content", "replace", "code", "new_code");

                        if (string.IsNullOrWhiteSpace(path)) return "ERROR: Tham số 'path' không được để trống khi gọi patch_file.";
                        if (string.IsNullOrEmpty(targetBlock)) return "ERROR: Tham số 'target_block' không được để trống.";

                        string projName = Path.GetFileName(projectPath);
                        path = NormalizeWorkspaceRelativePath(path, projName);
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "ERROR: Không được phép truy cập ra ngoài thư mục dự án.";
                        }
                        if (!System.IO.File.Exists(fullPath)) return $"ERROR: Tệp '{path}' không tồn tại. Dùng write_file nếu muốn tạo mới.";
                        string existing = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);

                        string targetNorm = targetBlock.Replace("\r\n", "\n");
                        string existingNorm = existing.Replace("\r\n", "\n");

                        if (!existingNorm.Contains(targetNorm))
                        {
                            string trimmed = targetNorm.Trim();
                            if (existingNorm.Contains(trimmed))
                            {
                                targetNorm = trimmed;
                            }
                            else
                            {
                                return $"ERROR: Không tìm thấy target_block trong '{path}'. Hãy gọi read_file để xem chính xác mã nguồn hiện tại.";
                            }
                        }

                        string replacementNorm = (replacementBlock ?? "").Replace("\r\n", "\n");
                        string patched = existingNorm.Replace(targetNorm, replacementNorm);
                        if (string.IsNullOrWhiteSpace(patched))
                        {
                            return $"ERROR: Thao tác patch_file làm tệp '{path}' trở thành rỗng (0 byte). Thao tác bị từ chối.";
                        }

                        await System.IO.File.WriteAllTextAsync(fullPath, patched, Encoding.UTF8);
                        string relPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                        if (!modifiedFiles.Contains(relPath)) modifiedFiles.Add(relPath);
                        return $"Successfully patched file: {relPath}";
                    }
                    case "edit_file":
                    {
                        string path = ExtractStringArg(args, "path", "file_path", "filepath", "file", "filename");
                        string target = ExtractStringArg(args, "target", "old_content", "search", "existing_code", "find");
                        string replacement = ExtractStringArg(args, "replacement", "new_content", "replace", "code", "new_code");

                        if (string.IsNullOrWhiteSpace(path)) return "ERROR: Tham số 'path' không được để trống khi gọi edit_file.";
                        if (string.IsNullOrEmpty(target)) return "ERROR: Tham số 'target' không được để trống khi gọi edit_file. Nếu muốn ghi đè toàn bộ tệp, hãy sử dụng write_file.";

                        string projName = Path.GetFileName(projectPath);
                        path = NormalizeWorkspaceRelativePath(path, projName);
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "ERROR: Không được phép truy cập ra ngoài thư mục dự án.";
                        }
                        if (!System.IO.File.Exists(fullPath)) return $"ERROR: Tệp '{path}' không tồn tại. Nếu bạn muốn tạo mới tệp này, hãy dùng write_file.";
                        string existing = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                        if (!existing.Contains(target))
                        {
                            string trimmed = target.Trim();
                            if (existing.Contains(trimmed))
                            {
                                string updated = existing.Replace(trimmed, replacement);
                                if (string.IsNullOrWhiteSpace(updated))
                                {
                                    return $"ERROR: Thao tác edit_file làm tệp '{path}' trở thành rỗng (0 byte). Thao tác bị từ chối. Hãy cung cấp replacement với code hoàn chỉnh.";
                                }
                                await System.IO.File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8);
                                string rPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                                if (!modifiedFiles.Contains(rPath)) modifiedFiles.Add(rPath);
                                return $"Successfully edited file: {rPath}";
                            }
                            return $"ERROR: Không tìm thấy đoạn target trong '{path}'. Hãy dùng read_file để xem chính xác mã nguồn hiện tại, hoặc dùng write_file để ghi đè toàn bộ nội dung hoàn chỉnh của tệp.";
                        }
                        string newContent = existing.Replace(target, replacement);
                        if (string.IsNullOrWhiteSpace(newContent))
                        {
                            return $"ERROR: Thao tác edit_file làm tệp '{path}' trở thành rỗng (0 byte). Thao tác bị từ chối. Hãy cung cấp replacement với code hoàn chỉnh.";
                        }
                        await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8);
                        string relPath = Path.GetRelativePath(projectPath, fullPath).Replace('\\', '/');
                        if (!modifiedFiles.Contains(relPath)) modifiedFiles.Add(relPath);
                        return $"Successfully edited file: {relPath}";
                    }
                    case "delete_file":
                    {
                        string path = ExtractStringArg(args, "path", "file_path", "filepath", "file", "filename");
                        if (string.IsNullOrWhiteSpace(path)) return "ERROR: Tham số 'path' không được để trống.";

                        // Check if user explicitly asked for deletion in prompt
                        bool explicitDelete = !string.IsNullOrWhiteSpace(userPrompt) && 
                            (userPrompt.Contains("xóa", StringComparison.OrdinalIgnoreCase) || 
                             userPrompt.Contains("xoa", StringComparison.OrdinalIgnoreCase) || 
                             userPrompt.Contains("delete", StringComparison.OrdinalIgnoreCase) || 
                             userPrompt.Contains("remove", StringComparison.OrdinalIgnoreCase));

                        if (!explicitDelete)
                        {
                            return $"ERROR: Tuyệt đối CẤM tự ý xóa file '{path}' khi tự sửa lỗi hoặc biên dịch thất bại! Bạn BẮT BUỘC phải giữ nguyên toàn bộ file đã tạo/sửa và chỉ dùng read_file, patch_file để khắc phục lỗi cú pháp.";
                        }

                        string projName = Path.GetFileName(projectPath);
                        path = NormalizeWorkspaceRelativePath(path, projName);
                        string fullPath = Path.GetFullPath(Path.Combine(projectPath, path));
                        if (!fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return "ERROR: Không được phép truy cập ra ngoài thư mục dự án.";
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
                        string command = ExtractStringArg(args, "command", "cmd", "script", "exec");
                        if (string.IsNullOrWhiteSpace(command)) return "ERROR: Tham số 'command' không được để trống.";
                        
                        string lowerCmd = command.ToLowerInvariant();
                        if (lowerCmd.Contains("rm -rf /") || lowerCmd.Contains(":(){ :|:& };:") || lowerCmd.Contains("shutdown") || lowerCmd.Contains("reboot") || lowerCmd.Contains("mkfs") || lowerCmd.Contains("dd if="))
                        {
                            return "ERROR: Câu lệnh bị từ chối vì lý do an toàn hệ thống.";
                        }

                        executedCommands.Add(command);
                        AutoFixMapStaticAssets(projectPath);
                        int timeoutSec = 60;
                        if (args?.ContainsKey("timeout_seconds") == true && int.TryParse(args["timeout_seconds"]?.ToString(), out int tSec) && tSec > 0)
                        {
                            timeoutSec = Math.Min(tSec, 180);
                        }

                        if (lowerCmd.Contains("dotnet restore"))
                        {
                            timeoutSec = Math.Min(timeoutSec, 30);
                            if (!command.Contains("--disable-parallel", StringComparison.OrdinalIgnoreCase))
                            {
                                command += " --disable-parallel --nologo";
                            }
                        }
                        else if (lowerCmd.Contains("dotnet build"))
                        {
                            timeoutSec = Math.Min(timeoutSec, 25);
                            bool hasAssets = System.IO.File.Exists(Path.Combine(projectPath, "obj", "project.assets.json"));
                            if (hasAssets && !command.Contains("--no-restore", StringComparison.OrdinalIgnoreCase))
                            {
                                command += " --no-restore";
                            }
                            if (!command.Contains("UseSharedCompilation", StringComparison.OrdinalIgnoreCase))
                            {
                                command += " --nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false -maxCpuCount:1";
                            }
                        }

                        var (ok, output) = await RunCliProcessAsync(projectPath, command, "", timeoutSec);
                        if (command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase))
                        {
                            bool zeroErrors = ok || output.Contains("0 Error(s)");
                            if (zeroErrors)
                            {
                                setBuildPassed(true);
                                return $"ExitCode: 0\nOutput:\n{output}\n\n[SYSTEM NOTICE: dotnet build SUCCEEDED with 0 Errors (Warnings are ignored)! All requested files and build verification are complete. DO NOT call any more tools. Provide your final explanation in natural Vietnamese now.]";
                            }
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

        private static string GenerateSvgPlaceholder(string prompt, string filename)
        {
            string title = System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(filename).Replace('-', ' ').Replace('_', ' '));
            string sub = System.Net.WebUtility.HtmlEncode(prompt.Length > 60 ? prompt[..57] + "..." : prompt);
            return $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 800 500"" width=""100%"" height=""100%"">
  <defs>
    <linearGradient id=""grad"" x1=""0%"" y1=""0%"" x2=""100%"" y2=""100%"">
      <stop offset=""0%"" style=""stop-color:#1e293b;stop-opacity:1"" />
      <stop offset=""100%"" style=""stop-color:#0f172a;stop-opacity:1"" />
    </linearGradient>
  </defs>
  <rect width=""100%"" height=""100%"" fill=""url(#grad)"" />
  <circle cx=""400"" cy=""200"" r=""70"" fill=""rgba(59,130,246,0.2)"" />
  <path d=""M370 220 L395 185 L415 210 L430 195 L450 220 Z"" fill=""#38bdf8"" />
  <circle cx=""385"" cy=""180"" r=""8"" fill=""#38bdf8"" />
  <text x=""400"" y=""310"" font-family=""system-ui, sans-serif"" font-size=""26"" font-weight=""bold"" fill=""#f8fafc"" text-anchor=""middle"">{title}</text>
  <text x=""400"" y=""350"" font-family=""system-ui, sans-serif"" font-size=""16"" fill=""#94a3b8"" text-anchor=""middle"">{sub}</text>
</svg>";
        }

        private static void GenerateArtisticBinaryImage(string fullPath, string prompt, string filename, string? aspectRatio)
        {
            try
            {
                int width = 800;
                int height = 500;
                if (aspectRatio == "1:1") { width = 600; height = 600; }
                else if (aspectRatio == "4:3") { width = 800; height = 600; }
                else if (aspectRatio == "9:16") { width = 450; height = 800; }

                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;

                string pLower = ((prompt ?? "") + " " + (filename ?? "")).ToLowerInvariant();
                SKColor color1, color2, accent;

                if (pLower.Contains("tỏ tình") || pLower.Contains("love") || pLower.Contains("heart") || pLower.Contains("yêu") || pLower.Contains("romance") || pLower.Contains("lãng mạn") || pLower.Contains("couple"))
                {
                    color1 = new SKColor(244, 63, 94);  // #f43f5e Rose
                    color2 = new SKColor(139, 92, 246); // #8b5cf6 Violet
                    accent = new SKColor(251, 113, 133); // #fb7185
                }
                else if (pLower.Contains("shop") || pLower.Contains("cafe") || pLower.Contains("coffee") || pLower.Contains("food") || pLower.Contains("bán hàng"))
                {
                    color1 = new SKColor(245, 158, 11); // #f59e0b Amber
                    color2 = new SKColor(120, 53, 15);  // #78350f Dark Amber
                    accent = new SKColor(251, 191, 36);
                }
                else if (pLower.Contains("tech") || pLower.Contains("code") || pLower.Contains("dark") || pLower.Contains("cyber") || pLower.Contains("dashboard"))
                {
                    color1 = new SKColor(15, 23, 42);   // #0f172a
                    color2 = new SKColor(30, 41, 59);   // #1e293b
                    accent = new SKColor(56, 189, 248); // #38bdf8
                }
                else
                {
                    color1 = new SKColor(99, 102, 241); // #6366f1 Indigo
                    color2 = new SKColor(14, 165, 233); // #0ea5e9 Cyan
                    accent = new SKColor(129, 140, 248);
                }

                // 1. Draw smooth gradient background
                using (var paint = new SKPaint())
                {
                    paint.Shader = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0),
                        new SKPoint(width, height),
                        new[] { color1, color2 },
                        SKShaderTileMode.Clamp);
                    canvas.DrawRect(0, 0, width, height, paint);
                }

                // 2. Draw aesthetic ambient radial glow
                using (var glowPaint = new SKPaint
                {
                    Color = accent.WithAlpha(60),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                })
                {
                    canvas.DrawCircle(width / 2f, height / 2f - 20, Math.Min(width, height) / 3f, glowPaint);
                }

                // 3. Draw subtle inner frame card
                using (var strokePaint = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, 45),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.5f
                })
                {
                    canvas.DrawRoundRect(new SKRoundRect(new SKRect(width * 0.08f, height * 0.08f, width * 0.92f, height * 0.92f), 20, 20), strokePaint);
                }

                // 4. If avatar/person, draw a stylized avatar silhouette so it looks like a real avatar
                if (pLower.Contains("avatar") || pLower.Contains("portrait") || pLower.Contains("nhân viên") || pLower.Contains("person") || pLower.Contains("user") || pLower.Contains("chân dung"))
                {
                    using var avatarPaint = new SKPaint
                    {
                        Color = new SKColor(255, 255, 255, 140),
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill
                    };
                    float cx = width / 2f;
                    float cy = height * 0.40f;
                    float headRadius = Math.Min(width, height) * 0.17f;
                    canvas.DrawCircle(cx, cy, headRadius, avatarPaint);

                    float bodyTop = cy + headRadius * 1.25f;
                    float bodyWidth = headRadius * 2.6f;
                    canvas.DrawRoundRect(new SKRoundRect(new SKRect(cx - bodyWidth / 2f, bodyTop, cx + bodyWidth / 2f, height * 0.88f), 24, 24), avatarPaint);
                }

                // 4. Encode to 100% valid binary image
                using var image = surface.Snapshot();
                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                var format = ext switch
                {
                    ".png" => SKEncodedImageFormat.Png,
                    ".webp" => SKEncodedImageFormat.Webp,
                    _ => SKEncodedImageFormat.Jpeg
                };

                using var data = image.Encode(format, 92);
                using var fileStream = System.IO.File.Create(fullPath);
                data.SaveTo(fileStream);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GenerateArtisticBinaryImage error: {ex.Message}");
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
                if (!string.IsNullOrWhiteSpace(project))
                {
                    string cleanProj = SanitizeProjectName(project);
                    string projectPath = ResolveWorkspacePath(cleanProj);
                    AutoFixMapStaticAssets(projectPath);
                }

                int port = RunningAppSessionManager.AllocatePort(CurrentUserId, project ?? "app");
                return Ok(new { ok = true, port });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        // POST /api/ide/run-project
        [HttpPost("run-project")]
        public async Task<IActionResult> RunProjectEndpoint([FromBody] RunProjectRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Project))
            {
                return BadRequest(new { ok = false, error = "Project name is required" });
            }

            int userId = CurrentUserId > 0 ? CurrentUserId : 1;
            string cleanProj = SanitizeProjectName(model.Project);
            string projectPath = ResolveWorkspacePath(cleanProj);
            if (!Directory.Exists(projectPath))
            {
                return NotFound(new { ok = false, error = $"Không tìm thấy thư mục dự án '{cleanProj}'." });
            }

            // 1. Dọn dẹp mọi tiến trình cũ đang chạy từ dự án này để giải phóng hoàn toàn khóa file .dll và .db
            await RunningAppSessionManager.KillProjectProcessesAsync(cleanProj, userId);

            // Auto-fix settings and assets
            AutoFixMapStaticAssets(projectPath);

            // 2. Chạy dotnet build trước tuần tự (không khóa nodeReuse, không dùng Roslyn shared socket để tránh deadlock)
            var buildRes = await RunCliProcessAsync(projectPath, "dotnet", "build --nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false -maxCpuCount:1", 90);
            if (!buildRes.ok)
            {
                return Ok(new
                {
                    ok = false,
                    stage = "build",
                    error = buildRes.output,
                    message = "Biên dịch dự án thất bại. Vui lòng kiểm tra thông báo lỗi bên dưới."
                });
            }

            // 3. Cấp phát cổng động (5200..5299)
            int port = RunningAppSessionManager.AllocatePort(userId, cleanProj);

            // 4. Khởi chạy ứng dụng trực tiếp từ .dll (hoặc dotnet run) ở chế độ nền
            try
            {
                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = projectPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                string dllPath = Path.Combine(projectPath, "bin", "Debug", "net10.0", $"{cleanProj}.dll");
                if (!System.IO.File.Exists(dllPath))
                {
                    var binDir = Path.Combine(projectPath, "bin");
                    if (Directory.Exists(binDir))
                    {
                        var dlls = Directory.GetFiles(binDir, "*.dll", SearchOption.AllDirectories)
                            .Where(f => !f.Contains("ref" + Path.DirectorySeparatorChar))
                            .ToList();
                        dllPath = dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d) == cleanProj) ?? dlls.FirstOrDefault() ?? "";
                    }
                }

                psi.FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
                if (System.IO.File.Exists(dllPath))
                {
                    psi.Arguments = $"\"{dllPath}\" --urls http://127.0.0.1:{port}";
                }
                else
                {
                    psi.Arguments = $"run --no-build --no-launch-profile --urls http://127.0.0.1:{port}";
                }

                psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
                psi.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true";
                psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
                psi.Environment["PORT"] = port.ToString();
                psi.Environment["DOTNET_NOLOGO"] = "1";
                psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
                psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                psi.Environment["UseSharedCompilation"] = "false";

                if (!OperatingSystem.IsWindows())
                {
                    psi.Environment["DOTNET_ROOT"] = "/usr/share/dotnet";
                    string curPath = psi.Environment.ContainsKey("PATH") ? (psi.Environment["PATH"] ?? "") : (Environment.GetEnvironmentVariable("PATH") ?? "");
                    if (!curPath.Contains("/usr/share/dotnet"))
                    {
                        psi.Environment["PATH"] = $"/usr/share/dotnet:{curPath}";
                    }
                }

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    var appLogs = new StringBuilder();
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) appLogs.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) appLogs.AppendLine(e.Data); };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    RunningAppSessionManager.RegisterActiveApp(port, userId, cleanProj, proc.Id);

                    // Chờ và kiểm tra xem ứng dụng đã thực sự bind cổng thành công chưa
                    using var hc = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
                    bool bound = false;
                    for (int i = 0; i < 20; i++)
                    {
                        if (proc.HasExited) break;
                        try
                        {
                            var resp = await hc.GetAsync($"http://127.0.0.1:{port}/");
                            if (resp.StatusCode != 0) { bound = true; break; }
                        }
                        catch { }
                        await Task.Delay(250);
                    }

                    if (proc.HasExited && !bound)
                    {
                        return Ok(new
                        {
                            ok = false,
                            stage = "start_failed",
                            error = $"Tiến trình ứng dụng đã thoát đột ngột (Exit Code: {proc.ExitCode}):\n{appLogs}",
                            message = "Không thể khởi chạy web server. Vui lòng kiểm tra log lỗi."
                        });
                    }

                    return Ok(new
                    {
                        ok = true,
                        port = port,
                        url = $"http://127.0.0.1:{port}",
                        stage = "running",
                        message = $"Ứng dụng đã khởi chạy thành công trên cổng :{port}."
                    });
                }

                return StatusCode(500, new { ok = false, error = "Không thể khởi chạy tiến trình web server." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }
    }

    // ==========================================
    // MODELS
    // ==========================================

    public class RunProjectRequestModel
    {
        public string? Project { get; set; }
    }

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
        public string? File { set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(Path)) Path = value; } }
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

    public class RunCommandRequestModel
    {
        public string? Project { get; set; }
        public string? Command { get; set; }
        public int? UserId { get; set; }
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
        public bool Stream { get; set; } = true;
        public string? ImageBase64 { get; set; }
        public string? ImageMimeType { get; set; }
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

    public class StopPortRequestModel
    {
        public int Port { get; set; }
    }
}
