#!/usr/bin/env python3
"""Render a markdown document to a PDF in presentation/.

The renderer loads the generated HTML with no base URL, so relative <img src> never
resolves. Screenshots are therefore inlined as data URIs into a temporary copy of the
markdown; the checked-in markdown keeps ordinary relative paths so it still renders on
GitHub and in an editor.

Usage:
    python3 tools/build_feature_pdf.py                      # the feature guide (default)
    python3 tools/build_feature_pdf.py SOURCE.md OUT.pdf "Title"

Set MAKE_PDF_BIN if the make-pdf binary lives somewhere other than the default.
"""

from __future__ import annotations

import base64
import mimetypes
import os
import pathlib
import re
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parent.parent
SOURCE = REPO / "docs" / "QS-Cost-Feature-Guide.md"
OUTPUT = REPO / "presentation" / "QS-Cost-Feature-Guide.pdf"
TITLE = "QS Cost — Feature Guide"
DEFAULT_BIN = pathlib.Path.home() / ".claude" / "skills" / "wstack" / "make-pdf" / "dist" / "pdf"

IMAGE = re.compile(r"!\[([^\]]*)\]\(([^)\s]+)\)")


def inline(markdown: str, base: pathlib.Path) -> str:
    """Replace every local image reference with a base64 data URI."""

    def swap(match: re.Match[str]) -> str:
        alt, src = match.group(1), match.group(2)
        if src.startswith(("http://", "https://", "data:")):
            return match.group(0)
        path = (base / src).resolve()
        if not path.is_file():
            sys.exit(f"error: image not found: {src} (resolved to {path})")
        mime = mimetypes.guess_type(path.name)[0] or "image/png"
        payload = base64.b64encode(path.read_bytes()).decode()
        # An <img> with width:100% is what actually scales — a plain markdown image
        # renders at its natural pixel width and is clipped by the text column.
        return (
            f'<img src="data:{mime};base64,{payload}" width="100%">\n\n'
            f'<p class="figure-caption"><em>{alt}</em></p>'
        )

    return IMAGE.sub(swap, markdown)


def main() -> None:
    args = sys.argv[1:]
    if len(args) not in (0, 3):
        sys.exit(__doc__)

    # Paths are resolved against the caller's cwd so the documented invocation works from
    # the repo root; with no arguments the feature guide builds exactly as it always did.
    src = pathlib.Path(args[0]).resolve() if args else SOURCE
    out = pathlib.Path(args[1]).resolve() if args else OUTPUT
    title = args[2] if args else TITLE

    if not src.is_file():
        sys.exit(f"error: source not found: {src}")

    binary = pathlib.Path(os.environ.get("MAKE_PDF_BIN", DEFAULT_BIN))
    if not binary.is_file():
        sys.exit(f"error: make-pdf binary not found at {binary}; set MAKE_PDF_BIN")

    source = src.read_text()
    count = len(IMAGE.findall(source))
    markdown = inline(source, src.parent)

    with tempfile.TemporaryDirectory() as tmp:
        staged = pathlib.Path(tmp) / src.name
        staged.write_text(markdown)
        result = subprocess.run(
            [
                str(binary), "generate", str(staged), str(out),
                "--cover", "--toc", "--no-confidential",
                "--title", title,
                "--author", "QS Cost",
            ],
            check=False,
        )

    if result.returncode != 0:
        sys.exit(result.returncode)
    print(f"{out.relative_to(REPO)} — {count} images inlined, {out.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
