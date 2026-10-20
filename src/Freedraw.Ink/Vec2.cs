using System.Runtime.CompilerServices;

namespace Freedraw.Ink;

/// <summary>Vetor 2D. Os nomes seguem o perfect-freehand para o port ser conferivel.</summary>
public readonly record struct Vec2(float X, float Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    /// <summary>Perpendicular. perfect-freehand usa [y, -x].</summary>
    public Vec2 Per() => new(Y, -X);

    public float Length() => MathF.Sqrt(X * X + Y * Y);

    /// <summary>Normaliza. Vetor nulo permanece nulo em vez de virar NaN.</summary>
    public Vec2 Unit()
    {
        float len = Length();
        return len < 1e-9f ? Zero : new Vec2(X / len, Y / len);
    }

    public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    public static float Dist(Vec2 a, Vec2 b) => (a - b).Length();

    /// <summary>Distancia ao quadrado — evita a raiz em comparacoes.</summary>
    public static float Dist2(Vec2 a, Vec2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => a + (b - a) * t;

    /// <summary>Projeta: a + direcao * distancia.</summary>
    public static Vec2 Project(Vec2 a, Vec2 direction, float distance) => a + direction * distance;

    public static Vec2 RotateAround(Vec2 a, Vec2 center, float radians)
    {
        float s = MathF.Sin(radians), c = MathF.Cos(radians);
        float px = a.X - center.X, py = a.Y - center.Y;
        return new Vec2(px * c - py * s + center.X, px * s + py * c + center.Y);
    }

    public bool NearlyEquals(Vec2 other) => Dist2(this, other) < 1e-12f;
}

/// <summary>Entrada bruta de um traco: posicao mais pressao 0..1.</summary>
public readonly record struct InkPoint(float X, float Y, float Pressure)
{
    public Vec2 Position => new(X, Y);
}
