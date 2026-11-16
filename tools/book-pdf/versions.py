import pathlib,struct,hashlib,json,subprocess,sys,platform
from inventory import BUILD
from build import PANDOC
def version(cmd):
    p=subprocess.run(cmd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,encoding='utf-8',errors='replace');return p.stdout.strip()
def font_version(path):
    data=path.read_bytes();offset=struct.unpack_from('>I',data,12)[0] if data[:4]==b'ttcf' else 0
    count=struct.unpack_from('>H',data,offset+4)[0];names=[]
    for i in range(count):
        tag,cs,start,length=struct.unpack_from('>4sIII',data,offset+12+i*16)
        if tag!=b'name':continue
        fmt,n,strings=struct.unpack_from('>HHH',data,start)
        for j in range(n):
            plat,enc,lang,key,size,pos=struct.unpack_from('>HHHHHH',data,start+6+j*12)
            if key==5:
                try:names.append(data[start+strings+pos:start+strings+pos+size].decode('utf-16-be' if plat in (0,3) else 'latin1'))
                except UnicodeError:pass
    return sorted(set(names))
fonts=[]
for name in ['cambria.ttc','cambriab.ttf','cambriai.ttf','cambriaz.ttf','consola.ttf','consolab.ttf','consolai.ttf','consolaz.ttf','segoeui.ttf','segoeuib.ttf','seguisym.ttf','msyh.ttc']:
    p=pathlib.Path('C:/Windows/Fonts')/name
    fonts.append(dict(path=str(p),version=font_version(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
record=dict(pandoc=version([str(PANDOC),'--version']),lualatex=version(['lualatex','--version']),node=version(['node','--version']),npm=version(['npm.cmd','--version']),mermaid_cli=version(['node',str(BUILD/'node_modules/@mermaid-js/mermaid-cli/src/cli.js'),'--version']),poppler=version(['pdfinfo','-v']),python=sys.version,python_path=sys.executable,platform=platform.platform(),fonts=fonts,package_lock_sha256=hashlib.sha256((BUILD/'package-lock.json').read_bytes()).hexdigest(),pandoc_archive_sha256=hashlib.sha256((BUILD/'tools/pandoc.zip').read_bytes()).hexdigest())
(BUILD/'versions.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
