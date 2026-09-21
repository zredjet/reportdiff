#!/usr/bin/env python3
"""保存済み PNG と期待値で Python 参照実装を照合する。フォント・入力・期待値は再生成しない。"""

import importlib.util
import json
from pathlib import Path
import sys

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def verify():
    spec = importlib.util.spec_from_file_location("reportdiff_reference", ROOT / "reference/prototype.py")
    reference = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = reference
    spec.loader.exec_module(reference)
    expected = json.loads((ROOT / "reference/golden/expected.json").read_text(encoding="utf-8"))
    if cv2.__version__ != expected["opencv"]:
        raise ValueError(f"OpenCV は期待値と同じ {expected['opencv']} を使用してください（現在 {cv2.__version__}）。")
    checked = []
    for case in expected["cases"]:
        images = [cv2.imdecode(np.frombuffer((ROOT / "reference/golden" / f"{case['id']}_{side}.png").read_bytes(),
                                             np.uint8), cv2.IMREAD_COLOR) for side in ("a", "b")]
        actual = reference.compare_page(*images, reference.Params(**case["params"]))
        for key in ("status", "raw_pixels", "noise_dropped", "absorbed_groups", "max_shift_px"):
            if getattr(actual, key) != case[key]:
                raise ValueError(f"{case['id']} の {key} が一致しません: {getattr(actual, key)} / {case[key]}")
        if [vars(cluster) for cluster in actual.clusters] != case["clusters"]:
            raise ValueError(f"{case['id']} のクラスタが一致しません。")
        checked.append(case["id"])
    return {"opencv": cv2.__version__, "numpy": np.__version__, "cases": checked, "exact_match": True}


if __name__ == "__main__":
    try:
        print(json.dumps(verify(), ensure_ascii=False, indent=2))
    except (ValueError, OSError) as error:
        sys.exit(f"参照実装の検証に失敗しました: {error}")
