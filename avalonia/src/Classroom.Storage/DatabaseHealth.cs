using System.Text.Json;
using Classroom.Contracts;
using Classroom.Domain;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Storage;

public static class DatabaseHealth
{
    // Validation reads metadata/JSON only. Student SQL is never executed here.
    public static void Validate(ClassroomDb db)
    {
        db.Database.OpenConnection();
        using (var integrity = db.Database.GetDbConnection().CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (integrity.ExecuteScalar() as string != "ok")
                throw new InvalidDataException("Повреждён файл SQLite.");
        }
        using (var foreignKeys = db.Database.GetDbConnection().CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var rows = foreignKeys.ExecuteReader();
            if (rows.Read())
                throw new InvalidDataException("Нарушены связи между записями.");
        }
        var profiles = db.Profiles.AsNoTracking().ToArray();
        if (profiles.Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Login) || p.Role is not ("teacher" or "student") || !Passwords.ValidHash(p.PasswordHash)) || profiles.Select(p => p.Login).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Length)
            throw new InvalidDataException("Некорректные профили в резервной копии.");
        foreach (var p in profiles)
        {
            Themes.Validate(Wire.Read<ThemePreference>(p.ThemeJson));
            Themes.ValidatePresets(Wire.Read<ThemePreset[]>(p.PresetsJson));
        }
        foreach (var row in db.Tasks.AsNoTracking())
            Task(row.Id, row.Json);
        foreach (var row in db.Variants.AsNoTracking())
            Task(row.TaskId, row.Json);
        foreach (var row in db.TaskRevisions.AsNoTracking())
        {
            Task(row.TaskId, row.Json);
            foreach (var alternative in Wire.Read<string[]>(row.AlternativesJson))
                Task(row.TaskId, alternative);
        }
        foreach (var row in db.Assignments.AsNoTracking())
        {
            var task = Task(row.TaskId, row.Json);
            if (Wire.Read<long[]>(row.HintsJson).Length > task.Hints.Length || row.Checks < 0 || row.Errors < 0 || row.Errors > row.Checks || row.ActiveSeconds < 0)
                throw new InvalidDataException("Некорректная статистика в резервной копии.");
        }
        foreach (var row in db.Snapshots.AsNoTracking())
            ElectronImport.ValidateSnapshot(row.Base64);
        foreach (var row in db.Operations.AsNoTracking())
            using (JsonDocument.Parse(row.ResponseJson))
            {
            }
        // Materialize all remaining tables to reject incomplete or incompatible columns.
        _ = db.Groups.AsNoTracking().ToArray();
        _ = db.Attempts.AsNoTracking().ToArray();
        _ = db.Imports.AsNoTracking().ToArray();
        foreach (var row in db.ElectronicsWorks.AsNoTracking())
        {
            var metadata = ElectronicsProjectFormat.Inspect(row.ProjectJson);
            if (row.Sequence < 1 || row.UpdatedAt < 1 || !profiles.Any(p=>p.Id==row.StudentId&&p.Role=="student") || !Guid.TryParse(row.ClientId,out _) || metadata.Name != row.Name || metadata.Parts != row.Parts || metadata.Wires != row.Wires || row.ProjectHash != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(row.ProjectJson))))
                throw new InvalidDataException("Повреждена электронная работа.");
        }
        foreach (var row in db.ElectronicsReports.AsNoTracking())
        {
            var metadata = ElectronicsProjectFormat.Inspect(row.ProjectJson);
            if (row.At < 1 || !profiles.Any(p=>p.Id==row.StudentId&&p.Role=="student") || !Guid.TryParse(row.Id,out _) || !Guid.TryParse(row.ClientId,out _) || !ElectronicsProjectFormat.ReportKinds.Contains(row.Kind) || row.Detail.Length > 16000 || metadata.Name != row.Name || row.ProjectHash != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(row.ProjectJson))))
                throw new InvalidDataException("Повреждён результат по электронике.");
        }
    }

    static TaskDefinition Task(string id, string json)
    {
        var task = Catalog.Read(json);
        Catalog.Validate(task);
        if (task.Id != id)
            throw new InvalidDataException("Идентификатор задания не совпадает с содержимым.");
        return task;
    }
}
