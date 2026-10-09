# Матрица соответствия функций

Источник — текущий сайт (`app`, `lib`) и Electron (`desktop`), включая тестовые профили из Electron. Колонка проверки разделяет код/автоматизированный сценарий и приёмку на Windows; отсутствие ручной проверки не помечается как завершённый перенос.

| Функция старой версии | Новая реализация | Выполненная проверка | Приёмка |
|---|---|---|---|
| 120 задач, четыре версии | Domain/Catalog, Variants, export-catalog | Fixtures TypeScript, все решения | Автоматически проверено |
| Практические базы, три проекта | SeedProfiles, Assignments, Snapshots | Все четыре цепочки трёх проектов | Автоматически проверено |
| Курс / практика / проекты, поиск, 8 задач | StudentScreen | Native rendering 1320/900 px | Клавиатура/DPI Windows |
| Условие, объяснение, пример, термины, словарь | StudentTask, StudentScreen | Рендер и каталог | Длинные условия Windows |
| Последовательные подсказки | hint API и journal | Порядок и повтор запроса | Проверено API |
| SQL-редактор с подсветкой | AvaloniaEdit 11.4.1 | Headless Skia снимки | Ввод/IME Windows |
| Таблицы и результаты | SQLite previews + типы/ключи, DataGrid | Запросы и UI снимки | Большие таблицы/DPI |
| SQLite error + русское пояснение | SqlEngine.Explain | Syntax/missing column/access denied | Автоматически проверено |
| Сравнение строк/дубликатов/порядка | Query Normalize | Неверные ответы и отдельные проверки | Автоматически проверено |
| Состояние/ограничения/транзакции | State, constraint/transaction probes | Все эталоны и альтернативы | Автоматически проверено |
| Варианты и прежние условия | Полный Assignments JSON | Правка каталога после назначения | Автоматически проверено |
| Проектный снимок только после успеха | Транзакция Completion + Snapshot | Проект, повтор/перезапуск | Автоматически проверено |
| Черновик и статус сохранения | debounce, сериализация, память окна | Draft API / перезапуск | Обрыв связи при вводе Windows |
| Темы, пользовательские цвета, 30 пресетов | ThemeManager, AppearanceWindow, API | Профильная изоляция/перезапуск/UI | Шесть тем Windows |
| Ученики, группы, пароли | TeacherScreen + role API | 35 профилей, смена группы | UI Windows |
| Создание/редактирование задач и вариантов | TeacherScreen, проверка эталона, TaskRevisions | Проектный authoring | Редактор Windows |
| Результаты, ошибки, подсказки, активность | overview, TeacherScreen, CSV, сводки ошибок/времени/остановок | Attempts/progress/hints | CSV и UI Windows |
| Copy/paste, пауза 30 с, unlock | Tunnel keys/drop, policy API | 423 и ручной unlock API | Клавиатура/clipboard Windows |
| Захват окна Windows | SetWindowDisplayAffinity | В новой версии ещё не проверено на Windows | Обязательная ручная проверка |
| Импорт тестовых аккаунтов Electron | Read-only preview, scrypt, transaction | Синтетический импорт/rollback/idempotency | Локальная копия: 1 профиль / 1 назначение / 1 попытка, хеш/прогресс совпали |
| Импорт снимков/правок/тем/результатов | ElectronImport | Fixture, целостность SQLite и backup restore | Реальные проекты при наличии |
| HTTPS, fingerprint, чужая роль | ClassroomApi / ASP.NET | Правильный/неверный сертификат, 403 | Два физических компьютера |
| До 35 клиентов | 4 worker + очередь 35 | 35 настоящих API-клиентов одновременно | Кабинетная сеть |
| Отдельный сервер, tray, завершение | HostManager, process stdin, native tray | Серверный restart | Tray Windows |
| Изоляция по времени/правам/памяти | Authorizer, deadline, restricted token, Job | Файловые запреты, 5-с таймаут | Windows native limits |
| Backup до импорта/миграции, daily/manual/restore | Storage/Backups | Online backup и restore | Обновление установщика |
| Раздельные Windows x64-поставки | Self-contained publish, Inno Setup, CI | Windows CI: 15 проверок, два установщика, проверка Student | Установщики Windows 11/10 |

Окончательная готовность требует закрыть ручные пункты из [acceptance.md](acceptance.md). Сайт и прежний Electron сохранены для сравнения и отката. Личные базы, пароли и сертификаты не входят в репозиторий.

## Выполненная Windows CI · 9 октября 2026

[Run 37902279682](https://github.com/TISHKEREFPEK/sql-trainer/actions/runs/37902279682), коммит `b9ab3af`: locked restore, Release build без предупреждений/ошибок, 10 тестов логики/API и 5 UI-тестов прошли. Сетевой тест использует Windows worker с Job Object и restricted token, 35 клиентов и 5-секундный таймаут. Проверены импорт/rollback, восстановление старой схемы и повторный вход. Обе self-contained публикации и оба Inno Setup установщика собраны, проверка отсутствия серверных файлов в Student прошла. Это Windows runner, не физический кабинет и не проверка захвата окна/DPI/tray.
