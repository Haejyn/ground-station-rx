#!/usr/bin/env python3
"""README 그림을 만든다 — docs/img/*.png.

- doppler_pass.png  패스 전체의 예측 도플러와 실측 반송파, 잔차 — tools/PassReport 가 낸 build/report/carrier.csv
- demod.png         실제 버스트의 연판정 심볼 분포, 합성 신호 BER 과 이론 곡선 — soft.csv · ber.csv
- decode_pn.png     버스트별 복원 프레임(이 수신기 · gr-satellites 두 경로), PN 채움과 무작위 블록의 GF(256) 스펙트럼

먼저: GSRX_RECORDINGS=<녹음 폴더> dotnet run -c Release --project tools/PassReport
글꼴은 Pretendard. 없으면 공식 릴리스(v1.3.9, OFL)를 build/fonts 로 받아 쓴다 — 저장소에는 넣지 않는다.
사용: python tools/make_readme_figures.py   (matplotlib · numpy)
"""
from __future__ import annotations

import csv
import math
import os
import urllib.request
import zipfile
from datetime import datetime
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from matplotlib import font_manager

ROOT = Path(__file__).resolve().parent.parent
REPORT = ROOT / "build" / "report"
GOLDEN = ROOT / "tests" / "GroundStationRx.Tests" / "golden"
OUT = ROOT / "docs" / "img"
PRETENDARD_URL = "https://github.com/orioncactus/pretendard/releases/download/v1.3.9/Pretendard-1.3.9.zip"
BLACK, GRAY, LIGHT, BLUE, RED = "#111111", "#6b6b6b", "#d9d9d9", "#1f4e79", "#b03a2e"
T0 = datetime.fromisoformat("2024-12-09T07:57:51")  # 짧은 녹음 시작
NOMINAL = 435.4e6


def setup_style() -> None:
    fonts = ROOT / "build" / "fonts"
    candidates = [Path(os.environ["PRETENDARD_DIR"])] if "PRETENDARD_DIR" in os.environ else []
    candidates.append(fonts)
    files = [f for d in candidates if d.exists() for f in d.glob("Pretendard-*.ttf")]
    if not files:
        fonts.mkdir(parents=True, exist_ok=True)
        archive = fonts / "Pretendard.zip"
        urllib.request.urlretrieve(PRETENDARD_URL, archive)
        with zipfile.ZipFile(archive) as z:
            for name in z.namelist():
                if name.startswith("public/static/alternative/") and name.endswith(".ttf"):
                    (fonts / Path(name).name).write_bytes(z.read(name))
        files = list(fonts.glob("Pretendard-*.ttf"))
    for f in files:
        font_manager.fontManager.addfont(str(f))
    plt.rcParams.update({
        "font.family": "Pretendard", "font.size": 9, "axes.unicode_minus": False,
        "axes.linewidth": 0.8, "axes.edgecolor": BLACK, "axes.labelcolor": BLACK,
        "xtick.direction": "in", "ytick.direction": "in", "xtick.top": True, "ytick.right": True,
        "xtick.major.size": 3.5, "ytick.major.size": 3.5, "xtick.minor.size": 2, "ytick.minor.size": 2,
        "xtick.color": BLACK, "ytick.color": BLACK,
        "legend.frameon": False, "legend.fontsize": 8.5, "figure.dpi": 200, "savefig.bbox": "tight",
        "savefig.pad_inches": 0.05, "lines.linewidth": 1.0,
    })


def panel(ax, letter: str) -> None:
    ax.text(-0.02, 1.02, f"({letter})", transform=ax.transAxes, ha="right", va="bottom", fontsize=10, fontweight="semibold")


def seconds(utc: str) -> float:
    return (datetime.fromisoformat(utc.rstrip("Z")[:26]) - T0).total_seconds()


def read_csv(name: str) -> list[dict[str, str]]:
    with (REPORT / name).open(encoding="utf-8") as f:
        return list(csv.DictReader(f))


