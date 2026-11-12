import {mkdirSync,copyFileSync,readFileSync,writeFileSync} from 'node:fs';
mkdirSync('dist/server',{recursive:true});mkdirSync('dist/.openai',{recursive:true});
writeFileSync('dist/server/index.js',readFileSync('worker/index.js','utf8').replace("/*UI*/''",()=>JSON.stringify(readFileSync('worker/ui.html','utf8'))));copyFileSync('.openai/hosting.json','dist/.openai/hosting.json');
console.log('Built Worker ESM');
