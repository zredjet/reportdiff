"""推定後にだけ正解座標を照合し、写像・元画素被覆・参照マスクを独立に検査する。"""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
inputs, folder = (Path(p).resolve() for p in sys.argv[1:])
spec = importlib.util.spec_from_file_location("candidate_reference", root / "reference/prototype.py")
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)
expected = {c["test"]["id"]: c for c in json.loads((inputs / "observations.json").read_text())}
runs = json.loads((folder / "candidates.json").read_text())
checks, mappings, unpaired = [], [], []
positive = {"R10", "R11", "chain3", "new-page-extra", "shifted-R10", "shifted-R11", "shifted-chain3",
            "paired-content-tone", "paired-boundary-tone", "skia-local-R10", "skia-local-R11", "skia-local-chain3",
            "two-inserts", "two-inserts-tone", "flow-number-change"}
negative = {
    "text-change": "text_mismatch", "same-text-pixels": "nonidentical_band_not_proven",
    "band-edge-tone": "nonidentical_band_not_proven", "neighbor-tone": "nonidentical_band_not_proven",
    "repeated": "repeated_body_text", "no-footer": "fixed_parts_support", "single-support": "fixed_parts_support",
    "skia-text-change": "text_mismatch", "skia-repeated": "repeated_body_text", "skia-edge-repeat": "unproven_body_bounds",
}
# 絶対座標で描くSkia入力は罫線の実画素が異なる。R10の成功入力へ読み替えない。
negative.update({name: "nonidentical_band_not_proven" for name in (
    "skia-R10", "skia-R11", "skia-chain3", "skia-band-tone", "skia-paired-tone", "skia-boundary-tone",
    "skia-extra", "skia-partial-chain", "skia-fractional")})


def read(path, flags=cv2.IMREAD_COLOR):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), flags)


def key(b):
    return b["side"], b["page"], b["start"], b["length"]


def crop(image, band):
    return image[band["start"]:band["start"] + band["length"]]


def compare(a, b, profile, raw, clusters=None, status=None, mask=None):
    params = reference.Params()
    if profile == "strict":
        params.max_shift_mm, params.edge_tolerance = 0, 0
    elif profile == "loose":
        params.max_shift_mm = .30
    result = reference.compare_page(a, b, params)
    assert result.raw_pixels == raw
    if clusters is not None:
        assert len(result.clusters) == clusters
    if status is not None:
        assert result.status == status
    if mask is not None:
        assert np.array_equal(result.raw_mask, read(mask, cv2.IMREAD_GRAYSCALE) > 0), str(mask)
    else:
        assert raw == 0 and not np.any(result.raw_mask)
    checks.append(dict(run=run["folder"], profile=profile, raw_pixels=raw, csharp_mask_equal=True))


