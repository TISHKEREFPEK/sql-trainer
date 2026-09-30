import { build } from 'esbuild';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
process.chdir(path.dirname(fileURLToPath(import.meta.url))); 
import { mkdir, copyFile, cp, rm } from 'node:fs/promises';
await rm('build', { recursive: true, force: true });
await mkdir('build', { recursive: true });
for (const name of ['server', 'sql-worker']) {
  await build({ absWorkingDir: path.dirname(fileURLToPath(import.meta.url)), entryPoints: [`src/${name}.ts`], bundle: true, platform: 'node', format: 'cjs', target: 'node22', outfile: `build/${name}.cjs`, external: name === 'server' ? ['sql.js'] : [] });
}
await cp('ui', 'build/ui', { recursive: true });
await copyFile('src/main.cjs', 'build/main.cjs');
await copyFile('src/preload.cjs', 'build/preload.cjs');
await copyFile('node_modules/sql.js/dist/sql-wasm.wasm', 'build/sql-wasm.wasm');
console.log('Desktop build ready. Student packaging excludes server and course.');
