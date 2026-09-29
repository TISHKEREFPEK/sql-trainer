import type { Database } from "sql.js";
import type { Task } from "./course";

export const VARIANT_COUNT = 4;
const projectTables: Record<string, string[]> = {
  library: ["books", "readers", "loans"],
  shop: ["products", "purchases"],
  classes: ["courses", "learners", "enrollments"],
};

/** Rewrite identifiers, never string values or SQL comments. */
export function variantSql(sql: string, names: Record<string, string> = {}) {
  return sql.replace(/'(?:''|[^'])*'|--[^\n]*|\/\*[\s\S]*?\*\/|"(?:""|[^"])*"|`[^`]*`|\[[^\]]*\]|\b[a-zA-Z_]\w*\b/g, token => {
    if (token.startsWith("'") || token.startsWith("--") || token.startsWith("/*")) return token;
    const quoted = ['"', '`', '['].includes(token[0]);
    const key = quoted ? token.slice(1, -1) : token;
    const replacement = names[key.toLowerCase()];
    return replacement ? quoted ? `${token[0]}${replacement}${token.at(-1)}` : replacement : token;
  });
}
function variantText(text: string, names: Record<string, string>) {
  return text.replace(/\b[a-zA-Z_]\w*\b/g, name => names[name.toLowerCase()] || name);
}
function namesFor(task: Task, seed: string, index: number) {
  const sql = `${seed}\n${task.solution}`;
  const names = new Set(projectTables[task.project || ""] || []);
  const pattern = /\b(?:CREATE\s+(?:TEMP\s+)?(?:TABLE|VIEW|TRIGGER|(?:UNIQUE\s+)?INDEX)(?:\s+IF\s+NOT\s+EXISTS)?|FROM|JOIN|UPDATE|INTO|REFERENCES|ALTER\s+TABLE)\s+([a-zA-Z_]\w*)/gi;
  for (const match of sql.matchAll(pattern)) {
    const name = match[1].toLowerCase();
    if (!["select", "set", "on", "of", "or", "as", "where", "sqlite_master", "sqlite_schema"].includes(name)) names.add(name);
  }
  return Object.fromEntries([...names].map(name => [name, `${name}_v${index}`]));
}

/** Base + three alternatives preserve the concept and all project dependencies. */
export function createTaskVariants(task: Task, seedOverride = task.seed): Task[] {
  if (task.variantEligible === false) return [];
  return [2, 3, 4].map(index => {
    const tableMap = namesFor(task, seedOverride, index);
    const copy: Task = { ...task, terms: [...task.terms], hints: [...task.hints], variantIndex: index, tableMap, dataVariant: index };
    for (const field of ["seed", "solution", "example"] as const) copy[field] = variantSql(task[field], tableMap);
    for (const field of ["title", "concept", "prompt", "starter"] as const) copy[field] = variantText(task[field], tableMap);
    copy.hints = task.hints.map(hint => variantText(hint, tableMap)) as [string, string];
    if (task.id === "where") {
      const city = ["", "", "Казань", "Тула", "Москва"][index];
      const extra = index === 4 ? " AND age >= 20" : "";
      copy.solution = variantSql(`SELECT name, age FROM students WHERE city = '${city}'${extra};`, tableMap);
      copy.prompt = `Выведи name и age учеников из города ${city}${index === 4 ? ", которым не меньше 20 лет" : ""}.`;
      copy.hints = ["Сравни city с названием города.", `Условие: city = '${city}'${extra}.`];
    }
    if (task.id === "sort") {
      const limit = [0, 2, 3, 1, 4][index];
      copy.solution = variantSql(`SELECT name, age FROM students ORDER BY age DESC LIMIT ${limit};`, tableMap);
      copy.prompt = `Покажи имя и возраст ${limit} самых старших учеников, от старшего к младшему.`;
      copy.hints = ["Сначала отсортируй age по убыванию.", `Добавь ORDER BY age DESC LIMIT ${limit}.`];
    }
    if (task.id === "parking-cars-filter") {
      const brand = ["", "", "BMW", "Nissan", "Kia"][index];
      copy.solution = variantSql(`SELECT model, plate FROM cars WHERE brand = '${brand}';`, tableMap);
      copy.title = `Найди автомобили ${brand}`;
      copy.prompt = `Выведи модели и госномера всех автомобилей ${brand}. Назови столбцы model и plate.`;
      copy.hints = ["Выбери model и plate из таблицы автомобилей.", `Условие: brand = '${brand}'.`];
    }
    if (task.id === "spot-recursive-numbers") {
      const limit = [0, 10, 12, 15, 20][index];
      copy.solution = variantSql(`WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < ${limit}) SELECT n FROM numbers;`, tableMap);
      copy.prompt = `С помощью WITH RECURSIVE выведи числа от 1 до ${limit} в столбце n.`;
      copy.hints = ["Начни CTE числом 1.", `Рекурсивная часть прибавляет 1, пока n меньше ${limit}.`];
    }
    const tables = Object.keys(tableMap).filter(name => /\bCREATE\s+TABLE\b/i.test(seedOverride) && new RegExp(`\\bCREATE\\s+TABLE\\s+${name}\\b`, "i").test(seedOverride));
    if (tables.length && !tables.some(name => task.prompt.includes(name))) copy.prompt += ` Таблицы: ${tables.map(name => tableMap[name]).join(", ")}.`;
    // No solution is copied into examples. Non-intro variants also use different data.
    return copy;
  });
}

/** Round robin within a group; all steps of a project use the same slot. */
export function assignedVariantIndex(task: Task, enrollmentIndex: number) {
  if (task.variantEligible === false) return 1;
  const key = task.project || task.id;
  let hash = 0;
  for (const character of key) hash = (Math.imul(hash, 31) + character.charCodeAt(0)) >>> 0;
  return (hash + enrollmentIndex) % VARIANT_COUNT + 1;
}
export function variantForLearner(task: Task, enrollmentIndex: number, alternatives: Task[]) {
  const index = assignedVariantIndex(task, enrollmentIndex);
  return { ...(index === 1 ? task : alternatives[index - 2] || task), variantIndex: index };
}

/** Change meaningful numeric data while preserving IDs, links, signs and zeroes. */
export function prepareVariantDatabase(db: Database, task: Task) {
  if (!task.dataVariant) return;
  const allowed = new Set(["age", "amount", "price", "daily_rate", "quantity", "prize", "debt", "paid_amount"]);
  const tables = db.exec("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")[0]?.values || [];
  const factor = 1 + (task.dataVariant - 1) * 0.15;
  for (const [name] of tables) {
    const table = String(name).replaceAll('"', '""');
    const columns = db.exec(`PRAGMA table_info("${table}")`)[0]?.values || [];
    for (const column of columns) {
      const field = String(column[1]);
      if (!allowed.has(field) || !/INT|REAL|NUM|DEC|FLOAT|DOUBLE/i.test(String(column[2]))) continue;
      const safe = field.replaceAll('"', '""');
      db.run(`UPDATE "${table}" SET "${safe}" = ROUND("${safe}" * ${factor}) WHERE "${safe}" IS NOT NULL`);
    }
  }
}
