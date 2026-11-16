import argparse,collections,json,pathlib,re,subprocess,sys,unicodedata,hashlib
import xml.etree.ElementTree as ET
from pypdf import PdfReader
from PIL import Image,ImageOps,ImageDraw
from inventory import BUILD,ROOT,manifest,verify
from build import nodes,plain
def norm(s):return re.sub(r'\s+','',''.join(c for c in unicodedata.normalize('NFKC',s) if c not in '\u00ad\u200b\u21aa'))
def inspect_pdf(mode):
    p=BUILD/(mode+'.pdf');r=PdfReader(p)
    pages=[page.extract_text(extraction_mode='plain') or '' for page in r.pages]
    (BUILD/(mode+'.extracted.txt')).write_text('\n\f\n'.join(pages),encoding='utf-8')
    joined=norm(''.join(pages));structure=json.loads((BUILD/'structure.json').read_text(encoding='utf-8'))
    dest=r.named_destinations;missing=[];links=collections.Counter();bad=[];uris=[]
    for i,page in enumerate(r.pages):
        for a in page.get('/Annots',[]):
            a=a.get_object();action=a.get('/A',{});d=a.get('/Dest') or action.get('/D')
            if d is not None:
                links['internal']+=1
                if isinstance(d,str) and d not in dest:bad.append(dict(page=i+1,destination=str(d)))
            if action.get('/URI'):uris.append(str(action['/URI']))
    included=manifest['book_content_order'] if mode=='book' else ['00-book-release.md','00-scope.md','I-01-architecture.md','I-02-values.md','I-05-indexes.md','I-11-sql.md','II-02-values.md','II-04-format.md','II-05-indexes.md','II-06-catalog.md','II-16-development.md','A-01-sources.md','A-02-experiments.md','A-03-reconciliation-audit.md']
    expected=[a for a in structure['anchors'] if a['file'] in included]
    if mode=='book':missing=[a for a in expected if a['destination'] not in dest]
    chapter_pages={name:r.get_destination_page_number(dest[pathlib.Path(name).stem.lower()+'--file'])+1 for name in included if pathlib.Path(name).stem.lower()+'--file' in dest}
    start_mismatch=[]
    if mode=='book':
        for name in included:
            first=next(h for h in structure['headings'] if h['file']==name and h['level']==1)
            target=next(a['destination'] for a in structure['anchors'] if a.get('reference_label')==first['id'])
            page=r.get_destination_page_number(dest[target])+1
            if chapter_pages.get(name)!=page:start_mismatch.append(dict(file=name,file_start=chapter_pages.get(name),first_heading=page))
    head_missing=[];note_missing=[];code_missing=[];math=[];source_uris=[];numeric_tokens=set();source_citation_ids=set();code_lines_checked=0;notes_checked=0
    if mode=='book':
        for h in structure['headings']:
            if norm(h['text']) not in joined:head_missing.append(h)
        for name in included:
            doc=json.loads((BUILD/'ast'/(name+'.json')).read_text(encoding='utf-8'))
            for n in nodes(doc):
                if n['t']=='Link' and re.match(r'^[a-zA-Z][a-zA-Z0-9+.-]*:',n['c'][2][0]):source_uris.append(n['c'][2][0])
                if n['t'] in ('Str','Code','CodeBlock'):
                    value=n['c'] if n['t']=='Str' else n['c'][1]
                    if n['t']!='CodeBlock' or 'mermaid' not in n['c'][0][1]:
                        numeric_tokens.update(re.findall(r'\b0[xX][0-9a-fA-F_]+\b|\b[0-9a-fA-F]{16,}\b',value))
                        source_citation_ids.update(re.findall(r'\b(?:S-MS-|S-JACK-|S-WEB-)[A-Z0-9-]+\b',value))
                if n['t']=='Note':
                    notes_checked+=1
                    s=plain(n['c']);probe=norm(s)[:85]
                    if probe and probe not in joined:note_missing.append(dict(file=name,text=s[:150]))
                if n['t']=='CodeBlock' and 'mermaid' not in n['c'][0][1]:
                    for line in n['c'][1].splitlines():
                        if len(norm(line))>15:
                            code_lines_checked+=1
                            if norm(line) not in joined:code_missing.append(dict(file=name,line=line))
                if n['t']=='Math':math.append(n['c'])
    log=(BUILD/(mode+'.log')).read_text(encoding='utf-8',errors='replace')
    bbox=BUILD/(mode+'.bbox.html')
    subprocess.run(['pdftotext','-bbox-layout',str(p),str(bbox)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    tree=ET.parse(bbox);edge=[]
    for i,page in enumerate(tree.findall('.//{*}page')):
        width=float(page.attrib['width']);height=float(page.attrib['height'])
        if int(r.pages[i].get('/Rotate',0))%180:width,height=height,width
        words=[w for w in page.findall('.//{*}word') if float(w.attrib['xMin'])<20 or float(w.attrib['xMax'])>width-20 or float(w.attrib['yMin'])<12 or float(w.attrib['yMax'])>height-12]
        if words:edge.append(dict(page=i+1,words=[dict(text=w.text,**w.attrib) for w in words]))
    result=dict(page_count=len(r.pages),page_sizes=dict(collections.Counter(f"{float(p.mediabox.width):.1f}x{float(p.mediabox.height):.1f}" for p in r.pages)),named_destination_count=len(dest),missing_destinations=missing,broken_pdf_links=bad,internal_link_annotations=links['internal'],external_link_annotations=len(uris),chapter_pages=chapter_pages,heading_search_misses=head_missing,footnote_search_misses=note_missing,code_line_search_misses=code_missing,math_count=len(math),page_edge_flags=edge,missing_glyphs=re.findall(r'Missing character:[^\n]*',log),overfull_hbox=re.findall(r'Overfull \\hbox[^\n]*',log),overfull_vbox=re.findall(r'Overfull \\vbox[^\n]*',log),ignored_longtable_glue_messages=log.count('Infinite glue shrinkage found in box being split'),warnings=re.findall(r'(?:LaTeX|Package [\w-]+) Warning:[^\n]*',log),source_hashes_match=verify()['all_match'])
    result.update(chapter_order_matches=list(chapter_pages)==included and list(chapter_pages.values())==sorted(chapter_pages.values()),footnote_occurrences_checked=notes_checked,code_lines_checked=code_lines_checked,unique_external_urls_in_source=len(set(source_uris)),external_urls_missing_from_pdf=sorted(set(source_uris)-set(uris)),numeric_literals_checked=len(numeric_tokens),numeric_literal_search_misses=sorted(t for t in numeric_tokens if norm(t) not in joined),citation_ids_checked=len(source_citation_ids),citation_id_search_misses=sorted(t for t in source_citation_ids if norm(t) not in joined))
    result['undefined_reference_warnings']=re.findall(r'LaTeX Warning: (?:Hyper reference|Reference)[^\n]*undefined[^\n]*',log)
    result['undefined_reference_summary']='There were undefined references' in log
    result['chapter_start_heading_mismatches']=start_mismatch
    result['underfull_hbox']=re.findall(r'Underfull \\hbox[^\n]*',log)
    result['underfull_vbox']=re.findall(r'Underfull \\vbox[^\n]*',log)
    result['pdf_sha256']=hashlib.sha256(p.read_bytes()).hexdigest()
    (BUILD/(mode+'.qa.json')).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v if not isinstance(v,list) else len(v) for k,v in result.items()},ensure_ascii=True,indent=2))
    return result
