# SQL-пространство

Русскоязычный интерактивный тренажёр SQLite для начинающих. Включает 16 коротких уроков, практику по работам преподавателя 1–10 (в том числе расширенный набор запросов к парковке), три сквозных проекта, подсказки и словарь, повторение, локальное сохранение гостевого прогресса, синхронизацию по коду группы и кабинет преподавателя с редактором заданий. Полный набор таблиц из «Парковка.xlsx» и CSV-наборы работ 7, 9 и 10 загружаются по запросу при открытии соответствующих упражнений.

## Локальный запуск

Нужен Node.js 22.13 или новее и pnpm 11.

```sh
pnpm install --frozen-lockfile
pnpm exec drizzle-kit generate
pnpm build
node --import ./scripts/sites-env.mjs ./node_modules/wrangler/bin/wrangler.js d1 execute DB --local --config dist/server/wrangler.json --persist-to .wrangler/state --file drizzle/0000_ambitious_kylun.sql
node --import ./scripts/sites-env.mjs ./node_modules/wrangler/bin/wrangler.js d1 execute DB --local --config dist/server/wrangler.json --persist-to .wrangler/state --file drizzle/0001_teacher_tasks.sql
node --import ./scripts/sites-env.mjs ./node_modules/wrangler/bin/wrangler.js d1 execute DB --local --config dist/server/wrangler.json --persist-to .wrangler/state --file drizzle/0002_task_variants_analytics.sql
pnpm dev
```

Для локального входа администратора создайте `.dev.vars` с `ADMIN_PASSWORD_HASH`, равным SHA-256 выбранного пароля. Файл игнорируется Git. На размещённом сайте значение задаётся в Sites как secret с тем же ключом.

## Хранение и проверка

SQLite WASM запускает учебные запросы отдельно в браузере. D1 хранит группы, псевдонимы, прогресс, проектные снимки, время выполнения заданий, частые ошибки, варианты и редактируемый каталог заданий. Гостевые данные хранятся в IndexedDB. Запросы ученика не выполняются в серверной базе.

## Участники проекта

- [domain1337](https://github.com/domain1337) — супер-кодер, генератор идей и главный тестировщик, который доводит проект до блеска.
- [TISHKEREFPEK](https://github.com/TISHKEREFPEK) — создатель проекта, легендарный архитектор SQL-пространства и капитан команды.
- [Codex](https://openai.com/codex/) — помощь с реализацией и сопровождением кода.
