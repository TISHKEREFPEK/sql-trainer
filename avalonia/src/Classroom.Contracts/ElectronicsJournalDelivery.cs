namespace Classroom.Contracts;

public record ElectronicsJournalBatch(string StreamId, string ClientId, string LessonId, string? AssignmentId, long StartedAt, string BaseJson,
    long KnownSequence, ElectronicsChange[] Changes, string? ForkedFrom = null);
public record ElectronicsJournalAck(string StreamId, string Status, long Sequence, string StateHash);
public record ElectronicsJournalInfo(string Id, string ClientId, string LessonId, string? AssignmentId, string Title, long StartedAt, long ReceivedAt, long Sequence, string? ForkedFrom);
public record ElectronicsJournalEvent(ElectronicsChange Change, long ReceivedAt, string Actor);
public record ElectronicsJournalEvents(ElectronicsJournalEvent[] Events, bool HasMore, long NextSequence);
public record ElectronicsJournalState(string StreamId, long Sequence, string ProjectJson);
public record ElectronicsJournalReportLink(string Availability, string? ReportId = null, string? Kind = null);
