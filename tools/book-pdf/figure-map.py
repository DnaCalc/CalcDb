import json,re,collections
from inventory import BUILD,SRC,manifest
inv=json.loads((BUILD/'inventory.json').read_text(encoding='utf-8'))
figures=[]
for c in inv['captions']:
    if not c['text'].startswith('**Figure'):continue
    lines=(SRC/c['file']).read_text(encoding='utf-8').splitlines();idx=c['line']
    following='\n'.join(lines[idx:idx+8])
    nearby=[d for d in inv['mermaid'] if d['file']==c['file'] and 0<d['line']-c['line']<=8]
    kind='Mermaid' if nearby else 'table' if re.search(r'^\|',following,re.M) else 'code / text schematic' if '```' in following else 'prose / list'
    figures.append(dict(file=c['file'],line=c['line'],figure_id=c['ids'][0],caption=c['text'],kind=kind,asset=nearby[0]['key'] if nearby else None))
counts=collections.Counter(f['figure_id'] for f in figures)
result=dict(captions=len(figures),unique_figure_ids=len(counts),intentional_reuse={k:v for k,v in counts.items() if v>1},mermaid_fences=len(inv['mermaid']),kinds=dict(collections.Counter(f['kind'] for f in figures)),figures=figures,unmatched_mermaid=[d for d in inv['mermaid'] if not any(f['asset']==d['key'] for f in figures)])
(BUILD/'figure-map.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print({k:v for k,v in result.items() if k not in ('figures','unmatched_mermaid')});print('unmatched Mermaid',len(result['unmatched_mermaid']))
