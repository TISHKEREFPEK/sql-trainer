module.exports = {
  appId: 'ru.sqlspace.teacher', productName: 'SQL Teacher',
  directories: { output: 'release/teacher' },
  files: ['build/**', 'package.json'], asarUnpack: ['build/sql-wasm.wasm', 'build/sql-worker.cjs'],
  extraMetadata: { classroomRole: 'teacher' },
  win: { target: 'nsis' }, nsis: { oneClick: false, perMachine: false, allowToChangeInstallationDirectory: true },
  artifactName: 'SQL-Teacher-${version}-${arch}.${ext}'
};
