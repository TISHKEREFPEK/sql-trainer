module.exports = {
  appId: 'ru.sqlspace.student', productName: 'SQL Student',
  directories: { output: 'release/student' },
  files: ['build/main.cjs', 'build/preload.cjs', 'build/ui/**', 'package.json', '!node_modules/**'],
  extraMetadata: { classroomRole: 'student', dependencies: {} },
  win: { target: 'nsis' }, nsis: { oneClick: false, perMachine: false, allowToChangeInstallationDirectory: true },
  artifactName: 'SQL-Student-${version}-${arch}.${ext}'
};
