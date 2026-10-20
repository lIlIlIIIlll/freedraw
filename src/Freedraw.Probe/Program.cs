using Freedraw.Input;
using Freedraw.Probe;

// Sonda de input. Dois modos:
//   (sem args)    mostra pressao/tilt/estado ao vivo
//   --calibrate   mapeia a area que responde, para achar faixa morta

bool calibrate = args.Contains("--calibrate");
bool jumps = args.Contains("--jumps");
bool buttons = args.Contains("--buttons");

Console.WriteLine(new string('=', 70));
Console.WriteLine(buttons ? "FREEDRAW -- botoes da caneta"
    : jumps ? "FREEDRAW -- caca ao teleporte de coordenada"
    : calibrate ? "FREEDRAW -- calibracao da area ativa"
    : "FREEDRAW -- sonda de input (etapa 1)");
Console.WriteLine(new string('=', 70));

if (buttons)
{
    Console.WriteLine("  Com a caneta EM ALCANCE (pairando ou encostada):");
    Console.WriteLine("    1) aperte e solte o BOTAO LATERAL varias vezes");
    Console.WriteLine("    2) encoste e levante a ponta");
    Console.WriteLine("    3) vire a caneta e encoste a borracha");
    Console.WriteLine("  Cada mudanca de estado aparece abaixo. Ctrl+C para sair.\n");
}
else if (jumps)
{
    Console.WriteLine("  Faca RETAS LENTAS atravessando a mesa, principalmente passando");
    Console.WriteLine("  pela regiao onde o ponteiro teleporta. Repita varias vezes.");
    Console.WriteLine("  Pode ser com a caneta encostada ou pairando.");
    Console.WriteLine("  Ctrl+C quando terminar.\n");
}
else if (calibrate)
{
    Console.WriteLine("  VARRA A MESA INTEIRA com a caneta, encostada, como se pintasse.");
    Console.WriteLine("  Cubra tudo: bordas, cantos, meio. Leve ~1 minuto.");
    Console.WriteLine("  Ctrl+C quando terminar.\n");
}

using var source = new RawInputPenSource();

if (buttons)
{
    // Os dois botoes desta caneta nao passam pelo Barrel Switch: um virou tecla
    // e o outro sai pela colecao de mouse da mesa.
    source.TabletButtonDown += b =>
        Console.WriteLine($"  >>> BOTAO DE MOUSE DA MESA: {b}");
    source.TabletKeyDown += vk =>
        Console.WriteLine($"  >>> TECLA DA MESA: vk=0x{vk:X2} ({(char)vk})");
}

// Grade menor que a primeira versao: 48x29 com uma varredura de ~30s dava 4.7
// marcacoes por celula, e nessa densidade celula vazia nao significa nada.
var grid = args.FirstOrDefault(a => a.StartsWith("--grid=", StringComparison.Ordinal));
int cols = 32, gridRows = 20;
if (grid is not null)
{
    var parts = grid[7..].Split('x');
    if (parts.Length == 2 && int.TryParse(parts[0], out var gc) && int.TryParse(parts[1], out var gr))
        (cols, gridRows) = (Math.Clamp(gc, 4, 200), Math.Clamp(gr, 4, 200));
}
var coverage = new CoverageMap(cols, gridRows);

int reports = 0, strokes = 0;
float maxPressure = 0;
bool wasDown = false;
var lastPrint = Environment.TickCount64;
float prevU = 0, prevV = 0;
bool hasPrev = false;

(bool inRange, bool tip, bool barrel, bool eraser) lastButtons = default;
int sawBarrel = 0, sawEraser = 0, sawTip = 0;
static string Flag(bool value) => value ? "SIM" : " . ";

// --jumps: caneta nao teleporta, entao passo grande entre amostras consecutivas
// a ~200Hz e sempre defeito. Aqui so MEDIMOS -- o filtro fica no app.
const float JumpThreshold = 0.03f;
var jumpOrigins = new CoverageMap(cols, gridRows);
var jumpVisits = new CoverageMap(cols, gridRows);
var biggest = new List<(float dist, float fromU, float fromV, float toU, float toV)>();
int jumpCount = 0;
float maxJump = 0;
// TODOS os passos, nao so os saltos: o limiar util fica entre o maior movimento
// legitimo e o menor glitch, e sem medir o movimento normal nao da para achar.
var allSteps = new List<float>();
int saturatedJumps = 0;

source.SourceChanged += src =>
{
    Console.ForegroundColor = src == InputSource.Pen ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
    Console.WriteLine($"\n  >>> origem do input: {src}\n");
    Console.ResetColor();
};

