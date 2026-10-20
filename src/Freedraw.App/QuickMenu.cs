using Freedraw.Ink;
using SkiaSharp;

namespace Freedraw.App;

/// <summary>O que um gesto da caneta no menu pede para o app fazer.</summary>
public enum MenuAction { None, Changed, Close }

/// <summary>
/// Painel de ajuste rapido, aberto segurando a ponta parada.
///
/// Desenhado no proprio canvas do overlay -- nao e janela do Windows. Uma janela
/// de verdade roubaria foco e teria que lidar com o overlay topmost por cima
/// dela; aqui e so pixel e hit-test em coordenadas de tela.
///
/// Os circulos de cor sao SLOTS editaveis: tocar ativa o slot, e os sliders RGB
/// editam a cor do slot ativo no lugar -- nao ha botao de salvar, o app grava
/// os slots sozinho (<see cref="ColorSlots"/>). Nenhum toque dentro do painel
/// fecha o menu: so tocar fora. Espessura e estabilizador vivem aqui enquanto
/// o menu esta aberto; o app sincroniza na abertura e le de volta a cada
/// <see cref="MenuAction.Changed"/>.
/// </summary>
public sealed class QuickMenu
{
    public const float MinSize = 2f, MaxSize = 48f;
    public const float MinStreamline = 0f, MaxStreamline = 0.9f;   // 1.0 congela o traco

    private const float Width = 340f;
    private const float Height = 286f;
    private const float Radius = 16f;
    private const float Pad = 18f;

    private const float SwatchY = 52f;
    private const float SwatchRadius = 15f;
    private const float SwatchSpacing = 44f;

    private const float RgbFirstY = 92f;
    private const float RgbSpacing = 26f;
    private const float SizeLabelY = 184f;
    private const float SizeY = 204f;
    private const float StreamLabelY = 238f;
    private const float StreamY = 258f;

    private const float TrackLeft = 40f;      // relativo a esquerda do painel
    private const float TrackRightPad = 64f;  // espaco do valor a direita
    private const float TrackHeight = 6f;
    private const float ThumbRadius = 7f;
    // Metade da distancia entre sliders RGB: mais que isso e um toque ambiguo.
    private const float SliderHitHalfHeight = 12f;

    private enum Slider { None, R, G, B, Size, Streamline }

    public bool IsOpen { get; private set; }

    /// <summary>Um slider esta sendo arrastado agora.</summary>
    public bool IsDragging => _dragging != Slider.None;

    /// <summary>Compartilhado com o app: o menu edita os slots no lugar.</summary>
    public required ColorSlots Slots { get; init; }

    public SKColor Color => Slots.Current;
    public float Size { get; set; }
    public float Streamline { get; set; }

    private SKRect _panel;
    private Vec2 _hover;
    private Slider _dragging;

    public void Open(Vec2 at, float screenLeft, float screenTop, float screenW, float screenH)
    {
        // Abre acima do ponto, para a mao nao cobrir o painel; se nao couber,
        // desce para baixo do ponto.
        float x = at.X - Width / 2f;
        float y = at.Y - Height - 28f;
        if (y < screenTop + 8f) y = at.Y + 28f;

        x = Math.Clamp(x, screenLeft + 8f, screenLeft + screenW - Width - 8f);
        y = Math.Clamp(y, screenTop + 8f, screenTop + screenH - Height - 8f);

        _panel = new SKRect(x, y, x + Width, y + Height);
        // Sem isso o primeiro quadro acende o item sob o hover da abertura ANTERIOR.
        _hover = at;
        _dragging = Slider.None;
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        _dragging = Slider.None;
    }

    public void Hover(Vec2 point) => _hover = point;

    /// <summary>Ponta desceu. Fora do painel fecha; slot vira o ativo; slider
    /// comeca um arrasto.</summary>
    public MenuAction Down(Vec2 point)
    {
        if (!_panel.Contains(point.X, point.Y))
            return MenuAction.Close;

        int swatch = SwatchAt(point);
        if (swatch >= 0)
        {
            // Nao fecha: o proximo passo natural e ajustar a cor nos sliders.
            Slots.Active = swatch;
            return MenuAction.Changed;
        }

        _dragging = SliderAt(point);
        if (_dragging == Slider.None) return MenuAction.None;
        SetFromX(_dragging, point.X);
        return MenuAction.Changed;
    }

    /// <summary>Ponta segue encostada. O slider fica preso ao arrasto mesmo que a
    /// caneta escorregue para a linha de cima ou de baixo.</summary>
    public MenuAction Drag(Vec2 point)
    {
        if (_dragging == Slider.None) return MenuAction.None;
        SetFromX(_dragging, point.X);
        return MenuAction.Changed;
    }

    public void Up() => _dragging = Slider.None;

    // ---- geometria

