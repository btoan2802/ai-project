using QuestPDF.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
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

var dbProvider = builder.Configuration.GetValue<string>("DbProvider") ?? "SqlServer";
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
                    ?? builder.Configuration.GetConnectionString("SqlServerConnection");

if (!string.IsNullOrEmpty(connectionString))
{
    if (dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) || connectionString.Contains("FS-TOANNB21") || connectionString.Contains("Trusted_Connection") || connectionString.Contains("(local)"))
    {
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sqlOptions => sqlOptions.CommandTimeout(3)));
    }
    else
    {
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 30))));
    }
}

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("NoRedirectsClient").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

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
        EnableMultipleHttp2Connections = true
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

builder.Services.AddSignalR();

// Authentication & Session
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "BtoanAi.Auth";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
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
";
        if (dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            dbContext.Database.ExecuteSqlRaw(sql);
        }
        else
        {
            // MySQL / MariaDB / SQLite auto-migration for missing columns in users table
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