source.PenSample += sample =>
{
    reports++;
    maxPressure = Math.Max(maxPressure, sample.Pressure);

    if (buttons)
    {
        // So transicoes: a mesa manda centenas de reports por segundo e imprimir
        // todos afogaria justamente o evento que estamos procurando.
        var state = (sample.InRange, sample.TipDown, sample.BarrelDown, sample.Eraser);
        if (state != lastButtons)
        {
            lastButtons = state;
            if (sample.BarrelDown) sawBarrel++;
            if (sample.Eraser) sawEraser++;
            if (sample.TipDown) sawTip++;
            Console.WriteLine($"  alcance={Flag(sample.InRange)}  ponta={Flag(sample.TipDown)}  "
                              + $"BOTAO={Flag(sample.BarrelDown)}  borracha={Flag(sample.Eraser)}"
                              + $"   p={sample.Pressure:F2}");
        }
        return;
    }

    if (jumps)
    {
        if (!sample.InRange) { hasPrev = false; return; }
        jumpVisits.Add(sample.X, sample.Y);

        if (hasPrev)
        {
            float dx = sample.X - prevU, dy = sample.Y - prevV;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            allSteps.Add(d);

            if (d > JumpThreshold)
            {
                jumpCount++;
                maxJump = MathF.Max(maxJump, d);
                jumpOrigins.Add(prevU, prevV);   // ONDE o salto nasceu

                // Valor exatamente no extremo do range logico = clamp do sensor,
                // nao posicao real. Assinatura de perda de rastreio.
                bool sat = prevU <= 0f || prevU >= 1f || sample.X <= 0f || sample.X >= 1f
                           || prevV <= 0f || prevV >= 1f || sample.Y <= 0f || sample.Y >= 1f;
                if (sat) saturatedJumps++;

                biggest.Add((d, prevU, prevV, sample.X, sample.Y));
                Console.WriteLine($"  salto {d:F3}  ({prevU:F3},{prevV:F3}) -> "
                                  + $"({sample.X:F3},{sample.Y:F3})   dY={dy:+0.000;-0.000}"
                                  + (sat ? "  [SATURADO]" : ""));
            }
        }
        prevU = sample.X; prevV = sample.Y; hasPrev = true;
        return;
    }

    if (calibrate)
    {
        // Fora de alcance o report vem x=0 y=0. Alem de nao contar, e preciso
        // QUEBRAR a continuidade: se a caneta saiu e voltou, interpolar entre as
        // duas pontas preencheria justamente o buraco que procuramos.
        if (!sample.InRange)
        {
            hasPrev = false;
        }
        else
        {
            if (hasPrev) coverage.AddSegment(prevU, prevV, sample.X, sample.Y);
            else coverage.Add(sample.X, sample.Y);
            prevU = sample.X;
            prevV = sample.Y;
            hasPrev = true;
        }

        var tick = Environment.TickCount64;
        if (tick - lastPrint >= 500)
        {
            lastPrint = tick;
            Console.Write($"\r  amostras: {reports,7}   cobertura: "
                          + $"{coverage.CoveredCells() * 100 / (coverage.Columns * coverage.Rows),3}%"
                          + $"   densidade: {coverage.Density,5:F1}  ");
        }
        return;
    }

    // Transicao da ponta e o que abre e fecha um traco -- o overlay vai usar
    // exatamente esse sinal na etapa 2.
    if (sample.TipDown && !wasDown)
    {
        strokes++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  [traco {strokes} INICIO] x={sample.X:F4} y={sample.Y:F4}");
        Console.ResetColor();
    }
    else if (!sample.TipDown && wasDown)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [traco {strokes} FIM]");
        Console.ResetColor();
    }
    wasDown = sample.TipDown;

    // Throttle: a mesa emite centenas de reports por segundo.
    var now = Environment.TickCount64;
    if (now - lastPrint >= 120)
    {
        lastPrint = now;
        var bar = new string('#', (int)(sample.Pressure * 30));
        Console.WriteLine($"  {sample}  |{bar,-30}|");
    }
};

try
{
    source.Start();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  ERRO: {ex.Message}");
    if (ex.InnerException is not null) Console.WriteLine($"         {ex.InnerException.Message}");
    Console.ResetColor();
    return 1;
}

Console.WriteLine("  Raw Input ativo. Encoste a caneta e desenhe.");
Console.WriteLine("  Ctrl+C para sair.\n");

using var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };

