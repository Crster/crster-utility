using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class AgentCliNotFoundException(string providerName, Exception innerException)
        : InvalidOperationException($"{providerName} is not installed or is not available on PATH.", innerException);

    internal enum AgentCliProviderKind { Codex, Claude }
    internal enum AgentCliEventKind { Thinking, Tool, Output }

    internal sealed record AgentCliRequest(
        string WorkspacePath,
        string Prompt,
        string Instruction,
        string? SessionId = null,
        bool AllowEdits = true,
        string? Model = null,
        string? ReasoningEffort = null);

    internal sealed record AgentCliEvent(AgentCliEventKind Kind, string Title, string Content, bool? Succeeded = null);
    internal sealed record AgentCliResult(string Text, string? SessionId);

    internal interface IAgentCliProvider
    {
        string DisplayName { get; }
        Task<AgentCliResult> RunAsync(
            AgentCliRequest request,
            Func<AgentCliEvent, Task> reportEventAsync,
            CancellationToken cancellationToken);
    }

    internal static class AgentCliProviderFactory
    {
        public static IAgentCliProvider Create(string provider) =>
            Enum.TryParse<AgentCliProviderKind>(provider, true, out var kind) && kind == AgentCliProviderKind.Claude
                ? new ClaudeCliProvider()
                : new CodexCliProvider();
    }

    internal abstract class AgentCliProvider : IAgentCliProvider
    {
        public abstract string DisplayName { get; }
        protected abstract string ExecutableName { get; }
        protected abstract void ConfigureArguments(ProcessStartInfo startInfo, AgentCliRequest request);
        protected abstract Task ParseLineAsync(
            string line,
            AgentCliState state,
            Func<AgentCliEvent, Task> reportEventAsync);

        public async Task<AgentCliResult> RunAsync(
            AgentCliRequest request,
            Func<AgentCliEvent, Task> reportEventAsync,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath) || !Directory.Exists(request.WorkspacePath))
                throw new DirectoryNotFoundException("The selected Cody workspace is unavailable.");

            var startInfo = new ProcessStartInfo(ExecutableName)
            {
                WorkingDirectory = request.WorkspacePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ConfigureArguments(startInfo, request);

            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"{DisplayName} could not be started.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new AgentCliNotFoundException(DisplayName, exception);
            }

            using (process)
            using (cancellationToken.Register(() => TryTerminate(process)))
            {
                var input = $"{request.Instruction}\n\nUser request:\n{request.Prompt}";
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();

                var state = new AgentCliState();
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                    await ParseLineAsync(line, state, reportEventAsync);

                await process.WaitForExitAsync(cancellationToken);
                var stderr = (await stderrTask).Trim();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                        ? $"{DisplayName} exited with code {process.ExitCode}."
                        : stderr);
                if (string.IsNullOrWhiteSpace(state.FinalText))
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                        ? $"{DisplayName} completed without a response."
                        : stderr);
                return new AgentCliResult(state.FinalText.Trim(), state.SessionId);
            }
        }

        protected static string? ReadString(JsonNode? node, params string[] path)
        {
            foreach (var segment in path)
                node = node?[segment];
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        protected static string ReadContent(JsonNode? content)
        {
            if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
            if (content is not JsonArray items) return string.Empty;
            var result = new StringBuilder();
            foreach (var item in items)
            {
                var itemText = ReadString(item, "text") ?? ReadString(item, "content");
                if (!string.IsNullOrWhiteSpace(itemText)) result.AppendLine(itemText);
            }
            return result.ToString().Trim();
        }

        private static void TryTerminate(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        protected sealed class AgentCliState
        {
            public string FinalText { get; set; } = string.Empty;
            public string? SessionId { get; set; }
        }
    }

    internal sealed class CodexCliProvider : AgentCliProvider
    {
        public override string DisplayName => "Codex";
        protected override string ExecutableName => ResolveExecutablePath();

        private static string ResolveExecutablePath()
        {
            var runtimeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI",
                "Codex",
                "bin");
            if (!Directory.Exists(runtimeDirectory)) return "codex";

            return Directory.EnumerateDirectories(runtimeDirectory)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? "codex";
        }

        protected override void ConfigureArguments(ProcessStartInfo startInfo, AgentCliRequest request)
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--json");
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                startInfo.ArgumentList.Add("--model");
                startInfo.ArgumentList.Add(request.Model);
            }
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add($"model_reasoning_effort=\"{request.ReasoningEffort}\"");
            }
            startInfo.ArgumentList.Add("--sandbox");
            startInfo.ArgumentList.Add(request.AllowEdits ? "workspace-write" : "read-only");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                startInfo.ArgumentList.Add("resume");
                startInfo.ArgumentList.Add(request.SessionId);
            }
            startInfo.ArgumentList.Add("-");
        }

        protected override async Task ParseLineAsync(
            string line,
            AgentCliState state,
            Func<AgentCliEvent, Task> reportEventAsync)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(line); }
            catch (JsonException) { return; }
            var type = ReadString(root, "type") ?? string.Empty;
            if (type == "thread.started") state.SessionId = ReadString(root, "thread_id");
            if (type != "item.completed") return;

            var item = root?["item"];
            var itemType = ReadString(item, "type") ?? string.Empty;
            var text = ReadString(item, "text") ?? ReadString(item, "output") ?? ReadContent(item?["content"]);
            if (itemType is "agent_message" or "message")
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    state.FinalText = text;
                    await reportEventAsync(new AgentCliEvent(AgentCliEventKind.Output, "Codex", text));
                }
                return;
            }
            if (itemType is "reasoning")
            {
                if (!string.IsNullOrWhiteSpace(text))
                    await reportEventAsync(new AgentCliEvent(AgentCliEventKind.Thinking, "Thinking", text));
                return;
            }
            if (itemType is "command_execution" or "file_change" or "mcp_tool_call")
            {
                var title = ReadString(item, "command") ?? ReadString(item, "name") ?? itemType.Replace('_', ' ');
                await reportEventAsync(new AgentCliEvent(
                    AgentCliEventKind.Tool,
                    title,
                    string.IsNullOrWhiteSpace(text) ? "Completed." : text,
                    !string.Equals(ReadString(item, "status"), "failed", StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    internal sealed class ClaudeCliProvider : AgentCliProvider
    {
        public override string DisplayName => "Claude Code";
        protected override string ExecutableName => "claude";

        protected override void ConfigureArguments(ProcessStartInfo startInfo, AgentCliRequest request)
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("--output-format");
            startInfo.ArgumentList.Add("stream-json");
            startInfo.ArgumentList.Add("--verbose");
            startInfo.ArgumentList.Add("--include-partial-messages");
            startInfo.ArgumentList.Add("--permission-mode");
            startInfo.ArgumentList.Add(request.AllowEdits ? "acceptEdits" : "plan");
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                startInfo.ArgumentList.Add("--model");
                startInfo.ArgumentList.Add(request.Model);
            }
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                startInfo.ArgumentList.Add("--resume");
                startInfo.ArgumentList.Add(request.SessionId);
            }
        }

        protected override async Task ParseLineAsync(
            string line,
            AgentCliState state,
            Func<AgentCliEvent, Task> reportEventAsync)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(line); }
            catch (JsonException) { return; }
            var type = ReadString(root, "type") ?? string.Empty;
            state.SessionId ??= ReadString(root, "session_id");
            if (type == "stream_event")
            {
                var streamEvent = root?["event"];
                if (ReadString(streamEvent, "type") == "content_block_delta")
                {
                    var delta = streamEvent?["delta"];
                    var textDelta = ReadString(delta, "text");
                    var thinkingDelta = ReadString(delta, "thinking");
                    if (!string.IsNullOrEmpty(textDelta))
                    {
                        state.FinalText += textDelta;
                        await reportEventAsync(new AgentCliEvent(AgentCliEventKind.Output, "Claude Code", textDelta));
                    }
                    else if (!string.IsNullOrEmpty(thinkingDelta))
                        await reportEventAsync(new AgentCliEvent(AgentCliEventKind.Output, "Claude Code", thinkingDelta));
                }
                return;
            }
            if (type == "result")
            {
                state.FinalText = ReadString(root, "result") ?? state.FinalText;
                return;
            }
            if (type != "assistant" || root?["message"]?["content"] is not JsonArray content) return;
            foreach (var item in content)
            {
                var itemType = ReadString(item, "type") ?? string.Empty;
                if (itemType == "text")
                {
                    var text = ReadString(item, "text");
                    if (!string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(state.FinalText))
                    {
                        state.FinalText = text;
                        await reportEventAsync(new AgentCliEvent(AgentCliEventKind.Output, "Claude Code", text));
                    }
                }
                else if (itemType == "thinking")
                {
                    var thinking = ReadString(item, "thinking");
                    if (!string.IsNullOrWhiteSpace(thinking))
                        await reportEventAsync(new AgentCliEvent(AgentCliEventKind.Thinking, "Thinking", thinking));
                }
                else if (itemType == "tool_use")
                {
                    await reportEventAsync(new AgentCliEvent(
                        AgentCliEventKind.Tool,
                        ReadString(item, "name") ?? "Tool",
                        item?["input"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "Started."));
                }
            }
        }
    }
}
