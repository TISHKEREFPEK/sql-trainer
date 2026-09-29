CREATE TABLE `learner_task_variants` (
	`learner_id` text NOT NULL,
	`task_id` text NOT NULL,
	`task_json` text NOT NULL,
	`assigned_at` integer NOT NULL,
	PRIMARY KEY(`learner_id`, `task_id`),
	FOREIGN KEY (`learner_id`) REFERENCES `learners`(`id`) ON UPDATE no action ON DELETE no action
);
