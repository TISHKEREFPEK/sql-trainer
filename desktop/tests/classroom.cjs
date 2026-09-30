const {test} = require('node:test');
const assert = require('node:assert/strict');
const {mkdtempSync, rmSync} = require('node:fs');
const {tmpdir} = require('node:os');
const path = require('node:path');
const {startServer} = require('../build/server.cjs');

test('roles, assignment, grading, revision, restrictions and restart', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'sql-classroom-'));
  let host = await startServer({port:0, dataDir:dir});
  const call = async (route, body, token, origin) => {
    const response = await fetch(`http://127.0.0.1:${host.port}${route}`, {method: body === undefined ? 'GET' : 'POST', headers: {'Content-Type':'application/json', ...(token ? {Authorization:`Bearer ${token}`} : {}), ...(origin ? {Origin:origin} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    return {status:response.status, ...await response.json()};
  };
  try {
    assert.equal((await call('/api/setup', {password:'teacher-test-2026'}, null, 'https://evil.example')).status, 403);
    assert.equal((await call('/api/setup', {password:'teacher-test-2026'})).status, 200);
    assert.equal((await call('/api/setup', {password:'replacement-password'})).status, 409);
    let admin = (await call('/api/login', {role:'teacher', password:'teacher-test-2026'})).token;
    await call('/api/teacher/student', {login:'student-one', password:'student-test-2026'}, admin);
    await call('/api/teacher/student', {login:'student-two', password:'student-test-2026'}, admin);
    let student = (await call('/api/login', {role:'student', login:'student-one', password:'student-test-2026'})).token;
    assert.equal((await call('/api/teacher/overview', undefined, student)).status, 403);
    assert.equal((await call('/api/login', {role:'student', login:'student-one', password:'wrong-password'})).status, 401);
    const tasks = (await call('/api/teacher/overview', undefined, admin)).tasks;
    assert.equal(tasks.length, 120);
    const first = tasks[0];
    const assignment = (await call('/api/task', {taskId:first.id}, student)).task;
    for (const secret of ['solution', 'seed', 'tableMap']) assert.equal(secret in assignment, false);
    const malformed = await call('/api/execute', {taskId:first.id, code:'SELECT * FRM students;', check:true}, student);
    assert.match(malformed.error, /syntax error/);
    const wrong = await call('/api/execute', {taskId:first.id, code:'SELECT name FROM students;', check:true, completed:true}, student);
    assert.equal(wrong.correct, false);
    const right = await call('/api/execute', {taskId:first.id, code:first.solution, check:true}, student);
    assert.equal(right.correct, true);
    assert.equal('snapshot' in right, false);
    const edited = await call('/api/teacher/task', {...first, prompt:'Обновлённое условие', restricted:true}, admin);
    assert.equal(edited.status, 200);
    const pinned = (await call('/api/task', {taskId:first.id}, student)).task;
    assert.equal(pinned.prompt, assignment.prompt); assert.equal(pinned.restricted, true);
    const second = (await call('/api/login', {role:'student', login:'student-two', password:'student-test-2026'})).token;
    assert.equal((await call('/api/task', {taskId:first.id}, second)).task.prompt, 'Обновлённое условие');
    await call('/api/violation', {taskId:first.id}, student);
    assert.equal((await call('/api/execute', {taskId:first.id, code:first.solution, check:true}, student)).status, 423);
    const overview = await call('/api/teacher/overview', undefined, admin);
    await call('/api/teacher/unlock', {id:overview.students[0].id}, admin);
    assert.equal((await call('/api/execute', {taskId:first.id, code:first.solution, check:true}, student)).correct, true);
    const spinning = await call('/api/execute', {taskId:first.id, code:'WITH RECURSIVE x(a) AS (VALUES(1) UNION ALL SELECT a+1 FROM x) SELECT sum(a) FROM x;', check:false}, student);
    assert.ok(spinning.error); // worker is terminated without stopping teacher server
    assert.equal((await call('/api/status')).status, 200);
    await host.close(); host = await startServer({port:0, dataDir:dir});
    student = (await call('/api/login', {role:'student', login:'student-one', password:'student-test-2026'})).token;
    const restored = await call('/api/catalog', undefined, student);
    assert.ok(restored.completed.includes(first.id));
    assert.equal((await call('/api/task', {taskId:first.id}, student)).task.prompt, assignment.prompt);
  } finally {await host.close(); rmSync(dir, {recursive:true, force:true});}
});

test('server SQL worker accepts 120 course solutions and three project chains', async () => {
  const {Worker} = require('node:worker_threads');
  const fs = require('node:fs');
  const ts = require('../../node_modules/typescript');
  require.extensions['.ts'] = (module,file) => module._compile(ts.transpileModule(fs.readFileSync(file,'utf8'), {compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022,esModuleInterop:true}}).outputText,file);
  const {allTasks} = require('../../lib/course.ts');
  const {createTaskVariants,variantForLearner} = require('../../lib/task-variants.ts');
  const {parkingSpotsSeed,teacherDatasetSeeds} = require('../../lib/teacher-data.ts');
  const {parkingDatabaseSeed} = require('../../lib/parking-dataset.ts');
  const seeds = {...teacherDatasetSeeds,'parking-spots':parkingSpotsSeed,'parking-database':parkingDatabaseSeed};
  const snapshots = {};
  for (const base of allTasks) {
    const seed = base.seedProfile ? seeds[base.seedProfile] : base.seed;
    const task = variantForLearner(base, 1, createTaskVariants(base, seed));
    if (base.seedProfile) task.seed = seed;
    const result = await new Promise((resolve,reject) => {
      const worker = new Worker(path.resolve('build/sql-worker.cjs'), {workerData:{task,snapshot:task.project ? snapshots[task.project] : undefined, code:task.solution,check:true}});
      worker.once('message', value => {void worker.terminate(); resolve(value);}); worker.once('error',reject);
    });
    assert.equal(result.error, undefined, task.id); assert.equal(result.correct,true, task.id);
    if(result.snapshot) snapshots[task.project] = result.snapshot;
  }
  assert.equal(Object.keys(snapshots).length,3);
});
