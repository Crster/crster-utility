using System;

namespace App.Models
{
    internal enum TechnicianModelTier { Standard, HighThinking, Escalated }
    internal enum TechnicianCheckpoint { None, CompactAndRaiseThinking, CompactAndUpgrade, Stop }
    internal enum TechnicianContextRegion { Workspace, Session, Specialist }

    internal sealed record TechnicianTurnClassification(
        bool Related,
        string NewContext,
        bool RequestPlan,
        bool RequestRetry,
        bool RequestResearch,
        bool RequiresExecution)
    {
        public static TechnicianTurnClassification SafeContinuation { get; } = new(
            true,
            string.Empty,
            false,
            false,
            false,
            false);
    }

    internal sealed record TechnicianCompactionInput(
        string OriginalRequest,
        string ExistingContext,
        string Transcript,
        string? CourseCorrection = null);
}
