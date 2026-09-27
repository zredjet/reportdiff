#!/usr/bin/env python3
"""Type3 vector PDF controls with integer-pixel padding, fixed anchors and known input translations.
The product must independently estimate alignment and infer links from the actual PDFs.
Requires pypdf only for authoring; no production dependencies or thresholds change.
"""
import hashlib,json,sys
from pathlib import Path
from pypdf import PdfReader,PdfWriter
from pypdf.generic import NameObject,DecodedStreamObject,RectangleObject
root=Path(__file__).resolve().parents[2]
out=Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=True)
scale=.24; pad=256; height=1250+2*pad
cases=[('zero',[0,0],0,None),('down',[4,4],0,None),('up',[-6,-6],0,None),('variable',[4,-6],0,None),('mixed',[0,4],0,None),('chain',[4,-6,3],0,None),('horizontal',[0,0],5,None),('both',[4,4],-5,None),('body-tone',[4,4],0,'body'),('band-tone',[4,4],0,'band'),('edge',[4,4],0,'edge'),('subpixel',[4.5,4.5],0,None),('unpaired',[4,4,4],0,None)]
multiple = len(sys.argv) > 2 and sys.argv[2] == '--multiple'
numeric = len(sys.argv) > 2 and sys.argv[2] == '--numeric'
shared = len(sys.argv) > 2 and sys.argv[2] == '--shared'
shared_strong = len(sys.argv) > 2 and sys.argv[2] == '--shared-strong'
if shared_strong:
 shared=True;pad=512;height=1250+2*pad
if multiple:
 cases=[('multiple-down',[4,4,4,4],0,None),('multiple-up',[-6,-6,-6,-6],0,None),('multiple-variable',[4,-6,0,3],0,None),('multiple-opposite',[-6,4,3,0],0,None),('multiple-terminal-tone',[4,-6,0,3],0,None),('multiple-horizontal',[4,-6,0,3],5,None)]
if numeric:
 cases=[('numeric-down',[4,4],0,None),('numeric-up',[-6,-6],0,None),('numeric-variable',[4,-6],0,None),('numeric-multiple',[4,-6,0,3],0,None),('numeric-weak',[4,4],0,None),('numeric-horizontal',[4,4],5,None)]
if shared:
 cases=[('shared-down',[4,4],0,None),('shared-up',[-6,-6],0,None),('shared-variable',[4,-6],0,None),('shared-global-tone',[4,4],0,None),('shared-global-multiple',[4,-6,0,3],0,None),('shared-horizontal',[4,4],5,None),('shared-global-number',[4,4],0,None)]
if shared_strong:
 cases=[('shared-strong-number',[4,4],0,None),('shared-strong-multiple',[4,-6,0,3],0,None)]
manifest=[]
for name, shifts, dx, mutation in cases:
 folder=out/name;folder.mkdir(exist_ok=True)
 scenario='chain3' if name=='chain' else 'R11' if name=='unpaired' else 'R10'
 if multiple:
  scenario={'multiple-down':'independent-inserts','multiple-up':'independent-opposite','multiple-variable':'independent-inserts','multiple-opposite':'independent-opposite','multiple-terminal-tone':'nonflow-terminal-tone','multiple-horizontal':'independent-inserts'}[name]
 if numeric:
  scenario={'numeric-down':'terminal-number','numeric-up':'terminal-minus','numeric-variable':'terminal-number','numeric-multiple':'multiple-terminal','numeric-weak':'weak-actual-support','numeric-horizontal':'terminal-number'}[name]
 if shared:
  scenario={'shared-down':'shared-two','shared-up':'shared-two','shared-variable':'shared-two','shared-global-tone':'shared-tone','shared-global-multiple':'independent-and-shared','shared-horizontal':'shared-two','shared-global-number':'shared-number','shared-strong-number':'shared-number','shared-strong-multiple':'independent-and-shared'}[name]
 for side in ['a','b']:
  writer=PdfWriter(clone_from=root/'tests/ReportDiff.Tests/Fixtures'/('page-flow-shared' if shared else 'page-flow-numeric' if numeric else 'page-flow-multiple' if multiple else 'page-flow')/scenario/f'{side}.pdf')
  for i,page in enumerate(writer.pages):
   dy=shifts[i] if side=='b' else 0; xshift=dx if side=='b' else 0
   content=page.get_contents().get_data()
   # Y is PDF bottom-up; raise original by padding, then shift the entire page.
   full=f'q 1 0 0 1 0 {pad*scale:.8f} cm\n'.encode()+content+b'\nQ\n0 g\n'
   anchor_height=440 if shared_strong else 184
   for start in [64,height-64-anchor_height]:
    for y in range(0,anchor_height,9):
     for x in range(64,1000-72,9):
      if (x*37+y*17+x*y*13)%11<9:
       full+=f'{x*scale:.8f} {(height-start-y-8)*scale:.8f} {8*scale:.8f} {8*scale:.8f} re f\n'.encode()
   if side=='b' and i==1 and mutation in ['body','band']:
    top=(550 if mutation=='body' else 350)+pad
    full+=f'0.6274509804 g {650*scale:.8f} {(height-top-10)*scale:.8f} {10*scale:.8f} {10*scale:.8f} re f\n'.encode()
   full=f'q 1 0 0 1 {xshift*scale:.8f} {-dy*scale:.8f} cm\n'.encode()+full+b'Q\n'
   if side=='b' and i==1 and mutation=='edge':
    full+=f'0.9960784314 g 120 {(height-1)*scale:.8f} 0.24 0.24 re f\n'.encode()
   stream=DecodedStreamObject();stream.set_data(full);page[NameObject('/Contents')]=writer._add_object(stream)
   page.mediabox=RectangleObject([0,0,240,height*scale])
   page.compress_content_streams()
  path=folder/f'{side}.pdf'
  with path.open('wb') as f:writer.write(f)
  manifest.append({'path':str(path.relative_to(out)),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'pages':len(writer.pages)})
(out/'sha256.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(len(manifest),'vector PDFs written')
