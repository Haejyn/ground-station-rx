#!/usr/bin/env python3
"""
독립 기준값 생성기 — 관례 기저 CCSDS RS(255,223) 부호어를 파이썬 reedsolo 로 만든다.

이 저장소는 SpaceLink 의 이중 기저 RS 복호기를 GF(2) 기저 변환으로 감싸 관례 기저 바이트를 복호한다.
그 감싸기가 맞는지는 **다른 구현**이 만든 관례 기저 부호어로만 판정할 수 있다 — reedsolo 는 기저 변환 없이
관례 기저에서 바로 부호화한다.

주의 — reedsolo 의 `generator` 는 "α 의 몇 제곱" 이 아니라 **원소 값**이다. CCSDS 는 β = α^11 을 쓰므로
GF(2^8)/0x187 에서 α^11 을 계산한 값 0xAD 를 넣어야 한다(11 을 넣으면 다른 부호가 된다 — 타당성 시험에서 실제로 틀렸다).

  python tools/gen_golden_rs_conventional.py          # → tests/GroundStationRx.Tests/golden/rs_conventional.bin
  python tools/gen_golden_rs_conventional.py --check

파일 형식 — 사례마다 [오류 수 1 바이트][받은 코드블록 255 바이트][원래 데이터 223 바이트]
"""
from __future__ import annotations

import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "GroundStationRx.Tests" / "golden" / "rs_conventional.bin"
CASES_PER_ERROR_COUNT = 40


def gf_mul(a: int, b: int, poly: int = 0x187) -> int:
    r = 0
    while b:
        if b & 1:
            r ^= a
        a <<= 1
        if a & 0x100:
            a ^= poly
        b >>= 1
    return r


def build() -> bytes:
    import reedsolo

    beta = 1
    for _ in range(11):
        beta = gf_mul(beta, 2)
    assert beta == 0xAD
    rs = reedsolo.RSCodec(32, nsize=255, fcr=112, prim=0x187, generator=beta, c_exp=8)
    rng = random.Random(20241209)
    out = bytearray()
    for nerr in range(0, 17):
        for _ in range(CASES_PER_ERROR_COUNT):
            data = bytes(rng.randrange(256) for _ in range(223))
            cw = bytearray(rs.encode(data))
            for pos in rng.sample(range(255), nerr):
                cw[pos] ^= rng.randrange(1, 256)
            out += bytes([nerr]) + bytes(cw) + data
    return bytes(out)


def main() -> int:
    try:
        blob = build()
    except ImportError:
        print("reedsolo 가 없어 건너뜀 (pip install reedsolo)")
        return 0
    if "--check" in sys.argv:
        ok = OUT.read_bytes() == blob
        print("rs golden reproducible" if ok else "rs golden differs")
        return 0 if ok else 1
    OUT.write_bytes(blob)
    print(f"wrote {OUT.relative_to(ROOT)} ({len(blob) // 479} cases)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
