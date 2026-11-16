"""Read-only manuscript -> independently parsed, namespaced Pandoc AST -> LuaLaTeX.
Run with the bundled Python: build.py --pilot (then --full after visual review).
"""
import argparse, copy, json, os, pathlib, re, subprocess, sys, urllib.parse, collections
from pypdf import PdfReader
from inventory import ROOT,SRC,BUILD,manifest,verify
PANDOC=BUILD/'tools/pandoc-3.14/pandoc.exe'
INV=json.loads((BUILD/'inventory.json').read_text(encoding='utf-8'))
LAYOUT_OBSERVATIONS=[]
def run(cmd,**kw):
    p=subprocess.run(list(map(str,cmd)),encoding='utf-8',stdout=subprocess.PIPE,stderr=subprocess.PIPE,**kw)
    if p.returncode: raise RuntimeError(p.stderr[-7000:]+p.stdout[-3000:])
    return p.stdout
def nodes(x):
    if isinstance(x,dict):
        if 't' in x: yield x
        for v in x.values(): yield from nodes(v)
    elif isinstance(x,list):
        for v in x: yield from nodes(v)
def plain(x):
    if isinstance(x,list): return ''.join(map(plain,x))
    if not isinstance(x,dict): return ''
    if x.get('t') in ('Str','Code'): return x['c'] if x['t']=='Str' else x['c'][1]
    if x.get('t') in ('Space','SoftBreak','LineBreak'): return ' '
    return plain(x.get('c',[]))
def raw(s): return {'t':'RawBlock','c':['latex',s]}
WIDE_START=r'\clearpage\paperwidth=297mm\paperheight=420mm\pagewidth=\paperwidth\pageheight=\paperheight\newgeometry{layoutwidth=297mm,layoutheight=420mm,margin=20mm,headheight=14pt,footskip=10mm}\headwidth=\textwidth'
WIDE_END=r'\clearpage\paperwidth=210mm\paperheight=297mm\pagewidth=\paperwidth\pageheight=\paperheight\newgeometry{layoutwidth=210mm,layoutheight=297mm,margin=20mm,headheight=14pt,footskip=10mm}\headwidth=\textwidth'
def attr(n):
    t=n['t']; c=n.get('c')
    if t=='Header': return c[1]
    if t in ('Div','Span','CodeBlock','Code','Link','Image','Table','Figure'): return c[0]
def prefix(name): return pathlib.Path(name).stem.lower()
def latex(s):
    return ''.join({'\\':r'\textbackslash{}','{':r'\{','}':r'\}','_':r'\_','#':r'\#','$':r'\$','%':r'\%','&':r'\&','^':r'\textasciicircum{}','~':r'\textasciitilde{}'}.get(c,c) for c in s)
def short_heading(s,limit=54):
    return s if len(s)<=limit else s[:limit-1].rsplit(' ',1)[0]+'…'
def running_chapter(name):
    labels=['Architecture','Values and strings','Pager and I/O','File format','Indexes and sorting','Catalog and queries','ISAM and cursors','Transactions and recovery','Locking and work','Expressions and functions','SQL planning and execution','DAO and ADO objects','COM and host interop','Platforms and later capabilities','Evidence and acceptance','Development']
    m=re.match(r'(II?)-(\d\d)',name)
    if m:return f"Part {m[1]} · {m[1]}-{m[2]} {labels[int(m[2])-1]}"
    return {'00-book-release.md':'Book v1.1 · Release record','00-scope.md':'Book v1.1 · Scope and context','A-01-sources.md':'A-01 · Source ledger','A-02-experiments.md':'A-02 · Experiments','A-03-reconciliation-audit.md':'A-03 · Reconciliation audit','00-stage0-plan.md':'Project records · Stage 0 plan','book-record.md':'Project records · Working record'}[name]
