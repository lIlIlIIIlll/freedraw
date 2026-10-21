using System.Collections.Concurrent;
using System.Windows.Forms;
using Freedraw.Ink;
using Freedraw.Input;
using SkiaSharp;
using static Freedraw.App.Win32;

namespace Freedraw.App;

internal enum PenMode { Draw, Erase, PickColor }

internal static class Program
{
    // So para o Ctrl+Alt+S, que cicla entre estes; no menu a espessura e um slider.
    private static readonly float[] Sizes = [6f, 10f, 14f, 22f, 34f];

    private const long HoldMs = 500;          // segurar a ponta parada NO COMECO do traco abre o menu
    private const float HoldSlackPx = 7f;     // distancia maxima da descida da ponta durante o hold
    private const long MenuGraceMs = 1500;    // caneta fora de alcance por mais que isso fecha o menu
    private const long PenIdleMs = 500;       // watchdog de perda da caneta
    private const float TakeoverSlack = 0.01f; // 1% da mesa: movimento que retoma do mouse

    /// <summary>Pedido pelo item "Reiniciar para atualizar": o Main sobe o exe
    /// novo DEPOIS de soltar o mutex, senao a nova instancia se acharia duplicada.</summary>
    private static bool _restartForUpdate;

    [STAThread]
    private static int Main()
    {
        // Duas instancias criam dois overlays de tela cheia e duas cadeias de
        // hook que se engolem; a segunda ainda perde todos os hotkeys. Local\
        // e nao Global\: cada sessao de usuario tem direito ao proprio overlay.
        using var single = new Mutex(true, @"Local\Freedraw.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("O Freedraw ja esta rodando (veja o icone na bandeja).",
                            "Freedraw", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Updater.CleanupPreviousUpdate();

        int code;
        try
        {
            code = Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"O Freedraw encerrou com erro.\n\n{ex}", "Freedraw",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (_restartForUpdate && Environment.ProcessPath is { } exe)
        {
            single.ReleaseMutex();
            single.Dispose();
            System.Diagnostics.Process.Start(exe);
        }
        return code;
    }

    private static int Run()
    {
        OverlayWindow overlay;
        RawInputPenSource source;
        try
        {
            overlay = new OverlayWindow();
            source = new RawInputPenSource();
            source.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nao foi possivel iniciar o Freedraw.\n\n{ex.Message}", "Freedraw",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        bool drawingEnabled = true;
        var mode = PenMode.Draw;

        var colors = ColorSlots.Load();

        var canvas = new StrokeCanvas
        {
            Color = colors.Current,
            Options = new StrokeOptions
            {
                Size = 14f,
                Thinning = 0.6f,
                Smoothing = 0.5f,
                Streamline = 0.5f,
                SimulatePressure = false,
            },
        };

        var menu = new QuickMenu { Slots = colors };

        // Mapear contra UM monitor, nao contra o bounding box do desktop virtual:
        // com dois monitores o aspecto agregado encolhe drasticamente a area
        // alcancavel e inclui regioes onde nao existe pixel.
        var mapper = new TabletMapper
        {
            Rotation = TabletRotation.None,
            AreaLeft = 0f, AreaTop = 0f, AreaRight = 1f, AreaBottom = 1f,
        };
        var primary = OverlayWindow.PrimaryMonitor();
        mapper.SetScreen(primary.x, primary.y, primary.width, primary.height);

        var jumpFilter = new PenJumpFilter(maxJump: 0.10f);
        var pending = new ConcurrentQueue<PenSample>();
        source.PenSample += pending.Enqueue;

        using var tray = new TrayIcon();
        // A cor atual aparece no tooltip da bandeja. Atualizar o tooltip e uma
        // chamada ao shell: arrastar o slider de cor faria isso a cada amostra
        // da caneta. Marca aqui e atualiza uma vez, quando o arrasto acaba.
        bool statusDirty = false, colorsDirty = false;

        Vec2 cursor = Vec2.Zero, holdOrigin = Vec2.Zero, penAtTakeover = Vec2.Zero;
        float cursorPressure = 0;
        bool cursorVisible = false, wasDown = false, havePenAtTakeover = false;

        // Gesto de segurar: armado SO na descida da ponta e desarmado de vez assim
        // que o traco sai da folga. Uma ancora que acompanhava a caneta fazia
        // qualquer pausa no meio do traco -- um circulo pequeno, um detalhe lento
        // -- abrir o menu e jogar fora o traco inteiro.
        bool holdArmed = false;
        long holdStart = 0, lastPenActivity = Environment.TickCount64;

        // Escritos da thread do Raw Input, lidos da thread de UI e dos callbacks
        // de hook. int + Volatile em vez de bool para deixar a intencao explicita.
        int penInRangeFlag = 0;
        int mouseHasControlFlag = 0;

        bool PenInRange() => Volatile.Read(ref penInRangeFlag) != 0;

        void Quit() => Application.ExitThread();

        // Toda mudanca de cor passa por aqui: grava no slot ativo e agenda o
        // salvamento. Salvar no disco por amostra da caneta seria absurdo
        // durante um arrasto de slider -- grava uma vez, quando ele acaba.
        void ColorsChanged()
        {
            canvas.Color = colors.Current;
            statusDirty = true;
            colorsDirty = true;
        }

        // O menu guarda os valores so enquanto esta aberto: na abertura ele copia
        // o estado atual (que o conta-gotas e os atalhos podem ter mudado) e a
        // cada mudanca o app le de volta.
        void SyncMenuFromCanvas()
        {
            menu.Size = canvas.Options.Size;
            menu.Streamline = canvas.Options.Streamline;
        }

        void ApplyFromMenu()
        {
            ColorsChanged();
            canvas.Options = canvas.Options with { Size = menu.Size, Streamline = menu.Streamline };
        }

        void UpdateTooltip() => tray.SetStatus(
            $"Freedraw - {canvas.CompletedCount} tracos, {ModeName(mode)} "
            + $"#{canvas.Color.Red:X2}{canvas.Color.Green:X2}{canvas.Color.Blue:X2}"
            + (drawingEnabled ? "" : " (desativado)"));

        void Undo() { canvas.Undo(); UpdateTooltip(); }

        void ReleaseCapture()
        {
            overlay.ClickThrough = true;
            overlay.HideSystemCursor = false;
            if (wasDown) { canvas.EndStroke(); wasDown = false; }
            canvas.ResetErase();
            jumpFilter.Reset();
            // O menu NAO fecha aqui: o alcance de hover e curto, e levantar a
            // caneta para ir ate um item ja tira ela de alcance. Quem fecha e a
            // carencia no Tick (MenuGraceMs) ou o mouse assumindo. Mas um arrasto
            // de slider acaba: a ponta que voltar e uma descida nova.
            menu.Up();
            Volatile.Write(ref penInRangeFlag, 0);
            cursorVisible = false;
        }

        void SetEnabled(bool value)
        {
            drawingEnabled = value;
            tray.SetEnabled(value);
            if (!value)
            {
                canvas.CancelStroke();
                wasDown = false;
                ReleaseCapture();
                menu.Close();
            }
            UpdateTooltip();
        }

        // Mexeu no mouse de verdade: o mouse assume o controle e amostras de
        // HOVER param de retomar a captura. Sem isso, uma caneta largada dentro
        // do alcance renova o watchdog para sempre e o overlay fica engolindo
        // todo clique da tela, sem rota de recuperacao pelo mouse.
        source.SourceChanged += src =>
        {
            if (src == InputSource.Mouse)
            {
                Volatile.Write(ref mouseHasControlFlag, 1);
                Volatile.Write(ref penInRangeFlag, 0);
            }
        };

        overlay.DesktopChanged += () =>
        {
            var monitor = OverlayWindow.PrimaryMonitor();
            mapper.SetScreen(monitor.x, monitor.y, monitor.width, monitor.height);
            // Os tracos guardados estao em coordenadas do layout antigo.
            canvas.Clear();
            menu.Close();
            UpdateTooltip();
        };

        // Os hooks sao opcionais: sem eles o app desenha, so perde Ctrl+Z e o
        // botao direito. Politica de EDR bloqueando hooks nao pode matar o app.
        GlobalInputHook? hooks = null;
        try
        {
            hooks = new GlobalInputHook(() => PenInRange() && drawingEnabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Ctrl+Z e o botao direito da caneta nao vao funcionar.\n\n{ex.Message}\n\n"
                + "O resto do Freedraw continua normal.",
                "Freedraw", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        tray.ClearRequested += () => { canvas.Clear(); UpdateTooltip(); };
        tray.UndoRequested += Undo;
        tray.ToggleRequested += () => SetEnabled(!drawingEnabled);
        tray.QuitRequested += Quit;
        tray.RestartRequested += () => { _restartForUpdate = true; Quit(); };

        using var updater = new Updater();
        updater.Start();
        bool updateAnnounced = false;

        overlay.HotkeyPressed += id =>
        {
            switch (id)
            {
                case HOTKEY_CLEAR: canvas.Clear(); break;
                case HOTKEY_UNDO: Undo(); break;
                case HOTKEY_TOGGLE: SetEnabled(!drawingEnabled); return;
                case HOTKEY_QUIT: Quit(); return;
                case HOTKEY_COLOR:
                    colors.Active = (colors.Active + 1) % colors.Slots.Length;
                    ColorsChanged();
                    break;
                case HOTKEY_SIZE:
                    // Proximo degrau acima da espessura atual, que pode ter vindo
                    // do slider e nao bater com nenhum degrau.
                    float next = Sizes.FirstOrDefault(s => s > canvas.Options.Size + 0.5f, Sizes[0]);
                    canvas.Options = canvas.Options with { Size = next };
                    break;
            }
            if (menu.IsOpen) SyncMenuFromCanvas();
            UpdateTooltip();
        };

        overlay.Tick += () =>
        {
            for (int undos = hooks?.TakePendingUndos() ?? 0; undos > 0; undos--) Undo();

            // Botao direito cicla o modo. Vem do hook, nao do Barrel Switch:
            // nenhum dos dois botoes desta caneta passa por ele.
            for (int clicks = hooks?.TakePendingRightClicks() ?? 0; clicks > 0; clicks--)
            {
                if (menu.IsOpen)
                {
                    menu.Close();
                    continue;
                }
                mode = mode switch
                {
                    PenMode.Draw => PenMode.Erase,
                    PenMode.Erase => PenMode.PickColor,
                    _ => PenMode.Draw,
                };
                canvas.CancelStroke();
                wasDown = false;
                UpdateTooltip();
            }

            while (pending.TryDequeue(out var sample))
            {
                if (!sample.InRange || !drawingEnabled)
                {
                    ReleaseCapture();
                    havePenAtTakeover = false;
                    continue;
                }

                if (!jumpFilter.Accept(sample.X, sample.Y)) continue;

                var raw = new Vec2(sample.X, sample.Y);

                // Enquanto o mouse tem o controle, hover NAO retoma a captura --
                // so encostar a ponta ou mover a caneta de verdade.
                if (Volatile.Read(ref mouseHasControlFlag) != 0)
                {
                    if (!havePenAtTakeover) { penAtTakeover = raw; havePenAtTakeover = true; }
                    if (!sample.TipDown && Vec2.Dist(raw, penAtTakeover) < TakeoverSlack)
                        continue;
                    Volatile.Write(ref mouseHasControlFlag, 0);
                    havePenAtTakeover = false;
                }

                lastPenActivity = Environment.TickCount64;
                Volatile.Write(ref penInRangeFlag, 1);
                overlay.ClickThrough = false;

                cursorVisible = true;
                cursor = mapper.ToScreen(sample.X, sample.Y);
                cursorPressure = sample.Pressure;

                // ---- menu aberto: a caneta so interage com ele
                if (menu.IsOpen)
                {
                    menu.Hover(cursor);

                    // Toque (descida) escolhe amostra ou pega um slider; arrasto
                    // move o slider pego. O contato que sobra depois de uma
                    // amostra fechar o menu nao desenha nem reabre: so uma nova
                    // descida da ponta arma o hold.
                    var action = MenuAction.None;
                    if (sample.TipDown && !wasDown) action = menu.Down(cursor);
                    else if (sample.TipDown) action = menu.Drag(cursor);
                    else if (wasDown) menu.Up();

                    if (action != MenuAction.None) ApplyFromMenu();
                    if (action == MenuAction.Close) menu.Close();
                    wasDown = sample.TipDown;
                    continue;
                }

                // Ponta invertida sempre apaga, independente do modo do botao.
                bool erasing = sample.Eraser || mode == PenMode.Erase;

                if (erasing)
                {
                    if (wasDown) { canvas.EndStroke(); wasDown = false; }
                    if (sample.TipDown) canvas.EraseAt(cursor, StrokeCanvas.EraserRadius);
                    else canvas.ResetErase();
                    if (!sample.TipDown) UpdateTooltip();
                    wasDown = sample.TipDown;
                    continue;
                }

                if (mode == PenMode.PickColor)
                {
                    if (sample.TipDown && !wasDown)
                    {
                        // Grava no slot ativo: a cor da caneta e sempre a de
                        // um slot, e fica salva como qualquer edicao do menu.
                        colors.Current = ReadScreenPixel(cursor);
                        ColorsChanged();
                        mode = PenMode.Draw;      // pegou a cor, volta a desenhar
                        UpdateTooltip();
                    }
                    wasDown = sample.TipDown;
                    continue;
                }

                // ---- modo desenho
                if (sample.TipDown && !wasDown)
                {
                    canvas.BeginStroke(cursor, sample.Pressure);
                    holdArmed = true;
                    holdOrigin = cursor;
                    holdStart = Environment.TickCount64;
                }
                else if (sample.TipDown)
                {
                    canvas.AddPoint(cursor, sample.Pressure);

                    // Distancia da DESCIDA, nao do ultimo ponto: saiu da folga uma
                    // vez, o traco e de verdade e o gesto morre ate levantar.
                    if (holdArmed && Vec2.Dist(cursor, holdOrigin) > HoldSlackPx)
                        holdArmed = false;
                }
                else if (wasDown)
                {
                    canvas.EndStroke();
                    UpdateTooltip();
                }
                wasDown = sample.TipDown;
            }

            // O prazo do hold e conferido por quadro, nao por amostra: com a ponta
            // perfeitamente parada a mesa pode parar de mandar report, e o menu
            // ficaria esperando um movimento para abrir. IsDrawing cobre todo
            // jeito de o traco acabar (levantar, borracha, troca de modo, perda
            // da caneta) sem precisar desarmar em cada um deles.
            if (holdArmed && canvas.IsDrawing && mode == PenMode.Draw && !menu.IsOpen
                && Environment.TickCount64 - holdStart >= HoldMs)
            {
                holdArmed = false;
                canvas.CancelStroke();
                // Contra o monitor sob a descida, nao contra o desktop virtual:
                // senao o painel pode nascer atravessando a divisa entre monitores.
                var host = OverlayWindow.MonitorAt((int)holdOrigin.X, (int)holdOrigin.Y);
                SyncMenuFromCanvas();
                menu.Open(holdOrigin, host.x, host.y, host.width, host.height);
                UpdateTooltip();
            }

            // Menu sem caneta por perto: o mouse assumiu, ou a caneta saiu e nao
            // voltou dentro da carencia. Sem isso o painel ficaria pendurado na
            // tela com o overlay em click-through, ignorando o mouse.
            if (menu.IsOpen
                && (Volatile.Read(ref mouseHasControlFlag) != 0
                    || (!PenInRange() && Environment.TickCount64 - lastPenActivity > MenuGraceMs)))
                menu.Close();

            if (!updateAnnounced && updater.Installed is { } installed)
            {
                updateAnnounced = true;
                tray.ShowUpdateReady(installed);
            }

            if (colorsDirty && !menu.IsDragging)
            {
                colors.Save();
                colorsDirty = false;
            }

            if (statusDirty && !menu.IsDragging)
            {
                UpdateTooltip();
                statusDirty = false;
            }

            // Rede de seguranca: sem sinal da caneta por meio segundo, devolve o
            // input. Sem isso, uma falha no meio de um traco deixaria a tela
            // inteira capturando cliques com um painel de menu preso por cima.
            if (!overlay.ClickThrough && Environment.TickCount64 - lastPenActivity > PenIdleMs)
                ReleaseCapture();

            // O cursor do sistema some enquanto a caneta manda -- o app ja
            // desenha o proprio, e dois cursores na tela so confundem.
            overlay.HideSystemCursor = PenInRange() && drawingEnabled;

            overlay.Render(skCanvas =>
            {
                canvas.Render(skCanvas);
                menu.Render(skCanvas);
                if (!cursorVisible) return;
                if (menu.IsOpen) canvas.RenderPointer(skCanvas, cursor);
                else canvas.RenderCursor(skCanvas, cursor, cursorPressure, wasDown,
                                         mode == PenMode.Erase);
            });

            // Depois do Render, nao antes: no primeiro quadro da captura e ele
            // que compoe o alfa de fundo 1/255, e so com esse alfa o overlay
            // existe para o hit-test e recebe o WM_SETCURSOR que esconde o cursor.
            // De brinde, ao voltar para o mouse o cursor continua de onde a
            // caneta parou, como com um driver de mesa normal.
            if (overlay.HideSystemCursor && cursorVisible)
                overlay.MoveSystemCursor((int)cursor.X, (int)cursor.Y);
        };

        UpdateTooltip();
        if (overlay.FailedHotkeys.Count > 0)
        {
            MessageBox.Show(
                "Estes atalhos ja estao em uso por outro programa:\n\n  "
                + string.Join("\n  ", overlay.FailedHotkeys)
                + "\n\nO menu da bandeja continua funcionando.", "Freedraw",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        try
        {
            // Application.Run e nao um laco proprio: o ContextMenuStrip da bandeja
            // depende do IMessageFilter do ToolStrip, que so roda dentro do laco do
            // WinForms. Com laco manual o dropdown nao fecha em clique fora nem
            // responde a Esc -- e a bandeja e a unica UI sempre alcancavel.
            Application.Run();
        }
        finally
        {
            // Sair pelo atalho no meio de um arrasto de slider ainda grava.
            if (colorsDirty) colors.Save();
            hooks?.Dispose();
            source.Dispose();
            overlay.Dispose();     // restaura o contador do ShowCursor
            canvas.Dispose();
        }
        return 0;
    }

    /// <summary>
    /// Le a cor do pixel da tela sob o cursor.
    ///
    /// O overlay esta por cima com alfa 1/255 preto (necessario para capturar o
    /// input), entao o valor lido vem escurecido em 1/255. A divisao desfaz essa
    /// mistura e devolve a cor original.
    /// </summary>
    private static SKColor ReadScreenPixel(Vec2 point)
    {
        var dc = GetDC(IntPtr.Zero);
        uint bgr = GetPixel(dc, (int)point.X, (int)point.Y);
        ReleaseDC(IntPtr.Zero, dc);

        if (bgr == 0xFFFFFFFF) return SKColors.White;   // CLR_INVALID

        byte Unblend(uint channel) => (byte)Math.Clamp(channel * 255 / 254, 0, 255);
        return new SKColor(Unblend(bgr & 0xFF), Unblend((bgr >> 8) & 0xFF), Unblend((bgr >> 16) & 0xFF));
    }

    private static string ModeName(PenMode mode) => mode switch
    {
        PenMode.Erase => "borracha",
        PenMode.PickColor => "conta-gotas",
        _ => "caneta",
    };
}
