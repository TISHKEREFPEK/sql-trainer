import type { Task } from "./course";

const textPools: Record<string,string[]> = {
  city:["Москва","Казань","Тула","Самара","Уфа","Пермь"],
  brand:["Cadillac","Toyota","Volvo","Honda","Suzuki","BMW","Nissan","Kia"],
  status:["new","paid","cancelled","shipped"],
  zone:["Стандарт","Крытая","VIP","Открытая"],
  movement_type:["production","sale","quality_check","damaged"],
  promo_type:["discount","clearance","seasonal"],
  plate:["А123ВС77","М002РТ150","Е678ХМ190","К777КК99"],
  person:["Аня","Борис","Вика","Глеб","Ирина","Олег"],
};

function escapeRegExp(value:string){return value.replace(/[.*+?^${}()|[\]\\]/g,"\\$&");}
function replaceWords(value:string,from:string,to:string){return value.replace(new RegExp(`(?<![\\p{L}\\p{N}_])${escapeRegExp(from)}(?![\\p{L}\\p{N}_])`,"giu"),to);}
function updateText(task:Task,from:string,to:string){
  task.prompt=replaceWords(task.prompt,from,to);
  task.title=replaceWords(task.title,from,to);
  task.example=replaceWords(task.example,from,to);
  task.hints=[replaceWords(task.hints[0],from,to),replaceWords(task.hints[1],from,to)];
  const genitives:Record<string,string>={Москва:"Москвы",Казань:"Казани",Тула:"Тулы",Самара:"Самары",Уфа:"Уфы",Пермь:"Перми"};
  if(genitives[from]&&genitives[to])task.prompt=replaceWords(task.prompt,genitives[from],genitives[to]);
  if(/^\d+$/.test(from)&&/^\d+$/.test(to)){
    const forms=["нуля","одного","двух","трёх","четырёх","пяти","шести","семи","восьми","девяти","десяти"];
    const before=forms[Number(from)],after=forms[Number(to)];
    if(before&&after)task.prompt=task.prompt.replace(new RegExp(`(?<![\\p{L}])${escapeRegExp(before)}(?![\\p{L}])`,"giu"),after);
  }
}
function columnForLiteral(sql:string,literal:string){
  const index=sql.indexOf(literal);
  if(index<0)return "";
  return /([a-zA-Z_][\w.]*)\s*(?:=|<>|!=|>=|<=|>|<|LIKE|IN)\s*$/i.exec(sql.slice(Math.max(0,index-80),index))?.[1]?.split(".").at(-1)?.toLowerCase()||"";
}
function stringReplacement(current:string,column:string,variant:number,seed:string){
  const pool=textPools[column]||[];
  const matches=pool.filter(value=>value!==current&&seed.includes(`'${value}'`));
  if(matches.length)return matches[(variant-1)%matches.length];
  if(/^\d{4}-\d\d-\d\d$/.test(current)){
    const day=Number(current.slice(-2))+variant;
    const next=`${current.slice(0,8)}${String(day).padStart(2,"0")}`;
    return next;
  }
  if(/^[А-ЯЁA-Z]\d{3}[А-ЯЁA-Z]{2}\d{2,3}$/i.test(current)){
    const plates=[...seed.matchAll(/'([^']+)'/g)].map(match=>match[1]).filter(value=>/^[А-ЯЁA-Z]\d{3}[А-ЯЁA-Z]{2}\d{2,3}$/i.test(value)&&value!==current);
    if(plates.length)return plates[(variant-1)%plates.length];
  }
  if(/^[A-ZА-ЯЁ]{2,6}-\d+$/i.test(current)){
    const ids=[...seed.matchAll(/'([^']+)'/g)].map(match=>match[1]).filter(value=>/^[A-ZА-ЯЁ]{2,6}-\d+$/i.test(value)&&value!==current);
    if(ids.length)return ids[(variant-1)%ids.length];
  }
  if(current.length>1)return `${current} · вариант ${variant}`;
  return undefined;
}
function replaceLiterals(task:Task,variant:number,seed:string){
  const original=task.solution;
  const literals=[...new Set(original.match(/'(?:''|[^'])*'|\b\d+(?:\.\d+)?\b/g)||[])];
  let solution=original;
  let changed=false;
  for(const token of literals){
    const quoted=token.startsWith("'");
    const value=quoted?token.slice(1,-1).replaceAll("''","'"):token;
    const cityForms:Record<string,string>={Москва:"Москвы",Казань:"Казани",Тула:"Тулы",Самара:"Самары",Уфа:"Уфы",Пермь:"Перми"};
    const numberForms=["нуля","одного","двух","трёх","четырёх","пяти","шести","семи","восьми","девяти","десяти"];
    const mentioned=[task.prompt,...task.hints].some(text=>quoted?(text.includes(value)||text.includes(cityForms[value]||"\u0000")):new RegExp(`(?<![\\p{L}\\p{N}_])${escapeRegExp(value)}(?![\\p{L}\\p{N}_])`,"u").test(text)||/^\d+$/.test(value)&&text.toLocaleLowerCase("ru").includes(numberForms[Number(value)]||"\u0000"));
    if(!mentioned)continue;
    const column=columnForLiteral(original,token);
    const limitTarget=!!quoted===false&&new RegExp(`\\bLIMIT\\s+${escapeRegExp(value)}\\b`,'i').test(original);
    const insertTarget=!quoted&&task.mode==="state"&&/\bINSERT\s+INTO\b[\s\S]*?\bVALUES\s*\(/i.test(original);
    if(!quoted&&!column&&!limitTarget&&!insertTarget)continue;
    if(quoted&&column==="movement_type"&&value==="quality_check")continue;
    const replacement=quoted?stringReplacement(value,column,variant,seed):String(Number(value)+variant);
    if(!replacement||replacement===value)continue;
    const nextToken=quoted?`'${replacement.replaceAll("'","''")}'`:replacement;
    solution=solution.replaceAll(token,nextToken);
    updateText(task,value,replacement);
    changed=true;
  }
  task.solution=solution;
  return changed;
}
function topLevelSelect(sql:string){
  let depth=0,quote="";
  for(let index=0;index<sql.length;index++){
    const char=sql[index];
    if(quote){if(char===quote){if(sql[index+1]===quote){index++;continue;}quote="";}continue;}
    if(char==="'"||char==='"'||char==="`"){quote=char;continue;}
    if(char==="("){depth++;continue;} if(char===")"){depth--;continue;}
    if(depth===0&&/^SELECT\b/i.test(sql.slice(index))){
      const from=/\bFROM\b/i.exec(sql.slice(index+6));
      if(from)return {start:index,end:index+6+from.index,expr:sql.slice(index+6,index+6+from.index).trim()};
    }
  }
  return null;
}
function structuralVariant(task:Task,variant:number){
  const suffix=`_v${variant}`;
  let next=task.solution;
  let from="",to="";
  const index=/\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+([a-zA-Z_]\w*)/i.exec(next);
  const trigger=/\bCREATE\s+TRIGGER\s+([a-zA-Z_]\w*)/i.exec(next);
  const alter=/\bALTER\s+TABLE\s+([a-zA-Z_]\w*)\s+ADD\s+COLUMN\s+([a-zA-Z_]\w*)/i.exec(next);
  if(index){from=index[1];to=`${from}${suffix}`;next=next.replace(from,to);}
  else if(trigger){from=trigger[1];to=`${from}${suffix}`;next=next.replace(from,to);task.prompt+=` Создай триггер с именем ${to}.`;}
  else if(alter){from=alter[2];to=`${from}${suffix}`;next=next.replace(alter[0],alter[0].replace(from,to));}
  else if(/\bCREATE\s+TABLE\b/i.test(next)&&/\bTEXT\b/i.test(next)){
    const column=/\b([a-zA-Z_]\w*)\s+TEXT\b/i.exec(next);
    if(column){from=`${column[1]} TEXT`;to=`${column[1]} VARCHAR(${80+variant*40})`;next=next.replace(from,to);task.prompt=task.prompt.replace(new RegExp(`\\b${column[1]}\\s+TEXT\\b`,"i"),to);task.example=task.example.replace(new RegExp(`\\b${column[1]}\\s+TEXT\\b`,"i"),to);task.solution=next;return true;}
  } else if(/\bCREATE\s+TABLE\b/i.test(next)){
    const integerColumns=[...next.matchAll(/\b([a-zA-Z_]\w*)\s+INTEGER\b/g)];
    const eligibleColumns=integerColumns.filter(match=>!/(?:PRIMARY\s+KEY|AUTOINCREMENT)/i.test(next.slice(match.index!,next.indexOf(",",match.index!)<0?next.length:next.indexOf(",",match.index!))));
    const column=eligibleColumns[(variant-1)%Math.max(1,eligibleColumns.length)];
    if(column){from=`${column[1]} INTEGER`;to=`${column[1]} REAL`;next=next.replace(from,to);task.prompt=task.prompt.replace(new RegExp(`\\b${column[1]}\\s+INTEGER\\b`),to);task.example=task.example.replace(new RegExp(`\\b${column[1]}\\s+INTEGER\\b`),to);task.solution=next;return true;}
  }
  if(from){updateText(task,from,to);task.solution=next;task.example=task.example.replace(new RegExp(escapeRegExp(from),"g"),to);return true;}
  const aliases=[...next.matchAll(/\bAS\s+(?!SELECT\b)([a-zA-Z_]\w*)/gi)];
  if(aliases.length){
    const previous=aliases[0][1];const alias=`${previous}${suffix}`;
    next=next.replace(new RegExp(`\\b${escapeRegExp(previous)}\\b`,"g"),alias);
    task.prompt+=` В этой версии назови результат «${alias}».`;
    task.solution=next;task.example=next;return true;
  }
  const clause=topLevelSelect(next);
  if(clause&&clause.expr&&clause.expr!=="*"&&!/\bAS\b/i.test(clause.expr)){
    const alias=`answer${variant}`;
    const expressionStart=clause.start+6;
    const prefix=next.slice(0,expressionStart),suffixSql=next.slice(expressionStart);
    const commaIndex=(()=>{let depth=0,quote="";for(let i=0;i<clause.expr.length;i++){const c=clause.expr[i];if(quote){if(c===quote)quote="";continue;}if(c==="'"||c==='"'){quote=c;continue;}if(c==="(")depth++;else if(c===")")depth--;else if(c===","&&depth===0)return i;}return -1;})();
    const first=commaIndex<0?clause.expr:clause.expr.slice(0,commaIndex).trimEnd();
    const at=suffixSql.indexOf(clause.expr);
    task.solution=`${prefix}${suffixSql.slice(0,at)}${first} AS ${alias}${commaIndex<0?"":clause.expr.slice(commaIndex)}${suffixSql.slice(at+clause.expr.length)}`;
    task.example=task.solution;task.prompt+=` В этой версии назови первый столбец «${alias}».`;return true;
  }
  if(task.mode==="state"&&!task.project&&/\bCREATE\s+TABLE\s+[a-zA-Z_]\w*/i.test(next)){
    const match=/\bCREATE\s+TABLE\s+([a-zA-Z_]\w*)/i.exec(next)!;from=match[1];to=`${from}${suffix}`;task.solution=next.replace(from,to);task.example=task.example.replace(from,to);task.prompt=replaceWords(task.prompt,from,to);return true;
  }
  if(task.mode!=="state"){
    const star=/\bSELECT\s+\*\s+FROM\s+([a-zA-Z_]\w*)/i.exec(next);
    if(star){const limit=8+variant*5;task.solution=`SELECT * FROM ${star[1]} ORDER BY rowid LIMIT ${limit};`;task.example=task.solution;task.prompt=`Покажи первые ${limit} строк таблицы ${star[1]} в исходном порядке.`;task.ordered=true;return true;}
  }
  const deleteThreshold=/\bDELETE\s+FROM\s+([a-zA-Z_]\w*)\s+WHERE\s+([a-zA-Z_]\w*)\s*<\s*(-?\d+(?:\.\d+)?)/i.exec(next);
  if(deleteThreshold){const threshold=Number(deleteThreshold[3])-variant;task.solution=next.replace(deleteThreshold[0],`DELETE FROM ${deleteThreshold[1]} WHERE ${deleteThreshold[2]} < ${threshold}`);task.example=task.solution;task.prompt=`Удали из ${deleteThreshold[1]} строки, где ${deleteThreshold[2]} меньше ${threshold}.`;return true;}
  const order=/\bORDER\s+BY\s+([\w.]+)(?:\s+(ASC|DESC))?/i.exec(next);
  if(order){const direction=order[2]?.toUpperCase()==="DESC"?"ASC":"DESC";task.solution=next.replace(order[0],`ORDER BY ${order[1]} ${direction}`);task.example=task.solution;task.prompt+=` В этой версии отсортируй результат по ${order[1]} ${direction==="ASC"?"по возрастанию":"по убыванию"}.`;task.ordered=true;return true;}
  return false;
}

export function createTaskVariants(task:Task,seedOverride=task.seed):Task[]{
  if(task.variantEligible===false)return [];
  const variants=[1,2].map(variant=>{
    const copy={...task,terms:[...task.terms],hints:[...task.hints] as [string,string]};
    if(!replaceLiterals(copy,variant,seedOverride))structuralVariant(copy,variant);
    if(copy.solution===task.solution){
      const fallback=structuralVariant(copy,variant);
      if(!fallback&&copy.mode!=="state"){const select=topLevelSelect(copy.solution);if(select&&select.expr!=="*"){const alias=`answer${variant}`;copy.solution=copy.solution.replace(select.expr,`${select.expr} AS ${alias}`);copy.example=copy.solution;copy.prompt+=` Назови первый столбец «${alias}».`;}}
    }
    if(copy.prompt===task.prompt&&copy.solution!==task.solution){
      const predicate=/\bWHERE\s+([\s\S]*?)(?:\bGROUP\s+BY\b|\bORDER\s+BY\b|\bLIMIT\b|;|$)/i.exec(copy.solution)?.[1]?.trim();
      const limit=/\bLIMIT\s+(\d+)/i.exec(copy.solution)?.[1];
      if(predicate)copy.prompt+=` Условие этой версии: ${predicate}.`;
      else if(limit)copy.prompt+=` В этой версии выведи ${limit} строк.`;
      else copy.prompt+=` Для этой версии используй указанную формулировку и порядок результата.`;
    }
    copy.variantEligible=true;
    return copy;
  });
  if(variants[0].solution===variants[1].solution&&task.mode!=="state"){
    const second=variants[1];
    const order=/\bORDER\s+BY\s+([\w.]+)(?:\s+(ASC|DESC))?/i.exec(second.solution);
    if(order){const direction=order[2]?.toUpperCase()==="DESC"?"ASC":"DESC";second.solution=second.solution.replace(order[0],`ORDER BY ${order[1]} ${direction}`);}
    else second.solution=second.solution.replace(/;?\s*$/," ORDER BY 1 DESC;");
    second.example=second.solution;
    second.prompt+=` В этой версии отсортируй результат по первому столбцу по убыванию.`;
    second.ordered=true;
  }
  return variants;
}

export function variantForLearner(task:Task,learnerId:string,alternatives:Task[]){
  if(!alternatives.length)return task;
  const stableKey=task.project?`${learnerId}:${task.project}`:`${learnerId}:${task.id}`;
  let hash=2166136261;
  for(const character of stableKey)hash=Math.imul(hash^character.charCodeAt(0),16777619);
  const selected=(hash>>>0)%(alternatives.length+1);
  return selected===0?task:alternatives[selected-1]||task;
}
