const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const ts = require('typescript');
require.extensions['.ts'] = (module, file) => {
  const output = ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true } }).outputText;
  module._compile(output, file);
};
const { allTasks } = require('../lib/course.ts');
const { createTaskVariants, variantSql, prepareVariantDatabase, assignedVariantIndex } = require('../lib/task-variants.ts');
const { parkingSpotsSeed, teacherDatasetSeeds } = require('../lib/teacher-data.ts');
const { parkingDatabaseSeed } = require('../lib/parking-dataset.ts');
const seeds = { ...teacherDatasetSeeds, 'parking-spots': parkingSpotsSeed, 'parking-database': parkingDatabaseSeed };
(async () => {
  const SQL = await require('sql.js')({ locateFile: () => path.resolve('public/sql-wasm.wasm') });
  const failures = []; let runs = 0; let varied = 0;
  for (const task of allTasks) {
    const seed = task.seedProfile ? seeds[task.seedProfile] : task.seed;
    const alternatives = createTaskVariants(task, seed);
    assert.equal(alternatives.length, task.variantEligible === false ? 0 : 3, task.id);
    if (alternatives.length) {
      varied++;
      assert.equal(new Set([task.solution, ...alternatives.map(item => item.solution)]).size, 4, `distinct SQL: ${task.id}`);
      assert.equal(new Set([0, 1, 2, 3].map(index => assignedVariantIndex(task, index))).size, 4);
    }
  }
  for (let slot = 1; slot <= 4; slot++) {
    const projects = new Map();
    for (const base of allTasks) {
      const seed = base.seedProfile ? seeds[base.seedProfile] : base.seed;
      const task = slot === 1 || base.variantEligible === false ? base : createTaskVariants(base, seed)[slot - 2];
      let db = task.project ? projects.get(task.project) : undefined;
      if (!db) {
        db = new SQL.Database();
        try { db.run(variantSql(seed, task.tableMap)); prepareVariantDatabase(db, task); }
        catch (error) { failures.push(`${base.id}.${slot} seed: ${error.message}`); db.close(); continue; }
        if (task.project) projects.set(task.project, db);
      }
      try { db.exec(task.solution); runs++; }
      catch (error) { failures.push(`${base.id}.${slot}: ${error.message}`); }
      if(slot>1 && base.variantEligible!==false && !base.project){
        const copyDb=new SQL.Database();
        try{
          copyDb.run(variantSql(seed,task.tableMap)); prepareVariantDatabase(copyDb,task);
          const copied=copyDb.exec(base.solution).at(-1);
          const schema="SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name";
          const expected=db.exec(task.mode==='state' ? schema : task.solution).at(-1);
          const actual=task.mode==='state' ? copyDb.exec(schema).at(-1) : copied;
          assert.notDeepEqual(actual,expected,`unchanged copied answer accepted: ${base.id}.${slot}`);
        }catch(error){if(error.code==='ERR_ASSERTION')failures.push(error.message);}
        finally{copyDb.close();}
      }
      if (!task.project) db.close();
    }
    for (const db of projects.values()) db.close();
  }
  assert.equal(variantSql("SELECT cars.id FROM cars WHERE brand='cars'; -- cars", {cars:'cars_v2'}), "SELECT cars_v2.id FROM cars_v2 WHERE brand='cars'; -- cars");
  for (const project of ['library','shop','classes']) {
    const tasks = allTasks.filter(task => task.project === project);
    for (let learner = 0; learner < 4; learner++) assert.equal(new Set(tasks.map(task => assignedVariantIndex(task, learner))).size, 1);
    // Import a partially completed guest project into each student namespace,
    // then finish it without losing the guest's rows or breaking foreign keys.
    for(let slot=2;slot<=4;slot++){
      const db=new SQL.Database();db.run(tasks[0].seed);db.run(tasks[0].solution);
      const first=createTaskVariants(tasks[0])[slot-2];
      const names=new Set(db.exec("SELECT name FROM sqlite_master WHERE type='table'")[0]?.values.map(row=>row[0]));
      for(const [from,to] of Object.entries(first.tableMap))if(names.has(from))db.run(`ALTER TABLE "${from}" RENAME TO "${to}"`);
      for(const step of tasks.slice(1))db.run(createTaskVariants(step)[slot-2].solution);
      db.close();
    }
  }
  if (failures.length) { console.error(failures.join('\n')); process.exitCode = 1; }
  else console.log(`OK: ${allTasks.length} tasks; ${varied} with 4 variants; ${runs} executable solutions; all project chains and round-robin slots verified.`);
})();
