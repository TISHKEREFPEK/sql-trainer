CREATE TABLE `teacher_tasks` (
	`id` text PRIMARY KEY NOT NULL,
	`task_json` text NOT NULL,
	`deleted` integer DEFAULT 0 NOT NULL,
	`updated_at` integer NOT NULL
);
