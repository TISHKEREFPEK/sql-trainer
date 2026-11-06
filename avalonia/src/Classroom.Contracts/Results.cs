namespace Classroom.Contracts;

public record ResultsPreference(string Mode = "lesson");
public record ResultsSummary(int Students, int Joined, int Started, int Completed, int Total, int Pending, int Reviewed, double? AveragePercent);
public record ResultsGroup(string Key, string Name, ResultsSummary Summary);
public record ResultsStudent(string Id, string Name, string GroupKey, string GroupName, bool Joined, ResultsSummary Summary, long? LastActivityAt);
public record ResultsWork(string StudentId, string Subject, string TaskId, string? AssignmentId, string? LessonId, string Title, string State,
    bool Required, bool Started, bool Completed, bool Reused, long? StartedAt, long? CompletedAt, long? LastActivityAt,
    int Checks, int Errors, int? Hints, int? Violations, int? ActiveSeconds, int? Grade, string? ReportId, long? ReviewedAt, int? FirstCheckFailed = null);
public record ResultsDifficulty(string Subject, string TaskId, string Title, int Assigned, int Started, int Checked, int Unsolved, double? UnsolvedPercent, int FirstChecksKnown, int FirstChecksFailed, bool SmallSample);
public record ResultsOverview(string Mode, string? LessonId, string Title, string Note, long GeneratedAt, ResultsSummary Summary,
    ResultsGroup[] Groups, ResultsStudent[] Students, ResultsWork[] Works, ResultsDifficulty[] Difficulties);
public record ResultsSqlDetail(string Title, string Prompt, string Draft, string? AcceptedCode, int? Variant, int? Revision,
    string? SourceLessonId, string WorkScope, bool Opened, LearningEvent[] Events, bool HasMore, int NextOffset);
public record ResultsCsv(string Csv, long GeneratedAt);
