namespace Freedraw.Probe;

/// <summary>
/// Mapa de cobertura da area ativa da mesa.
///
/// Guardar so min/max nao detecta area morta: nao distingue "nao respondeu aqui"
/// de "nao passei aqui". Marcando celulas, o que sobra vazio depois de uma
/// varredura completa E a area morta -- e da para ver o formato dela.
/// </summary>
public sealed class CoverageMap(int columns = 32, int rows = 20)
{
    private readonly int[,] _hits = new int[columns, rows];
    private int _total;

    public int Columns => columns;
    public int Rows => rows;
    public int TotalSamples => _total;

    /// <summary>Media de marcacoes por celula coberta. Abaixo de ~15 o mapa nao
    /// e confiavel: celula vazia pode ser so falta de amostra.</summary>
    public float Density
    {
        get { int covered = CoveredCells(); return covered == 0 ? 0 : _total / (float)covered; }
    }

    public void Add(float u, float v)
    {
        int c = Math.Clamp((int)(u * columns), 0, columns - 1);
        int r = Math.Clamp((int)(v * rows), 0, rows - 1);
        _hits[c, r]++;
        _total++;
    }

    /// <summary>
    /// Marca todas as celulas ATRAVESSADAS entre duas amostras consecutivas.
    ///
    /// Sem isso o mapa fica cheio de buracos falsos: a caneta e continua mas os
    /// reports sao discretos, e um movimento rapido pula varias celulas entre
    /// duas leituras.
    ///
    /// So deve ser chamado quando a caneta permaneceu EM ALCANCE entre as duas
    /// amostras. Se ela saiu e voltou, interpolar preencheria justamente o
    /// buraco que estamos tentando encontrar.
    /// </summary>
    public void AddSegment(float u0, float v0, float u1, float v1)
    {
        float du = (u1 - u0) * columns;
        float dv = (v1 - v0) * rows;
        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(du), MathF.Abs(dv)));

        // Salto maior que o proprio mapa nao e movimento continuo plausivel.
        if (steps <= 0 || steps > columns + rows) { Add(u1, v1); return; }

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            Add(u0 + (u1 - u0) * t, v0 + (v1 - v0) * t);
        }
    }

    public int CoveredCells()
    {
        int n = 0;
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < rows; r++)
                if (_hits[c, r] > 0) n++;
        return n;
    }

    /// <summary>Desenha o mapa. Densidade vira intensidade do caractere.</summary>
    public void Print(TextWriter output)
    {
        int max = 0;
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < rows; r++)
                max = Math.Max(max, _hits[c, r]);
        if (max == 0) { output.WriteLine("  (nenhuma amostra)"); return; }

        output.WriteLine("  +" + new string('-', columns) + "+");
        for (int r = 0; r < rows; r++)
        {
            output.Write("  |");
            for (int c = 0; c < columns; c++)
            {
                int h = _hits[c, r];
                output.Write(h == 0 ? '.' : h * 8 < max ? '-' : h * 3 < max ? '+' : '#');
            }
            output.WriteLine("|");
        }
        output.WriteLine("  +" + new string('-', columns) + "+");
        output.WriteLine("  '.' = SEM RESPOSTA   '-' '+' '#' = densidade crescente");
    }

    public int HitsAt(int c, int r) => _hits[c, r];

    /// <summary>
    /// Mapa de TAXA: este mapa (saltos) dividido por outro (visitas).
    ///
    /// Contagem bruta de saltos nao compara entre rodadas, porque depende de onde
    /// a pessoa desenhou. Taxa por celula nao: se a caneta passou 500 vezes por
    /// uma celula e saltou em 50, sao 10% independente de quanto ela passou nas
    /// outras. E o que permite testar cabo/posicao/monitor com honestidade.
    /// </summary>
    public void PrintRate(TextWriter output, CoverageMap visits, int minVisits = 20)
    {
        output.WriteLine("  +" + new string('-', columns) + "+");
        for (int r = 0; r < rows; r++)
        {
            output.Write("  |");
            for (int c = 0; c < columns; c++)
            {
                int v = visits.HitsAt(c, r);
                if (v < minVisits) { output.Write(' '); continue; }   // amostra insuficiente
                float rate = _hits[c, r] / (float)v;
                output.Write(rate <= 0f ? '.' : rate < 0.02f ? '-' : rate < 0.08f ? '+' : '#');
            }
            output.WriteLine("|");
        }
        output.WriteLine("  +" + new string('-', columns) + "+");
        output.WriteLine($"  ' ' = menos de {minVisits} passagens (sem dado)   '.' = limpo");
        output.WriteLine("  '-' <2%   '+' 2-8%   '#' >8% de saltos por passagem");
    }

    /// <summary>
    /// Taxa mediana e pior taxa entre celulas com amostragem suficiente.
    /// Independente da distribuicao do movimento -- comparavel entre rodadas.
    /// </summary>
    public (float median, float worst, int cells) RateStats(CoverageMap visits, int minVisits = 20)
    {
        var rates = new List<float>();
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < rows; r++)
            {
                int v = visits.HitsAt(c, r);
                if (v >= minVisits) rates.Add(_hits[c, r] / (float)v);
            }
        if (rates.Count == 0) return (0, 0, 0);
        rates.Sort();
        return (rates[rates.Count / 2], rates[^1], rates.Count);
    }

    /// <summary>Retangulo que envolve tudo que respondeu, em 0..1.</summary>
    public (float left, float top, float right, float bottom)? BoundingBox()
    {
        int minC = columns, maxC = -1, minR = rows, maxR = -1;
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < rows; r++)
                if (_hits[c, r] > 0)
                {
                    minC = Math.Min(minC, c); maxC = Math.Max(maxC, c);
                    minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                }
        if (maxC < 0) return null;
        return (minC / (float)columns, minR / (float)rows,
                (maxC + 1) / (float)columns, (maxR + 1) / (float)rows);
    }

    /// <summary>
    /// Maior retangulo TOTALMENTE coberto. E este, e nao o bounding box, que
    /// serve como area util quando a faixa morta encosta numa borda.
    /// </summary>
    public (float left, float top, float right, float bottom)? LargestFullRect()
    {
        // Histograma por linha + maior retangulo em histograma: O(cols*rows).
        var heights = new int[columns];
        int best = 0;
        (int c0, int c1, int r0, int r1) bestRect = default;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
                heights[c] = _hits[c, r] > 0 ? heights[c] + 1 : 0;

            // Maior retangulo em histograma. A altura precisa viajar na pilha
            // junto com o indice: ao empilhar um 'start' anterior, heights[start]
            // ainda guarda a altura ORIGINAL daquela coluna, nao a do retangulo.
            var stack = new Stack<(int Index, int Height)>();
            for (int c = 0; c <= columns; c++)
            {
                int h = c == columns ? 0 : heights[c];
                int start = c;
                while (stack.Count > 0 && stack.Peek().Height > h)
                {
                    var (idx, height) = stack.Pop();
                    int width = c - idx;
                    if (height * width > best)
                    {
                        best = height * width;
                        bestRect = (idx, c, r - height + 1, r + 1);
                    }
                    start = idx;
                }
                if (c < columns) stack.Push((start, h));
            }
        }

        if (best == 0) return null;
        return (bestRect.c0 / (float)columns, bestRect.r0 / (float)rows,
                bestRect.c1 / (float)columns, bestRect.r1 / (float)rows);
    }
}
