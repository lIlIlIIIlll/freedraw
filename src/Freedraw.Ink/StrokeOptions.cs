namespace Freedraw.Ink;

/// <summary>
/// Parametros do traco. Os defaults reproduzem o que o Excalidraw usa na
/// ferramenta de desenho livre.
/// </summary>
public sealed record StrokeOptions
{
    /// <summary>Espessura base. O Excalidraw usa strokeWidth * 4.25.</summary>
    public float Size { get; init; } = 16f;

    /// <summary>
    /// Quanto a pressao afeta a espessura, -1..1. 0 desliga (traco uniforme).
    /// Positivo afina com pressao baixa.
    /// </summary>
    public float Thinning { get; init; } = 0.6f;

    /// <summary>Suaviza os cantos do contorno, 0..1.</summary>
    public float Smoothing { get; init; } = 0.5f;

    /// <summary>
    /// ESTABILIZACAO. 0 = segue o input cru, 1 = maximo de arrasto.
    /// E este parametro, e nao um subsistema, que tira o tremor da mao.
    /// </summary>
    public float Streamline { get; init; } = 0.5f;

    /// <summary>Curva de resposta da pressao. Default do Excalidraw: sin(t*PI/2).</summary>
    public Func<float, float> Easing { get; init; } = t => MathF.Sin(t * MathF.PI / 2f);

    /// <summary>
    /// Deriva pressao da velocidade em vez de usar a real. Para mouse, que nao
    /// tem pressao. Com a mesa deve ficar false.
    /// </summary>
    public bool SimulatePressure { get; init; }

    /// <summary>Traco terminado. Muda o tratamento do ultimo ponto e das pontas.</summary>
    public bool IsComplete { get; init; }

    public float TaperStart { get; init; }
    public float TaperEnd { get; init; }
    public bool CapStart { get; init; } = true;
    public bool CapEnd { get; init; } = true;

    public Func<float, float> TaperStartEasing { get; init; } = t => t * (2f - t);
    public Func<float, float> TaperEndEasing { get; init; } = t => { t -= 1f; return t * t * t + 1f; };
}

/// <summary>Ponto ja passado pelo streamline, com vetor e comprimento acumulado.</summary>
public readonly record struct StrokePoint(
    Vec2 Point,
    float Pressure,
    Vec2 Vector,
    float Distance,
    float RunningLength);
