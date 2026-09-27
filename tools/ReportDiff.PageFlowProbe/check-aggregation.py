"""固定した件数期待、編集列、実構造参照、内容マスク転写を集約器とは別に検査する。"""
import difflib
import hashlib
import json
from collections import Counter
from pathlib import Path
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folders = [Path(p).resolve() for p in sys.argv[1:]]
checks = []
audit_totals = Counter()
# (未集約, 集約後, 内容クラスタ, 網羅性)。A/B交換で同じ期待を適用する。
expected = {
    "R10": (5, 1, 0, True), "R11": (6, 1, 0, False), "chain3": (8, 1, 0, True),
    "shifted-R11": (6, 1, 0, False), "paired-content-tone": (6, 2, 1, True),
    "paired-boundary-tone": (6, 2, 1, True),
    "skia-local-R10": (5, 1, 0, True), "skia-local-R11": (6, 1, 0, False),
    "skia-local-chain3": (8, 1, 0, True),
    "two-inserts": (7, 7, 0, True), "two-inserts-tone": (8, 8, 1, True),
    "new-page-extra": (6, 6, 0, False), "flow-number-change": (9, 9, 9, True),
}


def read(path):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), cv2.IMREAD_GRAYSCALE)


def ref(value):
    return value["page"], value["structural_change_id"]