def doppler_pass() -> None:
    rows = read_csv("carrier.csv")
    t = np.array([seconds(r["utc"]) for r in rows])
    meas = np.array([float(r["measured_hz"]) for r in rows]) - NOMINAL
    pred = np.array([float(r["predicted_hz"]) for r in rows]) - NOMINAL
    res = meas - pred
    offset = float(np.median(res))

    fig, (a, b) = plt.subplots(2, 1, figsize=(6.4, 4.6), sharex=True, gridspec_kw={"height_ratios": [1.6, 1], "hspace": 0.12})
    a.plot(t, pred / 1e3, color=GRAY, lw=0.9, label="SGP4 예측 도플러")
    a.plot(t, meas / 1e3, ".", color=BLUE, ms=2.2, label="실측 반송파 (0.2 초 구간)")
    a.set_ylabel("435.4 MHz 기준 (kHz)")
    a.legend(loc="upper right")
    panel(a, "a")

    b.axhline(offset, color=GRAY, lw=0.8, ls="--")
    b.plot(t, res, ".", color=BLUE, ms=2.2)
    b.set_ylabel("실측 − 예측 (Hz)")
    b.set_xlabel("녹음 시작 뒤 시간 (초, 2024-12-09 07:57:51 UTC)")
    b.set_ylim(offset - 25, offset + 25)
    miss = seconds("2024-12-09T08:04:17")
    for ax in (a, b):
        ax.axvspan(miss - 2, miss + 2.5, color=LIGHT, lw=0)
    b.text(miss + 4, offset + 17, "신호 없음", color=GRAY, fontsize=8)
    b.text(t[0] + 5, offset + 17, f"중앙값 {offset:.0f} Hz", color=GRAY, fontsize=8)
    panel(b, "b")
    fig.savefig(OUT / "doppler_pass.png")
    plt.close(fig)


def demod() -> None:
    soft = np.array([float(r["soft"]) for r in read_csv("soft.csv")])
    ber = read_csv("ber.csv")
    e = np.array([float(r["ebn0_db"]) for r in ber])
    m = np.array([float(r["ber"]) for r in ber])
    theory_x = np.linspace(0, 8, 200)
    theory = 0.5 * np.array([math.erfc(math.sqrt(10 ** (x / 10))) for x in theory_x])

    fig, (a, b) = plt.subplots(1, 2, figsize=(6.4, 2.6), gridspec_kw={"wspace": 0.32})
    a.hist(soft, bins=120, range=(-1.6, 1.6), color=BLUE, lw=0)
    a.set_xlabel("연판정 값")
    a.set_ylabel("심볼 수")
    a.set_xlim(-1.6, 1.6)
    panel(a, "a")

    b.semilogy(theory_x, theory, color=GRAY, lw=0.9, label="이론 ½·erfc(√(Eb/N0))")
    b.semilogy(e, m, "o", color=BLUE, ms=3.2, mfc="white", mew=0.9, label="이 복조기")
    b.set_xlabel("Eb/N0 (dB)")
    b.set_ylabel("비트 오류율")
    b.set_xlim(-0.3, 7.5)
    b.set_ylim(3e-4, 0.2)
    b.legend(loc="lower left")
    panel(b, "b")
    fig.savefig(OUT / "demod.png")
    plt.close(fig)


def gf_spectrum_nonzero(v: bytes) -> np.ndarray:
    exp = [0] * 510
    log = [0] * 256
    x = 1
    for i in range(255):
        exp[i] = x
        log[x] = i
        x <<= 1
        if x & 0x100:
            x ^= 0x187
    for i in range(255, 510):
        exp[i] = exp[i - 255]

    def mul(p: int, q: int) -> int:
        return 0 if p == 0 or q == 0 else exp[log[p] + log[q]]

    out = np.zeros(255, bool)
    for k in range(255):
        acc = 0
        for byte in v:
            acc = mul(acc, exp[k]) ^ byte
        out[k] = acc != 0
    return out


def pn_bytes() -> bytes:
    s = [1] * 8
    while len(s) < 255 * 8:
        n = len(s) - 8
        s.append(s[n + 7] ^ s[n + 5] ^ s[n + 3] ^ s[n])
    return bytes(int("".join(map(str, s[i:i + 8])), 2) for i in range(0, 255 * 8, 8))


