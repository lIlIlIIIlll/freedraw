using Freedraw.Ink;
using SkiaSharp;

namespace Freedraw.App;

/// <summary>
/// Guarda os tracos e desenha. Tracos terminados viram SKPath imutavel; o traco
/// em andamento e recalculado no maximo UMA VEZ POR QUADRO.
///
/// Essa ultima parte importa: a caneta emite amostras mais rapido que a taxa de
/// render, e o recalculo percorre todos os pontos do traco. Recalcular por
/// amostra faz o custo de um traco de n pontos ser O(n^2); recalcular por quadro
/// desacopla o custo da taxa de report da mesa.
/// </summary>
public sealed class StrokeCanvas
{
    public const float EraserRadius = 22f;

    // A cor fica gravada em cada traco: trocar de cor no menu nao pode repintar
    // o que ja foi desenhado.
    private readonly List<(SKPath path, SKColor color)> _completed = [];
    private readonly List<InkPoint> _current = [];
    private readonly object _gate = new();
    private SKPath? _currentPath;
    private bool _currentDirty;

    // Reaproveitados entre quadros: o Render roda 60x por segundo e alocar um
    // SKPaint por chamada e lixo puro.
    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _cursorPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };

    public SKColor Color { get; set; } = new(0xFF, 0x2D, 0x55);
    public StrokeOptions Options { get; set; } = new() { Size = 14f };

    public bool IsDrawing { get; private set; }
    public int CompletedCount { get { lock (_gate) return _completed.Count; } }

    public void BeginStroke(Vec2 point, float pressure)
    {
        lock (_gate)
        {
            _current.Clear();
            _current.Add(new InkPoint(point.X, point.Y, pressure));
            IsDrawing = true;
            _currentDirty = true;
        }
    }

    public void AddPoint(Vec2 point, float pressure)
    {
        lock (_gate)
        {
            if (!IsDrawing) return;
            _current.Add(new InkPoint(point.X, point.Y, pressure));
            _currentDirty = true;   // o recalculo acontece no Render, uma vez por quadro
        }
    }

    public void EndStroke()
    {
        lock (_gate)
        {
            if (!IsDrawing) return;
            IsDrawing = false;

            // Recalcula com IsComplete: muda o tratamento do ultimo ponto e das
            // pontas, entao o traco final difere levemente do preview.
            var outline = FreehandStroke.GetStroke(_current, Options with { IsComplete = true });
            var path = ToPath(outline);
            if (path is not null) _completed.Add((path, Color));

            DiscardCurrent();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var (p, _) in _completed) p.Dispose();
            _completed.Clear();
            DiscardCurrent();
            IsDrawing = false;
        }
    }

    /// <summary>
    /// Descarta o traco em andamento sem grava-lo. Usado quando o menu abre:
    /// segurar a ponta parada ja tinha comecado um traco -- que, como o gesto so
    /// vale no comeco do traco, nao passa de um ponto -- e ele nao deve virar um
    /// borrao no lugar onde voce so queria abrir o menu.
    /// </summary>
    public void CancelStroke()
    {
        lock (_gate)
        {
            DiscardCurrent();
            IsDrawing = false;
        }
    }

    private void DiscardCurrent()
    {
        _current.Clear();
        _currentPath?.Dispose();
        _currentPath = null;
        _currentDirty = false;
    }

    /// <summary>Remove o ultimo traco terminado. Devolve false se nao havia nada.</summary>
    public bool Undo()
    {
        lock (_gate)
        {
            if (_completed.Count == 0) return false;
            _completed[^1].path.Dispose();
            _completed.RemoveAt(_completed.Count - 1);
            return true;
        }
    }

    /// <summary>
    /// Apaga tracos sob o ponto. Testa alguns pontos num circulo de raio
    /// <paramref name="radius"/> em vez de so o centro, senao seria preciso
    /// acertar o traco com precisao de pixel.
    ///
    /// SKPath.Contains percorre os verbos do path; chamar isso por amostra da
    /// caneta e caro. O salto por distancia abaixo reduz a frequencia sem abrir
    /// buracos no rastro -- o limiar e metade do raio, entao circulos
    /// consecutivos sempre se sobrepoem.
    /// </summary>
    public int EraseAt(Vec2 point, float radius)
    {
        lock (_gate)
        {
            if (_hasErasePos && Vec2.Dist(point, _lastErasePos) < radius * 0.5f)
                return 0;
            _lastErasePos = point;
            _hasErasePos = true;

            int removed = 0;
            for (int i = _completed.Count - 1; i >= 0; i--)
            {
                var path = _completed[i].path;
                var bounds = path.Bounds;
                bounds.Inflate(radius, radius);
                if (!bounds.Contains(point.X, point.Y)) continue;

                bool hit = path.Contains(point.X, point.Y);
                for (int k = 0; !hit && k < 8; k++)
                {
                    float a = k * MathF.PI / 4f;
                    hit = path.Contains(point.X + MathF.Cos(a) * radius,
                                        point.Y + MathF.Sin(a) * radius);
                }
                if (!hit) continue;

                path.Dispose();
                _completed.RemoveAt(i);
                removed++;
            }
            return removed;
        }
    }

    /// <summary>Chamar ao soltar a ponta: o proximo apagamento recomeca limpo.</summary>
    public void ResetErase() { lock (_gate) _hasErasePos = false; }

    private Vec2 _lastErasePos;
    private bool _hasErasePos;

    /// <summary>
    /// O contorno e um poligono fechado, nao uma polilinha: fecha e preenche.
    /// </summary>
    private static SKPath? ToPath(List<Vec2> outline)
    {
        if (outline.Count < 3) return null;

        var path = new SKPath { FillType = SKPathFillType.Winding };
        path.MoveTo(outline[0].X, outline[0].Y);
        for (int i = 1; i < outline.Count; i++)
            path.LineTo(outline[i].X, outline[i].Y);
        path.Close();
        return path;
    }

    /// <summary>Nao limpa o canvas: quem limpa e a OverlayWindow, porque o alfa
    /// de fundo faz parte do mecanismo de captura de input.</summary>
    public void Render(SKCanvas canvas)
    {
        lock (_gate)
        {
            if (_currentDirty)
            {
                _currentDirty = false;
                var outline = FreehandStroke.GetStroke(_current, Options);
                _currentPath?.Dispose();
                _currentPath = ToPath(outline);
            }

            foreach (var (path, color) in _completed)
            {
                _fillPaint.Color = color;
                canvas.DrawPath(path, _fillPaint);
            }
            _fillPaint.Color = Color;
            if (_currentPath is not null) canvas.DrawPath(_currentPath, _fillPaint);
        }
    }

    /// <summary>Cursor proprio: a caneta nao move o cursor do Windows e ele some
    /// no contato, entao sem isso voce desenha as cegas. Ver FINDINGS.md.</summary>
    public void RenderCursor(SKCanvas canvas, Vec2 position, float pressure, bool tipDown, bool eraser)
    {
        _cursorPaint.Color = eraser ? new SKColor(0xFF, 0xFF, 0xFF, 0xE0)
                           : tipDown ? Color
                           : Color.WithAlpha(0xA0);
        _cursorPaint.StrokeWidth = eraser ? 2f : 1.5f;

        if (eraser)
        {
            canvas.DrawRect(position.X - EraserRadius, position.Y - EraserRadius,
                            EraserRadius * 2, EraserRadius * 2, _cursorPaint);
            return;
        }

        // Mesma funcao que o traco usa, nao uma reimplementacao: duplicar a
        // formula fazia o anel ignorar o easing e mostrar sempre menos do que
        // sairia de verdade.
        var options = Options;
        float r = MathF.Max(2f,
            FreehandStroke.GetStrokeRadius(options.Size, options.Thinning, pressure, options.Easing));
        canvas.DrawCircle(position.X, position.Y, r, _cursorPaint);
        canvas.DrawCircle(position.X, position.Y, 1.2f, _cursorPaint);
    }

    /// <summary>Cursor neutro, usado com o menu aberto: nao representa espessura.</summary>
    public void RenderPointer(SKCanvas canvas, Vec2 position)
    {
        _cursorPaint.Color = SKColors.White;
        _cursorPaint.StrokeWidth = 1.5f;
        canvas.DrawCircle(position.X, position.Y, 5f, _cursorPaint);
        canvas.DrawCircle(position.X, position.Y, 1.2f, _cursorPaint);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (p, _) in _completed) p.Dispose();
            _completed.Clear();
            _currentPath?.Dispose();
            _currentPath = null;
        }
        _fillPaint.Dispose();
        _cursorPaint.Dispose();
    }
}
