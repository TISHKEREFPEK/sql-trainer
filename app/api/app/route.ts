import { env } from "cloudflare:workers";
import { allTasks, type Task } from "../../../lib/course";
import { parkingSpotsSeed, teacherDatasetSeeds } from "../../../lib/teacher-data";
import { parkingDatabaseSeed } from "../../../lib/parking-dataset";
import { createTaskVariants, variantForLearner } from "../../../lib/task-variants";

import { createTaskVariants as legacyVariants, variantForLearner as legacyAssignment } from "../../../lib/legacy-task-variants";

export const runtime = "edge";
type Session = { role: "admin" | "student"; learnerId: string | null };
type Learner = { id:string; name:string; group_id:string; completed_json:string; snapshots_json:string };
const datasets: Record<string,string> = {...teacherDatasetSeeds,"parking-spots":parkingSpotsSeed,"parking-database":parkingDatabaseSeed};
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
    && (task.ordered === undefined || typeof task.ordered === "boolean")
    && (task.variantEligible === undefined || typeof task.variantEligible === "boolean");
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
async function taskVariantPools(tasks:Task[]) {
  const rows = await db().prepare("SELECT task_id,variant_index,variant_json FROM teacher_task_variants ORDER BY task_id,variant_index").all<{task_id:string;variant_index:number;variant_json:string}>();
  const pools:Record<string,Task[]> = {};
  for (const task of tasks) {
    if (task.variantEligible === false) { pools[task.id] = []; continue; }
    const seed = task.seedProfile ? datasets[task.seedProfile] : task.seed;
    const variants = createTaskVariants(task,seed);
    for (const row of rows.results.filter(item=>item.task_id===task.id)) {
      const candidate = safeJson<unknown>(row.variant_json,null);
      if (Number.isInteger(row.variant_index) && row.variant_index >= 1 && row.variant_index <= 3 && validTask(candidate)) {
        const value = candidate as Task;
        if (value.id===task.id && value.mode===task.mode && value.project===task.project) variants[row.variant_index-1] = {...value,variantIndex:row.variant_index+1};
      }
    }
    pools[task.id] = variants;
  }
  return pools;
}
async function learnerTasks(tasks:Task[],learner: Learner,pools:Record<string,Task[]>) {
  const saved = await db().prepare("SELECT task_id,task_json FROM learner_task_variants WHERE learner_id=?").bind(learner.id).all<{task_id:string;task_json:string}>();
  const assignments = new Map(saved.results.map(row=>[row.task_id,safeJson<Task|null>(row.task_json,null)]));
  const position = await db().prepare("SELECT COUNT(*) AS ordinal FROM learners WHERE group_id=? AND rowid < (SELECT rowid FROM learners WHERE id=?)").bind(learner.group_id,learner.id).first<{ordinal:number}>();
  const snapshots=safeJson<Record<string,string>>(learner.snapshots_json,{});
  const result:Task[]=[];
  const writes:D1PreparedStatement[]=[];
  const legacyOverrides=Object.keys(snapshots).length?await db().prepare("SELECT task_id,variant_index,variant_json FROM teacher_task_variants ORDER BY variant_index").all<{task_id:string;variant_index:number;variant_json:string}>():null;
  for(const task of tasks){
    const existing=assignments.get(task.id);
    if(existing){result.push(existing);continue;}
    let assigned:Task=variantForLearner(task,position?.ordinal||0,pools[task.id]||[]);
    // Preserve the exact old project schema for learners who already have a snapshot.
    if(task.project&&snapshots[task.project]){
      const peer=[...assignments.values()].find(item=>item&&item.project===task.project&&item.variantIndex);
      if(peer?.variantIndex){assigned={...(peer.variantIndex===1?task:pools[task.id]?.[peer.variantIndex-2]||task),variantIndex:peer.variantIndex};}
      else{
        const seed=task.seedProfile?datasets[task.seedProfile]:task.seed;
        const alternatives=legacyVariants(task,seed);
        for(const row of legacyOverrides?.results.filter(item=>item.task_id===task.id)||[]){const value=safeJson<unknown>(row.variant_json,null);if(row.variant_index>=1&&row.variant_index<=4&&validTask(value))alternatives[row.variant_index-1]=value;}
        assigned=legacyAssignment(task,learner.id,alternatives);
      }
    }
    result.push(assigned);
    writes.push(db().prepare("INSERT OR IGNORE INTO learner_task_variants (learner_id,task_id,task_json,assigned_at) VALUES (?,?,?,?)").bind(learner.id,task.id,JSON.stringify(assigned),Date.now()));
  }
  if(writes.length){
    await db().batch(writes);
    // Concurrent first reads must both return the stored winner.
    const fresh=await db().prepare("SELECT task_id,task_json FROM learner_task_variants WHERE learner_id=?").bind(learner.id).all<{task_id:string;task_json:string}>();
    const stored=new Map(fresh.results.map(row=>[row.task_id,safeJson<Task|null>(row.task_json,null)]));
    return result.map(task=>stored.get(task.id)||task);
  }
  return result;
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
      const pools = await taskVariantPools(tasks);
      return row ? json({role:"student",...progress(row),tasks:await learnerTasks(tasks,row,pools)}) : json({role:"guest",tasks});
    }
    const groups = await db().prepare("SELECT id,name,code,created_at FROM groups ORDER BY created_at DESC").all();
    const students = await db().prepare("SELECT id,name,group_id,completed_json,updated_at FROM learners ORDER BY updated_at DESC").all();
    const errors = await db().prepare("SELECT task_id,message,COUNT(*) AS count FROM attempts GROUP BY task_id,message ORDER BY count DESC LIMIT 20").all();
    const taskStats = await db().prepare("SELECT s.task_id,s.learner_id,l.name,l.group_id,g.name AS group_name,s.started_at,s.completed_at,s.seconds_spent,s.check_count,s.error_count,s.last_activity_at FROM learner_task_stats s JOIN learners l ON l.id=s.learner_id JOIN groups g ON g.id=l.group_id").all();
    return json({role:"admin",groups:groups.results,students:students.results.map(row=>({...row,completed:safeJson<string[]>(String(row.completed_json),[])})),errors:errors.results,tasks,variantPools:await taskVariantPools(tasks),taskStats:taskStats.results});
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
      const tasks = await taskCatalog();
      const response = json({role:"student",...progress(learner),tasks:await learnerTasks(tasks,learner,await taskVariantPools(tasks))}); response.headers.set("Set-Cookie",await newSession("student",row.id)); return response;
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
    if (action === "delete-group" && auth.role === "admin") {
      if (typeof body.groupId !== "string" || body.groupId.length < 1 || body.groupId.length > 100) return json({error:"Некорректный идентификатор группы"},400);
      const group = await db().prepare("SELECT id FROM groups WHERE id=?").bind(body.groupId).first<{id:string}>();
      if (!group) return json({error:"Группа не найдена"},404);
      await db().batch([
        db().prepare("DELETE FROM sessions WHERE learner_id IN (SELECT id FROM learners WHERE group_id=?)").bind(group.id),
        db().prepare("DELETE FROM attempts WHERE learner_id IN (SELECT id FROM learners WHERE group_id=?)").bind(group.id),
        db().prepare("DELETE FROM learner_task_stats WHERE learner_id IN (SELECT id FROM learners WHERE group_id=?)").bind(group.id),
        db().prepare("DELETE FROM learner_task_variants WHERE learner_id IN (SELECT id FROM learners WHERE group_id=?)").bind(group.id),
        db().prepare("DELETE FROM learners WHERE group_id=?").bind(group.id),
        db().prepare("DELETE FROM groups WHERE id=?").bind(group.id),
      ]);
      return json({ok:true});
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
      const tasks=await taskCatalog();
      return json({ok:true,tasks,variantPools:await taskVariantPools(tasks)});
    }
    if (auth.role === "admin" && action === "save-variant") {
      if (typeof body.taskId !== "string" || !validTask(body.task) || body.task.id !== body.taskId || !Number.isInteger(body.variantIndex) || Number(body.variantIndex)<1 || Number(body.variantIndex)>3) return json({error:"Проверьте параметры альтернативного задания"},400);
      const base = (await taskCatalog()).find(task=>task.id===body.taskId);
      if (!base || base.variantEligible===false) return json({error:"Для этого задания альтернативы отключены"},409);
      const variant = body.task as Task;
      if (variant.mode!==base.mode || variant.project!==base.project) return json({error:"Вариант должен сохранять тип задания и сквозной проект"},400);
      await db().prepare("INSERT INTO teacher_task_variants (task_id,variant_index,variant_json,updated_at) VALUES (?,?,?,?) ON CONFLICT(task_id,variant_index) DO UPDATE SET variant_json=excluded.variant_json,updated_at=excluded.updated_at").bind(base.id,Number(body.variantIndex),JSON.stringify(variant),Date.now()).run();
      return json({ok:true,variantPools:await taskVariantPools(await taskCatalog())});
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
    if (["task-start","task-time","task-check"].includes(String(action))) {
      if (typeof body.taskId!=="string" || !activeTaskIds.has(body.taskId)) return json({error:"Некорректное задание"},400);
      const now=Date.now();
      if (action==="task-start") {
        await db().prepare("INSERT INTO learner_task_stats (learner_id,task_id,started_at,last_activity_at) VALUES (?,?,?,?) ON CONFLICT(learner_id,task_id) DO UPDATE SET last_activity_at=excluded.last_activity_at").bind(learner.id,body.taskId,now,now).run();
      } else if (action==="task-time") {
        const seconds=typeof body.seconds==="number"&&Number.isFinite(body.seconds)?Math.max(0,Math.min(60,Math.floor(body.seconds))):0;
        if(seconds>0) await db().prepare("INSERT INTO learner_task_stats (learner_id,task_id,started_at,seconds_spent,last_activity_at) VALUES (?,?,?,?,?) ON CONFLICT(learner_id,task_id) DO UPDATE SET seconds_spent=seconds_spent+excluded.seconds_spent,last_activity_at=excluded.last_activity_at").bind(learner.id,body.taskId,now,seconds,now).run();
      } else {
        const correct=body.correct===true;
        await db().prepare("INSERT INTO learner_task_stats (learner_id,task_id,started_at,completed_at,check_count,error_count,last_activity_at) VALUES (?,?,?,?,1,?,?) ON CONFLICT(learner_id,task_id) DO UPDATE SET completed_at=CASE WHEN ?=1 THEN COALESCE(completed_at,excluded.completed_at) ELSE completed_at END,check_count=check_count+1,error_count=error_count+excluded.error_count,last_activity_at=excluded.last_activity_at").bind(learner.id,body.taskId,now,correct?now:null,correct?0:1,now,correct?1:0).run();
      }
      return json({ok:true});
    }
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
