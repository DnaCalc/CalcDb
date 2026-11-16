import concurrent.futures,json,re,subprocess,hashlib
from pypdf import PdfReader
from inventory import BUILD,manifest
r=PdfReader(BUILD/'book.pdf')
q=json.loads((BUILD/'book.qa.json').read_text(encoding='utf-8')) if (BUILD/'book.qa.json').exists() else {}
if q.get('pdf_sha256')!=hashlib.sha256((BUILD/'book.pdf').read_bytes()).hexdigest():
    dest=r.named_destinations
    q=dict(chapter_pages={n:r.get_destination_page_number(dest[n.removesuffix('.md').lower()+'--file'])+1 for n in manifest['book_content_order']},page_edge_flags=[])
starts=q['chapter_pages']
native=BUILD/'book.native.txt'
subprocess.run(['pdftotext','-layout',str(BUILD/'book.pdf'),str(native)],check=True)
pages=native.read_text(encoding='utf-8').split('\f')[:len(r.pages)]
inv=json.loads((BUILD/'inventory.json').read_text(encoding='utf-8'))
selected={}
def add(p,reason):
    if 1<=p<=len(r.pages):selected.setdefault(p,[]).append(reason)
def find(probe,reason,start=1):
    norm=lambda s:re.sub(r'\s+','',s)
    for i,text in enumerate(pages[start-1:],start):
        if norm(probe) in norm(text):add(i,reason);return i
add(1,'Generated title');add(2,'Contents typography');add(starts['00-book-release.md']-1,'End of contents')
for name in ['00-book-release.md','00-scope.md','I-01-architecture.md','I-16-development.md','II-01-architecture.md','II-04-format.md','II-08-transactions.md','II-16-development.md','A-01-sources.md','A-02-experiments.md','A-03-reconciliation-audit.md','00-stage0-plan.md','book-record.md']:
    add(starts[name],name+' chapter boundary');add(starts[name]+1,name+' dense continuation/header')
add(len(r.pages),'Final page')
for i,p in enumerate(r.pages):
    if float(p.mediabox.width)>800:add(i+1,'A3 diagram')
    if int(p.get('/Rotate',0))%180 and ('II-16' in pages[i] or 'A-03' in pages[i]):add(i+1,'II-16 or audit landscape table')
for i,text in enumerate(pages):
    if re.search(r'25[. ]+Retired|29[. ]+Retired',text,re.I) and starts['II-08-transactions.md']<=i+1<starts['II-09-locking.md']:add(i+1,'Retired II-08 sections retained')
for code in sorted(inv['code'],key=lambda x:x['width'],reverse=True)[:3]:
    doc=json.loads((BUILD/'ast'/(code['file']+'.json')).read_text(encoding='utf-8'))
    from build import nodes
    lines=[line for n in nodes(doc) if n['t']=='CodeBlock' and 'mermaid' not in n['c'][0][1] for line in n['c'][1].splitlines()]
    line=max(lines,key=len);find(line[:75],'Long code/hex line: '+code['file'],starts[code['file']])
for i,text in enumerate(pages):
    if re.search(r'000000[0-9a-fA-F]{2}.*(?:[0-9a-fA-F]{2} ){8}',text):add(i+1,'Hex offset alignment');break
for probe,why in [('w0=00000000','Aligned numeric hex words'),('E0 = 7f','Longest hex example'),('中','Unicode CJK glyph'),('Encounter Attr Expression Flag','Eight-column catalog table'),('b2800c24c2','Previously flagged ledger hash')]:
    p=find(probe,why,starts['II-02-values.md'])
    if p and probe=='Encounter Attr Expression Flag':add(p-1,'Eight-column catalog caption');add(p+1,'Eight-column catalog continuation')
    if p and probe=='b2800c24c2':add(p+1,'Ledger hash paragraph continuation check')
for name in ['A-01-sources.md','A-02-experiments.md','A-03-reconciliation-audit.md']:
    start=starts[name];names=list(starts);j=names.index(name);end=starts[names[j+1]]-1
    add((start+end)//2,name+' middle');add(end,name+' final page')
labels={s:i+1 for i,s in enumerate(r.page_labels)}
log=(BUILD/'book.log').read_text(encoding='utf-8',errors='replace')
for m in re.finditer(r'(?:Overfull|Underfull) \\[hv]box[^\n]*',log):
    previous=list(re.finditer(r'\[(\d+)(?=[\s<\]])',log[:m.start()]))
    if previous:
        number=int(previous[-1][1])+1
        for delta in (-1,0,1):
            if str(number+delta) in labels:add(labels[str(number+delta)],m[0])
for flag in q['page_edge_flags']:add(flag['page'],'Page-edge warning')
out=BUILD/'qa/book';out.mkdir(parents=True,exist_ok=True)
assert out.resolve().is_relative_to(BUILD.resolve())
for old in out.glob('full-*.png'):old.unlink()
plan=[dict(page=p,reasons=why,image=f'full-{p:04}.png') for p,why in sorted(selected.items())]
(BUILD/'inspection-plan.json').write_text(json.dumps(dict(chapter_pages=starts,pages=plan),indent=2),encoding='utf-8')
def render(item):
    p=item['page'];subprocess.run(['pdftoppm','-f',str(p),'-l',str(p),'-scale-to','1800','-png','-singlefile',str(BUILD/'book.pdf'),str(out/f'full-{p:04}')],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:list(pool.map(render,plan))
print('Full-resolution pages:',[p['page'] for p in plan])
