# 요구사항 추적 매트릭스 (자동 생성)

> `python tools/trace.py` 가 `docs/requirements.md` · 시험 코드의 `[Trait("Requirement", …)]` · TRX 결과로 만든다. 손으로 고치지 않는다.

| 요구사항 | 내용 | 시험 | 실행 | 판정 |
|---|---|---|---|---|
| REQ-ORB-01 | SGP4 근지구 전파가 Vallado 기준 구현과 같은 궤도를 낸다. 심우주 궤도(주기 225 분 이상)는 조용히 틀리지 않고 거부한다 | `Sgp4Tests#NearEarthCases_MatchValladoReferenceOutput`<br>`Sgp4Tests#DeepSpaceCases_AreRejectedInsteadOfPropagatedWrongly`<br>`Sgp4Tests#DecayedOrbit_ThrowsWithReason` | 3/3 | ✅ 통과 |
| REQ-ORB-02 | TLE 를 고정 열 위치대로 읽고, 형식 오류·체크섬 오류·행 번호 불일치는 거부한다 | `TleTests#Parse_ReadsEveryFieldAtItsColumn`<br>`TleTests#ImpliedDecimalFields_AreDecodedWithSignedExponent`<br>`TleTests#EverySingleDigitCorruption_IsRejectedByChecksum`<br>`TleTests#ValladoDeliberatelyBrokenCases_FailChecksum`<br>`TleTests#MalformedLines_AreRejected`<br>`TleTests#MismatchedSatelliteNumbers_AreRejected` | 13/13 | ✅ 통과 |
| REQ-ORB-03 | 위성–지상국 거리·거리 변화율·고도각을 계산한다 | `LinkGeometryTests#JulianDate_OfJ2000Epoch`<br>`LinkGeometryTests#Gmst_MatchesValladoExample3_5`<br>`LinkGeometryTests#LocalTime_IsRejected`<br>`LinkGeometryTests#RealPass_MatchesSkyfield`<br>`LinkGeometryTests#Doppler_IsPositiveWhileApproachingAndSignFlipsAtClosestApproach` | 5/5 | ✅ 통과 |
| REQ-ORB-04 | 궤도로 예측한 도플러가 실제 녹음의 반송파 변화를 설명한다 | `CarrierTests#RealRecording_CarrierFollowsPredictedDoppler` | 1/1 | ✅ 통과 |
| REQ-DOP-01 | BPSK 반송파 주파수를 변조와 무관하게 재고, 기대 주파수 창 밖의 신호는 무시한다 | `CarrierTests#Fft_MatchesDirectDft`<br>`CarrierTests#SyntheticBpsk_CarrierIsMeasuredWithinHalfHertz`<br>`CarrierTests#SearchWindow_IgnoresStrongerSignalOutsideIt` | 6/6 | ✅ 통과 |
| REQ-REC-01 | SigMF 녹음(ci16_le · cf32_le · cu8)의 메타데이터와 표본을 읽는다 | `SigmfRecordingTests#Ci16Le_MetadataAndSamples`<br>`SigmfRecordingTests#Cf32LeAndCu8_AreDecoded`<br>`SigmfRecordingTests#UnsupportedOrAmbiguousRecordings_AreRejected`<br>`SigmfRecordingTests#RealAsrtu1Recording_Metadata` | 4/4 | ✅ 통과 |

**요구사항 6개 중 6개 검증됨.**
