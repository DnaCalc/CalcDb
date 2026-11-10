"""Render a Markdown working doc to a PDF, A4, with the one-pager style.

    python docs/build_pdf.py docs/calcdb-one-pager.md        -> docs/calcdb-one-pager.pdf

Needs the `markdown` package (pip install markdown) and Microsoft Edge or Chrome for
headless printing. Prints the page count; the one-pager must stay at 1.
"""
import pathlib, re, shutil, subprocess, sys, tempfile, time

import markdown

CSS = """
@page { size: A4; margin: 12mm 13mm 11mm 13mm; }
html { -webkit-print-color-adjust: exact; }
body { font-family: "Segoe UI", "Helvetica Neue", Arial, sans-serif; font-size: 9pt;
       line-height: 1.27; color: #111; margin: 0; }
h1 { font-size: 15pt; margin: 0 0 1pt 0; letter-spacing: -0.2pt; }
h1 + p { color: #555; margin-top: 0; font-size: 8pt; }
h2 { font-size: 11pt; margin: 7pt 0 2pt 0; padding-bottom: 1pt; border-bottom: 0.6pt solid #bbb;
     break-after: avoid; }
p { margin: 0 0 4pt 0; text-align: justify; hyphens: auto; }
ul, ol { margin: 0 0 4pt 0; padding-left: 13pt; }
li { margin: 0 0 1.5pt 0; text-align: justify; hyphens: auto; }
strong { font-weight: 600; }
code { font-family: Consolas, monospace; font-size: 8.3pt; }
"""

BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    "msedge", "google-chrome", "chromium", "chromium-browser",
]


def browser():
    for b in BROWSERS:
        if pathlib.Path(b).exists() or shutil.which(b):
            return b
    sys.exit("no Edge or Chrome found for headless printing")


def main():
    src = pathlib.Path(sys.argv[1]).resolve()
    out = src.with_suffix(".pdf")
    body = markdown.markdown(src.read_text(encoding="utf-8"), extensions=["extra", "sane_lists", "smarty"])
    title = re.search(r"<h1>(.*?)</h1>", body)
    html = (f"<!doctype html><html lang=en><head><meta charset=utf-8><title>"
            f"{re.sub('<[^>]+>', '', title.group(1)) if title else src.stem}</title>"
            f"<style>{CSS}</style></head><body>{body}</body></html>")
    with tempfile.TemporaryDirectory() as tmp:
        page = pathlib.Path(tmp) / "page.html"
        page.write_text(html, encoding="utf-8")
        out.unlink(missing_ok=True)
        subprocess.run([browser(), "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                        f"--user-data-dir={tmp}", f"--print-to-pdf={out}", page.as_uri()],
                       check=True, capture_output=True)
        for _ in range(100):           # Edge can return before the file is written
            if out.exists() and out.stat().st_size:
                time.sleep(0.5)
                break
            time.sleep(0.2)
    pages = len(re.findall(rb"/Type\s*/Page(?!s)", out.read_bytes()))
    print(f"{out}: {pages} page(s)")
    sys.exit(0 if pages == 1 else 1)


if __name__ == "__main__":
    main()
