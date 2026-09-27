"""保存済みの試行を既存Python比較器と照合する。送りの採用判定ではない。"""
import importlib.util
import json
from pathlib import Path
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location("page_flow_reference", root / "reference/prototype.py")
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)


def read(path, flags=cv2.IMREAD_COLOR):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), flags)


checks = []
for case in json.loads((folder / "observations.json").read_text()):
    directory = folder / case["test"]["id"]
    comparisons = []
    for link in case["links"]:
        for profile in link["profiles"]:
            comparisons.append((profile["prefix"], profile["profile"], profile["margin"], profile["raw"],
                                profile["band_raw"], profile["status"], profile["clusters"]))
    for page in case["pages"]:
        oracle = page["oracle"]
        if oracle["status"] == "built":
            comparisons.append((f"p{page['page']}-oracle-c", "normal", None,
                                oracle["raw_pixels"], None, None, oracle["clusters"]))
    for prefix, profile, margin, raw, band_raw, status, clusters in comparisons:
        a = read(directory / (prefix + "-a.png"))
        b = read(directory / (prefix + "-b.png"))
        p = reference.Params()
        if profile == "strict":
            p.max_shift_mm, p.edge_tolerance = 0, 0
        result = reference.compare_page(a, b, p)
        expected = read(directory / (prefix + "-raw.png"), cv2.IMREAD_GRAYSCALE) > 0
        assert np.array_equal(expected, result.raw_mask), (directory.name, prefix, "mask")
        assert result.raw_pixels == raw and len(result.clusters) == clusters, (directory.name, prefix, "counts")
        if status is not None:
            assert result.status == status, (directory.name, prefix, "status")
        if margin is not None:
            assert np.count_nonzero(result.raw_mask[margin:margin + 100]) == band_raw
        checks.append(dict(case=directory.name, prefix=prefix, csharp_mask_equal=True,
                           raw_pixels=result.raw_pixels, band_raw=band_raw, clusters=clusters))
    print(directory.name, "参照マスク一致", flush=True)

(folder / "reference.json").write_text(json.dumps(dict(opencv=cv2.__version__, comparisons=checks), indent=2) + "\n")
print(f"{len(checks)}比較の生差分マスク・件数を照合。製品の送り採用の検証ではありません。")
