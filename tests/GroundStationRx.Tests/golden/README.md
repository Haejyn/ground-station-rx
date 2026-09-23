# 기준 자료 (golden)

| 파일 | 출처 | 만드는 법 |
|---|---|---|
| `SGP4-VER.TLE` · `tcppver.out` | Vallado 등(2006) SGP4 검증 세트 — CelesTrak 배포본, python-sgp4 2.x 패키지(MIT)에 들어 있는 것을 **수정 없이** 복사 | 복사 |
| `link_skyfield.csv` | Skyfield 로 계산한 ASRTU-1 드빙겔로 패스의 거리·거리 변화율·고도각 | `python tools/gen_golden_link.py` (`--check` 로 재현 확인) |
