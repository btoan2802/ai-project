# BÁO CÁO HỒ SƠ MÃ NGUỒN & QUÉT LỖI BACKEND: BtoanAiNet
**Dự án:** `BtoanAiNet` (ASP.NET Core 10.0 MVC / AI Multi-Agent Workspace)  
**Thời gian quét:** Tháng 11/2026  
**Chế độ:** CHỈ ĐỌC (READ-ONLY AUDIT - Tuyệt đối không can thiệp code)  
**Bảo mật:** Toàn bộ API Key, Password, Connection String, Token nhạy cảm đã được che thành `<REDACTED>`.

---

## PHẦN 1: KẾT QUẢ KIỂM TRA TỰ ĐỘNG

### 1.1. Kết quả chạy `dotnet build`
Khi chạy lệnh build tại thư mục gốc của dự án:

#### A. Lệnh mặc định: `dotnet build`
```text
MSBUILD : error MSB1011: Specify which project or solution file to use because this folder contains more than one project or solution file.
```
> [!WARNING] CẢNH BÁO HYGIENE THƯ MỤC GỐC
> Thư mục gốc chứa đồng thời 3 file project/solution: `BtoanAiNet.sln`, `BtoanAiNet.slnx`, và `BtoanAiNet.csproj`. Điều này khiến lệnh CLI `dotnet build` hoặc các pipeline CI/CD tự động (GitHub Actions, Docker multi-stage build, Render build) bị crash nếu không chỉ định rõ target file.

#### B. Lệnh chỉ định target: `dotnet build BtoanAiNet.csproj --no-incremental`
```text
  Determining projects to restore...
D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj : warning NU1902: Package 'MySqlConnector' 2.4.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-473q-m89c-ghf8
  All projects are up-to-date for restore.
D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj : warning NU1902: Package 'MySqlConnector' 2.4.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-473q-m89c-ghf8
D:\ai.btoan.com\BtoanAiNet\Services\DiffService.cs(25,34): warning CS8600: Converting null literal or possible null value to non-nullable type. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\DiffService.cs(26,34): warning CS8600: Converting null literal or possible null value to non-nullable type. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\ChatService.cs(296,37): warning CS8600: Converting null literal or possible null value to non-nullable type. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\ChatService.cs(329,42): warning CS8600: Converting null literal or possible null value to non-nullable type. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\DiffService.cs(67,39): warning CS8600: Converting null literal or possible null value to non-nullable type. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\DiffService.cs(99,42): warning CS8603: Possible null reference return. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\DiffService.cs(104,35): warning CS8602: Dereference of a possibly null reference. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\FileService.cs(90,35): warning CS8600: Converting null literal or possible null value to non-nullable type. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
D:\ai.btoan.com\BtoanAiNet\Services\FileService.cs(91,34): warning CS8600: Converting null literal or possible null value to non-nullable type. [D:\ai.btoan.com\BtoanAiNet\BtoanAiNet.csproj]
  BtoanAiNet -> D:\ai.btoan.com\BtoanAiNet\bin\Debug\net10.0\BtoanAiNet.dll

Build succeeded.

    11 Warning(s)
    0 Error(s)

Time Elapsed 00:00:25.07
```
**Tổng hợp lỗi & cảnh báo mã nguồn:**
1. `BtoanAiNet.csproj`: Lỗ hổng bảo mật mức trung bình `NU1902` từ package `MySqlConnector` phiên bản 2.4.0 (GHSA-473q-m89c-ghf8). Cần nâng cấp lên version >= 2.4.1 hoặc bản mới nhất hỗ trợ .NET 10.
2. `Services/ChatService.cs:296, 329`: Cảnh báo `CS8600` do ép kiểu null sang kiểu non-nullable string.
3. `Services/DiffService.cs:25, 26, 67`: Cảnh báo `CS8600` do gán giá trị có khả năng null.
4. `Services/DiffService.cs:99`: Cảnh báo `CS8603` trả về null reference từ method non-nullable.
5. `Services/DiffService.cs:104`: Cảnh báo `CS8602` dereference đối tượng có thể null mà không kiểm tra.
6. `Services/FileService.cs:90, 91`: Cảnh báo `CS8600` do gán giá trị nullable.

---

### 1.2. Kiểm tra Đăng ký Dependency Injection (DI) & Vòng đời (Lifetime)
Rà soát toàn bộ `Program.cs` đối chiếu với các Controller, Service, Hub, Consumer:

| Thành phần | Interface / Class | Đăng ký trong Program.cs | Nơi tiêu thụ / Sử dụng | Đánh giá & Rủi ro |
| :--- | :--- | :--- | :--- | :--- |
| **CodeDiffService** | `ICodeDiffService` -> `CodeDiffService` | Dòng 64: `AddScoped` | **Không có Controller/Service nào inject** | **Dead Registration:** Hệ thống thực tế inject `IDiffService` -> `DiffService`. Đăng ký này dư thừa. |
| **AgentToolLoopOrchestrator** | `IAgentToolLoopOrchestrator` -> `AgentToolLoopOrchestrator` | Dòng 125: `AddScoped` | **Không có Controller/Service nào inject** | **Dead Registration / Incomplete Feature:** Được viết cho vòng lặp tool calling nhưng chưa gắn vào controller. |
| **ProjectRuntimeManagerService** | `IProjectRuntimeManagerService` -> `ProjectRuntimeManagerService` | Dòng 128: `AddSingleton` | `ProjectRuntimeController` | **Hợp lệ:** Quản lý process nền dài hạn cho terminal, inject `IHubContext<TerminalHub>` và `IServiceScopeFactory`. |
| **RuntimeService** | `IRuntimeService` -> `RuntimeService` | Dòng 94: `AddScoped` | `AgentOrchestrator`, `AgentTeamManager`, `AgentWorkflowService`, `DeploymentService`, `RuntimeController` | **Hợp lệ:** Thực thi shell/docker ngắn hạn đồng bộ. |
| **DiffService** | `IDiffService` -> `DiffService` | Dòng 62: `AddScoped` | `DiffReviewController`, `AgentTeamManager` | **Hợp lệ:** Sử dụng DiffPlex để sinh và duyệt diff. |
| **QdrantVectorDatabaseService** | `IVectorDatabaseService` -> `QdrantVectorDatabaseService` | Dòng 107: `AddSingleton` | `WorkspaceIndexingService`, `ContextRetrieverService` | **Hợp lệ:** Duy trì kết nối Qdrant gRPC client dài hạn. |
| **MediaGenerationConsumer** | MassTransit Consumer | Dòng 182: `AddConsumer` | RabbitMQ Bus Endpoint | **Tách rời:** Consumer lắng nghe hàng đợi nhưng frontend không kích hoạt luồng này. |

---

### 1.3. Kiểm tra Nguy cơ Lộ Secret / API Key / Password thật
Quét toàn bộ cây thư mục mã nguồn:

> [!CAUTION] CÁC NGUY CƠ LỘ THÔNG TIN BẢO MẬT ĐÃ PHÁT HIỆN
> - **`appsettings.json`**: Chứa giá trị thật tại key `Gemini:ApiKey` (định dạng `<REDACTED>`). Khi push lên GitHub repo Public sẽ bị Google Cloud & GitHub Secret Scanner phát hiện và vô hiệu hóa ngay lập tức.
> - **`appsettings.Production.json`**: Chứa mật khẩu cơ sở dữ liệu thật tại key `ConnectionStrings:DefaultConnection` (chứa chuỗi password plaintext của MySQL production).
> - **`Services/GeminiService.cs`**: Dòng 21 khai báo hằng số fallback chứa chuỗi base64 của Google Gemini API Key thật (`<REDACTED>`).
> - **`Services/ImageGenerationService.cs`**: Dòng 20 khai báo hằng số fallback chứa base64 của Gemini API Key thật.
> - **`Services/IntentEngineService.cs`**: Dòng 18 khai báo hằng số fallback chứa base64 của Gemini API Key thật.
> - **`Services/MusicGenerationService.cs`**: Dòng 20 khai báo hằng số fallback chứa base64 của Gemini API Key thật.
> 
> *Khuyến nghị:* Di chuyển toàn bộ API Key và Connection String sang Environment Variables (`.env` hoặc host environment settings) và chỉ commit các file mẫu như `appsettings.Example.json`.

---

### 1.4. Kiểm tra Cấu hình ForwardedHeaders & StaticFiles trong `Program.cs`

1. **`UseForwardedHeaders` (Cho Cloudflare Tunnel / Reverse Proxy `ai.btoan.com`):**
   - **Hiện trạng:** **CHƯA CẤU HÌNH** trong `Program.cs`.
   - **Rủi ro kỹ thuật:** Khi ứng dụng chạy trên Docker/Kestrel đứng sau Cloudflare Tunnel hoặc Nginx reverse proxy, Kestrel chỉ nhận thấy kết nối HTTP từ `127.0.0.1`. Thiếu `UseForwardedHeaders` sẽ gây ra:
     - `HttpContext.Connection.RemoteIpAddress` luôn là IP local của tunnel, làm sai lệch nhật ký bảo mật/đăng nhập của người dùng.
     - `Request.Scheme` nhận là `http` thay vì `https`, dẫn đến việc các redirect (như HTTPS redirection, OAuth callback của Google, cookies `SameSite=None; Secure`) bị từ chối hoặc tạo vòng lặp redirect (Redirect Loop).
   - **Khắc phục:** Cần bổ sung trước `app.UseRouting()`:
     ```csharp
     app.UseForwardedHeaders(new ForwardedHeadersOptions {
         ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
     });
     ```

2. **`StaticFileOptions` cho file model face-api không đuôi mở rộng (`-shard1`, `-shard2`):**
   - **Hiện trạng:** **ĐÃ CẤU HÌNH CHÍNH XÁC** tại `Program.cs` dòng 434–437:
     ```csharp
     var staticFileOptions = new StaticFileOptions {
         ServeUnknownFileTypes = true,
         DefaultContentType = "application/octet-stream"
     };
     app.UseStaticFiles(staticFileOptions);
     ```
   - **Đánh giá:** Kestrel cho phép tải các file manifest và shard nhị phân không có đuôi mở rộng (như `tiny_face_detector_model-shard1`, `face_landmark_68_model-shard1`, `face_recognition_model-shard1`) nằm trong `wwwroot/facelib/models/`.

---

### 1.5. Phân tích Trùng lặp Vai trò & Dead Code (Kiến trúc Backend)

#### 1. Cặp `DiffService` vs `CodeDiffService`
- **`DiffService.cs` (Kế thừa `IDiffService`):**
  - Sử dụng thư viện `DiffPlex` (`DiffPlex.DiffBuilder.InlineDiffBuilder`).
  - Hỗ trợ đầy đủ việc sinh inline diff, tính toán hunk thêm/sửa/xóa, và áp dụng patch trực tiếp vào file.
  - Được inject và hoạt động thực tế trong `Controllers/DiffReviewController.cs` và `Services/AgentTeamManager.cs`.
- **`CodeDiffService.cs` (Kế thừa `ICodeDiffService`):**
  - Tự viết logic tìm và thay thế khối code thô sơ bằng regex string split/replace.
  - Được đăng ký DI tại `Program.cs:64` nhưng **hoàn toàn không có bất kỳ Controller hay Service nào inject hay tham chiếu đến**.
  - **Kết luận:** `CodeDiffService` là **DEAD CODE 100%**.

