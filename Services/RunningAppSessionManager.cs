using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BtoanAiNet.Services
{
    public class RunningAppSession
    {
        public int Port { get; set; }
        public int UserId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string ConnectionId { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    public static class RunningAppSessionManager
    {
        private static readonly ConcurrentDictionary<int, RunningAppSession> ActiveSessions = new();
        private static readonly object LockObj = new();
        public const int MinPort = 5200;
        public const int MaxPort = 5299;

        public static int AllocatePort(int userId, string projectName, string connectionId = "")
        {
            lock (LockObj)
            {
                CleanupDeadSessions();

                // Check if this project already has a live session for this user
                foreach (var kvp in ActiveSessions)
                {
                    if (kvp.Value.UserId == userId &&
                        string.Equals(kvp.Value.ProjectName, projectName, StringComparison.OrdinalIgnoreCase) &&
                        IsPortInUse(kvp.Key))
                    {
                        return kvp.Key;
                    }
                }

                // Allocate a free port in 5200..5299
                for (int port = MinPort; port <= MaxPort; port++)
                {
                    if (!ActiveSessions.ContainsKey(port) && IsPortAvailable(port))
                    {
                        var session = new RunningAppSession
                        {
                            Port = port,
                            UserId = userId,
                            ProjectName = projectName ?? "app",
                            ConnectionId = connectionId,
                            StartedAt = DateTime.UtcNow
                        };
                        ActiveSessions[port] = session;
                        return port;
                    }
                }

                throw new InvalidOperationException($"Không còn cổng khả dụng trong dải {MinPort}-{MaxPort}.");
            }
        }

        public static void RegisterActiveApp(int port, int userId, string projectName, int processId, string connectionId = "")
        {
            if (port < MinPort || port > MaxPort) return;

            var session = new RunningAppSession
            {
                Port = port,
                UserId = userId,
                ProjectName = projectName ?? "app",
                ProcessId = processId,
                ConnectionId = connectionId,
                StartedAt = DateTime.UtcNow
            };
            ActiveSessions[port] = session;
        }

        public static bool IsPortAuthorized(int port, int userId)
        {
            if (port < MinPort || port > MaxPort) return false;
            if (ActiveSessions.TryGetValue(port, out var session))
            {
                // Authenticated user matches or admin (userId 1)
                return session.UserId == userId || userId == 1;
            }
            return false;
        }

        public static RunningAppSession? GetSession(int port)
        {
            ActiveSessions.TryGetValue(port, out var session);
            return session;
        }

        public static System.Collections.Generic.List<RunningAppSession> GetUserSessions(int userId)
        {
            CleanupDeadSessions();
            var list = new System.Collections.Generic.List<RunningAppSession>();
            foreach (var s in ActiveSessions.Values)
            {
                if (s.UserId == userId || userId == 1)
                {
                    list.Add(s);
                }
            }
            list.Sort((a, b) => a.Port.CompareTo(b.Port));
            return list;
        }

        public static void ReleasePort(int port)
        {
            if (ActiveSessions.TryRemove(port, out var session))
            {
                KillProcessSafe(session.ProcessId);
            }
        }

        public static void ReleaseSessionByConnection(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return;
            foreach (var kvp in ActiveSessions)
            {
                if (kvp.Value.ConnectionId == connectionId)
                {
                    ReleasePort(kvp.Key);
                }
            }
        }

        public static void ReleaseUserSessions(int userId)
        {
            foreach (var kvp in ActiveSessions)
            {
                if (kvp.Value.UserId == userId)
                {
                    ReleasePort(kvp.Key);
                }
            }
        }

        public static void CleanupDeadSessions()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in ActiveSessions)
            {
                var session = kvp.Value;
                bool isDead = false;

                // 1. If process ID is recorded and process has exited
                if (session.ProcessId > 0)
                {
                    try
                    {
                        var p = Process.GetProcessById(session.ProcessId);
                        if (p.HasExited) isDead = true;
                    }
                    catch
                    {
                        isDead = true;
                    }
                }

                // 2. Idle timeout > 30 minutes if port not in use
                if (!isDead && (now - session.StartedAt).TotalMinutes > 30 && !IsPortInUse(session.Port))
                {
                    isDead = true;
                }

                if (isDead)
                {
                    ReleasePort(kvp.Key);
                }
            }
        }

        public static bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsPortInUse(int port)
        {
            return !IsPortAvailable(port);
        }

        private static void KillProcessSafe(int pid)
        {
            if (pid <= 0) return;
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
    }
}
