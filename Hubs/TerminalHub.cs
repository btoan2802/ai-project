using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BtoanAiNet.Controllers;
using BtoanAiNet.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace BtoanAiNet.Hubs
{
    public class TerminalInteractiveSession
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string SessionId { get; set; } = "term-1";
        public int UserId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string ProjectRootPath { get; set; } = string.Empty;
        public string CurrentDirectory { get; set; } = string.Empty;
        public Process? ActiveProcess { get; set; }
        public CancellationTokenSource? Cts { get; set; }
    }

    public class TerminalHub : Hub
    {
        private static readonly ConcurrentDictionary<string, TerminalInteractiveSession> Sessions = new();
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<TerminalHub> _logger;

        private static string GetSessionKey(string connectionId, string? sessionId)
        {
            string sid = string.IsNullOrWhiteSpace(sessionId) ? "term-1" : sessionId.Trim();
            return $"{connectionId}_{sid}";
        }

        private static readonly Regex LocalUrlRegex = new(
            @"https?:\/\/(localhost|127\.0\.0\.1)(:\d+)?(\/[^\s]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] DangerousCommands = new[]
        {
            "format ", "del /s /q c:", "rmdir /s /q c:", "shutdown", "reg add", "reg delete",
            "bcdedit", "net user", "vssadmin", "attrib -h -r -s c:"
        };

        private static readonly string[] AllowedCommandPrefixes = new[]
        {
            "node", "npm", "npx", "ng", "yarn", "pnpm", "vite", "tsc",
            "dotnet", "python", "py", "pip", "uvicorn", "flask", "pytest",
            "java", "javac", "mvn", "gradle",
            "php", "composer",
            "go", "cargo", "docker", "git",
            "dir", "ls", "pwd", "cd", "mkdir", "cat", "type", "echo", "cls", "clear",
            "where", "whoami", "ping", "if", "else", "try", "catch", "for", "foreach", "while",
            "powershell", "pwsh", "test-path", "get-childitem", "select-string", "start-process", "write-output"
        };

        public TerminalHub(IWebHostEnvironment env, ILogger<TerminalHub> logger)
        {
            _env = env;
            _logger = logger;
        }

        private int GetCurrentUserId()
        {
            var claimId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (int.TryParse(claimId, out int uid) && uid > 0) return uid;

            var httpContext = Context.GetHttpContext();
            if (httpContext?.Session != null)
            {
                var sUid = httpContext.Session.GetInt32("user_id");
                if (sUid.HasValue && sUid.Value > 0) return sUid.Value;
            }

            // Check query string: ?user_id=1
            if (httpContext?.Request.Query.TryGetValue("user_id", out var qUid) == true
                && int.TryParse(qUid, out int parsedQUid) && parsedQUid > 0)
            {
                return parsedQUid;
            }

            // Check cookie: user_id
            if (httpContext?.Request.Cookies.TryGetValue("user_id", out var cUid) == true
                && int.TryParse(cUid, out int parsedCUid) && parsedCUid > 0)
            {
                return parsedCUid;
            }

            // Check header: X-User-Id
            if (httpContext?.Request.Headers.TryGetValue("X-User-Id", out var hUid) == true
                && int.TryParse(hUid, out int parsedHUid) && parsedHUid > 0)
            {
                return parsedHUid;
            }

            if (Context.User?.Identity?.IsAuthenticated == true)
            {
                return 1; // Default to admin for authenticated users
            }

            // Default to admin user (1) for interactive Web IDE terminal session
            return 1;
        }

        private string ResolveProjectRoot(int userId, string projectName)
        {
            string cleanProj = Regex.Replace(projectName ?? "shop-senior-mvc", @"[^a-zA-Z0-9_\-\.]", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(cleanProj) || cleanProj == "demo" || cleanProj == "demo-app")
            {
                cleanProj = "shop-senior-mvc";
            }

            string userRoot = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", $"user_{userId}");
            if (!Directory.Exists(userRoot))
            {
                Directory.CreateDirectory(userRoot);
            }
            Controllers.IdeController.EnsureDirectoryBuildProps(userRoot);

            string projectPath = Path.Combine(userRoot, cleanProj);
            if (!Directory.Exists(projectPath))
            {
                // Never create empty demo folder - fallback to existing projects if available
                var existingDirs = Directory.GetDirectories(userRoot)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith(".") && n != "demo" && n != "demo-app")
                    .ToList();
                if (existingDirs.Count > 0)
                {
                    var fallbackProj = existingDirs.FirstOrDefault(d => d == "shop-senior-mvc") ?? existingDirs.FirstOrDefault() ?? "shop-senior-mvc";
                    return Path.Combine(userRoot, fallbackProj);
                }
                Directory.CreateDirectory(projectPath);
            }
            return projectPath;
        }

        public async Task InitializeTerminal(string project, string sessionId)
        {
            string sId = string.IsNullOrWhiteSpace(sessionId) ? "term-1" : sessionId.Trim();
            string key = GetSessionKey(Context.ConnectionId, sId);

            int userId = GetCurrentUserId();
            if (userId <= 0)
            {
                await Clients.Caller.SendAsync("TerminalOutput", 
                    "\r\n\x1b[31m[Restricted Mode] Vui lòng đăng nhập để sử dụng Terminal tương tác.\x1b[0m\r\n", sId);
                await Clients.Caller.SendAsync("SessionInitialized", false, "", sId);
                return;
            }

            string rootPath = ResolveProjectRoot(userId, project);
            Controllers.IdeController.AutoFixMapStaticAssets(rootPath);

            // Chống in lặp banner: Nếu connection đã có session đang sống tại đúng thư mục này thì tái sử dụng luôn
            if (Sessions.TryGetValue(key, out var existingSession) && 
                existingSession.UserId == userId && 
                string.Equals(existingSession.ProjectRootPath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Caller.SendAsync("SessionInitialized", true, existingSession.CurrentDirectory, sId);
                return;
            }

            var session = new TerminalInteractiveSession
            {
                ConnectionId = Context.ConnectionId,
                SessionId = sId,
                UserId = userId,
                ProjectName = project,
                ProjectRootPath = rootPath,
                CurrentDirectory = rootPath
            };

            Sessions[key] = session;

            await Clients.Caller.SendAsync("SessionInitialized", true, session.CurrentDirectory, sId);
            await Clients.Caller.SendAsync("TerminalOutput", 
                $"\x1b[36mBtoan Agent Multi-Environment Interactive Terminal (Node, Angular, Python, Java, .NET)\x1b[0m\r\n" +
                $"\x1b[90mSession started [{sId}]. Workspace: {Path.GetFileName(rootPath)}\x1b[0m\r\n\r\n" +
                $"PS {session.CurrentDirectory}> ", sId);
        }

        public async Task ExecuteCommand(string project, string rawCommand)
        {
            await ExecuteCommandInSession(project, rawCommand, "term-1");
        }

        public async Task ExecuteCommandInSession(string project, string rawCommand, string sessionId)
        {
            string sId = string.IsNullOrWhiteSpace(sessionId) ? "term-1" : sessionId.Trim();
            string key = GetSessionKey(Context.ConnectionId, sId);

            int userId = GetCurrentUserId();
            if (userId <= 0)
            {
                await Clients.Caller.SendAsync("TerminalOutput", 
                    "\r\n\x1b[31m[Restricted Mode] Thao tác bị từ chối: Chưa đăng nhập.\x1b[0m\r\n", sId);
                return;
            }

            if (!Sessions.TryGetValue(key, out var session))
            {
                string root = ResolveProjectRoot(userId, project);
                session = new TerminalInteractiveSession
                {
                    ConnectionId = Context.ConnectionId,
                    SessionId = sId,
                    UserId = userId,
                    ProjectName = project,
                    ProjectRootPath = root,
                    CurrentDirectory = root
                };
                Sessions[key] = session;
            }

            string trimmedCmd = rawCommand?.Trim() ?? string.Empty;
            if (session == null) return;
            if (!string.IsNullOrWhiteSpace(session.ProjectRootPath))
            {
                Controllers.IdeController.AutoFixMapStaticAssets(session.ProjectRootPath);
            }
            if (string.IsNullOrWhiteSpace(trimmedCmd))
            {
                await Clients.Caller.SendAsync("TerminalOutput", $"\r\nPS {session.CurrentDirectory}> ", sId);
                return;
            }

            // If a previous process is still actively running in this session, terminate it cleanly before starting new command
            if (session.ActiveProcess != null && !session.ActiveProcess.HasExited)
            {
                _logger.LogInformation("[TerminalHub] Cleaning up active process before executing new command in session {SessionId}", sId);
                KillSessionProcess(session);
            }

            // 1. Internal cls / clear
            if (string.Equals(trimmedCmd, "cls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmedCmd, "clear", StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Caller.SendAsync("TerminalClear", sId);
                await Clients.Caller.SendAsync("TerminalOutput", $"PS {session.CurrentDirectory}> ", sId);
                return;
            }

            // 2. Internal pwd
            if (string.Equals(trimmedCmd, "pwd", StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Caller.SendAsync("TerminalOutput", $"\r\n{session.CurrentDirectory}\r\n\r\nPS {session.CurrentDirectory}> ", sId);
                return;
            }

            // 3. Internal cd navigation & compound command chaining
            if (trimmedCmd.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmedCmd, "cd", StringComparison.OrdinalIgnoreCase))
            {
                int sepIdx = -1;
                string sep = "";
                int idxAmp = trimmedCmd.IndexOf("&&", StringComparison.Ordinal);
                int idxSemi = trimmedCmd.IndexOf(';');
                if (idxAmp >= 0 && (idxSemi < 0 || idxAmp < idxSemi))
                {
                    sepIdx = idxAmp;
                    sep = "&&";
                }
                else if (idxSemi >= 0)
                {
                    sepIdx = idxSemi;
                    sep = ";";
                }

                string cdPart = sepIdx >= 0 ? trimmedCmd.Substring(0, sepIdx).Trim() : trimmedCmd;
                string? nextCmd = sepIdx >= 0 ? trimmedCmd.Substring(sepIdx + sep.Length).Trim() : null;

                string targetSub = cdPart.Length > 3 ? cdPart.Substring(3).Trim(' ', '"', '\'') : "";
                if (!string.IsNullOrEmpty(targetSub))
                {
                    string newPath = Path.GetFullPath(Path.Combine(session.CurrentDirectory, targetSub));

                    // If already inside target subfolder, gracefully accept
                    if (!Directory.Exists(newPath) && string.Equals(Path.GetFileName(session.CurrentDirectory), targetSub, StringComparison.OrdinalIgnoreCase))
                    {
                        newPath = session.CurrentDirectory;
                    }

                    if (!newPath.StartsWith(session.ProjectRootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await Clients.Caller.SendAsync("TerminalOutput", 
                            $"\r\n\x1b[31m[Security Violation] Không thể chuyển thư mục ra ngoài phạm vi dự án: {session.ProjectName}\x1b[0m\r\n\r\nPS {session.CurrentDirectory}> ", sId);
                        return;
                    }

                    if (!Directory.Exists(newPath))
                    {
                        await Clients.Caller.SendAsync("TerminalOutput", 
                            $"\r\n\x1b[31mHệ thống không tìm thấy đường dẫn được chỉ định: {targetSub}\x1b[0m\r\n\r\nPS {session.CurrentDirectory}> ", sId);
                        return;
                    }

                    session.CurrentDirectory = newPath;
                }

                if (string.IsNullOrEmpty(nextCmd))
                {
                    await Clients.Caller.SendAsync("TerminalOutput", $"\r\nPS {session.CurrentDirectory}> ", sId);
                    return;
                }

                trimmedCmd = nextCmd;
            }

            // 4. Security Check
            string lowerCmd = trimmedCmd.ToLowerInvariant();
            foreach (var blocked in DangerousCommands)
            {
                if (lowerCmd.Contains(blocked))
                {
                    await Clients.Caller.SendAsync("TerminalOutput", 
                        $"\r\n\x1b[31m[Security Error] Lệnh chứa từ khóa nguy hiểm bị cấm: '{blocked}'\x1b[0m\r\n\r\nPS {session.CurrentDirectory}> ", sId);
                    return;
                }
            }

            // 5. Check allowed command prefix
            string baseExecutable = lowerCmd.Split(' ', '\t')[0].Trim('(', '{', '$', '"', '\'');
            bool isAllowed = false;
            foreach (var prefix in AllowedCommandPrefixes)
            {
                if (baseExecutable == prefix || baseExecutable.EndsWith("\\" + prefix) || baseExecutable.EndsWith("/" + prefix))
                {
                    isAllowed = true;
                    break;
                }
            }

            if (!isAllowed)
            {
                _logger.LogInformation("[TerminalHub] Executing custom/external command: '{Executable}' in session {SessionId}", baseExecutable, sId);
            }

            // 6. Execute process with real-time streaming
            if (trimmedCmd.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.Contains("dotnet run", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.Contains("ng serve", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.Contains("npm start", StringComparison.OrdinalIgnoreCase))
            {
                await RunningAppSessionManager.KillProjectProcessesAsync(session.ProjectName, session.UserId);
            }

            if (trimmedCmd.Contains("dotnet run", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.Contains("dotnet watch", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.Contains("npm start", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.Contains("ng serve", StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Caller.SendAsync("TerminalOutput", "\x1b[36m💡 Mẹo: Bạn có thể bấm nút Run (▶) trên thanh công cụ để chạy nền và tự động mở Live Preview.\x1b[0m\r\n", sId);
            }

            await RunProcessStreamingAsync(session, trimmedCmd);
        }

        private async Task RunProcessStreamingAsync(TerminalInteractiveSession session, string command)
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = session.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (OperatingSystem.IsWindows())
            {
                string script = $"$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8;\n{command}";
                byte[] bytes = Encoding.Unicode.GetBytes(script);
                string encoded = Convert.ToBase64String(bytes);

                psi.FileName = "powershell.exe";
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";
            }
            else
            {
                psi.FileName = "/bin/bash";
                psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
            }

            // Set environment paths
            psi.Environment["CI"] = "false";
            psi.Environment["FORCE_COLOR"] = "1";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5200";
            psi.Environment["PORT"] = "5200";
            psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
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

            // Inject project secrets into environment variables
            try
            {
                string userRoot = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", $"user_{session.UserId}");
                var secrets = IdeController.LoadProjectSecrets(userRoot, session.ProjectName);
                foreach (var kvp in secrets)
                {
                    psi.Environment[kvp.Key] = kvp.Value;
                }
            }
            catch { }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            session.ActiveProcess = process;
            session.Cts = new CancellationTokenSource();

            var caller = Clients.Caller;
            string sId = session.SessionId;
            int exitCode = -1;

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    ScanForDevServerUrl(caller, e.Data, session);
                    _ = caller.SendAsync("TerminalOutput", e.Data + "\r\n", sId);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    ScanForDevServerUrl(caller, e.Data, session);
                    _ = caller.SendAsync("TerminalOutput", "\x1b[33m" + e.Data + "\x1b[0m\r\n", sId);
                }
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token, timeoutCts.Token);

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(linkedCts.Token);
                exitCode = process.ExitCode;

                // Notify client that command completed
                await caller.SendAsync("TerminalCommandFinished", new { exitCode, command, sessionId = sId }, sId);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (timeoutCts.IsCancellationRequested)
                {
                    await caller.SendAsync("TerminalOutput", "\r\n\x1b[33m⏱️ [Timeout 90s] Lệnh đã chạy quá 90 giây. Nếu muốn chạy server liên tục, vui lòng bấm nút Run (▶) để chạy nền mà không chiếm giữ Terminal.\x1b[0m\r\n", sId);
                }
                else
                {
                    await caller.SendAsync("TerminalOutput", "\r\n\x1b[31m^C (Process terminated by user)\x1b[0m\r\n", sId);
                }
                await caller.SendAsync("TerminalCommandFinished", new { exitCode = 130, command, sessionId = sId }, sId);
            }
            catch (Exception ex)
            {
                await caller.SendAsync("TerminalOutput", $"\r\n\x1b[31m[Process Error] {ex.Message}\x1b[0m\r\n", sId);
                await caller.SendAsync("TerminalCommandFinished", new { exitCode = 1, command, sessionId = sId }, sId);
            }
            finally
            {
                session.ActiveProcess = null;
                session.Cts?.Dispose();
                session.Cts = null;
                await caller.SendAsync("TerminalOutput", $"\r\nPS {session.CurrentDirectory}> ", sId);
            }
        }

        public async Task KillRunningProcess()
        {
            await KillRunningProcessInSession("term-1");
        }

        public async Task KillRunningProcessInSession(string sessionId)
        {
            string sId = string.IsNullOrWhiteSpace(sessionId) ? "term-1" : sessionId.Trim();

            if (string.Equals(sId, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kvp in Sessions.Where(k => k.Value.ConnectionId == Context.ConnectionId).ToList())
                {
                    KillSessionProcess(kvp.Value);
                }
                RunningAppSessionManager.ReleaseSessionByConnection(Context.ConnectionId);
                await Clients.Caller.SendAsync("AppPreviewStopped", new { });
                return;
            }

            string key = GetSessionKey(Context.ConnectionId, sId);
            if (Sessions.TryGetValue(key, out var session))
            {
                KillSessionProcess(session);
                RunningAppSessionManager.ReleaseSessionByConnection(Context.ConnectionId);
                await Clients.Caller.SendAsync("AppPreviewStopped", new { projectName = session.ProjectName, sessionId = sId });
            }
        }

        private void KillSessionProcess(TerminalInteractiveSession session)
        {
            if (session.ActiveProcess != null && !session.ActiveProcess.HasExited)
            {
                try
                {
                    session.Cts?.Cancel();
                    session.ActiveProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill process tree for connection {ConnectionId}", session.ConnectionId);
                }
                finally
                {
                    session.ActiveProcess = null;
                }
            }
        }

        public async Task ResetTerminalSession(string project)
        {
            await ResetTerminalSessionInSession(project, "term-1");
        }

        public async Task ResetTerminalSessionInSession(string project, string sessionId)
        {
            await KillRunningProcessInSession(sessionId);
            await InitializeTerminal(project, sessionId);
        }

        private void ScanForDevServerUrl(IClientProxy caller, string line, TerminalInteractiveSession session)
        {
            try
            {
                var match = LocalUrlRegex.Match(line);
                if (match.Success)
                {
                    string detectedUrl = match.Value.TrimEnd('.', ',', ')', ']');
                    caller.SendAsync("DevServerDetected", detectedUrl, session.SessionId);

                    var portMatch = Regex.Match(detectedUrl, @":(\d+)");
                    if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out int port))
                    {
                        RunningAppSessionManager.RegisterActiveApp(port, session.UserId, session.ProjectName, session.ActiveProcess?.Id ?? 0, session.ConnectionId);
                        caller.SendAsync("AppPreviewReady", new { port, projectName = session.ProjectName, sessionId = session.SessionId });
                    }
                }
            }
            catch { }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            RunningAppSessionManager.ReleaseSessionByConnection(Context.ConnectionId);

            var toRemove = Sessions.Where(k => k.Value.ConnectionId == Context.ConnectionId).ToList();
            foreach (var kvp in toRemove)
            {
                KillSessionProcess(kvp.Value);
                Sessions.TryRemove(kvp.Key, out _);
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
