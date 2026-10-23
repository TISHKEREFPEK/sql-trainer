namespace Classroom.Contracts;

public record ElectronicsReportRequest(string Id,string Kind,string ProjectJson,string Detail);
public record ElectronicsSyncRequest(string ClientId,long Sequence,string ProjectJson,ElectronicsReportRequest[] Reports);
public record ElectronicsSyncReceipt(long Sequence,long UpdatedAt,string[] ReportIds);
public record ElectronicsStudentResult(string StudentId,string Login,string? GroupId,int WorkCount,int Submitted,int Compiled,int Errors,long? LastAt,string? LastKind);
public record ElectronicsOverview(GroupDto[] Groups,ElectronicsStudentResult[] Students);
public record ElectronicsWorkInfo(string ClientId,string Name,int Parts,int Wires,long UpdatedAt);
public record ElectronicsReportInfo(string Id,string ClientId,string Name,string Kind,long At);
public record ElectronicsStudentDetail(string StudentId,string Login,string? GroupId,ElectronicsWorkInfo[] Works,ElectronicsReportInfo[] Reports,bool HasMore,int NextOffset);
public record ElectronicsSnapshot(string ProjectJson,string Kind,long At,string Detail);
