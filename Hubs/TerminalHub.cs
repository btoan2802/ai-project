using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace BtoanAiNet.Hubs
{
    public class TerminalInteractiveSession
    {
        public string ConnectionId { get; set; } = string.Empty;
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
            "where", "whoami", "ping"
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

            if (Context.User?.Identity?.IsAuthenticated == true)
            {
                return 1; // Default to admin for authenticated users
            }

            return 0;
        }

        private string ResolveProjectRoot(int userId, string projectName)
        {
            string cleanProj = Regex.Replace(projectName ?? "my-project", @"[^a-zA-Z0-9_\-\.]", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(cleanProj)) cleanProj = "my-project";

            string userRoot = Path.Combine(_env.ContentRootPath, "App_Data", "UserWorkspaces", $"user_{userId}");
            if (!Directory.Exists(userRoot))
            {
                Directory.CreateDirectory(userRoot);
            }

            string projectPath = Path.Combine(userRoot, cleanProj);
            if (!Directory.Exists(projectPath))
            {
                Directory.CreateDirectory(projectPath);
            }
            return projectPath;
        }

        public async Task InitializeTerminal(string project)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0)
            {
                await Clients.Caller.SendAsync("TerminalOutput", 
                    "\r\n\x1b[31m[Restricted Mode] Vui lòng đăng nhập để sử dụng Terminal tương tác.\x1b[0m\r\n");
                await Clients.Caller.SendAsync("SessionInitialized", false, "");
                return;
            }

            string rootPath = ResolveProjectRoot(userId, project);
            var session = new TerminalInteractiveSession
            {
                ConnectionId = Context.ConnectionId,
                UserId = userId,
                ProjectName = project,
                ProjectRootPath = rootPath,
                CurrentDirectory = rootPath
            };

            Sessions[Context.ConnectionId] = session;

            await Clients.Caller.SendAsync("SessionInitialized", true, session.CurrentDirectory);
            await Clients.Caller.SendAsync("TerminalOutput", 
                $"\x1b[36mAntigravity Multi-Environment Interactive Terminal (Node, Angular, Python, Java, .NET)\x1b[0m\r\n" +
                $"\x1b[90mSession started. Workspace: {Path.GetFileName(rootPath)}\x1b[0m\r\n\r\n" +
                $"PS {session.CurrentDirectory}> ");
        }

        public async Task ExecuteCommand(string project, string rawCommand)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0)
            {
                await Clients.Caller.SendAsync("TerminalOutput", 
                    "\r\n\x1b[31m[Restricted Mode] Thao tác bị từ chối: Chưa đăng nhập.\x1b[0m\r\n");
                return;
            }

            if (!Sessions.TryGetValue(Context.ConnectionId, out var session))
            {
                string root = ResolveProjectRoot(userId, project);
                session = new TerminalInteractiveSession
                {
                    ConnectionId = Context.ConnectionId,
                    UserId = userId,
                    ProjectName = project,
                    ProjectRootPath = root,
                    CurrentDirectory = root
                };
                Sessions[Context.ConnectionId] = session;
            }

            string trimmedCmd = rawCommand?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedCmd))
            {
                await Clients.Caller.SendAsync("TerminalOutput", $"\r\nPS {session.CurrentDirectory}> ");
                return;
            }

            // 1. Internal cls / clear
            if (string.Equals(trimmedCmd, "cls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmedCmd, "clear", StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Caller.SendAsync("TerminalClear");
                await Clients.Caller.SendAsync("TerminalOutput", $"PS {session.CurrentDirectory}> ");
                return;
            }

            // 2. Internal pwd
            if (string.Equals(trimmedCmd, "pwd", StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Caller.SendAsync("TerminalOutput", $"\r\n{session.CurrentDirectory}\r\n\r\nPS {session.CurrentDirectory}> ");
                return;
            }

            // 3. Internal cd navigation
            if (trimmedCmd.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmedCmd, "cd", StringComparison.OrdinalIgnoreCase))
            {
                string targetSub = trimmedCmd.Length > 3 ? trimmedCmd.Substring(3).Trim(' ', '"', '\'') : "";
                if (string.IsNullOrEmpty(targetSub))
                {
                    // Print current directory
                    await Clients.Caller.SendAsync("TerminalOutput", $"\r\n{session.CurrentDirectory}\r\n\r\nPS {session.CurrentDirectory}> ");
                    return;
                }

                string newPath = Path.GetFullPath(Path.Combine(session.CurrentDirectory, targetSub));
                if (!newPath.StartsWith(session.ProjectRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    await Clients.Caller.SendAsync("TerminalOutput", 
                        $"\r\n\x1b[31m[Security Violation] Không thể chuyển thư mục ra ngoài phạm vi dự án: {session.ProjectName}\x1b[0m\r\n\r\nPS {session.CurrentDirectory}> ");
                    return;
                }

                if (!Directory.Exists(newPath))
                {
                    await Clients.Caller.SendAsync("TerminalOutput", 
                        $"\r\n\x1b[31mHệ thống không tìm thấy đường dẫn được chỉ định: {targetSub}\x1b[0m\r\n\r\nPS {session.CurrentDirectory}> ");
                    return;
                }

                session.CurrentDirectory = newPath;
                await Clients.Caller.SendAsync("TerminalOutput", $"\r\nPS {session.CurrentDirectory}> ");
                return;
            }

            // 4. Security Check
            string lowerCmd = trimmedCmd.ToLowerInvariant();
            foreach (var blocked in DangerousCommands)
            {
                if (lowerCmd.Contains(blocked))
                {
                    await Clients.Caller.SendAsync("TerminalOutput", 
                        $"\r\n\x1b[31m[Security Error] Lệnh chứa từ khóa nguy hiểm bị cấm: '{blocked}'\x1b[0m\r\n\r\nPS {session.CurrentDirectory}> ");
                    return;
                }
            }

            // 5. Check allowed command prefix
            string baseExecutable = lowerCmd.Split(' ', '\t')[0].Trim();
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
                await Clients.Caller.SendAsync("TerminalOutput", 
                    $"\r\n\x1b[33m[Notice] Lệnh '{baseExecutable}' không nằm trong whitelist chính, đang chuyển sang shell hệ thống...\x1b[0m\r\n");
            }

            // 6. Execute process with real-time streaming
            await RunProcessStreamingAsync(session, trimmedCmd);
        }

        private async Task RunProcessStreamingAsync(TerminalInteractiveSession session, string command)
        {
            string shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
            string args = OperatingSystem.IsWindows() ? $"/c \"{command}\"" : $"-c \"{command.Replace("\"", "\\\"")}\"";

            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = args,
                WorkingDirectory = session.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Set environment paths
            psi.Environment["CI"] = "false";
            psi.Environment["FORCE_COLOR"] = "1";

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            session.ActiveProcess = process;
            session.Cts = new CancellationTokenSource();

            var caller = Clients.Caller;

            process.OutputDataReceived += async (s, e) =>
            {
                if (e.Data != null)
                {
                    ScanForDevServerUrl(caller, e.Data);
                    await caller.SendAsync("TerminalOutput", e.Data + "\r\n");
                }
            };

            process.ErrorDataReceived += async (s, e) =>
            {
                if (e.Data != null)
                {
                    ScanForDevServerUrl(caller, e.Data);
                    await caller.SendAsync("TerminalOutput", "\x1b[33m" + e.Data + "\x1b[0m\r\n");
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(session.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await caller.SendAsync("TerminalOutput", "\r\n\x1b[31m^C (Process terminated by user)\x1b[0m\r\n");
            }
            catch (Exception ex)
            {
                await caller.SendAsync("TerminalOutput", $"\r\n\x1b[31m[Process Error] {ex.Message}\x1b[0m\r\n");
            }
            finally
            {
                session.ActiveProcess = null;
                session.Cts?.Dispose();
                session.Cts = null;
                await caller.SendAsync("TerminalOutput", $"\r\nPS {session.CurrentDirectory}> ");
            }
        }

        public async Task KillRunningProcess()
        {
            if (Sessions.TryGetValue(Context.ConnectionId, out var session))
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
                        _logger.LogWarning(ex, "Failed to kill process tree for connection {ConnectionId}", Context.ConnectionId);
                    }
                    finally
                    {
                        session.ActiveProcess = null;
                    }
                }
            }
        }

        public async Task ResetTerminalSession(string project)
        {
            await KillRunningProcess();
            await InitializeTerminal(project);
        }

        private void ScanForDevServerUrl(IClientProxy caller, string line)
        {
            try
            {
                var match = LocalUrlRegex.Match(line);
                if (match.Success)
                {
                    string detectedUrl = match.Value.TrimEnd('.', ',', ')', ']');
                    caller.SendAsync("DevServerDetected", detectedUrl);
                }
            }
            catch { }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (Sessions.TryRemove(Context.ConnectionId, out var session))
            {
                if (session.ActiveProcess != null && !session.ActiveProcess.HasExited)
                {
                    try
                    {
                        session.Cts?.Cancel();
                        session.ActiveProcess.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
            }
            await base.OnDisconnectedAsync(exception);
        }
    }
}
