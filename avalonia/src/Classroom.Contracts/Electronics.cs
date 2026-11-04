namespace Classroom.Contracts;

public record ElectronicsReportRequest(string Id,string Kind,string ProjectJson,string Detail,int? ExpectedAssignmentVersion=null);
public record ElectronicsSyncRequest(string ClientId,long Sequence,string ProjectJson,ElectronicsReportRequest[] Reports,string? AssignmentId=null);
public record ElectronicsSyncReceipt(long Sequence,long UpdatedAt,string[] ReportIds,string? AssignmentState=null,bool? CanSubmit=null,int? AssignmentVersion=null);
public record ElectronicsStudentResult(string StudentId,string Login,string? GroupId,int WorkCount,int Submitted,int Compiled,int Errors,long? LastAt,string? LastKind);
public record ElectronicsOverview(GroupDto[] Groups,ElectronicsStudentResult[] Students);
public record ElectronicsWorkInfo(string ClientId,string Name,int Parts,int Wires,long UpdatedAt);
public record ElectronicsReportInfo(string Id,string ClientId,string Name,string Kind,long At);
public record ElectronicsStudentDetail(string StudentId,string Login,string? GroupId,ElectronicsWorkInfo[] Works,ElectronicsReportInfo[] Reports,bool HasMore,int NextOffset);
public record ElectronicsSnapshot(string ProjectJson,string Kind,long At,string Detail);
public record ElectronicsTaskRequest(string Title,string Prompt,string? StarterJson=null,bool Restricted=false,int ExpectedRevision=0);
public record ElectronicsTaskDto(string Id,string Title,string Prompt,string? StarterJson,bool Restricted,int Revision,long UpdatedAt);
public record ElectronicsTaskSummary(string Id,string Title,bool Restricted,int Revision,long UpdatedAt);
public record ElectronicsLessonTask(string Id,string Title,string Prompt,bool Restricted,int Revision,long IssuedAt);
public record ElectronicsSelectionRequest(string[] TaskIds);
public record ElectronicsSelection(ElectronicsLessonTask[] Tasks);
public record ElectronicsAssignmentDto(string Id,string LessonId,string TaskId,string Title,string Prompt,int Revision,bool Restricted,string ProjectJson,string State,bool CanSubmit,bool Available=true,int StateVersion=1);
public record StudentElectronicsWork(string TaskId,string Title,string State,string? AssignmentId,int Revision);
public record ElectronicsAssignmentStatus(string Id,string State,bool CanSubmit,bool Available,int StateVersion,ElectronicsReviewDecision? LatestReview=null);

public record ElectronicsReviewRequest(string Action,string Comment,int? Grade,string? ExpectedReviewId,int? ExpectedAssignmentVersion);
public record ElectronicsReviewDecision(string Id,int Sequence,string Action,string Comment,int? Grade,long At);
public record ElectronicsReviewItem(string StudentId,string StudentName,string? GroupId,string GroupName,string ReportId,string? AssignmentId,string? LessonId,string Title,long SubmittedAt,string State,int? Grade);
public record ElectronicsReviewQueue(ElectronicsReviewItem[] Items,int PendingCount,bool HasMore,int NextOffset);
public record ElectronicsReviewLesson(string Id,string GroupName,long CreatedAt);
public record ElectronicsReviewFilters(GroupDto[] Groups,ElectronicsReviewLesson[] Lessons);
public record ElectronicsReviewDetail(ElectronicsReviewItem Item,string Prompt,string ProjectJson,ElectronicsReviewDecision[] History,bool CanSubmit,int? AssignmentVersion,bool IsLatestSubmission);
