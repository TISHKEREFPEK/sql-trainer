# SQL-пространство

Русскоязычный интерактивный тренажёр SQLite для начинающих. Включает 16 коротких уроков, три сквозных проекта, подсказки и словарь, повторение, локальное сохранение гостевого прогресса, синхронизацию по коду группы и кабинет преподавателя.

## Локальный запуск

Нужен Node.js 22.13 или новее и pnpm 11.

```sh
pnpm install --frozen-lockfile
pnpm exec drizzle-kit generate
pnpm build
node --import ./scripts/sites-env.mjs ./node_modules/wrangler/bin/wrangler.js d1 execute DB --local --config dist/server/wrangler.json --persist-to .wrangler/state --file drizzle/0000_ambitious_kylun.sql
pnpm dev
```

Для локального входа администратора создайте `.dev.vars` с `ADMIN_PASSWORD_HASH`, равным SHA-256 выбранного пароля. Файл игнорируется Git. На размещённом сайте значение задаётся в Sites как secret с тем же ключом.

## Хранение и проверка

SQLite WASM запускает учебные запросы отдельно в браузере. D1 хранит группы, псевдонимы, прогресс, проектные снимки и частые ошибки. Гостевые данные хранятся в IndexedDB. Запросы ученика не выполняются в серверной базе.