    private float TrackStart => _panel.Left + TrackLeft;
    private float TrackEnd => _panel.Right - TrackRightPad;

    private float SliderY(Slider s) => _panel.Top + s switch
    {
        Slider.R => RgbFirstY,
        Slider.G => RgbFirstY + RgbSpacing,
        Slider.B => RgbFirstY + RgbSpacing * 2f,
        Slider.Size => SizeY,
        Slider.Streamline => StreamY,
        _ => 0f,
    };

    private SKPoint SwatchCenter(int i)
        => new(_panel.Left + Pad + SwatchRadius + i * SwatchSpacing, _panel.Top + SwatchY);

    private int SwatchAt(Vec2 p)
    {
        for (int i = 0; i < Slots.Slots.Length; i++)
        {
            var c = SwatchCenter(i);
            float dx = p.X - c.X, dy = p.Y - c.Y;
            float r = SwatchRadius + 6f;
            if (dx * dx + dy * dy <= r * r) return i;
        }
        return -1;
    }

    private Slider SliderAt(Vec2 p)
    {
        // Folga horizontal alem da trilha: encostar logo depois da ponta ainda
        // pega o slider e crava no extremo.
        if (p.X < TrackStart - 14f || p.X > TrackEnd + 14f) return Slider.None;
        foreach (var s in new[] { Slider.R, Slider.G, Slider.B, Slider.Size, Slider.Streamline })
            if (MathF.Abs(p.Y - SliderY(s)) <= SliderHitHalfHeight) return s;
        return Slider.None;
    }

    // ---- valor <-> posicao (t = 0..1 ao longo da trilha)

    private float Fraction(Slider s) => s switch
    {
        Slider.R => Color.Red / 255f,
        Slider.G => Color.Green / 255f,
        Slider.B => Color.Blue / 255f,
        Slider.Size => (Size - MinSize) / (MaxSize - MinSize),
        Slider.Streamline => (Streamline - MinStreamline) / (MaxStreamline - MinStreamline),
        _ => 0f,
    };

    private void SetFromX(Slider s, float x)
    {
        float t = Math.Clamp((x - TrackStart) / (TrackEnd - TrackStart), 0f, 1f);
        byte channel = (byte)MathF.Round(t * 255f);
        switch (s)
        {
            case Slider.R: Slots.Current = new SKColor(channel, Color.Green, Color.Blue); break;
            case Slider.G: Slots.Current = new SKColor(Color.Red, channel, Color.Blue); break;
            case Slider.B: Slots.Current = new SKColor(Color.Red, Color.Green, channel); break;
            // Espessura em pixel inteiro e estabilizador em passos de 1%: valor
            // quebrado so faria o numero exibido tremer durante o arrasto.
            case Slider.Size: Size = MathF.Round(MinSize + t * (MaxSize - MinSize)); break;
            case Slider.Streamline:
                Streamline = MathF.Round((MinStreamline + t * (MaxStreamline - MinStreamline)) * 100f) / 100f;
                break;
        }
    }

    private string ValueText(Slider s) => s switch
    {
        Slider.R => Color.Red.ToString(),
        Slider.G => Color.Green.ToString(),
        Slider.B => Color.Blue.ToString(),
        Slider.Size => $"{Size:0} px",
        Slider.Streamline => $"{Streamline * 100f:0}%",
        _ => "",
    };

    // ---- render

