using System.Security.Cryptography;
using System.Text;

namespace Classroom.Contracts;

public static class SqlSyncHash
{
    public static string Text(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
public record SqlDraftSync(string Id, string TaskId, string LessonId, string WorkScope, string BaseHash, string Code, long At);
public record SqlDraftReceipt(string Id, string Status, string ServerCode, string ServerHash);
public record SqlActionSync(string Id, string TaskId, string LessonId, string WorkScope, string Action, long At, int Seconds = 0);
public record SqlExecutionReceiptRequest(string TaskId, ExecuteRequest Request);
public record SqlExecutionReceipt(bool Found, ExecutionDto? Result);
