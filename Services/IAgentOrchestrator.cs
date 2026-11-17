using BtoanAiNet.Models;

namespace BtoanAiNet.Services
{
    /// <summary>
    /// Orchestrates autonomous multi-step software engineering workflows, including planning,
    /// code editing, self-healing execution loops, and automated verification.
    /// </summary>
    public interface IAgentOrchestrator
    {
        /// <summary>
        /// Executes an autonomous multi-phase workflow for a given user prompt against a project workspace.
        /// </summary>
        /// <param name="projectId">Target project workspace identifier.</param>
        /// <param name="prompt">User requirement or engineering feature request.</param>
        /// <param name="userId">Authorized user identifier.</param>
        /// <param name="mode">Execution mode: 'AUTO' for auto-commit or 'SAFE' for human approval review.</param>
        /// <param name="webRoot">Host application web root path for physical disk resolution.</param>
        /// <returns>Result summary containing generated subtasks, code changes, logs, and build status.</returns>
        Task<AutonomousRunResult> RunAutonomousWorkflowAsync(int projectId, string prompt, int userId, string mode, string webRoot);

        /// <summary>
        /// Approves and applies staged code modifications in SAFE review mode.
        /// </summary>
        /// <param name="agentTaskId">Staged agent task identifier.</param>
        /// <param name="userId">Authorizing user identifier.</param>
        /// <param name="webRoot">Host application web root path.</param>
        /// <returns>True if changes were successfully committed to disk and versioned.</returns>
        Task<bool> ApproveAutonomousTaskAsync(int agentTaskId, int userId, string webRoot);

        /// <summary>
        /// Retrieves all autonomous agent tasks associated with a given project workspace.
        /// </summary>
        Task<List<AutonomousAgentTask>> GetProjectAutonomousTasksAsync(int projectId, int userId);

        /// <summary>
        /// Fetches chronological execution logs for active or recent autonomous agent runs.
        /// </summary>
        Task<List<string>> GetExecutionLogsAsync(int projectId, int userId);
    }

    /// <summary>
    /// Represents the comprehensive outcome of an autonomous agent execution cycle.
    /// </summary>
    public class AutonomousRunResult
    {
        public int AgentTaskId { get; set; }
        public int ProjectId { get; set; }
        public string Mode { get; set; } = "AUTO"; // AUTO or SAFE
        public string Status { get; set; } = "Completed";
        public List<AutonomousSubtaskDto> Subtasks { get; set; } = new();
        public List<AgentChangeDto> Changes { get; set; } = new();
        public List<string> ExecutionLogs { get; set; } = new();
        public bool BuildSucceeded { get; set; }
        public int SelfHealRetries { get; set; }
    }

    /// <summary>
    /// Represents an individual planned subtask in the hierarchical decomposition tree.
    /// </summary>
    public class AutonomousSubtaskDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public int Order { get; set; }
    }

    /// <summary>
    /// Encapsulates a discrete file modification staged or applied by the agent.
    /// </summary>
    public class AgentChangeDto
    {
        public string FilePath { get; set; } = string.Empty;
        public string ChangeType { get; set; } = "Create";
        public string OldContent { get; set; } = string.Empty;
        public string NewContent { get; set; } = string.Empty;
    }
}
