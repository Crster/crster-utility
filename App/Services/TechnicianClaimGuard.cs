using System;
using System.Text.RegularExpressions;

namespace App.Services
{
    /// <summary>
    /// Catches an answer that reports finished work when no tool changed anything in that turn.
    /// Small models do this often, and the user cannot tell the difference from a real fix.
    /// </summary>
    internal static class TechnicianClaimGuard
    {
        public const string CorrectionPrompt =
            "Stop. You reported a completed change, but no tool changed anything in this turn, so nothing was written, repaired, or configured. Do one of two things now. Either make the change with the correct tool (write_file for file text, run_command or run_elevated_command for system changes) and verify the result, or rewrite your answer to say plainly that the change was not applied and give the user the exact step to take. Never describe planned work as done.";

        public const string UnprovedChangeWarning =
            "Technician reported a completed change, but no tool changed anything in this turn. Treat the change as not applied and check it yourself.";

        private static readonly Regex CompletedChangePattern = new(
            @"\b(?:i|i've|i have|we|we've)\s+(?:just\s+|now\s+|already\s+)?(?:wrote|written|created|added|updated|edited|modified|changed|saved|fixed|repaired|applied|patched|installed|uninstalled|removed|deleted|disabled|enabled|reset|restored|configured|set)\b"
            + @"|\b(?:has|have|had)\s+been\s+(?:written|created|added|updated|edited|modified|changed|saved|fixed|repaired|applied|patched|installed|removed|deleted|disabled|enabled|reset|restored|configured|set)\b"
            + @"|\b(?:file|setting|service|driver|registry|value|key|config|configuration)\s+(?:is|was|are|were)\s+now\s+(?:written|created|updated|changed|saved|fixed|applied|set)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

        /// <summary>True when the answer states the work is done rather than proposing it.</summary>
        public static bool ClaimsCompletedChange(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return false;
            try { return CompletedChangePattern.IsMatch(answer); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
