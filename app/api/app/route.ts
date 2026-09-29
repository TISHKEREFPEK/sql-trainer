import { env } from "cloudflare:workers";
import { allTasks, type Task } from "../../../lib/course";
import { parkingSpotsSeed, teacherDatasetSeeds } from "../../../lib/teacher-data";
import { parkingDatabaseSeed } from "../../../lib/parking-dataset";

export const runtime = "edge";
type Session = { role: "admin" | "student"; learnerId: string | null };
type Learner = { id:string; name:string; group_id:string; completed_json:string; snapshots_json:string };
const datasets = {...teacherDatasetSeeds,"parking-spots":parkingSpotsSeed,"parking-database":parkingDatabaseSeed};
const projectIds = new Set(["library","shop","classes"]);
const json = (value:unknown,status=200) => Response.json(value,{status,headers:{"Cache-Control":"no-store"}});
const db = () => {
  const binding = (env as unknown as {DB?:D1Database}).DB;
  if (!binding) throw new Error("Хранилище временно недоступно");
  return binding;
};
const cookie = (request:Request) => /(?:^|;\s*)sql_session=([^;]+)/.exec(request.headers.get("cookie") || "")?.[1];
async function digest(value:string) {
  const bytes = new TextEncoder().encode(value);
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256",bytes))].map(x=>x.toString(16).padStart(2,"0")).join("");
}
async function session(request:Request):Promise<Session|null> {
  const token = cookie(request);
  if (!token) return null;
  const hash = await digest(token);
  const row = await db().prepare("SELECT role, learner_id, expires_at FROM sessions WHERE token_hash=?").bind(hash).first<{role:string;learner_id:string|null;expires_at:number}>();
  if (!row || row.expires_at < Date.now()) return null;
  return {role:row.role as Session["role"],learnerId:row.learner_id};
}
async function newSession(role:Session["role"],learnerId:string|null) {
  const token = crypto.randomUUID()+crypto.randomUUID();
  const hash = await digest(token);
  await db().prepare("INSERT INTO sessions (token_hash,role,learner_id,expires_at) VALUES (?,?,?,?)").bind(hash,role,learnerId,Date.now()+30*86400000).run();
  return `sql_session=${token}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=2592000`;
}
function sameOrigin(request:Request) {
  const origin = request.headers.get("origin");
  return !!origin && origin === new URL(request.url).origin;
}
function safeJson<T>(raw:string,fall:T):T { try { return JSON.parse(raw) as T; } catch { return fall; } }
function validTask(value:unknown): value is Task {
  if (!value || typeof value !== "object") return false;
  const task = value as Record<string, unknown>;
  return typeof task.id === "string" && /^[a-zA-Z0-9_-]{1,64}$/.test(task.id)
    && typeof task.module === "string" && task.module.trim().length > 0 && task.module.length <= 100
    && typeof task.title === "string" && task.title.trim().length > 0 && task.title.length <= 160
    && typeof task.minutes === "number" && task.minutes >= 1 && task.minutes <= 240
    && ["concept", "example", "prompt", "starter", "solution", "seed"].every(key => typeof task[key] === "string" && (task[key] as string).length <= 100000)
    && Array.isArray(task.terms) && task.terms.length <= 20 && task.terms.every(x => typeof x === "string" && x.length <= 80)
    && Array.isArray(task.hints) && task.hints.length === 2 && task.hints.every(x => typeof x === "string" && x.length <= 1000)
    && (task.mode === undefined || task.mode === "query" || task.mode === "state")
    && (task.seedProfile === undefined || typeof task.seedProfile === "string" && Object.hasOwn(datasets, task.seedProfile))
    && (task.ordered === undefined || typeof task.ordered === "boolean");
}
async function taskCatalog(): Promise<Task[]> {
  const rows = await db().prepare("SELECT id,task_json,deleted FROM teacher_tasks").all<{id:string;task_json:string;deleted:number}>();
  const catalog = new Map(allTasks.map(task => [task.id, task]));
  for (const row of rows.results) {
    if (row.deleted) { catalog.delete(row.id); continue; }
    const task = safeJson<unknown>(row.task_json, null);
    if (validTask(task) && task.id === row.id) catalog.set(task.id, task);
  }
  return [...catalog.values()];
}
function learnerTasks(tasks:Task[],learnerId:string):Task[] {
  let hash=2166136261;
  for (const character of learnerId) hash=Math.imul(hash^character.charCodeAt(0),16777619);
  const variant=(hash>>>0)%24;
  const thresholds=[250,350,450,550,650,750];
  const status=variant%2===0?"new":"paid";
  const direction=Math.floor(variant/2)%2===0?"ASC":"DESC";
  const threshold=thresholds[Math.floor(variant/4)];
  return tasks.map(task=>{
    if(task.id==="random-orders") return {
      ...task,
      prompt:`Покажи id и amount заказов со статусом '${status}' и суммой больше ${threshold}. Отсортируй по amount ${direction === "ASC" ? "по возрастанию" : "по убыванию"}.`,
      solution:`SELECT id, amount FROM orders WHERE status = '${status}' AND amount > ${threshold} ORDER BY amount ${direction};`,
      example:`SELECT id, amount FROM orders WHERE status = '${status}' AND amount > ${threshold} ORDER BY amount ${direction};`,
      hints:[`Отфильтруй status = '${status}' и amount > ${threshold}.`,`Добавь ORDER BY amount ${direction}.`]
    };
    if(task.id==="where") {
      const city=["Москва","Казань","Тула"][variant%3];
      return {...task,prompt:`Выведи name и age учеников из города ${city}.`,solution:`SELECT name, age FROM students WHERE city = '${city}';`,example:`SELECT name, age FROM students WHERE city = '${city}';`,hints:[`Сравни city со значением '${city}'.`,`Условие: WHERE city = '${city}'.`]};
    }
    if(task.id==="sort") {
      const youngest=Math.floor(variant/3)%2===1;
      const limit=1+(variant%3);
      const direction=youngest?"ASC":"DESC";
      return {...task,prompt:`Покажи имя и возраст ${limit} ${youngest?"самых младших учеников":"самых старших учеников"}, от ${youngest?"младшего к старшему":"старшего к младшему"}.`,solution:`SELECT name, age FROM students ORDER BY age ${direction} LIMIT ${limit};`,example:`SELECT name, age FROM students ORDER BY age ${direction} LIMIT ${limit};`,hints:[`Отсортируй age по ${youngest?"возрастанию":"убыванию"}.`,`Добавь ORDER BY age ${direction} LIMIT ${limit}.`]};
    }
    if(task.id==="aggregate") {
      const chosenStatus=status;
      return {...task,prompt:`Посчитай количество заказов со статусом '${chosenStatus}' и назови столбец total.`,solution:`SELECT COUNT(*) AS total FROM orders WHERE status = '${chosenStatus}';`,example:`SELECT COUNT(*) AS total FROM orders WHERE status = '${chosenStatus}';`,hints:[`Добавь фильтр status = '${chosenStatus}'.`,`Используй COUNT(*) AS total и WHERE status = '${chosenStatus}'.`]};
    }
    if(task.id==="group") {
      const byStudent=variant%2===1;
      const column=byStudent?"student_id":"status";
      return {...task,prompt:`Покажи ${column} и общую сумму amount для каждого значения. Сумму назови total.`,solution:`SELECT ${column}, SUM(amount) AS total FROM orders GROUP BY ${column};`,example:`SELECT ${column}, SUM(amount) AS total FROM orders GROUP BY ${column};`,hints:[`Сгруппируй строки по ${column}.`,`Используй SUM(amount) AS total и GROUP BY ${column}.`]};
    }
    if(task.id==="subquery") {
      const younger=variant%2===1;
      const operator=younger?"<":">";
      return {...task,prompt:`Покажи name учеников ${younger?"младше":"старше"} среднего возраста.`,solution:`SELECT name FROM students WHERE age ${operator} (SELECT AVG(age) FROM students);`,example:`SELECT name FROM students WHERE age ${operator} (SELECT AVG(age) FROM students);`,hints:[`Внутренний запрос вычисляет средний age.`,`Сравни age со средним через знак '${operator}'.`]};
    }
    return task;
  });
}
function validName(input:unknown,max=60):input is string { return typeof input === "string" && input.trim().length >= 2 && input.trim().length <= max; }
async function learnerData(id:string) {
  return db().prepare("SELECT id,name,group_id,completed_json,snapshots_json FROM learners WHERE id=?").bind(id).first<Learner>();
}
function progress(row:Learner) { return {name:row.name,completed:safeJson<string[]>(row.completed_json,[]),snapshots:safeJson<Record<string,string>>(row.snapshots_json,{})}; }

