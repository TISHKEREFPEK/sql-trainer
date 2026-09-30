import { allTasks, glossary, glossaryLong } from '../../lib/course';
import { parkingSpotsSeed, teacherDatasetSeeds } from '../../lib/teacher-data';
import { parkingDatabaseSeed } from '../../lib/parking-dataset';
import { createTaskVariants, variantForLearner } from '../../lib/task-variants';
import type { Task } from '../../lib/course';
export { allTasks, glossary, glossaryLong };
const seeds: Record<string, string> = { ...teacherDatasetSeeds, 'parking-spots': parkingSpotsSeed, 'parking-database': parkingDatabaseSeed };
export function sourceSeed(task: Task) { return task.seedProfile ? seeds[task.seedProfile] : task.seed; }
export function assign(task: Task, slot: number) {
  const seed = sourceSeed(task);
  const assigned = variantForLearner(task, slot, createTaskVariants(task, seed));
  // Alternatives already rewrite their inline seed; seed profiles need the source seed.
  return { ...assigned, seed: task.seedProfile ? seed : assigned.seed };
}
export function publicTask(task: Task, revision: number, restricted: boolean) {
  return { id: task.id, module: task.module, title: task.title, prompt: task.prompt,
    concept: task.concept, example: task.example, starter: task.starter, terms: task.terms,
    hints: task.hints, project: task.project, variantIndex: task.variantIndex || 1,
    revision, restricted, definitions: Object.fromEntries(task.terms.map(term => [term, glossary[term] || ''])) };
}
