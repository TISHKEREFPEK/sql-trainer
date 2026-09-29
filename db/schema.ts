import { index, integer, primaryKey, sqliteTable, text, uniqueIndex } from "drizzle-orm/sqlite-core";

export const groups = sqliteTable("groups", {
  id: text("id").primaryKey(),
  name: text("name").notNull(),
  code: text("code").notNull(),
  createdAt: integer("created_at").notNull(),
}, table => [uniqueIndex("idx_groups_code").on(table.code)]);

export const learners = sqliteTable("learners", {
  id: text("id").primaryKey(),
  groupId: text("group_id").notNull().references(() => groups.id),
  name: text("name").notNull(),
  nameKey: text("name_key").notNull(),
  completedJson: text("completed_json").notNull().default("[]"),
  snapshotsJson: text("snapshots_json").notNull().default("{}"),
  updatedAt: integer("updated_at").notNull(),
}, table => [uniqueIndex("idx_learners_group_name").on(table.groupId, table.nameKey)]);

export const attempts = sqliteTable("attempts", {
  id: text("id").primaryKey(),
  learnerId: text("learner_id").notNull().references(() => learners.id),
  taskId: text("task_id").notNull(),
  message: text("message").notNull(),
  createdAt: integer("created_at").notNull(),
});

export const sessions = sqliteTable("sessions", {
  tokenHash: text("token_hash").primaryKey(),
  role: text("role").notNull(),
  learnerId: text("learner_id"),
  expiresAt: integer("expires_at").notNull(),
});

export const teacherTasks = sqliteTable("teacher_tasks", {
  id: text("id").primaryKey(),
  taskJson: text("task_json").notNull(),
  deleted: integer("deleted").notNull().default(0),
  updatedAt: integer("updated_at").notNull(),
});

export const teacherTaskVariants = sqliteTable("teacher_task_variants", {
  taskId: text("task_id").notNull(),
  variantIndex: integer("variant_index").notNull(),
  variantJson: text("variant_json").notNull(),
  updatedAt: integer("updated_at").notNull(),
}, table => [primaryKey({columns:[table.taskId,table.variantIndex]})]);

export const learnerTaskStats = sqliteTable("learner_task_stats", {
  learnerId: text("learner_id").notNull().references(() => learners.id),
  taskId: text("task_id").notNull(),
  startedAt: integer("started_at").notNull(),
  completedAt: integer("completed_at"),
  secondsSpent: integer("seconds_spent").notNull().default(0),
  checkCount: integer("check_count").notNull().default(0),
  errorCount: integer("error_count").notNull().default(0),
  lastActivityAt: integer("last_activity_at").notNull(),
}, table => [
  primaryKey({columns:[table.learnerId,table.taskId]}),
  index("idx_learner_task_stats_task").on(table.taskId),
]);