for run in runs:
    result, case = run["result"], expected[run["id"]]
    directory = folder / run["folder"]
    assert not result["automatic_product_adoption"]
    for side in ("a", "b"):
        assert hashlib.sha256((inputs / run["id"] / f"{side}.pdf").read_bytes()).hexdigest() == case[f"{side}_sha256"]
    images = {(side, p): read(directory / f"{side}-p{p}.png")
              for side in ("a", "b") for p in result[f"pages_read_{side}"]}
    verified = [p for p in result["links"] if p["status"] == "band_verified"]
    if "selected" in run:
        assert all(result[f"pages_read_{s}"] == run["selected"] for s in ("a", "b"))
        assert not verified and not result["links"]
        assert not (directory / "a-p2.png").exists() and not (directory / "b-p2.png").exists()
    elif run["id"] in positive:
        wanted = []
        for link in case["links"]:
            source = ("b" if run["reverse"] else "a", link["link"]["a_page"], link["band_a"]["y"], link["band_a"]["h"])
            target = ("a" if run["reverse"] else "b", link["link"]["b_page"], link["band_b"]["y"], link["band_b"]["h"])
            wanted.append((source, target))
        assert [(key(p["source"]), key(p["target"])) for p in verified] == wanted, run["folder"]
        assert len(result["links"]) == len(wanted)
    else:
        assert run["id"] in negative and not verified
        reason = negative[run["id"]]
        assert result["reason"] == reason or all(p["reason"] == reason for p in result["links"]) and result["links"]

    endpoints = {key(p[k]) for p in verified for k in ("source", "target")}
    layouts = [(s, l) for s in ("a", "b") for l in result[f"layouts_{s}"]]
    if layouts:
        first_side, first = layouts[0]
        anchor = images[first_side, first["page"]]
        for side, layout in layouts:
            image = images[side, layout["page"]]
            assert np.array_equal(anchor[:first["header_end"]], image[:layout["header_end"]])
            assert np.array_equal(anchor[first["footer_start"]:], image[layout["footer_start"]:])
    mapped_endpoints, unpaired_endpoints = set(), set()
    for evidence in result["evidence"]:
        source, target = evidence["source"], evidence["target"]
        a = images[source["side"], source["page"]]
        b = images[target["side"], target["page"]]
        equal = np.array_equal(crop(a, source), crop(b, target))
        assert equal == evidence["exact_band_pixels"]
        assert len(evidence["comparisons"]) == (12 if equal else 0)
        for c in evidence["comparisons"]:
            original, band, donor = (a, source, crop(b, target)) if c["side"] == "source" else (b, target, crop(a, source))
            replaced = original.copy()
            replaced[band["start"]:band["start"] + band["length"]] = donor
            compare(replaced if c["reverse"] else original, original if c["reverse"] else replaced,
                    c["profile"], c["raw_pixels"])

    for page in result["pages"]:
        number = page["page"]
        if page["status"].startswith("only_in_"):
            side = page["status"][-1]
            assert page["difference_count_complete"] is False
            assert all(key(b) in endpoints for b in page["verified_bands"])
            unpaired_endpoints.update(key(b) for b in page["verified_bands"])
            if page["fixed_parts_verified"]:
                layout = next(l for l in result[f"layouts_{side}"] if l["page"] == number)
                image = images[side, number]
                residual = np.any(image != 255, axis=2)
                residual[:layout["header_end"]] = False
                residual[layout["footer_start"]:] = False
                for band in page["verified_bands"]:
                    residual[band["start"]:band["start"] + band["length"]] = False
                count = int(np.count_nonzero(residual))
                assert count == page["residual_nonwhite_pixels"]
                assert page["only_verified_bands_and_fixed_parts"] == (count == 0 and bool(page["verified_bands"]))
                if run["id"].startswith("skia-") and not page["verified_bands"]:
                    assert count > 0 and not page["only_verified_bands_and_fixed_parts"]
                elif run["id"] == "new-page-extra":
                    assert count == 3570 and not page["only_verified_bands_and_fixed_parts"]
                else:
                    assert count == 0 and page["only_verified_bands_and_fixed_parts"]
            else:
                assert run["id"] == "single-support" and not page["only_verified_bands_and_fixed_parts"]
            unpaired.append(dict(run=run["folder"], **page))
            continue
        a, b = images["a", number], images["b", number]
        if page["status"] != "built":
            if run["id"] in positive and "selected" not in run:
                expected_failures = {("shifted-R10", 2): "unproven_regular_layout", ("shifted-chain3", 3): "unproven_regular_layout",
                                     ("flow-number-change", 2): "unverified_edge_band"}
                assert page["reason"] == expected_failures[run["id"], number]
            assert page["fallback_preserved"] and page["baseline"]["alignment"]["status"] == "skipped"
            assert page["baseline"]["status"] == "different" and page["baseline"]["raw_pixels"] > 0
            compare(a, b, "normal", page["baseline"]["raw_pixels"], page["baseline"]["clusters"],
                    page["baseline"]["status"], directory / f"p{number}-fallback-raw.png")
            continue
        assert run["id"] in positive and "selected" not in run and not page["fallback_preserved"]
        assert page["baseline"]["status"] == "different"
        assert len(page["segments"]) == len(page["kinds"])
        omitted = {r["segment_index"]: r for r in page["removed"]}
        assert set(omitted) == {i for i, kind in enumerate(page["kinds"]) if kind == 1}
        for i, removal in omitted.items():
            band = removal["band"]
            segment = page["segments"][i]
            assert segment[f"{band['side']}_start"] == band["start"] and segment["length"] == band["length"]
            assert band["page"] == number
            if removal["proof"] == "verified_carry_range":
                assert key(band) in endpoints
                mapped_endpoints.add(key(band))
            else:
                # 原因行の位置は期待側から求め、推定器には渡さない。
                layout = case.get("layout", dict(top=72, step=24))
                top, pitch = round(layout["top"] * 300 / 72), round(layout["step"] * 300 / 72)
                wanted_causes = {("a" if run["reverse"] else "b", 1, top + 2 * pitch, pitch)}
                if run["id"].startswith("two-inserts"):
                    wanted_causes.add(("a" if run["reverse"] else "b", 2, top + 3 * pitch, pitch))
                assert key(band) in wanted_causes
                assert removal["proof"] == "interior_unmatched_with_two_sided_support"
        for side, original in (("a", a), ("b", b)):
            coverage = np.zeros(len(original), dtype=int)
            for segment, kind in zip(page["segments"], page["kinds"]):
                start, length = segment[f"{side}_start"], segment["length"]
                if start is None:
                    continue
                coverage[start:start + length] += 1
                if kind == 2:
                    assert np.all(original[start:start + length] == 255)
            assert np.all(coverage == 1), (run["folder"], number, side, "D被覆")
            content = read(directory / f"p{number}-c-{side}.png")
            rendered = np.full_like(content, 255)
            coverage[:] = 0
            for segment in page["content_segments"]:
                start, length, dest = segment[f"{side}_start"], segment["length"], segment["canvas_start"]
                if start is not None:
                    rendered[dest:dest + length] = original[start:start + length]
                    coverage[start:start + length] += 1
            for removal in page["removed"]:
                band = removal["band"]
                if band["side"] == side:
                    coverage[band["start"]:band["start"] + band["length"]] += 1
            assert np.all(coverage == 1) and np.array_equal(rendered, content), (run["folder"], number, side, "C被覆と画素")
        for c in page["comparisons"]:
            if run["id"] in {"paired-content-tone", "paired-boundary-tone", "two-inserts-tone"} and number == 1:
                assert c["raw_pixels"] > 0 and c["status"] == "different" and c["clusters"] > 0
            else:
                assert c["raw_pixels"] == 0 and c["status"] == "same" and c["clusters"] == 0
            compare(read(directory / f"p{number}-c-a.png"), read(directory / f"p{number}-c-b.png"),
                    c["profile"], c["raw_pixels"], c["clusters"], c["status"], directory / c["mask"])
        mappings.append(dict(run=run["folder"], page=number, complete_source_coverage=True,
                             omitted_ranges_proven=True, original_pixels_preserved=True))
    assert len(result["range_correspondence"]) == len(verified)
    for correspondence in result["range_correspondence"]:
        proofs = []
        for end in ("source", "target"):
            band = key(correspondence[end])
            proof = ("paired_page_structural_range" if band in mapped_endpoints else
                     "unpaired_page_verified_range" if band in unpaired_endpoints else None)
            assert correspondence[end + "_proof"] == proof
            proofs.append(proof)
        assert correspondence["both_endpoints_accounted_for"] == all(proofs)
    assert result["all_paired_maps_built"] == all(p["status"] in ("built", "only_in_a", "only_in_b") for p in result["pages"])
    if "gate" in result:
        gate = result["gate"]
        ready = (result["status"] == "prepared" and bool(verified) and len(verified) == len(result["links"])
                 and result["all_paired_maps_built"]
                 and all(p["both_endpoints_accounted_for"] for p in result["range_correspondence"]))
        assert gate["ready"] == ready
        assert gate["selected_links"] == (verified if ready else [])
        for selection in gate["selections"]:
            page = next(p for p in result["pages"] if p["page"] == selection["page"])
            paired = not page["status"].startswith("only_in_")
            assert selection["choice"] == ("unpaired" if not paired else "candidate" if ready else "baseline")
            if not paired:
                continue
            selected = next(s for s in result["selected_masks"] if s["page"] == page["page"])
            expected_mask = f"p{page['page']}-normal-raw.png" if ready else f"p{page['page']}-baseline-raw.png"
            assert selected["choice"] == selection["choice"] and selected["source"] == expected_mask
            assert (directory / selected["mask"]).read_bytes() == (directory / expected_mask).read_bytes()
            if page["status"] == "built":
                compare(read(directory / f"p{page['page']}-baseline-a.png"), read(directory / f"p{page['page']}-baseline-b.png"),
                        "normal", page["baseline"]["raw_pixels"], page["baseline"]["clusters"],
                        page["baseline"]["status"], directory / f"p{page['page']}-baseline-raw.png")
        if run["id"] in {"shifted-R10", "shifted-chain3"}:
            assert not ready and any(p["status"] == "built" for p in result["pages"])
        if run["id"].startswith("skia-") and not run["id"].startswith("skia-local-"):
            assert not ready
    print(run["folder"], "帯の帰属・被覆・参照マスクを確認", flush=True)