export async function GET(request:Request) {
  try {
    const requestedSeed = new URL(request.url).searchParams.get("seedProfile");
    if (requestedSeed) {
      const seed = datasets[requestedSeed];
      return seed ? json({seed}) : json({error:"Набор данных не найден"},404);
    }
    const auth = await session(request);
    const tasks = await taskCatalog();
    if (!auth) return json({role:"guest",tasks});
    if (auth.role === "student" && auth.learnerId) {
      const row = await learnerData(auth.learnerId);
      return row ? json({role:"student",...progress(row),tasks:learnerTasks(tasks,row.id)}) : json({role:"guest",tasks});
    }
    const groups = await db().prepare("SELECT id,name,code,created_at FROM groups ORDER BY created_at DESC").all();
    const students = await db().prepare("SELECT id,name,group_id,completed_json,updated_at FROM learners ORDER BY updated_at DESC").all();
    const errors = await db().prepare("SELECT task_id,message,COUNT(*) AS count FROM attempts GROUP BY task_id,message ORDER BY count DESC LIMIT 20").all();
    return json({role:"admin",groups:groups.results,students:students.results.map(row=>({...row,completed:safeJson<string[]>(String(row.completed_json),[])})),errors:errors.results,tasks});
  } catch { return json({error:"Не удалось загрузить данные. Попробуйте ещё раз."},503); }
}

