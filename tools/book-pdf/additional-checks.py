import json,re,pathlib,collections,subprocess,hashlib
from pypdf import PdfReader
from inventory import BUILD,SRC,manifest
from build import nodes,plain
from qa import norm
r=PdfReader(BUILD/'book.pdf');dest=r.named_destinations
digest=hashlib.sha256((BUILD/'book.pdf').read_bytes()).hexdigest()
assert json.loads((BUILD/'book.qa.json').read_text(encoding='utf-8')).get('pdf_sha256')==digest,'Run qa.py on the current PDF first.'
text=(BUILD/'book.extracted.txt').read_text(encoding='utf-8');joined=norm(text)
caps=[];miss=[]
for name in manifest['book_content_order']:
    doc=json.loads((BUILD/'ast'/(name+'.json')).read_text(encoding='utf-8'))
    for n in nodes(doc):
        if n['t']=='Para' and n['c'] and n['c'][0]['t']=='Strong' and re.match(r'^Figure F-\d+',plain(n)):
            value=plain(n['c'][0]);caps.append(dict(file=name,text=value))
            if norm(value) not in joined:miss.append(dict(file=name,text=value))
ledger=json.loads((BUILD/'ast/A-01-sources.md.json').read_text(encoding='utf-8'))
table=next(n for n in nodes(ledger) if n['t']=='Table');rows=[row for body in table['c'][4] for row in body[3]]
assert len(rows)==618 and all(len(row[1])==7 for row in rows)
ids=[plain(row[1][0][4]) for row in rows];assert len(ids)==len(set(ids))
missing_ids=[value for value in ids if norm(value) not in joined]
structure=json.loads((BUILD/'structure.json').read_text(encoding='utf-8'))
def outlines(items):
    for item in items:
        if isinstance(item,list):yield from outlines(item)
        else:yield item
bookmarks=list(outlines(r.outline));titles={norm(str(x.title)) for x in bookmarks}
bookmark_misses=[h for h in structure['headings'] if h['level']==1 and norm(h['text']) not in titles]
scope=[x for x in structure['internal_links'] if x['original'].startswith('00-scope.md')]
scope_missing=[x for x in scope if x['destination'] not in dest]
stubs=[h for h in structure['headings'] if h['file']=='II-08-transactions.md' and re.match(r'^(25|26|27|28|29)\.',h['text'])]
fonts=subprocess.check_output(['pdffonts',str(BUILD/'book.pdf')]).decode('utf-8')
(BUILD/'book.fonts.txt').write_text(fonts,encoding='utf-8')
font_rows=[line.split() for line in fonts.splitlines()[2:] if line.strip()]
embedded=all(row[-5]=='yes' for row in font_rows)
v=dict(landscape_pages=sum(int(p.get('/Rotate',0))%180!=0 for p in r.pages),a3_pages=sum(float(p.mediabox.width)>800 for p in r.pages),captions_checked=len(caps),caption_search_misses=miss,ledger_records=len(rows),ledger_fields=7,ledger_unique_ids=len(set(ids)),ledger_id_search_misses=missing_ids,scope_links_checked=len(scope),scope_links_missing=scope_missing,retired_II08_sections=stubs,all_fonts_embedded=embedded,font_resources_checked=len(font_rows))
v.update(bookmark_count=len(bookmarks),chapter_bookmark_misses=bookmark_misses)
v['pdf_sha256']=digest
assert len(caps)==226 and not miss and not missing_ids and not scope_missing and len(stubs)==5 and embedded and not bookmark_misses
(BUILD/'additional-checks.json').write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
print({k:len(v) if isinstance(v,list) else v for k,v in v.items()})
