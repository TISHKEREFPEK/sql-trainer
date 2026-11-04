namespace Classroom.Storage;

// Append-only decisions, including permission to resubmit. A correction adds a row.
public sealed class ElectronicsReviewEntity
{
    public string Id { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string ReportId { get; set; } = "";
    public string TeacherId { get; set; } = "";
    public int Sequence { get; set; }
    public string? PreviousReviewId { get; set; }
    public string Action { get; set; } = "";
    public string Comment { get; set; } = "";
    public int? Grade { get; set; }
    public string RequestHash { get; set; } = "";
    public long At { get; set; }
}
