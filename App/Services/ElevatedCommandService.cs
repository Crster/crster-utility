using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal static class ElevatedCommandService
    {
        internal const string HelperArgument = "--technician-elevated-command";

        internal static async Task<ElevatedCommandResult> RunAsync(
            string command,
            string workingDirectory,
            CancellationToken token)
        {
            var requestPath = Path.Combine(Path.GetTempPath(), $"crster-command-{Guid.NewGuid():N}.request.json");
            var resultPath = Path.Combine(Path.GetTempPath(), $"crster-command-{Guid.NewGuid():N}.result.json");
            try
            {
                await File.WriteAllTextAsync(
                    requestPath,
                    JsonSerializer.Serialize(new ElevatedCommandRequest(command, workingDirectory)),
                    token);
                var appExecutable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The application executable path is unavailable.");
                var start = new ProcessStartInfo(appExecutable)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                start.ArgumentList.Add(HelperArgument);
                start.ArgumentList.Add(requestPath);
                start.ArgumentList.Add(resultPath);
                using var helper = Process.Start(start)
                    ?? throw new InvalidOperationException("The elevated command helper could not be started.");
                await helper.WaitForExitAsync(token);
                if (!File.Exists(resultPath))
                    throw new InvalidOperationException("The elevated command did not return a result.");
                return JsonSerializer.Deserialize<ElevatedCommandResult>(
                           await File.ReadAllTextAsync(resultPath, token))
                       ?? throw new InvalidOperationException("The elevated command returned an invalid result.");
            }
            finally
            {
                TryDelete(requestPath);
                TryDelete(resultPath);
            }
        }

        internal static int RunHelper(string requestPath, string resultPath)
        {
            try
            {
                var request = JsonSerializer.Deserialize<ElevatedCommandRequest>(File.ReadAllText(requestPath))
                    ?? throw new InvalidOperationException("The elevated command request is invalid.");
                using var process = new Process
                {
                    StartInfo = CodyToolService.CreateCommandStartInfo(
                        request.Command,
                        request.WorkingDirectory)
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                Task.WaitAll(stdout, stderr);
                File.WriteAllText(resultPath, JsonSerializer.Serialize(
                    new ElevatedCommandResult(stdout.Result, stderr.Result, process.ExitCode)));
                return 0;
            }
            catch (Exception exception)
            {
                File.WriteAllText(resultPath, JsonSerializer.Serialize(
                    new ElevatedCommandResult(string.Empty, exception.Message, -1)));
                return 1;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed record ElevatedCommandRequest(string Command, string WorkingDirectory);
    }

    internal sealed record ElevatedCommandResult(string Stdout, string Stderr, int ExitCode);
}
