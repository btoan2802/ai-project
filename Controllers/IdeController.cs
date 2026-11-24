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

                return 0;
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
                if (!System.IO.File.Exists(propsPath))
                {
                    string content = @"<Project>
  <PropertyGroup>
    <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn>
  </PropertyGroup>
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
        public IActionResult CreateProject([FromBody] IdeCreateProjectRequestModel model)
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

                // Scaffolding templates
                if (string.Equals(model?.Template, "dotnet-mvc", StringComparison.OrdinalIgnoreCase))
                {
                    GenerateMvcStarterTemplate(projectPath, cleanName);
                }
                else if (string.Equals(model?.Template, "dotnet-webapi", StringComparison.OrdinalIgnoreCase))
                {
                    GenerateWebApiStarterTemplate(projectPath, cleanName);
                }
                else if (string.Equals(model?.Template, "angular", StringComparison.OrdinalIgnoreCase))
                {
                    GenerateAngularStarterTemplate(projectPath, cleanName);
                }
                else if (string.Equals(model?.Template, "fullstack", StringComparison.OrdinalIgnoreCase))
                {
                    string bePath = Path.Combine(projectPath, "backend");
                    string fePath = Path.Combine(projectPath, "frontend");
                    Directory.CreateDirectory(bePath);
                    Directory.CreateDirectory(fePath);
                    GenerateWebApiStarterTemplate(bePath, $"{cleanName}Api");
                    GenerateAngularStarterTemplate(fePath, $"{cleanName}Web");
                }
                else if (string.Equals(model?.Template, "html5", StringComparison.OrdinalIgnoreCase) ||
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

        private static void GenerateMvcStarterTemplate(string projectPath, string projectName)
        {
            Directory.CreateDirectory(Path.Combine(projectPath, "Controllers"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Data"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Models"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Views", "Shared"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Views", "Products"));
            Directory.CreateDirectory(Path.Combine(projectPath, "wwwroot", "css"));
            Directory.CreateDirectory(Path.Combine(projectPath, "wwwroot", "js"));

            string csproj = $@"<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Microsoft.EntityFrameworkCore.Sqlite"" Version=""10.0.0"" />
    <PackageReference Include=""Microsoft.EntityFrameworkCore.SqlServer"" Version=""10.0.0"" />
  </ItemGroup>
</Project>";
            System.IO.File.WriteAllText(Path.Combine(projectPath, $"{projectName}.csproj"), csproj, Encoding.UTF8);

            string appsettings = @"{
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information"",
      ""Microsoft.AspNetCore"": ""Warning""
    }
  },
  ""AllowedHosts"": ""*"",
  ""Database"": {
    ""Provider"": ""Sqlite"",
    ""SqliteConnection"": ""Data Source=app.db"",
    ""SqlServerConnection"": ""Server=.;Database=MyAppDb;Trusted_Connection=True;TrustServerCertificate=True;""
  }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "appsettings.json"), appsettings, Encoding.UTF8);

            string dbContext = @"using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace App.Data
{
    public class Product
    {
        public int Id { get; set; }
        [Required]
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string Category { get; set; } = ""Chung"";
        public int Stock { get; set; } = 10;
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public DbSet<Product> Products => Set<Product>();
    }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Data", "AppDbContext.cs"), dbContext, Encoding.UTF8);

            string controller = @"using App.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace App.Controllers
{
    public class ProductsController : Controller
    {
        private readonly AppDbContext _db;
        public ProductsController(AppDbContext db) => _db = db;

        public async Task<IActionResult> Index()
        {
            var items = await _db.Products.ToListAsync();
            return View(items);
        }

        [HttpPost]
        public async Task<IActionResult> Create(Product model)
        {
            if (ModelState.IsValid)
            {
                _db.Products.Add(model);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var item = await _db.Products.FindAsync(id);
            if (item != null)
            {
                _db.Products.Remove(item);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }
    }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Controllers", "ProductsController.cs"), controller, Encoding.UTF8);

            string viewStart = @"@{
    Layout = ""_Layout"";
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Views", "_ViewStart.cshtml"), viewStart, Encoding.UTF8);

            string layout = @"<!DOCTYPE html>
<html lang=""vi"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>@ViewData[""Title""] - Btoan MVC App</title>
    <link href=""https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css"" rel=""stylesheet"" />
    <link href=""https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css"" rel=""stylesheet"" />
    <style>
        body { background: #121212; color: #e0e0e0; font-family: system-ui, -apple-system, sans-serif; }
        .card { background: #1e1e1e; border: 1px solid #333; }
        .table { color: #e0e0e0; }
        .table-dark { background-color: #252525; }
        .navbar { background: #181818 !important; border-bottom: 1px solid #333; }
    </style>
</head>
<body>
    <nav class=""navbar navbar-expand-lg navbar-dark mb-4"">
        <div class=""container"">
            <a class=""navbar-brand fw-bold text-info"" href=""/""><i class=""bi bi-box-seam me-2""></i>Btoan MVC App (SQLite)</a>
            <span class=""badge bg-success""><i class=""bi bi-database me-1""></i>SQLite: app.db</span>
        </div>
    </nav>
    <div class=""container"">
        @RenderBody()
    </div>
    <script src=""https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js""></script>
</body>
</html>";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Views", "Shared", "_Layout.cshtml"), layout, Encoding.UTF8);

            string viewIndex = @"@model IEnumerable<App.Data.Product>
@{
    ViewData[""Title""] = ""Quản lý Sản phẩm"";
}

<div class=""card p-4 shadow-sm mb-4"">
    <div class=""d-flex justify-content-between align-items-center mb-3"">
        <h4 class=""mb-0 text-white""><i class=""bi bi-grid-3x3-gap-fill text-primary me-2""></i>Danh sách Sản phẩm</h4>
        <button class=""btn btn-sm btn-primary"" data-bs-toggle=""collapse"" data-bs-target=""#createForm""><i class=""bi bi-plus-lg me-1""></i>Thêm sản phẩm mới</button>
    </div>

    <div id=""createForm"" class=""collapse mb-4 p-3 bg-black rounded border border-secondary"">
        <form asp-action=""Create"" method=""post"" class=""row g-2"">
            <div class=""col-md-4"">
                <input type=""text"" name=""Name"" class=""form-control form-control-sm bg-dark text-light border-secondary"" placeholder=""Tên sản phẩm..."" required />
            </div>
            <div class=""col-md-3"">
                <input type=""number"" name=""Price"" class=""form-control form-control-sm bg-dark text-light border-secondary"" placeholder=""Giá (VNĐ)..."" required />
            </div>
            <div class=""col-md-3"">
                <input type=""text"" name=""Category"" class=""form-control form-control-sm bg-dark text-light border-secondary"" placeholder=""Danh mục..."" value=""Thiết bị"" />
            </div>
            <div class=""col-md-2"">
                <button type=""submit"" class=""btn btn-sm btn-success w-100""><i class=""bi bi-check-lg me-1""></i>Lưu</button>
            </div>
        </form>
    </div>

    <div class=""table-responsive"">
        <table class=""table table-dark table-hover table-bordered align-middle"">
            <thead>
                <tr class=""table-secondary text-dark"">
                    <th>#ID</th>
                    <th>Tên sản phẩm</th>
                    <th>Giá</th>
                    <th>Danh mục</th>
                    <th>Tồn kho</th>
                    <th style=""width: 80px;"">Thao tác</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var p in Model)
                {
                    <tr>
                        <td><strong>#@p.Id</strong></td>
                        <td class=""text-info fw-semibold"">@p.Name</td>
                        <td>@p.Price.ToString(""N0"") đ</td>
                        <td><span class=""badge bg-secondary"">@p.Category</span></td>
                        <td><span class=""badge bg-info text-dark"">@p.Stock cái</span></td>
                        <td>
                            <form asp-action=""Delete"" method=""post"" onsubmit=""return confirm('Xóa sản phẩm này?')"">
                                <input type=""hidden"" name=""id"" value=""@p.Id"" />
                                <button type=""submit"" class=""btn btn-sm btn-outline-danger py-0 px-2""><i class=""bi bi-trash""></i></button>
                            </form>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
</div>";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Views", "Products", "Index.cshtml"), viewIndex, Encoding.UTF8);

            string program = @"using App.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

string sqliteConn = builder.Configuration[""Database:SqliteConnection""] ?? ""Data Source=app.db"";
builder.Services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(sqliteConn));

var app = builder.Build();

// Auto-migrate & seed sample data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    if (!db.Products.Any())
    {
        db.Products.AddRange(
            new Product { Name = ""Laptop Dell XPS 15"", Price = 35000000, Category = ""Laptop"", Stock = 5 },
            new Product { Name = ""Bàn phím cơ Keychron Q1"", Price = 4200000, Category = ""Phụ kiện"", Stock = 12 },
            new Product { Name = ""Chuột Logitech MX Master 3S"", Price = 2400000, Category = ""Phụ kiện"", Stock = 20 },
            new Product { Name = ""Màn hình Dell UltraSharp 27 inch"", Price = 12500000, Category = ""Màn hình"", Stock = 8 },
            new Product { Name = ""Tai nghe Sony WH-1000XM5"", Price = 7800000, Category = ""Âm thanh"", Stock = 15 }
        );
        db.SaveChanges();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(""/Home/Error"");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: ""default"",
    pattern: ""{controller=Products}/{action=Index}/{id?}"");

app.Run();";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Program.cs"), program, Encoding.UTF8);
        }

        private static void GenerateWebApiStarterTemplate(string projectPath, string projectName)
        {
            Directory.CreateDirectory(Path.Combine(projectPath, "Controllers"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Data"));

            string csproj = $@"<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Microsoft.AspNetCore.OpenApi"" Version=""10.0.0"" />
    <PackageReference Include=""Microsoft.EntityFrameworkCore.Sqlite"" Version=""10.0.0"" />
    <PackageReference Include=""Microsoft.EntityFrameworkCore.SqlServer"" Version=""10.0.0"" />
  </ItemGroup>
</Project>";
            System.IO.File.WriteAllText(Path.Combine(projectPath, $"{projectName}.csproj"), csproj, Encoding.UTF8);

            string appsettings = @"{
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information"",
      ""Microsoft.AspNetCore"": ""Warning""
    }
  },
  ""AllowedHosts"": ""*"",
  ""Database"": {
    ""Provider"": ""Sqlite"",
    ""SqliteConnection"": ""Data Source=app.db"",
    ""SqlServerConnection"": ""Server=.;Database=MyAppDb;Trusted_Connection=True;TrustServerCertificate=True;""
  }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "appsettings.json"), appsettings, Encoding.UTF8);

            string dbContext = @"using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace App.Data
{
    public class TodoItem
    {
        public int Id { get; set; }
        [Required]
        public string Title { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public DbSet<TodoItem> Todos => Set<TodoItem>();
    }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Data", "AppDbContext.cs"), dbContext, Encoding.UTF8);

            string controller = @"using App.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace App.Controllers
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class TodosController : ControllerBase
    {
        private readonly AppDbContext _db;
        public TodosController(AppDbContext db) => _db = db;

        [HttpGet]
        public async Task<ActionResult<IEnumerable<TodoItem>>> GetTodos()
        {
            return await _db.Todos.OrderByDescending(t => t.Id).ToListAsync();
        }

        [HttpGet(""{id}"")]
        public async Task<ActionResult<TodoItem>> GetTodo(int id)
        {
            var item = await _db.Todos.FindAsync(id);
            if (item == null) return NotFound();
            return item;
        }

        [HttpPost]
        public async Task<ActionResult<TodoItem>> CreateTodo(TodoItem item)
        {
            _db.Todos.Add(item);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetTodo), new { id = item.Id }, item);
        }

        [HttpPut(""{id}"")]
        public async Task<IActionResult> UpdateTodo(int id, TodoItem updated)
        {
            if (id != updated.Id) return BadRequest();
            _db.Entry(updated).State = EntityState.Modified;
            await _db.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete(""{id}"")]
        public async Task<IActionResult> DeleteTodo(int id)
        {
            var item = await _db.Todos.FindAsync(id);
            if (item == null) return NotFound();
            _db.Todos.Remove(item);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Controllers", "TodosController.cs"), controller, Encoding.UTF8);

            string program = @"using App.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

string sqliteConn = builder.Configuration[""Database:SqliteConnection""] ?? ""Data Source=app.db"";
builder.Services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(sqliteConn));

var app = builder.Build();

// Auto-migrate & seed sample data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    if (!db.Todos.Any())
    {
        db.Todos.AddRange(
            new TodoItem { Title = ""Nghiên cứu kiến trúc Web IDE"", IsCompleted = true },
            new TodoItem { Title = ""Tích hợp SQLite Studio vào trình duyệt"", IsCompleted = true },
            new TodoItem { Title = ""Viết Reverse Proxy chống SSRF cho Live Preview"", IsCompleted = true },
            new TodoItem { Title = ""Triển khai test thực tế bằng Browser Agent"", IsCompleted = false }
        );
        db.SaveChanges();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRouting();
app.UseAuthorization();
app.MapControllers();

app.Run();";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "Program.cs"), program, Encoding.UTF8);
        }

        private static void GenerateAngularStarterTemplate(string projectPath, string projectName)
        {
            Directory.CreateDirectory(Path.Combine(projectPath, "src", "app", "pages", "admin-products"));
            Directory.CreateDirectory(Path.Combine(projectPath, "public"));

            string packageJson = $@"{{
  ""name"": ""{projectName.ToLowerInvariant()}"",
  ""version"": ""0.0.0"",
  ""scripts"": {{
    ""ng"": ""ng"",
    ""start"": ""ng serve"",
    ""build"": ""ng build"",
    ""watch"": ""ng build --watch --configuration development"",
    ""test"": ""ng test""
  }},
  ""private"": true,
  ""dependencies"": {{
    ""@angular/animations"": ""^19.0.0"",
    ""@angular/common"": ""^19.0.0"",
    ""@angular/compiler"": ""^19.0.0"",
    ""@angular/core"": ""^19.0.0"",
    ""@angular/forms"": ""^19.0.0"",
    ""@angular/platform-browser"": ""^19.0.0"",
    ""@angular/platform-browser-dynamic"": ""^19.0.0"",
    ""@angular/router"": ""^19.0.0"",
    ""rxjs"": ""~7.8.0"",
    ""tslib"": ""^2.3.0"",
    ""zone.js"": ""~0.15.0""
  }},
  ""devDependencies"": {{
    ""@angular-devkit/build-angular"": ""^19.0.0"",
    ""@angular/cli"": ""^19.0.0"",
    ""@angular/compiler-cli"": ""^19.0.0"",
    ""typescript"": ""~5.6.2""
  }}
}}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "package.json"), packageJson, Encoding.UTF8);

            string angularJson = $@"{{
  ""$schema"": ""./node_modules/@angular/cli/lib/config/schema.json"",
  ""version"": 1,
  ""newProjectRoot"": ""projects"",
  ""projects"": {{
    ""{projectName.ToLowerInvariant()}"": {{
      ""projectType"": ""application"",
      ""schematics"": {{}},
      ""root"": """",
      ""sourceRoot"": ""src"",
      ""prefix"": ""app"",
      ""architect"": {{
        ""build"": {{
          ""builder"": ""@angular-devkit/build-angular:application"",
          ""options"": {{
            ""outputPath"": ""dist/{projectName.ToLowerInvariant()}"",
            ""index"": ""src/index.html"",
            ""browser"": ""src/main.ts"",
            ""polyfills"": [""zone.js""],
            ""tsConfig"": ""tsconfig.app.json"",
            ""assets"": [""src/favicon.ico"", ""src/assets""],
            ""styles"": [""src/styles.css""],
            ""scripts"": []
          }}
        }},
        ""serve"": {{
          ""builder"": ""@angular-devkit/build-angular:dev-server"",
          ""configurations"": {{
            ""production"": {{
              ""buildTarget"": ""{projectName.ToLowerInvariant()}:build:production""
            }},
            ""development"": {{
              ""buildTarget"": ""{projectName.ToLowerInvariant()}:build:development""
            }}
          }},
          ""defaultConfiguration"": ""development""
        }}
      }}
    }}
  }}
}}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "angular.json"), angularJson, Encoding.UTF8);

            string tsconfig = @"{
  ""compileOnSave"": false,
  ""compilerOptions"": {
    ""outDir"": ""./dist/out-tsc"",
    ""strict"": true,
    ""noImplicitOverride"": true,
    ""noPropertyAccessFromIndexSignature"": true,
    ""noImplicitReturns"": true,
    ""noFallthroughCasesInSwitch"": true,
    ""skipLibCheck"": true,
    ""isolatedModules"": true,
    ""esModuleInterop"": true,
    ""experimentalDecorators"": true,
    ""moduleResolution"": ""bundler"",
    ""importHelpers"": true,
    ""target"": ""ES2022"",
    ""module"": ""ES2022""
  }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "tsconfig.json"), tsconfig, Encoding.UTF8);

            string indexHtml = $@"<!doctype html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <title>{projectName} - Btoan Studio App</title>
  <base href=""/"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <link rel=""icon"" type=""image/x-icon"" href=""favicon.ico"">
</head>
<body>
  <app-root></app-root>
</body>
</html>";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "index.html"), indexHtml, Encoding.UTF8);

            string stylesCss = @"/* Global Application Styles */
body {
  margin: 0;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
  background-color: #121212;
  color: #e0e0e0;
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "styles.css"), stylesCss, Encoding.UTF8);

            string mainTs = @"import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { AppComponent } from './app/app.component';

bootstrapApplication(AppComponent, appConfig)
  .catch((err) => console.error(err));";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "main.ts"), mainTs, Encoding.UTF8);

            string appConfig = @"import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes)
  ]
};";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "app.config.ts"), appConfig, Encoding.UTF8);

            string appRoutes = @"import { Routes } from '@angular/router';
import { AdminProducts } from './pages/admin-products/admin-products';

export const routes: Routes = [
  { path: '', component: AdminProducts },
  { path: 'admin-products', component: AdminProducts }
];";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "app.routes.ts"), appRoutes, Encoding.UTF8);

            string appComponent = @"import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent {
  title = '" + projectName + @"';
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "app.component.ts"), appComponent, Encoding.UTF8);

            string appComponentHtml = @"<main class=""main"">
  <router-outlet></router-outlet>
</main>";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "app.component.html"), appComponentHtml, Encoding.UTF8);
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "app.component.css"), "/* App Component CSS */", Encoding.UTF8);

            // AdminProducts Component
            string adminProductsTs = @"import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';

export interface ProductItem {
  id: number;
  name: string;
  price: number;
  stock: number;
  category: string;
}

@Component({
  selector: 'app-admin-products',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './admin-products.html',
  styleUrl: './admin-products.css'
})
export class AdminProducts {
  products: ProductItem[] = [
    { id: 1, name: 'Bàn phím cơ Custom Btoan AI', price: 1850000, stock: 25, category: 'Gaming Gear' },
    { id: 2, name: 'Chuột không dây Ultra-Light 4K', price: 1250000, stock: 40, category: 'Gaming Gear' },
    { id: 3, name: 'Màn hình 27 inch 2K 165Hz IPS', price: 5400000, stock: 12, category: 'Màn hình' },
    { id: 4, name: 'Tai nghe chụp tai chống ồn ANC', price: 2100000, stock: 18, category: 'Audio' }
  ];

  selectedProduct: ProductItem | null = null;

  selectProduct(p: ProductItem) {
    this.selectedProduct = p;
  }
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "pages", "admin-products", "admin-products.ts"), adminProductsTs, Encoding.UTF8);

            string adminProductsHtml = @"<div class=""admin-container"">
  <header class=""admin-header"">
    <h1>📦 Quản lý Sản phẩm (Admin Products)</h1>
    <span class=""badge"">Angular Standalone Component</span>
  </header>

  <div class=""product-grid"">
    <div class=""card"" *ngFor=""let p of products"" (click)=""selectProduct(p)"">
      <div class=""card-header"">
        <span class=""cat-tag"">{{ p.category }}</span>
        <span class=""stock-tag"">Kho: {{ p.stock }}</span>
      </div>
      <h3>{{ p.name }}</h3>
      <p class=""price"">{{ p.price | number }} VNĐ</p>
      <button class=""btn-action"">Xem Chi Tiết</button>
    </div>
  </div>

  <div class=""details-box"" *ngIf=""selectedProduct"">
    <h3>Đã chọn: {{ selectedProduct.name }}</h3>
    <p>Giá niêm yết: {{ selectedProduct.price | number }} VNĐ — Tồn kho: {{ selectedProduct.stock }} cái</p>
  </div>
</div>";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "pages", "admin-products", "admin-products.html"), adminProductsHtml, Encoding.UTF8);

            string adminProductsCss = @".admin-container {
  padding: 30px;
  max-width: 1100px;
  margin: 0 auto;
}
.admin-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  border-bottom: 1px solid #333;
  padding-bottom: 15px;
  margin-bottom: 25px;
}
.admin-header h1 {
  margin: 0;
  font-size: 24px;
  color: #61dafb;
}
.badge {
  background: #282c34;
  color: #98c379;
  padding: 5px 12px;
  border-radius: 12px;
  font-size: 13px;
  border: 1px solid #98c379;
}
.product-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 20px;
}
.card {
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 8px;
  padding: 16px;
  transition: transform 0.2s, border-color 0.2s;
  cursor: pointer;
}
.card:hover {
  transform: translateY(-4px);
  border-color: #61dafb;
}
.card-header {
  display: flex;
  justify-content: space-between;
  margin-bottom: 10px;
  font-size: 12px;
}
.cat-tag { color: #e5c07b; }
.stock-tag { color: #abb2bf; }
.price {
  font-size: 18px;
  font-weight: bold;
  color: #98c379;
  margin: 12px 0;
}
.btn-action {
  width: 100%;
  padding: 8px;
  background: #007acc;
  color: white;
  border: none;
  border-radius: 4px;
  cursor: pointer;
}
.details-box {
  margin-top: 30px;
  background: #252526;
  border-left: 4px solid #61dafb;
  padding: 15px 20px;
  border-radius: 4px;
}";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "pages", "admin-products", "admin-products.css"), adminProductsCss, Encoding.UTF8);

            string adminProductsSpec = @"import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminProducts } from './admin-products';

describe('AdminProducts', () => {
  let component: AdminProducts;
  let fixture: ComponentFixture<AdminProducts>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AdminProducts]
    }).compileComponents();

    fixture = TestBed.createComponent(AdminProducts);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create the component', () => {
    expect(component).toBeTruthy();
  });

  it('should have initial products list', () => {
    expect(component.products.length).toBeGreaterThan(0);
  });
});";
            System.IO.File.WriteAllText(Path.Combine(projectPath, "src", "app", "pages", "admin-products", "admin-products.spec.ts"), adminProductsSpec, Encoding.UTF8);
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
                    if (userId <= 0)
                    {
                        return Content("<h3>Vui lòng đăng nhập để xem trước dự án.</h3>", "text/html; charset=utf-8");
                    }
                    return NotFound($"Không tìm thấy dự án '{cleanProj}'.");
                }
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
Bạn là Btoan AI Agent - Trợ lý lập trình siêu cấp trong Btoan Studio IDE.
Nhiệm vụ của bạn:
1. Nếu người dùng yêu cầu sửa đổi/giải thích một file hiện tại: Phân tích file và trả về các thay đổi (action: 'edit').
2. Nếu người dùng yêu cầu TẠO NGUYÊN MỘT DỰ ÁN MỚI:
   - Đặt một tên dự án ngắn gọn bằng tiếng Anh/không dấu (ví dụ: 'shop-fullstack', 'my-webapi', 'love-letter', 'admin-dashboard').
   - QUY TẮC DÙNG CLI SCAFFOLDING ('setupCommands'):
     + Khi người dùng yêu cầu ASP.NET Core MVC: Sử dụng 'dotnet new mvc' trong 'setupCommands' (không tự chế tay file .csproj sơ sài). Nếu có Database, thêm lệnh 'dotnet add package Microsoft.EntityFrameworkCore.Sqlite'.
     + Khi người dùng yêu cầu ASP.NET Core Web API: Sử dụng 'dotnet new webapi --use-controllers' trong 'setupCommands'. Nếu có Database, thêm lệnh 'dotnet add package Microsoft.EntityFrameworkCore.Sqlite'.
     + QUY TẮC .NET CSPROJ & CHẶN CẢNH BÁO NUGET AUDIT (NU1902, NU1903):
       * Mọi dự án .NET / C# khi tạo hoặc chỉnh sửa file .csproj BẮT BUỘC phải chứa thẻ <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn> bên trong <PropertyGroup> để loại trừ triệt để cảnh báo bảo mật NuGet Audit khi build.
     + QUY TẮC CƠ SỞ DỮ LIỆU BẮT BUỘC (SQLITE & EF CORE):
       * Luôn dùng EF Core + SQLite ('Data Source=app.db') làm mặc định để chạy được ngay trên Web IDE mà không cần cài SQL Server.
       * Cung cấp công tắc chuyển đổi Database trong 'appsettings.json':
         ""Database"": { ""Provider"": ""Sqlite"", ""SqliteConnection"": ""Data Source=app.db"", ""SqlServerConnection"": ""Server=.;Database=MyAppDb;Trusted_Connection=True;TrustServerCertificate=True;"" }
       * Trong 'Program.cs': Đọc 'Database:Provider' (nếu là 'SqlServer' thì UseSqlServer, mặc định UseSqlite), luôn gọi 'db.Database.EnsureCreated()' và seed 5-10 bản ghi mẫu khởi tạo để ứng dụng có dữ liệu chạy ngay.
       * Tuyệt đối KHÔNG hardcode kết nối SQL Server cục bộ (ví dụ 'Server=localhost;...') vì sẽ gây crash lỗi kết nối trên Web IDE.
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