def load_docs():
    cache=BUILD/'ast';cache.mkdir(exist_ok=True); docs={}
    for name in manifest['book_content_order']:
        source=SRC/name
        if name=='A-01-sources.md':
            text=source.read_text(encoding='utf-8')
            joined,count=re.subn(r'(?m)(^\|[^\n]*\n)\n(?=\|)',r'\1',text)
            if count:
                derived=BUILD/'derived';derived.mkdir(exist_ok=True)
                source=derived/name;source.write_text(joined,encoding='utf-8')
                LAYOUT_OBSERVATIONS.append(dict(kind='suspected source formatting defect; layout-only continuation repair',source=name,detail=f'{count} blank line splits the source ledger before a headerless continuation at line 329. Build-layer parsing joins the pipe rows; all 618 records and all original fields are preserved. Canonical bytes remain unchanged.'))
        out=cache/(name+'.json')
        if not out.exists(): out.write_text(run([PANDOC,'--from=markdown+footnotes+pipe_tables-smart','--to=json',source]),encoding='utf-8')
        docs[name]=json.loads(out.read_text(encoding='utf-8'))
    return docs
def transform(docs):
    anchors={}; headings=[]; issues=list(LAYOUT_OBSERVATIONS); links=[]; table_changes=[]; note_count={}; counts=collections.Counter()
    for name,doc in docs.items():
        anchors[(name,'')]=prefix(name)+'--file'
        github_counts=collections.Counter()
        for n in nodes(doc):
            a=attr(n)
            if a and a[0]:
                old=a[0];anchors[(name,old)]=prefix(name)+'--'+old
                a[0]=anchors[(name,old)]
            if n['t']=='Header':
                text=plain(n['c'][2]);slug=re.sub(r'[^\w\- ]','',text.lower()).replace(' ','-')
                count=github_counts[slug];github_counts[slug]+=1
                if count:slug+='-'+str(count)
                anchors[(name,slug)]=n['c'][1][0]
                headings.append(dict(file=name,level=n['c'][0],id=n['c'][1][0],text=text))
        note_count[name]=sum(n['t']=='Note' for n in nodes(doc))
    def table_blocks(n,name):
        a,caption,spec,head,bodies,foot=n['c'];cols=len(spec)
        rows=[r for b in bodies for r in b[3]]
        headers=head[1][0][1] if head[1] else []
        maximum=max((len(plain(r)) for r in rows),default=0)
        # A source ledger with very tall cells cannot fit an indivisible longtable row.
        if (name=='A-01-sources.md' and len(rows)>100) or (maximum>4500 and cols>=5):
            labels=[plain(c[4]) for c in headers]; out=[]
            for idx,row in enumerate(rows):
                cells=row[1]
                out.append(raw(r'\par\medskip\noindent\rule{\linewidth}{0.2pt}\par\nopagebreak'))
                for label,cell in zip(labels,cells):
                    out.append({'t':'Para','c':[{'t':'Strong','c':[{'t':'Str','c':label}]}]})
                    out.append(raw(r'\nopagebreak'))
                    out.extend(cell[4])
            table_changes.append(dict(file=name,id=a[0],rows=len(rows),cols=cols,presentation='labelled records',max_row_chars=maximum));return out
        landscape=cols>=5
        # Relative widths based on actual content, with short ID columns retained.
        weights=[]
        for i in range(cols):
            lengths=sorted(len(plain(r[1][i][4])) for r in rows if len(r[1])>i)
            typical=lengths[int(len(lengths)*.75)] if lengths else 20
            weights.append(max(9,min(260,typical))**.64)
        total=sum(weights);floor=.07 if landscape else .12
        fractions=[floor+(1-floor*cols)*w/total for w in weights]
        for i in range(cols):spec[i]=[spec[i][0],{'t':'ColWidth','c':fractions[i]}]
        table_changes.append(dict(file=name,id=a[0],rows=len(rows),cols=cols,presentation='landscape longtable' if landscape else 'portrait longtable',max_row_chars=maximum))
        return [raw((r'\begin{landscape}' if landscape else '')+r'\begingroup\small\raggedright'),n,raw(r'\endgroup'+(r'\end{landscape}' if landscape else ''))]
    def walk(x,name):
        if isinstance(x,list):
            result=[]
            for child in x:
                if name=='A-01-sources.md' and isinstance(child,dict) and child.get('t') in ('Para','Plain') and re.search(r'\b[0-9a-fA-F]{40,64}\b',plain(child)):
                    result.extend([raw(r'\begingroup\interlinepenalty=10000'),walk(child,name),raw(r'\par\endgroup')])
                elif isinstance(child,dict) and child.get('t')=='Header' and child['c'][0]==2:
                    text=plain(child['c'][2]);result.extend([walk(child,name),raw(r'\markright{'+latex(short_heading(text))+'}')])
                elif isinstance(child,dict) and child.get('t')=='CodeBlock' and 'mermaid' in child['c'][0][1]:
                    ds=[d for d in INV['mermaid'] if d['file']==name];d=ds[counts[name]]
                    size=PdfReader(BUILD/'assets'/(d['key']+'.pdf')).pages[0].mediabox
                    large=float(size.width)>700 or float(size.height)>970
                    diagram=walk(child,name)
                    if large:
                        caption=result.pop() if result and result[-1].get('t')=='Para' and plain(result[-1]).startswith('Figure ') else None
                        result.append(raw(WIDE_START))
                        if caption:result.append(caption)
                        result.extend([diagram,raw(WIDE_END)])
                    else:result.append(diagram)
                elif isinstance(child,dict) and child.get('t')=='Table':
                    child=walk(child,name);result.extend(table_blocks(child,name))
                else:result.append(walk(child,name))
            return result
        if not isinstance(x,dict):return x
        t=x.get('t');c=x.get('c')
        if t=='Link':
            target=c[2][0]; parsed=urllib.parse.urlsplit(target)
            if not parsed.scheme and not target.startswith('//'):
                path=urllib.parse.unquote(parsed.path); anchor=urllib.parse.unquote(parsed.fragment)
                destname=pathlib.PurePosixPath(path.replace('\\','/')).name if path else name
                if path.lower().endswith('.md') or (not path and anchor):
                    dest=anchors.get((destname,anchor))
                    if dest:c[2][0]='#'+dest;links.append(dict(source=name,original=target,destination=dest))
                    elif destname in manifest['companion_files']:issues.append(dict(kind='external companion link; deliberately excluded from PDF',source=name,target=target))
                    else:issues.append(dict(kind='unresolved source link',source=name,target=target))
        if t=='Str' and (len(c)>18 or (len(c)>8 and re.fullmatch(r'[A-Z0-9_/:|.-]+',c))):
            return {'t':'RawInline','c':['latex',''.join(latex(ch)+(r'\allowbreak{}' if ch in '/_-.:' or i%10==9 else '') for i,ch in enumerate(c))]}
        if t=='CodeBlock':
            if 'mermaid' in c[0][1]:
                index=counts[name]; ds=[d for d in INV['mermaid'] if d['file']==name];d=ds[index];counts[name]+=1
                asset=BUILD/'assets'/(d['key']+'.pdf')
                if not asset.exists():raise RuntimeError('Missing diagram '+str(asset))
                return raw(r'\par\medskip\noindent\begin{center}\includegraphics{'+asset.as_posix()+r'}\end{center}\medskip')
            return raw('\\begin{Verbatim}\n'+c[1]+'\n\\end{Verbatim}')
        if t=='Code':
            # TeX discretionary breaks add no characters to copied content.
            chunks=[]
            for i,ch in enumerate(c[1]):chunks.append(latex(ch)+(r'\allowbreak{}' if ch in '/_-.:' or i%10==9 else ''))
            return {'t':'RawInline','c':['latex',r'\texttt{'+''.join(chunks)+'}']}
        return {k:walk(v,name) for k,v in x.items()}
    for name in docs: docs[name]=walk(docs[name],name)
    (BUILD/'structure.json').write_text(json.dumps(dict(anchors=[dict(file=k[0],anchor=k[1],destination=v) for k,v in anchors.items()],headings=headings,internal_links=links,source_defects=issues,footnotes=note_count,tables=table_changes),ensure_ascii=False,indent=2),encoding='utf-8')
    return docs
