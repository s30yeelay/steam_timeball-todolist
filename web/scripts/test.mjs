import assert from 'node:assert/strict';import {readFileSync} from 'node:fs';
export class Bucket {constructor(){this.data=new Map();this.rev=0}async get(key){const o=this.data.get(key);return o?{etag:o.etag,json:async()=>JSON.parse(o.value)}:null}async put(key,value,options={}){const old=this.data.get(key),cond=options.onlyIf;if(cond&&((cond.etagMatches&&cond.etagMatches!==old?.etag)||(cond.etagDoesNotMatch==='*'&&old)))return null;const etag=String(++this.rev);this.data.set(key,{value,etag});return {etag}}async delete(key){this.data.delete(key)}async list({prefix,limit}){return {objects:[...this.data.keys()].filter(k=>k.startsWith(prefix)).sort().slice(0,limit).map(key=>({key}))}}}
const {default:worker}=await import('data:text/javascript;base64,'+Buffer.from(readFileSync('dist/server/index.js')).toString('base64'));
if(process.argv.includes('--serve')){
 const {createServer}=await import('node:http');const bucket=new Bucket();
 createServer(async(req,res)=>{const chunks=[];for await(const b of req)chunks.push(b);const headers=new Headers(req.headers);headers.set('oai-authenticated-user-id','local-preview');const request=new Request('http://127.0.0.1:5179'+req.url,{method:req.method,headers,body:['GET','HEAD'].includes(req.method)?undefined:Buffer.concat(chunks)});const response=await worker.fetch(request,{BUCKET:bucket});res.writeHead(response.status,Object.fromEntries(response.headers));let text=await response.text();if(req.url==='/')text=text.replace('<body>','<body><div style="padding:6px;text-align:center;background:#e6cba7;font-size:12px">本地界面预览 · 临时数据，不是云端任务</div>');res.end(text)}).listen(5179,'127.0.0.1',()=>console.log('Local preview: http://127.0.0.1:5179'));await new Promise(()=>{});
}else{
 const env={BUCKET:new Bucket()};let checks=0;const check=(v)=>{assert.ok(v);checks++};
 async function call(user,path,data){const response=await worker.fetch(new Request('https://quest.test/api/'+path,{method:data?'POST':'GET',headers:{...(user?{'oai-authenticated-user-id':user}:{}),'content-type':'application/json',origin:'https://quest.test'},body:data?JSON.stringify(data):undefined}),env);return {status:response.status,data:await response.json()}}
 check((await call(null,'state')).status===401);
 let a=await call('alice','tasks',{title:'private A',reward:55});check(a.status===200&&a.data.tasks.length===1);const id=a.data.tasks[0].id;
 check((await call('bob','state')).data.tasks.length===0);
 check((await call('bob','task',{id,action:'complete'})).status===404);
 check((await call('alice','task',{id,action:'complete'})).data.earned===55);
 check((await call('bob','feed')).data.length===0);
 await call('alice','share',{taskId:id,text:'only this message'});const feed=(await call('bob','feed')).data;check(feed.length===1&&!JSON.stringify(feed).includes('private A')&&!JSON.stringify(feed).includes('alice'));
 check((await call('bob','unshare',{id:feed[0].id})).status===403);await call('alice','unshare',{id:feed[0].id});check((await call('bob','feed')).data.length===0);
 check((await call('alice','task',{id,action:'edit',title:'cheat',reward:90})).status===400);
 const b=await call('alice','tasks',{title:'second',reward:55});const second=b.data.tasks.find(t=>t.title==='second').id;check((await call('alice','task',{id:second,action:'complete'})).data.earned===90);
 const c=await call('alice','tasks',{title:'IELTS',kind:'ielts',reward:45});const ielts=c.data.tasks.find(t=>t.kind==='ielts').id;check((await call('alice','task',{id:ielts,action:'complete'})).status===400);
 const pair=await call('alice','device/create',{name:'test PC'});check(pair.data.deviceKey.length===64);
 let poll=await call(null,'device/poll',{deviceKey:pair.data.deviceKey,spentSeconds:600,ieltsCompletedDay:c.data.day});check(poll.status===200&&poll.data.earnedMinutes===90);check((await call('alice','state')).data.tasks.find(t=>t.id===ielts).completedAt);
 check((await call('bob','state')).data.devices.length===0);
 const own=(await call('alice','state')).data;await call('alice','device/revoke',{id:own.devices[0].id});check((await call(null,'device/poll',{deviceKey:pair.data.deviceKey})).status===401);
 const d=await call('alice','tasks',{title:'delete only me',reward:1});const deleteId=d.data.tasks.find(t=>t.title==='delete only me').id;const after=await call('alice','task',{id:deleteId,action:'delete'});check(after.data.tasks.length===3);
 check((await call('alice','tasks',{title:'bad',reward:91})).status===400);
 const csrf=await worker.fetch(new Request('https://quest.test/api/tasks',{method:'POST',headers:{'oai-authenticated-user-id':'alice',origin:'https://evil.test'},body:'{"title":"bad"}'}),env);check(csrf.status===403);
 const html=readFileSync('worker/ui.html','utf8');const js=html.match(/<script>([\s\S]*?)<\/script>/)[1];new Function(js);check(html.includes('id="quick"')&&html.includes('id="search"'));
 const parserSource=html.split('// quick-parser-start')[1].split('// quick-parser-end')[0];const parseQuick=new Function(parserSource+';return parseQuick;')();
 check(parseQuick('看完ros的第三课 45').title==='看完ros的第三课'&&parseQuick('看完ros的第三课 45').reward===45);
 check(parseQuick('学ROS3').title==='学ROS3'&&parseQuick('学ROS3').reward===15);
 check(parseQuick('阅读 +45').reward===45&&parseQuick('阅读+45').reward===45);
 check(parseQuick('阅读 ４５').reward===45&&parseQuick('阅读 45分钟').reward===45&&parseQuick('阅读 45min').reward===45);
 check(parseQuick('阅读 0').reward===0&&parseQuick('阅读 90').reward===90);
 for(const bad of ['阅读 91','阅读 -5','阅读 4.5','45','']){let rejected=false;try{parseQuick(bad)}catch{rejected=true}check(rejected);}
 const shorthand=parseQuick('看完ros的第三课 45');const created=await call('short-input','tasks',shorthand);check(created.data.earned===0&&created.data.tasks[0].reward===45);
 check((await call('short-input','task',{id:created.data.tasks[0].id,action:'complete'})).data.earned===45);
 const planTasks=await call('planner','tasks',{items:[{title:'Share this unfinished plan',reward:45,criteria:'private completion detail'},{title:'Do not share this',reward:20,criteria:'another private detail'}]});
 const selected=planTasks.data.tasks[0].id;
 check((await call('planner','share-plan',{taskIds:[]})).status===400);
 check((await call('bob','share-plan',{taskIds:[selected]})).status===403);
 check((await call('planner','share-plan',{taskIds:[selected],text:'My selected plan'})).status===200);
 const planFeed=(await call('bob','feed')).data,sharedPlan=planFeed.find(p=>p.kind==='plan');
 check(sharedPlan.plan.length===1&&sharedPlan.plan[0].title==='Share this unfinished plan'&&!sharedPlan.plan[0].completed);
 check(!JSON.stringify(sharedPlan).includes('private completion detail')&&!JSON.stringify(sharedPlan).includes('Do not share this')&&!JSON.stringify(sharedPlan).includes('reward'));
 check((await call('bob','task',{id:selected,action:'complete'})).status===404);
 await call('planner','task',{id:selected,action:'complete'});
 check(!(await call('bob','feed')).data.find(p=>p.id===sharedPlan.id).plan[0].completed);
 check((await call('bob','unshare',{id:sharedPlan.id})).status===403);
 await call('planner','unshare',{id:sharedPlan.id});check(!(await call('bob','feed')).data.some(p=>p.id===sharedPlan.id));
 const RealDate=globalThis.Date;let now=RealDate.parse('2026-11-09T23:59:00+08:00');
 globalThis.Date=class extends RealDate{constructor(...args){super(...(args.length?args:[now]))}static now(){return now}};
 try {
  const carry=await call('carry','tasks',{items:[{title:'yesterday',day:'2026-11-08',reward:45},{title:'third day',day:'2026-11-07',reward:20},{title:'expired',day:'2026-11-06',reward:15},{title:'tomorrow',day:'2026-11-10',reward:10}]});
  check(carry.data.day==='2026-11-09'&&carry.data.openFrom==='2026-11-07');
  const ids=Object.fromEntries(carry.data.tasks.map(t=>[t.title,t.id]));
  check((await call('carry','task',{id:ids.yesterday,action:'complete'})).data.earned===45);
  check((await call('carry','task',{id:ids['third day'],action:'complete'})).data.earned===65);
  check((await call('carry','task',{id:ids.expired,action:'complete'})).status===400);
  check((await call('carry','task',{id:ids.tomorrow,action:'complete'})).status===400);
  check((await call('carry','task',{id:ids.yesterday,action:'complete'})).data.earned===20);
  check((await call('carry','task',{id:ids.yesterday,action:'complete'})).data.earned===65);
  now=RealDate.parse('2026-11-10T00:01:00+08:00');const next=(await call('carry','state')).data;
  check(next.earned===0&&next.tasks.length===4&&next.openFrom==='2026-11-08');
  check((await call('carry','task',{id:ids.yesterday,action:'complete'})).status===400);
  check((await call('carry','task',{id:ids.tomorrow,action:'complete'})).data.earned===10);
  const language=await call('carry-ielts','tasks',{items:[{title:'old IELTS',day:'2026-11-08',kind:'ielts',reward:25},{title:'new IELTS',day:'2026-11-10',kind:'ielts',reward:45}]});
  const device=(await call('carry-ielts','device/create',{name:'carry PC'})).data;
  const evidence={deviceKey:device.deviceKey,ieltsCompletedDay:'2026-11-10'};
  check((await call(null,'device/poll',evidence)).data.earnedMinutes===25);
  check((await call(null,'device/poll',evidence)).data.earnedMinutes===25);
  const evidenceState=(await call('carry-ielts','state')).data;
  check(evidenceState.tasks[0].completedAt&&!evidenceState.tasks[1].completedAt);
  // Test actual UI predicate: default today includes carry-over and today's completions.
  const helperSource=html.slice(html.indexOf('function completedDay('),html.indexOf('function render(){'));
  const onToday=new Function('state',helperSource+';return onToday;')({day:'2026-11-10',openFrom:'2026-11-08'});
  check(onToday({day:'2026-11-08',completedAt:null})&&!onToday({day:'2026-11-07',completedAt:null}));
  check(onToday({day:'2026-11-08',completedAt:'2026-11-10T00:00:00+08:00'})&&!onToday({day:'2026-11-08',completedAt:'2026-11-09T23:59:00+08:00'}));
 }finally{globalThis.Date=RealDate}
 console.log('PASS: '+checks+' checks (isolation, quota, device revocation, shorthand, three-day carry-over, Shanghai rollover and completion-day credit).');
}
