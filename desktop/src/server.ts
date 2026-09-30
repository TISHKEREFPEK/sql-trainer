import http from 'node:http';
import { randomBytes, scryptSync, timingSafeEqual } from 'node:crypto';
import { readFileSync, writeFileSync, existsSync, mkdirSync, renameSync } from 'node:fs';
import path from 'node:path';
import { Worker } from 'node:worker_threads';
import initSqlJs from 'sql.js';
import { allTasks, assign, publicTask, glossaryLong, sourceSeed } from './catalog';
import type { Task } from '../../lib/course';

const token = () => randomBytes(24).toString('hex');
function hash(password: string, salt = token()) { return `${salt}:${scryptSync(password, salt, 32).toString('hex')}`; }
function verify(password: string, saved: string) { const [salt, value] = saved.split(':'); return timingSafeEqual(Buffer.from(value, 'hex'), scryptSync(password, salt, 32)); }
function fail(message: string, status = 400): never { throw Object.assign(new Error(message), { status }); }
const bounded = (value: unknown, max: number) => typeof value === 'string' && value.trim().length > 0 && value.length <= max ? value : fail('Проверьте заполнение полей.');

export async function startServer(options: { port?: number; dataDir?: string } = {}) {
  const dir = options.dataDir || process.env.SQL_CLASSROOM_DATA || path.join(process.cwd(), '.classroom-data');
  mkdirSync(dir, { recursive: true });
  const file = path.join(dir, 'classroom.sqlite');
  const SQL = await initSqlJs({ locateFile: () => path.join(__dirname, 'sql-wasm.wasm') });
  const db = existsSync(file) ? new SQL.Database(readFileSync(file)) : new SQL.Database();
  db.run('CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), value TEXT NOT NULL)');
  const saved = db.exec('SELECT value FROM state WHERE id=1')[0]?.values[0]?.[0];
  let state: any = saved ? JSON.parse(String(saved)) : { teacherHash: null, students: [], overrides: {}, policies: {}, revision: 1, attempts: [] };
  function persist() {
    db.run('INSERT OR REPLACE INTO state(id,value) VALUES(1,?)', [JSON.stringify(state)]);
    writeFileSync(file + '.tmp', db.export(), { mode: 0o600 }); renameSync(file + '.tmp', file);
  }
  const sessions = new Map<string, {role: 'teacher' | 'student'; id?: string; expires: number}>();
  const attempts = new Map<string, {count: number; reset: number}>();
  let activeWorkers = 0;
  const busy = new Set<string>();
  function catalog(): Task[] { return allTasks.map(task => state.overrides[task.id] || task); }
  function assignment(student: any, taskId: string) {
    const task = catalog().find(item => item.id === taskId) || fail('Задание не найдено.', 404);
    if (!student.assigned[taskId]) {
      // Project chain is pinned together before the first step, including task content.
      const chain = task.project ? catalog().filter(item => item.project === task.project) : [task];
      for (const item of chain) student.assigned[item.id] ||= { task: assign(item, student.slot), revision: state.revision };
      persist();
    }
    return student.assigned[taskId];
  }
  const visible = (student: any, taskId: string) => {
    const item = assignment(student, taskId);
    return publicTask(item.task, item.revision, !!state.policies[taskId]);
  };
  function evaluate(task: Task, snapshot: string | undefined, code: string, check: boolean): Promise<any> {
    if (activeWorkers >= 4) fail('Проверка занята. Повторите через несколько секунд.', 429);
    activeWorkers++;
    return new Promise((resolve, reject) => {
      const worker = new Worker(path.join(__dirname, 'sql-worker.cjs'), {workerData: {task, snapshot, code, check}, resourceLimits: {maxOldGenerationSizeMb: 128}});
      let settled = false;
      const finish = (error?: Error, value?: any) => {
        if (settled) return; settled = true; clearTimeout(timer); activeWorkers--; void worker.terminate();
        error ? reject(error) : resolve(value);
      };
      const timer = setTimeout(() => finish(new Error('Запрос превысил допустимое время выполнения.')), 5000);
      worker.once('message', value => finish(undefined, value));
      worker.once('error', error => finish(error));
      worker.once('exit', () => finish(new Error('Проверка SQL была остановлена.')));
    });
  }
  async function body(req: http.IncomingMessage) {
    if (req.headers['content-type'] !== 'application/json') fail('Нужен JSON.', 415);
    let text = '';
    for await (const chunk of req) { text += chunk; if (text.length > 1_000_000) fail('Слишком большой запрос.', 413); }
    try { return JSON.parse(text); } catch { fail('Некорректный JSON.'); }
  }
  function limited(req: http.IncomingMessage) {
    const key = req.socket.remoteAddress || 'local', now = Date.now();
    let entry = attempts.get(key);
    if (!entry || entry.reset < now) { entry = {count: 0, reset: now + 60_000}; attempts.set(key, entry); }
    if (++entry.count > 15) fail('Слишком много попыток входа. Подождите минуту.', 429);
  }
  const server = http.createServer(async (req, res) => {
    res.setHeader('Cache-Control', 'no-store'); res.setHeader('X-Content-Type-Options', 'nosniff');
    const respond = (value: any, status = 200) => { res.writeHead(status, {'Content-Type': 'application/json; charset=utf-8'}); res.end(JSON.stringify(value)); };
    try {
      // Test server deliberately listens on loopback only; LAN requires TLS/pairing first.
      if (req.headers.origin && req.headers.origin !== `http://${req.headers.host}`) fail('Запрос с другого сайта запрещён.', 403);
      const pathname = new URL(req.url || '/', 'http://localhost').pathname;
      if (!pathname.startsWith('/api/')) {
        const assets: Record<string, string> = {'/': 'index.html', '/app.js': 'app.js', '/style.css': 'style.css'};
        const asset = assets[pathname]; if (!asset) fail('Страница не найдена.', 404);
        res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'");
        res.writeHead(200, {'Content-Type': asset.endsWith('.js') ? 'text/javascript' : asset.endsWith('.css') ? 'text/css' : 'text/html; charset=utf-8'});
        res.end(readFileSync(path.join(__dirname, 'ui', asset))); return;
      }
      if (pathname === '/api/status' && req.method === 'GET') return respond({ setup: !state.teacherHash, revision: state.revision, localOnly: true });
      const input = req.method === 'POST' ? await body(req) : {};
      if (pathname === '/api/setup' && req.method === 'POST') {
        limited(req); if (state.teacherHash) fail('Первичная настройка уже завершена.', 409);
        const password = bounded(input.password, 128); if (password.length < 10) fail('Пароль преподавателя: минимум 10 символов.');
        state.teacherHash = hash(password); persist(); return respond({ok: true});
      }
      if (pathname === '/api/login' && req.method === 'POST') {
        limited(req); const password = bounded(input.password, 128);
        const student = state.students.find((item: any) => item.login === input.login);
        const role = input.role === 'teacher' ? 'teacher' : 'student';
        const credential = role === 'teacher' ? state.teacherHash : student?.passwordHash;
        if (!credential || !verify(password, credential)) fail('Неверный логин или пароль.', 401);
        const session = token(); sessions.set(session, {role, id: role === 'student' ? student.id : undefined, expires: Date.now() + 8 * 3600_000});
        return respond({token: session, role});
      }
      const bearer = (req.headers.authorization || '').replace(/^Bearer /, '');
      const session = sessions.get(bearer); if (!session || session.expires < Date.now()) fail('Войдите в приложение.', 401);
      if (pathname === '/api/logout' && req.method === 'POST') { sessions.delete(bearer); return respond({ok: true}); }
      if (pathname.startsWith('/api/teacher/')) {
        if (session.role !== 'teacher') fail('Доступ только преподавателю.', 403);
        if (pathname === '/api/teacher/overview' && req.method === 'GET') return respond({revision: state.revision,
          tasks: catalog().map(task => ({...task, seed: sourceSeed(task)})), policies: state.policies, students: state.students.map(({id, login, completed, lockedUntil}: any) => ({id, login, completed, lockedUntil})), attempts: state.attempts.slice(-100)});
        if (pathname === '/api/teacher/student' && req.method === 'POST') {
          const login = bounded(input.login, 40).trim(), password = bounded(input.password, 128);
          if (password.length < 8) fail('Пароль ученика: минимум 8 символов.');
          if (state.students.some((item: any) => item.login === login)) fail('Такой логин уже существует.', 409);
          state.students.push({ id: token(), login, passwordHash: hash(password), slot: state.students.length,
            assigned: {}, completed: [], snapshots: {}, lockedUntil: 0 }); persist(); return respond({ok: true});
        }
        if (pathname === '/api/teacher/task' && req.method === 'POST') {
          const previous = catalog().find(task => task.id === input.id) || fail('Задание не найдено.', 404);
          const task = { ...previous, prompt: bounded(input.prompt, 12000), concept: bounded(input.concept, 12000),
            seed: typeof input.seed === 'string' && input.seed.length <= 500000 ? input.seed : fail('Некорректная база.'),
            solution: bounded(input.solution, 12000), seedProfile: undefined };
          const seedCheck = await evaluate(task, undefined, task.solution, true);
          if (seedCheck.error || !seedCheck.correct) fail('Эталонное решение не прошло проверку: ' + (seedCheck.error || 'проверьте ограничения'));
          state.overrides[task.id] = task; state.policies[task.id] = input.restricted === true;
          state.revision++; persist(); return respond({ok: true, revision: state.revision});
        }
        if (pathname === '/api/teacher/unlock' && req.method === 'POST') {
          const student = state.students.find((item: any) => item.id === input.id) || fail('Ученик не найден.', 404);
          student.lockedUntil = 0; persist(); return respond({ok: true});
        }
        fail('Действие не найдено.', 404);
      }
      if (session.role !== 'student') fail('Нужен вход ученика.', 403);
      const student = state.students.find((item: any) => item.id === session.id) || fail('Профиль не найден.', 401);
      if (pathname === '/api/catalog' && req.method === 'GET') return respond({revision: state.revision, completed: student.completed,
        lockedUntil: student.lockedUntil, tasks: catalog().map(task => ({id: task.id, title: task.title, module: task.module, project: task.project}))});
      if (pathname === '/api/glossary' && req.method === 'GET') return respond(glossaryLong);
      if (req.method !== 'POST') fail('Действие не найдено.', 404);
      const taskId = bounded(input.taskId, 100), item = assignment(student, taskId);
      if (pathname === '/api/task') return respond({task: visible(student, taskId), lockedUntil: student.lockedUntil});
      if (pathname === '/api/violation') {
        if (state.policies[taskId]) {
          student.lockedUntil = Math.max(student.lockedUntil, Date.now() + 30_000);
          state.attempts.push({student: student.login, taskId, kind: 'clipboard', at: Date.now()});
          state.attempts = state.attempts.slice(-1000); persist();
        }
        return respond({lockedUntil: student.lockedUntil});
      }
      if (pathname === '/api/execute') {
        if (student.lockedUntil > Date.now()) fail('Работа приостановлена на 30 секунд.', 423);
        if (busy.has(student.id)) fail('Предыдущий запрос ещё выполняется.', 429);
        const task = item.task;
        if (task.project) {
          const chain = catalog().filter(candidate => candidate.project === task.project);
          const index = chain.findIndex(candidate => candidate.id === taskId);
          if (chain.slice(0, index).some(candidate => !student.completed.includes(candidate.id))) fail('Сначала завершите предыдущий шаг проекта.', 409);
        }
        if (input.check && task.project && student.completed.includes(taskId)) fail('Шаг проекта уже сохранён. Повторная проверка не изменяет проект.', 409);
        const code = typeof input.code === 'string' && input.code.length <= 12000 ? input.code : fail('SQL слишком длинный.');
        busy.add(student.id);
        try {
          const result = await evaluate(task, task.project ? student.snapshots[task.project] : undefined, code, input.check === true);
          if (input.check && result.correct) {
            if (!student.completed.includes(taskId)) student.completed.push(taskId);
            if (result.snapshot) student.snapshots[task.project] = result.snapshot;
          }
          // Never accept a completed flag or database snapshot supplied by the client.
          if (input.check) { state.attempts.push({student: student.login, taskId, kind: result.error || (result.correct ? 'correct' : 'different'), at: Date.now()}); state.attempts = state.attempts.slice(-1000); persist(); }
          const {snapshot: _privateSnapshot, ...response} = result; return respond(response);
        } finally { busy.delete(student.id); }
      }
      fail('Действие не найдено.', 404);
    } catch (error: any) { respond({error: error.status ? error.message : 'Не удалось выполнить действие.'}, error.status || 500); }
  });
  await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(options.port ?? 47831, '127.0.0.1', resolve); });
  return { server, port: (server.address() as any).port, close: () => new Promise<void>(resolve => server.close(() => { db.close(); resolve(); })) };
}
if (require.main === module) startServer().then(({port}) => console.log(`SQL Classroom test server: http://127.0.0.1:${port}`)).catch(error => {console.error(error.message); process.exitCode = 1;});
