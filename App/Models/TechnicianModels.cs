using System;

namespace App.Models
{
    internal enum TechnicianScope { Project, Coding, Troubleshooting, OutOfScope }
    internal enum TechnicianRelationship { None, Related, New }
    internal enum TechnicianWorkType { Advice, Implementation, Diagnosis }
    internal enum TechnicianSpecialist { None, Research }
    internal enum TechnicianModelTier { Standard, Escalated }
    internal enum TechnicianCheckpoint { None, CompactAndUpgrade, CourseCorrect, Stop }
    internal enum TechnicianContextRegion { Workspace, Session, Specialist }

    internal sealed record TechnicianTurnClassification(
        TechnicianScope Scope,
        TechnicianRelationship Relationship,
        TechnicianWorkType WorkType,
        TechnicianSpecialist Specialist,
        string Reason)
    {
        public static TechnicianTurnClassification SafeContinuation { get; } = new(
            TechnicianScope.Coding,
            TechnicianRelationship.Related,
            TechnicianWorkType.Implementation,
            TechnicianSpecialist.None,
            "Classification was unavailable; existing context was preserved.");
    }

    internal sealed record TechnicianCompactionInput(
        string OriginalRequest,
        string ExistingContext,
        string Transcript,
        string? CourseCorrection = null);
}
