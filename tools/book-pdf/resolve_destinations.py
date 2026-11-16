"""Resolve Pandoc's LaTeX labels to the actual named destinations in the PDF."""
import json,pathlib,re
from inventory import BUILD
def group(text,pos):
    while pos<len(text) and text[pos].isspace():pos+=1
    if pos>=len(text) or text[pos]!='{':raise ValueError('Expected group')
    start=pos+1;depth=1;pos+=1
    while pos<len(text):
        c=text[pos]
        if c=='\\':pos+=2;continue
        if c=='{':depth+=1
        if c=='}':
            depth-=1
            if not depth:return text[start:pos],pos+1
        pos+=1
    raise ValueError('Incomplete auxiliary group')
def resolve(mode='book'):
    aux=(BUILD/(mode+'.aux')).read_text(encoding='utf-8');labels={}
    for match in re.finditer(r'\\newlabel(?=\{)',aux):
        label,pos=group(aux,match.end());body,pos=group(aux,pos);fields=[];at=0
        while at<len(body):
            value,at=group(body,at);fields.append(value)
        if len(fields)>=4:labels[label]=fields[3]
    path=BUILD/'structure.json';s=json.loads(path.read_text(encoding='utf-8'));missing=[]
    for a in s['anchors']:
        reference=a.get('reference_label',a['destination']);a['reference_label']=reference
        if reference in labels:a['destination']=labels[reference]
        else:missing.append(dict(file=a['file'],anchor=a['anchor'],reference_label=reference))
    for link in s['internal_links']:
        reference=link.get('reference_label',link['destination']);link['reference_label']=reference
        if reference in labels:link['destination']=labels[reference]
    path.write_text(json.dumps(s,ensure_ascii=False,indent=2),encoding='utf-8')
    (BUILD/(mode+'.destination-resolution.json')).write_text(json.dumps(dict(latex_labels=len(labels),unresolved=missing),ensure_ascii=False,indent=2),encoding='utf-8')
    return missing
if __name__=='__main__':
    import sys
    missing=resolve(sys.argv[1] if len(sys.argv)>1 else 'book');print('unresolved source anchors',len(missing))