def decode_pn() -> None:
    bursts = read_csv("bursts.csv")
    frames = read_csv("frames.csv")
    burst_key = {(r["recording"], r["index"]): seconds(r["start_utc"]) for r in bursts}
    mc_time = {int(f["mc"]): burst_key[(f["recording"], f["burst"])] for f in frames}
    ours = {}
    for f in frames:
        k = burst_key[(f["recording"], f["burst"])]
        ours[k] = ours.get(k, 0) + 1

    def ref_counts(name: str) -> dict[float, int]:
        blob = (GOLDEN / name).read_bytes()
        seen = {blob[i:i + 223] for i in range(0, len(blob), 223)}
        c: dict[float, int] = {}
        for fr in seen:
            k = mc_time[fr[2]]
            c[k] = c.get(k, 0) + 1
        return c

    raw = ref_counts("asrtu1_fullpass_grsatellites_frames.bin")
    for k, v in ref_counts("asrtu1_grsatellites_frames.bin").items():
        raw[k] = raw.get(k, 0) + v
    satnogs = ref_counts("asrtu1_satnogs_iq_grsatellites_frames.bin")

    fig, (a, b) = plt.subplots(2, 1, figsize=(6.4, 4.4), gridspec_kw={"height_ratios": [1.5, 1], "hspace": 0.45})
    xs = sorted(ours)
    w = 6
    a.bar([x - w for x in xs], [ours[x] for x in xs], width=w, color=BLUE, lw=0, label="이 수신기 (원시 IQ)")
    a.bar(xs, [raw.get(x, 0) for x in xs], width=w, color=GRAY, lw=0, label="gr-satellites (원시 IQ)")
    a.bar([x + w for x in xs], [satnogs.get(x, 0) for x in xs], width=w, color=LIGHT, lw=0, label="gr-satellites (SatNOGS IQ)")
    miss = seconds("2024-12-09T08:04:17")
    a.annotate("신호 없음", xy=(miss, 0.2), xytext=(miss, 3.2), ha="center", fontsize=8, color=GRAY,
               arrowprops={"arrowstyle": "-", "color": GRAY, "lw": 0.7})
    a.set_ylabel("버스트당 프레임")
    a.set_xlabel("녹음 시작 뒤 시간 (초)")
    a.set_ylim(0, 9.5)
    a.legend(loc="upper center", ncol=3, bbox_to_anchor=(0.5, 1.2))
    panel(a, "a")

    pn = pn_bytes()
    fill = bytes(pn[(41 - j) % 255] ^ pn[j] for j in range(255))
    rng = np.random.default_rng(1)
    rand = bytes(rng.integers(0, 256, 255, dtype=np.uint8))
    roots = sorted((11 * (112 + i)) % 255 for i in range(32))
    for r in roots:
        b.axvline(r, color=RED, lw=0.6, alpha=0.5)
    for row, (name, v) in enumerate([("무작위 블록", rand), ("PN 채움 블록", fill)]):
        nz = np.flatnonzero(gf_spectrum_nonzero(v))
        b.plot(nz, np.full(len(nz), row), "|", color=BLUE if row else GRAY, ms=9, mew=1.1)
    b.set_yticks([0, 1], ["무작위 블록", "PN 채움 블록"])
    b.set_ylim(-0.6, 1.6)
    b.set_xlim(0, 255)
    b.set_xlabel("GF(256) 스펙트럼 위치 k (α^k 에서 값이 0 이 아닌 곳)")
    b.text(252, 1.25, "빨간 선: RS 근 32 개", color=RED, fontsize=8, ha="right", backgroundcolor="white")
    panel(b, "b")
    fig.savefig(OUT / "decode_pn.png")
    plt.close(fig)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    setup_style()
    doppler_pass()
    demod()
    decode_pn()
    print("wrote", ", ".join(p.name for p in sorted(OUT.glob("*.png"))))


if __name__ == "__main__":
    main()
