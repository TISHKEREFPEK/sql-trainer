namespace Classroom.Contracts;

public record CreateReviewAccess(string StudentId);
public record ReviewAccessCode(string Id,string Code,long Expires);
public record RedeemReviewAccess(string Code);
public record ReviewAccessSession(string Token,string StudentId,string StudentName,long Expires);
public record ReviewAccessStatus(string StudentId,string StudentName,long Expires);
