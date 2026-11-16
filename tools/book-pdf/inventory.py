import pathlib, json, hashlib, re, collections, zipfile
ROOT=pathlib.Path(__file__).resolve().parents[2]
BUILD=pathlib.Path(__file__).resolve().parent
SRC=BUILD/'input/extracted'
ARCHIVE=BUILD/'input/calcdb-book.v1.1-final.zip'
EXPECTED_ARCHIVE_SHA='67a51f6905384e9c2addd5d1ad90905e2c310e398309730dc7b79c52b31e4ce9'
if not SRC.exists():
    with zipfile.ZipFile(ARCHIVE) as z: z.extractall(SRC)
manifest=json.loads((SRC/'book-manifest.v1.1.json').read_text(encoding='utf-8'))
def sha(b): return hashlib.sha256(b).hexdigest()
def verify():
    rows=[]
    for f in manifest['files']:
        data=(SRC/f['name']).read_bytes()
        rows.append(dict(name=f['name'],bytes=len(data),sha256=sha(data),matches=len(data)==f['bytes'] and sha(data)==f['sha256']))
    import subprocess
    expected={f['name'] for f in manifest['files']}|{'book-manifest.v1.1.json'}
    actual={p.name for p in SRC.iterdir() if p.is_file()}
    with zipfile.ZipFile(ARCHIVE) as z:
        members=z.namelist()
        archive_checks=[dict(name=n,matches=z.read(n)==(SRC/n).read_bytes()) for n in sorted(expected)]
        archive_ok=sha(ARCHIVE.read_bytes())==EXPECTED_ARCHIVE_SHA and len(members)==41 and len(set(members))==41 and set(members)==expected and all(x['matches'] for x in archive_checks) and z.testzip() is None
    tag_commit=subprocess.check_output(['git','rev-parse','book-v1.1^{}'],cwd=ROOT).decode().strip()
    tag_checks=[]
    for n in sorted(expected):
        data=subprocess.check_output(['git','show','book-v1.1:docs/book/'+n],cwd=ROOT)
        tag_checks.append(dict(name=n,sha256=sha(data),matches=data==(SRC/n).read_bytes()))
    result=dict(files=rows,all_match=all(r['matches'] for r in rows) and archive_ok and all(x['matches'] for x in tag_checks),directory_members=sorted(actual),unlisted_directory_members=sorted(actual-expected),missing_directory_members=sorted(expected-actual),archive_checks=archive_checks,archive_status='verified' if archive_ok else 'FAILED',archive_sha256=sha(ARCHIVE.read_bytes()),archive_members=members,archive_expected_sha256=EXPECTED_ARCHIVE_SHA,tag_commit=tag_commit,tag_checks=tag_checks,manifest_sha256=sha((SRC/'book-manifest.v1.1.json').read_bytes()))
    return result
if __name__=='__main__':
    v=verify(); (BUILD/'source-hashes.before.json').write_text(json.dumps(v,indent=2),encoding='utf-8'); assert v['all_match']
    inv=[]; chars=collections.Counter(); diagrams=[]; captions=[]; tables=[]; codes=[]; footnotes=collections.defaultdict(list)
    for name in manifest['book_content_order']:
        text=(SRC/name).read_text(encoding='utf-8'); chars.update(c for c in text if ord(c)>127)
        for m in re.finditer(r'^```mermaid[^\n]*\n(.*?)^```',text,re.M|re.S):
            key=f'diagram-{len(diagrams)+1:03}'; (BUILD/'assets').mkdir(exist_ok=True)
            (BUILD/'assets'/f'{key}.mmd').write_text(m[1],encoding='utf-8'); diagrams.append(dict(key=key,file=name,line=text[:m.start()].count('\n')+1,source=m[1]))
        for m in re.finditer(r'^.*(?:Figure\s+F-\d+).*$',text,re.M):
            if re.match(r'^\s*(?:\*\*)?Figure ',m[0]): captions.append(dict(file=name,line=text[:m.start()].count('\n')+1,text=m[0],ids=re.findall(r'F-\d+',m[0])))
        for m in re.finditer(r'(?m)^\|.*(?:\n\|.*)+',text):
            lines=m[0].splitlines();headered=bool(re.match(r'^\|[\s:|\-]+\|\s*$',lines[1]));tables.append(dict(file=name,line=text[:m.start()].count('\n')+1,rows=len(lines)-(2 if headered else 0),headered=headered,cols=lines[0].count('|')-1,max_line=max(map(len,lines))))
        for m in re.finditer(r'^```([^\n]*)\n(.*?)^```',text,re.M|re.S):
            if m[1]!='mermaid': codes.append(dict(file=name,line=text[:m.start()].count('\n')+1,language=m[1],lines=len(m[2].splitlines()),width=max(map(len,m[2].splitlines()),default=0)))
        for label in re.findall(r'^\[\^([^]]+)\]:',text,re.M): footnotes[label].append(name)
    inv=dict(files=manifest['book_content_order'],mermaid=diagrams,captions=captions,tables=tables,code=codes,unicode=dict(chars),repeated_footnotes={k:v for k,v in footnotes.items() if len(v)>1})
    (BUILD/'inventory.json').write_text(json.dumps(inv,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(hashes_match=v['all_match'],archive=v['archive_status'],diagrams=len(diagrams),captions=len(captions),tables=len(tables),widest_tables=sorted(tables,key=lambda t:(t['cols'],t['max_line']),reverse=True)[:4],longest_code=sorted(codes,key=lambda t:t['width'],reverse=True)[:3],unicode=''.join(chars),repeated_footnote_labels=len(inv['repeated_footnotes'])),ensure_ascii=False,indent=2))
