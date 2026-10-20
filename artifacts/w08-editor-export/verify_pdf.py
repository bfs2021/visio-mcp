from pathlib import Path
import json
from pypdf import PdfReader
import pypdfium2 as pdfium
root=Path(__file__).parent
source=root/'mixed-pages.pdf'
reader=PdfReader(source)
assert len(reader.pages)==2
texts=[p.extract_text() for p in reader.pages]
assert 'Landscape page' in texts[0] and 'export verification' in texts[0]
assert 'Portrait page' in texts[1] and '600 pt' in texts[1]
doc=pdfium.PdfDocument(str(source))
coloured=[]
for i,page in enumerate(doc):
    image=page.render(scale=1).to_pil().convert('RGB')
    image.save(root/f'pdf-page-{i+1}.png')
    coloured.append(sum(1 for r,g,b in image.getdata() if b>r+15 and b>g))
assert coloured[0]>2000, coloured
summary={'pages':[{'widthPt':float(p.mediabox.width),'heightPt':float(p.mediabox.height)} for p in reader.pages], 'asciiLabelsPresent':True,'gradientPixelCount':coloured[0], 'visualInspection':'PASS by Codex; this is not manual human acceptance','unicodeExtraction':'Some punctuation maps to replacement characters although rendered glyphs are correct.'}
(root/'pdf-verification.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
print(json.dumps(summary))
