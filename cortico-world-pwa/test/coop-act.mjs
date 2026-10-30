import world from '../index.js'
const host={pushEvent:async()=>({cursor:0}),store:{},modelFacts:{},blob:()=>null,log:()=>{}}
const ctx={id:world.id,cfg:{...world.defaults(),enabled:true,watch:false,auto:false},timezone:'Asia/Shanghai',botName:'x',botDir:process.cwd(),packageDir:process.cwd(),dataDir:process.cwd(),repoRoot:process.cwd(),secret:()=>'',storeSecret:()=>{},persist:async()=>{},restart:async()=>{}}
const w=world.create(ctx); const t=new Map(w.tools().map(x=>[x.name,x]))
const call=async(n,a={})=>JSON.parse(await t.get('sts2_'+n).handler(a,{role:'t'}))
await w.start(host)
// 选涅奥第一个选项，进入地图
let s=await call('view',{full:true})
if(s.screen==='EVENT'){ await call('act',{action:'choose_event_option',index:0}); s=await call('view',{full:true}) }
console.log('after event:', s.screen, 'floor=', s.run?.floor, 'actions=', (s.actions||[]).join(','))
if(s.screen==='MAP' && s.map?.options?.length){ await call('act',{action:'choose_map_node',index:s.map.options[0].i}); await new Promise(r=>setTimeout(r,2500)); s=await call('view',{full:true}) }
console.log('after map:', s.screen, 'floor=', s.run?.floor)
const hand=s.combat?.hand??[]
const card=hand.find(c=>c.playable && c.target_type==='AnyEnemy')
const enemy=(s.combat?.enemies??[]).find(e=>e.alive)
if(card && enemy){
  const r=await call('act',{action:'play_card',card_ref:card.ref,target_id:enemy.combat_id})
  console.log('play:', card.name, '->', r.status, 'enemy', r.state?.combat?.enemies?.[0]?.hp)
}
await w.stop()
