"""Bind the completed manual inspection record to the reviewed PDF and images."""
import hashlib,json,pathlib
B=pathlib.Path(__file__).resolve().parent
read=lambda n:json.loads((B/n).read_text(encoding='utf-8'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
r=read('reviewed-pages.json');plan=read('inspection-plan.json');q=read('book.qa.json')
assert len(r['pages'])==len(set(r['pages']))==159
assert set(r['pages'])=={p['page'] for p in plan['pages']}
assert r['sheets']==list(range(1,41))
assert sha(B/'book.pdf')==q['pdf_sha256']
assert q['pdf_sha256']=='5a814d28fb3a33e38657810bf879093d59b0cb8cef640da13b9b293a1fb71a86','This manual review applies only to the inspected final PDF; review any changed rendering again.'
v={
 'pdf_sha256':q['pdf_sha256'],
 'contact_page_coverage':[1,q['page_count']],
 'all_page_contact_sheets_inspected':[{'file':f'contact-{n:03}.jpg','sha256':sha(B/'qa/book'/f'contact-{n:03}.jpg')} for n in r['sheets']],
 'contact_thumbnail_note':'All 1200 physical pages reviewed on 40 contact sheets; thumbnail review does not establish fine-detail legibility on every page.',
 'full_resolution_long_edge_px':1800,
 'full_resolution_pages_inspected':r['pages'],
 'full_resolution_selection_reasons':plan['pages'],
 'full_resolution_image_hashes':{f'full-{n:04}.png':sha(B/'qa/book'/f'full-{n:04}.png') for n in r['pages']},
 'observations':'No visible clipping, overlapping text, missing-glyph boxes or unreadable tables found in the inspected enlarged pages. All five A3 diagrams were inspected. Scope headers identify the preamble and current S-section; body headers identify chapter and current section. Landscape tables repeat their column headings. Code and hex alignment, footnotes and the repaired ledger continuation were checked. The previously page-split ledger hash is complete within its paragraph on physical page 843, wrapping inside the margins. Some long ledger URLs have loose word spacing; some figure captions occupy sparse portrait pages before their landscape tables. These are retained presentation limitations of the inherited layout. No further rendering defect was identified in the reviewed images.',
 'pilot':read('pilot-inspection.json'),
 'limitations':['Exactly 159 final pages reviewed at 1800 pixels on the long edge; remaining pages reviewed as thumbnails only.','Text searches normalize whitespace and Unicode and do not establish byte-identical copying from PDF.','External URL network reachability and independent technical acceptance were not tested.'],
}
(B/'visual-inspection.json').write_text(json.dumps(v,indent=2,ensure_ascii=False),encoding='utf-8')
print('Recorded 159 enlarged pages and 40 contact sheets for',q['pdf_sha256'])
