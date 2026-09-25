# Golden extraction files

`<part>.txt` is the NotRag prototype's extracted text of
`admincode/ADMINISTRATIVE CODE 41 <part>.htm`. `CorpusGoldenTests` requires DocQA's C# extractor
to reproduce each one byte for byte (plan item 2).

- **Source.** The prototype's own `extract_corpus()`, called by `tools/generate_golden.py`, not
  `extract.py --show`. On Windows, `print` would turn `\n` into `\r\n`.
- **Generated.** 2026-09-14, with the prototype's pinned environment: lxml 6.1.3,
  libxml2 2.11.9, Python 3.13.14.
- **Format.** UTF-8 with no BOM, `\n` line endings, and no trailing newline. `.gitattributes`
  marks these files `-text`, so git never rewrites them.

## `blocks.json`

The prototype's assembled document blocks, from its own `assemble_corpus()` (plan item 3).
`CorpusBlocksGoldenTests` requires DocQA's blocks to match them field for field and in order:
title, context, citation setting, the 1-hour breakpoint on the last block only, and each text's
length in code points and SHA-256.

It also records `prototype_tokens`, the prototype's `count_tokens` total for exactly these blocks
(system prompt, blocks and a `"?"` question, on `claude-opus-5`). The script copies the total
from the prototype's `.notrag_state.json`, and refuses to unless that file's fingerprint is the
fingerprint of these blocks. It is a historical record: DocQA's system prompt has since diverged
from the prototype's, so the live `CorpusTokenCountLiveTests` pins DocQA's own totals instead.

## Regenerating

Do not edit these files by hand. To regenerate them, from the repository root:

```
D:\code\NotRag\.venv\Scripts\python.exe tools\generate_golden.py
```
