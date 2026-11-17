using BtoanAiNet.Models;

namespace BtoanAiNet.Services
{
    /// <summary>
    /// Manages containerized and isolated runtime environments for code execution, compilation, and terminal sessions.
    /// </summary>
    public interface IRuntimeService
    {
        /// <summary>
        /// Provisions an isolated sandbox environment (Docker container or restricted host) and executes a shell command.
        /// </summary>
        /// <param name="projectId">Project workspace identifier.</param>
        /// <param name="command">CLI command or build script to execute.</param>
        /// <param name="userId">Authorized user identifier.</param>
        /// <param name="webRoot">Host web root path for workspace volume mounts.</param>
        /// <returns>A terminal execution session with captured stdout, stderr, and exit codes.</returns>
        Task<TerminalSession> CreateEnvironmentAndExecuteAsync(int projectId, string command, int userId, string webRoot);

        /// <summary>
        /// Retrieves the real-time or historical streaming output of a terminal execution session.
        /// </summary>
        Task<TerminalSession?> GetSessionOutputAsync(int sessionId, int userId);

        /// <summary>
        /// Terminates an actively executing sandbox process or container.
        /// </summary>
        Task<bool> StopExecutionAsync(int sessionId, int userId);
    }
}
