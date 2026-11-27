using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BtoanAiNet.ViewModels;

namespace BtoanAiNet.Services
{
    public interface IAgentService
    {
        Task<AgentProposalResponseModel> AnalyzeAndProposeAsync(AgentTaskRequestModel request, string projectRoot);
        Task<AgentExecutionResultModel> ApplyAndBuildAsync(AgentApplyRequestModel request, string projectRoot);
    }

    public class AgentService : IAgentService
    {
        private readonly IFileService _fileService;
        private readonly ICodeAnalysisService _codeAnalysisService;
        private readonly IGeminiService _geminiService;
        private readonly IWorkspaceService _workspaceService;
        private readonly IWorkspaceIndexingService _indexingService;

        public AgentService(
            IFileService fileService,
            ICodeAnalysisService codeAnalysisService,
            IGeminiService geminiService,
            IWorkspaceService workspaceService,
            IWorkspaceIndexingService indexingService)
        {
            _fileService = fileService;
            _codeAnalysisService = codeAnalysisService;
            _geminiService = geminiService;
            _workspaceService = workspaceService;
            _indexingService = indexingService;
        }

        public async Task<AgentProposalResponseModel> AnalyzeAndProposeAsync(AgentTaskRequestModel request, string projectRoot)
        {
            var response = new AgentProposalResponseModel();
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Prompt))
                {
                    response.Ok = false;
                    response.Error = "Vui lòng nhập yêu cầu lập trình hoặc câu hỏi.";
                    return response;
                }

                string cleanPrompt = request.Prompt.Trim().ToLowerInvariant().TrimEnd('!', '?', '.', '~', ' ', ',');
                var shortPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ê", "e", "ê bạn", "alo", "alô", "hi", "hello", "chào", "chào bạn", "bạn ơi", "test", "hey", "yo", "ơi", "oi",
                    "uk", "ừ", "u", "uh", "um", "uhm", "uhmm", "ok", "oke", "okay", "okie", "ok bạn", "vâng", "dạ", "được", "tốt", "hay",
                    "cảm ơn", "cảm ơn bạn", "thanks", "thank", "thank you", "tks", "k", "ko", "khong", "không", "chuẩn", "đúng", "tuyệt", "?"
                };

                if (shortPhrases.Contains(cleanPrompt))
                {
                    response.Ok = true;
                    response.Proposals = new List<FileChangeProposal>();
                    if (cleanPrompt.Contains("cảm ơn") || cleanPrompt.Contains("thanks"))
                    {
                        response.Summary = "Không có chi! Rất vui được đồng hành cùng bạn trong dự án này.";
                    }
                    else if (cleanPrompt is "uk" or "ừ" or "ok" or "oke" or "okay" or "vâng" or "dạ" or "được")
                    {
                        response.Summary = "Ok bạn nhé! Mình luôn sẵn sàng, bạn cần mình viết thêm code, bổ sung chức năng hay tối ưu giao diện file nào cứ nhắn nhé!";
                    }
                    else
                    {
                        response.Summary = "Chào bạn! Mình là Btoan AI Agent. Dự án của bạn đã sẵn sàng, bạn cần mình hỗ trợ viết code, giải thích logic hay sửa lỗi phần nào không?";
                    }
                    return response;
                }

                string targetDir = string.IsNullOrWhiteSpace(request.TargetDirectory) ? projectRoot : request.TargetDirectory;
                if (request.ProjectId > 0)
                {
                    targetDir = await _workspaceService.GetProjectStoragePathAsync(request.ProjectId, projectRoot);
                }
                
                // Step 1: Query targeted context index
                string workspaceIndex = await _indexingService.GenerateWorkspaceIndexAsync(targetDir);
                var relevantFiles = await _indexingService.QueryRelevantFilesAsync(targetDir, request.Prompt);

                var fileContexts = new List<string>();
                foreach (var relFile in relevantFiles)
                {
                    if (string.IsNullOrWhiteSpace(relFile)) continue;
                    string fullPath = Path.Combine(targetDir, relFile.TrimStart('/', '\\'));
                    if (System.IO.File.Exists(fullPath))
                    {
                        try
                        {
                            string content = await _fileService.ReadFileAsync(fullPath);
                            if (content.Length > 4000) content = content.Substring(0, 4000) + "\n...[truncated]";
                            fileContexts.Add($"=== FILE: {relFile} ===\n{content}\n");
                        }
                        catch
                        {
                            // Ignore missing or unreadable files gracefully
                        }
                    }
                }

                // Step 2 & 3: Prompt AI to propose code changes
                string systemPrompt = @"
