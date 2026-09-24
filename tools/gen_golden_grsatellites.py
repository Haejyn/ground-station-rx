#!/usr/bin/env python3
"""
독립 기준값 생성기 — 같은 ASRTU-1 녹음을 **gr-satellites**(Daniel Estévez, GNU Radio)로 복호해 전송 프레임을 저장한다.

gr-satellites 는 이 저장소와 다른 구현이다: 되먹임 루프 복조(FLL band-edge · Costas · 클럭 복구), GNU Radio FEC 비터비,
libfec 계열 RS, ASM 을 매 프레임 찾는 동기. 위성 정의(ASRTU-1.yml)는 gr-satellites 저장소의 것 그대로다.
두 구현이 같은 녹음에서 같은 바이트를 내면, 복조부터 RS 까지 이 저장소의 해석이 맞다는 근거가 된다.

  python tools/gen_golden_grsatellites.py           # → tests/GroundStationRx.Tests/golden/asrtu1_grsatellites_frames.bin
  python tools/gen_golden_grsatellites.py --check   # 다시 복호해 저장된 파일과 같은지 확인

필요: radioconda(GNU Radio 3.10 + gr-satellites 5.7) — 환경 변수 RADIOCONDA 또는 ~/radioconda. 녹음은 GSRX_RECORDINGS.
설정: --f_offset 110150 Hz (녹음 중심 대비 반송파 — 이 녹음 68 초 안의 도플러 변화는 약 120 Hz 라 FLL 이 따라간다). 나머지는 기본값.
형식: 223 바이트 프레임을 복호 순서대로 이어 붙임. gr-satellites 의 KISS 출력에 섞인 8 바이트 시각 기록은 버린다.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "GroundStationRx.Tests" / "golden" / "asrtu1_grsatellites_frames.bin"
REC = "asrtu_2024_12_09_07_57_51_435.300MHz_1.00Msps_ci16_le.sigmf-data"
F_OFFSET = "110150"


def kiss_frames(blob: bytes) -> list[bytes]:
    frames, cur, esc = [], None, False
    for x in blob:
        if x == 0xC0:
            if cur:
                frames.append(bytes(cur[1:]))  # 첫 바이트는 KISS 명령
            cur = bytearray()
            continue
        if cur is None:
            continue
        if esc:
            cur.append({0xDC: 0xC0, 0xDD: 0xDB}.get(x, x))
            esc = False
        elif x == 0xDB:
            esc = True
        else:
            cur.append(x)
    return frames


def decode() -> bytes | None:
    conda_root = Path(os.environ.get("RADIOCONDA", Path.home() / "radioconda"))
    conda = conda_root / ("condabin/conda.bat" if os.name == "nt" else "bin/conda")
    rec = Path(os.environ.get("GSRX_RECORDINGS", ROOT / "recordings")) / REC
    if not conda.exists() or not rec.exists():
        print(f"건너뜀 — radioconda({conda}) 또는 녹음({rec}) 없음")
        return None
    with tempfile.TemporaryDirectory() as d:
        kiss = Path(d) / "out.kiss"
        cmd = [str(conda), "run", "-p", str(conda_root), "gr_satellites", "ASRTU-1", "--rawint16", str(rec), "--iq",
               "--samp_rate", "1e6", "--f_offset", F_OFFSET, "--kiss_out", str(kiss), "--hexdump"]
        subprocess.run(cmd, check=True, capture_output=True)
        frames = [f for f in kiss_frames(kiss.read_bytes()) if len(f) == 223]
    return b"".join(frames)


def main() -> int:
    blob = decode()
    if blob is None:
        return 0
    if "--check" in sys.argv:
        ok = OUT.read_bytes() == blob
        print("gr-satellites golden reproducible" if ok else "gr-satellites golden differs")
        return 0 if ok else 1
    OUT.write_bytes(blob)
    print(f"wrote {OUT.relative_to(ROOT)} ({len(blob) // 223} frames)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
