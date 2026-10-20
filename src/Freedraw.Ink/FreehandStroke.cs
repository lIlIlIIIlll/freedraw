namespace Freedraw.Ink;

/// <summary>
/// Port de perfect-freehand (Steve Ruiz) — a biblioteca que o Excalidraw usa no
/// desenho livre. Nomes e ordem seguem o original para o port ser conferivel.
///
/// Duas etapas:
///   1. <see cref="GetStrokePoints"/> reamostra e ESTABILIZA (streamline).
///   2. <see cref="GetStrokeOutlinePoints"/> converte pressao em raio e gera o
///      contorno deslocando perpendicularmente dos dois lados.
///
/// O resultado NAO e uma linha com espessura: e um POLIGONO FECHADO para
/// preencher. Isso simplifica a renderizacao a um unico FillPath.
/// </summary>
public static class FreehandStroke
{
    private const float RateOfPressureChange = 0.275f;

    /// <summary>PI com folga: evita que o angulo exato feche o arco cedo demais.</summary>
    private static readonly float FixedPi = MathF.PI + 0.0001f;

    /// <summary>Conveniencia: do input cru direto ao poligono.</summary>
    public static List<Vec2> GetStroke(IReadOnlyList<InkPoint> points, StrokeOptions options)
        => GetStrokeOutlinePoints(GetStrokePoints(points, options), options);

    // ------------------------------------------------------------------ etapa 1
    public static List<StrokePoint> GetStrokePoints(IReadOnlyList<InkPoint> points, StrokeOptions options)
    {
        var result = new List<StrokePoint>();
        if (points.Count == 0) return result;

        // t = 1 significa "sem suavizacao". O piso de 0.15 impede que streamline=1
        // congele o traco por completo.
        float t = 0.15f + (1f - Math.Clamp(options.Streamline, 0f, 1f)) * 0.85f;

        // Caminho dedicado para um ponto so, em vez de copiar a lista inteira em
        // toda chamada apenas para poder acrescentar o ponto sintetico. Com o
        // traco sendo recalculado a cada quadro, essa copia era puro trafego de
        // memoria proporcional ao tamanho do traco.
        if (points.Count == 1)
        {
            var only = points[0];
            var synthetic = new InkPoint(only.X + 1f, only.Y + 1f, only.Pressure);
            return GetStrokePoints([only, synthetic], options);
        }

        var pts = points;

        var prev = new StrokePoint(
            Point: pts[0].Position,
            Pressure: pts[0].Pressure >= 0 ? pts[0].Pressure : 0.25f,
            Vector: new Vec2(1, 1),
            Distance: 0,
            RunningLength: 0);
        result.Add(prev);

        bool hasReachedMinimumLength = false;
        float runningLength = 0;
        int max = pts.Count - 1;

        for (int i = 1; i < pts.Count; i++)
        {
            // No ultimo ponto de um traco terminado, usa a posicao exata: sem isso
            // o traco fica sempre um pouco atras de onde a caneta parou.
            Vec2 point = options.IsComplete && i == max
                ? pts[i].Position
                : Vec2.Lerp(prev.Point, pts[i].Position, t);

            if (prev.Point.NearlyEquals(point)) continue;

            float distance = Vec2.Dist(point, prev.Point);
            runningLength += distance;

            // Segura os primeiros pontos ate juntar comprimento: comeco de traco
            // tem vetores instaveis e produziria um contorno com ruido.
            if (i < max && !hasReachedMinimumLength)
            {
                if (runningLength < options.Size) continue;
                hasReachedMinimumLength = true;
            }

            prev = new StrokePoint(
                Point: point,
                Pressure: pts[i].Pressure >= 0 ? pts[i].Pressure : 0.5f,
                Vector: (prev.Point - point).Unit(),
                Distance: distance,
                RunningLength: runningLength);
            result.Add(prev);
        }

        if (result.Count > 1)
            result[0] = result[0] with { Vector = result[1].Vector };

        return result;
    }