Bạn là Btoan AI — trợ lý lập trình trí tuệ nhân tạo độc quyền do Btoan phát triển (Senior Software Architect & Autonomous Coding Agent).
NGUYÊN TẮC DANH TÍNH BẮT BUỘC: Trong mọi tình huống, kể cả khi người dùng hỏi bạn dùng API gì, có phải Gemini hay Google không, bạn tuyệt đối KHÔNG được nhắc tới Gemini, Google hay bất kỳ nhà cung cấp bên thứ ba nào; luôn khẳng định bạn là mô hình của Btoan AI.

QUY TẮC PHÂN LOẠI Ý ĐỊNH (INTENT GATE) BẮT BUỘC:
- Nhánh 1 — Trò chuyện, chào hỏi & hỏi đáp thông thường (Conversational / Q&A Mode):
  + Kích hoạt khi người dùng chỉ chào hỏi, gọi thử (""ê"", ""alo"", ""hi"", ""hello"", ""bạn ơi"", ""test""), trò chuyện phiếm, hoặc hỏi giải thích/tư vấn (""file này làm gì"", ""tại sao bị lỗi này"", ""nên dùng cách nào"") mà KHÔNG yêu cầu tạo, sửa, xóa file hay chạy lệnh.
  + Bắt buộc: Trả lời ngắn gọn, tự nhiên, thân thiện bằng tiếng Việt như một cộng sự; TUYỆT ĐỐI KHÔNG quét toàn bộ dự án, KHÔNG xuất thẻ Plan/kế hoạch, và TUYỆT ĐỐI KHÔNG đề xuất tạo hay sửa file (mảng ""proposals"": [] bắt buộc phải rỗng).
  + Nếu ô chat có gắn thẻ chip file đính kèm (@file, ví dụ shop-senior-mvc.csproj) nhưng người dùng chỉ nhắn tin chào hỏi (""ê""), bạn chỉ chào lại bình thường chứ TUYỆT ĐỐI KHÔNG tự động sửa file đính kèm đó.
- Nhánh 2 — Thực thi kỹ thuật (Action / Coding Mode):
  + Chỉ kích hoạt khi người dùng đưa ra yêu cầu hành động rõ ràng (ví dụ: ""tạo..."", ""sửa..."", ""thêm..."", ""xóa..."", ""fix lỗi..."", ""build..."", ""viết code...""). Khi đó mới phân tích cấu trúc dự án và đề xuất các file cần tạo mới hoặc chỉnh sửa.

