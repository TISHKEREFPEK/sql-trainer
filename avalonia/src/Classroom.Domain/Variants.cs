using System.Text.RegularExpressions;

namespace Classroom.Domain;
public static class Variants
{
    const string Tokens = "'(?:''|[^'])*'|--[^\\n]*|/\\*[\\s\\S]*?\\*/|\"(?:\"\"|[^\"])*\"|`[^`]*`|\\[[^\\]]*\\]|\\b[a-zA-Z_]\\w*\\b";
    public static string WithoutComments(string sql) => Regex.Replace(sql, Tokens, m => m.Value.StartsWith("--") || m.Value.StartsWith("/*") ? " " : m.Value);
    public static string Sql(string sql, Dictionary<string, string>? names)
    {
        if (names is null)
            return sql;
        return Regex.Replace(sql, Tokens, match =>
        {
            var token = match.Value;
            if (token.StartsWith('\'') || token.StartsWith("--") || token.StartsWith("/*"))
                return token;
            var quoted = "\"`[".Contains(token[0]);
            var key = (quoted ? token[1..^1] : token).ToLowerInvariant();
            return names.TryGetValue(key, out var value) ? quoted ? token[0] + value + token[^1] : value : token;
        });
    }

    static string Text(string text, Dictionary<string, string> names) => Regex.Replace(text, "\\b[a-zA-Z_]\\w*\\b", m => names.GetValueOrDefault(m.Value.ToLowerInvariant(), m.Value));
    public static int Index(TaskDefinition task, int slot)
    {
        if (task.VariantEligible == false)
            return 1;
        uint hash = 0;
        foreach (var c in task.Project ?? task.Id)
            hash = unchecked(hash * 31 + c);
        return (int)(((ulong)hash + (uint)slot) % 4) + 1;
    }

    public static TaskDefinition Assign(TaskDefinition task, int slot, TaskDefinition[] alternatives)
    {
        var index = Index(task, slot);
        return (index == 1 ? task : alternatives.ElementAtOrDefault(index - 2) ?? task)with
        {
            VariantIndex = index
        };
    }

    public static TaskDefinition[] Create(TaskDefinition task)
    {
        if (task.VariantEligible == false)
            return[];
        return Enumerable.Range(2, 3).Select(index => Create(task, index)).ToArray();
    }

    static TaskDefinition Create(TaskDefinition task, int index)
    {
        var projectNames = task.Project switch
        {
            "library" => new[]
            {
                "books",
                "readers",
                "loans"
            },
            "shop" => ["products", "purchases"],
            "classes" => ["courses", "learners", "enrollments"],
            _ => Array.Empty<string>()};
        var names = new HashSet<string>(projectNames);
        var pattern = "\\b(?:CREATE\\s+(?:TEMP\\s+)?(?:TABLE|VIEW|TRIGGER|(?:UNIQUE\\s+)?INDEX)(?:\\s+IF\\s+NOT\\s+EXISTS)?|FROM|JOIN|UPDATE|INTO|REFERENCES|ALTER\\s+TABLE)\\s+([a-zA-Z_]\\w*)";
        foreach (Match m in Regex.Matches(task.Seed + "\n" + task.Solution, pattern, RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (!new[]
            {
                "select",
                "set",
                "on",
                "of",
                "or",
                "as",
                "where",
                "sqlite_master",
                "sqlite_schema"
            }.Contains(name))
                names.Add(name);
        }

        var map = names.ToDictionary(name => name, name => $"{name}_v{index}");
        var copy = task with
        {
            VariantIndex = index,
            DataVariant = index,
            TableMap = map,
            Seed = Sql(task.Seed, map),
            Solution = Sql(task.Solution, map),
            Example = Sql(task.Example, map),
            Title = Text(task.Title, map),
            Concept = Text(task.Concept, map),
            Prompt = Text(task.Prompt, map),
            Starter = Text(task.Starter, map),
            Terms = [..task.Terms],
            Hints = task.Hints.Select(h => Text(h, map)).ToArray()
        };
        if (task.Id == "where")
        {
            var city = new[]
            {
                "",
                "",
                "Казань",
                "Тула",
                "Москва"
            }[index];
            var extra = index == 4 ? " AND age >= 20" : "";
            copy = copy with
            {
                Solution = Sql($"SELECT name, age FROM students WHERE city = '{city}'{extra};", map),
                Prompt = $"Выведи name и age учеников из города {city}{(index == 4 ? ", которым не меньше 20 лет" : "")}.",
                Hints = ["Сравни city с названием города.", $"Условие: city = '{city}'{extra}."]
            };
        }

        if (task.Id == "sort")
        {
            var limit = new[]
            {
                0,
                2,
                3,
                1,
                4
            }[index];
            copy = copy with
            {
                Solution = Sql($"SELECT name, age FROM students ORDER BY age DESC LIMIT {limit};", map),
                Prompt = $"Покажи имя и возраст {limit} самых старших учеников, от старшего к младшему.",
                Hints = ["Сначала отсортируй age по убыванию.", $"Добавь ORDER BY age DESC LIMIT {limit}."]
            };
        }

        if (task.Id == "parking-cars-filter")
        {
            var brand = new[]
            {
                "",
                "",
                "BMW",
                "Nissan",
                "Kia"
            }[index];
            copy = copy with
            {
                Solution = Sql($"SELECT model, plate FROM cars WHERE brand = '{brand}';", map),
                Title = $"Найди автомобили {brand}",
                Prompt = $"Выведи модели и госномера всех автомобилей {brand}. Назови столбцы model и plate.",
                Hints = ["Выбери model и plate из таблицы автомобилей.", $"Условие: brand = '{brand}'."]
            };
        }

        if (task.Id == "spot-recursive-numbers")
        {
            var limit = new[]
            {
                0,
                10,
                12,
                15,
                20
            }[index];
            copy = copy with
            {
                Solution = Sql($"WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < {limit}) SELECT n FROM numbers;", map),
                Prompt = $"С помощью WITH RECURSIVE выведи числа от 1 до {limit} в столбце n.",
                Hints = ["Начни CTE числом 1.", $"Рекурсивная часть прибавляет 1, пока n меньше {limit}."]
            };
        }

        var tables = map.Keys.Where(name => Regex.IsMatch(task.Seed, $"\\bCREATE\\s+TABLE\\s+{Regex.Escape(name)}\\b", RegexOptions.IgnoreCase)).ToArray();
        if (tables.Length > 0 && !tables.Any(task.Prompt.Contains))
            copy = copy with
            {
                Prompt = copy.Prompt + " Таблицы: " + string.Join(", ", tables.Select(n => map[n])) + "."
            };
        return copy;
    }
}