def sheets(mode):
    out=BUILD/'qa'/mode;out.mkdir(parents=True,exist_ok=True)
    assert out.resolve().is_relative_to((BUILD/'qa').resolve())
    for pattern in ('page-*.jpg','contact-*.jpg'):
        for old in out.glob(pattern):old.unlink()
    subprocess.run(['pdftoppm','-jpeg','-scale-to','360','-jpegopt','quality=78',str(BUILD/(mode+'.pdf')),str(out/'page')],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    files=sorted(out.glob('page-*.jpg'))
    for start in range(0,len(files),30):
        group=files[start:start+30];sheet=Image.new('RGB',(1500,1860),'#cbd0d5');draw=ImageDraw.Draw(sheet)
        for j,file in enumerate(group):
            image=Image.open(file);image.thumbnail((284,282));x=(j%5)*300+(300-image.width)//2;y=(j//5)*310+20
            sheet.paste(image,(x,y));draw.text(((j%5)*300+12,(j//5)*310+3),f'Page {start+j+1}',fill='black')
        sheet.save(out/f'contact-{start//30+1:03}.jpg',quality=90)
    print('contact sheets:',(len(files)+29)//30)
if __name__=='__main__':
    a=argparse.ArgumentParser();a.add_argument('mode',choices=['pilot','book']);a.add_argument('--sheets',action='store_true');args=a.parse_args()
    inspect_pdf(args.mode)
    if args.sheets:sheets(args.mode)
