using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Classroom.Domain;
public record TaskDefinition
{
    public required string Id { get; init; }
    public required string Module { get; init; }
    public required string Title { get; init; }
    public int Minutes { get; init; }
    public required string Concept { get; init; }
    public required string[] Terms { get; init; }
    public required string Example { get; init; }
    public required string Prompt { get; init; }
    public required string Starter { get; init; }
    public required string Solution { get; init; }
    public required string[] Hints { get; init; }
    public required string Seed { get; init; }
    public string? SeedProfile { get; init; }
    public string? Mode { get; init; }
    public bool Ordered { get; init; }
    public string? Project { get; init; }
    public bool? VariantEligible { get; init; }
    public int? VariantIndex { get; init; }
    public Dictionary<string, string>? TableMap { get; init; }
    public int? DataVariant { get; init; }
}

public sealed record CourseData(TaskDefinition[] Tasks, Dictionary<string, string> Glossary, Dictionary<string, string> GlossaryLong, Dictionary<string, string>? SeedProfiles = null);
public static class Catalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly CourseData Data = Load();
    static CourseData Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Classroom.Domain.Data.catalog.json")!;
        var raw = JsonSerializer.Deserialize<CourseData>(stream, Json)!;
        return raw with
        {
            Tasks = raw.Tasks.Select(t => t with { Seed = t.SeedProfile is { } profile ? raw.SeedProfiles![profile] : t.Seed, SeedProfile = null }).ToArray()
        };
    }

    public static TaskDefinition Read(string json) => JsonSerializer.Deserialize<TaskDefinition>(json, Json) ?? throw new InvalidDataException("Некорректное задание.");
    public static string Write(TaskDefinition task) => JsonSerializer.Serialize(task, Json);
    public static void Validate(TaskDefinition task)
    {
        if (!Regex.IsMatch(task.Id ?? "", "^[a-zA-Z0-9_-]{1,100}$") || string.IsNullOrWhiteSpace(task.Title) || string.IsNullOrWhiteSpace(task.Prompt) || string.IsNullOrWhiteSpace(task.Solution) || task.Hints is null || task.Terms is null || task.Seed is null || task.Concept is null || task.Example is null || task.Starter is null || task.Module is null || task.Terms.Any(t => string.IsNullOrWhiteSpace(t)) || task.Terms.Distinct().Count() != task.Terms.Length || task.Hints.Any(h => h is null) || task.Mode is not (null or "query" or "state") || task.Solution.Length > 12000 || task.Seed.Length > 500000)
            throw new InvalidDataException("Проверьте поля задания.");
    }
}