    /// <summary>Pressao -> raio. E aqui que Thinning e Easing atuam.</summary>
    public static float GetStrokeRadius(float size, float thinning, float pressure, Func<float, float> easing)
    {
        if (thinning == 0f) return size / 2f;
        pressure = Math.Clamp(pressure, 0f, 1f);
        return size * easing(0.5f - thinning * (0.5f - pressure));
    }

    // ------------------------------------------------------------------ etapa 2
    public static List<Vec2> GetStrokeOutlinePoints(IReadOnlyList<StrokePoint> points, StrokeOptions options)
    {
        var outline = new List<Vec2>();
        if (points.Count == 0 || options.Size <= 0) return outline;

        float size = options.Size;
        float thinning = options.Thinning;
        float totalLength = points[^1].RunningLength;

        float taperStart = options.TaperStart;
        float taperEnd = options.TaperEnd;
        float minDistance = size * options.Smoothing;
        minDistance *= minDistance;

        var leftPts = new List<Vec2>();
        var rightPts = new List<Vec2>();

        // Media das primeiras amostras: evita que o traco nasca com a espessura
        // errada por causa de uma unica leitura ruim de pressao.
        float prevPressure = points[0].Pressure;
        int seed = Math.Min(10, points.Count);
        for (int i = 0; i < seed; i++)
        {
            float pressure = points[i].Pressure;
            if (options.SimulatePressure)
            {
                float sp = MathF.Min(1f, points[i].Distance / size);
                float rp = MathF.Min(1f, 1f - sp);
                pressure = MathF.Min(1f, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
            }
            prevPressure = (prevPressure + pressure) / 2f;
        }

        float radius = GetStrokeRadius(size, thinning, points[^1].Pressure, options.Easing);
        float? firstRadius = null;
        Vec2 prevVector = points[0].Vector;
        Vec2 pl = points[0].Point, pr = pl;
        Vec2 tl = pl, tr = pr;
        bool isPrevPointSharpCorner = false;

        for (int i = 0; i < points.Count; i++)
        {
            var sp0 = points[i];
            float pressure = sp0.Pressure;
            Vec2 point = sp0.Point;
            Vec2 vector = sp0.Vector;
            float distance = sp0.Distance;
            float runningLength = sp0.RunningLength;

            // Descarta pontos colados no fim: eles so adicionariam ruido a ponta.
            if (i < points.Count - 1 && totalLength - runningLength < 3f) continue;

            if (thinning != 0f)
            {
                if (options.SimulatePressure)
                {
                    float sp = MathF.Min(1f, distance / size);
                    float rp = MathF.Min(1f, 1f - sp);
                    pressure = MathF.Min(1f, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
                }
                radius = GetStrokeRadius(size, thinning, pressure, options.Easing);
            }
            else
            {
                radius = size / 2f;
            }

            firstRadius ??= radius;

            // Afilamento das pontas
            float ts = runningLength < taperStart && taperStart > 0
                ? options.TaperStartEasing(runningLength / taperStart)
                : 1f;
            float te = totalLength - runningLength < taperEnd && taperEnd > 0
                ? options.TaperEndEasing((totalLength - runningLength) / taperEnd)
                : 1f;
            radius = MathF.Max(0.01f, radius * MathF.Min(ts, te));

            Vec2 nextVector = (i < points.Count - 1 ? points[i + 1] : points[i]).Vector;
            float nextDpr = i < points.Count - 1 ? Vec2.Dot(vector, nextVector) : 1f;
            float prevDpr = Vec2.Dot(vector, prevVector);

            bool isPointSharpCorner = prevDpr < 0f && !isPrevPointSharpCorner;
            bool isNextPointSharpCorner = nextDpr < 0f;

            // Canto agudo: em vez de deslocar (o que cruzaria as bordas e faria
            // um no), varre um semicirculo ao redor do ponto.
            if (isPointSharpCorner || isNextPointSharpCorner)
            {
                Vec2 offsetCorner = prevVector.Per() * radius;
                for (float step = 1f / 13f, tt = 0f; tt <= 1f; tt += step)
                {
                    tl = Vec2.RotateAround(point - offsetCorner, point, FixedPi * tt);
                    leftPts.Add(tl);
                    tr = Vec2.RotateAround(point + offsetCorner, point, FixedPi * -tt);
                    rightPts.Add(tr);
                }
                pl = tl;
                pr = tr;
                if (isNextPointSharpCorner) isPrevPointSharpCorner = true;
                continue;
            }

            isPrevPointSharpCorner = false;

            if (i == points.Count - 1)
            {
                Vec2 offsetLast = vector.Per() * radius;
                leftPts.Add(point - offsetLast);
                rightPts.Add(point + offsetLast);
                continue;
            }

            Vec2 offset = Vec2.Lerp(nextVector, vector, nextDpr).Per() * radius;

            tl = point - offset;
            if (i <= 1 || Vec2.Dist2(pl, tl) > minDistance) { leftPts.Add(tl); pl = tl; }

            tr = point + offset;
            if (i <= 1 || Vec2.Dist2(pr, tr) > minDistance) { rightPts.Add(tr); pr = tr; }

            prevPressure = pressure;
            prevVector = vector;
        }

        return BuildOutline(points, options, leftPts, rightPts, radius, firstRadius ?? radius);
    }

    private static List<Vec2> BuildOutline(
        IReadOnlyList<StrokePoint> points, StrokeOptions options,
        List<Vec2> leftPts, List<Vec2> rightPts, float radius, float firstRadius)
    {
        Vec2 firstPoint = points[0].Point;
        Vec2 lastPoint = points.Count > 1 ? points[^1].Point : firstPoint + new Vec2(1, 1);

        var startCap = new List<Vec2>();
        var endCap = new List<Vec2>();

        // Traco de um ponto so: vira um ponto redondo.
        if (points.Count == 1)
        {
            if (options.TaperStart == 0f && options.TaperEnd == 0f || options.IsComplete)
            {
                Vec2 start = Vec2.Project(firstPoint, (firstPoint - lastPoint).Per().Unit(), -firstRadius);
                var dot = new List<Vec2>();
                for (float step = 1f / 13f, t = step; t <= 1f; t += step)
                    dot.Add(Vec2.RotateAround(start, firstPoint, FixedPi * 2f * t));
                return dot;
            }
        }
        else
        {
            if (options.TaperStart > 0f)
            {
                // afilado: sem tampa
            }
            else if (options.CapStart && rightPts.Count > 0)
            {
                for (float step = 1f / 13f, t = step; t <= 1f; t += step)
                    startCap.Add(Vec2.RotateAround(rightPts[0], firstPoint, FixedPi * t));
            }
            else if (leftPts.Count > 0 && rightPts.Count > 0)
            {
                Vec2 corners = leftPts[0] - rightPts[0];
                Vec2 a = corners * 0.5f, b = corners * 0.51f;
                startCap.Add(firstPoint - a);
                startCap.Add(firstPoint - b);
                startCap.Add(firstPoint + b);
                startCap.Add(firstPoint + a);
            }

            Vec2 direction = (-points[^1].Vector).Per();
            if (options.TaperEnd > 0f)
            {
                endCap.Add(lastPoint);
            }
            else if (options.CapEnd)
            {
                Vec2 start = Vec2.Project(lastPoint, direction, radius);
                for (float step = 1f / 29f, t = step; t < 1f; t += step)
                    endCap.Add(Vec2.RotateAround(start, lastPoint, FixedPi * 3f * t));
            }
            else
            {
                endCap.Add(lastPoint + direction * radius);
                endCap.Add(lastPoint + direction * (radius * 0.99f));
                endCap.Add(lastPoint - direction * (radius * 0.99f));
                endCap.Add(lastPoint - direction * radius);
            }
        }

        // Lado esquerdo, tampa do fim, lado direito invertido, tampa do inicio.
        var outline = new List<Vec2>(leftPts.Count + rightPts.Count + startCap.Count + endCap.Count);
        outline.AddRange(leftPts);
        outline.AddRange(endCap);
        for (int i = rightPts.Count - 1; i >= 0; i--) outline.Add(rightPts[i]);
        outline.AddRange(startCap);
        return outline;
    }
}