#### 2. Cặp `RuntimeService` vs `ProjectRuntimeManagerService`
- **`RuntimeService.cs` (`IRuntimeService`, Scoped):**
  - Thực thi các lệnh shell / process đơn lẻ (ví dụ: `dotnet build`, `npm test`, `git status`) hoặc chạy trong Docker container tạm thời.
  - Đóng gói kết quả trả về `ExecutionResult` (ExitCode, Stdout, Stderr, IsSuccess) theo kiểu synchronous/short-lived.
  - Phục vụ cho các pipeline tự động của Agent: `AgentOrchestrator`, `AgentTeamManager`, `AgentWorkflowService`, `DeploymentService`, `RuntimeController`.
- **`ProjectRuntimeManagerService.cs` (`IProjectRuntimeManagerService`, Singleton):**
  - Quản lý các tiến trình chạy nền tương tác dài hạn (long-running background processes) như `npm run dev`, `dotnet watch run`, web server.
  - Sử dụng hàng đợi kênh bất đồng bộ `System.Threading.Channels.Channel` và truyền luồng output trực tiếp về trình duyệt thông qua SignalR `TerminalHub` (`Clients.Group(projectId).SendAsync("ReceiveOutput", ...)`).
  - Phục vụ độc quyền cho tính năng Terminal Web IDE trong `Controllers/ProjectRuntimeController.cs`.
- **Kết luận:** Hai service này **KHÔNG TRÙNG LẶP**, mà chia tách nhiệm vụ rõ ràng: một bên là Short-lived Batch Command Executor (`RuntimeService`), một bên là Long-lived Interactive Process Manager (`ProjectRuntimeManagerService`).

#### 3. Cặp `MediaController` vs `MediaAsyncController`
- **`MediaController.cs`:**
  - Nhận HTTP POST trực tiếp (`/Media/GenerateImage`, `/Media/GenerateMusic`, `/Media/GenerateVideo`), gọi đồng bộ tới `ImageGenerationService`, `MusicGenerationService`, `VideoGenerationService`, chờ Gemini/external AI sinh xong và trả về kết quả base64 / URL trong cùng request HTTP.
- **`MediaAsyncController.cs`:**
  - Nhận yêu cầu và gửi message `GenerateMediaCommand` vào RabbitMQ thông qua MassTransit (`IBus.Publish`), lập tức trả về `202 Accepted` kèm `jobId`. Consumer `MediaGenerationConsumer.cs` nhận message và xử lý nền.
- **Kết luận:** `MediaAsyncController` là kiến trúc EDA (Event-Driven Architecture) rất chuẩn, nhưng **bị cô lập (Orphan/Disconnected)** vì toàn bộ Frontend JS (`app.js`) hiện vẫn gọi đồng bộ sang các endpoint kiểu PHP cũ hoặc controller đồng bộ, không hề có cơ chế lưu trữ kết quả vào Database kèm SignalR notify để client lấy lại kết quả của job bất đồng bộ.

#### 4. Cụm 5 Service trong họ `Agent*`
1. `AgentService.cs`: Service đời đầu (legacy), gọi trực tiếp Gemini với prompt đơn giản để sửa file hoặc giải thích code, không có khái niệm workspace đa file.
2. `AgentOrchestrator.cs`: Orchestrator chính cho Agent cá nhân (Single Agent). Có cơ chế lập kế hoạch (Plan Generation), phân tích ngữ cảnh dự án, sinh `CodeChange`, và áp dụng thay đổi.
3. `AgentTeamManager.cs`: Orchestrator cho Multi-Agent (Team kiến trúc gồm Architect, Coder, Reviewer). Điều phối thảo luận nội bộ giữa các agent, duyệt diff, và xuất ra bản vá code hoàn chỉnh.
4. `AgentWorkflowService.cs`: Quản lý quy trình làm việc theo chuỗi bước tuần tự (Pipeline/State Machine: Analyze -> Design -> Code -> Review -> Test -> Deploy).
5. `AgentToolLoopOrchestrator.cs`: Engine lặp Function Calling động (Agent gọi Tool -> Hệ thống chạy Tool -> Trả kết quả cho Agent -> Lặp lại đến khi kết thúc). Được đăng ký trong DI nhưng chưa được nối vào endpoint nào.
- **Kết luận:** Có sự chồng chéo giữa `AgentService` (cũ) và `AgentOrchestrator` (mới). `AgentToolLoopOrchestrator` đang ở trạng thái phát triển dở dang chưa tích hợp vào luồng chính.

---

## PHẦN 2: TOÀN VĂN CÁC FILE CẤU HÌNH & HẠ TẦNG CỐT LÕI (ĐÃ CHE SECRET)

### 2.1. `BtoanAiNet.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MassTransit" Version="8.3.6" />
    <PackageReference Include="MassTransit.RabbitMQ" Version="8.3.6" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="9.0.0" />
    <PackageReference Include="Pomelo.EntityFrameworkCore.MySql" Version="9.0.0" />
  </ItemGroup>

</Project>

```

### 2.2. `Program.cs`
```csharp
using BtoanAiNet.Data;
using BtoanAiNet.Repositories;
using BtoanAiNet.Services;
using BtoanAiNet.Services.VectorDb;
using BtoanAiNet.Messaging.Consumers;
using BtoanAiNet.FunctionCalling.Services;
using BtoanAiNet.FunctionCalling.Tools;
using MassTransit;
using Microsoft.EntityFrameworkCore;

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
            options.UseSqlServer(connectionString));
    }
    else
    {
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 30))));
    }
}

builder.Services.AddHttpClient();

// Register Repositories
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();
builder.Services.AddScoped<ITrainingRepository, TrainingRepository>();

// Register Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IFaceAuthService, FaceAuthService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddScoped<IGeminiService, GeminiService>();
builder.Services.AddScoped<IIntentEngineService, IntentEngineService>();
builder.Services.AddScoped<IMemoryService, MemoryService>();
builder.Services.AddScoped<ITrainingService, TrainingService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IImageGenerationService, ImageGenerationService>();
builder.Services.AddScoped<IVideoGenerationService, VideoGenerationService>();
builder.Services.AddScoped<IMusicGenerationService, MusicGenerationService>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddScoped<ICodeAnalysisService, CodeAnalysisService>();
builder.Services.AddScoped<IProjectScannerService, ProjectScannerService>();
builder.Services.AddScoped<IFileEditorService, FileEditorService>();
builder.Services.AddScoped<ICodeDiffService, CodeDiffService>();
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
        }
        catch (Exception initEx)
        {
            Console.WriteLine($"DB EnsureCreated Warning: {initEx.Message}");
        }

        string sql = @"
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Projects]') AND name = 'UserId')
BEGIN
    DROP TABLE [dbo].[Projects];
END

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[ProjectFiles]') AND name = 'ProjectId')
BEGIN
    DROP TABLE [dbo].[ProjectFiles];
END

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[FileVersions]') AND name = 'ProjectFileId')
BEGIN
    DROP TABLE [dbo].[FileVersions];
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
    }
    catch (Exception ex)
    {
        Console.WriteLine($"DB AutoInit Warning: {ex.Message}");
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Enable serving face-api model shard files (files without extension)
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true
});

app.Use((context, next) =>
{
    context.Request.EnableBuffering();
    return next();
});

app.UseRouting();

app.UseSession();
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

app.Run();

```

### 2.3. `appsettings.json`
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "DbProvider": "SqlServer",
  "ConnectionStrings": {
    "DefaultConnection": "Server=(local);Database=btoan_ai;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true;",
    "SqlServerConnection": "Server=(local);Database=btoan_ai;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true;",
    "MySqlConnection": "Server=127.0.0.1;Database=btoan_ai;User=root;Password=;CharSet=utf8mb4;"
  },
  "Gemini": {
    "ApiKey": "<REDACTED>"
  },
  "DockerSandbox": {
    "DefaultImage": "mcr.microsoft.com/dotnet/sdk:10.0-alpine",
    "MemoryLimit": "512m",
    "CpuLimit": 1.0,
    "PidsLimit": 64,
    "ExecutionTimeoutSeconds": 45,
    "AllowNetworkAccess": false,
    "FallbackToRestrictedHost": true
  },
  "Qdrant": {
    "Endpoint": "http://localhost:6333",
    "ApiKey": ""
  },
  "RabbitMQ": {
    "Host": "",
    "User": "guest",
    "Pass": "guest"
  },
  "AllowedHosts": "*"
}

```

### 2.4. `appsettings.Example.json`
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "DbProvider": "SqlServer",
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=btoan_ai;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true;",
    "SqlServerConnection": "Server=localhost;Database=btoan_ai;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true;",
    "MySqlConnection": "Server=127.0.0.1;Database=btoan_ai;User=root;Password=<REDACTED>;CharSet=utf8mb4;"
  },
  "Gemini": {
    "ApiKey": "YOUR_GEMINI_API_KEY_HERE"
  },
  "DockerSandbox": {
    "DefaultImage": "mcr.microsoft.com/dotnet/sdk:10.0-alpine",
    "MemoryLimit": "512m",
    "CpuLimit": 1.0,
    "PidsLimit": 64,
    "ExecutionTimeoutSeconds": 45,
    "AllowNetworkAccess": false,
    "FallbackToRestrictedHost": true
  },
  "Qdrant": {
    "Endpoint": "http://localhost:6333",
    "ApiKey": ""
  },
  "RabbitMQ": {
    "Host": "",
    "User": "guest",
    "Pass": "guest"
  },
  "AllowedHosts": "*"
}


```

### 2.5. `Dockerfile`
```dockerfile
# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["BtoanAiNet.csproj", "./"]
RUN dotnet restore "BtoanAiNet.csproj"

# Copy all project files and build
COPY . .
RUN dotnet publish "BtoanAiNet.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Install bash, curl and git for terminal and workspace operations
RUN apt-get update && apt-get install -y curl git && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Expose ports (Render, Koyeb, HF default to 8080 or PORT env)
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "BtoanAiNet.dll"]

```

### 2.6. `render.yaml`
```yaml
services:
  - type: web
    name: btoan-ai-net
    env: docker
    dockerfilePath: ./Dockerfile
    plan: free
    region: singapore # or oregon / frankfurt
    envVars:
      - key: ASPNETCORE_ENVIRONMENT
        value: Production
      - key: ASPNETCORE_URLS
        value: http://+:8080
      - key: DbProvider
        value: MySql
      - key: ConnectionStrings__DefaultConnection
        sync: false # Set via dashboard to keep secret

```

### 2.7. `Data/AppDbContext.cs`
```csharp
using BtoanAiNet.Models;
using Microsoft.EntityFrameworkCore;

