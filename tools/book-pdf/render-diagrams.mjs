import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const dir=path.dirname(fileURLToPath(import.meta.url));
const inv=JSON.parse(fs.readFileSync(path.join(dir,'inventory.json'),'utf8'));
const cli=path.join(dir,'node_modules/@mermaid-js/mermaid-cli/src/cli.js');
let next=0,failed=[];
async function worker(){while(next<inv.mermaid.length){const d=inv.mermaid[next++];for(const ext of ['svg','pdf']){const target=path.join(dir,'assets',d.key+'.'+ext);if(fs.existsSync(target))continue;
 await new Promise(resolve=>{const log=fs.openSync(path.join(dir,'assets',d.key+'.'+ext+'.log'),'w');const child=spawn(process.execPath,[cli,'-i',path.join(dir,'assets',d.key+'.mmd'),'-o',target,'-p',path.join(dir,'puppeteer.json'),'-c',path.join(dir,'mermaid.json')],{stdio:['ignore',log,log],windowsHide:true});child.on('exit',code=>{fs.closeSync(log);if(code)failed.push([d.key,ext,code]);resolve();});});}console.log(d.key);}}
await Promise.all(Array.from({length:3},worker));
fs.writeFileSync(path.join(dir,'diagram-results.json'),JSON.stringify({count:inv.mermaid.length,failed},null,2));
if(failed.length)process.exitCode=1;
