"use client";

import { useEffect, useState } from "react";
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from "./ui/accordion";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "./ui/tabs";
import type { Task } from "../lib/course";

const pageSize = 8;
const categoryFor = (task: Task) => task.project ? "projects" : /^\d/.test(task.module) ? "course" : "practice";
const sectionFor = (task: Task) => task.project || task.module;
const projectNames: Record<string, string> = { library: "Библиотека", shop: "Магазин", classes: "Запись на занятия" };

export function TaskNavigation({ tasks, selected, completed, onSelect }: {
  tasks: Task[]; selected: string; completed: Set<string>; onSelect: (id: string) => void;
}) {
  const current = tasks.find(task => task.id === selected) || tasks[0];
  const [category, setCategory] = useState(current ? categoryFor(current) : "course");
  const [section, setSection] = useState(current ? sectionFor(current) : "");
  const [page, setPage] = useState(0);
  const [expanded, setExpanded] = useState(false);
  useEffect(() => {
    if (!current) return;
    setCategory(categoryFor(current)); setSection(sectionFor(current));
    const items = tasks.filter(task => sectionFor(task) === sectionFor(current));
    setPage(Math.floor(items.findIndex(task => task.id === current.id) / pageSize));
  }, [selected, tasks, current]);
  const sections = [...new Set(tasks.filter(task => categoryFor(task) === category).map(sectionFor))];
  const done = tasks.filter(task => completed.has(task.id)).length;
  const percent = Math.round(done / Math.max(1, tasks.length) * 100);
  return <aside className="sidebar">
    <div className="sidebar-progress"><div><strong>Задания</strong><span>{done} из {tasks.length}</span></div><div className="progress-track" role="progressbar" aria-label="Выполнено заданий" aria-valuemin={0} aria-valuemax={tasks.length} aria-valuenow={done}><div style={{ width: `${percent}%` }} /></div><button className="mobile-nav-toggle" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>{expanded ? "Скрыть список" : "Выбрать задание"}</button></div>
    <div className={`sidebar-navigation ${expanded ? "expanded" : ""}`}>
      <Tabs value={category} onValueChange={value => { setCategory(value); setSection(""); setPage(0); }}>
        <TabsList className="task-nav-tabs" aria-label="Тип заданий"><TabsTrigger value="course">Курс</TabsTrigger><TabsTrigger value="practice">Практика</TabsTrigger><TabsTrigger value="projects">Проекты</TabsTrigger></TabsList>
        <TabsContent value={category} className="side-scroll">
          <Accordion type="single" collapsible value={section} onValueChange={value => { setSection(value); setPage(0); }}>
            {sections.map(key => {
              const items = tasks.filter(task => categoryFor(task) === category && sectionFor(task) === key);
              const pageCount = Math.ceil(items.length / pageSize);
              const activePage = Math.min(page, pageCount - 1);
              return <AccordionItem value={key} key={key} className="nav-section"><AccordionTrigger className="nav-section-heading"><span>{projectNames[key] || key.replace(/^\d+\s*·\s*/, "")}</span><small>{items.filter(task => completed.has(task.id)).length}/{items.length}</small></AccordionTrigger><AccordionContent className="nav-section-content">
                {items.slice(activePage * pageSize, (activePage + 1) * pageSize).map(item => {
                  const index = items.findIndex(task => task.id === item.id);
                  const locked = !!item.project && index > 0 && !completed.has(items[index - 1].id);
                  return <button key={item.id} className={`lesson-link ${item.id === selected ? "active" : ""}`} aria-current={item.id === selected ? "step" : undefined} disabled={locked} onClick={() => { onSelect(item.id); setExpanded(false); }}><span className={`lesson-number ${completed.has(item.id) ? "done" : ""}`}>{completed.has(item.id) ? "✓" : String(tasks.findIndex(task => task.id === item.id) + 1).padStart(2, "0")}</span><span>{item.title}</span></button>;
                })}
                {pageCount > 1 && <div className="nav-pagination"><button aria-label="Предыдущие задания" disabled={activePage === 0} onClick={() => setPage(activePage - 1)}>Назад</button><span>{activePage + 1} / {pageCount}</span><button aria-label="Следующие задания" disabled={activePage + 1 === pageCount} onClick={() => setPage(activePage + 1)}>Далее</button></div>}
              </AccordionContent></AccordionItem>;
            })}
          </Accordion>
        </TabsContent>
      </Tabs>
    </div>
  </aside>;
}
