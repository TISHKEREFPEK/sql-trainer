import { env } from "cloudflare:workers";
import { allTasks } from "../../../lib/course";

export const runtime = "edge";
type Session = { role: "admin" | "student"; learnerId: string | null };
type Learner = { id:string; name:string; group_id:string; completed_json:string; snapshots_json:string };
const taskIds = new Set(allTasks.map(task => task.id));
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
function validName(input:unknown,max=60):input is string { return typeof input === "string" && input.trim().length >= 2 && input.trim().length <= max; }
async function learnerData(id:string) {
  return db().prepare("SELECT id,name,group_id,completed_json,snapshots_json FROM learners WHERE id=?").bind(id).first<Learner>();
}
function progress(row:Learner) { return {name:row.name,completed:safeJson<string[]>(row.completed_json,[]),snapshots:safeJson<Record<string,string>>(row.snapshots_json,{})}; }

export async function GET(request:Request) {
  try {
    const auth = await session(request);
    if (!auth) return json({role:"guest"});
    if (auth.role === "student" && auth.learnerId) {
      const row = await learnerData(auth.learnerId);
      return row ? json({role:"student",...progress(row)}) : json({role:"guest"});
    }
    const groups = await db().prepare("SELECT id,name,code,created_at FROM groups ORDER BY created_at DESC").all();
    const students = await db().prepare("SELECT id,name,group_id,completed_json,updated_at FROM learners ORDER BY updated_at DESC").all();
    const errors = await db().prepare("SELECT task_id,message,COUNT(*) AS count FROM attempts GROUP BY task_id,message ORDER BY count DESC LIMIT 20").all();
    return json({role:"admin",groups:groups.results,students:students.results.map(row=>({...row,completed:safeJson<string[]>(String(row.completed_json),[])})),errors:errors.results});
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
      const response = json({role:"admin"}); response.headers.set("Set-Cookie",await newSession("admin",null)); return response;
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
      const response = json({role:"student",...progress(learner)}); response.headers.set("Set-Cookie",await newSession("student",row.id)); return response;
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
    if (auth.role !== "student" || !auth.learnerId) return json({error:"Нет доступа"},403);
    const learner = await learnerData(auth.learnerId);
    if (!learner) return json({error:"Профиль не найден"},404);
    if (action === "progress") {
      const completed = new Set(safeJson<string[]>(learner.completed_json,[]).filter(x=>taskIds.has(x)));
      if (Array.isArray(body.completed)) for (const id of body.completed) if (typeof id === "string" && taskIds.has(id)) completed.add(id);
      const snapshots = safeJson<Record<string,string>>(learner.snapshots_json,{});
      if (typeof body.project === "string" && projectIds.has(body.project) && typeof body.snapshot === "string" && body.snapshot.length < 250000) snapshots[body.project] = body.snapshot;
      await db().prepare("UPDATE learners SET completed_json=?,snapshots_json=?,updated_at=? WHERE id=?").bind(JSON.stringify([...completed]),JSON.stringify(snapshots),Date.now(),learner.id).run();
      return json({ok:true,completed:[...completed],snapshots});
    }
    if (action === "attempt") {
      if (typeof body.taskId !== "string" || !taskIds.has(body.taskId) || typeof body.message !== "string") return json({error:"Некорректная попытка"},400);
      await db().prepare("INSERT INTO attempts (id,learner_id,task_id,message,created_at) VALUES (?,?,?,?,?)").bind(crypto.randomUUID(),learner.id,body.taskId,body.message.slice(0,120),Date.now()).run();
      return json({ok:true});
    }
    return json({error:"Неизвестное действие"},400);
  } catch { return json({error:"Не удалось сохранить данные. Попробуйте ещё раз."},503); }
}
