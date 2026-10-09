// One-time authoring bridge. The installed .NET applications do not depend on Node.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const ts = require(path.join(root, 'node_modules/typescript'));
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022,esModuleInterop:true}}).outputText,file);
const {allTasks, glossary, glossaryLong} = require(path.join(root, 'lib/course.ts'));
const {sourceSeed, assign} = require(path.join(root, 'desktop/src/catalog.ts'));
const {createTaskVariants,variantSql} = require(path.join(root,'lib/task-variants.ts'));
const tasks = allTasks;
const seedProfiles = Object.fromEntries(allTasks.filter(t=>t.seedProfile).map(t=>[t.seedProfile,sourceSeed(t)]));
const fixtures = allTasks.map(task=>({id:task.id, alternatives:createTaskVariants(task,sourceSeed(task)).map(alt=>({...alt,seed:'',seedProfile:undefined,seedHash:crypto.createHash('sha256').update(variantSql(task.seedProfile?sourceSeed(task):alt.seed,alt.tableMap)).digest('hex')})), assigned:Array.from({length:4},(_,slot)=>({variantIndex:assign(task,slot).variantIndex}))}));
fs.writeFileSync(path.join(__dirname, '../src/Classroom.Domain/Data/catalog.json'), JSON.stringify({tasks,glossary,glossaryLong,seedProfiles},null,2));
fs.writeFileSync(path.join(__dirname, '../tests/Classroom.Tests/variant-fixtures.json'),JSON.stringify(fixtures));
console.log(`Exported ${tasks.length} tasks and their variant fixtures; no learner records exported.`);