# A/B交換で、成功と失敗、送り端点、比較面の元画素が対称であること。
by_folder = {r["folder"]: r for r in runs}
for run in runs:
    if run["reverse"]:
        continue
    other = by_folder[run["folder"].replace("-ab", "-ba")]
    a, b = run["result"], other["result"]
    assert a["status"] == b["status"] and a["reason"] == b["reason"]
    assert [(p["status"], p["reason"]) for p in a["links"]] == [(p["status"], p["reason"]) for p in b["links"]]
    for page in a["pages"]:
        counterpart = next(p for p in b["pages"] if p["page"] == page["page"])
        assert counterpart["status"] == page["status"].replace("only_in_b", "only_in_a")
        if page["status"] == "built":
            for side, opposite in (("a", "b"), ("b", "a")):
                assert np.array_equal(read(folder / run["folder"] / f"p{page['page']}-c-{side}.png"),
                                      read(folder / other["folder"] / f"p{page['page']}-c-{opposite}.png"))

sources = json.loads((root / "docs/verification/t3-1b-acceptance.json").read_text())["product_source_sha256"]
assert all(hashlib.sha256((root / p).read_bytes()).hexdigest() == h for p, h in sources.items())
record = dict(opencv=cv2.__version__, runs=len(runs), comparisons=checks, mappings=mappings, unpaired=unpaired,
              selected_runs=sum("selected" in r for r in runs),
              verified_bands=sum(p["status"] == "band_verified" for r in runs for p in r["result"]["links"]),
              verified_links_with_both_endpoints=sum(p["both_endpoints_accounted_for"] for r in runs for p in r["result"]["range_correspondence"]),
              gate_ready_runs=sum(r["result"].get("gate", {}).get("ready", False) for r in runs),
              selected_links=sum(len(r["result"].get("gate", {}).get("selected_links", [])) for r in runs),
              bidirectional_results_equal=True, product_sources_unchanged=len(sources), automatic_carry_adoptions=0)
(folder / "candidate-reference.json").write_text(json.dumps(record, indent=2) + "\n")
print(f"{len(runs)}実行、{len(checks)}参照比較、{len(mappings)}比較面の元画素と範囲が一致。製品への採用なし。")
