#!/usr/bin/env python3
"""FFmpeg を含まない macOS 両アーキテクチャのローカル NuGet を生成する。"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / "out/native-build"
VERSION = "4.13.0.20260627"
PACKAGE = "ReportDiff.OpenCvSharp4.runtime.osx"
SOURCES = {
    "opencv": (
        "https://codeload.github.com/opencv/opencv/tar.gz/4.13.0",
        "1d40ca017ea51c533cf9fd5cbde5b5fe7ae248291ddf2af99d4c17cf8e13017d",
        "opencv-4.13.0",
    ),
    "opencvsharp": (
        "https://codeload.github.com/shimat/opencvsharp/tar.gz/b161e7e012f5101f6d5dc68a835c59db6cc88b18",
        "65e6715c66115285db30db5ff66cde8aeb36463dc244039fca01dee64db1b0f9",
        "opencvsharp-b161e7e012f5101f6d5dc68a835c59db6cc88b18",
    ),
}


def run(*args, **kwargs):
    print("実行:", " ".join(map(str, args)), flush=True)
    return subprocess.run(list(map(str, args)), check=True, **kwargs)


def source(name):
    url, expected, directory = SOURCES[name]
    downloads = WORK / "downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    archive = downloads / (name + ".tar.gz")
    if not archive.exists():
        with urllib.request.urlopen(url, timeout=120) as response:
            archive.write_bytes(response.read())
    if hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
        raise RuntimeError(f"ソースの SHA-256 が一致しません: {archive}")
    destination = downloads / directory
    if not destination.exists():
        with tarfile.open(archive) as compressed:
            compressed.extractall(downloads, filter="data")
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jobs", type=int, default=min(os.cpu_count() or 2, 6))
    args = parser.parse_args()
    if platform.system() != "Darwin":
        parser.error("このスクリプトは macOS と Xcode Command Line Tools が必要です。")
    if args.jobs < 1:
        parser.error("--jobs は 1 以上を指定してください。")

    opencv = source("opencv")
    wrapper = source("opencvsharp")
    tools = WORK / "tools"
    if not (tools / "bin/python").exists():
        run("python3", "-m", "venv", tools)
    run(tools / "bin/python", "-m", "pip", "install", "cmake==4.3.0", "ninja==1.13.0")
    cmake = tools / "bin/cmake"
    ninja = tools / "bin/ninja"
    assets = {}
    evidence = {}
    for rid, arch in [("osx-arm64", "arm64"), ("osx-x64", "x86_64")]:
        build = WORK / rid
        install = build / "install"
        common = ["-G", "Ninja", f"-DCMAKE_MAKE_PROGRAM={ninja}",
                  "-DCMAKE_BUILD_TYPE=Release", f"-DCMAKE_OSX_ARCHITECTURES={arch}",
                  "-DCMAKE_SYSTEM_NAME=Darwin", f"-DCMAKE_SYSTEM_PROCESSOR={arch}",
                  "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0"]
        run(cmake, "--fresh", "-S", opencv, "-B", build / "opencv", *common,
            f"-DCMAKE_INSTALL_PREFIX={install}", "-DBUILD_SHARED_LIBS=OFF",
            "-DBUILD_LIST=core,imgproc,imgcodecs", "-DBUILD_TESTS=OFF",
            "-DBUILD_PERF_TESTS=OFF", "-DBUILD_EXAMPLES=OFF", "-DBUILD_DOCS=OFF",
            "-DBUILD_opencv_apps=OFF", "-DBUILD_JAVA=OFF", "-DBUILD_opencv_python3=OFF",
            "-DWITH_FFMPEG=OFF", "-DWITH_GSTREAMER=OFF", "-DWITH_AVFOUNDATION=OFF",
            "-DWITH_1394=OFF", "-DWITH_V4L=OFF", "-DWITH_GTK=OFF", "-DWITH_QT=OFF",
            "-DWITH_IPP=OFF", "-DWITH_ITT=OFF", "-DWITH_OPENCL=OFF", "-DWITH_LAPACK=OFF",
            "-DWITH_KLEIDICV=OFF",
            "-DWITH_EIGEN=OFF", "-DWITH_ADE=OFF", "-DWITH_PROTOBUF=OFF",
            "-DWITH_OPENEXR=OFF", "-DWITH_OPENJPEG=OFF", "-DWITH_JASPER=OFF",
            "-DWITH_WEBP=OFF", "-DWITH_AVIF=OFF", "-DWITH_JPEGXL=OFF",
            "-DBUILD_JPEG=ON", "-DBUILD_PNG=ON", "-DBUILD_TIFF=ON",
            "-DBUILD_ZLIB=ON", "-DWITH_ZLIB_NG=OFF",
            "-DCMAKE_IGNORE_PREFIX_PATH=/opt/homebrew;/usr/local")
        run(cmake, "--build", build / "opencv", "--parallel", args.jobs)
        run(cmake, "--install", build / "opencv")
        run(cmake, "--fresh", "-S", ROOT / "tools/native", "-B", build / "wrapper", *common,
            f"-DOpenCV_DIR={install}/lib/cmake/opencv4", f"-DOPENCVSHARP_SOURCE={wrapper}")
        run(cmake, "--build", build / "wrapper", "--parallel", args.jobs)
        library = build / "wrapper/libOpenCvSharpExtern.dylib"
        architecture = run("lipo", "-archs", library, capture_output=True, text=True).stdout.strip()
        if architecture != arch:
            raise RuntimeError(f"アーキテクチャが一致しません: {rid}: {architecture}")
        linked = run("otool", "-L", library, capture_output=True, text=True).stdout
        symbols = run("nm", "-g", library, capture_output=True, text=True).stdout
        strings = run("strings", library, capture_output=True, text=True).stdout
        if any(token in symbols.lower() for token in ["_avcodec_", "_avformat_", "_swscale_", "_sws_"]):
            raise RuntimeError(f"FFmpeg のシンボルが含まれています: {rid}")
        if "ffmpeg" in linked.lower() or "lgpl" in strings.lower():
            raise RuntimeError(f"許可していないネイティブ依存が含まれています: {rid}")
        for line in linked.splitlines()[1:]:
            dependency = line.strip().split(" (")[0]
            if not dependency.startswith(("/usr/lib/", "/System/Library/", "@rpath/libOpenCvSharpExtern")):
                raise RuntimeError(f"再配布できないローカル依存です: {dependency}")
        assets[f"runtimes/{rid}/native/libOpenCvSharpExtern.dylib"] = library.read_bytes()
        evidence[rid] = {"sha256": hashlib.sha256(library.read_bytes()).hexdigest(),
                         "size_bytes": library.stat().st_size, "linked_libraries": linked}

    packages = ROOT / "out/packages"
    packages.mkdir(parents=True, exist_ok=True)
    package = packages / f"{PACKAGE}.{VERSION}.nupkg"
    assets[f"{PACKAGE}.nuspec"] = f'''<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{PACKAGE}</id><version>{VERSION}</version><authors>ReportDiff contributors</authors>
    <description>ReportDiff 用の FFmpeg なし macOS arm64 / x64 ランタイム。OpenCV 4.13.0 の core、imgproc、imgcodecs のみ。</description>
    <license type="file">licenses/NOTICE.md</license>
    <dependencies><group targetFramework="net10.0" /></dependencies>
  </metadata>
</package>
'''.encode()
    assets["licenses/OpenCvSharp-LICENSE"] = (wrapper / "LICENSE").read_bytes()
    assets["licenses/OpenCV-LICENSE"] = (opencv / "LICENSE").read_bytes()
    # コーデック内の許諾文もライブラリと一緒に保持する。
    for directory in ["libjpeg-turbo", "libpng", "libtiff", "zlib"]:
        for item in (opencv / "3rdparty" / directory).rglob("*"):
            if item.is_file() and (item.name.lower().startswith(("license", "copying")) or item.name == "README.ijg"):
                assets[f"licenses/{directory}/{item.relative_to(opencv / '3rdparty' / directory)}"] = item.read_bytes()
    assets["licenses/NOTICE.md"] = (ROOT / "tools/native/NOTICE.md").read_bytes()
    assets["build-evidence.json"] = json.dumps(evidence, indent=2).encode()
    assets["[Content_Types].xml"] = b'''<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="nuspec" ContentType="application/octet"/><Default Extension="dylib" ContentType="application/octet"/><Default Extension="md" ContentType="text/plain"/><Default Extension="json" ContentType="application/json"/></Types>'''
    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(assets.items()):
            info = zipfile.ZipInfo(name, (2026, 6, 27, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
    (WORK / "build-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print(f"生成しました: {package}", flush=True)


if __name__ == "__main__":
    main()
