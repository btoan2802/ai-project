using QuestPDF.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Net;
using System.Net.Sockets;
using BtoanAiNet.Data;
using BtoanAiNet.Repositories;
using BtoanAiNet.Services;
using BtoanAiNet.Services.VectorDb;
using BtoanAiNet.Messaging.Consumers;
using BtoanAiNet.FunctionCalling.Services;
using BtoanAiNet.FunctionCalling.Tools;
using MassTransit;
using Microsoft.EntityFrameworkCore;

QuestPDF.Settings.License = LicenseType.Community;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.Converters.Add(new BtoanAiNet.ViewModels.FlexibleStringConverter());
    });

var dbProvider = builder.Configuration.GetValue<string>("DbProvider");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
                    ?? builder.Configuration.GetConnectionString("SqlServerConnection") ?? "";

// Robust multi-provider detection:
// 1. Explicit SQLite or path contains .db
// 2. MySQL if explicitly configured or connection string contains CharSet / utf8mb4 / Port=3306
// 3. SQL Server if on Windows and configured with LocalDB or Trusted_Connection
// 4. Fallback: On Linux/Docker or if SQL Server is not reachable, use SQLite in App_Data/btoan_ai.db
bool isSqlite = (!string.IsNullOrEmpty(dbProvider) && dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                || connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase);

bool isMySql = (!string.IsNullOrEmpty(dbProvider) && dbProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
               || connectionString.Contains("CharSet=", StringComparison.OrdinalIgnoreCase)
               || connectionString.Contains("utf8mb4", StringComparison.OrdinalIgnoreCase);

bool isSqlServer = (!string.IsNullOrEmpty(dbProvider) && dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
                   || (OperatingSystem.IsWindows() && (connectionString.Contains("Trusted_Connection") || connectionString.Contains("(local)")));

if (isMySql)
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 30))));
}
else if (isSqlServer && OperatingSystem.IsWindows())
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(connectionString, sqlOptions => sqlOptions.CommandTimeout(5)));
}
else
{
    var appDataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
    Directory.CreateDirectory(appDataDir);
    var sqliteDbPath = Path.Combine(appDataDir, "btoan_ai.db");
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite($"Data Source={sqliteDbPath}"));
}

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("NoRedirectsClient").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

// Register Repositories
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();
builder.Services.AddScoped<ITrainingRepository, TrainingRepository>();

// Register Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddHttpClient<IGeminiService, GeminiService>()
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(240))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
        EnableMultipleHttp2Connections = true,
        ConnectCallback = async (context, cancellationToken) =>
        {
            // Force IPv4 in Linux/Docker environments to prevent 8-15s IPv6 resolution timeout
            var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
            var address = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address == null) throw new SocketException((int)SocketError.HostNotFound);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });
builder.Services.AddScoped<IIntentEngineService, IntentEngineService>();
builder.Services.AddScoped<IMemoryService, MemoryService>();
builder.Services.AddScoped<ITrainingService, TrainingService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IImageGenerationService, ImageGenerationService>();
builder.Services.AddScoped<IVideoGenerationService, VideoGenerationService>();
builder.Services.AddScoped<IMusicGenerationService, MusicGenerationService>();
builder.Services.AddHttpClient<IStorageService, StorageService>();
builder.Services.AddScoped<IArtifactGeneratorService, ArtifactGeneratorService>();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddScoped<ICodeAnalysisService, CodeAnalysisService>();
builder.Services.AddScoped<IProjectScannerService, ProjectScannerService>();
builder.Services.AddScoped<IFileEditorService, FileEditorService>();
builder.Services.AddScoped<IWorkspaceService, WorkspaceService>();
builder.Services.AddScoped<IWorkspaceIndexingService, WorkspaceIndexingService>();
builder.Services.AddScoped<ITerminalExecutionService, TerminalExecutionService>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IRuntimeService, RuntimeService>();
builder.Services.AddScoped<IAgentWorkflowService, AgentWorkflowService>();
builder.Services.AddScoped<IWorkspaceEditService, WorkspaceEditService>();
builder.Services.AddScoped<IVersionControlService, VersionControlService>();
builder.Services.AddSingleton<IProjectRuntimeManagerService, ProjectRuntimeManagerService>();
builder.Services.AddScoped<IContextRetrieverService, ContextRetrieverService>();
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();
builder.Services.AddScoped<IDiffService, DiffService>();
builder.Services.AddScoped<IAgentTeamManager, AgentTeamManager>();
builder.Services.AddScoped<IDeploymentService, DeploymentService>();

// Vector Database & Semantic Embeddings
builder.Services.AddHttpClient<IVectorDatabaseService, QdrantVectorDatabaseService>();
builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>();
builder.Services.AddSingleton<ISemanticChunkingService, SemanticChunkingService>();

