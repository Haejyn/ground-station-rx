using System.Numerics;

namespace GroundStationRx.Dsp;

/// <summary>기수-2 복소 FFT (제자리, 길이는 2 의 거듭제곱). 주파수 추정용이라 속도보다 단순함을 택했다.</summary>
public static class Fft
{
    public static void Forward(Span<Complex> x)
    {
        int n = x.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException($"FFT 길이 {n} 는 2 의 거듭제곱이어야 한다", nameof(x));

        // 비트 뒤집기 순서로 재배치
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (x[i], x[j]) = (x[j], x[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < half; k++)
                {
                    var u = x[i + k];
                    var v = x[i + k + half] * w;
                    x[i + k] = u + v;
                    x[i + k + half] = u - v;
                    w *= wlen;
                }
            }
        }
    }
}