for folder in folders:
    data = json.loads((folder / "aggregation.json").read_text())
    source = Path(data["source"])
    assert hashlib.sha256((source / "candidates.json").read_bytes()).hexdigest() == data["source_sha256"]
    candidates = {r["folder"]: r for r in json.loads((source / "candidates.json").read_text())}
    assert not data["automatic_product_adoption"]
    paired_results = {}
    for record in data["records"]:
        name, value, result = record["run"], record["input"], record["decision"]
        candidate = candidates[name]
        case = candidate["id"]
        pages = value["pages"]
        structures = {ref(s["reference"]): s for p in pages for s in p["structures"]}
        clusters = sum(p["clusters"] for p in pages)
        assert result["difference_count"] == sum(p["difference_count"] for p in pages)
        assert result["difference_count_complete"] == (all(p["complete"] for p in pages) and not value["selection_limited"])
        assert result["aggregated_difference_count_complete"] == result["difference_count_complete"]
        disabled = record["disabled"]
        assert disabled["status"] == "disabled" and disabled["aggregated_difference_count"] is None
        assert disabled["aggregated_difference_count_complete"] is None and not disabled["groups"]
        assert disabled["difference_count"] == result["difference_count"]
        rows = {side: sorted((r for r in value["rows"] if r["side"] == side), key=lambda r: (r["page"], r["start"])) for side in ("a", "b")}
        edits = [op for op in difflib.SequenceMatcher(None, [r["text"] for r in rows["a"]],
                 [r["text"] for r in rows["b"]], autojunk=False).get_opcodes() if op[0] != "equal"]
        if case in expected and "selected" not in candidate:
            assert (result["difference_count"], result["aggregated_difference_count"], clusters,
                    result["aggregated_difference_count_complete"]) == expected[case], name
        if case.startswith("two-inserts"):
            assert value["gate_ready"] and len(value["links"]) == 1
            assert len(edits) == 2 and all(op[0] == ("delete" if candidate["reverse"] else "insert") for op in edits)
            assert result["status"] == "skipped" and result["reason"] == "multiple_cause_ranges"
        elif case == "new-page-extra":
            assert result["reason"] == "unpaired_residual_not_proven"
        elif case in expected and case != "flow-number-change" and "selected" not in candidate:
            assert result["status"] == "grouped", name
        else:
            assert result["status"] == "skipped" and result["reason"] == "document_gate_not_ready", name
        used = []
        for group in result["groups"]:
            refs = [ref(s) for s in group["structures"]]
            assert len(refs) == len(set(refs)) and all(s in structures and not structures[s]["excluded"] for s in refs)
            assert set(refs) == set(structures) and ref(group["cause"]) in refs
            used.extend(refs)
            assert len(edits) == 1 and edits[0][0] in ("insert", "delete")
            assert structures[ref(group["cause"])]["kind"] == ("inserted" if edits[0][0] == "insert" else "deleted")
            assert group["links"] == value["links"]
            assert all(not next(p for p in pages if p["number"] == b["page"])["paired"] for b in group["auxiliary_bands"])
            for balance in group["balance"]:
                assert balance["rows_b"] - balance["rows_a"] == balance["cause_delta"] + balance["incoming"] - balance["outgoing"]
                assert balance["rows_a"] == sum(r["page"] == balance["page"] for r in rows["a"])
                assert balance["rows_b"] == sum(r["page"] == balance["page"] for r in rows["b"])
        assert len(used) == len(set(used))
        assert result["aggregated_difference_count"] == result["difference_count"] - sum(len(g["structures"]) - 1 for g in result["groups"])
        for projection in record["projections"]:
            number = projection["page"]
            page = next(p for p in pages if p["number"] == number)
            assert page["clusters"] == len(projection["clusters"]) == len(projection["content_clusters"])
            assert [(s["reference"]["structural_change_id"], s["kind"], s["excluded"]) for s in page["structures"]] == [
                (s["id"], s["kind"], s["excluded"]) for s in projection["structures"]]
            for cluster in projection["clusters"]:
                original = next(c for c in projection["content_clusters"] if c["id"] == cluster["row"]["content_id"])
                assert cluster["pixels"] == original["pixels"] == sum(p["pixels"] for p in cluster["row"]["parts"])
            spec = next(p for p in candidate["result"]["pages"] if p["page"] == number)
            content = read(source / name / f"p{number}-normal-raw.png")
            raw_path = folder / name / projection["raw_mask"]
            assert hashlib.sha256(raw_path.read_bytes()).hexdigest() == projection["raw_sha256"]
            actual = read(raw_path)
            wanted = np.zeros_like(actual)
            cursor = 0
            for segment, kind in zip(spec["segments"], spec["kinds"]):
                if kind == 1:
                    continue
                start, length = segment["canvas_start"], segment["length"]
                wanted[start:start + length] = content[cursor:cursor + length]
                cursor += length
            assert cursor == len(content) and np.array_equal(wanted, actual)
            assert np.count_nonzero(actual) == projection["raw_pixels"] == np.count_nonzero(content)
            assert projection["status"] == "different"
        paired_results[name] = (result["status"], result["reason"], result["difference_count"], result["aggregated_difference_count"],
                                result["aggregated_difference_count_complete"], clusters)
        checks.append(dict(run=name, status=result["status"], reason=result["reason"], before=result["difference_count"],
                           after=result["aggregated_difference_count"], content_clusters=clusters,
                           complete=result["aggregated_difference_count_complete"], projected_pages=len(record["projections"])))
    for name, value in paired_results.items():
        if "-ab" in name:
            assert paired_results[name.replace("-ab", "-ba")] == value
    audit = json.loads((folder / "aggregation-audit.json").read_text())
    audit_totals.update(c["check"] for c in audit)
    for c in audit:
        if c["check"] not in {"reverse_rows", "reverse_links", "reverse_pages_and_structures", "incomplete_preserved"}:
            assert c["status"] == "skipped" and c["reason"]

sources = json.loads((root / "docs/verification/t3-1b-acceptance.json").read_text())["product_source_sha256"]
assert all(hashlib.sha256((root / p).read_bytes()).hexdigest() == h for p, h in sources.items())
summary = dict(runs=len(checks), grouped=sum(c["status"] == "grouped" for c in checks),
               skipped=sum(c["status"] == "skipped" for c in checks), projected_pages=sum(c["projected_pages"] for c in checks),
               audits=dict(sorted(audit_totals.items())), checks=checks, product_sources_unchanged=len(sources),
               bidirectional_counts_equal=True, content_masks_and_clusters_preserved=True, automatic_product_adoption=False)
(folders[-1] / "aggregation-reference.json").write_text(json.dumps(summary, indent=2) + "\n")
print(json.dumps({k: v for k, v in summary.items() if k != "checks"}, indent=2))
