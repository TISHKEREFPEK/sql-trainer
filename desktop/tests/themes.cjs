const {test}=require('node:test');
const assert=require('node:assert/strict');
const {readFileSync}=require('node:fs');
const vm=require('node:vm');
const luminance=hex=>[1,3,5].map(i=>parseInt(hex.slice(i,i+2),16)/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4).reduce((sum,v,i)=>sum+v*[.2126,.7152,.0722][i],0);
const contrast=(a,b)=>{const x=luminance(a),y=luminance(b);return(Math.max(x,y)+.05)/(Math.min(x,y)+.05);};
test('all presets and custom colors retain readable text, buttons and SQL',()=>{
  const values={};const context={window:{},document:{documentElement:{style:{setProperty:(key,value)=>values[key]=value}}}};
  vm.runInNewContext(readFileSync('ui/themes.js','utf8'),context);
  const themes=context.window.SQLThemes;
  assert.equal(Object.keys(themes.presets).length,6);
  const configurations=[...Object.keys(themes.presets).map(preset=>({preset})),...["#000000","#FFFFFF","#808080","#00FF00"].map(color=>({preset:'onyx',colors:{background:color,surface:color,accent:color,editor:color}}))];
  for(const configuration of configurations){
    themes.apply(configuration);
    for(const [text,bg]of [['ink','surface'],['muted','surface'],['editor-ink','editor'],['on-accent','accent'],['error','error-bg']]){
      assert.ok(contrast(values['--'+text],values['--'+bg])>=4.5,configuration.preset+': '+text);
    }
  }
});
