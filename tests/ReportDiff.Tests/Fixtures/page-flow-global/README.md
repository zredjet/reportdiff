# Vertical global alignment controls

These 26 PDFs are synthetic vector inputs for production CLI regression tests. They extend the repository's Type3 page-flow fixtures with 256px-equivalent top/bottom padding, deterministic fixed patterns, and known input translations. The CLI receives only the PDFs and ordinary settings; it must independently estimate global alignment and infer page flow.

Generate with `tools/ReportDiff.PageFlowProbe/create-global-pdfs.py` (pypdf). `sha256.json` fixes each PDF's bytes. Type3 glyphs intentionally use distinct geometric shapes and a Unicode text map instead of OS fonts. The native 300dpi rendering is 999 x 1762px. These fixed-pattern controls do not establish support for arbitrary real forms or resolve the A4 failures.

Cases: zero, positive/negative vertical, page-dependent and mixed shifts, three-page chain, horizontal/both-axis fallback, body/band changes, edge content, subpixel shift, and an unpaired page. Reverse direction, exclusion, and regional variants reuse these PDFs with different invocation settings.
