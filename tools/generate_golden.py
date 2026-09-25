"""One-off: write the NotRag prototype's extraction output as DocQA's golden files (plans items 2, 3).

The C# extractor must reproduce the ``<part>.txt`` files byte for byte (item 2). ``blocks.json``
records the prototype's assembled document blocks, each text replaced by its length and SHA-256,
with the token count the prototype recorded for exactly those blocks (item 3). Run it with the
prototype's own virtual environment, so the prototype's pinned lxml does the parsing:

    D:\\code\\NotRag\\.venv\\Scripts\\python.exe tools\\generate_golden.py

It imports the prototype's ``extract_corpus()`` instead of running ``extract.py --show``. On
Windows, ``print`` translates ``\\n`` to ``\\r\\n``, and shell redirection can re-encode the
output; either would show up as a parser difference that is not one. Each document is written
as UTF-8 with no BOM and ``\\n`` line endings.

The prototype is frozen and is only read, never modified. It reads this repository's
``admincode/`` pages, which are byte-identical to the prototype's. No API call is made: the token
count comes from the prototype's ``.notrag_state.json``, and is recorded only if that file's
fingerprint is the fingerprint of the blocks written here.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parent.parent
CORPUS_DIR = REPO_ROOT / "admincode"
GOLDEN_DIR = REPO_ROOT / "tests" / "golden"
BLOCKS_GOLDEN = GOLDEN_DIR / "blocks.json"
DEFAULT_PROTOTYPE = Path(r"D:\code\NotRag")

#: Golden files are named by Part number: "ADMINISTRATIVE CODE 41 175.htm" -> "175.txt".
_PART = re.compile(r"ADMINISTRATIVE CODE 41 (\d+)\.html?")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--prototype",
        type=Path,
        default=DEFAULT_PROTOTYPE,
        help=f"the NotRag prototype's folder (default: {DEFAULT_PROTOTYPE})",
    )
    args = parser.parse_args(argv)

    sys.path.insert(0, str(args.prototype))
    import lxml  # noqa: E402  (the prototype's environment, after the path change)
    from lxml import etree  # noqa: E402
    from config import MODEL  # noqa: E402
    from corpus import assemble_corpus  # noqa: E402
    from extract import extract_corpus  # noqa: E402

    documents = extract_corpus(CORPUS_DIR)
    GOLDEN_DIR.mkdir(parents=True, exist_ok=True)
    for document in documents:
        match = _PART.fullmatch(document.filename)
        if match is None:
            print(f"Unexpected corpus file name: {document.filename}", file=sys.stderr)
            return 1
        path = GOLDEN_DIR / f"{match.group(1)}.txt"
        with open(path, "w", encoding="utf-8", newline="\n") as file:
            file.write(document.text)
        print(f"{path.relative_to(REPO_ROOT)}: {len(document.text):,} characters")

    # The counter is never consulted for the golden: the token count comes from the state file.
    corpus = assemble_corpus(documents, lambda blocks: 0)
    state_file = args.prototype / ".notrag_state.json"
    state = json.loads(state_file.read_text(encoding="utf-8"))
    if state.get("fingerprint") != corpus.fingerprint:
        print(
            f"{state_file} records fingerprint {state.get('fingerprint')}, but these blocks fingerprint "
            f"{corpus.fingerprint}: its token count is for a different corpus. Run the prototype's "
            f"corpus.py to refresh it, then run this again.",
            file=sys.stderr,
        )
        return 1

    golden = {
        "prototype_fingerprint": corpus.fingerprint,
        "prototype_tokens": state["tokens"],
        "prototype_tokens_model": MODEL,
        "prototype_tokens_recorded_at": state["recorded_at"],
        "blocks": [_without_text(block) for block in corpus.document_blocks()],
    }
    with open(BLOCKS_GOLDEN, "w", encoding="utf-8", newline="\n") as file:
        file.write(json.dumps(golden, indent=2) + "\n")
    print(f"{BLOCKS_GOLDEN.relative_to(REPO_ROOT)}: {len(golden['blocks'])} blocks, {state['tokens']:,} tokens")

    libxml2 = ".".join(str(part) for part in etree.LIBXML_VERSION)
    print(f"lxml {lxml.__version__}, libxml2 {libxml2}, Python {sys.version.split()[0]}")
    return 0


def _without_text(block: dict[str, Any]) -> dict[str, Any]:
    """The block with its text replaced by the text's length in code points and its SHA-256."""
    source = dict(block["source"])
    text = source.pop("data")
    source["chars"] = len(text)
    source["data_sha256"] = hashlib.sha256(text.encode("utf-8")).hexdigest()
    return {**block, "source": source}


if __name__ == "__main__":
    raise SystemExit(main())
