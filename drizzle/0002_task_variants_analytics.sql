CREATE TABLE `learner_task_stats` (
	`learner_id` text NOT NULL,
	`task_id` text NOT NULL,
	`started_at` integer NOT NULL,
	`completed_at` integer,
	`seconds_spent` integer DEFAULT 0 NOT NULL,
	`check_count` integer DEFAULT 0 NOT NULL,
	`error_count` integer DEFAULT 0 NOT NULL,
	`last_activity_at` integer NOT NULL,
	PRIMARY KEY(`learner_id`, `task_id`),
	FOREIGN KEY (`learner_id`) REFERENCES `learners`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_learner_task_stats_task` ON `learner_task_stats` (`task_id`);--> statement-breakpoint
CREATE TABLE `teacher_task_variants` (
	`task_id` text NOT NULL,
	`variant_index` integer NOT NULL,
	`variant_json` text NOT NULL,
	`updated_at` integer NOT NULL,
	PRIMARY KEY(`task_id`, `variant_index`)
);
