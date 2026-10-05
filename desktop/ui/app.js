const root = document.querySelector('#root');
const escape = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
let auth = '', role = 'student', activeTask, overview, catalogue, restricted = false, lockedUntil = 0, currentPage = 0, selectedModule = '', syncing = false;
const bridge = window.classroom;
const localDraftKey = id => `classroom:draft:${role}:${document.querySelector('#connection').dataset.login || ''}:${id}`;
function draft(id) {try {return localStorage.getItem(localDraftKey(id));} catch {return null;}}
function saveDraft() {if (activeTask && document.querySelector('#sql')) try {localStorage.setItem(localDraftKey(activeTask.id), document.querySelector('#sql').value);} catch {}}
window.addEventListener('beforeunload', saveDraft);
function notice(message) {const node = document.querySelector('#notice'); node.textContent = message; node.classList.add('visible'); setTimeout(() => node.classList.remove('visible'), 5000);}
async function api(route, data) {
  const value = bridge ? await bridge.api(route, data) : await fetch(route, {method: data === undefined ? 'GET' : 'POST',headers: {'Content-Type':'application/json', ...(auth ? {Authorization:`Bearer ${auth}`} : {})},body: data === undefined ? undefined : JSON.stringify(data)}).then(res => res.json());
  if (value.error) throw new Error(value.error); return value;
}
function protect(value) {restricted = value.restricted; lockedUntil = value.lockUntil || value.lockedUntil || 0;}
bridge?.onPolicy(protect);
setInterval(() => {const remaining = Math.ceil((lockedUntil - Date.now())/1000); document.querySelector('#lock').hidden = remaining <= 0; document.querySelector('#seconds').textContent = Math.max(0, remaining);}, 250);
async function violation(event) {
  if (!restricted) return; event.preventDefault();
  if (Date.now() < lockedUntil) return;
  saveDraft(); lockedUntil = Date.now() + 30000;
  if (bridge) bridge.clipboardAttempt(); else try {protect(await api('/api/violation', {taskId: activeTask.id}));} catch(error) {notice(error.message);}
}
for (const event of ['copy','cut','paste']) document.addEventListener(event, violation);
document.addEventListener('drop', event => {if (restricted) {event.preventDefault(); void violation(event);}});
document.querySelector('#logout').onclick = async () => {
  saveDraft(); await api('/api/logout', {}); auth = ''; activeTask = null; restricted = false; lockedUntil = 0;
  document.querySelector('#logout').hidden = true; await loginScreen();
};

// Keep the select as the source of truth; use the same accessible picker in both roles.
function enhanceSelects() {
  for (const select of root.querySelectorAll('select')) {
    if (select.hidden) continue;
    select.hidden = true;
    const picker = document.createElement('details'); picker.className = 'picker';
    const summary = document.createElement('summary');
    const menu = document.createElement('div'); menu.className = 'picker-menu';
    const search = document.createElement('input'); search.type = 'search'; search.placeholder = 'Поиск'; search.setAttribute('aria-label', 'Поиск в списке');
    const options = document.createElement('div'); options.className = 'picker-options';
    const empty = document.createElement('div'); empty.className = 'picker-empty'; empty.textContent = 'Ничего не найдено'; empty.hidden = true;
    const buttons = [...select.options].map(option => {
      const button = document.createElement('button'); button.type = 'button'; button.textContent = option.textContent;
      button.setAttribute('aria-pressed', String(option.selected));
      button.onclick = () => {
        select.value = option.value; summary.textContent = option.textContent; picker.open = false;
        buttons.forEach(item => item.setAttribute('aria-pressed', String(item === button)));
        select.dispatchEvent(new Event('change', {bubbles:true})); summary.focus();
      };
      options.append(button); return button;
    });
    summary.textContent = select.selectedOptions[0]?.textContent || 'Выберите раздел';
    search.oninput = () => {
      const query = search.value.toLocaleLowerCase('ru');
      buttons.forEach(button => {button.hidden = !button.textContent.toLocaleLowerCase('ru').includes(query);});
      empty.hidden = buttons.some(button => !button.hidden);
    };
    picker.addEventListener('keydown', event => {
      if (event.key === 'Escape') {picker.open = false; summary.focus(); event.preventDefault();}
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault(); picker.open = true;
        const visible = buttons.filter(button => !button.hidden), current = visible.indexOf(document.activeElement);
        visible[(current + (event.key === 'ArrowDown' ? 1 : -1) + visible.length) % visible.length]?.focus();
      }
    });
    picker.addEventListener('toggle', () => {if (picker.open) {search.value = ''; buttons.forEach(button => {button.hidden = false;}); empty.hidden = true; root.querySelectorAll('.picker').forEach(other => {if (other !== picker) other.open = false;});}});
    menu.append(search, options, empty); picker.append(summary, menu); select.after(picker);
  }
}
document.addEventListener('click', event => root.querySelectorAll('.picker[open]').forEach(picker => {if (!picker.contains(event.target)) picker.open = false;}));

