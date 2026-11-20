namespace Classroom.Contracts;

public record SqlReviewAttempt(string Id,string StudentId,string StudentName,string TaskId,string? LessonId,string? WorkScope,long At,
    string Action,string Kind,string? Code,string Title,string? Prompt,int? Variant,int? Revision,string StateAvailability,ExecutionDto? OriginalResult);
public record SqlReviewRun(string Code);