namespace BtoanAiNet.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Conversation> Conversations { get; set; } = null!;
        public DbSet<Chat> Chats { get; set; } = null!;
        public DbSet<UserMemory> UserMemories { get; set; } = null!;
        public DbSet<AiTraining> AiTrainings { get; set; } = null!;
        public DbSet<Message> Messages { get; set; } = null!;
        public DbSet<Project> Projects { get; set; } = null!;
        public DbSet<ProjectFile> ProjectFiles { get; set; } = null!;
        public DbSet<FileVersion> FileVersions { get; set; } = null!;
        public DbSet<TerminalSession> TerminalSessions { get; set; } = null!;
        public DbSet<AgentTask> AgentTasks { get; set; } = null!;
        public DbSet<AgentMessage> AgentMessages { get; set; } = null!;
        public DbSet<AgentChange> AgentChanges { get; set; } = null!;
        public DbSet<ProjectVersion> ProjectVersions { get; set; } = null!;
        public DbSet<FileSnapshot> FileSnapshots { get; set; } = null!;
        public DbSet<FileChangeHistory> FileChangeHistories { get; set; } = null!;
        public DbSet<AutonomousAgentTask> AutonomousAgentTasks { get; set; } = null!;
        public DbSet<ProjectMemory> ProjectMemories { get; set; } = null!;
        public DbSet<CodeChange> CodeChanges { get; set; } = null!;
        public DbSet<TeamAgentTask> TeamAgentTasks { get; set; } = null!;
        public DbSet<AgentTeamMessage> AgentTeamMessages { get; set; } = null!;
        public DbSet<ProjectKnowledgeBase> ProjectKnowledgeBases { get; set; } = null!;
        public DbSet<Deployment> Deployments { get; set; } = null!;
        public DbSet<ProjectEnvironmentVariable> ProjectEnvironmentVariables { get; set; } = null!;
        public DbSet<DeploymentLog> DeploymentLogs { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(e => e.Username).IsUnique();
            });

            modelBuilder.Entity<Project>(entity =>
            {
                entity.HasIndex(e => e.UserId);
                entity.HasMany(e => e.Files)
                      .WithOne(f => f.Project)
                      .HasForeignKey(f => f.ProjectId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ProjectFile>(entity =>
            {
                entity.HasIndex(e => new { e.ProjectId, e.RelativePath }).IsUnique();
                entity.HasMany(e => e.Versions)
                      .WithOne(v => v.ProjectFile)
                      .HasForeignKey(v => v.ProjectFileId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<FileVersion>(entity =>
            {
                entity.HasIndex(e => new { e.ProjectFileId, e.VersionNumber });
            });
        }
    }
}

```

---

## PHẦN 3: TOÀN VĂN CÁC FILE LUỒNG TRỌNG YẾU (ĐÃ CHE SECRET)

### 3.1. Luồng Face Biometrics: `Controllers/AccountController.cs`
```csharp
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

        public AccountController(
            IAuthService authService,
            IFaceAuthService faceAuthService,
            IGoogleAuthService googleAuthService,
            IUserRepository userRepository)
        {
            _authService = authService;
            _faceAuthService = faceAuthService;
            _googleAuthService = googleAuthService;
            _userRepository = userRepository;
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
                var user = await _userRepository.GetByUsernameAsync(username.Trim());
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

            try
            {
                bool success = await _authService.UpdateNameAsync(model.User_Id.Value, model.Full_Name ?? "", model.Dob ?? "");
                return Json(new { success });
            }
            catch
            {
                return Json(new { success = false });
            }
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
                var user = await _userRepository.GetByUsernameAsync(username.Trim());
                if (user == null || user.FaceEnabled != 1 || string.IsNullOrEmpty(user.FaceDescriptor))
                {
                    return Json(new { success = false, message = "Chưa đăng ký FaceID" });
                }

                return Json(new
                {
                    success = true,
                    username = user.Username,
                    face_descriptor = user.FaceDescriptor,
                    face_image_url = user.FaceImageUrl
                });
            }
            catch
            {
                return Json(new { success = false, message = "Lỗi truy vấn dữ liệu" });
            }
        }
    }
}

```

### 3.2. Luồng Face Biometrics: `Services/FaceAuthService.cs`
```csharp
using System;
using System.Text.Json;

namespace BtoanAiNet.Services
{
    public interface IFaceAuthService
    {
        (bool IsMatch, double Distance, string Message) VerifyFace(string rawLiveDescriptor, string rawStoredDescriptor);
    }

    public class FaceAuthService : IFaceAuthService
    {
        private const double StrictThreshold = 0.45;

        public (bool IsMatch, double Distance, string Message) VerifyFace(string rawLiveDescriptor, string rawStoredDescriptor)
        {
            if (string.IsNullOrWhiteSpace(rawLiveDescriptor) || string.IsNullOrWhiteSpace(rawStoredDescriptor))
            {
                return (false, 1.0, "Dữ liệu sinh trắc học không hợp lệ");
            }

            try
            {
                var live = JsonSerializer.Deserialize<double[]>(rawLiveDescriptor);
                var stored = JsonSerializer.Deserialize<double[]>(rawStoredDescriptor);

                if (live == null || stored == null || live.Length == 0 || live.Length != stored.Length)
                {
                    return (false, 1.0, "Dữ liệu sinh trắc học không hợp lệ");
                }

                double sum = 0.0;
                for (int i = 0; i < live.Length; i++)
                {
                    sum += Math.Pow(live[i] - stored[i], 2);
                }

                double distance = Math.Sqrt(sum);

                if (distance > StrictThreshold)
                {
                    return (false, distance, "Xác minh khuôn mặt thất bại! Bạn không phải chủ sở hữu tài khoản này.");
                }

                return (true, distance, "Đăng nhập khuôn mặt thành công");
            }
            catch
            {
                return (false, 1.0, "Dữ liệu sinh trắc học không hợp lệ");
            }
        }
    }
}

