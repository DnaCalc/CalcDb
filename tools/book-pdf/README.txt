The Book of CalcDb — PDF build

Builds docs/The-Book-of-CalcDb.v1.1.pdf from the canonical v1.1 archive in
input/calcdb-book.v1.1-final.zip (SHA-256 67a51f69…4ce9, 41 members). The
archive holds the same bytes as docs/book at tag book-v1.1. The build checks
each file against book-manifest.v1.1.json, the archive and the tag before and
after rendering, and never edits a source. Book order comes from the manifest.

Rerun on Windows from the repository root:

  pwsh -File tools/book-pdf/render.ps1 -Python <python.exe> -Edition Full

Use -Edition Pilot for the stress-case pilot. Only the sources listed in
.gitignore are tracked; everything else in this folder is generated or
restored by the build (input/extracted, assets, node_modules, tools/pandoc,
lualatex.fmt, logs, QA output and contact sheets).

Dependencies
- Python 3.12 with pypdf and Pillow (the Codex bundled runtime has both).
- Node 25 / npm 11. npm ci restores the exact package-lock.json tree:
  Mermaid CLI 12.2.0 with Mermaid 12.3.0.
- Google Chrome at the path in puppeteer.json.
- Pandoc 3.14, Windows x86_64, unpacked under tools/pandoc-3.14:
  https://github.com/jgm/pandoc/releases/download/3.14/pandoc-3.14-windows-x86_64.zip
- MiKTeX LuaHBTeX 1.24.0 / LaTeX2e 2025-11-01. Package installation is
  disabled during compilation; install missing packages with
  miktex packages install NAME. Packages: fontspec, luaotfload, geometry,
  longtable, booktabs, array, calc, graphics, xcolor, fvextra, fancyvrb,
  upquote, lineno, pdflscape, microtype, fancyhdr, xurl, hyperref, bookmark,
  footnotehyper and their dependencies.
- Poppler pdftoppm, pdftotext and pdfinfo.
- Windows fonts: Cambria (four faces), Consolas, Segoe UI, Segoe UI Symbol,
  Microsoft YaHei.

Stages (render.ps1)
1. inventory.py: extracts the archive if needed, verifies every hash and
   inventories diagrams, captions, tables, code and footnotes.
2. render-diagrams.mjs: Mermaid to SVG and cropped vector PDF in assets/.
3. figure-map.py, versions.py: caption map; tool and font identities.
4. build.py: Pandoc per file with namespaced IDs and per-file footnotes; A4
   portrait, A4 landscape for wide tables, A3 for oversized diagrams; A-01's
   ledger as labelled records; three LuaLaTeX passes; copies the full edition
   to docs/. The A-01 source has a blank line before line 329 that splits the
   ledger table; the build joins it in derived/A-01-sources.md only.
5. qa.py, additional-checks.py: links, destinations, glyphs, code lines,
   footnotes, headings, captions, ledger IDs, scope links, warnings and
   all-page contact sheets in qa/.
6. prepare-inspection.py (or prepare-pilot-inspection.py): renders the dense
   and flagged pages for visual inspection; record-inspection.py binds the
   manual inspection record (visual-inspection.json) to the PDF.

A successful compile is not visual QA: after any change of layout, tools,
fonts or input, inspect the contact sheets and flagged pages again.
Byte-identical PDF output is not claimed.

For a later book version: replace the archive and its expected hash, the
manifest name and the book-v1.1 tag in inventory.py, and the edition labels
and output name in build.py.
