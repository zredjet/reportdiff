"""現行CLIの基準出力を保存する。T3-1cの採用期待の代替にはしない。"""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
fixture = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
output.mkdir(parents=True, exist_ok=False)
cli = root / "src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll"
records = []


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


for case in json.loads((fixture / "observations.json").read_text()):
    name = case["test"]["id"]
    for reverse in (False, True):
        side_a, side_b = ("b", "a") if reverse else ("a", "b")
        direction = "ba" if reverse else "ab"
        paired_results = []
        for enabled in (False, True):
            directory = output / f"{name}-{direction}-rows-{str(enabled).lower()}"
            args = ["dotnet", str(cli), "compare", str(fixture / name / f"{side_a}.pdf"),
                    str(fixture / name / f"{side_b}.pdf"), "--out", str(directory), "--raw-overlay", "--quiet"]
            if enabled:
                args += ["--config", str(root / "examples/rows.yaml")]
            run = subprocess.run(args, cwd=root, capture_output=True, text=True)
            assert run.returncode == 1, (name, direction, enabled, run.returncode, run.stderr)
            result = json.loads((directory / "result.json").read_text())
            assert result["summary"]["status"] == "different"
            assert result["summary"]["structural_change_count"] == 0
            pages = []
            raw_files = {}
            for page in result["pages"]:
                row = page["row_alignment"]
                assert row["status"] != "applied", (name, direction, page["page"])
                # raw evidence の画像をキー名に依存せず、参照先のPNGから集計する。
                def evidence_files(value):
                    if isinstance(value, dict):
                        for child in value.values():
                            evidence_files(child)
                    elif isinstance(value, list):
                        for child in value:
                            evidence_files(child)
                    elif isinstance(value, str) and value.endswith(".png"):
                        raw_files[value] = digest(directory / value)
                evidence_files(page.get("raw_evidence"))
                pages.append(dict(page=page["page"], status=page["status"], raw_pixels=page["raw_pixels"],
                                  clusters=len(page["clusters"]), row_status=row["status"],
                                  reason=row["reason"], detail=row["detail"]))
            assert raw_files, (name, direction, "raw evidence missing")
            record = dict(case=name, direction=direction, rows_enabled=enabled, exit_code=run.returncode,
                          summary=result["summary"], pages=pages, raw_evidence_sha256=raw_files,
                          result_sha256=digest(directory / "result.json"), output=directory.relative_to(root).as_posix())
            records.append(record)
            paired_results.append(record)
        off, on = paired_results
        assert off["summary"] == on["summary"], (name, direction, "summary changed")
        assert off["raw_evidence_sha256"] == on["raw_evidence_sha256"], (name, direction, "raw evidence changed")
        assert [(p["status"], p["raw_pixels"], p["clusters"]) for p in off["pages"]] == [
            (p["status"], p["raw_pixels"], p["clusters"]) for p in on["pages"]]
        expected_unpaired = abs(case["a_pages"] - case["b_pages"])
        unpaired = [p for p in on["pages"] if p["status"].startswith("only_in_")]
        assert len(unpaired) == expected_unpaired
        if expected_unpaired:
            assert not on["summary"]["difference_count_complete"]
            assert all(p["status"] == ("only_in_a" if reverse else "only_in_b") for p in unpaired)
        print(name, direction, "基準出力・raw evidence維持", flush=True)

(output / "cli.json").write_text(json.dumps(dict(runs=records, product_page_flow_implemented=False), indent=2) + "\n")
print(f"{len(records)}実行を保存。送り・集約の受け入れは未成立です。")