// --seconds=N encerra sozinho, para smoke test nao-interativo.
var timeout = args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.Ordinal));
if (timeout is not null && double.TryParse(timeout.AsSpan(10), out var seconds))
    quit.Wait(TimeSpan.FromSeconds(seconds));
else
    quit.Wait();

Console.WriteLine("\n" + new string('=', 70));

if (buttons)
{
    Console.WriteLine("BOTOES DA CANETA");
    Console.WriteLine($"  ponta acionada    : {sawTip}x");
    Console.WriteLine($"  BOTAO LATERAL     : {sawBarrel}x");
    Console.WriteLine($"  borracha (virar)  : {sawEraser}x");

    if (sawTip == 0)
    {
        Console.WriteLine("\n  Nada foi acionado -- a caneta nao chegou a encostar.");
    }
    else if (sawBarrel == 0)
    {
        Console.WriteLine("\n  CONFIRMADO: o botao lateral NAO aparece no report HID da caneta.");
        Console.WriteLine("  A ponta aparece, entao a leitura de botoes funciona -- o bit do");
        Console.WriteLine("  botao simplesmente nao vem.");
        Console.WriteLine("\n  Causa provavel: o mapeamento gravado na mesa faz o botao emitir");
        Console.WriteLine("  um evento de TECLADO (Ctrl+Z) em vez de ligar o Barrel Switch.");
        Console.WriteLine("  Solucao: no painel do driver, 'Reset' em Custom Function keys para");
        Console.WriteLine("  devolver o botao ao comportamento nativo, e rode este teste de novo.");
    }
    else
    {
        Console.WriteLine("\n  O botao CHEGA ao app. Se mesmo assim nao cicla o modo no Freedraw,");
        Console.WriteLine("  o problema esta na deteccao de borda, nao no input.");
    }
}
else if (jumps)
{
    Console.WriteLine("TELEPORTES DE COORDENADA");
    Console.WriteLine($"  {reports} amostras, {jumpCount} saltos acima de {JumpThreshold:F3}");

    if (jumpCount == 0)
    {
        Console.WriteLine("\n  Nenhum salto detectado. Ou o defeito nao ocorreu nesta sessao,");
        Console.WriteLine("  ou o limiar esta alto demais para captura-lo.");
    }
    else
    {
        Console.WriteLine($"  maior salto: {maxJump:F3} (da largura da mesa)\n");

        // Taxa, nao contagem: contagem bruta depende de onde a pessoa desenhou e
        // por isso nao compara entre rodadas.
        Console.WriteLine("  TAXA DE SALTO POR REGIAO:");
        jumpOrigins.PrintRate(Console.Out, jumpVisits);

        var (medianRate, worstRate, ratedCells) = jumpOrigins.RateStats(jumpVisits);
        Console.WriteLine($"\n  Taxa mediana {medianRate:P1}, pior celula {worstRate:P1} "
                          + $"({ratedCells} celulas com amostra suficiente)");

        Console.WriteLine("\n  OS 10 MAIORES:");
        foreach (var j in biggest.OrderByDescending(j => j.dist).Take(10))
            Console.WriteLine($"    {j.dist:F3}  ({j.fromU:F3},{j.fromV:F3}) -> ({j.toU:F3},{j.toV:F3})");

        Console.WriteLine($"\n  Saltos com valor no extremo do range: {saturatedJumps}/{jumpCount}"
                          + $" ({saturatedJumps * 100 / jumpCount}%)");
        if (saturatedJumps * 2 >= jumpCount)
        {
            Console.WriteLine("  => PERDA DE RASTREIO. Coordenada grampeada em 0 ou 32767 nao e");
            Console.WriteLine("     posicao real: o sensor perdeu a caneta e saturou o valor.");
        }

        // Mediana e desvio absoluto mediano: media e desvio-padrao sao destruidos
        // por dois outliers de largura total, e foi assim que a primeira versao
        // desta analise concluiu "espalhado" olhando para dados concentrados.
        var origins = biggest.Select(j => j.fromU).OrderBy(x => x).ToArray();
        float median = origins[origins.Length / 2];
        var deviations = origins.Select(x => MathF.Abs(x - median)).OrderBy(x => x).ToArray();
        float mad = deviations[deviations.Length / 2];

        Console.WriteLine($"\n  Origem em X: mediana {median:F3}, MAD {mad:F3}");
        Console.WriteLine(mad < 0.15f
            ? $"  => CONCENTRADO em torno de x={median:F2}: defeito localizado do digitalizador."
            : "  => ESPALHADO: suspeite de cabo, alimentacao ou interferencia.");

        // O limiar precisa ficar ACIMA do movimento normal e ABAIXO do menor
        // glitch. Percentil do movimento real, nao fracao do maior outlier.
        allSteps.Sort();
        float P(float q) => allSteps[Math.Clamp((int)(q * allSteps.Count), 0, allSteps.Count - 1)];
        Console.WriteLine($"\n  Passos normais: p50={P(0.50f):F4}  p95={P(0.95f):F4}  "
                          + $"p99={P(0.99f):F4}  p99.9={P(0.999f):F4}");

        float suggested = MathF.Max(0.03f, MathF.Min(P(0.999f) * 2f, 0.10f));
        Console.WriteLine($"  Limiar sugerido: maxJump = {suggested:F3}f");
        Console.WriteLine("  (acima do movimento real, abaixo dos saltos observados)");

        // Linha unica para comparar rodadas ao mudar cabo/porta/posicao da mesa.
        // A taxa so e comparavel se o movimento for parecido -- use o mesmo
        // protocolo de varredura nas duas medicoes.
        Console.WriteLine("\n  ASSINATURA (compare entre rodadas):");
        Console.WriteLine($"    taxa_mediana {medianRate:P1} | pior_celula {worstRate:P1}"
                          + $" | max {maxJump:F3} | saturados {saturatedJumps * 100 / jumpCount}%");
        Console.WriteLine("    (taxa por passagem -- nao depende de onde voce desenhou)");
    }
}
else if (calibrate)
{
    Console.WriteLine("MAPA DE COBERTURA DA MESA");
    Console.WriteLine($"  {coverage.TotalSamples} amostras, "
                      + $"{coverage.CoveredCells()}/{coverage.Columns * coverage.Rows} celulas\n");
    coverage.Print(Console.Out);

    var box = coverage.BoundingBox();
    int total = coverage.Columns * coverage.Rows;
    int covered = coverage.CoveredCells();
    float pct = covered * 100f / total;

    Console.WriteLine($"\n  cobertura: {covered}/{total} celulas ({pct:F0}%)");
    Console.WriteLine($"  densidade: {coverage.Density:F1} marcacoes por celula coberta");

    if (box is null)
    {
        Console.WriteLine("\n  Nenhuma amostra -- a caneta nao foi movida.");
    }
    else if (coverage.Density < 15f || pct < 90f)
    {
        // Sem densidade nao da para separar "nao respondeu" de "nao passei".
        // Sugerir um retangulo aqui produz um numero que parece valido e nao e.
        Console.WriteLine("\n  INCONCLUSIVO -- varredura insuficiente.");
        Console.WriteLine("  Celula vazia nesta densidade pode ser so falta de amostra,");
        Console.WriteLine("  entao NAO vou sugerir area: o numero pareceria valido e nao seria.");
        Console.WriteLine("\n  Varra de novo, mais devagar e cobrindo tudo, ate ver");
        Console.WriteLine("  'cobertura' passar de 90% e 'densidade' de 15.");
        Console.WriteLine("  Se um trecho NUNCA cobrir por mais que voce insista, e area morta.");
    }
    else
    {
        var full = coverage.LargestFullRect();
        Console.WriteLine($"\n  Envelope do que respondeu: "
                          + $"({box.Value.left:F3}, {box.Value.top:F3}) -> "
                          + $"({box.Value.right:F3}, {box.Value.bottom:F3})");

        if (pct >= 99.5f)
        {
            Console.WriteLine("\n  SEM AREA MORTA -- a mesa respondeu em toda a superficie.");
            Console.WriteLine("  Nao ha o que recortar; deixe a area padrao 0..1.");
        }
        else if (full is not null)
        {
            var f = full.Value;
            Console.WriteLine("\n  MAIOR RETANGULO 100% COBERTO -- cole em Program.cs do Freedraw.App:");
            Console.WriteLine($"    AreaLeft   = {f.left:F4}f,");
            Console.WriteLine($"    AreaTop    = {f.top:F4}f,");
            Console.WriteLine($"    AreaRight  = {f.right:F4}f,");
            Console.WriteLine($"    AreaBottom = {f.bottom:F4}f,");
        }
    }
}
else
{
    Console.WriteLine("RESUMO");
    Console.WriteLine($"  reports recebidos : {reports}");
    Console.WriteLine($"  tracos            : {strokes}");
    Console.WriteLine($"  pressao maxima    : {maxPressure:F3}");
}

Console.WriteLine("\n  dispositivos:");
foreach (var device in source.KnownDevices)
    Console.WriteLine($"    {device}");

return 0;
