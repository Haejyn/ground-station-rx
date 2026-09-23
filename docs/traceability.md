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
| REQ-DEM-01 | BPSK 버스트를 연판정 심볼로 복조한다 — 심볼율 오차·소수 타이밍·잔여 반송파를 버스트 안에서 스스로 추정한다 | `DemodulatorTests#Noiseless_AllSymbolsRecovered_UnderTimingRateAndCarrierErrors`<br>`DemodulatorTests#BitErrorRate_WithinHalfDecibelOfTheory`<br>`DemodulatorTests#Unwrap_KeepsPhaseContinuousAcrossTheCut` | 5/5 | ✅ 통과 |
| REQ-FEC-01 | CCSDS 길쌈 부호(K=7, r=1/2, G2 반전)를 연판정 비터비로 복호한다 | `CodingTests#Viterbi_NoiselessRoundTrip_AndInversionTransparency`<br>`CodingTests#Viterbi_CodingGain` | 3/3 | ✅ 통과 |
| REQ-FEC-02 | 관례 기저 RS(255,223) 를 복호한다(SpaceLink 이중 기저 복호기 + 기저 변환) | `CodingTests#BasisTransform_RoundTripsAll256Values`<br>`CodingTests#ConventionalCodewordsFromReedsolo_AreCorrectedUpTo16Errors`<br>`CodingTests#SeventeenErrors_AreNotSilentlyReturnedAsOriginal` | 3/3 | ✅ 통과 |
| REQ-RX-01 | 원시 IQ 녹음에서 전송 프레임을 복원한다 | `ReceiverTests#Asrtu1Recording_EveryTmFrameIsRecovered` | 1/1 | ✅ 통과 |

**요구사항 10개 중 10개 검증됨.**
