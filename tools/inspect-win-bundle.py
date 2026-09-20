#!/usr/bin/env python3
"""Windows 単一 exe の内部から必須ネイティブ DLL を検証する（実行はしない）。"""

import argparse
import hashlib
import io
import json
from pathlib import Path
import struct
import zlib

# .NET 10 の bundle_marker.cpp / Bundle/Manifest.cs / Bundle/FileEntry.cs の形式。
# https://github.com/dotnet/runtime/tree/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle
SIGNATURE = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")
REQUIRED = {"pdfium.dll", "OpenCvSharpExtern.dll", "libSkiaSharp.dll"}


def read_string(stream):
    length = 0
    for shift in range(0, 35, 7):
        value = stream.read(1)
        if not value:
            raise ValueError("バンドルの文字列が途中で切れています。")
        length |= (value[0] & 127) << shift
        if value[0] < 128:
            data = stream.read(length)
            if len(data) != length:
                raise ValueError("バンドルの文字列長が不正です。")
            return data.decode("utf-8")
    raise ValueError("バンドルの文字列長が不正です。")


def require_x64_pe(data, name):
    if data[:2] != b"MZ":
        raise ValueError(f"Windows PE ではありません: {name}")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", data, pe + 4)[0] != 0x8664:
        raise ValueError(f"Windows x64 ではありません: {name}")


def inspect(path):
    data = path.read_bytes()
    require_x64_pe(data, path.name)
    marker = data.find(SIGNATURE)
    if marker < 8:
        raise ValueError(".NET 単一ファイルのマーカーが見つかりません。")
    offset = struct.unpack_from("<q", data, marker - 8)[0]
    if not 0 < offset < len(data):
        raise ValueError("バンドルのヘッダー位置が不正です。")
    stream = io.BytesIO(data)
    stream.seek(offset)
    major, minor, count = struct.unpack("<III", stream.read(12))
    if major != 6 or minor != 0 or not 0 < count < 10000:
        raise ValueError(f"未対応のバンドル形式です: {major}.{minor} / {count} 件")
    bundle_id = read_string(stream)
    stream.read(40)  # deps.json・runtimeconfig.json の位置とフラグ
    native = []
    names = []
    metadata = {}
    for _ in range(count):
        start, size, compressed, kind = struct.unpack("<qqqB", stream.read(25))
        name = read_string(stream)
        if name in names:
            raise ValueError(f"バンドル内のファイル名が重複しています: {name}")
        names.append(name)
        stored_size = compressed or size
        if start < 0 or size < 0 or stored_size < 0 or start + stored_size > offset:
            raise ValueError(f"埋め込み範囲が不正です: {name}")
        if name in REQUIRED or name in {"reportdiff.deps.json", "reportdiff.runtimeconfig.json"}:
            payload = data[start:start + stored_size]
            if compressed:
                payload = zlib.decompress(payload, -15)
            if len(payload) != size:
                raise ValueError(f"DLL サイズが一致しません: {name}")
            if name.endswith(".json"):
                metadata[name] = json.loads(payload)
                continue
            if kind != 2:
                raise ValueError(f"ネイティブ DLL として記録されていません: {name}")
            require_x64_pe(payload, name)
            if name == "OpenCvSharpExtern.dll":
                for forbidden in [b"videoio_VideoCapture_new1", b"avcodec_", b"avformat_", b"libswscale license"]:
                    if forbidden in payload:
                        raise ValueError(f"除外対象の動画コードが含まれています: {forbidden!r}")
            native.append({"name": name, "size_bytes": size, "machine": "win-x64",
                           "sha256": hashlib.sha256(payload).hexdigest()})
    if {entry["name"] for entry in native} != REQUIRED:
        raise ValueError("必須ネイティブ DLL が揃っていません。")
    if any("ffmpeg" in name.lower() or name.endswith((".dylib", ".so")) for name in names):
        raise ValueError("FFmpeg または Windows 以外のネイティブ資産が含まれています。")
    if len(metadata) != 2:
        raise ValueError("依存関係またはランタイム設定の JSON がありません。")
    dependencies = {name: library["type"] for name, library in metadata["reportdiff.deps.json"]["libraries"].items()
                    if library["type"] in {"package", "runtimepack"}}
    return {"file": str(path), "size_bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
            "bundle_version": f"{major}.{minor}", "bundle_id": bundle_id,
            "embedded_files": count, "required_native_libraries": native,
            "dependencies": dependencies,
            "runtime_options": metadata["reportdiff.runtimeconfig.json"]["runtimeOptions"],
            "ffmpeg_assets": [], "foreign_native_assets": []}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", type=Path)
    arguments = parser.parse_args()
    print(json.dumps(inspect(arguments.exe), ensure_ascii=False, indent=2))
