using System.Security.Cryptography;
using System.Text;

namespace Classroom.Domain;

public sealed class ReviewAccessException(string message, bool rateLimited = false) : Exception(message)
{
    public bool RateLimited { get; } = rateLimited;
}
public sealed record ReviewCode(string Id, string Code, long Expires);
public sealed record ReviewLease(string Id, string StudentId, string StudentName, string TeacherId, string IssuerSession, string StudentSession, long Expires)
{
    // The server populates this set under its database gate, after checking the
    // report is pending and owned by this pupil. Reviewed reports stay viewable.
    public HashSet<string> OpenedReports { get; } = [];
}
public sealed record RedeemedReview(string Token, ReviewLease Lease);

public sealed class ReviewAccessRegistry(TimeProvider? timeProvider = null)
{
    sealed record Issued(string Id, string Hash, string StudentId, string StudentName, string TeacherId, string IssuerSession, long Expires);
    readonly object gate = new();
    readonly Dictionary<string, Issued> codes = [];
    readonly Dictionary<string, ReviewLease> leases = [];
    readonly Dictionary<string, (int Count, long Until)> failures = [];
    long Now => (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    void Prune()
    {
        foreach (var key in codes.Where(x => x.Value.Expires <= Now).Select(x => x.Key).ToArray()) codes.Remove(key);
        foreach (var key in leases.Where(x => x.Value.Expires <= Now).Select(x => x.Key).ToArray()) leases.Remove(key);
        foreach (var key in failures.Where(x => x.Value.Until <= Now).Select(x => x.Key).ToArray()) failures.Remove(key);
    }
    public ReviewCode Issue(string studentId, string studentName, string teacherId, string issuerSession)
    {
        lock (gate)
        {
            Prune();
            foreach (var key in codes.Where(x => x.Value.StudentId == studentId).Select(x => x.Key).ToArray()) codes.Remove(key);
            foreach (var key in leases.Where(x => x.Value.StudentId == studentId).Select(x => x.Key).ToArray()) leases.Remove(key);
            string code;
            do { code = string.Concat(Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetInt32(10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture))); } while (codes.ContainsKey(Hash(code)));
            var id = Guid.NewGuid().ToString("N"); var expires = Now + 5 * 60_000;
            codes.Add(Hash(code), new(id, Hash(code), studentId, studentName, teacherId, issuerSession, expires));
            return new(id, code[..4] + "-" + code[4..8] + "-" + code[8..], expires);
        }
    }
    public RedeemedReview Redeem(string code, string studentId, string studentSession, Func<string, bool> issuerAlive)
    {
        lock (gate)
        {
            Prune();
            if (failures.TryGetValue(studentId, out var failed) && failed.Count >= 5) throw new ReviewAccessException("Слишком много неверных кодов. Повторите через минуту.", true);
            var normalized = new string(code.Where(c => c is not (' ' or '-')).ToArray());
            var hash = Hash(normalized);
            if (normalized.Length != 12 || normalized.Any(c => c is < '0' or > '9') || !codes.TryGetValue(hash, out var issued) || issued.StudentId != studentId || !issuerAlive(issued.IssuerSession))
            {
                failures[studentId] = (failed.Count + 1, failed.Until > Now ? failed.Until : Now + 60_000);
                throw new ReviewAccessException("Код недействителен, использован или истёк. Получите новый код у преподавателя.");
            }
            codes.Remove(hash); failures.Remove(studentId);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var lease = new ReviewLease(issued.Id, studentId, issued.StudentName, issued.TeacherId, issued.IssuerSession, studentSession, Now + 15 * 60_000);
            leases.Add(Hash(token), lease);
            return new(token, lease);
        }
    }
    public ReviewLease Authorize(string token, string studentId, string studentSession, Func<string, bool> issuerAlive)
    {
        lock (gate)
        {
            Prune();
            if (!leases.TryGetValue(Hash(token), out var lease) || lease.StudentId != studentId || lease.StudentSession != studentSession || !issuerAlive(lease.IssuerSession))
                throw new ReviewAccessException("Режим проверки завершён. Вернитесь к работе ученика или введите новый код.");
            return lease;
        }
    }
    public void End(string token) { lock (gate) leases.Remove(Hash(token)); }
    public void Revoke(string id, string teacherId)
    {
        lock (gate)
        {
            foreach (var key in codes.Where(x => x.Value.Id == id && x.Value.TeacherId == teacherId).Select(x => x.Key).ToArray()) codes.Remove(key);
            foreach (var key in leases.Where(x => x.Value.Id == id && x.Value.TeacherId == teacherId).Select(x => x.Key).ToArray()) leases.Remove(key);
        }
    }
    public void Clear() { lock (gate) { codes.Clear(); leases.Clear(); failures.Clear(); } }
}
