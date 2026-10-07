window.SQLThemes = (() => {
  const presets = {
    light:{name:'Светлая',background:'#EDF2F4',surface:'#FFFFFF',accent:'#137C80',editor:'#20343D'},
    mist:{name:'Туман',background:'#E9EDF5',surface:'#F7F9FF',accent:'#5267AA',editor:'#28334F'},
    paper:{name:'Бумага',background:'#EFECE5',surface:'#FFFCF5',accent:'#71613D',editor:'#36362F'},
    dark:{name:'Тёмная',background:'#20262C',surface:'#2A323A',accent:'#6BC9BF',editor:'#1A2026'},
    onyx:{name:'Оникс',background:'#101010',surface:'#191919',accent:'#B9A6EB',editor:'#121212'},
    midnight:{name:'Полночь',background:'#141D30',surface:'#1D2A42',accent:'#89AEFA',editor:'#10192A'}
  };
  const rgb = hex => [1,3,5].map(i=>parseInt(hex.slice(i,i+2),16));
  const luminance = hex => rgb(hex).map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4;}).reduce((sum,v,i)=>sum+v*[.2126,.7152,.0722][i],0);
  const contrast = (a,b) => {const x=luminance(a),y=luminance(b);return (Math.max(x,y)+.05)/(Math.min(x,y)+.05);};
  const mix = (a,b,t) => '#'+rgb(a).map((v,i)=>Math.round(v*(1-t)+rgb(b)[i]*t).toString(16).padStart(2,'0')).join('');
  const ink = bg => {const preferred=contrast(bg,'#EEEEEE')>contrast(bg,'#20343D')?'#EEEEEE':'#20343D';return contrast(bg,preferred)>=4.5?preferred:contrast(bg,'#FFFFFF')>contrast(bg,'#000000')?'#FFFFFF':'#000000';};
  const resolve = theme => ({...presets[theme?.preset] || presets.light,...theme?.colors});
  function apply(theme) {
    const c=resolve(theme),text=ink(c.surface),editorInk=ink(c.editor),accentInk=contrast(c.accent,'#FFFFFF')>contrast(c.accent,'#101010')?'#FFFFFF':'#101010';
    const errorBg=mix(c.surface,'#B54444',.13), errorColor=luminance(c.surface)<.25?'#FFADAD':'#A72E38';
    const vars={background:c.background,surface:c.surface,accent:c.accent,editor:c.editor,ink:text,muted:contrast(mix(text,c.surface,.28),c.surface)>=4.5?mix(text,c.surface,.28):text,line:mix(c.surface,text,.18),soft:mix(c.surface,c.accent,.12),hover:mix(c.surface,c.accent,.08),subtle:mix(c.surface,text,.035),'on-accent':accentInk,'editor-ink':editorInk,'editor-muted':contrast(mix(editorInk,c.editor,.25),c.editor)>=4.5?mix(editorInk,c.editor,.25):editorInk,'editor-line':mix(c.editor,editorInk,.2),'accent-ink':contrast(c.accent,mix(c.surface,c.accent,.12))>=4.5?c.accent:text,error:contrast(errorColor,errorBg)>=4.5?errorColor:ink(errorBg),'error-bg':errorBg};
    for(const [key,value]of Object.entries(vars)) document.documentElement.style.setProperty('--'+key,value);
    document.documentElement.style.colorScheme=luminance(c.surface)<.25?'dark':'light';
  }
  return {presets,resolve,apply};
})();
