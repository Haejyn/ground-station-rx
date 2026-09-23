# 📡 ground-station-rx

위성 지상국 수신 처리 (C# / .NET 10) — 원시 IQ 녹음 → 궤도 기반 도플러 보정 → 복조 → 비터비 → CCSDS 프레임

- 대상: ASRTU-1 (저궤도 큐브샛, BPSK 9k6 · CCSDS 연접 부호), 녹음은 CAMRAS 드빙겔로 25 m 전파망원경 (CC BY 4.0)
- 채널 복호 뒷단은 [SpaceLink](https://github.com/Haejyn/ccsds-downlink-reliability) v1.1, 패스 탐색은 [orbit-pass-sim](https://github.com/Haejyn/orbit-pass-sim) v1.0 을 서브모듈로 재사용

진행 중 — 요구사항 `docs/requirements.md`, 추적 `docs/traceability.md`

```
git clone --recursive …
dotnet test tests/GroundStationRx.Tests -c Release
```
