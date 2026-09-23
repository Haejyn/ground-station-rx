namespace GroundStationRx.Orbit;

/// <summary>배정밀도 3차원 벡터(km 또는 km/s). System.Numerics.Vector3 는 float 라 궤도 계산에는 모자란다.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(double s, Vec3 a) => new(s * a.X, s * a.Y, s * a.Z);

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Norm => Math.Sqrt(Dot(this));

    public static Vec3 Add(Vec3 a, Vec3 b) => a + b;
    public static Vec3 Subtract(Vec3 a, Vec3 b) => a - b;
    public static Vec3 Multiply(double s, Vec3 a) => s * a;
}
