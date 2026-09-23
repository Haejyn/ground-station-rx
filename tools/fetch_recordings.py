#!/usr/bin/env python3
"""
실제 위성 녹음 내려받기 — 저장소에는 녹음을 넣지 않고, 고정된 주소·크기·sha256 만 둔다.

  python tools/fetch_recordings.py                 # → recordings/ (또는 환경 변수 GSRX_RECORDINGS)
  python tools/fetch_recordings.py --dir D:/iq     # 다른 폴더
  python tools/fetch_recordings.py --check         # 받지 않고 있는 파일의 sha256 만 확인

출처: CAMRAS 드빙겔로 25 m 전파망원경 원시 녹음 https://data.camras.nl/satellites/raw/ (페이지 표기 CC BY 4.0).
서버가 연결 하나당 약 50 KB/s 로 느려서 바이트 범위 요청을 여러 개 동시에 보낸다(24 개면 약 0.8 MB/s).
"""
from __future__ import annotations

import argparse
import hashlib
import os
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BASE = "https://data.camras.nl/satellites/raw/"
SHORT = "asrtu_2024_12_09_07_57_51_435.300MHz_1.00Msps_ci16_le"

# (파일 이름, 크기, sha256) — 내용이 바뀌면 시험 결과가 달라지므로 고정한다
RECORDINGS = [
    (f"{SHORT}.sigmf-meta", 569, "956e47f6e77059cf45456c988b125236318fe0c8edb80301604eaec714ac8033"),
    (f"{SHORT}.sigmf-data", 270_880_000, "e138f8decedf56e2d8b5a323fe02380ecc9fe2c92cae2bf084c76aecbf3a3b7b"),
]


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def download(url: str, out: Path, size: int, parts: int) -> None:
    step = -(-size // parts)

    def fetch(i: int) -> None:
        lo, hi = i * step, min(size, (i + 1) * step) - 1
        part = out.with_name(f"{out.name}.part{i:03d}")
        for attempt in range(30):
            have = part.stat().st_size if part.exists() else 0
            if lo + have > hi:
                return
            try:
                req = urllib.request.Request(url, headers={"Range": f"bytes={lo + have}-{hi}"})
                with urllib.request.urlopen(req, timeout=60) as r, part.open("ab") as f:
                    while chunk := r.read(1 << 16):
                        f.write(chunk)
            except OSError as e:
                print(f"  part {i} retry {attempt}: {e}", flush=True)
                time.sleep(3)
        raise RuntimeError(f"part {i} failed")

    with ThreadPoolExecutor(parts) as ex:
        list(ex.map(fetch, range(parts)))
    with out.open("wb") as f:
        for i in range(parts):
            part = out.with_name(f"{out.name}.part{i:03d}")
            f.write(part.read_bytes())
            part.unlink()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=os.environ.get("GSRX_RECORDINGS", str(ROOT / "recordings")))
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--parts", type=int, default=24)
    args = ap.parse_args()
    d = Path(args.dir)
    d.mkdir(parents=True, exist_ok=True)
    bad = 0
    for name, size, digest in RECORDINGS:
        path = d / name
        if not args.check and not (path.exists() and path.stat().st_size == size):
            print(f"downloading {name} ({size / 1e6:.1f} MB)", flush=True)
            t0 = time.time()
            if size < 1_000_000:
                urllib.request.urlretrieve(BASE + name, path)
            else:
                download(BASE + name, path, size, args.parts)
            print(f"  {time.time() - t0:.0f} s", flush=True)
        ok = path.exists() and sha256(path) == digest
        print(f"{'ok ' if ok else 'BAD'} {name}")
        bad += not ok
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