async function loginScreen() {
  root.dataset.screen = 'login';
  try {
    const status = await api('/api/status');
    const setup = role === 'teacher' && status.setup;
    root.innerHTML = `<section class="card login"><h1>${setup ? 'Настройка преподавателя' : role === 'teacher' ? 'Вход преподавателя' : 'Вход ученика'}</h1><p class="muted">${setup ? 'Задайте пароль. Он хранится на этом компьютере в виде защищённого хеша.' : 'Тестовая версия для работы на одном компьютере.'}</p><form id="login-form">${role === 'student' ? '<label>Логин<input name="login" required maxlength="40" autocomplete="username"></label>' : ''}<label>Пароль<input name="password" type="password" required minlength="${setup ? 10 : 1}" autocomplete="${setup ? 'new-password' : 'current-password'}"></label><button class="primary">${setup ? 'Сохранить и войти' : 'Войти'}</button></form>${!bridge ? '<p class="muted">Проверка в браузере: <button id="switch-role">Сменить роль</button></p>' : ''}</section>`;
    document.querySelector('#switch-role')?.addEventListener('click', () => {role = role === 'teacher' ? 'student' : 'teacher'; void loginScreen();});
    document.querySelector('#login-form').onsubmit = async event => {
      event.preventDefault(); const form = new FormData(event.currentTarget), password = form.get('password'), login = form.get('login');
      try {
        if (setup) await api('/api/setup', {password});
        const response = await api('/api/login', {role, login, password}); auth = response.token || 'desktop-session';
        document.querySelector('#connection').dataset.login = login || 'teacher'; document.querySelector('#logout').hidden = false;
        await (role === 'teacher' ? teacherScreen() : studentScreen());
      } catch(error) {notice(error.message);}
    };
  } catch(error) {root.innerHTML = '<section class="card login"><h1>Преподаватель недоступен</h1><p>Запустите преподавательское приложение на этом компьютере.</p><button id="retry">Повторить</button></section>'; document.querySelector('#retry').onclick = loginScreen; notice(error.message);}
}
async function teacherScreen() {
  root.dataset.screen = 'teacher';
  const selectedId = document.querySelector('#teacher-task')?.value;
  overview = await api('/api/teacher/overview');
  root.innerHTML = `<div class="columns"><aside class="panel"><h2>Ученики</h2><form id="new-student"><label>Логин<input name="login" required maxlength="40"></label><label>Пароль<input name="password" type="password" required minlength="8" autocomplete="new-password"></label><button class="primary">Добавить</button></form><div id="students">${overview.students.map(student => `<div class="student-row"><span>${escape(student.login)}<small><br>${student.completed.length} / ${overview.tasks.length}</small></span><button data-unlock="${escape(student.id)}">Разблокировать</button></div>`).join('')}</div></aside><div class="stack"><section class="card"><h1>Задания и контрольные</h1><p class="muted">Правки доступны ученикам автоматически. Уже начатые задания сохраняют свою версию. Ограничения копирования меняются сразу.</p><label>Задание<select id="teacher-task">${overview.tasks.map(task => `<option value="${escape(task.id)}" ${task.id === selectedId ? 'selected' : ''}>${escape(task.module)} · ${escape(task.title)}</option>`).join('')}</select></label><form id="edit-task"></form></section><section class="card"><h2>Последние проверки</h2><div class="table-scroll"><table><thead><tr><th>Ученик</th><th>Задание</th><th>Результат</th></tr></thead><tbody>${overview.attempts.slice(-20).reverse().map(row => `<tr><td>${escape(row.student)}</td><td>${escape(overview.tasks.find(task => task.id === row.taskId)?.title || row.taskId)}</td><td>${escape(row.kind === 'correct' ? 'Верно' : row.kind === 'different' ? 'Результат отличается' : row.kind === 'clipboard' ? 'Попытка копирования / вставки' : row.kind)}</td></tr>`).join('')}</tbody></table></div></section></div></div>`;
  const edit = () => {
    const task = overview.tasks.find(task => task.id === document.querySelector('#teacher-task').value);
    document.querySelector('#edit-task').innerHTML = `<label>Задание<textarea name="prompt" rows="3" required>${escape(task.prompt)}</textarea></label><label>Объяснение<textarea name="concept" rows="3" required>${escape(task.concept)}</textarea></label><label>Исходная база (SQL)<textarea name="seed" class="sql-input" rows="5">${escape(task.seed)}</textarea></label><label>Эталонное решение<textarea name="solution" class="sql-input" rows="3" required>${escape(task.solution)}</textarea></label><label class="check"><input name="restricted" type="checkbox" ${overview.policies[task.id] ? 'checked' : ''}><span>Контрольная с запретом копирования и вставки<br><small>В Windows включается защита захвата окна. За попытку копирования или вставки — пауза 30 секунд.</small></span></label><button class="primary">Сохранить</button>`;
  };
  document.querySelector('#teacher-task').onchange = edit; edit(); enhanceSelects();
  document.querySelector('#edit-task').onsubmit = async event => {
    event.preventDefault(); const form = new FormData(event.currentTarget);
    try {await api('/api/teacher/task', {id: document.querySelector('#teacher-task').value, prompt:form.get('prompt'), concept:form.get('concept'), seed:form.get('seed'), solution:form.get('solution'), restricted:form.has('restricted')}); notice('Сохранено. Ученики получат обновление автоматически.'); await teacherScreen();} catch(error) {notice(error.message);}
  };
  document.querySelector('#new-student').onsubmit = async event => {
    event.preventDefault(); const form = new FormData(event.currentTarget);
    try {await api('/api/teacher/student', {login:form.get('login'), password:form.get('password')}); await teacherScreen();} catch(error) {notice(error.message);}
  };
  for (const button of document.querySelectorAll('[data-unlock]')) button.onclick = async () => {try {await api('/api/teacher/unlock', {id:button.dataset.unlock}); notice('Ученик разблокирован.');} catch(error) {notice(error.message);}};
}
async function studentScreen() {
  root.dataset.screen = 'student';
  catalogue = await api('/api/catalog');
  const modules = [...new Set(catalogue.tasks.map(task => task.module))]; selectedModule ||= modules[0];
  root.innerHTML = `<div class="columns"><aside class="panel"><h2>Задания</h2><p class="muted" id="progress">Выполнено ${catalogue.completed.length} из ${catalogue.tasks.length}</p><label>Раздел<select id="module">${modules.map(module => `<option ${module === selectedModule ? 'selected' : ''}>${escape(module)}</option>`).join('')}</select></label><div id="task-list" class="task-list"></div><div class="actions pagination"><button id="prev" aria-label="Предыдущая страница">←</button><small id="page"></small><button id="next" aria-label="Следующая страница">→</button></div><button id="glossary">Справочник</button></aside><div id="work"></div></div>`;
  document.querySelector('#module').onchange = () => {selectedModule = document.querySelector('#module').value; currentPage = 0; list(); const first = catalogue.tasks.find(task => task.module === selectedModule); if (first) void openTask(first.id).catch(error => notice(error.message));};
  document.querySelector('#prev').onclick = () => {currentPage--; list();}; document.querySelector('#next').onclick = () => {currentPage++; list();};
  document.querySelector('#glossary').onclick = async () => {try {saveDraft(); const definitions = await api('/api/glossary'); document.querySelector('#work').innerHTML = '<section class="card reference"><h1>Справочник</h1>' + Object.entries(definitions).map(([term,text]) => `<details><summary>${escape(term)}</summary><p>${escape(text)}</p></details>`).join('') + '</section>'; saveDraft(); activeTask = null; restricted = false;} catch(error) {notice(error.message);}};
  enhanceSelects(); list(); await openTask(activeTask?.id || catalogue.tasks[0].id);
}
function list() {
  const tasks = catalogue.tasks.filter(task => task.module === selectedModule), pages = Math.ceil(tasks.length/8);
  currentPage = Math.max(0, Math.min(currentPage, pages-1));
  document.querySelector('#task-list').innerHTML = tasks.slice(currentPage*8,currentPage*8+8).map(task => `<button data-task="${escape(task.id)}" class="${task.id === activeTask?.id ? 'selected' : ''}">${escape(task.title)}${catalogue.completed.includes(task.id) ? '<small>Выполнено</small>' : ''}</button>`).join('');
  document.querySelector('.pagination').hidden = pages <= 1;
  document.querySelector('#page').textContent = `${currentPage+1} / ${pages}`;
  document.querySelector('#prev').disabled = currentPage === 0; document.querySelector('#next').disabled = currentPage+1 >= pages;
  for (const button of document.querySelectorAll('[data-task]')) button.onclick = () => openTask(button.dataset.task).catch(error => notice(error.message));
}
async function openTask(id) {
  saveDraft(); const response = await api('/api/task', {taskId:id}); activeTask = response.task; protect({restricted:activeTask.restricted, lockedUntil:response.lockedUntil});
  const task = activeTask;
  document.querySelector('#work').innerHTML = `<div class="workspace"><section class="card task-card"><div class="task-heading"><h1>${escape(task.title)}</h1><span class="badge">Вариант ${task.variantIndex}${task.restricted ? ' · Ограничения контрольной' : ''}</span></div><p class="prompt">${escape(task.prompt)}</p><details><summary>Подсказки</summary>${task.hints.map((hint,index) => `<details><summary>Подсказка ${index+1}</summary><p>${escape(hint)}</p></details>`).join('')}</details><div class="lesson"><h2>Объяснение</h2><p>${escape(task.concept)}</p><details><summary>Пример</summary><pre>${escape(task.example)}</pre></details><h3>Термины</h3><div class="inline-terms">${Object.entries(task.definitions).map(([term,text]) => `<details><summary>${escape(term)}</summary><p>${escape(text)}</p></details>`).join('')}</div></div><div class="lesson"><h2>Учебная база</h2><div id="preview">Загрузка таблиц…</div></div></section><div class="stack"><section class="card"><h2>SQL-редактор</h2><textarea id="sql" aria-label="SQL-запрос" spellcheck="false"></textarea><div class="actions"><button id="run">Запустить</button><button id="check" class="primary">Проверить</button></div></section><section class="card"><h2>Результат</h2><p id="result-status" role="status">Введите запрос.</p><div id="result" class="table-scroll"></div></section></div></div>`;
  document.querySelector('#sql').value = draft(id) ?? task.starter;
  document.querySelector('#sql').oninput = saveDraft;
  document.querySelector('#run').onclick = () => execute(false); document.querySelector('#check').onclick = () => execute(true);
  list();
  try {const response = await api('/api/execute', {taskId:id, code:'', check:false}); if(response.error) throw new Error(response.error); showTables(response.tables);} catch(error) {document.querySelector('#preview').textContent = error.message;}
}
function table(output) {return `<table><thead><tr>${output.columns.map(name => `<th>${escape(name)}</th>`).join('')}</tr></thead><tbody>${output.values.map(row => `<tr>${row.map(value => `<td>${escape(value === null ? 'NULL' : value)}</td>`).join('')}</tr>`).join('')}</tbody></table>`;}
function showTables(tables = []) {document.querySelector('#preview').innerHTML = tables.length ? tables.map(item => `<details><summary>${escape(item.name)}</summary><p class="muted">${item.columns.map(column => escape(`${column[1]} · ${column[2]}`)).join(', ')}</p><div class="table-scroll">${table(item.rows)}</div></details>`).join('') : '<p class="muted">Таблиц пока нет. Создайте их запросом.</p>';}
async function execute(check) {
  saveDraft(); const code = document.querySelector('#sql').value; if (!code.trim()) return notice('Введите SQL-запрос.');
  const id = activeTask.id; document.querySelector('#run').disabled = document.querySelector('#check').disabled = true;
  try {
    const response = await api('/api/execute', {taskId:id, code, check});
    const status = document.querySelector('#result-status'); status.className = check ? response.correct ? 'success' : 'error' : '';
    status.textContent = check ? response.correct ? 'Задание выполнено.' : 'Запрос выполнен, но результат отличается от задания.' : 'Запрос выполнен.';
    document.querySelector('#result').innerHTML = table(response.output); showTables(response.tables);
    if (response.correct && !catalogue.completed.includes(id)) {catalogue.completed.push(id); list(); document.querySelector('#progress').textContent = `Выполнено ${catalogue.completed.length} из ${catalogue.tasks.length}`;}
  } catch(error) {
    document.querySelector('#result').innerHTML = '';
    const status = document.querySelector('#result-status'); status.className = 'error'; status.textContent = error.message;
    if (/no such|syntax|constraint/i.test(error.message)) {const note = document.createElement('p'); note.textContent = 'Сверьте имена таблиц и столбцов, синтаксис и ограничения с учебной базой.'; status.append(note);}
  } finally {if (document.querySelector('#run')) document.querySelector('#run').disabled = document.querySelector('#check').disabled = false;}
}
setInterval(async () => {
  if (!auth || role !== 'student' || syncing) return; syncing = true;
  try {
    const next = await api('/api/catalog');
    document.querySelector('#connection').textContent = 'Подключено к преподавателю';
    lockedUntil = next.lockedUntil;
    if (next.revision !== catalogue.revision) {
      catalogue = next; list();
      if (activeTask) {const nextTask = await api('/api/task', {taskId:activeTask.id}); protect({restricted:nextTask.task.restricted, lockedUntil:nextTask.lockedUntil});}
      notice('Настройки преподавателя обновлены. Начатое задание сохранено.');
    }
  } catch {document.querySelector('#connection').textContent = 'Нет связи · черновик сохранён';} finally {syncing = false;}
}, 2000);
(async () => {if (bridge) role = (await bridge.info()).role; await loginScreen();})();
