# 기준 자료 (golden)

| 파일 | 출처 | 만드는 법 |
|---|---|---|
| `SGP4-VER.TLE` · `tcppver.out` | Vallado 등(2006) SGP4 검증 세트 — CelesTrak 배포본, python-sgp4 2.x 패키지(MIT)에 들어 있는 것을 **수정 없이** 복사 | 복사 |
| `link_skyfield.csv` | Skyfield 로 계산한 ASRTU-1 드빙겔로 패스의 거리·거리 변화율·고도각 | `python tools/gen_golden_link.py` (`--check` 로 재현 확인) |
| `rs_conventional.bin` | 파이썬 reedsolo 로 만든 관례 기저 CCSDS RS 부호어 680 개(오류 0~16 개) | `python tools/gen_golden_rs_conventional.py` |
| `asrtu1_grsatellites_frames.bin` · `asrtu1_fullpass_grsatellites_frames.bin` · `asrtu1_satnogs_iq_grsatellites_frames.bin` | ASRTU-1 녹음(CAMRAS 드빙겔로, CC BY 4.0)을 gr-satellites 5.7 로 복호한 전송 프레임 — 짧은 녹음 · 전체 패스(도플러 보정 없음) · SatNOGS IQ 덤프. 녹음 자체는 넣지 않는다 | `python tools/gen_golden_grsatellites.py` (radioconda 필요) |