// MassTransit Message Queue (RabbitMQ with In-Memory fallback for Dev)
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<MediaGenerationConsumer>();

    string? rabbitHost = builder.Configuration["RabbitMQ:Host"];
    if (!string.IsNullOrEmpty(rabbitHost) && !rabbitHost.Equals("in-memory", StringComparison.OrdinalIgnoreCase))
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(rabbitHost, "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:User"] ?? "guest");
                h.Password(builder.Configuration["RabbitMQ:Pass"] ?? "guest");
            });

            cfg.ReceiveEndpoint("btoan-media-generation-queue", e =>
            {
                e.ConfigureConsumer<MediaGenerationConsumer>(context);
            });
        });
    }
    else
    {
        x.UsingInMemory((context, cfg) =>
        {
            cfg.ConfigureEndpoints(context);
        });
    }
});

// Native Function Calling Engine
builder.Services.AddSingleton<IToolRegistry>(sp =>
{
    var registry = new ToolRegistry();
    registry.RegisterServiceTools<IWorkspaceTools>();
    return registry;
});
builder.Services.AddScoped<IWorkspaceTools, WorkspaceTools>();
builder.Services.AddScoped<IFunctionCallingDispatcher, FunctionCallingDispatcher>();
builder.Services.AddScoped<IAgentToolLoopOrchestrator, AgentToolLoopOrchestrator>();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(3);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
});

// Authentication & Session
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "BtoanAi.Auth";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.HttpOnly = true;
        options.LoginPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(24);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Auto ensure DB tables created for Workspace projects
using (var scope = app.Services.CreateScope())
{
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            dbContext.Database.EnsureCreated();

            // Seed default Admin user if not exists
            if (!dbContext.Users.Any(u => u.Username == "admin"))
            {
                string adminSeedPassword = builder.Configuration["AdminSeed:Password"] 
                    ?? Environment.GetEnvironmentVariable("ADMIN_SEED_PASSWORD") 
                    ?? "AdminPassword123!";

                dbContext.Users.Add(new BtoanAiNet.Models.User
                {
                    Username = "admin",
                    Password = adminSeedPassword,
                    Email = "admin@btoan.com",
                    FullName = "Btoan Admin",
                    CreatedAt = DateTime.UtcNow
                });
                dbContext.SaveChanges();
                Console.WriteLine("[SEED] Created default admin account: admin");
            }

            // Ensure shop-senior-mvc workspace template is seeded in user_1 if missing or incomplete
            try
            {
                string shopSeniorPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "UserWorkspaces", "user_1", "shop-senior-mvc");
                string templatePath = Path.Combine(builder.Environment.ContentRootPath, "WorkspaceTemplates", "shop-senior-mvc");
                if (!Directory.Exists(templatePath))
                {
                    templatePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "WorkspaceTemplates", "shop-senior-mvc");
                }

                bool needSeed = !Directory.Exists(shopSeniorPath)
                    || !File.Exists(Path.Combine(shopSeniorPath, "Controllers", "ProductsController.cs"))
                    || !File.Exists(Path.Combine(shopSeniorPath, "Data", "AppDbContext.cs"));

                if (needSeed && Directory.Exists(templatePath))
                {
                    Directory.CreateDirectory(shopSeniorPath);
                    foreach (string dir in Directory.GetDirectories(templatePath, "*", SearchOption.AllDirectories))
                    {
                        string destDir = dir.Replace(templatePath, shopSeniorPath);
                        Directory.CreateDirectory(destDir);
                    }
                    foreach (string file in Directory.GetFiles(templatePath, "*.*", SearchOption.AllDirectories))
                    {
                        string destFile = file.Replace(templatePath, shopSeniorPath);
                        File.Copy(file, destFile, true);
                    }
                    Console.WriteLine("[SEED] Seeded full Senior Architect Shop template for shop-senior-mvc.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SEED] shop-senior-mvc seed note: {ex.Message}");
            }

            // Auto sanitize SQLite connection strings across existing workspaces
            try
            {
                string userWorkspacesDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "UserWorkspaces");
                if (Directory.Exists(userWorkspacesDir))
                {
                    foreach (var uDir in Directory.GetDirectories(userWorkspacesDir))
                    {
                        foreach (var pDir in Directory.GetDirectories(uDir))
                        {
                            BtoanAiNet.Controllers.IdeController.AutoFixMapStaticAssets(pDir);
                        }
                    }
                }
            }
            catch { }

            try
            {
                dbContext.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS AgentChatMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    ProjectName TEXT NOT NULL,
    Role TEXT NOT NULL,
    Content TEXT,
    ThoughtText TEXT,
    ThoughtSeconds INTEGER DEFAULT 0,
    PlanJson TEXT,
    FilePillsJson TEXT,
    ToolActionsJson TEXT,
    CheckpointId TEXT,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_AgentChatMessages_User_Project ON AgentChatMessages(UserId, ProjectName);
");

                string[] alterCols = new[]
                {
                    "ALTER TABLE AgentChatMessages ADD COLUMN ThoughtText TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN ThoughtSeconds INTEGER DEFAULT 0;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN PlanJson TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN FilePillsJson TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN ToolActionsJson TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN CheckpointId TEXT;"
                };
                foreach (var alter in alterCols)
                {
                    try { dbContext.Database.ExecuteSqlRaw(alter); } catch { }
                }

                // Ensure Learning Upgrade tables
                dbContext.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS LearningTopicStats (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    Subject TEXT NOT NULL,
    GradeLevel TEXT NOT NULL,
    TopicName TEXT NOT NULL,
    TotalQuestions INTEGER NOT NULL DEFAULT 0,
    CorrectQuestions INTEGER NOT NULL DEFAULT 0,
    MasteryPercent REAL NOT NULL DEFAULT 0,
    LastPracticedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IDX_LearningTopicStats_User ON LearningTopicStats(UserId, Subject);

CREATE TABLE IF NOT EXISTS StudyRoadmaps (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    Title TEXT NOT NULL,
    Subject TEXT NOT NULL,
    GradeLevel TEXT NOT NULL,
    ExamDate TEXT NOT NULL,
    TargetScore TEXT NOT NULL,
    DailyMinutes INTEGER NOT NULL DEFAULT 60,
    SourceDocName TEXT NULL,
    PlanJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IDX_StudyRoadmaps_User ON StudyRoadmaps(UserId);
");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AgentChatMessages] Startup ensure warning: {ex.Message}");
            }
        }
        catch (Exception initEx)
        {
            Console.WriteLine($"DB EnsureCreated / Seed Warning: {initEx.Message}");
        }

        string sql = @"