export async function POST(request:Request) {
  if (!sameOrigin(request)) return json({error:"Недопустимый источник запроса"},403);
  try {
    const body = await request.json() as Record<string,unknown>;
    const action = body.action;
    if (action === "admin-login") {
      const configured = (env as unknown as {ADMIN_PASSWORD_HASH?:string}).ADMIN_PASSWORD_HASH;
      if (!configured) return json({error:"Пароль администратора ещё не настроен"},503);
      if (typeof body.password !== "string" || (await digest(body.password)) !== configured) return json({error:"Неверный пароль"},401);
      const response = json({role:"admin",tasks:await taskCatalog()}); response.headers.set("Set-Cookie",await newSession("admin",null)); return response;
    }
    if (action === "student-login") {
      if (!validName(body.name,40) || typeof body.code !== "string") return json({error:"Введите имя и код группы"},400);
      const code = body.code.trim().toUpperCase();
      const group = await db().prepare("SELECT id FROM groups WHERE code=?").bind(code).first<{id:string}>();
      if (!group) return json({error:"Группа с таким кодом не найдена"},404);
      const name = body.name.trim(); const key = name.toLocaleLowerCase("ru");
      let row = await db().prepare("SELECT id FROM learners WHERE group_id=? AND name_key=?").bind(group.id,key).first<{id:string}>();
      if (!row) {
        const id = crypto.randomUUID();
        await db().prepare("INSERT OR IGNORE INTO learners (id,group_id,name,name_key,completed_json,snapshots_json,updated_at) VALUES (?,?,?,?,?,?,?)").bind(id,group.id,name,key,"[]","{}",Date.now()).run();
        row = await db().prepare("SELECT id FROM learners WHERE group_id=? AND name_key=?").bind(group.id,key).first<{id:string}>();
      }
      if (!row) throw new Error("learner creation failed");
      const learner = await learnerData(row.id);
      if (!learner) throw new Error("learner missing");
      const response = json({role:"student",...progress(learner),tasks:learnerTasks(await taskCatalog(),row.id)}); response.headers.set("Set-Cookie",await newSession("student",row.id)); return response;
    }
    const auth = await session(request);
    if (action === "logout") {
      const token = cookie(request); if (token) await db().prepare("DELETE FROM sessions WHERE token_hash=?").bind(await digest(token)).run();
      const response = json({role:"guest"}); response.headers.set("Set-Cookie","sql_session=; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=0"); return response;
    }
    if (!auth) return json({error:"Сначала войдите в группу"},401);
    if (action === "create-group" && auth.role === "admin") {
      if (!validName(body.name)) return json({error:"Введите название группы"},400);
      const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
      const values = crypto.getRandomValues(new Uint8Array(10));
      const code = [...values].map(x=>alphabet[x%alphabet.length]).join("");
      await db().prepare("INSERT INTO groups (id,name,code,created_at) VALUES (?,?,?,?)").bind(crypto.randomUUID(),body.name.trim(),code,Date.now()).run();
      return json({ok:true,code});
    }
    if (action === "seed-demo" && auth.role === "admin") {
      const definitions = [
        { key:"base", name:"SQL · демо-группа 1", code:"DEMO26BAS", learners:[
          { name:"Тест · не начинал", fraction:0, daysAgo:0 },
          { name:"Тест · старт", fraction:0.18, daysAgo:1 },
          { name:"Тест · середина", fraction:0.56, daysAgo:2 },
        ]},
        { key:"advanced", name:"SQL · демо-группа 2", code:"DEMO26ADV", learners:[
          { name:"Тест · знакомство", fraction:0.08, daysAgo:0 },
          { name:"Тест · практика", fraction:0.37, daysAgo:1 },
          { name:"Тест · всё пройдено", fraction:1, daysAgo:2 },
        ]},
      ];
      const tasks = await taskCatalog();
      const createdGroups: {id:string;name:string;code:string}[] = [];
      for (const definition of definitions) {
        const id = `demo-group-${definition.key}`;
        await db().prepare("INSERT OR IGNORE INTO groups (id,name,code,created_at) VALUES (?,?,?,?)").bind(id,definition.name,definition.code,Date.now()).run();
        const group = await db().prepare("SELECT id,name,code FROM groups WHERE code=?").bind(definition.code).first<{id:string;name:string;code:string}>();
        if (!group) continue;
        createdGroups.push(group);
        for (const [index, learner] of definition.learners.entries()) {
          const learnerId = `demo-learner-${definition.key}-${index + 1}`;
          const completedCount = Math.round(tasks.length * learner.fraction);
          const completed = tasks.slice(0,completedCount).map(task => task.id);
          const nameKey = learner.name.toLocaleLowerCase("ru");
          await db().prepare("INSERT INTO learners (id,group_id,name,name_key,completed_json,snapshots_json,updated_at) VALUES (?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET group_id=excluded.group_id,name=excluded.name,name_key=excluded.name_key,completed_json=excluded.completed_json,snapshots_json=excluded.snapshots_json,updated_at=excluded.updated_at")
            .bind(learnerId,group.id,learner.name,nameKey,JSON.stringify(completed),"{}",Date.now()-learner.daysAgo*86400000).run();
        }
      }
      return json({ok:true,groups:createdGroups});
    }
    if (auth.role === "admin" && action === "save-task") {
      if (!validTask(body.task)) return json({error:"Проверьте поля задания: часть данных отсутствует или слишком длинная"},400);
      const task = body.task;
      const originalId = typeof body.originalId === "string" ? body.originalId : "";
      if (originalId && originalId !== task.id && (await taskCatalog()).some(item => item.id === task.id)) return json({error:"Задание с таким идентификатором уже существует"},409);
      if (originalId && originalId !== task.id) {
        await db().prepare("INSERT INTO teacher_tasks (id,task_json,deleted,updated_at) VALUES (?, '{}',1,?) ON CONFLICT(id) DO UPDATE SET deleted=1,updated_at=excluded.updated_at")
          .bind(originalId,Date.now()).run();
      }
      await db().prepare("INSERT INTO teacher_tasks (id,task_json,deleted,updated_at) VALUES (?,?,0,?) ON CONFLICT(id) DO UPDATE SET task_json=excluded.task_json,deleted=0,updated_at=excluded.updated_at")
        .bind(task.id,JSON.stringify(task),Date.now()).run();
      return json({ok:true,tasks:await taskCatalog()});
    }
    if (auth.role === "admin" && action === "delete-task") {
      if (typeof body.taskId !== "string" || !/^[a-zA-Z0-9_-]{1,64}$/.test(body.taskId)) return json({error:"Некорректный идентификатор задания"},400);
      await db().prepare("INSERT INTO teacher_tasks (id,task_json,deleted,updated_at) VALUES (?, '{}',1,?) ON CONFLICT(id) DO UPDATE SET deleted=1,updated_at=excluded.updated_at")
        .bind(body.taskId,Date.now()).run();
      return json({ok:true,tasks:await taskCatalog()});
    }
    if (auth.role !== "student" || !auth.learnerId) return json({error:"Нет доступа"},403);
    const learner = await learnerData(auth.learnerId);
    if (!learner) return json({error:"Профиль не найден"},404);
    const activeTaskIds = new Set((await taskCatalog()).map(task => task.id));
    if (action === "progress") {
      const completed = new Set(safeJson<string[]>(learner.completed_json,[]).filter(x=>activeTaskIds.has(x)));
      if (Array.isArray(body.completed)) for (const id of body.completed) if (typeof id === "string" && activeTaskIds.has(id)) completed.add(id);
      const snapshots = safeJson<Record<string,string>>(learner.snapshots_json,{});
      if (typeof body.project === "string" && projectIds.has(body.project) && typeof body.snapshot === "string" && body.snapshot.length < 250000) snapshots[body.project] = body.snapshot;
      await db().prepare("UPDATE learners SET completed_json=?,snapshots_json=?,updated_at=? WHERE id=?").bind(JSON.stringify([...completed]),JSON.stringify(snapshots),Date.now(),learner.id).run();
      return json({ok:true,completed:[...completed],snapshots});
    }
    if (action === "attempt") {
      if (typeof body.taskId !== "string" || !activeTaskIds.has(body.taskId) || typeof body.message !== "string") return json({error:"Некорректная попытка"},400);
      await db().prepare("INSERT INTO attempts (id,learner_id,task_id,message,created_at) VALUES (?,?,?,?,?)").bind(crypto.randomUUID(),learner.id,body.taskId,body.message.slice(0,120),Date.now()).run();
      return json({ok:true});
    }
    return json({error:"Неизвестное действие"},400);
  } catch { return json({error:"Не удалось сохранить данные. Попробуйте ещё раз."},503); }
}