```

### 3.3. Luồng Chat AI: `Controllers/ChatController.cs`
```csharp
using BtoanAiNet.Repositories;
using BtoanAiNet.Services;
using BtoanAiNet.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BtoanAiNet.Controllers
{
    public class ChatController : Controller
    {
        private readonly IChatService _chatService;
        private readonly IConversationRepository _conversationRepository;
        private readonly IMemoryService _memoryService;
        private readonly ITrainingService _trainingService;
        private readonly IWebHostEnvironment _env;

        public ChatController(
            IChatService chatService,
            IConversationRepository conversationRepository,
            IMemoryService memoryService,
            ITrainingService trainingService,
            IWebHostEnvironment env)
        {
            _chatService = chatService;
            _conversationRepository = conversationRepository;
            _memoryService = memoryService;
            _trainingService = trainingService;
            _env = env;
        }

        // POST /ai/api/chat.php
        [HttpPost]
        [Route("ai/api/chat.php")]
        [Route("Chat/Send")]
        public async Task<IActionResult> Chat([FromBody] ChatRequestModel model)
        {
            if (model == null)
            {
                return Json(new { ok = false, error = "Invalid request payload" });
            }

            try
            {
                string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var result = await _chatService.ProcessChatAsync(model, webRootPath);
                return Content(result.ToJsonString(), "application/json");
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // GET /ai/api/get_conversations.php
        [HttpGet]
        [Route("ai/api/get_conversations.php")]
        [Route("Chat/GetConversations")]
        public async Task<IActionResult> GetConversations([FromQuery] int user_id)
        {
            try
            {
                var conversations = await _conversationRepository.GetConversationsByUserIdAsync(user_id);
                var result = conversations.Select(c => new
                {
                    id = c.Id,
                    title = c.Title
                });

                return Json(result);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // GET /ai/api/get_messages.php
        [HttpGet]
        [Route("ai/api/get_messages.php")]
        [Route("Chat/GetMessages")]
        public async Task<IActionResult> GetMessages([FromQuery] int conversation_id)
        {
            if (conversation_id == 0)
            {
                return Json(new List<object>());
            }

            try
            {
                var chats = await _conversationRepository.GetMessagesByConversationIdAsync(conversation_id);
                var data = new List<object>();

                foreach (var chat in chats)
                {
                    if (!string.IsNullOrEmpty(chat.Question))
                    {
                        List<string> images = new List<string>();
                        if (!string.IsNullOrEmpty(chat.Images))
                        {
                            try
                            {
                                var decoded = System.Text.Json.JsonSerializer.Deserialize<List<string>>(chat.Images);
                                if (decoded != null) images = decoded;
                            }
                            catch { }
                        }

                        data.Add(new
                        {
                            role = "user",
                            text = chat.Question,
                            images = images
                        });
                    }

                    if (!string.IsNullOrEmpty(chat.Answer))
                    {
                        data.Add(new
                        {
                            role = "assistant",
                            text = chat.Answer,
                            images = new List<string>(),
                            videos = (object?)null,
                            music_url = (string?)null,
                            music_prompt = (string?)null,
                            music_duration = (int?)null
                        });
                    }
                }

                return Json(data);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // POST /ai/api/rename_conversation.php
        [HttpPost]
        [Route("ai/api/rename_conversation.php")]
        [Route("Chat/RenameConversation")]
        public async Task<IActionResult> RenameConversation([FromBody] RenameConversationRequestModel model)
        {
            if (model == null || model.Id == 0)
            {
                return Json(new { status = "error", message = "missing id" });
            }

            try
            {
                string title = model.Title?.Trim() ?? "Chat mới";
                await _conversationRepository.UpdateConversationTitleAsync(model.Id, title);

                return Json(new { status = "ok", title = title });
            }
            catch
            {
                return Json(new { status = "error", message = "db error" });
            }
        }

        // POST /ai/api/delete_conversation.php
        [HttpPost]
        [Route("ai/api/delete_conversation.php")]
        [Route("Chat/DeleteConversation")]
        public async Task<IActionResult> DeleteConversation([FromBody] DeleteConversationRequestModel model)
        {
            if (model == null || model.Id == 0)
            {
                return Json(new { error = "no data" });
            }

            try
            {
                await _conversationRepository.DeleteConversationAsync(model.Id);
                return Json(new { status = "deleted" });
            }
            catch
            {
                return Json(new { error = "db error" });
            }
        }

        // GET /ai/api/get_memory.php
        [HttpGet]
        [Route("ai/api/get_memory.php")]
        [Route("Chat/GetMemory")]
        public async Task<IActionResult> GetMemory([FromQuery] int user_id)
        {
            try
            {
                var memories = await _memoryService.GetMemoryByUserIdAsync(user_id);
                var result = memories.Select(m => new
                {
                    id = m.Id,
                    user_id = m.UserId,
                    memory_value = m.MemoryValue
                });

                return Json(result);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // POST /ai/api/save_memory.php
        [HttpPost]
        [Route("ai/api/save_memory.php")]
        [Route("Chat/SaveMemory")]
        public async Task<IActionResult> SaveMemory([FromBody] SaveMemoryRequestModel model)
        {
            if (model == null || model.User_Id == 0 || string.IsNullOrWhiteSpace(model.Memory))
            {
                return Json(new { status = "error", message = "missing data" });
            }

            try
            {
                await _memoryService.SaveMemoryAsync(model.User_Id, model.Memory.Trim());
                return Json(new { status = "ok" });
            }
            catch
            {
                return Json(new { status = "error", message = "db error" });
            }
        }

        // GET /ai/api/get_training.php
        [HttpGet]
        [Route("ai/api/get_training.php")]
        [Route("Chat/GetTraining")]
        public async Task<IActionResult> GetTraining([FromQuery] string q)
        {
            try
            {
                var trainings = await _trainingService.SearchTrainingAsync(q ?? "");
                var result = trainings.Select(t => new
                {
                    id = t.Id,
                    question = t.Question,
                    correct_answer = t.CorrectAnswer
                });

                return Json(result);
            }
            catch
            {
                return Json(new List<object>());
            }
        }

        // POST /ai/api/save_training.php
        [HttpPost]
        [Route("ai/api/save_training.php")]
        [Route("Chat/SaveTraining")]
        public async Task<IActionResult> SaveTraining([FromBody] SaveTrainingRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Question))
            {
                return Json(new { status = "error", message = "missing data" });
            }

            try
            {
                await _trainingService.SaveTrainingAsync(model.Question, model.Ai_Answer ?? "", model.Correct_Answer ?? "", model.User_Id);
                return Json(new { status = "ok" });
            }
            catch
            {
                return Json(new { status = "error", message = "db error" });
            }
        }
    }
}

```

### 3.4. Luồng Điều phối Agent: `Controllers/AgentController.cs`
```csharp
using BtoanAiNet.Services;
using BtoanAiNet.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BtoanAiNet.Controllers
{
    public class AgentController : Controller
    {
        private readonly IAgentService _agentService;
        private readonly IProjectScannerService _scannerService;
        private readonly IFileEditorService _editorService;
        private readonly IWorkspaceService _workspaceService;
        private readonly ITerminalExecutionService _terminalService;
        private readonly IAgentWorkflowService _workflowService;
        private readonly IWorkspaceEditService _editService;
        private readonly IWebHostEnvironment _env;

        public AgentController(
            IAgentService agentService,
            IProjectScannerService scannerService,
            IFileEditorService editorService,
            IWorkspaceService workspaceService,
            ITerminalExecutionService terminalService,
            IAgentWorkflowService workflowService,
            IWorkspaceEditService editService,
            IWebHostEnvironment env)
        {
            _agentService = agentService;
            _scannerService = scannerService;
            _editorService = editorService;
            _workspaceService = workspaceService;
            _terminalService = terminalService;
            _workflowService = workflowService;
            _editService = editService;
            _env = env;
        }

        // GET /agent
        [HttpGet("agent")]
        public IActionResult Index()
        {
            return View();
        }

        // GET /ai/api/agent/projects
        [HttpGet]
        [Route("ai/api/agent/projects")]
        public async Task<IActionResult> GetProjects([FromQuery] int user_id)
        {
            try
            {
                var projects = await _workspaceService.GetUserProjectsAsync(user_id);
                return Json(new { ok = true, projects });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // DELETE /ai/api/agent/projects/{id}
        [HttpDelete]
        [Route("ai/api/agent/projects/{id}")]
        public async Task<IActionResult> DeleteProject(int id, [FromQuery] int user_id)
        {
            try
            {
                string webRoot = GetWebRoot();
                bool deleted = await _workspaceService.DeleteProjectAsync(id, user_id, webRoot);
                return Json(new { ok = deleted, message = deleted ? "Project deleted successfully" : "Project not found" });
            }
            catch (UnauthorizedAccessException uex)
            {
                return Json(new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // GET /ai/api/agent/projects/{id}/export-zip
        [HttpGet]
        [Route("ai/api/agent/projects/{id}/export-zip")]
        public async Task<IActionResult> ExportZip(int id, [FromQuery] int user_id)
        {
            try
            {
                string webRoot = GetWebRoot();
                var bytes = await _workspaceService.ExportProjectAsZipAsync(id, user_id, webRoot);
                return File(bytes, "application/zip", $"Project_{id}_Workspace.zip");
            }
            catch (UnauthorizedAccessException uex)
            {
                return StatusCode(403, new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/projects/create
        [HttpPost]
        [Route("ai/api/agent/projects/create")]
        public async Task<IActionResult> CreateProject([FromBody] CreateProjectRequestModel model)
        {
            if (model == null) return Json(new { ok = false, error = "Invalid model" });
            try
            {
                string webRoot = GetWebRoot();
                var project = await _workspaceService.CreateProjectAsync(model.UserId, model.Name, model.Template, webRoot);
                return Json(new { ok = true, project });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/projects/upload-zip
        [HttpPost]
        [Route("ai/api/agent/projects/upload-zip")]
        public async Task<IActionResult> UploadZip([FromForm] int userId, [FromForm] string name, IFormFile file)
        {
            if (file == null || file.Length == 0) return Json(new { ok = false, error = "No file uploaded" });
            try
            {
                string webRoot = GetWebRoot();
                using var stream = file.OpenReadStream();
                var project = await _workspaceService.CreateProjectFromZipAsync(userId, name, stream, webRoot);
                return Json(new { ok = true, project });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/projects/import-git
        [HttpPost]
        [Route("ai/api/agent/projects/import-git")]
        public async Task<IActionResult> ImportGit([FromBody] ImportGitRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.GitUrl)) return Json(new { ok = false, error = "Git URL is required" });
            try
            {
                string webRoot = GetWebRoot();
                var project = await _workspaceService.CreateProjectFromGitAsync(model.UserId, model.Name, model.GitUrl, webRoot);
                return Json(new { ok = true, project });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // GET /ai/api/agent/tree
        [HttpGet]
        [Route("ai/api/agent/tree")]
        public async Task<IActionResult> GetTree([FromQuery] int projectId, [FromQuery] int userId = 0)
        {
            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(projectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                string webRoot = GetWebRoot();
                string targetDir = await _workspaceService.GetProjectStoragePathAsync(projectId, webRoot);
                var tree = await _scannerService.GetProjectTreeAsync(targetDir);
                return Json(new { ok = true, tree });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // GET /ai/api/agent/file
        [HttpGet]
        [Route("ai/api/agent/file")]
        public async Task<IActionResult> GetFile([FromQuery] int projectId, [FromQuery] string path, [FromQuery] int userId = 0)
        {
            if (string.IsNullOrWhiteSpace(path)) return Json(new { ok = false, error = "Path is required" });

            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(projectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                string webRoot = GetWebRoot();
                string targetDir = await _workspaceService.GetProjectStoragePathAsync(projectId, webRoot);
                string content = await _editorService.ReadFileContentAsync(targetDir, path);
                return Json(new { ok = true, projectId, path, content });
            }
            catch (UnauthorizedAccessException uex)
            {
                return BadRequest(new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/file/save
        [HttpPost]
        [Route("ai/api/agent/file/save")]
        public async Task<IActionResult> SaveFile([FromBody] SaveWorkspaceFileRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Path)) return Json(new { ok = false, error = "Invalid model" });

            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(model.ProjectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                string webRoot = GetWebRoot();
                await _workspaceService.SaveFileWithVersionAsync(model.ProjectId, model.Path, model.Content ?? "", "USER", "User manual save", webRoot);
                return Json(new { ok = true, message = "File saved and version recorded" });
            }
            catch (UnauthorizedAccessException uex)
            {
                return BadRequest(new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/file/create-item
        [HttpPost]
        [Route("ai/api/agent/file/create-item")]
        public async Task<IActionResult> CreateItem([FromBody] CreateItemRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Path)) return Json(new { ok = false, error = "Path is required" });
            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(model.ProjectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                string webRoot = GetWebRoot();
                await _workspaceService.CreateItemAsync(model.ProjectId, model.Path, model.IsDirectory, webRoot);
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/file/rename-item
        [HttpPost]
        [Route("ai/api/agent/file/rename-item")]
        public async Task<IActionResult> RenameItem([FromBody] RenameItemRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.OldPath) || string.IsNullOrWhiteSpace(model.NewPath))
                return Json(new { ok = false, error = "Old and new paths are required" });
            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(model.ProjectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                string webRoot = GetWebRoot();
                await _workspaceService.RenameItemAsync(model.ProjectId, model.OldPath, model.NewPath, webRoot);
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/file/delete-item
        [HttpPost]
        [Route("ai/api/agent/file/delete-item")]
        public async Task<IActionResult> DeleteItem([FromBody] DeleteItemRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Path)) return Json(new { ok = false, error = "Path is required" });
            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(model.ProjectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                string webRoot = GetWebRoot();
                await _workspaceService.DeleteItemAsync(model.ProjectId, model.Path, webRoot);
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/terminal/execute
        [HttpPost]
        [Route("ai/api/agent/terminal/execute")]
        public async Task<IActionResult> ExecuteTerminalCommand([FromBody] ExecuteTerminalRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Command))
                return Json(new { ok = false, error = "Command is required" });
            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(model.ProjectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                string webRoot = GetWebRoot();
                string targetDir = await _workspaceService.GetProjectStoragePathAsync(model.ProjectId, webRoot);
                var result = await _terminalService.ExecuteCommandAsync(targetDir, model.Command);
                return Json(new { ok = true, result });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // GET /ai/api/agent/file/versions
        [HttpGet]
        [Route("ai/api/agent/file/versions")]
        public async Task<IActionResult> GetFileVersions([FromQuery] int projectId, [FromQuery] string path, [FromQuery] int userId = 0)
        {
            try
            {
                if (!await _workspaceService.ValidateProjectOwnershipAsync(projectId, userId))
                    return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

                var versions = await _workspaceService.GetFileVersionHistoryAsync(projectId, path);
                var result = versions.Select(v => new
                {
                    id = v.Id,
                    versionNumber = v.VersionNumber,
                    createdBy = v.CreatedBy,
                    changeSummary = v.ChangeSummary,
                    createdAt = v.CreatedAt.ToString("HH:mm:ss dd/MM/yyyy")
                });
                return Json(new { ok = true, versions = result });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/file/rollback
        [HttpPost]
        [Route("ai/api/agent/file/rollback")]
        public async Task<IActionResult> Rollback([FromBody] RollbackRequestModel model, [FromQuery] int userId = 0)
        {
            try
            {
                string webRoot = GetWebRoot();
                bool success = await _workspaceService.RollbackFileVersionAsync(model.VersionId, webRoot);
                return Json(new { ok = success });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // POST /ai/api/agent/analyze
        [HttpPost]
        [Route("ai/api/agent/analyze")]
        public async Task<IActionResult> Analyze([FromBody] AgentTaskRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Prompt))
                return BadRequest(new { ok = false, error = "Yêu cầu không hợp lệ" });

            if (!await _workspaceService.ValidateProjectOwnershipAsync(model.ProjectId, userId))
                return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

            string webRoot = GetWebRoot();
            var response = await _agentService.AnalyzeAndProposeAsync(model, webRoot);
            return Ok(response);
        }

        // POST /ai/api/agent/apply
        [HttpPost]
        [Route("ai/api/agent/apply")]
        public async Task<IActionResult> Apply([FromBody] AgentApplyRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || model.ApprovedProposals == null)
                return BadRequest(new { ok = false, error = "Không có thay đổi nào được duyệt" });

            if (!await _workspaceService.ValidateProjectOwnershipAsync(model.ProjectId, userId))
                return StatusCode(403, new { ok = false, error = "Access Denied: You do not own this project." });

            string webRoot = GetWebRoot();
            var response = await _agentService.ApplyAndBuildAsync(model, webRoot);
            return Ok(response);
        }

        // POST /api/agent/task/create
        [HttpPost]
        [Route("api/agent/task/create")]
        public async Task<IActionResult> CreateWorkflowTask([FromBody] AgentTaskRequestModel model, [FromQuery] int userId = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Prompt))
                return BadRequest(new { ok = false, error = "Prompt and ProjectId are required" });

            try
            {
                string webRoot = GetWebRoot();
                var task = await _workflowService.CreateTaskAndAnalyzeAsync(model.ProjectId, model.Prompt, userId, webRoot);
                return Ok(new
                {
                    ok = true,
                    taskId = task.Id,
                    projectId = task.ProjectId,
                    status = task.Status,
                    summary = task.Summary,
                    createdAt = task.CreatedAt
                });
            }
            catch (UnauthorizedAccessException uex)
            {
                return StatusCode(403, new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // GET /api/agent/task/{id}
        [HttpGet]
        [Route("api/agent/task/{id}")]
        public async Task<IActionResult> GetTaskDetails(int id, [FromQuery] int userId = 0)
        {
            try
            {
                var task = await _workflowService.GetTaskDetailsAsync(id, userId);
                if (task == null) return NotFound(new { ok = false, error = "Task not found" });

                return Ok(new
                {
                    ok = true,
                    taskId = task.Id,
                    projectId = task.ProjectId,
                    prompt = task.Prompt,
                    status = task.Status,
                    summary = task.Summary,
                    createdAt = task.CreatedAt,
                    completedAt = task.CompletedAt,
                    messages = task.Messages.Select(m => new { m.Role, m.Content, m.CreatedAt }),
                    changesCount = task.Changes.Count
                });
            }
            catch (UnauthorizedAccessException uex)
            {
                return StatusCode(403, new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // GET /api/agent/task/{id}/changes
        [HttpGet]
        [Route("api/agent/task/{id}/changes")]
        public async Task<IActionResult> GetTaskChanges(int id, [FromQuery] int userId = 0)
        {
            try
            {
                var changes = await _workflowService.GetTaskChangesAsync(id, userId);
                return Ok(new
                {
                    ok = true,
                    changes = changes.Select(c => new
                    {
                        c.Id,
                        file = c.FilePath,
                        oldContent = c.OldContent,
                        newContent = c.NewContent,
                        changeType = c.ChangeType
                    })
                });
            }
            catch (UnauthorizedAccessException uex)
            {
                return StatusCode(403, new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/agent/task/{id}/approve
        [HttpPost]
        [Route("api/agent/task/{id}/approve")]
        public async Task<IActionResult> ApproveTaskChanges(int id, [FromQuery] int userId = 0)
        {
            try
            {
                string webRoot = GetWebRoot();
                bool applied = await _editService.ApplyChangesAsync(id, userId, webRoot);
                if (!applied) return BadRequest(new { ok = false, error = "Failed to apply changes" });

                // Run Build Loop Automatically
                bool buildOk = await _workflowService.ProcessBuildLoopAsync(id, userId, webRoot);

                return Ok(new
                {
                    ok = true,
                    message = buildOk ? "Changes applied and build succeeded!" : "Changes applied but build failed.",
                    buildSucceeded = buildOk
                });
            }
            catch (UnauthorizedAccessException uex)
            {
                return StatusCode(403, new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // POST /api/agent/task/{id}/reject
        [HttpPost]
        [Route("api/agent/task/{id}/reject")]
        public async Task<IActionResult> RejectTaskChanges(int id, [FromQuery] int userId = 0)
        {
            try
            {
                bool rejected = await _editService.RejectChangesAsync(id, userId);
                return Ok(new { ok = true, message = "Task changes rejected." });
            }
            catch (UnauthorizedAccessException uex)
            {
                return StatusCode(403, new { ok = false, error = uex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        private string GetWebRoot()
        {
            return _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }
    }
}

```

### 3.5. Luồng Quản lý File: `Controllers/FileController.cs`
```csharp
using BtoanAiNet.Services;
using BtoanAiNet.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BtoanAiNet.Controllers
{
    public class FileController : Controller
    {
        private readonly IStorageService _storageService;
        private readonly IWebHostEnvironment _env;

        public FileController(IStorageService storageService, IWebHostEnvironment env)
        {
            _storageService = storageService;
            _env = env;
        }

        // POST /ai/api/upload.php
        [HttpPost]
        [Route("ai/api/upload.php")]
        [Route("File/Upload")]
        public async Task<IActionResult> Upload([FromBody] FileUploadRequestModel model)
        {
            if (model == null) return Json(new { error = "no content" });

            string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var result = await _storageService.ExportFileAsync(model, webRootPath);
            return Content(result.ToJsonString(), "application/json");
        }

        // GET /ai/api/download_proxy.php
        [HttpGet]
        [Route("ai/api/download_proxy.php")]
        [Route("File/DownloadProxy")]
        public async Task<IActionResult> DownloadProxy([FromQuery] string url, [FromQuery] string filename = "downloaded_file")
        {
            if (string.IsNullOrEmpty(url)) return BadRequest("Missing URL");

            var (stream, contentType, downloadFilename) = await _storageService.GetDownloadProxyStreamAsync(url, filename);
            if (stream == null) return NotFound("Could not open source file.");

            return File(stream, contentType, downloadFilename);
        }
    }
}

```

### 3.6. Luồng Quản lý File: `Services/FileService.cs`
```csharp
using System.Text;

namespace BtoanAiNet.Services
{
    public interface IFileService
    {
        Task<List<string>> GetProjectFilesAsync(string projectPath);
        Task<string> ReadFileAsync(string filePath);
        Task WriteFileAsync(string filePath, string content);
        bool FileExists(string filePath);
        string ComputeDiff(string originalText, string newText, string fileName);
    }

    public class FileService : IFileService
    {
        private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".idea", "node_modules", "wwwroot/lib"
        };

        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".cshtml", ".json", ".css", ".js", ".md", ".txt", ".csproj", ".sql"
        };

        public Task<List<string>> GetProjectFilesAsync(string projectPath)
        {
            var result = new List<string>();
            if (!Directory.Exists(projectPath)) return Task.FromResult(result);

            ScanDirectory(projectPath, projectPath, result);
            return Task.FromResult(result);
        }

        private void ScanDirectory(string rootPath, string currentPath, List<string> result)
        {
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                var dirName = Path.GetFileName(dir);
                if (ExcludedDirectories.Contains(dirName) || dirName.StartsWith('.'))
                    continue;

                ScanDirectory(rootPath, dir, result);
            }

            foreach (var file in Directory.GetFiles(currentPath))
            {
                var ext = Path.GetExtension(file);
                if (AllowedExtensions.Contains(ext))
                {
                    var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                    result.Add(relativePath);
                }
            }
        }

        public async Task<string> ReadFileAsync(string filePath)
        {
            if (!File.Exists(filePath)) return string.Empty;
            return await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        }

        public async Task WriteFileAsync(string filePath, string content)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
        }

        public bool FileExists(string filePath)
        {
            return File.Exists(filePath);
        }

        public string ComputeDiff(string originalText, string newText, string fileName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- a/{fileName}");
            sb.AppendLine($"+++ b/{fileName}");

            var origLines = originalText.Split('\n');
            var newLines = newText.Split('\n');

            int max = Math.Max(origLines.Length, newLines.Length);
            for (int i = 0; i < max; i++)
            {
                string origLine = i < origLines.Length ? origLines[i].TrimEnd('\r') : null;
                string newLine = i < newLines.Length ? newLines[i].TrimEnd('\r') : null;

                if (origLine == newLine)
                {
                    if (origLine != null) sb.AppendLine($"  {origLine}");
                }
                else
                {
                    if (origLine != null) sb.AppendLine($"- {origLine}");
                    if (newLine != null) sb.AppendLine($"+ {newLine}");
                }
            }

            return sb.ToString();
        }
    }
}

```

### 3.7. Luồng Lưu trữ Đa nền tảng: `Services/StorageService.cs`
```csharp
using System.Text.Json.Nodes;
using BtoanAiNet.ViewModels;

namespace BtoanAiNet.Services
{
    public interface IStorageService
    {
        Task<JsonObject> ExportFileAsync(FileUploadRequestModel model, string webRootPath);
        Task<(Stream? Stream, string ContentType, string DownloadFilename)> GetDownloadProxyStreamAsync(string url, string filename);
    }

    public class StorageService : IStorageService
    {
        private readonly HttpClient _httpClient;

        public StorageService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<JsonObject> ExportFileAsync(FileUploadRequestModel model, string webRootPath)
        {
            string content = model.Content?.Trim() ?? "";
            if (string.IsNullOrEmpty(content))
            {
                return new JsonObject { ["error"] = "no content" };
            }

            string ext = model.Ext?.ToLower()?.Trim() ?? "txt";
            string filename = $"file_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            string filesDir = Path.Combine(webRootPath, "files");
            if (!Directory.Exists(filesDir)) Directory.CreateDirectory(filesDir);

            if (ext == "txt")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.txt");
                byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
                byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
                byte[] finalBytes = bom.Concat(contentBytes).ToArray();

                await File.WriteAllBytesAsync(targetFilePath, finalBytes);
                return new JsonObject { ["url"] = $"/files/{filename}.txt" };
            }

            if (ext == "md")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.md");
                await File.WriteAllTextAsync(targetFilePath, content, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.md" };
            }

            if (ext == "docx" || ext == "doc")
            {
                // Professional Word-compatible HTML format with styling
                string docFilePath = Path.Combine(filesDir, $"{filename}.doc");
                string escapedHtml = System.Net.WebUtility.HtmlEncode(content).Replace("\n", "<br/>");
                string wordDocContent = $@"<html xmlns:o='urn:schemas-microsoft-com:office:office' xmlns:w='urn:schemas-microsoft-com:office:word' xmlns='http://www.w3.org/TR/REC-html40'>
<head><meta charset='utf-8'><title>Tài liệu Btoan AI</title>
<!--[if gte mso 9]>
<xml><w:WordDocument><w:View>Print</w:View><w:Zoom>100</w:Zoom></w:WordDocument></xml>
<![endif]-->
<style>
body {{ font-family: 'Segoe UI', Calibri, Arial, sans-serif; font-size: 11pt; line-height: 1.6; padding: 40px; color: #1e293b; }}
h1, h2, h3 {{ color: #0f172a; margin-top: 18px; }}
p {{ margin-bottom: 12px; }}
pre, code {{ font-family: Consolas, 'Courier New', monospace; background: #f8fafc; padding: 4px 8px; border: 1px solid #e2e8f0; border-radius: 4px; }}
</style>
</head>
<body>
<div style='max-width: 800px; margin: 0 auto;'>
<div style='border-bottom: 2px solid #6366f1; padding-bottom: 10px; margin-bottom: 20px;'>
  <h2 style='color: #4f46e5; margin: 0;'>Btoan AI Workspace - Tài liệu xuất</h2>
  <small style='color: #64748b;'>Xuất bản lúc: {DateTime.Now:dd/MM/yyyy HH:mm:ss}</small>
</div>
<div>{escapedHtml}</div>
</div>
</body>
</html>";
                await File.WriteAllTextAsync(docFilePath, wordDocContent, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.doc" };
            }

            if (ext == "html")
            {
                string targetFilePath = Path.Combine(filesDir, $"{filename}.html");
                string htmlContent = $@"<!DOCTYPE html>
<html lang='vi'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
<title>Btoan AI Document</title>
<style>
body {{ font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif; padding: 40px; line-height: 1.6; max-width: 800px; margin: 0 auto; color: #1e293b; background: #f8fafc; }}
.card {{ background: white; padding: 32px; border-radius: 12px; box-shadow: 0 4px 6px -1px rgb(0 0 0 / 0.1); }}
</style>
</head>
<body>
<div class='card'>
{System.Net.WebUtility.HtmlEncode(content).Replace("\n", "<br/>")}
</div>
</body>
</html>";
                await File.WriteAllTextAsync(targetFilePath, htmlContent, System.Text.Encoding.UTF8);
                return new JsonObject { ["url"] = $"/files/{filename}.html" };
            }

            string genericPath = Path.Combine(filesDir, $"{filename}.{ext}");
            await File.WriteAllTextAsync(genericPath, content, System.Text.Encoding.UTF8);
            return new JsonObject { ["url"] = $"/files/{filename}.{ext}" };
        }

        public async Task<(Stream? Stream, string ContentType, string DownloadFilename)> GetDownloadProxyStreamAsync(string url, string filename)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                return (null, "text/plain", filename);
            }

            try
            {
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) return (null, "text/plain", filename);

                var stream = await response.Content.ReadAsStreamAsync();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                string downloadName = !string.IsNullOrEmpty(filename) ? filename : Path.GetFileName(url);

                return (stream, contentType, downloadName);
            }
            catch
            {
                return (null, "text/plain", filename);
            }
        }
    }
}

```

### 3.8. Luồng Vector Database: `Services/VectorDb/QdrantVectorDatabaseService.cs`
```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BtoanAiNet.Services.VectorDb
{
    public class QdrantOptions
    {
        public string Endpoint { get; set; } = "http://localhost:6333";
        public string ApiKey { get; set; } = string.Empty;
    }

    public class QdrantVectorDatabaseService : IVectorDatabaseService
    {
        private readonly HttpClient _http;
        private readonly ILogger<QdrantVectorDatabaseService> _logger;

        public QdrantVectorDatabaseService(
            HttpClient http, 
            IConfiguration configuration,
            ILogger<QdrantVectorDatabaseService> logger)
        {
            _logger = logger;
            var opts = configuration.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
            _http = http;
            _http.BaseAddress = new Uri(opts.Endpoint.TrimEnd('/') + "/");
            if (!string.IsNullOrEmpty(opts.ApiKey))
            {
                _http.DefaultRequestHeaders.Add("api-key", opts.ApiKey);
            }
        }

        public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var res = await _http.GetAsync("healthz", cancellationToken);
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task EnsureCollectionAsync(string collectionName, int vectorDimension = 768, CancellationToken cancellationToken = default)
        {
            try
            {
                var checkRes = await _http.GetAsync($"collections/{collectionName}", cancellationToken);
                if (checkRes.IsSuccessStatusCode) return;

                var payload = new
                {
                    vectors = new
                    {
                        size = vectorDimension,
                        distance = "Cosine"
                    }
                };

                var createRes = await _http.PutAsJsonAsync($"collections/{collectionName}", payload, cancellationToken);
                createRes.EnsureSuccessStatusCode();
                _logger.LogInformation("Created Qdrant collection: {CollectionName} with dim {Dim}", collectionName, vectorDimension);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Qdrant collection creation skipped or failed for {CollectionName}", collectionName);
            }
        }

        public async Task UpsertPointsAsync(string collectionName, IEnumerable<VectorPoint> points, CancellationToken cancellationToken = default)
        {
            try
            {
                var qdrantPoints = points.Select(p => new
                {
                    id = p.Id,
                    vector = p.Vector,
                    payload = p.Payload
                }).ToList();

                if (qdrantPoints.Count == 0) return;

                var body = new { points = qdrantPoints };
                var res = await _http.PutAsJsonAsync($"collections/{collectionName}/points?wait=true", body, cancellationToken);
                res.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert points into Qdrant collection {CollectionName}", collectionName);
            }
        }

        public async Task<List<VectorSearchResult>> SearchAsync(
            string collectionName, 
            float[] queryVector, 
            int topK = 5, 
            float minScore = 0.60f, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new
                {
                    vector = queryVector,
                    limit = topK,
                    with_payload = true,
                    score_threshold = minScore
                };

                var res = await _http.PostAsJsonAsync($"collections/{collectionName}/points/search", body, cancellationToken);
                if (!res.IsSuccessStatusCode) return new List<VectorSearchResult>();

                var json = await res.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
                var results = new List<VectorSearchResult>();

                if (json?["result"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        var id = item?["id"]?.ToString() ?? string.Empty;
                        var score = item?["score"]?.GetValue<float>() ?? 0f;
                        var payloadDict = new Dictionary<string, object>();

                        if (item?["payload"] is JsonObject payloadObj)
                        {
                            foreach (var kvp in payloadObj)
                            {
                                payloadDict[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
                            }
                        }

                        results.Add(new VectorSearchResult
                        {
                            Id = id,
                            Score = score,
                            Payload = payloadDict
                        });
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Qdrant search error for collection {CollectionName}", collectionName);
                return new List<VectorSearchResult>();
            }
        }

        public async Task DeleteByFilterAsync(string collectionName, string filterKey, object filterValue, CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new
                {
                    filter = new
                    {
                        must = new[]
                        {
                            new
                            {
                                key = filterKey,
                                match = new { value = filterValue }
                            }
                        }
                    }
                };

                await _http.PostAsJsonAsync($"collections/{collectionName}/points/delete", body, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete by filter in Qdrant {CollectionName}", collectionName);
            }
        }
    }
}


```

### 3.9. Toàn bộ file trong `Hubs/`
#### A. `Hubs/TerminalHub.cs`
```csharp
using Microsoft.AspNetCore.SignalR;

namespace BtoanAiNet.Hubs
{
    public class TerminalHub : Hub
    {
        public async Task JoinSessionGroup(int sessionId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{sessionId}");
        }

        public async Task LeaveSessionGroup(int sessionId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session_{sessionId}");
        }
    }
}

```

#### B. `Hubs/WorkspaceHub.cs`
```csharp
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace BtoanAiNet.Hubs
{
    public class WorkspaceHub : Hub
    {
        private static readonly ConcurrentDictionary<string, string> FileLocks = new();

        public async Task JoinProjectSession(int projectId, int userId)
        {
            string groupName = $"project-{projectId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            await Clients.Group(groupName).SendAsync("UserJoined", new { userId, connectionId = Context.ConnectionId });
        }

        public async Task LeaveProjectSession(int projectId, int userId)
        {
            string groupName = $"project-{projectId}";
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
            await Clients.Group(groupName).SendAsync("UserLeft", new { userId, connectionId = Context.ConnectionId });
        }

        public async Task RequestFileLock(int projectId, string filePath, int userId)
        {
            string lockKey = $"{projectId}:{filePath}";
            if (FileLocks.TryGetValue(lockKey, out string? ownerConnId) && ownerConnId != Context.ConnectionId)
            {
                await Clients.Caller.SendAsync("FileLockResponse", new { ok = false, message = "File is locked by another user." });
                return;
            }

            FileLocks[lockKey] = Context.ConnectionId;
            string groupName = $"project-{projectId}";
            await Clients.Group(groupName).SendAsync("FileLocked", new { filePath, userId, connectionId = Context.ConnectionId });
            await Clients.Caller.SendAsync("FileLockResponse", new { ok = true, filePath });
        }

        public async Task ReleaseFileLock(int projectId, string filePath, int userId)
        {
            string lockKey = $"{projectId}:{filePath}";
            FileLocks.TryRemove(lockKey, out _);
            string groupName = $"project-{projectId}";
            await Clients.Group(groupName).SendAsync("FileUnlocked", new { filePath, userId });
        }

        public async Task StreamCursorPosition(int projectId, string filePath, int lineNumber, int columnNumber, int userId)
        {
            string groupName = $"project-{projectId}";
            await Clients.OthersInGroup(groupName).SendAsync("CursorMoved", new { filePath, lineNumber, columnNumber, userId });
        }

        public async Task SendAiStreamChunk(int projectId, string chunkText, bool isComplete)
        {
            string groupName = $"project-{projectId}";
            await Clients.Group(groupName).SendAsync("ReceiveAiStreamChunk", new { chunkText, isComplete });
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            foreach (var kvp in FileLocks.Where(x => x.Value == Context.ConnectionId).ToList())
            {
                FileLocks.TryRemove(kvp.Key, out _);
            }
            return base.OnDisconnectedAsync(exception);
        }
    }
}

```

### 3.10. Toàn bộ file trong `Messaging/`
#### A. `Messaging/Consumers/MediaGenerationConsumer.cs`
```csharp
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using BtoanAiNet.Hubs;
using BtoanAiNet.Messaging.Contracts;
using BtoanAiNet.Services;
using BtoanAiNet.ViewModels;

namespace BtoanAiNet.Messaging.Consumers
{
    public class MediaGenerationConsumer : IConsumer<MediaGenerationCommand>
    {
        private readonly IHubContext<WorkspaceHub> _hubContext;
        private readonly IImageGenerationService _imageService;
        private readonly IVideoGenerationService _videoService;
        private readonly IMusicGenerationService _musicService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<MediaGenerationConsumer> _logger;

        public MediaGenerationConsumer(
            IHubContext<WorkspaceHub> hubContext,
            IImageGenerationService imageService,
            IVideoGenerationService videoService,
            IMusicGenerationService musicService,
            IWebHostEnvironment env,
            ILogger<MediaGenerationConsumer> logger)
        {
            _hubContext = hubContext;
            _imageService = imageService;
            _videoService = videoService;
            _musicService = musicService;
            _env = env;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<MediaGenerationCommand> context)
        {
            var cmd = context.Message;
            _logger.LogInformation("Processing Media Job: {JobId} ({Type}) for User: {UserId}", cmd.JobId, cmd.Type, cmd.UserId);

            // 1. Send JobStarted event via SignalR
            await NotifyClientAsync(cmd, "Started", 5, null, null);

            try
            {
                string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                string resultUrl = string.Empty;

                // 2. Process based on MediaType
                switch (cmd.Type)
                {
                    case MediaType.Image:
                        await NotifyClientAsync(cmd, "Processing", 30, null, "Generating high-resolution AI Image...");
                        var imgReq = new ImageGenerationRequestModel
                        {
                            Prompt = cmd.Prompt,
                            Conversation_Id = cmd.ConversationId ?? 0
                        };
                        var imgRes = await _imageService.GenerateImageAsync(imgReq);
                        resultUrl = imgRes?["image_url"]?.ToString() ?? string.Empty;
                        break;

                    case MediaType.Video:
                        await NotifyClientAsync(cmd, "Processing", 20, null, "Synthesizing AI Video Frames...");
                        var videoReq = new VideoGenerationRequestModel
                        {
                            Prompt = cmd.Prompt,
                            Conversation_Id = (cmd.ConversationId ?? 0).ToString(),
                            User_Id = cmd.UserId.ToString()
                        };
                        var videoRes = await _videoService.StartVideoJobAsync(videoReq, Directory.GetCurrentDirectory());
                        resultUrl = videoRes?["video_url"]?.ToString() ?? videoRes?["job_id"]?.ToString() ?? string.Empty;
                        break;

                    case MediaType.Music:
                        await NotifyClientAsync(cmd, "Processing", 25, null, "Composing melodies and lyrics...");
                        var musicReq = new MusicGenerationRequestModel
                        {
                            Prompt = cmd.Prompt,
                            User_Id = cmd.UserId,
                            Conversation_Id = cmd.ConversationId ?? 0,
                            Duration = int.TryParse(cmd.Parameters.GetValueOrDefault("duration", "30"), out int d) ? d : 30
                        };
                        var musicRes = await _musicService.GenerateMusicAsync(musicReq, webRootPath);
                        resultUrl = musicRes?["audio_url"]?.ToString() ?? string.Empty;
                        break;

                    default:
                        throw new NotSupportedException($"Media type {cmd.Type} is not supported.");
                }

                // 3. Complete and push result to Client
                await NotifyClientAsync(cmd, "Completed", 100, resultUrl, null);
                _logger.LogInformation("Job {JobId} completed successfully: {ResultUrl}", cmd.JobId, resultUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Media Job: {JobId}", cmd.JobId);
                await NotifyClientAsync(cmd, "Failed", 100, null, ex.Message);
            }
        }

        private async Task NotifyClientAsync(MediaGenerationCommand cmd, string status, int percent, string? url, string? error)
        {
            var payload = new MediaJobStatusEvent
            {
                JobId = cmd.JobId,
                UserId = cmd.UserId,
                ProjectId = cmd.ProjectId,
                Status = status,
                ProgressPercent = percent,
                ResultUrl = url,
                ErrorMessage = error
            };

            if (cmd.ProjectId.HasValue)
            {
                await _hubContext.Clients.Group($"project-{cmd.ProjectId.Value}").SendAsync("OnMediaJobUpdated", payload);
            }
            await _hubContext.Clients.User(cmd.UserId.ToString()).SendAsync("OnMediaJobUpdated", payload);
        }
    }
}

```

#### B. `Messaging/Contracts/MediaCommands.cs`
```csharp
namespace BtoanAiNet.Messaging.Contracts
{
    public enum MediaType
    {
        Image,
        Video,
        Music,
        ImageEdit
    }

    public record MediaGenerationCommand
    {
        public Guid JobId { get; init; } = Guid.NewGuid();
        public int UserId { get; init; }
        public int? ProjectId { get; init; }
        public int? ConversationId { get; init; }
        public MediaType Type { get; init; }
        public string Prompt { get; init; } = string.Empty;
        public Dictionary<string, string> Parameters { get; init; } = new();
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

    public record MediaJobStatusEvent
    {
        public Guid JobId { get; init; }
        public int UserId { get; init; }
        public int? ProjectId { get; init; }
        public string Status { get; init; } = string.Empty; // "Started", "Processing", "Completed", "Failed"
        public int ProgressPercent { get; init; }
        public string? ResultUrl { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    }
}


```

---

## PHẦN 4: BẢN ĐỒ TÓM TẮT (API & SIGNATURE MAP) CÁC FILE BACKEND CÒN LẠI

### 4.1. Controllers còn lại

#### 1. `Controllers/AutonomousAgentController.cs`
- **Route Base:** `[Route("api/autonomous-agent")]`, `[ApiController]`
- **Dependencies:** `AppDbContext`, `IAgentOrchestrator`, `ILogger<AutonomousAgentController>`
- **Endpoints:**
  - `[HttpPost("start")]` -> `Task<IActionResult> StartAutonomousTask([FromBody] StartAutonomousTaskRequest request)`: Khởi tạo tiến trình autonomous agent giải quyết bài toán phức tạp.
  - `[HttpGet("status/{taskId}")]` -> `Task<IActionResult> GetTaskStatus(string taskId)`: Kiểm tra tiến độ và nhật ký các bước của agent.
  - `[HttpPost("stop/{taskId}")]` -> `Task<IActionResult> StopTask(string taskId)`: Huỷ tiến trình agent đang chạy.

#### 2. `Controllers/CloudDeploymentController.cs`
- **Route Base:** `[Route("api/deployment")]`, `[ApiController]`
- **Dependencies:** `IDeploymentService`, `ILogger<CloudDeploymentController>`
- **Endpoints:**
  - `[HttpPost("deploy")]` -> `Task<IActionResult> DeployProject([FromBody] DeploymentRequest request)`: Deploy dự án lên Render / Docker Cloud.
  - `[HttpGet("status/{deploymentId}")]` -> `Task<IActionResult> GetDeploymentStatus(string deploymentId)`: Lấy trạng thái container/build.
  - `[HttpGet("logs/{deploymentId}")]` -> `Task<IActionResult> GetDeploymentLogs(string deploymentId)`: Đọc log streaming của container.

#### 3. `Controllers/DiffReviewController.cs`
- **Route Base:** `[Route("api/diff")]`, `[ApiController]`
- **Dependencies:** `IDiffService`, `IWorkspaceEditService`, `ILogger<DiffReviewController>`
- **Endpoints:**
  - `[HttpPost("generate")]` -> `IActionResult GenerateDiff([FromBody] GenerateDiffRequest request)`: So sánh code cũ và mới, trả về cấu trúc Unified Diff / Inline Diff.
  - `[HttpPost("apply")]` -> `Task<IActionResult> ApplyDiff([FromBody] ApplyDiffRequest request)`: Chấp nhận thay đổi và ghi patch vào file dự án.
  - `[HttpPost("reject")]` -> `IActionResult RejectDiff([FromBody] RejectDiffRequest request)`: Huỷ bỏ đề xuất sửa đổi code.

#### 4. `Controllers/EditorController.cs`
- **Route Base:** `[Route("api/editor")]`, `[ApiController]`
- **Dependencies:** `IFileEditorService`, `IWorkspaceService`
- **Endpoints:**
  - `[HttpPost("file/save")]` -> `Task<IActionResult> SaveFile([FromQuery] int userId, [FromBody] SaveFileRequest request)`: Lưu nội dung sửa đổi từ trình soạn thảo Monaco/CodeMirror.
  - `[HttpPost("file/format")]` -> `Task<IActionResult> FormatCode([FromBody] FormatCodeRequest request)`: Tự động format mã nguồn (Prettier/C# syntax formatter).

#### 5. `Controllers/HomeController.cs`
- **Route Base:** `[Route("[controller]")]` (Controller MVC truyền thống)
- **Dependencies:** `ILogger<HomeController>`, `AppDbContext`
- **Endpoints:**
  - `[HttpGet("")]`, `[HttpGet("/")]` -> `IActionResult Index()`: Render giao diện web chính (Chat AI & Workspace).
  - `[HttpGet("Privacy")]` -> `IActionResult Privacy()`: Render trang chính sách.
  - `[ResponseCache] [HttpGet("Error")]` -> `IActionResult Error()`: Xử lý hiển thị view lỗi hệ thống `ErrorViewModel`.

#### 6. `Controllers/MediaController.cs` & `Controllers/MediaAsyncController.cs`
- **`MediaController`:** `[Route("api/media")]` / `[Route("Media")]`
  - `[HttpPost("generate-image")]` -> `Task<IActionResult> GenerateImage([FromBody] ImageRequest req)` (Đồng bộ qua Gemini Imagen).
  - `[HttpPost("generate-music")]` -> `Task<IActionResult> GenerateMusic([FromBody] MusicRequest req)` (Đồng bộ qua Music API).
  - `[HttpPost("generate-video")]` -> `Task<IActionResult> GenerateVideo([FromBody] VideoRequest req)` (Đồng bộ qua Video Generation pipeline).
- **`MediaAsyncController`:** `[Route("api/media-async")]`
  - `[HttpPost("generate")]` -> `Task<IActionResult> EnqueueGeneration([FromBody] GenerateMediaCommand command)`: Đẩy vào MassTransit RabbitMQ, trả về `202 Accepted` kèm `jobId`.

#### 7. `Controllers/MultiAgentTeamController.cs`
- **Route Base:** `[Route("api/agent-team")]`, `[ApiController]`
- **Dependencies:** `IAgentTeamManager`, `ILogger<MultiAgentTeamController>`
- **Endpoints:**
  - `[HttpPost("discuss")]` -> `Task<IActionResult> StartTeamDiscussion([FromBody] TeamDiscussionRequest req)`: Kích hoạt cuộc họp nhóm giữa 3 agent ảo: Architect, Developer, Reviewer.
  - `[HttpPost("execute")]` -> `Task<IActionResult> ExecuteTeamPlan([FromBody] ExecuteTeamPlanRequest req)`: Phân chia task cho từng role và sinh code hoàn thiện.

#### 8. `Controllers/ProjectRuntimeController.cs`
- **Route Base:** `[Route("api/project-runtime")]`, `[ApiController]`
- **Dependencies:** `IProjectRuntimeManagerService`, `ILogger<ProjectRuntimeController>`
- **Endpoints:**
  - `[HttpPost("start")]` -> `Task<IActionResult> StartRuntime([FromBody] StartRuntimeRequest req)`: Khởi chạy ứng dụng dự án trong sandbox (chạy `dotnet run`, `npm start`...).
  - `[HttpPost("stop")]` -> `Task<IActionResult> StopRuntime([FromBody] StopRuntimeRequest req)`: Dừng process đang chạy.
  - `[HttpPost("send-input")]` -> `Task<IActionResult> SendTerminalInput([FromBody] TerminalInputRequest req)`: Gửi stdin vào terminal qua web socket.

#### 9. `Controllers/RuntimeController.cs`
- **Route Base:** `[Route("api/runtime")]`, `[ApiController]`
- **Dependencies:** `IRuntimeService`, `ILogger<RuntimeController>`
- **Endpoints:**
  - `[HttpPost("run")]` -> `Task<IActionResult> ExecuteCommand([FromQuery] int userId, [FromBody] ExecuteCommandRequest req)`: Thực thi lệnh console ngắn hạn và trả về kết quả stdout/stderr ngay lập tức.
  - `[HttpGet("output/{sessionId}")]` -> `IActionResult GetSessionOutput(string sessionId, [FromQuery] int userId)`: Lấy buffer output của phiên chạy.

#### 10. `Controllers/VersionController.cs`
- **Route Base:** `[Route("api/version")]`, `[ApiController]`
- **Dependencies:** `IVersionControlService`
- **Endpoints:**
  - `[HttpGet("history")]` -> `Task<IActionResult> GetFileHistory([FromQuery] int projectId, [FromQuery] string path)`: Lấy danh sách commit/phiên bản của file.
  - `[HttpPost("rollback")]` -> `Task<IActionResult> RollbackVersion([FromBody] RollbackRequest req)`: Khôi phục file về snapshot trước đó.

#### 11. `Controllers/WorkspaceController.cs`
- **Route Base:** `[Route("api/workspace")]`, `[ApiController]`
- **Dependencies:** `IWorkspaceService`, `IWorkspaceIndexingService`
- **Endpoints:**
  - `[HttpGet("{projectId}/tree")]` -> `Task<IActionResult> GetFileTree(int projectId, [FromQuery] int userId)`: Đọc cây thư mục dạng nested JSON.
  - `[HttpGet("file")]` -> `Task<IActionResult> GetFileContent([FromQuery] int projectId, [FromQuery] string path, [FromQuery] int userId)`: Đọc text hoặc base64 của file.
  - `[HttpPost("file/create")]` -> `Task<IActionResult> CreateFile([FromQuery] int userId, [FromBody] CreateFileRequest req)`: Tạo file mới.
  - `[HttpPost("folder/create")]` -> `Task<IActionResult> CreateFolder([FromQuery] int userId, [FromBody] CreateFolderRequest req)`: Tạo thư mục mới.
  - `[HttpPost("file/rename")]` -> `Task<IActionResult> RenameFile([FromQuery] int userId, [FromBody] RenameFileRequest req)`: Đổi tên file/folder.
  - `[HttpPost("file/delete")]` -> `Task<IActionResult> DeleteFile([FromQuery] int userId, [FromBody] DeleteFileRequest req)`: Xoá file/folder.

---

### 4.2. Services & Interfaces còn lại

1. **`AgentOrchestrator.cs` & `IAgentOrchestrator.cs`:**
   - *Dependencies:* `GeminiService`, `IWorkspaceService`, `IDiffService`, `IWorkspaceIndexingService`, `IContextRetrieverService`.
   - *Public Methods:* `Task<AgentPlanResponse> GeneratePlanAsync(int projectId, string userGoal)`, `Task<AgentExecutionResult> ExecutePlanAsync(int projectId, List<CodeChange> changes)`.
2. **`AgentService.cs`:**
   - *Dependencies:* `GeminiService`, `IWorkspaceService`.
   - *Public Methods:* `Task<string> AnalyzeCodeAsync(string code, string instruction)`, `Task<string> ApplyChangesAsync(string original, string prompt)`.
3. **`AgentTeamManager.cs` & `IAgentTeamManager.cs`:**
   - *Dependencies:* `GeminiService`, `IDiffService`, `IRuntimeService`, `ILogger<AgentTeamManager>`.
   - *Public Methods:* `Task<TeamDiscussionResult> ConductDiscussionAsync(string projectContext, string taskDescription)`, `Task<TeamExecutionResult> ExecuteTeamWorkflowAsync(int projectId, string taskDescription)`.
4. **`AgentToolLoopOrchestrator.cs` & `IAgentToolLoopOrchestrator`:**
   - *Dependencies:* `FunctionCallingDispatcher`, `GeminiService`, `ILogger<AgentToolLoopOrchestrator>`.
   - *Public Methods:* `Task<ToolLoopResult> ExecuteLoopAsync(string initialPrompt, int maxIterations)`. (Dead Registration, chưa có controller gọi).
5. **`AgentWorkflowService.cs` & `IAgentWorkflowService`:**
   - *Dependencies:* `IAgentOrchestrator`, `IRuntimeService`, `IDeploymentService`.
   - *Public Methods:* `Task<WorkflowStepResult> RunStepAsync(WorkflowState state, WorkflowStep step)`.
6. **`AuthService.cs`:**
   - *Dependencies:* `IUserRepository`, `IConfiguration`.
   - *Public Methods:* `Task<User?> AuthenticateAsync(string email, string password)`, `Task<User> RegisterAsync(string email, string password, string name)`, `string GenerateJwtToken(User user)`.
7. **`ChatService.cs`:**
   - *Dependencies:* `GeminiService`, `IConversationRepository`, `IContextRetrieverService`.
   - *Public Methods:* `Task<string> SendMessageAsync(int userId, int? conversationId, string message, string? imageBase64)`.
8. **`CodeAnalysisService.cs`:**
   - *Dependencies:* `GeminiService`.
   - *Public Methods:* `Task<AnalysisReport> AnalyzeProjectStructureAsync(string projectPath)`, `Task<List<SecurityFinding>> ScanVulnerabilitiesAsync(string code)`.
9. **`CodeDiffService.cs` & `ICodeDiffService`:**
   - *Dependencies:* Không có (logic xử lý chuỗi thô).
   - *Public Methods:* `string GenerateDiff(string oldCode, string newCode)`, `string ApplyPatch(string source, string diff)`. (Dead code, bị thay bởi `DiffService`).
10. **`ContextRetrieverService.cs` & `IContextRetrieverService`:**
    - *Dependencies:* `IVectorDatabaseService`, `IEmbeddingService`.
    - *Public Methods:* `Task<List<SemanticContextItem>> RetrieveRelevantSnippetsAsync(int projectId, string query, int topK = 5)`.
11. **`DeploymentService.cs` & `IDeploymentService`:**
    - *Dependencies:* `HttpClient`, `IConfiguration`, `ILogger<DeploymentService>`.
    - *Public Methods:* `Task<DeploymentResult> TriggerRenderDeployAsync(string serviceId, string apiKey)`, `Task<DeploymentStatus> CheckStatusAsync(string deploymentId)`.
12. **`DiffService.cs` & `IDiffService`:**
    - *Dependencies:* Thư viện ngoài `DiffPlex`.
    - *Public Methods:* `DiffModel BuildInlineDiff(string oldText, string newText)`, `DiffResult ApplyHunks(string originalText, List<DiffHunk> hunks)`.
13. **`FileEditorService.cs` & `IFileEditorService`:**
    - *Dependencies:* `IWorkspaceService`, `IVersionControlService`.
    - *Public Methods:* `Task<bool> SaveFileWithSnapshotAsync(int projectId, string path, string content, int userId)`.
14. **`GeminiService.cs`:**
    - *Dependencies:* `HttpClient`, `IConfiguration`, `ILogger<GeminiService>`.
    - *Public Methods:* `Task<string> GenerateContentAsync(string prompt, string? systemInstruction = null)`, `Task<string> GenerateWithImageAsync(string prompt, byte[] imageBytes, string mimeType)`.
15. **`GoogleAuthService.cs`:**
    - *Dependencies:* `HttpClient`, `IConfiguration`.
    - *Public Methods:* `Task<GoogleUserInfo?> VerifyGoogleTokenAsync(string idToken)`.
16. **`ImageGenerationService.cs`:**
    - *Dependencies:* `HttpClient`, `IConfiguration`.
    - *Public Methods:* `Task<string> GenerateImageAsync(string prompt, string aspectRatio = "1:1")`.
17. **`IntentEngineService.cs`:**
    - *Dependencies:* `GeminiService`.
    - *Public Methods:* `Task<UserIntent> ClassifyIntentAsync(string message)`. (Nhận biết ý định: Chat, Viết code, Sinh ảnh, Tìm kiếm, v.v.).
18. **`MemoryService.cs`:**
    - *Dependencies:* `IMemoryRepository`, `IEmbeddingService`, `IVectorDatabaseService`.
    - *Public Methods:* `Task StoreMemoryAsync(int userId, string key, string value)`, `Task<List<string>> RecallMemoriesAsync(int userId, string context)`.
19. **`MusicGenerationService.cs`:**
    - *Dependencies:* `HttpClient`, `IConfiguration`.
    - *Public Methods:* `Task<string> GenerateMusicTrackAsync(string prompt, string genre)`.
20. **`ProjectScannerService.cs`:**
    - *Dependencies:* Không có (Directory scanner).
    - *Public Methods:* `ProjectStructure ScanDirectory(string rootPath, List<string> ignoredFolders)`.
21. **`RuntimeService.cs` & `IRuntimeService`:**
    - *Dependencies:* `ILogger<RuntimeService>`.
    - *Public Methods:* `Task<ExecutionResult> ExecuteAsync(string command, string workingDir, int timeoutSeconds = 60)`.
22. **`TerminalExecutionService.cs`:**
    - *Dependencies:* `ILogger<TerminalExecutionService>`.
    - *Public Methods:* `Task<string> RunTerminalBatchAsync(string commandLine)`.
23. **`TrainingService.cs`:**
    - *Dependencies:* `ITrainingRepository`.
    - *Public Methods:* `Task SaveFineTuneDataAsync(int userId, string prompt, string completion)`.
24. **`VersionControlService.cs` & `IVersionControlService`:**
    - *Dependencies:* `AppDbContext`.
    - *Public Methods:* `Task<ProjectVersion> CreateSnapshotAsync(int projectId, string commitMessage)`, `Task<bool> RollbackToVersionAsync(int projectId, int versionId)`.
25. **`VideoGenerationService.cs`:**
    - *Dependencies:* `HttpClient`, `IConfiguration`.
    - *Public Methods:* `Task<string> InitiateVideoRenderAsync(string prompt)`.
26. **`WorkspaceEditService.cs` & `IWorkspaceEditService`:**
    - *Dependencies:* `IWorkspaceService`, `IDiffService`.
    - *Public Methods:* `Task<bool> ApplyChangesetAsync(int projectId, List<CodeChange> changes)`.
27. **`WorkspaceIndexingService.cs`:**
    - *Dependencies:* `ISemanticChunkingService`, `IEmbeddingService`, `IVectorDatabaseService`.
    - *Public Methods:* `Task IndexProjectWorkspaceAsync(int projectId, string projectRootPath)`.
28. **`WorkspaceService.cs` & `IWorkspaceService`:**
    - *Dependencies:* `IConfiguration`, `ILogger<WorkspaceService>`.
    - *Public Methods:* `string GetProjectPath(int projectId)`, `Task<FileTreeNode> BuildTreeAsync(int projectId)`, `Task<string> ReadFileAsync(int projectId, string relativePath)`, `Task WriteFileAsync(int projectId, string relativePath, string content)`.
29. **Cụm Vector Database (`Services/VectorDb/`):**
    - `EmbeddingService.cs` & `IEmbeddingService`: Gọi model Gemini `text-embedding-004` để chuyển văn bản thành vector embedding 768 chiều.
    - `SemanticChunkingService.cs` & `ISemanticChunkingService`: Tách mã nguồn và tài liệu thành các đoạn semantic chunks theo ranh giới hàm/lớp/khối logic.
    - `IVectorDatabaseService.cs`: Interface định nghĩa các phương thức `EnsureCollectionAsync`, `UpsertVectorsAsync`, `SearchSimilarityAsync`.

---

### 4.3. Models & Entities (`Models/`)
- `User.cs`: `Id`, `Email`, `PasswordHash`, `FullName`, `AvatarUrl`, `FaceDescriptor` (Lưu mảng 128 số float biểu diễn đặc trưng khuôn mặt dạng JSON), `CreatedAt`, `UpdatedAt`.
- `Project.cs`: `Id`, `UserId`, `Name`, `Description`, `RootDirectoryPath`, `CreatedAt`, `LastOpenedAt`.
- `ProjectFile.cs`: `Id`, `ProjectId`, `RelativePath`, `Extension`, `SizeBytes`, `LastModified`.
- `ProjectVersion.cs`: `Id`, `ProjectId`, `CommitHash`, `CommitMessage`, `SnapshotPath`, `CreatedAt`.
- `FileVersion.cs`: `Id`, `ProjectVersionId`, `FilePath`, `FileContentHash`, `CreatedAt`.
- `Conversation.cs`: `Id`, `UserId`, `Title`, `CreatedAt`, `UpdatedAt`.
- `Message.cs`: `Id`, `ConversationId`, `Sender` (User/Assistant), `Content`, `ImageUrl`, `AudioUrl`, `CreatedAt`.
- `CodeChange.cs`: `FilePath`, `OriginalContent`, `ModifiedContent`, `UnifiedDiff`, `Explanation`, `IsAccepted`.
- `AgentTask.cs` & `AutonomousAgentTask.cs`: `Id`, `ProjectId`, `TaskTitle`, `Status`, `StepsJson`, `CreatedDate`.
- `MultiAgentTeam.cs`: `TeamId`, `Name`, `RolesJson`, `DiscussionLog`.
- `DeploymentPlatform.cs`: `Id`, `PlatformName` (Render, Docker, Kube), `ApiEndpoint`, `ApiKeyConfigKey`.
- `TerminalSession.cs`: `SessionId`, `ProjectId`, `ProcessId`, `StartedAt`, `IsActive`.
- `UserMemory.cs`: `Id`, `UserId`, `MemoryKey`, `MemoryValue`, `EmbeddingId`, `CreatedAt`.
- `AiTraining.cs`: `Id`, `UserId`, `Prompt`, `Completion`, `Category`, `CreatedAt`.

---

### 4.4. Repositories (`Repositories/`)
- `IUserRepository.cs` / `UserRepository.cs`: Quản lý thực thể User, FaceDescriptor, tìm theo Email/Id.
- `IConversationRepository.cs` / `ConversationRepository.cs`: Quản lý danh sách hội thoại, tin nhắn chat, đổi tên, xoá hội thoại.
- `IMemoryRepository.cs` / `MemoryRepository.cs`: Lưu trữ và truy xuất bộ nhớ ngắn hạn / dài hạn của người dùng.
- `ITrainingRepository.cs` / `TrainingRepository.cs`: Lưu log các cặp Prompt - Response để phục vụ huấn luyện / fine-tune AI.

---

### 4.5. Function Calling (`FunctionCalling/`)
- `Attributes/AiToolAttribute.cs`: Custom attribute đánh dấu phương thức C# là công cụ cho AI (`[AiTool(Name = "read_file", Description = "Đọc nội dung file")]`).
- `Services/ToolRegistry.cs`: Quét Reflection để tìm nạp danh sách các tool khả dụng, sinh schema JSON chuẩn của OpenAI/Gemini Function Calling.
- `Services/FunctionCallingDispatcher.cs`: Tiếp nhận tên hàm và JSON arguments từ Gemini, phân giải sang C# method tương ứng và thực thi an toàn.
- `Tools/WorkspaceTools.cs`: Chứa các tool cụ thể cung cấp cho AI: `read_file`, `write_file`, `list_directory`, `search_files`, `run_command`.

---
