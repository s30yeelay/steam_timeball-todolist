const day=()=>new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date());
const dateOf=value=>value&&!Number.isNaN(Date.parse(value))?new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date(value)):'';
const openFrom=()=>new Date(Date.parse(day()+'T00:00:00Z')-2*86400000).toISOString().slice(0,10);
const available=t=>t.day>=openFrom()&&t.day<=day();
const json=(data,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store','x-content-type-options':'nosniff'}});
const fail=(message,status=400)=>{throw Object.assign(new Error(message),{status})};
const clean=(v,n=240)=>String(v??'').trim().slice(0,n);
const digest=async text=>Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(text)))).map(b=>b.toString(16).padStart(2,'0')).join('');
const blank=()=>({tasks:[],nickname:'学习伙伴',devices:[],revision:0});
async function load(env,key){const o=await env.BUCKET.get(key);return {value:o?await o.json():blank(),etag:o?.etag};}
async function mutate(env,key,fn){for(let i=0;i<4;i++){const {value,etag}=await load(env,key);await fn(value);value.revision++;const o=await env.BUCKET.put(key,JSON.stringify(value),{onlyIf:etag?{etagMatches:etag}:{etagDoesNotMatch:'*'},httpMetadata:{contentType:'application/json'}});if(o)return value;}fail('其他设备更新了数据，请刷新重试',409);}
function earned(s){return Math.min(90,s.tasks.filter(t=>dateOf(t.completedAt)===day()).reduce((a,t)=>a+t.reward,0));}
async function body(req){const text=await req.text();if(text.length>32000)fail('内容过长',413);try{return JSON.parse(text)}catch{fail('请求格式不正确')}}
function task(t){const title=clean(t.title),reward=Number(t.reward??15),date=clean(t.day||day(),10);if(!title)fail('请填写任务');if(!Number.isInteger(reward)||reward<0||reward>90)fail('奖励应为0–90的整数');if(!/^\d{4}-\d{2}-\d{2}$/.test(date))fail('日期格式错误');return {id:crypto.randomUUID(),title,reward,day:date,kind:t.kind==='ielts'?'ielts':'manual',criteria:clean(t.criteria,500),createdAt:new Date().toISOString(),completedAt:null};}
async function view(env,uid){const {value}=await load(env,'users/'+uid);return {...value,day:day(),openFrom:openFrom(),earned:earned(value)};}
async function api(req,env,url){
 if(!env.BUCKET)fail('云端存储未连接',503);const route=url.pathname;
 if(route==='/api/device/poll'){
  if(req.method!=='POST')fail('Method not allowed',405);const input=await body(req),token=clean(input.deviceKey,100);if(!/^[a-f0-9]{64}$/.test(token))fail('无效设备凭据',401);
  const o=await env.BUCKET.get('devices/'+await digest(token));if(!o)fail('设备未配对',401);const link=await o.json();
  const state=await mutate(env,'users/'+link.uid,s=>{const d=s.devices.find(d=>d.id===link.id);if(!d)fail('设备已撤销',401);d.lastSeen=new Date().toISOString();d.day=day();d.spent=Math.max(0,Math.min(86400,Number(input.spentSeconds)||0));if(input.ieltsCompletedDay===day()){s.ieltsClaims=s.ieltsClaims||{};if(!s.ieltsClaims[day()]&&!s.tasks.some(t=>t.kind==='ielts'&&dateOf(t.completedAt)===day()&&t.basis==='本机教练完成记录')){const t=s.tasks.filter(t=>available(t)&&t.kind==='ielts'&&!t.completedAt).sort((a,b)=>a.day.localeCompare(b.day)||a.createdAt.localeCompare(b.createdAt))[0];if(t){t.completedAt=new Date().toISOString();t.basis='本机教练完成记录';s.ieltsClaims[day()]=t.id;}}}});
  return json({day:day(),earnedMinutes:earned(state),maximumMinutes:90,pollAfterSeconds:30});
 }
 const uid=req.headers.get('oai-authenticated-user-id');if(!uid)fail('请先登录自己的账号',401);const key='users/'+uid;
 if(req.method!=='GET'&&req.headers.get('origin')&&req.headers.get('origin')!==url.origin)fail('拒绝跨站请求',403);
 if(route==='/api/state'&&req.method==='GET')return json(await view(env,uid));
 if(route==='/api/feed'&&req.method==='GET'){const list=await env.BUCKET.list({prefix:'posts/',limit:60});const posts=await Promise.all(list.objects.map(async o=>{const v=await env.BUCKET.get(o.key);return v?await v.json():null}));return json(posts.filter(Boolean).map(({author,...p})=>({...p,mine:author===uid})));}
 const input=req.method==='POST'?await body(req):{};
 if(route==='/api/tasks'&&req.method==='POST'){const items=Array.isArray(input.items)?input.items:[input];if(items.length>50)fail('最多一次导入50条');const tasks=items.map(task);await mutate(env,key,s=>{if(s.tasks.length+tasks.length>5000)fail('任务数量达到上限');s.tasks.push(...tasks)});return json(await view(env,uid));}
 if(route==='/api/task'&&req.method==='POST'){await mutate(env,key,s=>{const t=s.tasks.find(t=>t.id===input.id);if(!t)fail('任务不存在',404);if(input.action==='delete'){if(t.completedAt)fail('先撤销完成再删除');s.tasks=s.tasks.filter(t=>t.id!==input.id);return;}if(input.action==='complete'){if(t.completedAt?dateOf(t.completedAt)!==day():!available(t))fail('任务可在创建当天及之后两天完成；历史完成记录不能跨日撤销');if(t.kind==='ielts')fail('雅思任务由已配对电脑核验教练完成记录，不能手动赚取');t.completedAt=t.completedAt?null:new Date().toISOString();t.basis=t.completedAt?'自我确认':null;}else if(input.action==='edit'){if(t.completedAt)fail('先撤销完成再修改');const revised=task({...t,...input});t.title=revised.title;t.criteria=revised.criteria;t.reward=revised.reward;}else fail('未知操作');});return json(await view(env,uid));}
 if(route==='/api/profile'&&req.method==='POST'){await mutate(env,key,s=>{s.nickname=clean(input.nickname,30)||'学习伙伴'});return json(await view(env,uid));}
 if(route==='/api/share'&&req.method==='POST'){const {value}=await load(env,key);if(!value.tasks.find(t=>t.id===input.taskId&&t.completedAt))fail('只能分享自己的已完成任务');const text=clean(input.text,600);if(!text)fail('请填写分享内容');const id=String(9999999999999-Date.now())+'-'+crypto.randomUUID();await env.BUCKET.put('posts/'+id,JSON.stringify({id,author:uid,nickname:value.nickname,text,createdAt:new Date().toISOString()}));return json({ok:true});}
 if(route==='/api/share-plan'&&req.method==='POST'){
  const ids=Array.isArray(input.taskIds)?[...new Set(input.taskIds)]:[];if(!ids.length||ids.length>50)fail('请选择1–50条自己的任务');
  const {value}=await load(env,key);const chosen=ids.map(id=>value.tasks.find(t=>t.id===id));if(chosen.some(t=>!t))fail('只能分享自己的任务',403);
  const text=clean(input.text,600)||'我的任务规划';const id=String(9999999999999-Date.now())+'-'+crypto.randomUUID();
  await env.BUCKET.put('posts/'+id,JSON.stringify({id,author:uid,nickname:value.nickname,kind:'plan',text,plan:chosen.map(t=>({title:t.title,day:t.day,completed:!!t.completedAt})),createdAt:new Date().toISOString()}));return json({ok:true});
 }
 if(route==='/api/unshare'&&req.method==='POST'){const id='posts/'+clean(input.id,80),o=await env.BUCKET.get(id);if(!o)fail('打卡不存在',404);if((await o.json()).author!==uid)fail('不能撤回别人的打卡',403);await env.BUCKET.delete(id);return json({ok:true});}
 if(route==='/api/device/create'&&req.method==='POST'){const token=Array.from(crypto.getRandomValues(new Uint8Array(32))).map(b=>b.toString(16).padStart(2,'0')).join(''),d={id:crypto.randomUUID(),name:clean(input.name,40)||'我的电脑',lastSeen:null};await mutate(env,key,s=>{if(s.devices.length>=5)fail('最多配对5台电脑');s.devices.push(d)});await env.BUCKET.put('devices/'+await digest(token),JSON.stringify({uid,id:d.id}));return json({deviceKey:token,site:url.origin});}
 if(route==='/api/device/revoke'&&req.method==='POST'){await mutate(env,key,s=>{s.devices=s.devices.filter(d=>d.id!==input.id)});return json(await view(env,uid));}
 fail('接口不存在',404);
}
export default {async fetch(req,env){try{const url=new URL(req.url);if(url.pathname.startsWith('/api/'))return await api(req,env,url);if(url.pathname==='/manifest.webmanifest')return json({name:'拾刻 · 每日工作台',short_name:'拾刻',start_url:'/',display:'standalone',theme_color:'#285c48',background_color:'#f4f5ef'});if(url.pathname!=='/')return new Response('Not found',{status:404});return new Response(UI,{headers:{'content-type':'text/html; charset=utf-8','cache-control':'no-store','referrer-policy':'same-origin','x-content-type-options':'nosniff','content-security-policy':"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'self'; base-uri 'self'"}});}catch(e){return json({error:e.status?e.message:'请求未完成，请稍后重试'},e.status||500)}}};
const UI=/*UI*/'';

