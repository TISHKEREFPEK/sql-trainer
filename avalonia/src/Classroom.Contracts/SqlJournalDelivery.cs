namespace Classroom.Contracts;

public record SqlJournalBatch(string StreamId, string LessonId, string WorkScope, string TaskId, long StartedAt, string BaseCode,
    long KnownSequence, SqlTextChange[] Changes, string? ForkedFrom = null);
public record SqlJournalAck(string StreamId, string Status, long Sequence, string StateHash);
public record SqlJournalStreamInfo(string Id, string LessonId, string WorkScope, string TaskId, string Title, long StartedAt, long ReceivedAt, long Sequence, string? ForkedFrom);
public record SqlJournalEvent(SqlTextChange Change, long ReceivedAt, string Actor);
public record SqlJournalEvents(SqlJournalEvent[] Events, bool HasMore, long NextSequence);
public record SqlJournalText(string StreamId, long Sequence, string Code);
public record SqlJournalExecutionLink(string Availability, string? AttemptId = null);
