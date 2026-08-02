using App.Models;
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SmartToolService
    {
        private readonly SecretaryToolService _secretaryTools;

        public SmartToolService(SecretaryToolService secretaryTools)
        {
            _secretaryTools = secretaryTools;
        }

        public static JsonArray CreateDeclarations() => SecretaryToolService.CreateReadOnlyDeclarations();

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "search_memory" or "get_local_context" => await _secretaryTools.ExecuteAsync(name, arguments, token),
                    _ => Error("Smart cannot use that tool.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error(exception.Message); }
            catch (Exception) { return Error("Smart could not complete that operation."); }
        }

        private static ToolResult Error(string summary) => new(false, new JsonObject { ["status"] = "failed", ["summary"] = summary }.ToJsonString());
    }
}
