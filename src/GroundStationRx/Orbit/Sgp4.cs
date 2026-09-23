namespace GroundStationRx.Orbit;

/// <summary>SGP4 전파 결과 — TEME 좌표계의 위치(km)·속도(km/s).</summary>
public readonly record struct TemeState(Vec3 PositionKm, Vec3 VelocityKmS);

/// <summary>SGP4 가 궤도를 계산하지 못한 이유(Vallado 오류 코드 1·2·3·4·6).</summary>
public enum Sgp4Error
{
    None = 0,
    MeanEccentricityOutOfRange = 1,
    MeanMotionNegative = 2,
    PerturbedEccentricityOutOfRange = 3,
    SemiLatusRectumNegative = 4,
    Decayed = 6,
}

/// <summary>궤도를 계산할 수 없는 시각에서 던진다(예: 대기권에 떨어진 뒤).</summary>
public sealed class Sgp4Exception : Exception
{
    public Sgp4Error Error { get; }
    public Sgp4Exception(Sgp4Error error, string message) : base(message) => Error = error;
    public Sgp4Exception() { }
    public Sgp4Exception(string message) : base(message) { }
    public Sgp4Exception(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// SGP4 근지구 궤도 전파기 — Vallado, Crawford, Hujsak, Kelso (2006) "Revisiting Spacetrack Report #3" 의
/// 기준 구현을 옮겼다. 변수 이름과 계산 순서를 원본과 같게 두어 줄 단위로 대조할 수 있게 했다.
///
/// 범위를 좁힌 것 두 가지:
/// - **근지구(주기 225 분 미만)만** 다룬다. 심우주(SDP4, 달·태양 섭동과 공명 항)는 지상국 저궤도 위성에 필요 없어
///   구현하지 않았고, 주기 225 분 이상 TLE 는 생성자에서 거부한다 — 조용히 틀린 궤도를 내지 않는다.
/// - 중력 상수는 WGS-72, 동작 모드는 'i'(improved) — TLE 를 만든 쪽과 같은 조합이라야 한다.
///   Vallado 검증 출력 <c>tcppver.out</c> 이 이 조합으로 만들어졌고, 시험이 그 파일과 직접 대조한다.
/// </summary>
public sealed class Sgp4
{
    // WGS-72 (Vallado getgravconst "wgs72")
    private const double Mu = 398600.8;
    private const double RadiusEarthKm = 6378.135;
    private static readonly double Xke = 60.0 / Math.Sqrt(RadiusEarthKm * RadiusEarthKm * RadiusEarthKm / Mu);
    private const double J2 = 0.001082616;
    private const double J3 = -0.00000253881;
    private const double J4 = -0.00000165597;
    private const double J3oJ2 = J3 / J2;
    private const double TwoPi = 2.0 * Math.PI;
    private const double X2o3 = 2.0 / 3.0;
    private const double Temp4 = 1.5e-12;

    public Tle Tle { get; }

    // sgp4init 이 채우는 값 — 이름은 Vallado 와 같다
    private readonly double _bstar, _ecco, _argpo, _inclo, _mo, _nodeo, _noUnkozai;
    private readonly bool _isimp;
    private readonly double _con41, _cc1, _cc4, _cc5, _d2, _d3, _d4, _delmo, _eta, _argpdot, _omgcof, _sinmao;
    private readonly double _t2cof, _t3cof, _t4cof, _t5cof, _x1mth2, _x7thm1, _mdot, _nodedot, _xlcof, _xmcof, _nodecf, _aycof;

    public Sgp4(Tle tle)
    {
        ArgumentNullException.ThrowIfNull(tle);
        Tle = tle;
        const double deg2rad = Math.PI / 180.0;
        const double xpdotp = 1440.0 / (2.0 * Math.PI); // rev/day → rad/min

        _bstar = tle.Bstar;
        _ecco = tle.Eccentricity;
        _argpo = tle.ArgPerigeeDeg * deg2rad;
        _inclo = tle.InclinationDeg * deg2rad;
        _mo = tle.MeanAnomalyDeg * deg2rad;
        _nodeo = tle.RaanDeg * deg2rad;
        double noKozai = tle.MeanMotionRevPerDay / xpdotp;

        // ---------------- initl: Kozai 평균 운동 되돌리기 ----------------
        double eccsq = _ecco * _ecco;
        double omeosq = 1.0 - eccsq;
        double rteosq = Math.Sqrt(omeosq);
        double cosio = Math.Cos(_inclo);
        double cosio2 = cosio * cosio;
        double ak = Math.Pow(Xke / noKozai, X2o3);
        double d1 = 0.75 * J2 * (3.0 * cosio2 - 1.0) / (rteosq * omeosq);
        double del = d1 / (ak * ak);
        double adel = ak * (1.0 - del * del - del * (1.0 / 3.0 + 134.0 * del * del / 81.0));
        del = d1 / (adel * adel);
        _noUnkozai = noKozai / (1.0 + del);

        double ao = Math.Pow(Xke / _noUnkozai, X2o3);
        double sinio = Math.Sin(_inclo);
        double po = ao * omeosq;
        double con42 = 1.0 - 5.0 * cosio2;
        _con41 = -con42 - cosio2 - cosio2;
        double posq = po * po;
        double rp = ao * (1.0 - _ecco);

        if (TwoPi / _noUnkozai >= 225.0)
            throw new NotSupportedException(
                $"TLE {tle.SatelliteNumber}: 주기 {TwoPi / _noUnkozai:F1} 분 — 심우주(SDP4) 궤도는 지원하지 않는다");

        // ---------------- sgp4init: 근지구 초기화 ----------------
        double ss = 78.0 / RadiusEarthKm + 1.0;
        double qzms2ttemp = (120.0 - 78.0) / RadiusEarthKm;
        double qzms2t = qzms2ttemp * qzms2ttemp * qzms2ttemp * qzms2ttemp;

        // 근지점 고도 220 km 미만이면 단순 모델(isimp) — 고차 항력 항을 뺀다
        _isimp = rp < 220.0 / RadiusEarthKm + 1.0;
        double sfour = ss;
        double qzms24 = qzms2t;
        double perige = (rp - 1.0) * RadiusEarthKm;
        // 근지점 156 km 미만이면 대기 밀도 모델 상수 s, q0 를 바꾼다
        if (perige < 156.0)
        {
            sfour = perige - 78.0;
            if (perige < 98.0) sfour = 20.0;
            double qzms24temp = (120.0 - sfour) / RadiusEarthKm;
            qzms24 = qzms24temp * qzms24temp * qzms24temp * qzms24temp;
            sfour = sfour / RadiusEarthKm + 1.0;
        }

        double pinvsq = 1.0 / posq;
        double tsi = 1.0 / (ao - sfour);
        _eta = ao * _ecco * tsi;
        double etasq = _eta * _eta;
        double eeta = _ecco * _eta;
        double psisq = Math.Abs(1.0 - etasq);
        double coef = qzms24 * Math.Pow(tsi, 4.0);
        double coef1 = coef / Math.Pow(psisq, 3.5);
        double cc2 = coef1 * _noUnkozai * (ao * (1.0 + 1.5 * etasq + eeta * (4.0 + etasq))
                     + 0.375 * J2 * tsi / psisq * _con41 * (8.0 + 3.0 * etasq * (8.0 + etasq)));
        _cc1 = _bstar * cc2;
        double cc3 = 0.0;
        if (_ecco > 1.0e-4)
            cc3 = -2.0 * coef * tsi * J3oJ2 * _noUnkozai * sinio / _ecco;
        _x1mth2 = 1.0 - cosio2;
        _cc4 = 2.0 * _noUnkozai * coef1 * ao * omeosq *
               (_eta * (2.0 + 0.5 * etasq) + _ecco * (0.5 + 2.0 * etasq)
                - J2 * tsi / (ao * psisq) *
                  (-3.0 * _con41 * (1.0 - 2.0 * eeta + etasq * (1.5 - 0.5 * eeta))
                   + 0.75 * _x1mth2 * (2.0 * etasq - eeta * (1.0 + etasq)) * Math.Cos(2.0 * _argpo)));
        _cc5 = 2.0 * coef1 * ao * omeosq * (1.0 + 2.75 * (etasq + eeta) + eeta * etasq);
        double cosio4 = cosio2 * cosio2;
        double temp1 = 1.5 * J2 * pinvsq * _noUnkozai;
        double temp2 = 0.5 * temp1 * J2 * pinvsq;
        double temp3 = -0.46875 * J4 * pinvsq * pinvsq * _noUnkozai;
        _mdot = _noUnkozai + 0.5 * temp1 * rteosq * _con41
                + 0.0625 * temp2 * rteosq * (13.0 - 78.0 * cosio2 + 137.0 * cosio4);
        _argpdot = -0.5 * temp1 * con42 + 0.0625 * temp2 * (7.0 - 114.0 * cosio2 + 395.0 * cosio4)
                   + temp3 * (3.0 - 36.0 * cosio2 + 49.0 * cosio4);
        double xhdot1 = -temp1 * cosio;
        _nodedot = xhdot1 + (0.5 * temp2 * (4.0 - 19.0 * cosio2) + 2.0 * temp3 * (3.0 - 7.0 * cosio2)) * cosio;
        _omgcof = _bstar * cc3 * Math.Cos(_argpo);
        _xmcof = 0.0;
        if (_ecco > 1.0e-4)
            _xmcof = -X2o3 * coef * _bstar / eeta;
        _nodecf = 3.5 * omeosq * xhdot1 * _cc1;
        _t2cof = 1.5 * _cc1;
        // 경사각 180° 에서 0 으로 나누지 않게
        _xlcof = Math.Abs(cosio + 1.0) > Temp4
            ? -0.25 * J3oJ2 * sinio * (3.0 + 5.0 * cosio) / (1.0 + cosio)
            : -0.25 * J3oJ2 * sinio * (3.0 + 5.0 * cosio) / Temp4;
        _aycof = -0.5 * J3oJ2 * sinio;
        double delmotemp = 1.0 + _eta * Math.Cos(_mo);
        _delmo = delmotemp * delmotemp * delmotemp;
        _sinmao = Math.Sin(_mo);
        _x7thm1 = 7.0 * cosio2 - 1.0;

        if (!_isimp)
        {
            double cc1sq = _cc1 * _cc1;
            _d2 = 4.0 * ao * tsi * cc1sq;
            double temp = _d2 * tsi * _cc1 / 3.0;
            _d3 = (17.0 * ao + sfour) * temp;
            _d4 = 0.5 * temp * ao * tsi * (221.0 * ao + 31.0 * sfour) * _cc1;
            _t3cof = _d2 + 2.0 * cc1sq;
            _t4cof = 0.25 * (3.0 * _d3 + _cc1 * (12.0 * _d2 + 10.0 * cc1sq));
            _t5cof = 0.2 * (3.0 * _d4 + 12.0 * _cc1 * _d3 + 6.0 * _d2 * _d2 + 15.0 * cc1sq * (2.0 * _d2 + cc1sq));
        }
    }

    /// <summary>UTC 시각의 TEME 상태.</summary>
    public TemeState At(DateTime utc) => Propagate((utc - Tle.EpochUtc).TotalMinutes);

    /// <summary>에포크 뒤 <paramref name="tsince"/> 분의 TEME 상태. 계산할 수 없으면 <see cref="Sgp4Exception"/>.</summary>
    public TemeState Propagate(double tsince)
    {
        double vkmpersec = RadiusEarthKm * Xke / 60.0;

        // ------- 영년 중력·항력 갱신 -------
        double xmdf = _mo + _mdot * tsince;
        double argpdf = _argpo + _argpdot * tsince;
        double nodedf = _nodeo + _nodedot * tsince;
        double argpm = argpdf;
        double mm = xmdf;
        double t2 = tsince * tsince;
        double nodem = nodedf + _nodecf * t2;
        double tempa = 1.0 - _cc1 * tsince;
        double tempe = _bstar * _cc4 * tsince;
        double templ = _t2cof * t2;

        if (!_isimp)
        {
            double delomg = _omgcof * tsince;
            double delmtemp = 1.0 + _eta * Math.Cos(xmdf);
            double delm = _xmcof * (delmtemp * delmtemp * delmtemp - _delmo);
            double temp0 = delomg + delm;
            mm = xmdf + temp0;
            argpm = argpdf - temp0;
            double t3 = t2 * tsince;
            double t4 = t3 * tsince;
            tempa = tempa - _d2 * t2 - _d3 * t3 - _d4 * t4;
            tempe += _bstar * _cc5 * (Math.Sin(mm) - _sinmao);
            templ = templ + _t3cof * t3 + t4 * (_t4cof + tsince * _t5cof);
        }

        double nm = _noUnkozai;
        double em = _ecco;
        double inclm = _inclo;
        if (nm <= 0.0)
            throw new Sgp4Exception(Sgp4Error.MeanMotionNegative, $"평균 운동 {nm} ≤ 0");

        double am = Math.Pow(Xke / nm, X2o3) * tempa * tempa;
        nm = Xke / Math.Pow(am, 1.5);
        em -= tempe;
        if (em >= 1.0 || em < -0.001)
            throw new Sgp4Exception(Sgp4Error.MeanEccentricityOutOfRange, $"평균 이심률 {em} 가 [0, 1) 밖");
        if (em < 1.0e-6) em = 1.0e-6;
        mm += _noUnkozai * templ;
        double xlm = mm + argpm + nodem;

        nodem = nodem >= 0.0 ? nodem % TwoPi : -(-nodem % TwoPi);
        argpm = PyMod(argpm);
        xlm = PyMod(xlm);
        mm = PyMod(xlm - argpm - nodem);

        double sinim = Math.Sin(inclm);
        double cosim = Math.Cos(inclm);

        double ep = em, xincp = inclm, argpp = argpm, nodep = nodem, mp = mm;
        double sinip = sinim, cosip = cosim;

        // ------- 장주기 항 -------
        double axnl = ep * Math.Cos(argpp);
        double temp = 1.0 / (am * (1.0 - ep * ep));
        double aynl = ep * Math.Sin(argpp) + temp * _aycof;
        double xl = mp + argpp + nodep + temp * _xlcof * axnl;

        // ------- 케플러 방정식 -------
        double u = PyMod(xl - nodep);
        double eo1 = u;
        double tem5 = 9999.9;
        int ktr = 1;
        double sineo1 = 0, coseo1 = 0;
        while (Math.Abs(tem5) >= 1.0e-12 && ktr <= 10)
        {
            sineo1 = Math.Sin(eo1);
            coseo1 = Math.Cos(eo1);
            tem5 = 1.0 - coseo1 * axnl - sineo1 * aynl;
            tem5 = (u - aynl * coseo1 + axnl * sineo1 - eo1) / tem5;
            if (Math.Abs(tem5) >= 0.95) tem5 = tem5 > 0.0 ? 0.95 : -0.95;
            eo1 += tem5;
            ktr++;
        }

        // ------- 단주기 예비량 -------
        double ecose = axnl * coseo1 + aynl * sineo1;
        double esine = axnl * sineo1 - aynl * coseo1;
        double el2 = axnl * axnl + aynl * aynl;
        double pl = am * (1.0 - el2);
        if (pl < 0.0)
            throw new Sgp4Exception(Sgp4Error.SemiLatusRectumNegative, $"반통경 {pl} < 0");

        double rl = am * (1.0 - ecose);
        double rdotl = Math.Sqrt(am) * esine / rl;
        double rvdotl = Math.Sqrt(pl) / rl;
        double betal = Math.Sqrt(1.0 - el2);
        temp = esine / (1.0 + betal);
        double sinu = am / rl * (sineo1 - aynl - axnl * temp);
        double cosu = am / rl * (coseo1 - axnl + aynl * temp);
        double su = Math.Atan2(sinu, cosu);
        double sin2u = (cosu + cosu) * sinu;
        double cos2u = 1.0 - 2.0 * sinu * sinu;
        temp = 1.0 / pl;
        double temp1 = 0.5 * J2 * temp;
        double temp2 = temp1 * temp;

        // ------- 단주기 섭동 -------
        double mrt = rl * (1.0 - 1.5 * temp2 * betal * _con41) + 0.5 * temp1 * _x1mth2 * cos2u;
        su -= 0.25 * temp2 * _x7thm1 * sin2u;
        double xnode = nodep + 1.5 * temp2 * cosip * sin2u;
        double xinc = xincp + 1.5 * temp2 * cosip * sinip * cos2u;
        double mvt = rdotl - nm * temp1 * _x1mth2 * sin2u / Xke;
        double rvdot = rvdotl + nm * temp1 * (_x1mth2 * cos2u + 1.5 * _con41) / Xke;

        // ------- 방향 벡터 -------
        double sinsu = Math.Sin(su), cossu = Math.Cos(su);
        double snod = Math.Sin(xnode), cnod = Math.Cos(xnode);
        double sini = Math.Sin(xinc), cosi = Math.Cos(xinc);
        double xmx = -snod * cosi;
        double xmy = cnod * cosi;
        double ux = xmx * sinsu + cnod * cossu;
        double uy = xmy * sinsu + snod * cossu;
        double uz = sini * sinsu;
        double vx = xmx * cossu - cnod * sinsu;
        double vy = xmy * cossu - snod * sinsu;
        double vz = sini * cossu;

        if (mrt < 1.0)
            throw new Sgp4Exception(Sgp4Error.Decayed, $"궤도 반지름 {mrt:F4} 지구 반지름 — 이미 떨어졌다");

        double mr = mrt * RadiusEarthKm;
        return new TemeState(
            new Vec3(mr * ux, mr * uy, mr * uz),
            new Vec3((mvt * ux + rvdot * vx) * vkmpersec,
                     (mvt * uy + rvdot * vy) * vkmpersec,
                     (mvt * uz + rvdot * vz) * vkmpersec));
    }

    /// <summary>파이썬 % 와 같은 양수 나머지 — 원본(파이썬 판)과 음수 각에서 같은 값을 내려고.</summary>
    private static double PyMod(double x)
    {
        double r = x % TwoPi;
        return r < 0 ? r + TwoPi : r;
    }
}
