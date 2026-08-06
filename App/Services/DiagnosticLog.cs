using System;
using System.IO;
using System.Text;

namespace App.Services
{
    /// <summary>
    /// Appends timestamped lines to a file next to the app settings, for tracking down a fault that
    /// only shows up in a running window. Temporary: remove once the CLI restart crash is fixed.
    /// </summary>
    internal static class DiagnosticLog
    {
        private const string LogDirectoryName = "crster\\utility\\logs";
        private static readonly object Gate = new();
        private static bool _headerWritten;

        public static string Path { get; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LogDirectoryName,
            "cli-agent-diagnostics.log");

        public static void Write(string source, string message)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    var builder = new StringBuilder();
                    if (!_headerWritten)
                    {
                        _headerWritten = true;
                        builder.AppendLine(
                            $"===== session started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====");
                    }

                    builder.AppendLine($"{DateTime.Now:HH:mm:ss.fff}  {source,-18}  {message}");
                    File.AppendAllText(Path, builder.ToString().ReplaceLineEndings("\r\n"), Encoding.UTF8);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    // Diagnostics must never be the thing that breaks the app.
                }
            }
        }

        public static void WriteException(string source, Exception? exception)
        {
            if (exception is null)
            {
                Write(source, "exception was null");
                return;
            }

            Write(source, $"!! {exception.GetType().FullName}: {exception.Message}");
            Write(source, $"   HResult=0x{exception.HResult:X8}");
            foreach (var line in (exception.StackTrace ?? "(no stack)").Split('\n'))
                Write(source, $"   {line.TrimEnd()}");

            if (exception.InnerException is { } inner) WriteException($"{source}/inner", inner);
        }
    }
}
