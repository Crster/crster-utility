using System;

namespace App.Models
{
    internal enum TechnicianModelTier { Standard, HighThinking, Escalated }
    internal enum TechnicianRequestMode { Default, Plan, Think, Smart, Research, Design, Clear }
    internal enum TechnicianCheckpoint { None, CompactAndRaiseThinking, CompactAndUpgrade, Stop }
    internal enum TechnicianContextRegion { Workspace, Session, Specialist }

    internal sealed record TechnicianCompactionInput(
        string OriginalRequest,
        string ExistingContext,
        string Transcript,
        string? CourseCorrection = null);
}
