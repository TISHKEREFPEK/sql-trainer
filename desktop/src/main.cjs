const {app, BrowserWindow, ipcMain, Menu} = require('electron');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
let win, host, browserSession, session = '', currentTask = '', restricted = false, lockUntil = 0;
const pkg = require('../package.json');
const teacher = pkg.classroomRole === 'teacher' || (!app.isPackaged && process.argv.includes('--teacher'));
let address = 'http://127.0.0.1:47831';
const publicRoutes = new Set(['/api/status', '/api/login', '/api/logout', '/api/theme', '/api/theme-presets']);
const studentRoutes = new Set(['/api/catalog', '/api/task', '/api/execute', '/api/glossary', '/api/violation', '/api/hint']);
const teacherRoutes = new Set(['/api/setup', '/api/teacher/overview', '/api/teacher/student', '/api/teacher/task', '/api/teacher/unlock']);
function trusted(event) { return win && event.sender === win.webContents && event.senderFrame?.url === pathToFileURL(path.join(__dirname, 'ui/index.html')).href; }
async function api(route, data) {
  const response = await fetch(address + route, {method: data === undefined ? 'GET' : 'POST',
    headers: {'Content-Type':'application/json', ...(session ? {Authorization: `Bearer ${session}`} : {})},
    body: data === undefined ? undefined : JSON.stringify(data), signal: AbortSignal.timeout(8000), redirect: 'error'});
  return response.json();
}
async function policy() {
  if (teacher || !session || !currentTask) return;
  try {
    const response = await api('/api/task', {taskId: currentTask});
    if (response.task) { restricted = response.task.restricted; lockUntil = response.lockedUntil || 0;
      if (process.platform === 'win32') win.setContentProtection(restricted);
      win.webContents.send('policy', {restricted, lockUntil}); }
  } catch { /* Offline execution is unavailable; preserve the draft in the renderer. */ }
}
async function violation() {
  if (!restricted || !currentTask || Date.now() < lockUntil) return;
  lockUntil = Date.now() + 30000;
  win.webContents.send('policy', {restricted, lockUntil});
  try { await api('/api/violation', {taskId: currentTask}); } catch {}
}
app.whenReady().then(async () => {
  Menu.setApplicationMenu(null);
  if (teacher) {
    host = await require('./server.cjs').startServer({dataDir: path.join(app.getPath('userData'), 'classroom'), port: 47831});
  }
  win = new BrowserWindow({width: 1280, height: 850, minWidth: 850, minHeight: 600, backgroundColor: '#f5f6fa',
    title: teacher ? 'SQL · Преподаватель' : 'SQL · Ученик',
    webPreferences: {partition: teacher ? 'persist:classroom-teacher' : 'persist:classroom-student', preload: path.join(__dirname, 'preload.cjs'), sandbox: true, contextIsolation: true, nodeIntegration: false, devTools: !app.isPackaged}});
  win.on('page-title-updated', event => event.preventDefault());
  browserSession = win.webContents.session;
  win.webContents.setWindowOpenHandler(() => ({action: 'deny'}));
  win.webContents.on('will-navigate', event => event.preventDefault());
  win.webContents.session.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
  win.webContents.on('before-input-event', (event, input) => {
    if (restricted && input.type === 'keyDown' && (((input.control || input.meta) && ['c','v','x'].includes(input.key.toLowerCase())) || (input.key === 'Insert' && (input.control || input.shift)) || (input.key === 'Delete' && input.shift))) {
      event.preventDefault(); void violation();
    }
  });
  ipcMain.handle('info', event => {if (!trusted(event)) throw new Error('Forbidden'); return {role: teacher ? 'teacher' : 'student', address, captureSupported: process.platform === 'win32'};});
  ipcMain.handle('connect', (event, next) => {
    if (!trusted(event) || teacher) throw new Error('Forbidden');
    const url = new URL(next);
    if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || url.username || url.password || url.pathname !== '/' || url.search || url.hash) throw new Error('Пока поддерживается только тест на одном компьютере: http://127.0.0.1:47831');
    address = url.origin; session = ''; currentTask = ''; restricted = false; return {ok: true};
  });
  ipcMain.handle('api', async (event, route, data) => {
    if (!trusted(event) || typeof route !== 'string' || !(publicRoutes.has(route) || (teacher ? teacherRoutes : studentRoutes).has(route))) throw new Error('Forbidden');
    if (route === '/api/login' && data?.role !== (teacher ? 'teacher' : 'student')) throw new Error('Неверная роль.');
    const response = await api(route, data);
    if (route === '/api/login' && response.token) {session = response.token; delete response.token;}
    if (route === '/api/logout') {session = ''; currentTask = ''; restricted = false; lockUntil = 0; win.setContentProtection(false);}
    if (route === '/api/task' && response.task) {currentTask = response.task.id; await policy();}
    return response;
  });
  ipcMain.on('clipboard-attempt', event => {if (trusted(event)) void violation();});
  await win.loadFile(path.join(__dirname, 'ui/index.html'));
  const timer = setInterval(() => void policy(), 2000); timer.unref();
}).catch(error => {require('electron').dialog.showErrorBox('Не удалось запустить класс', error.message); app.quit();});
app.on('window-all-closed', () => app.quit());
app.on('before-quit', () => {browserSession?.flushStorageData(); if (host) host.server.close();});