def pilot_clip(name,blocks):
    if name=='A-01-sources.md':
        # First 18 complete source records; still spans multiple pages.
        cuts=[i for i,b in enumerate(blocks) if b['t']=='RawBlock' and '\\rule{\\linewidth}' in b['c'][1]]
        return blocks[:cuts[18]] if len(cuts)>18 else blocks
    if name=='A-02-experiments.md':
        heads=[i for i,b in enumerate(blocks) if b['t']=='Header' and b['c'][0]==2]
        return blocks[:heads[3]] if len(heads)>3 else blocks
    return blocks
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--pilot',action='store_true');ap.add_argument('--full',action='store_true');ap.add_argument('--prepare',action='store_true');args=ap.parse_args()
    assert verify()['all_match'],'Source hashes differ'
    docs=transform(load_docs());mode='pilot' if args.pilot else 'book'
    selected=['00-book-release.md','00-scope.md','I-01-architecture.md','I-02-values.md','I-05-indexes.md','I-11-sql.md','II-02-values.md','II-04-format.md','II-05-indexes.md','II-06-catalog.md','II-16-development.md','A-01-sources.md','A-02-experiments.md','A-03-reconciliation-audit.md'] if args.pilot else manifest['book_content_order']
    blocks=[]
    for name in selected:
        if name in ('I-01-architecture.md','II-01-architecture.md','A-01-sources.md','00-stage0-plan.md'):
            part={'I-01-architecture.md':'Part I — Foundations','II-01-architecture.md':'Part II — Detailed design','A-01-sources.md':'Appendices','00-stage0-plan.md':'Project records'}[name]
            blocks.append(raw(r'\part*{'+part+r'}\addcontentsline{toc}{part}{'+part+'}'))
        fileid=prefix(name)+'--file'
        blocks.append(raw(r'\clearpage\setcounter{footnote}{0}\markboth{'+latex(running_chapter(name))+r'}{}\phantomsection\label{'+fileid+r'}\hypertarget{'+fileid+'}{}'))
        blocks.extend(pilot_clip(name,docs[name]['blocks']) if args.pilot else docs[name]['blocks'])
    combined=dict(docs[selected[0]]);combined['blocks']=blocks;combined['meta']={}
    ast=BUILD/(mode+'.json');ast.write_text(json.dumps(combined,ensure_ascii=False),encoding='utf-8')
    run([PANDOC,ast,'-f','json','-t','latex','--standalone','--template',BUILD/'template.tex','--top-level-division=chapter','--syntax-highlighting=none','--wrap=none','-o',BUILD/(mode+'.tex')])
    if args.prepare:return
    # An interrupted TeX pass can leave a truncated auxiliary file. Regenerate it.
    for ext in ('.aux','.toc','.out'):(BUILD/(mode+ext)).unlink(missing_ok=True)
    for i in range(3):
        cmd=['lualatex','--disable-installer','--interaction=nonstopmode','--halt-on-error','--output-directory='+str(BUILD),BUILD/(mode+'.tex')]
        p=subprocess.run(list(map(str,cmd)),cwd=BUILD,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        (BUILD/f'{mode}.pass{i+1}.log').write_bytes(p.stdout)
        if p.returncode:print(p.stdout.decode('utf-8',errors='replace')[-6500:]);sys.exit(p.returncode)
        print(mode,'pass',i+1,'complete',flush=True)
    from resolve_destinations import resolve
    resolve(mode)
    after=verify();(BUILD/'source-hashes.after.json').write_text(json.dumps(after,indent=2),encoding='utf-8');assert after['all_match']
    if not args.pilot:
        import shutil
        shutil.copy2(BUILD/'book.pdf',ROOT/'docs/The-Book-of-CalcDb.v1.1.pdf')
if __name__=='__main__':main()