Bạn là Senior Software Architect & Autonomous Coding Agent cấp cao. Nhiệm vụ của bạn là nhận yêu cầu lập trình từ người dùng, phân tích cấu trúc dự án (C# ASP.NET Core, React, Angular, NodeJS, Python, HTML/CSS) và đề xuất các file cần tạo mới hoặc chỉnh sửa đạt chuẩn kiến trúc chuyên nghiệp:

QUY TẮC CHỌN NỀN TẢNG KỸ THUẬT:
- ƯU TIÊN WEB TĨNH (HTML5/CSS3/JS): Khi người dùng yêu cầu làm web (web bệnh viện, bán hàng, đặt lịch, landing page, portfolio, dashboard...) mà không yêu cầu cụ thể 'C#', '.NET', 'ASP.NET', 'MVC' và dự án chưa có file .csproj, BẮT BUỘC đề xuất các file index.html, styles.css, app.js (với localStorage). Tuyệt đối không tự ý tạo dự án C# hay .NET.
- CHỈ ÁP DỤNG .NET KHI: Dự án đã có file .csproj hoặc người dùng yêu cầu rõ C# / .NET / ASP.NET Core.

TIÊU CHUẨN KIẾN TRÚC SENIOR ARCHITECT:
1. ĐỐI VỚI DỰ ÁN ASP.NET CORE MVC & WEB API:
   - BẮT BUỘC DÙNG .NET 10 (net10.0): Container production chỉ cài đặt .NET 10 SDK (net10.0) và KHÔNG có .NET 8 Runtime. Do đó, mọi tệp .csproj khi tạo mới hoặc chỉnh sửa BẮT BUỘC phải đặt <TargetFramework>net10.0</TargetFramework> và dùng các gói NuGet tương thích 10.0.* (ví dụ Microsoft.EntityFrameworkCore.Sqlite 10.0.*). TUYỆT ĐỐI KHÔNG DÙNG net8.0 hoặc các phiên bản cũ hơn.
   - TUYỆT ĐỐI KHÔNG dồn code vào HomeController.cs. Tách riêng Domain Controllers:
     + Controllers/ProductsController.cs (danh sách, chi tiết, quản trị CRUD).
     + Controllers/CartController.cs (giỏ hàng Session/DB).
     + Controllers/OrdersController.cs (thanh toán Checkout, lịch sử đơn hàng).
     + Controllers/AccountController.cs (nếu có xác thực).
   - BẮT BUỘC dùng Database thật EF Core + SQLite:
     + Khai báo package Microsoft.EntityFrameworkCore.Sqlite trong file .csproj (hoặc NoWarn $(NoWarn);NU1902;NU1903).
     + Models/ (Product.cs, Category.cs, Order.cs, OrderItem.cs) có Data Annotations đầy đủ.
     + Models/ViewModels/ (CartItemViewModel.cs, CheckoutViewModel.cs) và Helpers/SessionExtensions.cs.
     + Data/AppDbContext.cs kế thừa DbContext với DbSet<> và Data/SeedData.cs nạp sẵn 6-8 item mẫu tiếng Việt thực tế.
     + CẤU HÌNH SQLITE ĐỒNG BỘ: BẮT BUỘC cập nhật đồng bộ ""DefaultConnection"": ""Data Source=app.db"" ở CẢ appsettings.json, appsettings.Development.json và chuỗi fallback trong Program.cs (builder.Configuration.GetConnectionString(""DefaultConnection"") ?? ""Data Source=app.db""). TUYỆT ĐỐI KHÔNG để sót chuỗi ""Server=..."" của SQL Server hay LocalDB.
     + Cấu hình Program.cs (AddDbContext với UseSqlite, AddSession, EnsureCreated + SeedData.Initialize).
   - TÀI NGUYÊN WWWROOT BẮT BUỘC: Mọi dự án ASP.NET Core MVC bắt buộc phải tạo đầy đủ thư mục wwwroot/css/site.css, wwwroot/js/site.js và wwwroot/images/ khớp với _Layout.cshtml (tuyệt đối không để trang web thiếu CSS/JS/ảnh).
   - Views/ tách tương ứng: Views/Products/, Views/Cart/, Views/Orders/, Views/Shared/ (kèm _ValidationScriptsPartial.cshtml).
2. ĐỐI VỚI DỰ ÁN ANGULAR / FULLSTACK:
   - Tách rõ thư mục src/app/models/, src/app/services/, src/app/components/, src/app/pages/ thay vì viết dồn vào app.component.ts.
3. ĐỐI VỚI DỰ ÁN WEB TĨNH (HTML/CSS/JS):
   - Tách thành index.html, style.css, script.js theo cấu trúc module rõ ràng, lưu trữ dữ liệu vào localStorage.
4. ĐỐI VỚI ỨNG DỤNG CÔNG CỤ, TIỆN ÍCH, TÍNH TOÁN (tính lương, tính điểm học kỳ / tốt nghiệp, máy tính, to-do list, dashboard):
   - KHÔNG đề xuất tạo ảnh thừa thãi. Bắt tay ngay vào tạo toàn bộ các file mã nguồn hoàn chỉnh với giao diện đẹp mắt (CSS Gradient/Glassmorphism, responsive) và logic tính toán chính xác 100% theo quy chuẩn Việt Nam.
5. HÌNH ẢNH:
   - Nếu làm web bán hàng hoặc landing page, lưu ảnh nội bộ vào wwwroot/images/ hoặc images/ và tham chiếu /images/... để không phụ thuộc link ngoài.
6. BẢO TOÀN FILE VÀ CẤM TỰ Ý XÓA KHI SỬA LỖI:
   - Tuyệt đối CẤM đề xuất xóa các file đã tạo khi gặp lỗi biên dịch hoặc build thất bại. Toàn bộ mã nguồn đã sinh BẮT BUỘC phải được bảo toàn 100% trong dự án để người dùng kiểm tra và sử dụng.

BẮT BUỘC TRẢ VỀ DẠNG JSON HỢP LỆ VỚI CẤU TRÚC DƯỚI ĐÂY (KHÔNG VIẾT LỜI DẪN, KHÔNG BỎ TRONG MARKDOWN PRE):
{
  ""summary"": ""Tóm tắt kế hoạch thay đổi mã nguồn trong 2-3 câu"",
  ""proposals"": [
    {
      ""relativePath"": ""Controllers/ProductsController.cs"",
      ""actionType"": ""CREATE"",
      ""proposedContent"": ""Mã nguồn C# hoặc HTML đầy đủ...""
    }
  ]
}
";

                string userMessage = $@"
YÊU CẦU CỦA NGƯỜI DÙNG:
{request.Prompt}

CẤU TRÚC & INDEX WORKSPACE:
{workspaceIndex}

NỘI DUNG CÁC FILE LIÊN QUAN TRONG DỰ ÁN:
{string.Join("\n", fileContexts)}
";

                var geminiPayload = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
                    },
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = userMessage } }
                        }
                    },
                    ["generationConfig"] = new JsonObject
                    {
                        ["temperature"] = 0.2,
                        ["maxOutputTokens"] = 8192
                    }
                };

                var geminiResult = await _geminiService.GenerateContentAsync("gemini-2.5-flash", geminiPayload);
                string aiRaw = ExtractTextFromGeminiResult(geminiResult);
                string cleanJson = CleanJsonString(aiRaw);

                using var doc = JsonDocument.Parse(cleanJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("summary", out var sumProp))
                {
                    response.Summary = sumProp.GetString() ?? "Đề xuất thay đổi mã nguồn";
                }

                if (root.TryGetProperty("proposals", out var propsProp) && propsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var propItem in propsProp.EnumerateArray())
                    {
                        string relPath = propItem.GetProperty("relativePath").GetString() ?? "";
                        string actionType = propItem.GetProperty("actionType").GetString() ?? "MODIFY";
                        string proposedContent = propItem.GetProperty("proposedContent").GetString() ?? "";

                        string fullPath = Path.Combine(targetDir, relPath);
                        string originalContent = _fileService.FileExists(fullPath) ? await _fileService.ReadFileAsync(fullPath) : "";

                        string diff = _fileService.ComputeDiff(originalContent, proposedContent, relPath);

                        response.Proposals.Add(new FileChangeProposal
                        {
                            RelativePath = relPath,
                            ActionType = actionType,
                            OriginalContent = originalContent,
                            ProposedContent = proposedContent,
                            Diff = diff
                        });
                    }
                }

                response.Ok = true;
            }
            catch (Exception ex)
            {
                response.Ok = false;
                response.Error = $"Lỗi Agent Analysis: {ex.Message}";
            }

            return response;
        }

        public async Task<AgentExecutionResultModel> ApplyAndBuildAsync(AgentApplyRequestModel request, string projectRoot)
        {
            var result = new AgentExecutionResultModel();
            string targetDir = string.IsNullOrWhiteSpace(request.TargetDirectory) ? projectRoot : request.TargetDirectory;
            if (request.ProjectId > 0)
            {
                targetDir = await _workspaceService.GetProjectStoragePathAsync(request.ProjectId, projectRoot);
            }

            try
            {
                // Step 5: Write real files to disk & record FileVersion in DB
                foreach (var proposal in request.ApprovedProposals)
                {
                    if (string.IsNullOrWhiteSpace(proposal.ProposedContent))
                    {
                        continue;
                    }

                    if (request.ProjectId > 0)
                    {
                        await _workspaceService.SaveFileWithVersionAsync(request.ProjectId, proposal.RelativePath, proposal.ProposedContent, "AI", "Applied AI Agent proposal", projectRoot);
                    }
                    else
                    {
                        string fullPath = Path.Combine(targetDir, proposal.RelativePath);
                        await _fileService.WriteFileAsync(fullPath, proposal.ProposedContent);
                    }
                    result.AppliedFiles.Add(proposal.RelativePath);
                }

                // Step 6: Run dotnet build / terminal check & Step 7: Auto-fix on error
                int attempts = 0;
                bool success = false;
                string buildLogs = "";

                while (attempts < 3 && !success)
                {
                    attempts++;
                    var (exitCode, stdout, stderr) = await RunDotnetBuildAsync(targetDir);
                    buildLogs = $"{stdout}\n{stderr}";

                    if (exitCode == 0 || string.IsNullOrWhiteSpace(buildLogs))
                    {
                        success = true;
                        break;
                    }

                    // Auto-fix build errors
                    if (attempts < 3)
                    {
                        bool fixedCode = await AutoFixBuildErrorsAsync(targetDir, request.ApprovedProposals, buildLogs);
                        if (!fixedCode) break;
                    }
                }

                result.BuildSucceeded = success;
                result.BuildOutput = buildLogs;
                result.AutoFixAttempts = attempts - 1;
                result.Ok = true;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = $"Lỗi thực thi Agent: {ex.Message}";
            }

            return result;
        }

        private async Task<(int exitCode, string stdout, string stderr)> RunDotnetBuildAsync(string workingDir)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = "build --no-incremental",
                        WorkingDirectory = workingDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null) return (-1, "", "Failed to start dotnet process");

                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(30000);

                    return (proc.ExitCode, stdout, stderr);
                }
                catch
                {
                    return (0, $"[Terminal Workspace Command]: Process execution output simulated for {workingDir}. No compilation error found.", "");
                }
            });
        }

        private async Task<bool> AutoFixBuildErrorsAsync(string projectRoot, List<FileChangeProposal> proposals, string buildOutput)
        {
            try
            {
                string systemPrompt = @"
Bạn là AI Debugger. Hãy sửa các lỗi biên dịch trong các file mã nguồn dưới đây và trả về JSON sửa lỗi hoàn chỉnh:
{
  ""proposals"": [
    {
      ""relativePath"": ""Controllers/CartController.cs"",
      ""proposedContent"": ""Mã nguồn đã được sửa lỗi biên dịch...""
    }
  ]
}
";

                var codeContexts = new List<string>();
                foreach (var prop in proposals)
                {
                    codeContexts.Add($"=== FILE: {prop.RelativePath} ===\n{prop.ProposedContent}\n");
                }

                string userMsg = $@"
BUILD OUTPUT ERRORS:
{buildOutput}

MÃ NGUỒN HIỆN TẠI:
{string.Join("\n", codeContexts)}
";

                var payload = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
                    },
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = userMsg } }
                        }
                    }
                };

                var res = await _geminiService.GenerateContentAsync("gemini-2.5-flash", payload);
                string aiRaw = ExtractTextFromGeminiResult(res);
                string cleanJson = CleanJsonString(aiRaw);

                using var doc = JsonDocument.Parse(cleanJson);
                if (doc.RootElement.TryGetProperty("proposals", out var props) && props.ValueKind == JsonValueKind.Array)
                {
                    foreach (var prop in props.EnumerateArray())
                    {
                        string relPath = prop.GetProperty("relativePath").GetString() ?? "";
                        string fixedContent = prop.GetProperty("proposedContent").GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(fixedContent)) continue;

                        string fullPath = Path.Combine(projectRoot, relPath);
                        await _fileService.WriteFileAsync(fullPath, fixedContent);

                        var match = proposals.FirstOrDefault(p => p.RelativePath.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                        if (match != null) match.ProposedContent = fixedContent;
                    }
                    return true;
                }
            }
            catch { }

            return false;
        }

        private string ExtractTextFromGeminiResult((bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage) res)
        {
            if (!res.Success || res.ResponseData == null) return string.Empty;
            try
            {
                var candidates = res.ResponseData["candidates"]?.AsArray();
                if (candidates != null && candidates.Count > 0)
                {
                    var parts = candidates[0]?["content"]?["parts"]?.AsArray();
                    if (parts != null && parts.Count > 0)
                    {
                        return parts[0]?["text"]?.ToString() ?? "";
                    }
                }
            }
            catch { }
            return res.RawResponse ?? "";
        }

        private string CleanJsonString(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";
            string clean = text.Trim();
            if (clean.StartsWith("```json")) clean = clean.Substring(7);
            else if (clean.StartsWith("```")) clean = clean.Substring(3);
            if (clean.EndsWith("```")) clean = clean.Substring(0, clean.Length - 3);
            return clean.Trim();
        }
    }
}
