using Classroom.Contracts;
using Classroom.Worker;

try
{
    var input = Console.ReadLine() ?? throw new InvalidDataException("Не переданы данные проверки.");
    if (input.Length > 64 * 1024 * 1024)
        throw new InvalidDataException("Слишком большой запрос проверки.");
    WorkerPrivileges.Restrict();
    Console.WriteLine(Wire.Write(SqlEngine.Evaluate(Wire.Read<WorkRequest>(input))));
}
catch (Exception)
{
    Console.Error.WriteLine("Не удалось выполнить проверку SQL.");
    Environment.ExitCode = 1;
}
