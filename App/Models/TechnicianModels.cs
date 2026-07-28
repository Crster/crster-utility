using System;

namespace App.Models
{
    internal enum TechnicianModelTier { Standard, Escalated }
    internal enum TechnicianCheckpoint { None, CompactAndUpgrade, CourseCorrect, Stop }
    internal enum TechnicianContextRegion { Workspace, Session, Specialist }

    internal sealed record TechnicianTurnClassification(
        bool Related,
        string NewContext,
        bool RequestPlan,
        bool RequestRetry,
        bool RequestResearch)
    {
        public static TechnicianTurnClassification SafeContinuation { get; } = new(
            true,
            string.Empty,
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