    public void Render(SKCanvas canvas)
    {
        if (!IsOpen) return;

        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 90),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(12, 12),
        };
        canvas.DrawRoundRect(_panel, Radius, Radius, shadow);

        using var background = new SKPaint { Color = new SKColor(0x1C, 0x1C, 0x1E, 0xF2), IsAntialias = true };
        canvas.DrawRoundRect(_panel, Radius, Radius, background);

        using var border = new SKPaint
        {
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0x24),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        };
        canvas.DrawRoundRect(_panel, Radius, Radius, border);

        using var label = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x8C), IsAntialias = true };
        using var value = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE0), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 11f);
        using var valueFont = new SKFont(SKTypeface.Default, 12f);

        canvas.DrawText("COR", _panel.Left + Pad, _panel.Top + 24f, font, label);
        canvas.DrawText("ESPESSURA", _panel.Left + Pad, _panel.Top + SizeLabelY, font, label);
        canvas.DrawText("ESTABILIZADOR", _panel.Left + Pad, _panel.Top + StreamLabelY, font, label);

        RenderColorChip(canvas, valueFont, value);
        RenderSwatches(canvas);

        var hovered = _dragging != Slider.None ? _dragging : SliderAt(_hover);
        foreach (var (s, letter) in new[] { (Slider.R, "R"), (Slider.G, "G"), (Slider.B, "B") })
        {
            canvas.DrawText(letter, _panel.Left + Pad, SliderY(s) + 4f, font, label);
            RenderSlider(canvas, s, hovered == s, valueFont, value);
        }
        RenderSlider(canvas, Slider.Size, hovered == Slider.Size, valueFont, value);
        RenderSlider(canvas, Slider.Streamline, hovered == Slider.Streamline, valueFont, value);
    }

    /// <summary>Cor do slot ativo em hexa, no canto do cabecalho.</summary>
    private void RenderColorChip(SKCanvas canvas, SKFont font, SKPaint text)
    {
        string hex = $"#{Color.Red:X2}{Color.Green:X2}{Color.Blue:X2}";
        float textWidth = font.MeasureText(hex);
        float right = _panel.Right - Pad;
        var chip = new SKRect(right - 22f, _panel.Top + 12f, right, _panel.Top + 30f);

        using (var fill = new SKPaint { Color = Color, IsAntialias = true })
            canvas.DrawRoundRect(chip, 5f, 5f, fill);
        using (var edge = new SKPaint
               {
                   Color = new SKColor(0xFF, 0xFF, 0xFF, 0x50),
                   IsAntialias = true,
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1f,
               })
            canvas.DrawRoundRect(chip, 5f, 5f, edge);

        canvas.DrawText(hex, chip.Left - 8f - textWidth, _panel.Top + 25f, font, text);
    }

    private void RenderSwatches(SKCanvas canvas)
    {
        int hovered = _dragging == Slider.None ? SwatchAt(_hover) : -1;

        for (int i = 0; i < Slots.Slots.Length; i++)
        {
            var center = SwatchCenter(i);

            if (i == hovered)
            {
                using var glow = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x1F), IsAntialias = true };
                canvas.DrawCircle(center, SwatchRadius + 5f, glow);
            }

            // Por indice, nao por cor: dois slots podem ter a mesma cor, e o
            // anel diz qual deles os sliders estao editando.
            if (i == Slots.Active)
            {
                using var ring = new SKPaint
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2f,
                };
                canvas.DrawCircle(center, SwatchRadius + 4f, ring);
            }

            using (var fill = new SKPaint { Color = Slots.Slots[i], IsAntialias = true })
                canvas.DrawCircle(center, SwatchRadius, fill);
            // Contorno sutil: sem ele o preto some contra o fundo escuro do painel.
            using (var edge = new SKPaint
                   {
                       Color = new SKColor(0xFF, 0xFF, 0xFF, 0x50),
                       IsAntialias = true,
                       Style = SKPaintStyle.Stroke,
                       StrokeWidth = 1f,
                   })
                canvas.DrawCircle(center, SwatchRadius, edge);
        }
    }

    private void RenderSlider(SKCanvas canvas, Slider s, bool active, SKFont font, SKPaint text)
    {
        float y = SliderY(s);
        var track = new SKRect(TrackStart, y - TrackHeight / 2f, TrackEnd, y + TrackHeight / 2f);
        float thumbX = TrackStart + Fraction(s) * (TrackEnd - TrackStart);

        using (var paint = new SKPaint { IsAntialias = true })
        {
            if (s is Slider.R or Slider.G or Slider.B)
            {
                // A trilha mostra a cor que sairia em cada ponto, mantendo os
                // outros dois canais: da para mirar a cor com o olho, nao so
                // com o numero.
                var (from, to) = s switch
                {
                    Slider.R => (new SKColor(0, Color.Green, Color.Blue), new SKColor(255, Color.Green, Color.Blue)),
                    Slider.G => (new SKColor(Color.Red, 0, Color.Blue), new SKColor(Color.Red, 255, Color.Blue)),
                    _ => (new SKColor(Color.Red, Color.Green, 0), new SKColor(Color.Red, Color.Green, 255)),
                };
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(track.Left, y), new SKPoint(track.Right, y),
                    [from, to], SKShaderTileMode.Clamp);
                canvas.DrawRoundRect(track, 3f, 3f, paint);
                paint.Shader = null;
            }
            else
            {
                paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x2A);
                canvas.DrawRoundRect(track, 3f, 3f, paint);
                paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xC0);
                canvas.DrawRoundRect(new SKRect(track.Left, track.Top, thumbX, track.Bottom), 3f, 3f, paint);
            }

            if (active)
            {
                paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x26);
                canvas.DrawCircle(thumbX, y, ThumbRadius + 6f, paint);
            }

            paint.Color = SKColors.White;
            canvas.DrawCircle(thumbX, y, active ? ThumbRadius + 1.5f : ThumbRadius, paint);
            paint.Color = new SKColor(0, 0, 0, 0x60);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1f;
            canvas.DrawCircle(thumbX, y, active ? ThumbRadius + 1.5f : ThumbRadius, paint);
        }

        string v = ValueText(s);
        canvas.DrawText(v, _panel.Right - Pad - font.MeasureText(v), y + 4f, font, text);
    }
}
