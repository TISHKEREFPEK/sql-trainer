const root = document.querySelector('#root');
const escape = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
let auth = '', role = 'student', activeTask, overview, catalogue, restricted = false, lockedUntil = 0, currentPage = 0, selectedModule = '', syncing = false;
const bridge = window.classroom;
let currentTheme = {preset:'light'}, themePresets = [];
function setTheme(theme, cache = true) {
  currentTheme = theme; SQLThemes.apply(theme);
  if (cache) try {localStorage.setItem('classroom:theme:'+role+':'+(document.querySelector('#connection').dataset.login || ''),JSON.stringify(theme));} catch {}
}
SQLThemes.apply(currentTheme);
document.querySelector('#appearance').onclick = () => {
  if (document.querySelector('#theme-dialog')) return;
  const saved = structuredClone(currentTheme); let choice = structuredClone(saved), saving = false, selectedPreset = themePresets.find(item=>['background','surface','accent','editor'].every(key=>SQLThemes.resolve(item.theme)[key].toUpperCase()===SQLThemes.resolve(saved)[key].toUpperCase()))?.id || '';
  const dialog = document.createElement('dialog'); dialog.id = 'theme-dialog';
  dialog.innerHTML = '<form method="dialog" class="theme-form"><div class="panel-heading"><h2>Оформление</h2><button type="button" id="close-theme" aria-label="Закрыть настройки">×</button></div><div class="theme-presets">'+Object.entries(SQLThemes.presets).map(([id,preset])=>'<button type="button" data-preset="'+id+'" aria-pressed="false"><span class="theme-swatch" aria-hidden="true"></span>'+preset.name+'</button>').join('')+'</div><section class="my-themes"><h3>Мои темы</h3><div id="my-theme-list"></div></section><p id="custom-theme-name" class="muted" hidden></p><fieldset><legend>Свои цвета</legend><div class="theme-colors">'+[['background','Фон'],['surface','Панели'],['accent','Акцент'],['editor','SQL-редактор']].map(([key,title])=>'<label>'+title+'<input type="color" name="'+key+'"></label>').join('')+'</div></fieldset><div class="preset-save"><label for="preset-name">Название своей темы</label><div><input id="preset-name" maxlength="50" placeholder="Например, Оникс с бирюзовым"><button type="button" id="save-preset">Сохранить как тему</button></div><p class="muted">Сохранённые темы остаются в профиле при отмене.</p></div><p class="muted">Цвет текста подбирается автоматически. Тема сохраняется в вашем профиле.</p><p id="theme-error" class="error" role="status"></p><div class="theme-actions"><button type="button" id="cancel-theme">Отменить</button><button type="button" id="save-theme" class="primary">Сохранить</button></div></form>';
  document.body.append(dialog);
  const update = () => {
    const colors=SQLThemes.resolve(choice);
    dialog.querySelectorAll('[data-preset]').forEach(button=>button.setAttribute('aria-pressed',String(!selectedPreset && button.dataset.preset===choice.preset)));
    dialog.querySelectorAll('input[type=color]').forEach(input=>input.value=colors[input.name]);
    const custom=dialog.querySelector('#custom-theme-name'); custom.hidden=!Object.keys(choice.colors || {}).length; custom.textContent='Свой вариант на основе темы «'+SQLThemes.presets[choice.preset].name+'»';
    dialog.querySelectorAll('[data-my-theme]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.myTheme===selectedPreset)));
    SQLThemes.apply(choice);
  };
  dialog.querySelectorAll('[data-preset]').forEach(button=>{
    const preset=SQLThemes.presets[button.dataset.preset];
    const swatch=button.querySelector('.theme-swatch'); swatch.style.background=preset.surface; swatch.style.borderColor=preset.accent; swatch.style.boxShadow='inset 16px 0 '+preset.editor;
    button.onclick=()=>{selectedPreset='';choice={preset:button.dataset.preset};update();};
  });
  dialog.querySelectorAll('input[type=color]').forEach(input=>input.oninput=()=>{selectedPreset='';choice.colors={...choice.colors,[input.name]:input.value};update();});
  const changePresets = async body => {
    if (saving) return;
    saving=true;dialog.querySelector('#theme-error').textContent='';
    dialog.querySelectorAll('button,input').forEach(control=>control.disabled=true);
    try {
      const response=await api('/api/theme-presets',body);themePresets=response.themePresets;
      if (body.action==='save') {selectedPreset=themePresets[themePresets.length-1].id;dialog.querySelector('#preset-name').value='';}
      else if (selectedPreset===body.id) selectedPreset='';
      renderPresets();update();reveal(dialog.querySelector('#my-theme-list'));
    } catch(error) {dialog.querySelector('#theme-error').textContent=error.message;}
    finally {saving=false;dialog.querySelectorAll('button,input').forEach(control=>control.disabled=false);}
  };
  const renderPresets = () => {
    const list=dialog.querySelector('#my-theme-list');
    list.innerHTML=themePresets.length ? themePresets.map(item=>'<div class="my-theme-row"><button type="button" data-my-theme="'+escape(item.id)+'" aria-pressed="false"><span class="theme-dots" aria-hidden="true"></span><span>'+escape(item.name)+'</span></button><button type="button" data-delete-theme="'+escape(item.id)+'" aria-label="Удалить тему '+escape(item.name)+'">×</button></div>').join('') : '<p class="muted">Сохраните свои цвета — тема появится здесь.</p>';
    list.querySelectorAll('[data-my-theme]').forEach(button=>{
      const item=themePresets.find(item=>item.id===button.dataset.myTheme), colors=SQLThemes.resolve(item.theme);
      for (const color of [colors.background,colors.surface,colors.accent,colors.editor]) {const dot=document.createElement('i');dot.style.background=color;button.querySelector('.theme-dots').append(dot);}
      button.onclick=()=>{selectedPreset=item.id;choice=structuredClone(item.theme);update();};
    });
    list.querySelectorAll('[data-delete-theme]').forEach(button=>button.onclick=()=>changePresets({action:'delete',id:button.dataset.deleteTheme}));
  };
  dialog.querySelector('#save-preset').onclick=()=>changePresets({action:'save',name:dialog.querySelector('#preset-name').value,theme:choice});
  dialog.querySelector('#preset-name').onkeydown=event=>{if(event.key==='Enter'){event.preventDefault();dialog.querySelector('#save-preset').click();}};
  renderPresets();
  const cancel=()=>{if (saving) return;setTheme(saved);dialog.close();};
  dialog.querySelector('#close-theme').onclick=cancel; dialog.querySelector('#cancel-theme').onclick=cancel;
  dialog.addEventListener('cancel',event=>{event.preventDefault();cancel();});
  dialog.addEventListener('close',()=>{dialog.remove();document.querySelector('#appearance').focus();});
  dialog.querySelector('#save-theme').onclick=async()=>{
    saving=true; const controls=[...dialog.querySelectorAll('button,input')]; controls.forEach(control=>control.disabled=true);
    try {const response=await api('/api/theme',{theme:choice});setTheme(response.theme);dialog.close();notice('Оформление сохранено.');}
    catch(error){dialog.querySelector('#theme-error').textContent=error.message;}
    finally{saving=false;controls.forEach(control=>control.disabled=false);}
  };
  update();dialog.showModal();reveal(dialog);
};
const hintProgress = new Map();
const categoryOf = module => /^Проект/.test(module) ? 'projects' : /^0[1-8] /.test(module) ? 'course' : 'practice';
let selectedCategory = 'course', workRequest = 0;
function reveal(node) {
  if (!node || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  node.animate([{opacity:0, transform:'translateY(5px)'},{opacity:1, transform:'translateY(0)'}], {duration:190, easing:'cubic-bezier(.2,.8,.2,1)'});
}
// Every disclosure uses the same motion, including tables and the reference.
document.addEventListener('toggle', event => {
  if (event.target instanceof HTMLDetailsElement && event.target.open) {
    for (const child of event.target.children) if (child.tagName !== 'SUMMARY') reveal(child);
  }
}, true);
function renderHints(task) {
  const host = document.querySelector('#hints');
  const hints = task.hints || [];
  if (!hints.length) {host.hidden = true; return;}
  const shown = Math.min(hints.length, hintProgress.get(task.id) || 0);
  host.innerHTML = '<div class="hint-heading"><h2>Подсказки</h2><span class="hint-count" role="status">' + shown + ' из ' + hints.length + '</span></div>' +
    '<ol class="hint-list" aria-live="polite">' + hints.slice(0, shown).map((hint, index) => '<li><span class="hint-step">' + (index+1) + '</span><p>' + escape(hint) + '</p></li>').join('') + '</ol>' +
    (shown < hints.length ? '<button id="next-hint" class="hint-button">' + (shown ? 'Следующая подсказка' : 'Открыть подсказку') + '</button>' : '<p class="hint-finished">Все подсказки открыты</p>');
  document.querySelector('#next-hint')?.addEventListener('click', async event => {
    const request = workRequest, button = event.currentTarget; button.disabled = true;
    try {
      const response = await api('/api/hint', {taskId:task.id, index:shown});
      if (request !== workRequest) return;
      hintProgress.set(task.id, response.hintUsage.count); renderHints(task);
    reveal(host.querySelector('li:last-child'));
    // Preserve keyboard focus when the clicked button is replaced.
    const next = host.querySelector('button') || host.querySelector('.hint-finished');
    if (next) {if (next.tagName !== 'BUTTON') next.tabIndex = -1; next.focus({preventScroll:true});}
    } catch(error) {if (request === workRequest) notice('Подсказка не сохранена: ' + error.message);} finally {button.disabled = false;}
  });
}
function renderTerms(task) {
  const host = document.querySelector('#terms');
  const entries = Object.entries(task.definitions || {});
  host.hidden = !entries.length;
  host.innerHTML = '<h3>Термины</h3><div class="term-buttons">' + entries.map(([term],index) => '<button type="button" data-term="' + index + '" aria-expanded="false" aria-controls="term-description">' + escape(term) + '</button>').join('') + '</div><div id="term-description" class="term-description" hidden></div>';
  host.querySelectorAll('[data-term]').forEach(button => button.onclick = () => {
    const opening = button.getAttribute('aria-expanded') !== 'true';
    host.querySelectorAll('[data-term]').forEach(item => item.setAttribute('aria-expanded','false'));
    const description = host.querySelector('#term-description'); description.hidden = !opening;
    if (opening) {
      button.setAttribute('aria-expanded','true'); const [term,text] = entries[Number(button.dataset.term)];
      description.innerHTML = '<strong>' + escape(term) + '</strong><p>' + escape(text) + '</p>'; reveal(description);
    }
  });
}
function renderNavigation() {
  const categories = [['course','Курс'],['practice','Практика'],['projects','Проекты']];
  const modules = [...new Set(catalogue.tasks.map(task => task.module))];
  const host = document.querySelector('#module-navigation');
  host.innerHTML = '<div class="category-buttons" role="group" aria-label="Тип заданий">' + categories.map(([id,title]) => '<button data-category="' + id + '" aria-pressed="' + (id === selectedCategory) + '">' + title + '</button>').join('') + '</div><div class="module-list" aria-label="Темы">' + modules.filter(module => categoryOf(module) === selectedCategory).map((module,index) => '<button data-module="' + escape(module) + '" aria-pressed="' + (module === selectedModule) + '"><span>' + escape(module.replace(/^0[1-8] · /,'').replace(/^Проект · /,'')) + '</span><span class="module-count">' + catalogue.tasks.filter(task => task.module === module).length + '</span></button>').join('') + '</div>';
  const choose = module => {
    selectedModule = module; selectedCategory = categoryOf(module); currentPage = 0; renderNavigation(); list();
    [...host.querySelectorAll('[data-module]')].find(button => button.dataset.module === module)?.focus({preventScroll:true});
    const first = catalogue.tasks.find(task => task.module === module); if (first) void openTask(first.id).catch(error => notice(error.message));
  };
  host.querySelectorAll('[data-category]').forEach(button => button.onclick = () => {
    if (button.dataset.category === selectedCategory) return;
    choose(modules.find(module => categoryOf(module) === button.dataset.category));
  });
  host.querySelectorAll('[data-module]').forEach(button => button.onclick = () => {if (button.dataset.module !== selectedModule || !activeTask) choose(button.dataset.module);});
}

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
  saveDraft(); workRequest++; hintProgress.clear(); await api('/api/logout', {}); auth = ''; activeTask = null; restricted = false; lockedUntil = 0;
  document.querySelector('#appearance').hidden = true; setTheme({preset:'light'},false); document.querySelector('#logout').hidden = true; await loginScreen();
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
  document.querySelector('#connection').textContent = 'Локальная тестовая версия';
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
        document.querySelector('#connection').dataset.login = login || 'teacher'; themePresets=response.themePresets || []; setTheme(response.theme || {preset:'light'}); document.querySelector('#appearance').hidden = false; document.querySelector('#logout').hidden = false; document.querySelector('#connection').textContent = role === 'teacher' ? 'Кабинет преподавателя' : 'Подключено к преподавателю';
        await (role === 'teacher' ? teacherScreen() : studentScreen());
      } catch(error) {notice(error.message);}
    };
  } catch(error) {root.innerHTML = '<section class="card login"><h1>Преподаватель недоступен</h1><p>Запустите преподавательское приложение на этом компьютере.</p><button id="retry">Повторить</button></section>'; document.querySelector('#retry').onclick = loginScreen; notice(error.message);}
}
async function teacherScreen() {
  root.dataset.screen = 'teacher';
  const selectedId = document.querySelector('#teacher-task')?.value;
  overview = await api('/api/teacher/overview');
  root.innerHTML = `<div class="columns teacher-layout"><aside class="panel"><div class="panel-heading"><h2>Ученики</h2><span class="count">${overview.students.length}</span></div><form id="new-student"><label>Логин<input name="login" required maxlength="40"></label><label>Пароль<input name="password" type="password" required minlength="8" autocomplete="new-password"></label><button class="primary">Добавить</button></form><div id="students">${overview.students.map(student => `<div class="student-row"><span>${escape(student.login)}<small><br>${student.completed.length} / ${overview.tasks.length}</small></span><button data-unlock="${escape(student.id)}">Разблокировать</button></div>`).join('')}</div></aside><div class="stack teacher-content"><section class="card teacher-editor"><div class="panel-heading"><h1>Редактор задания</h1><span class="badge">Преподаватель</span></div><p class="muted">Правки доступны ученикам автоматически. Уже начатые задания сохраняют свою версию. Ограничения копирования меняются сразу.</p><label>Задание<select id="teacher-task">${overview.tasks.map(task => `<option value="${escape(task.id)}" ${task.id === selectedId ? 'selected' : ''}>${escape(task.module)} · ${escape(task.title)}</option>`).join('')}</select></label><form id="edit-task"></form></section><section class="card teacher-journal"><div class="panel-heading"><h2>Журнал проверок</h2><div class="actions"><span class="count" id="attempt-count">${overview.attempts.length}</span><button id="refresh-reports">Обновить</button></div></div><div id="attempt-report" class="table-scroll"><table><thead><tr><th>Ученик</th><th>Задание</th><th>Результат</th></tr></thead><tbody>${overview.attempts.slice(-20).reverse().map(row => `<tr><td>${escape(row.student)}</td><td>${escape(overview.tasks.find(task => task.id === row.taskId)?.title || row.taskId)}</td><td>${escape(row.kind === 'correct' ? 'Верно' : row.kind === 'different' ? 'Результат отличается' : row.kind === 'clipboard' ? 'Попытка копирования / вставки' : row.kind)}</td></tr>`).join('')}</tbody></table></div><div class="panel-heading"><h2>Использование подсказок</h2><span id="hint-summary" class="count"></span></div><div id="hint-report" class="table-scroll"></div></section></div></div>`;
  renderHintReport(); document.querySelector('#refresh-reports').onclick = () => refreshReports().catch(error => notice(error.message));
  const edit = () => {
    const task = overview.tasks.find(task => task.id === document.querySelector('#teacher-task').value);
    document.querySelector('#edit-task').innerHTML = `<label>Задание<textarea name="prompt" rows="3" required>${escape(task.prompt)}</textarea></label><label>Объяснение<textarea name="concept" rows="3" required>${escape(task.concept)}</textarea></label><label>Исходная база (SQL)<textarea name="seed" class="sql-input" rows="5">${escape(task.seed)}</textarea></label><label>Эталонное решение<textarea name="solution" class="sql-input" rows="3" required>${escape(task.solution)}</textarea></label><label class="check"><input name="restricted" type="checkbox" ${overview.policies[task.id] ? 'checked' : ''}><span>Контрольная с запретом копирования и вставки<br><small>В Windows включается защита захвата окна. За попытку копирования или вставки — пауза 30 секунд.</small></span></label><button class="primary">Сохранить</button>`;
  };
  document.querySelector('#teacher-task').onchange = () => {edit(); reveal(document.querySelector('#edit-task'));}; edit(); enhanceSelects();
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

function renderHintReport() {
  const rows = overview.students.flatMap(student => (student.hintUsage || []).map(usage => ({student:student.login,...usage})));
  document.querySelector('#hint-summary').textContent = rows.reduce((sum,row) => sum+row.count,0);
  document.querySelector('#hint-report').innerHTML = rows.length ? '<table><thead><tr><th>Ученик</th><th>Задание</th><th>Открыто</th><th>Подсказки</th></tr></thead><tbody>' + rows.map(row => '<tr><td>'+escape(row.student)+'</td><td>'+escape(row.title)+'<small class="report-variant">Вариант '+row.variantIndex+'</small></td><td>'+row.count+' / '+row.total+'</td><td>'+row.openedAt.map((at,index) => '<span class="hint-time">№'+(index+1)+' — '+escape(new Date(at).toLocaleString('ru-RU'))+'</span>').join('')+'</td></tr>').join('') + '</tbody></table>' : '<p class="muted">Ученики пока не открывали подсказки.</p>';
}
let refreshingReports = false;
async function refreshReports() {
  if (refreshingReports || !auth || role !== 'teacher') return;
  refreshingReports = true; const session = auth;
  try {
    const next = await api('/api/teacher/overview');
    if (auth !== session || role !== 'teacher' || !document.querySelector('#hint-report')) return;
    overview = next; renderHintReport();
    document.querySelector('#attempt-count').textContent = overview.attempts.length;
    document.querySelector('#attempt-report tbody').innerHTML = overview.attempts.slice(-20).reverse().map(row => '<tr><td>'+escape(row.student)+'</td><td>'+escape(overview.tasks.find(task => task.id === row.taskId)?.title || row.taskId)+'</td><td>'+escape(row.kind === 'correct' ? 'Верно' : row.kind === 'different' ? 'Результат отличается' : row.kind === 'clipboard' ? 'Попытка копирования / вставки' : row.kind)+'</td></tr>').join('');
  } finally {refreshingReports = false;}
}
setInterval(() => {if (role === 'teacher' && auth) void refreshReports().catch(() => {});}, 5000);

async function studentScreen() {
  root.dataset.screen = 'student';
  catalogue = await api('/api/catalog');
  const modules = [...new Set(catalogue.tasks.map(task => task.module))]; if (!modules.includes(selectedModule)) selectedModule = modules[0]; selectedCategory = categoryOf(selectedModule);
  root.innerHTML = `<div class="columns"><aside class="panel"><div class="panel-heading"><h2>Задания</h2><span class="count">${catalogue.tasks.length}</span></div><p class="muted" id="progress">Выполнено ${catalogue.completed.length} из ${catalogue.tasks.length}</p><nav id="module-navigation" aria-label="Разделы заданий"></nav><div id="task-list" class="task-list"></div><div class="actions pagination"><button id="prev" aria-label="Предыдущая страница">←</button><small id="page"></small><button id="next" aria-label="Следующая страница">→</button></div><button id="glossary" class="reference-button">Справочник SQL</button></aside><div id="work"></div></div>`;

  document.querySelector('#prev').onclick = () => {currentPage--; list();}; document.querySelector('#next').onclick = () => {currentPage++; list();};
  document.querySelector('#glossary').onclick = async () => {if (document.querySelector('.reference')) return; try {saveDraft(); const request = ++workRequest, previous = activeTask?.id; const definitions = await api('/api/glossary'); if (request !== workRequest) return; document.querySelector('#glossary').setAttribute('aria-pressed','true'); document.querySelector('#work').innerHTML = '<section class="card reference"><div class="panel-heading"><h1>Справочник SQL</h1><button id="back-to-task">К заданию</button></div>' + Object.entries(definitions).map(([term,text]) => `<details><summary>${escape(term)}</summary><p>${escape(text)}</p></details>`).join('') + '</section>'; activeTask = null; restricted = false; list(); reveal(document.querySelector('.reference')); document.querySelector('#back-to-task').onclick = () => openTask(previous || catalogue.tasks.find(task => task.module === selectedModule).id).catch(error => notice(error.message));} catch(error) {notice(error.message);}};
  renderNavigation(); list(); await openTask(activeTask?.id || catalogue.tasks.find(task => task.module === selectedModule).id);
}
function list() {
  const tasks = catalogue.tasks.filter(task => task.module === selectedModule), pages = Math.ceil(tasks.length/8);
  currentPage = Math.max(0, Math.min(currentPage, pages-1));
  document.querySelector('#task-list').innerHTML = tasks.slice(currentPage*8,currentPage*8+8).map((task, index) => `<button data-task="${escape(task.id)}" class="${task.id === activeTask?.id ? 'selected' : ''}"><span class="task-number">${String(currentPage*8+index+1).padStart(2,"0")}</span><span>${escape(task.title)}${catalogue.completed.includes(task.id) ? '<small>Выполнено</small>' : ''}</span></button>`).join('');
  document.querySelector('.pagination').hidden = pages <= 1;
  document.querySelector('#page').textContent = `${currentPage+1} / ${pages}`;
  document.querySelector('#prev').disabled = currentPage === 0; document.querySelector('#next').disabled = currentPage+1 >= pages;
  for (const button of document.querySelectorAll('[data-task]')) button.onclick = () => openTask(button.dataset.task).catch(error => notice(error.message));
}
async function openTask(id) {
  saveDraft(); const request = ++workRequest; const response = await api('/api/task', {taskId:id}); if (request !== workRequest) return; activeTask = response.task; protect({restricted:activeTask.restricted, lockedUntil:response.lockedUntil});
  const task = activeTask;
  const navigationTask = catalogue.tasks.find(item => item.id === id); if (navigationTask) navigationTask.title = task.title;
  document.querySelector('#work').innerHTML = `<div class="workspace"><section class="card task-card"><div class="task-heading"><h1>${escape(task.title)}</h1><span class="badge">Вариант ${task.variantIndex}${task.restricted ? ' · Ограничения контрольной' : ''}</span></div><div class="assignment"><h2>Условие</h2><p class="prompt">${escape(task.prompt)}</p></div><section id="hints" class="hints" aria-label="Подсказки к заданию"></section><div class="lesson"><h2>Объяснение</h2><p>${escape(task.concept)}</p><details><summary>Пример</summary><pre>${escape(task.example)}</pre></details><section id="terms" class="terms"></section></div><div class="lesson"><h2>Учебная база</h2><div id="preview">Загрузка таблиц…</div></div></section><div class="stack query-stack"><section class="card editor-panel"><div class="panel-heading"><h2>SQL-редактор</h2><span class="editor-dialect">SQLite</span></div><textarea id="sql" aria-label="SQL-запрос" spellcheck="false"></textarea><div class="actions"><button id="run">Запустить</button><button id="check" class="primary">Проверить</button></div></section><section class="card result-panel"><div class="panel-heading"><h2>Результат</h2><span class="result-label">Вывод запроса</span></div><p id="result-status" role="status">Запустите запрос, чтобы увидеть результат.</p><div id="result" class="table-scroll"></div></section></div></div>`;
  document.querySelector('#glossary').setAttribute('aria-pressed','false');
  hintProgress.set(task.id, response.hintUsage?.count || 0);
  renderHints(task); renderTerms(task); reveal(document.querySelector('.workspace'));
  document.querySelector('#sql').value = draft(id) ?? task.starter;
  document.querySelector('#sql').oninput = saveDraft;
  document.querySelector('#run').onclick = () => execute(false); document.querySelector('#check').onclick = () => execute(true);
  list();
  try {const response = await api('/api/execute', {taskId:id, code:'', check:false}); if (request !== workRequest) return; if(response.error) throw new Error(response.error); showTables(response.tables);} catch(error) {if (request === workRequest) document.querySelector('#preview').textContent = error.message;}
}
function table(output) {return `<table><thead><tr>${output.columns.map(name => `<th>${escape(name)}</th>`).join('')}</tr></thead><tbody>${output.values.map(row => `<tr>${row.map(value => `<td>${escape(value === null ? 'NULL' : value)}</td>`).join('')}</tr>`).join('')}</tbody></table>`;}
function showTables(tables = []) {document.querySelector('#preview').innerHTML = tables.length ? tables.map(item => `<details><summary>${escape(item.name)}</summary><p class="muted">${item.columns.map(column => escape(`${column[1]} · ${column[2]}`)).join(', ')}</p><div class="table-scroll">${table(item.rows)}</div></details>`).join('') : '<p class="muted">Таблиц пока нет. Создайте их запросом.</p>';}
async function execute(check) {
  saveDraft(); const code = document.querySelector('#sql').value; if (!code.trim()) return notice('Введите SQL-запрос.');
  const id = activeTask.id, request = workRequest, run = document.querySelector('#run'), checkButton = document.querySelector('#check'); run.disabled = checkButton.disabled = true;
  try {
    const response = await api('/api/execute', {taskId:id, code, check});
    if (request !== workRequest) return;
    const status = document.querySelector('#result-status'); status.className = check ? response.correct ? 'success' : 'error' : '';
    status.textContent = check ? response.correct ? 'Задание выполнено.' : 'Запрос выполнен, но результат отличается от задания.' : 'Запрос выполнен.';
    document.querySelector('#result').innerHTML = table(response.output); showTables(response.tables);
    if (response.correct && !catalogue.completed.includes(id)) {catalogue.completed.push(id); list(); document.querySelector('#progress').textContent = `Выполнено ${catalogue.completed.length} из ${catalogue.tasks.length}`;}
  } catch(error) {
    if (request !== workRequest) return;
    document.querySelector('#result').innerHTML = '';
    const status = document.querySelector('#result-status'); status.className = 'error'; status.textContent = error.message;
    if (/no such|syntax|constraint/i.test(error.message)) {const note = document.createElement('p'); note.textContent = 'Сверьте имена таблиц и столбцов, синтаксис и ограничения с учебной базой.'; status.append(note);}
  } finally {if (request === workRequest) reveal(document.querySelector('#result-status')); run.disabled = checkButton.disabled = false;}
}
setInterval(async () => {
  if (!auth || role !== 'student' || syncing) return; syncing = true;
  try {
    const next = await api('/api/catalog');
    document.querySelector('#connection').textContent = 'Подключено к преподавателю';
    lockedUntil = next.lockedUntil;
    if (next.revision !== catalogue.revision) {
      catalogue = next; list();
      if (activeTask) {const id = activeTask.id, request = workRequest; const nextTask = await api('/api/task', {taskId:id}); if (request === workRequest && activeTask?.id === id) {protect({restricted:nextTask.task.restricted, lockedUntil:nextTask.lockedUntil}); const item = catalogue.tasks.find(task => task.id === id); if (item) item.title = nextTask.task.title; list();}}
      notice('Настройки преподавателя обновлены. Начатое задание сохранено.');
    }
  } catch {document.querySelector('#connection').textContent = 'Нет связи · черновик сохранён';} finally {syncing = false;}
}, 2000);
(async () => {if (bridge) role = (await bridge.info()).role; await loginScreen();})();