-- Tables preserved safely with IF NOT EXISTS

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[users] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [username] NVARCHAR(50) NULL,
        [full_name] NVARCHAR(255) NULL,
        [password] NVARCHAR(255) NULL,
        [created_at] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        [dob] DATE NULL,
        [email] NVARCHAR(255) NULL,
        [google_id] NVARCHAR(255) NULL,
        [avatar] NVARCHAR(MAX) NULL,
        [face_enabled] INT NULL,
        [face_image_url] NVARCHAR(MAX) NULL,
        [face_descriptor] NVARCHAR(MAX) NULL,
        [face_updated_at] DATETIME2 NULL
    );
    CREATE UNIQUE INDEX [IX_users_username] ON [dbo].[users] ([username]) WHERE [username] IS NOT NULL;
END

-- Ensure User Settings Columns Exist
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND type in (N'U'))
BEGIN
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'bio')
        ALTER TABLE [dbo].[users] ADD [bio] NVARCHAR(500) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'default_model')
        ALTER TABLE [dbo].[users] ADD [default_model] NVARCHAR(50) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'custom_instructions')
        ALTER TABLE [dbo].[users] ADD [custom_instructions] NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'temperature')
        ALTER TABLE [dbo].[users] ADD [temperature] FLOAT NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'long_term_memory')
        ALTER TABLE [dbo].[users] ADD [long_term_memory] INT NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'rag_enabled')
        ALTER TABLE [dbo].[users] ADD [rag_enabled] INT NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'theme')
        ALTER TABLE [dbo].[users] ADD [theme] NVARCHAR(20) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'chat_font_size')
        ALTER TABLE [dbo].[users] ADD [chat_font_size] NVARCHAR(20) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[users]') AND name = 'tts_speed')
        ALTER TABLE [dbo].[users] ADD [tts_speed] FLOAT NULL;
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[conversations]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[conversations] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [user_id] INT NULL,
        [title] NVARCHAR(MAX) NULL,
        [created_at] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[messages]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[messages] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [conversation_id] INT NULL,
        [role] NVARCHAR(20) NULL,
        [text] NVARCHAR(MAX) NULL,
        [created_at] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[chats]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[chats] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [user_id] INT NULL,
        [conversation_id] INT NULL,
        [question] NVARCHAR(MAX) NULL,
        [answer] NVARCHAR(MAX) NULL,
        [images] NVARCHAR(MAX) NULL,
        [model_used] NVARCHAR(50) NULL,
        [attachments_json] NVARCHAR(MAX) NULL,
        [artifacts_json] NVARCHAR(MAX) NULL,
        [created_at] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[chats]') AND name = 'model_used')
        ALTER TABLE [dbo].[chats] ADD [model_used] NVARCHAR(50) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[chats]') AND name = 'attachments_json')
        ALTER TABLE [dbo].[chats] ADD [attachments_json] NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[chats]') AND name = 'artifacts_json')
        ALTER TABLE [dbo].[chats] ADD [artifacts_json] NVARCHAR(MAX) NULL;
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[projects]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[projects] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [user_id] INT NOT NULL,
        [name] NVARCHAR(100) NOT NULL,
        [description] NVARCHAR(255) NULL,
        [template] NVARCHAR(50) NOT NULL,
        [storage_path] NVARCHAR(500) NOT NULL,
        [created_at] DATETIME2 NOT NULL,
        [updated_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[project_files]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[project_files] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [relative_path] NVARCHAR(500) NOT NULL,
        [language] NVARCHAR(50) NOT NULL,
        [content] NVARCHAR(MAX) NOT NULL,
        [is_directory] BIT NOT NULL DEFAULT 0,
        [updated_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[file_versions]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[file_versions] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_file_id] INT NOT NULL,
        [version_number] INT NOT NULL,
        [content] NVARCHAR(MAX) NOT NULL,
        [change_summary] NVARCHAR(255) NULL,
        [created_by] NVARCHAR(50) NOT NULL,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[terminal_sessions]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[terminal_sessions] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [user_id] INT NOT NULL,
        [command] NVARCHAR(500) NOT NULL,
        [status] NVARCHAR(50) NOT NULL,
        [stdout] NVARCHAR(MAX) NULL,
        [stderr] NVARCHAR(MAX) NULL,
        [exit_code] INT NULL,
        [started_at] DATETIME2 NOT NULL,
        [finished_at] DATETIME2 NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[agent_tasks]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[agent_tasks] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [user_id] INT NOT NULL,
        [prompt] NVARCHAR(MAX) NOT NULL,
        [status] NVARCHAR(50) NOT NULL,
        [summary] NVARCHAR(MAX) NULL,
        [created_at] DATETIME2 NOT NULL,
        [completed_at] DATETIME2 NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[agent_messages]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[agent_messages] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [agent_task_id] INT NOT NULL,
        [role] NVARCHAR(20) NOT NULL,
        [content] NVARCHAR(MAX) NOT NULL,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[agent_changes]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[agent_changes] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [agent_task_id] INT NOT NULL,
        [file_path] NVARCHAR(500) NOT NULL,
        [old_content] NVARCHAR(MAX) NULL,
        [new_content] NVARCHAR(MAX) NULL,
        [change_type] NVARCHAR(20) NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[project_versions]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[project_versions] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [user_id] INT NOT NULL,
        [parent_version_id] INT NULL,
        [version_number] INT NOT NULL,
        [commit_message] NVARCHAR(255) NOT NULL,
        [created_by_agent] BIT NOT NULL,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[file_snapshots]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[file_snapshots] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_version_id] INT NOT NULL,
        [file_path] NVARCHAR(500) NOT NULL,
        [content] NVARCHAR(MAX) NOT NULL,
        [file_hash] NVARCHAR(64) NOT NULL,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[file_change_histories]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[file_change_histories] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_version_id] INT NOT NULL,
        [file_path] NVARCHAR(500) NOT NULL,
        [action] NVARCHAR(20) NOT NULL,
        [old_hash] NVARCHAR(64) NULL,
        [new_hash] NVARCHAR(64) NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[autonomous_agent_tasks]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[autonomous_agent_tasks] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [parent_task_id] INT NULL,
        [title] NVARCHAR(255) NOT NULL,
        [description] NVARCHAR(MAX) NULL,
        [status] NVARCHAR(50) NOT NULL,
        [execution_order] INT NOT NULL,
        [created_at] DATETIME2 NOT NULL,
        [completed_at] DATETIME2 NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[project_memories]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[project_memories] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [memory_key] NVARCHAR(100) NOT NULL,
        [memory_value] NVARCHAR(MAX) NOT NULL,
        [updated_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[code_changes]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[code_changes] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [agent_task_id] INT NOT NULL,
        [project_id] INT NOT NULL,
        [file_path] NVARCHAR(500) NOT NULL,
        [old_content] NVARCHAR(MAX) NULL,
        [new_content] NVARCHAR(MAX) NULL,
        [diff_text] NVARCHAR(MAX) NULL,
        [status] NVARCHAR(20) NOT NULL,
        [ai_comment] NVARCHAR(MAX) NULL,
        [security_warning_count] INT NOT NULL DEFAULT 0,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[team_agent_tasks]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[team_agent_tasks] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [assigned_agent] NVARCHAR(50) NOT NULL,
        [title] NVARCHAR(255) NOT NULL,
        [description] NVARCHAR(MAX) NULL,
        [priority] NVARCHAR(20) NOT NULL,
        [status] NVARCHAR(50) NOT NULL,
        [execution_order] INT NOT NULL,
        [created_at] DATETIME2 NOT NULL,
        [completed_at] DATETIME2 NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[agent_team_messages]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[agent_team_messages] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [team_task_id] INT NULL,
        [from_agent] NVARCHAR(50) NOT NULL,
        [to_agent] NVARCHAR(50) NOT NULL,
        [message] NVARCHAR(MAX) NOT NULL,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[project_knowledge_bases]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[project_knowledge_bases] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [topic] NVARCHAR(100) NOT NULL,
        [details] NVARCHAR(MAX) NOT NULL,
        [updated_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[deployments]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[deployments] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [user_id] INT NOT NULL,
        [environment] NVARCHAR(20) NOT NULL,
        [provider] NVARCHAR(50) NOT NULL,
        [status] NVARCHAR(20) NOT NULL,
        [url] NVARCHAR(255) NOT NULL,
        [container_id] NVARCHAR(100) NULL,
        [port] INT NOT NULL,
        [detected_framework] NVARCHAR(50) NOT NULL,
        [created_at] DATETIME2 NOT NULL,
        [updated_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[project_env_vars]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[project_env_vars] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [project_id] INT NOT NULL,
        [environment] NVARCHAR(20) NOT NULL,
        [key_name] NVARCHAR(100) NOT NULL,
        [encrypted_value] NVARCHAR(MAX) NOT NULL,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[deployment_logs]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[deployment_logs] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [deployment_id] INT NOT NULL,
        [step] NVARCHAR(100) NOT NULL,
        [message] NVARCHAR(MAX) NOT NULL,
        [log_level] NVARCHAR(20) NOT NULL,
        [timestamp] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[agent_chat_messages]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[agent_chat_messages] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [user_id] INT NOT NULL,
        [project_name] NVARCHAR(100) NOT NULL,
        [role] NVARCHAR(20) NOT NULL,
        [content] NVARCHAR(MAX) NOT NULL,
        [plan_json] NVARCHAR(MAX) NULL,
        [tool_actions_json] NVARCHAR(MAX) NULL,
        [created_at] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[UserPreferences]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[UserPreferences] (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [UserId] INT NOT NULL,
        [CustomInstructions] NVARCHAR(MAX) NULL,
        [SavedMemories] NVARCHAR(MAX) NULL,
        [Theme] NVARCHAR(50) NOT NULL DEFAULT 'dark',
        [DefaultLocation] NVARCHAR(200) NOT NULL DEFAULT N'TP. Hồ Chí Minh, Việt Nam',
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        [UpdatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[UserGems]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[UserGems] (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [UserId] INT NOT NULL,
        [Name] NVARCHAR(100) NOT NULL,
        [Icon] NVARCHAR(20) NOT NULL DEFAULT N'💎',
        [Description] NVARCHAR(MAX) NULL,
        [SystemPrompt] NVARCHAR(MAX) NOT NULL,
        [DefaultDocId] INT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    INSERT INTO [dbo].[UserGems] ([UserId], [Name], [Icon], [Description], [SystemPrompt], [CreatedAt])
    VALUES 
    (1, N'Gia sư Bách Khoa', N'🎓', N'Gia sư gợi mở (Socratic) hướng dẫn giải bài từng bước', N'Bạn là Gia sư Bách Khoa kiên nhẫn. Đặt câu hỏi gợi mở, không đưa đáp án ngay.', GETUTCDATE()),
    (1, N'Chuyên gia C# / .NET', N'💻', N'Senior .NET Architect tối ưu mã nguồn và clean architecture', N'Bạn là Senior .NET Architect tư vấn code C# hiện đại, clean architecture.', GETUTCDATE()),
    (1, N'Luyện thi IELTS 8.0', N'🗣️', N'Huấn luyện viên sửa lỗi từ vựng và ngữ pháp IELTS cao cấp', N'You are an elite IELTS Examiner and Coach for band 8.0+.', GETUTCDATE());
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[UserFeedbacks]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[UserFeedbacks] (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [UserId] INT NULL,
        [UserName] NVARCHAR(200) NULL,
        [UserEmail] NVARCHAR(200) NULL,
        [Category] NVARCHAR(100) NULL,
        [Message] NVARCHAR(MAX) NOT NULL,
        [Rating] INT NOT NULL DEFAULT 5,
        [IsResolved] BIT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[UserFeedbacks]') AND name = 'UserName')
        ALTER TABLE [dbo].[UserFeedbacks] ADD [UserName] NVARCHAR(200) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[UserFeedbacks]') AND name = 'UserEmail')
        ALTER TABLE [dbo].[UserFeedbacks] ADD [UserEmail] NVARCHAR(200) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[UserFeedbacks]') AND name = 'IsResolved')
        ALTER TABLE [dbo].[UserFeedbacks] ADD [IsResolved] BIT NOT NULL DEFAULT 0;
END

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SharedLinks]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[SharedLinks] (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [UserId] INT NOT NULL,
        [ConversationId] INT NOT NULL,
        [ShareToken] NVARCHAR(100) NOT NULL,
        [Title] NVARCHAR(200) NULL,
        [IsRevoked] BIT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END
";
        if (dbContext.Database.IsSqlServer())
        {
            dbContext.Database.ExecuteSqlRaw(sql);
        }
        else if (dbContext.Database.IsSqlite())
        {
            dbContext.Database.EnsureCreated();
            var sqliteCols = new (string col, string type)[]
            {
                ("full_name", "TEXT NULL"),
                ("password", "TEXT NULL"),
                ("created_at", "TEXT NULL"),
                ("dob", "TEXT NULL"),
                ("email", "TEXT NULL"),
                ("google_id", "TEXT NULL"),
                ("avatar", "TEXT NULL"),
                ("bio", "TEXT NULL"),
                ("default_model", "TEXT NULL DEFAULT 'Btoan 2.5 Flash'"),
                ("custom_instructions", "TEXT NULL"),
                ("temperature", "REAL NULL DEFAULT 0.7"),
                ("long_term_memory", "INTEGER NULL DEFAULT 1"),
                ("rag_enabled", "INTEGER NULL DEFAULT 1"),
                ("theme", "TEXT NULL DEFAULT 'dark'"),
                ("chat_font_size", "TEXT NULL DEFAULT '15px'"),
                ("tts_speed", "REAL NULL DEFAULT 1.0")
            };
            foreach (var (col, colType) in sqliteCols)
            {
                try
                {
                    string alterSql = string.Format("ALTER TABLE users ADD COLUMN {0} {1};", col, colType);
                    dbContext.Database.ExecuteSqlRaw(alterSql);
                }
                catch { }
            }

            try
            {
                dbContext.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS AgentChatMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    ProjectName TEXT NOT NULL,
    Role TEXT NOT NULL,
    Content TEXT,
    ThoughtText TEXT,
    ThoughtSeconds INTEGER DEFAULT 0,
    PlanJson TEXT,
    FilePillsJson TEXT,
    ToolActionsJson TEXT,
    CheckpointId TEXT,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_AgentChatMessages_User_Project ON AgentChatMessages(UserId, ProjectName);
");

                string[] alterCols = new[]
                {
                    "ALTER TABLE AgentChatMessages ADD COLUMN ThoughtText TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN ThoughtSeconds INTEGER DEFAULT 0;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN PlanJson TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN FilePillsJson TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN ToolActionsJson TEXT;",
                    "ALTER TABLE AgentChatMessages ADD COLUMN CheckpointId TEXT;"
                };
                foreach (var alter in alterCols)
                {
                    try { dbContext.Database.ExecuteSqlRaw(alter); } catch { }
                }

                // Ensure Learning Upgrade tables
                dbContext.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS LearningTopicStats (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    Subject TEXT NOT NULL,
    GradeLevel TEXT NOT NULL,
    TopicName TEXT NOT NULL,
    TotalQuestions INTEGER NOT NULL DEFAULT 0,
    CorrectQuestions INTEGER NOT NULL DEFAULT 0,
    MasteryPercent REAL NOT NULL DEFAULT 0,
    LastPracticedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IDX_LearningTopicStats_User ON LearningTopicStats(UserId, Subject);

CREATE TABLE IF NOT EXISTS StudyRoadmaps (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    Title TEXT NOT NULL,
    Subject TEXT NOT NULL,
    GradeLevel TEXT NOT NULL,
    ExamDate TEXT NOT NULL,
    TargetScore TEXT NOT NULL,
    DailyMinutes INTEGER NOT NULL DEFAULT 60,
    SourceDocName TEXT NULL,
    PlanJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IDX_StudyRoadmaps_User ON StudyRoadmaps(UserId);
");

                // Ensure Btoan AI settings tables for SQLite
                string[] settingsSqlite = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS UserPreferences (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId INTEGER NOT NULL UNIQUE,
                        CustomInstructions TEXT,
                        SavedMemories TEXT,
                        Theme TEXT DEFAULT 'dark',
                        DefaultLocation TEXT DEFAULT 'TP. Hồ Chí Minh, Việt Nam',
                        CreatedAt TEXT,
                        UpdatedAt TEXT
                    );",
                    @"CREATE TABLE IF NOT EXISTS UserGems (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId INTEGER NOT NULL,
                        Name TEXT NOT NULL,
                        Icon TEXT DEFAULT '💎',
                        Description TEXT,
                        SystemPrompt TEXT NOT NULL,
                        DefaultDocId INTEGER DEFAULT 0,
                        CreatedAt TEXT
                    );",
                    @"CREATE TABLE IF NOT EXISTS UserFeedbacks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId INTEGER,
                        UserName TEXT,
                        UserEmail TEXT,
                        Category TEXT,
                        Message TEXT NOT NULL,
                        Rating INTEGER DEFAULT 5,
                        IsResolved INTEGER DEFAULT 0,
                        CreatedAt TEXT
                    );",
                    @"CREATE TABLE IF NOT EXISTS SharedLinks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId INTEGER NOT NULL,
                        ConversationId INTEGER NOT NULL,
                        ShareToken TEXT NOT NULL UNIQUE,
                        Title TEXT,
                        IsRevoked INTEGER DEFAULT 0,
                        CreatedAt TEXT
                    );",
                    @"CREATE INDEX IF NOT EXISTS IX_UserGems_UserId ON UserGems(UserId);",
                    @"CREATE INDEX IF NOT EXISTS IX_SharedLinks_Token ON SharedLinks(ShareToken);",
                    @"INSERT OR IGNORE INTO UserGems (Id, UserId, Name, Icon, Description, SystemPrompt, CreatedAt) VALUES 
                    (1, 1, 'Gia sư Bách Khoa', '🎓', 'Gia sư gợi mở (Socratic) hướng dẫn giải bài từng bước', 'Bạn là Gia sư Bách Khoa kiên nhẫn. Đặt câu hỏi gợi mở, không đưa đáp án ngay.', datetime('now')),
                    (2, 1, 'Chuyên gia C# / .NET', '💻', 'Senior .NET Architect tối ưu mã nguồn và clean architecture', 'Bạn là Senior .NET Architect tư vấn code C# hiện đại, clean architecture.', datetime('now')),
                    (3, 1, 'Luyện thi IELTS 8.0', '🗣️', 'Huấn luyện viên sửa lỗi từ vựng và ngữ pháp IELTS cao cấp', 'You are an elite IELTS Examiner and Coach for band 8.0+.', datetime('now'));"
                };
                foreach (var sqlStmt in settingsSqlite)
                {
                    try { dbContext.Database.ExecuteSqlRaw(sqlStmt); } catch { }
                }
            }
            catch { }
        }
        else
        {
            // MySQL / MariaDB auto-migration for missing columns in users table
            var nonSqlCols = new (string col, string type)[]
            {
                ("bio", "TEXT NULL"),
                ("default_model", "VARCHAR(50) NULL DEFAULT 'Btoan 2.5 Flash'"),
                ("custom_instructions", "TEXT NULL"),
                ("temperature", "DOUBLE NULL DEFAULT 0.7"),
                ("long_term_memory", "INT NULL DEFAULT 1"),
                ("rag_enabled", "INT NULL DEFAULT 1"),
                ("theme", "VARCHAR(20) NULL DEFAULT 'dark'"),
                ("chat_font_size", "VARCHAR(20) NULL DEFAULT '15px'"),
                ("tts_speed", "DOUBLE NULL DEFAULT 1.0")
            };

            foreach (var (col, colType) in nonSqlCols)
            {
                try
                {
                    string alterSql = string.Format("ALTER TABLE `users` ADD COLUMN `{0}` {1};", col, colType);
                    dbContext.Database.ExecuteSqlRaw(alterSql);
                }
                catch
                {
                    // Ignore if column already exists
                }
            }

            try
            {
                string[] settingsMySql = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS `agent_chat_messages` (
                        `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                        `user_id` INT NOT NULL,
                        `project_name` VARCHAR(100) NOT NULL,
                        `role` VARCHAR(20) NOT NULL,
                        `content` LONGTEXT NOT NULL,
                        `plan_json` LONGTEXT NULL,
                        `tool_actions_json` LONGTEXT NULL,
                        `created_at` DATETIME NOT NULL
                    );",
                    @"CREATE TABLE IF NOT EXISTS `UserPreferences` (
                        `Id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                        `UserId` INT NOT NULL UNIQUE,
                        `CustomInstructions` LONGTEXT NULL,
                        `SavedMemories` LONGTEXT NULL,
                        `Theme` VARCHAR(50) NOT NULL DEFAULT 'dark',
                        `DefaultLocation` VARCHAR(200) NOT NULL DEFAULT 'TP. Hồ Chí Minh, Việt Nam',
                        `CreatedAt` DATETIME NOT NULL,
                        `UpdatedAt` DATETIME NOT NULL
                    );",
                    @"CREATE TABLE IF NOT EXISTS `UserGems` (
                        `Id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                        `UserId` INT NOT NULL,
                        `Name` VARCHAR(100) NOT NULL,
                        `Icon` VARCHAR(20) NOT NULL DEFAULT '💎',
                        `Description` LONGTEXT NULL,
                        `SystemPrompt` LONGTEXT NOT NULL,
                        `DefaultDocId` INT NOT NULL DEFAULT 0,
                        `CreatedAt` DATETIME NOT NULL
                    );",
                    @"CREATE TABLE IF NOT EXISTS `UserFeedbacks` (
                        `Id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                        `UserId` INT NULL,
                        `Category` VARCHAR(100) NULL,
                        `Message` LONGTEXT NOT NULL,
                        `Rating` INT NOT NULL DEFAULT 5,
                        `CreatedAt` DATETIME NOT NULL
                    );",
                    @"CREATE TABLE IF NOT EXISTS `SharedLinks` (
                        `Id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                        `UserId` INT NOT NULL,
                        `ConversationId` INT NOT NULL,
                        `ShareToken` VARCHAR(100) NOT NULL UNIQUE,
                        `Title` VARCHAR(200) NULL,
                        `IsRevoked` TINYINT NOT NULL DEFAULT 0,
                        `CreatedAt` DATETIME NOT NULL
                    );"
                };
                foreach (var sqlStmt in settingsMySql)
                {
                    try { dbContext.Database.ExecuteSqlRaw(sqlStmt); } catch { }
                }
            }
            catch { }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"DB AutoInit Warning: {ex.Message}");
    }
}

// Configure the HTTP request pipeline.
// 1. Configure ForwardedHeaders for Cloudflare Tunnel / Reverse Proxy (ai.btoan.com)
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor 
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto 
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// 2. Standard Static Files for wwwroot
app.UseStaticFiles();

app.UseRouting();

app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "conversation",
    pattern: "c/{id?}",
    defaults: new { controller = "Home", action = "Index" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<BtoanAiNet.Hubs.TerminalHub>("/hubs/terminal");
app.MapHub<BtoanAiNet.Hubs.WorkspaceHub>("/ws/workspace");


// GET /health Endpoint for self-hosted infrastructure monitoring
app.MapGet("/health", async (
    AppDbContext db,
    BtoanAiNet.Services.VectorDb.IVectorDatabaseService vectorDb,
    IConfiguration config,
    IWebHostEnvironment env) =>
{
    var status = new Dictionary<string, object>();
    bool overallHealthy = true;
    bool isDegraded = false;

    // 1. Database check (fast timeout)
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        bool dbOk = await db.Database.CanConnectAsync(cts.Token);
        status["database"] = dbOk ? "Healthy" : "Degraded (Unavailable)";
        if (!dbOk) isDegraded = true;
    }
    catch (Exception ex)
    {
        status["database"] = $"Degraded: {ex.Message}";
        isDegraded = true;
    }

    // 2. Qdrant Vector DB check (optional, if down => degraded)
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        bool qdrantOk = await vectorDb.IsHealthyAsync(cts.Token);
        status["qdrant"] = qdrantOk ? "Healthy" : "Unavailable (Degraded)";
        if (!qdrantOk) isDegraded = true;
    }
    catch
    {
        status["qdrant"] = "Unavailable (Degraded)";
        isDegraded = true;
    }

    // 3. MessageBus check
    string rabbitMqHost = config["RabbitMQ:Host"] ?? "";
    status["messageBus"] = !string.IsNullOrEmpty(rabbitMqHost) ? $"RabbitMQ ({rabbitMqHost})" : "In-Memory / Direct";

    // 4. Storage directory write check
    try
    {
        string filesDir = Path.Combine(env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "files");
        if (!Directory.Exists(filesDir)) Directory.CreateDirectory(filesDir);
        string testFile = Path.Combine(filesDir, $".health_check_{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(testFile, "health");
        File.Delete(testFile);
        status["storageDir"] = "Healthy (Writable)";
    }
    catch (Exception ex)
    {
        status["storageDir"] = $"Unwritable: {ex.Message}";
        overallHealthy = false;
    }

    status["status"] = !overallHealthy ? "Unhealthy" : (isDegraded ? "Degraded" : "Healthy");
    status["timestamp"] = DateTime.UtcNow.ToString("o");

    int statusCode = !overallHealthy ? 503 : 200;
    return Results.Json(status, statusCode: statusCode);
});

app.Run();
