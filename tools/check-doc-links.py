# Walks docs/**/*.md + README.md, prints relative markdown links that do not resolve.
import re, pathlib, sys, urllib.parse
root = pathlib.Path(__file__).resolve().parent.parent
files = list((root/'docs').rglob('*.md')) + [root/'README.md']
n = 0
for f in files:
    for m in re.finditer(r'\]\(([^)\s]+)\)', f.read_text(encoding='utf-8', errors='replace')):
        t = re.sub(r':\d+(-\d+)?$', '', m.group(1).split('#')[0])  # file:line cites
        if not t or re.match(r'^(\w+:|/)', t): continue
        if not (f.parent/urllib.parse.unquote(t)).exists():
            n += 1; print(f"{f.relative_to(root)}: {m.group(1)}")
print(f"broken: {n}")
