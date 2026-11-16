import json,pathlib,subprocess,concurrent.futures,re
from pypdf import PdfReader
from inventory import BUILD
r=PdfReader(BUILD/'pilot.pdf');d=r.named_destinations
starts={name:r.get_destination_page_number(d[name.removesuffix('.md').lower()+'--file'])+1 for name in ['00-book-release.md','00-scope.md','I-01-architecture.md','II-02-values.md','II-04-format.md','II-05-indexes.md','II-06-catalog.md','II-16-development.md','A-01-sources.md','A-02-experiments.md','A-03-reconciliation-audit.md']}
p=BUILD/'pilot.native.txt';subprocess.run(['pdftotext','-layout',str(BUILD/'pilot.pdf'),str(p)],check=True)
texts=p.read_text(encoding='utf-8').split('\f');selected={}
def add(p,why):
    if 1<=p<=len(r.pages):selected.setdefault(p,[]).append(why)
for name,offset in [('00-scope.md',0),('00-scope.md',1),('I-01-architecture.md',0),('II-02-values.md',3),('A-01-sources.md',1),('A-02-experiments.md',1),('A-03-reconciliation-audit.md',1),('A-03-reconciliation-audit.md',8),('II-06-catalog.md',2)]:add(starts[name]+offset,name+' stress sample')
for i,page in enumerate(r.pages):
    if float(page.mediabox.width)>800:add(i+1,'A3 diagram')
for probe,why in [('Figure F-221','Widest II-16 six-column index'),('E0 = 7f','Longest hex example'),('w0=00000000','Hex offsets'),('Figure F-38','Wide catalog table')]:
    for i,text in enumerate(texts):
        if re.sub(r'\s+','',probe) in re.sub(r'\s+','',text):
            add(i+1,why);add(i+2,why+' continuation');break
out=BUILD/'qa/pilot';out.mkdir(parents=True,exist_ok=True)
plan=dict(pages=len(r.pages),chapter_pages=starts,full_resolution=[dict(page=p,reasons=why,image=f'full-{p:04}.png') for p,why in sorted(selected.items())])
(BUILD/'pilot-inspection-plan.json').write_text(json.dumps(plan,indent=2),encoding='utf-8')
def render(item):
    p=item['page'];subprocess.run(['pdftoppm','-f',str(p),'-l',str(p),'-scale-to','1800','-png','-singlefile',str(BUILD/'pilot.pdf'),str(out/f'full-{p:04}')],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:list(pool.map(render,plan['full_resolution']))
print(plan)
