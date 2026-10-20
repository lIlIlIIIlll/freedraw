using Freedraw.Ink;

namespace Freedraw.App;

public enum TabletRotation { None = 0, Clockwise90 = 90, UpsideDown = 180, CounterClockwise90 = 270 }

/// <summary>
/// Converte a posicao normalizada da mesa (0..1 nos dois eixos) em pixel de tela.
///
/// Dois detalhes que nao sao obvios:
///
/// 1. A mesa reporta 0..32767 nos DOIS eixos independente da forma fisica. A
///    MT1060 tem area ativa 10x6", entao usar o valor normalizado direto
///    distorce o traco -- um circulo desenhado na mesa sai como elipse.
///
/// 2. A mesa pode estar fisicamente girada. Nesse caso os eixos trocam antes de
///    qualquer ajuste de proporcao.
/// </summary>
public sealed class TabletMapper
{
    public TabletRotation Rotation { get; init; } = TabletRotation.None;

    /// <summary>Proporcao largura/altura da area ATIVA. MT1060 = 10x6".</summary>
    public float TabletAspect { get; init; } = 10f / 6f;

    /// <summary>
    /// Preserva a proporcao da mesa dentro da tela (letterbox). Falso estica a
    /// area da mesa para a tela inteira, o que distorce mas aproveita tudo.
    /// </summary>
    public bool PreserveAspect { get; init; } = true;

    /// <summary>
    /// Sub-retangulo da mesa que sera usado, em coordenadas normalizadas 0..1.
    /// Serve para excluir uma faixa morta do digitalizador: em vez de depender do
    /// painel do driver, o app recorta a area util e a estica para a tela.
    /// Default = mesa inteira.
    /// </summary>
    public float AreaLeft { get; init; }
    public float AreaTop { get; init; }
    public float AreaRight { get; init; } = 1f;
    public float AreaBottom { get; init; } = 1f;

    /// <summary>
    /// Retangulo de destino, em coordenadas de tela. Deve ser UM monitor, nao o
    /// bounding box do desktop virtual: com dois monitores o aspecto agregado
    /// distorce o letterbox e boa parte da tela fica inalcancavel pela caneta.
    ///
    /// Mutavel porque o layout de telas muda em tempo de execucao.
    /// </summary>
    public float ScreenX { get; set; }
    public float ScreenY { get; set; }
    public float ScreenWidth { get; set; } = 1f;
    public float ScreenHeight { get; set; } = 1f;

    public void SetScreen(int x, int y, int width, int height)
    {
        ScreenX = x;
        ScreenY = y;
        ScreenWidth = Math.Max(1, width);
        ScreenHeight = Math.Max(1, height);
    }

    public Vec2 ToScreen(float u, float v)
    {
        // Recorta para a area util ANTES de girar: os limites sao definidos em
        // coordenadas fisicas da mesa, independentes da rotacao configurada.
        float spanU = MathF.Max(1e-6f, AreaRight - AreaLeft);
        float spanV = MathF.Max(1e-6f, AreaBottom - AreaTop);
        u = Math.Clamp((u - AreaLeft) / spanU, 0f, 1f);
        v = Math.Clamp((v - AreaTop) / spanV, 0f, 1f);

        (u, v) = Rotation switch
        {
            TabletRotation.Clockwise90 => (v, 1f - u),
            TabletRotation.UpsideDown => (1f - u, 1f - v),
            TabletRotation.CounterClockwise90 => (1f - v, u),
            _ => (u, v),
        };

        // O recorte muda a proporcao fisica da area utilizada, nao so o alcance.
        float croppedAspect = TabletAspect * (spanU / spanV);

        // Girar 90 graus troca largura por altura.
        float aspect = Rotation is TabletRotation.Clockwise90 or TabletRotation.CounterClockwise90
            ? 1f / croppedAspect
            : croppedAspect;

        float w = ScreenWidth, h = ScreenHeight, ox = ScreenX, oy = ScreenY;

        if (PreserveAspect)
        {
            float screenAspect = ScreenWidth / ScreenHeight;
            if (aspect > screenAspect)
            {
                // mesa mais larga que a tela: sobra em cima e embaixo
                h = ScreenWidth / aspect;
                oy += (ScreenHeight - h) / 2f;
            }
            else
            {
                w = ScreenHeight * aspect;
                ox += (ScreenWidth - w) / 2f;
            }
        }

        return new Vec2(ox + u * w, oy + v * h);
    }
}
