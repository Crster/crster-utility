using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace App.Services.Mcp
{
    /// <summary>Extra launch arguments and environment that point one CLI agent at the Cody MCP server,
    /// plus the config files that had to be touched so the user can be told about them.</summary>
    internal sealed record CliAgentMcpWiring(
        string Arguments,
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyList<string> ConfiguredFiles,
        string Warning = "");

    /// <summary>
    /// Points a hosted CLI agent at the Cody MCP server. Each CLI is wired the way it documents:
    /// on the command line where it supports one, otherwise by merging a "cody" entry into its own
    /// config file. Merged entries are removed again when the session ends, so no stale port or
    /// token is left behind.
    /// </summary>
    internal static class CliAgentMcpConfigurator
    {
        private const string ServerName = "cody";

        /// <summary>Marker around the block written into Codex's TOML config, which has no merge API.</summary>
        private const string TomlBlockStart = "# >>> crster utility cody mcp";
        private const string TomlBlockEnd = "# <<< crster utility cody mcp";

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public static CliAgentMcpWiring Apply(CliAgentTool tool, string workspace, CodyMcpEndpoint endpoint)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CODY_MCP_URL"] = endpoint.Url,
                ["CODY_MCP_TOKEN"] = endpoint.Token
            };

            return tool.Id switch
            {
                // Claude Code takes a config file path or a JSON string; a path avoids quoting JSON
                // through the shell. The file lives in our own folder, not in a Claude config.
                "claude" => new CliAgentMcpWiring(
                    $"--mcp-config \"{WriteOwnConfigFile(workspace, ClientConfig(endpoint, "http"))}\"",
                    environment,
                    []),

                "codex" => new CliAgentMcpWiring(
                    string.Empty,
                    environment,
                    [WriteCodexConfig(endpoint)]),

                "gemini" => new CliAgentMcpWiring(
                    string.Empty,
                    environment,
                    [MergeJsonConfig(
                        GeminiSettingsPath(workspace),
                        "mcpServers",
                        new JsonObject
                        {
                            ["httpUrl"] = endpoint.Url,
                            ["headers"] = AuthorizationHeader(endpoint)
                        })]),

                "copilot" => new CliAgentMcpWiring(
                    string.Empty,
                    environment,
                    [MergeJsonConfig(
                        CopilotConfigPath(workspace),
                        "mcpServers",
                        new JsonObject
                        {
                            ["type"] = "http",
                            ["url"] = endpoint.Url,
                            ["headers"] = AuthorizationHeader(endpoint),
                            ["tools"] = new JsonArray("*")
                        })]),

                "opencode" => new CliAgentMcpWiring(
                    string.Empty,
                    environment,
                    [MergeJsonConfig(
                        OpencodeConfigPath(workspace),
                        "mcp",
                        new JsonObject
                        {
                            ["type"] = "remote",
                            ["url"] = endpoint.Url,
                            ["enabled"] = true,
                            ["headers"] = AuthorizationHeader(endpoint)
                        })]),

                _ => new CliAgentMcpWiring(
                    string.Empty,
                    environment,
                    [],
                    $"{tool.Name} has no known way to add an MCP server, so the Cody tools were not offered to it.")
            };
        }

        /// <summary>Takes the Cody entry back out, so a closed session leaves no dead port behind.</summary>
        public static void Remove(CliAgentTool tool, string workspace)
        {
            switch (tool.Id)
            {
                case "claude":
                    DeleteOwnConfigFile(workspace);
                    break;
                case "codex":
                    RemoveCodexConfig();
                    break;
                case "gemini":
                    RemoveJsonConfig(GeminiSettingsPath(workspace), "mcpServers");
                    break;
                case "copilot":
                    RemoveJsonConfig(CopilotConfigPath(workspace), "mcpServers");
                    break;
                case "opencode":
                    RemoveJsonConfig(OpencodeConfigPath(workspace), "mcp");
                    break;
            }
        }

        private static JsonObject AuthorizationHeader(CodyMcpEndpoint endpoint) => new()
        {
            ["Authorization"] = $"Bearer {endpoint.Token}"
        };

        private static JsonObject ClientConfig(CodyMcpEndpoint endpoint, string type) => new()
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = type,
                    ["url"] = endpoint.Url,
                    ["headers"] = AuthorizationHeader(endpoint)
                }
            }
        };

        private static string OwnConfigPath(string workspace) =>
            Path.Combine(workspace, ".crster", "cody-mcp.json");

        private static string GeminiSettingsPath(string workspace) =>
            Path.Combine(workspace, ".gemini", "settings.json");

        // Copilot reads .mcp.json from the working directory upwards, and it wins over the user config.
        private static string CopilotConfigPath(string workspace) =>
            Path.Combine(workspace, ".mcp.json");

        private static string OpencodeConfigPath(string workspace) =>
            Path.Combine(workspace, "opencode.json");

        private static string CodexConfigPath() =>
            Path.Combine(
                Environment.GetEnvironmentVariable("CODEX_HOME")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".codex"),
                "config.toml");

        private static string WriteOwnConfigFile(string workspace, JsonObject config)
        {
            var path = OwnConfigPath(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteText(path, config.ToJsonString(WriteOptions));
            return path;
        }

        private static void DeleteOwnConfigFile(string workspace)
        {
            try
            {
                File.Delete(OwnConfigPath(workspace));
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
            }
        }

        /// <summary>Sets one server entry inside an existing config file, leaving the rest of it alone.</summary>
        private static string MergeJsonConfig(string path, string sectionName, JsonObject entry)
        {
            var document = ReadJsonObject(path);
            if (document[sectionName] is not JsonObject section)
            {
                section = [];
                document[sectionName] = section;
            }

            section[ServerName] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteText(path, document.ToJsonString(WriteOptions));
            return path;
        }

        private static void RemoveJsonConfig(string path, string sectionName)
        {
            if (!File.Exists(path)) return;

            var document = ReadJsonObject(path);
            if (document[sectionName] is not JsonObject section || !section.Remove(ServerName)) return;

            // Drop the section too when we were the only thing in it, so the file goes back as it was.
            if (section.Count == 0) document.Remove(sectionName);
            if (document.Count == 0)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                }
                return;
            }

            WriteText(path, document.ToJsonString(WriteOptions));
        }

        private static JsonObject ReadJsonObject(string path)
        {
            if (!File.Exists(path)) return [];
            try
            {
                return JsonNode.Parse(
                    File.ReadAllText(path),
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    }) as JsonObject ?? [];
            }
            catch (Exception exception) when (IsExpectedFileException(exception) || exception is JsonException)
            {
                return [];
            }
        }

        private static string WriteCodexConfig(CodyMcpEndpoint endpoint)
        {
            var path = CodexConfigPath();
            var block = new StringBuilder()
                .AppendLine(TomlBlockStart)
                .AppendLine($"[mcp_servers.{ServerName}]")
                .AppendLine($"url = \"{endpoint.Url}\"")
                .AppendLine("bearer_token_env_var = \"CODY_MCP_TOKEN\"")
                .Append(TomlBlockEnd)
                .ToString();

            var existing = ReadCodexConfigWithoutBlock(path);
            var separator = existing.Length > 0 ? Environment.NewLine + Environment.NewLine : string.Empty;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteText(path, existing + separator + block + Environment.NewLine);
            return path;
        }

        private static void RemoveCodexConfig()
        {
            var path = CodexConfigPath();
            if (!File.Exists(path)) return;

            var existing = ReadCodexConfigWithoutBlock(path);
            if (existing.Length == 0)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                }
                return;
            }

            WriteText(path, existing + Environment.NewLine);
        }

        /// <summary>Reads Codex's config with our own marked block cut out, so nothing else is disturbed.</summary>
        private static string ReadCodexConfigWithoutBlock(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            try
            {
                var text = File.ReadAllText(path);
                var pattern = $"{Regex.Escape(TomlBlockStart)}.*?{Regex.Escape(TomlBlockEnd)}";
                return Regex.Replace(text, pattern, string.Empty, RegexOptions.Singleline).TrimEnd();
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                return string.Empty;
            }
        }

        private static void WriteText(string path, string text) =>
            File.WriteAllText(path, text.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));

        private static bool IsExpectedFileException(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or NotSupportedException;
    }
}
