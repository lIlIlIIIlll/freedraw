namespace Freedraw.Input;

/// <summary>Uma amostra da caneta, ja normalizada e independente do coletor HID.</summary>
public readonly record struct PenSample
{
    /// <summary>Posicao na area ativa da mesa, 0..1. Origem no canto superior esquerdo.</summary>
    public required float X { get; init; }
    public required float Y { get; init; }

    /// <summary>Pressao 0..1, normalizada pelo LogicalMax do proprio dispositivo.</summary>
    public required float Pressure { get; init; }

    /// <summary>Inclinacao em graus, -127..127 no hardware. 0 se o coletor nao reportar.</summary>
    public required int TiltX { get; init; }
    public required int TiltY { get; init; }

    /// <summary>Ponta encostada. E isso que abre e fecha um traco.</summary>
    public required bool TipDown { get; init; }

    /// <summary>Caneta dentro do alcance de deteccao (pairando ou encostada).</summary>
    public required bool InRange { get; init; }

    public required bool BarrelDown { get; init; }
    public required bool Eraser { get; init; }

    /// <summary>Valores crus, para diagnostico.</summary>
    public required int RawX { get; init; }
    public required int RawY { get; init; }
    public required int RawPressure { get; init; }

    public required long TimestampTicks { get; init; }

    public override string ToString() =>
        $"{(TipDown ? "CONTATO" : InRange ? "hover  " : "fora   ")} " +
        $"x={X:F4} y={Y:F4} p={Pressure:F3} tilt=({TiltX},{TiltY})";
}

/// <summary>Origem do ultimo input visto. Alimenta a decisao caneta-vs-mouse.</summary>
public enum InputSource
{
    Unknown,
    Pen,
    Mouse,
}

/// <summary>Botao emitido pela mesa pela colecao HID de mouse dela.</summary>
public enum TabletButton
{
    Left,
    Right,
    Middle,
}
