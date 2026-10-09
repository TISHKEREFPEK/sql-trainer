using System.Runtime.InteropServices;

namespace Classroom.Worker;
internal static class WorkerPrivileges
{
    [StructLayout(LayoutKind.Sequential)]
    struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")]
    static extern nint LocalFree(nint memory);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSidToSid(string text, out nint sid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CreateRestrictedToken(nint token, uint flags, uint disableCount, ref SidAndAttributes disabled, uint deleteCount, nint deletePrivileges, uint restrictCount, nint restricted, out nint result);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint duplicated);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetThreadToken(nint thread, nint token);
    public static void Restrict()
    {
        if (!OperatingSystem.IsWindows())
            return;
        nint token = 0, sid = 0, reduced = 0, impersonation = 0;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0xE, out token) || !ConvertStringSidToSid("S-1-5-32-544", out sid))
                throw new IOException("Не удалось ограничить права процесса проверки.");
            var disabled = new SidAndAttributes
            {
                Sid = sid,
                Attributes = 0x10
            };
            if (!CreateRestrictedToken(token, 1, 1, ref disabled, 0, 0, 0, 0, out reduced) || !DuplicateTokenEx(reduced, 0xE, 0, 2, 2, out impersonation) || !SetThreadToken(0, impersonation))
                throw new IOException("Не удалось ограничить права процесса проверки.");
        }
        finally
        {
            if (impersonation != 0)
                CloseHandle(impersonation);
            if (reduced != 0)
                CloseHandle(reduced);
            if (token != 0)
                CloseHandle(token);
            if (sid != 0)
                LocalFree(sid);
        }
    }
}
