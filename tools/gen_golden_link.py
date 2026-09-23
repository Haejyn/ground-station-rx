#!/usr/bin/env python3
"""
독립 기준값 생성기 — 위성–지상국 거리·거리 변화율·고도각을 Skyfield 로 계산한다.

이 저장소의 LinkGeometry 는 TEME → 지구 고정 변환을 **GMST 회전 한 번**으로 하고 UT1 대신 UTC 를 쓴다.
Skyfield 는 같은 SGP4 궤도를 **세차·장동·UT1(IERS 표)** 까지 넣은 다른 경로로 변환한다.
두 결과의 차이가 곧 이 저장소가 뺀 항들의 실제 크기다 — 가정한 정밀도 예산(EarthFrames 주석)을 숫자로 확인한다.

  python tools/gen_golden_link.py           # → tests/GroundStationRx.Tests/golden/link_skyfield.csv
  python tools/gen_golden_link.py --check   # 다시 계산해 저장된 파일과 허용 오차 안인지 확인

대상: ASRTU-1 (NORAD 61781) 의 2024-12-09 드빙겔로 패스 — 녹음(CAMRAS)이 있는 바로 그 패스.
TLE 는 SatNOGS 관측 10736393 이 쓴 것(에포크 24343.80, 패스 약 12.8 시간 전)을 그대로 쓴다.
"""
from __future__ import annotations

import csv
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "GroundStationRx.Tests" / "golden" / "link_skyfield.csv"

TLE1 = "1 61781U 24199AY  24343.80030339  .00013950  00000+0  57980-3 0  9999"
TLE2 = "2 61781  97.3798 208.0613 0018899 119.8177 240.4942 15.23747191 49005"
# SatNOGS 지상국 PI9RD (CAMRAS 드빙겔로) — 관측 10736393 의 station_lat/lng/alt
SITE_LAT, SITE_LON, SITE_ALT_M = 52.812, 6.396, 10.0
START = datetime(2024, 12, 9, 7, 57, 0, tzinfo=timezone.utc)
SPAN_S, STEP_S = 720, 10

RANGE_TOL_KM = 1e-6
RATE_TOL_KMS = 1e-9
EL_TOL_DEG = 1e-7


def compute() -> list[list[str]]:
    from skyfield.api import EarthSatellite, load, wgs84

    ts = load.timescale()  # 내장 IERS 표(UT1−UTC, 윤초)를 쓴다 — 네트워크 없이 재현된다
    sat = EarthSatellite(TLE1, TLE2, "ASRTU-1", ts)
    site = wgs84.latlon(SITE_LAT, SITE_LON, elevation_m=SITE_ALT_M)
    rows = []
    for k in range(0, SPAN_S + 1, STEP_S):
        t_utc = START + timedelta(seconds=k)
        t = ts.from_datetime(t_utc)
        topo = (sat - site).at(t)
        el, _az, dist, _el_rate, _az_rate, range_rate = topo.frame_latlon_and_rates(site)
        rows.append([t_utc.strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
                     f"{dist.km:.9f}", f"{range_rate.km_per_s:.12f}", f"{el.degrees:.9f}"])
    return rows


def main() -> int:
    try:
        rows = compute()
    except ImportError:
        print("skyfield 가 없어 건너뜀 (pip install skyfield)")
        return 0
    header = ["utc", "range_km", "range_rate_kms", "elevation_deg"]
    if "--check" not in sys.argv:
        OUT.parent.mkdir(parents=True, exist_ok=True)
        with OUT.open("w", newline="", encoding="utf-8") as f:
            w = csv.writer(f, lineterminator="\n")
            w.writerow(header)
            w.writerows(rows)
        print(f"wrote {OUT.relative_to(ROOT)} ({len(rows)} rows)")
        return 0
    with OUT.open(encoding="utf-8") as f:
        saved = list(csv.reader(f))[1:]
    bad = 0
    for a, b in zip(saved, rows, strict=True):
        if a[0] != b[0] or abs(float(a[1]) - float(b[1])) > RANGE_TOL_KM \
                or abs(float(a[2]) - float(b[2])) > RATE_TOL_KMS or abs(float(a[3]) - float(b[3])) > EL_TOL_DEG:
            bad += 1
            print("mismatch", a, b)
    print("link golden reproducible" if bad == 0 else f"{bad} rows differ")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
