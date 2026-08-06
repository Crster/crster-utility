using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace App.Services
{
    /// <summary>A command line coding agent Cody can host, and the flags that stop it asking for approval.</summary>
    internal sealed record CliAgentDefinition(
        string Id,
        string Name,
        string Description,
        IReadOnlyList<string> Commands,
        string YoloArguments,
        IReadOnlyList<string> ExtraDirectories);

    /// <summary>A definition that is installed on this machine, bound to the file that will be started.</summary>
    internal sealed record CliAgentTool(CliAgentDefinition Definition, string FileName)
    {
        public string Id => Definition.Id;

        public string Name => Definition.Name;

        public string Description => Definition.Description;

        /// <summary>The folder holding the launcher, added to PATH so the agent finds its own helpers.</summary>
        public string Directory => Path.GetDirectoryName(FileName) ?? string.Empty;

        /// <summary>True when the agent is started with flags that skip its approval prompts.</summary>
        public bool SkipsApprovals => Definition.YoloArguments.Length > 0;
    }

    /// <summary>Detects installed command line coding agents and builds their console start command.</summary>
    internal static class CliAgentCatalog
    {
        // Windows launchers, most specific first: a native binary beats an npm shim in the same folder.
        private static readonly string[] LauncherExtensions = [".exe", ".cmd", ".bat", ".ps1"];

        internal static IReadOnlyList<CliAgentDefinition> Definitions { get; } =
        [
            new CliAgentDefinition(
                "claude",
                "Claude Code",
                "Anthropic's coding agent in the terminal.",
                ["claude"],
                "--dangerously-skip-permissions",
                [UserDirectory(".claude", "local"), UserDirectory(".claude", "bin")]),
            new CliAgentDefinition(
                "codex",
                "Codex CLI",
                "OpenAI's coding agent in the terminal.",
                ["codex"],
                "--dangerously-bypass-approvals-and-sandbox",
                [UserDirectory(".codex", "bin")]),
            new CliAgentDefinition(
                "gemini",
                "Gemini CLI",
                "Google's coding agent in the terminal.",
                ["gemini"],
                "--yolo",
                [UserDirectory(".gemini", "bin")]),
            new CliAgentDefinition(
                "copilot",
                "GitHub Copilot CLI",
                "GitHub's coding agent in the terminal.",
                ["copilot", "github-copilot"],
                "--allow-all-tools --allow-all-paths",
                [UserDirectory(".copilot", "bin")]),
            new CliAgentDefinition(
                "opencode",
                "opencode",
                "Open source coding agent in the terminal.",
                ["opencode"],
                string.Empty,
                [
                    UserDirectory(".opencode", "bin"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs",
                        "opencode")
                ])
        ];

        /// <summary>Returns every supported agent that is installed, in catalog order.</summary>
        public static IReadOnlyList<CliAgentTool> Detect()
        {
            var tools = new List<CliAgentTool>();
            foreach (var definition in Definitions)
            {
                var fileName = FindLauncher(definition);
                if (fileName is not null) tools.Add(new CliAgentTool(definition, fileName));
            }
            return tools;
        }

        /// <summary>Builds the console command that starts the agent with its PATH, environment and flags.
        /// The shell stays open after the agent exits so the session output can still be read.
        /// Extra arguments and environment come from whatever is wired into the session, such as the
        /// Cody MCP server.</summary>
        public static string CreateCommandLine(
            CliAgentTool tool,
            string workspace,
            string extraArguments = "",
            IReadOnlyDictionary<string, string>? extraEnvironment = null)
        {
            var steps = new List<string> { "chcp 65001>nul" };

            var pathPrefix = string.Join(
                ";",
                new[] { tool.Directory, NodeModulesBin(workspace), NodeDirectory() }
                    .Where(directory => directory.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            if (pathPrefix.Length > 0) steps.Add($"set \"PATH={pathPrefix};%PATH%\"");

            // A ConPTY host reports as a real terminal, so tell the agent it may use colour and full-screen UI.
            steps.Add("set \"TERM=xterm-256color\"");
            steps.Add("set \"COLORTERM=truecolor\"");
            steps.Add("set \"FORCE_COLOR=1\"");
            steps.Add("set \"NO_UPDATE_NOTIFIER=1\"");
            if (workspace.Length > 0) steps.Add($"set \"CRSTER_WORKSPACE={workspace}\"");
            foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
                steps.Add($"set \"{key}={value}\"");
            steps.Add(CreateInvocation(tool, extraArguments));

            var script = string.Join(" && ", steps);
            var shell = ExecutableLocator.Find("cmd.exe") ?? "cmd.exe";
            // /s keeps the script verbatim after the outer quotes are stripped; /k leaves the shell open.
            return $"\"{shell}\" /d /s /k \"{script}\"";
        }

        private static string CreateInvocation(CliAgentTool tool, string extraArguments)
        {
            var arguments = string.Join(
                " ",
                new[] { tool.Definition.YoloArguments, extraArguments }.Where(part => part.Length > 0));
            var suffix = arguments.Length > 0 ? $" {arguments}" : string.Empty;

            if (!string.Equals(Path.GetExtension(tool.FileName), ".ps1", StringComparison.OrdinalIgnoreCase))
                return $"\"{tool.FileName}\"{suffix}";

            var powerShell = ExecutableLocator.Find("pwsh.exe")
                ?? ExecutableLocator.FindWindowsPowerShell()
                ?? "powershell.exe";
            return $"\"{powerShell}\" -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{tool.FileName}\"{suffix}";
        }

        private static string? FindLauncher(CliAgentDefinition definition)
        {
            foreach (var directory in SearchDirectories(definition))
            {
                foreach (var command in definition.Commands)
                {
                    foreach (var extension in LauncherExtensions)
                    {
                        var candidate = ExecutableLocator.FindIn([directory], command + extension);
                        if (candidate is not null) return candidate;
                    }
                }
            }
            return null;
        }

        private static IEnumerable<string> SearchDirectories(CliAgentDefinition definition) =>
            ExecutableLocator.PathDirectories()
                .Concat(PackageManagerDirectories())
                .Concat(definition.ExtraDirectories)
                .Where(directory => directory.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Folders where npm, pnpm, yarn, bun and deno place global command shims.</summary>
        private static IEnumerable<string> PackageManagerDirectories()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return
            [
                Path.Combine(roaming, "npm"),
                Path.Combine(local, "npm"),
                Path.Combine(local, "pnpm"),
                Path.Combine(local, "Yarn", "bin"),
                UserDirectory(".local", "bin"),
                UserDirectory(".bun", "bin"),
                UserDirectory(".deno", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs")
            ];
        }

        private static string NodeDirectory() =>
            Path.GetDirectoryName(ExecutableLocator.Find("node.exe") ?? string.Empty) ?? string.Empty;

        private static string NodeModulesBin(string workspace)
        {
            if (string.IsNullOrWhiteSpace(workspace)) return string.Empty;
            var binDirectory = Path.Combine(workspace, "node_modules", ".bin");
            return System.IO.Directory.Exists(binDirectory) ? binDirectory : string.Empty;
        }

        private static string UserDirectory(params string[] parts) =>
            Path.Combine(
                new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
                    .Concat(parts)
                    .ToArray());
    }
}
