#!/usr/bin/env python3
"""発行済み Windows exe と手順・設定例・許諾文を検証して ZIP にまとめる。"""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("bundle", ROOT / "tools/inspect-win-bundle.py")
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def package(exe, output):
    if output.exists():
        raise ValueError(f"出力先が既に存在します。別の ZIP 名を指定してください: {output}")
    inspection = bundle.inspect(exe)
    approved = json.loads((ROOT / "licenses/dependencies.json").read_text(encoding="utf-8"))
    if inspection["dependencies"] != approved["dependencies"]:
        raise ValueError("配布依存の版または構成が変わっています。許諾文と dependencies.json を確認してください。")
    native = {item["name"]: item["sha256"] for item in inspection["required_native_libraries"]}
    if native != approved["native_sha256"]:
        raise ValueError("ネイティブ DLL が許諾文を確認した版と一致しません。")
    if inspection["runtime_options"].get("includedFrameworks") != approved["included_frameworks"]:
        raise ValueError("自己完結ランタイムが許諾文を確認した版と一致しません。")
    sources = json.loads((ROOT / "licenses/sources.json").read_text(encoding="utf-8"))
    for entry in sources["files"]:
        if digest((ROOT / "licenses" / entry["file"]).read_bytes()) != entry["sha256"]:
            raise ValueError(f"許諾文のハッシュが一致しません: {entry['file']}")

    # 実行ファイルは検査済みの 1 個だけ。PDB や生の DLL は配布しない。
    files = {"reportdiff.exe": exe.read_bytes()}
    if digest(files["reportdiff.exe"]) != inspection["sha256"]:
        raise ValueError("検査後に exe が変更されました。再実行してください。")
    for name in ["LICENSE", "README.md", "THIRD_PARTY_NOTICES.md", "examples/settings.yaml"]:
        files[name] = (ROOT / name).read_bytes()
    for path in sorted((ROOT / "examples").rglob("*.yaml")):
        files[path.relative_to(ROOT).as_posix()] = path.read_bytes()
    # README と文書間の相対リンクを保つため、docs は一式を同梱する。
    for directory in ["docs", "licenses"]:
        for path in sorted((ROOT / directory).rglob("*")):
            if path.is_file() and path.name != ".DS_Store":
                files[path.relative_to(ROOT).as_posix()] = path.read_bytes()
    inspection["file"] = "reportdiff.exe"
    files["bundle-inspection.json"] = (json.dumps(inspection, ensure_ascii=False, indent=2) + "\n").encode()
    manifest = {name: {"sha256": digest(data), "size_bytes": len(data)} for name, data in sorted(files.items())}
    files["manifest.json"] = (json.dumps({"files": manifest}, ensure_ascii=False, indent=2) + "\n").encode()
    output.parent.mkdir(parents=True, exist_ok=True)
    # 検証途中の ZIP を配布先へ残さない。完成後も既存ファイルは上書きしない。
    with tempfile.TemporaryDirectory(prefix="reportdiff-package-") as temporary:
        staged = Path(temporary) / "reportdiff.zip"
        with zipfile.ZipFile(staged, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data in sorted(files.items()):
                archive.writestr(name, data)
        with zipfile.ZipFile(staged) as archive:
            if archive.testzip() is not None or set(archive.namelist()) != set(files):
                raise ValueError("ZIP の整合性検証に失敗しました。")
            for name, entry in manifest.items():
                if digest(archive.read(name)) != entry["sha256"]:
                    raise ValueError(f"ZIP 内のファイルが一致しません: {name}")
        with output.open("xb") as target:
            target.write(staged.read_bytes())
    print(json.dumps({"file": str(output), "size_bytes": output.stat().st_size,
                      "sha256": digest(output.read_bytes()), "files": len(files),
                      "exe_size_bytes": inspection["size_bytes"]}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", type=Path)
    parser.add_argument("zip", type=Path)
    arguments = parser.parse_args()
    try:
        package(arguments.exe, arguments.zip)
    except (ValueError, OSError) as error:
        parser.exit(1, f"配布物を作成できません: {error}\n")
