import { parentPort, workerData } from 'node:worker_threads';
import path from 'node:path';
import initSqlJs from 'sql.js';
import { variantSql, prepareVariantDatabase } from '../../lib/task-variants';
import type { Database } from 'sql.js';
const { task, snapshot, code, check } = workerData;
const quoted = (name: string) => `"${name.replaceAll('"', '""')}"`;
function state(db: Database) {
  const names = db.exec("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")[0]?.values || [];
  return JSON.stringify({ tables: names.map(([raw]) => {
    const name = String(raw), q = quoted(name);
    return { name, columns: db.exec(`PRAGMA table_info(${q})`)[0]?.values,
      indexes: db.exec(`PRAGMA index_list(${q})`)[0]?.values.map(row => [row[1], row[2]]),
      foreignKeys: db.exec(`PRAGMA foreign_key_list(${q})`)[0]?.values,
      rows: db.exec(`SELECT * FROM ${q}`)[0]?.values.map(row => JSON.stringify(row)).sort() || [] };
  }), objects: db.exec("SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('view','trigger') ORDER BY type,name")[0]?.values || [] });
}
const normalize = (res: any, ordered: boolean) => JSON.stringify({columns: res.columns, rows: ordered ? res.values.map((row: any) => JSON.stringify(row)) : res.values.map((row: any) => JSON.stringify(row)).sort()});
(async () => {
  const SQL = await initSqlJs({ locateFile: () => path.join(__dirname, 'sql-wasm.wasm') });
  const make = () => {
    const db = snapshot ? new SQL.Database(Buffer.from(snapshot, 'base64')) : new SQL.Database();
    if (!snapshot) { db.run(variantSql(task.seed, task.tableMap)); prepareVariantDatabase(db, task); }
    db.run('PRAGMA foreign_keys=ON'); return db;
  };
  const actual = make(), expected = make();
  try {
    if (code && task.mode !== 'state' && !/^\s*(SELECT|WITH)\b/i.test(code)) throw new Error('Для этого задания нужен запрос SELECT или WITH.');
    const output = code ? actual.exec(code).at(-1) || {columns: [], values: []} : {columns: [], values: []};
    let correct = false;
    if (check) {
      const target = expected.exec(task.solution).at(-1) || {columns: [], values: []};
      correct = task.mode === 'state' ? state(actual) === state(expected) : normalize(output, !!task.ordered) === normalize(target, !!task.ordered);
      if (task.id === 'transaction') correct = correct && /^\s*BEGIN\b/i.test(code) && /\bCOMMIT\s*;?\s*$/i.test(code.trim());
      if (task.id === 'constraint') {
        const probe = new SQL.Database(actual.export()), table = quoted(task.tableMap?.tickets || 'tickets');
        const rejects = (sql: string) => { try { probe.run(sql); return false; } catch { return true; } };
        try { probe.run(`INSERT INTO ${table}(id,code,price) VALUES(99,'probe',10)`); } catch { correct = false; }
        correct = correct && rejects(`INSERT INTO ${table} VALUES(100,'probe',10)`) && rejects(`INSERT INTO ${table} VALUES(101,'negative',-1)`) && rejects(`INSERT INTO ${table} VALUES(102,NULL,10)`);
        probe.close();
      }
    }
    const tables = (actual.exec("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")[0]?.values || []).map(([raw]) => {
      const name = String(raw), q = quoted(name);
      return {name, columns: actual.exec(`PRAGMA table_info(${q})`)[0]?.values || [], rows: actual.exec(`SELECT * FROM ${q} LIMIT 8`)[0] || {columns: [], values: []}};
    });
    parentPort!.postMessage({ correct, output: {columns: output.columns, values: output.values.slice(0, 200)}, tables,
      snapshot: correct && task.mode === 'state' && task.project ? Buffer.from(actual.export()).toString('base64') : undefined });
  } finally { actual.close(); expected.close(); }
})().catch(error => parentPort!.postMessage({error: String(error.message || error)}));
