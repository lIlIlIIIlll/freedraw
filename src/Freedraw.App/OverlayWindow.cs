using System.Runtime.InteropServices;
using SkiaSharp;
using static Freedraw.App.Win32;

namespace Freedraw.App;

/// <summary>
/// Janela de desenho: cobre o desktop virtual inteiro e fica sempre no topo.
///
/// Composicao via UpdateLayeredWindow: o Skia desenha num DIB de 32bpp com alfa
/// premultiplicado, que e exatamente o formato que a API espera.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "FreedrawOverlay";
    private const int TimerId = 1;

    public IntPtr Handle { get; private set; }
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    private readonly WndProc _wndProc;      // campo: o GC nao ve a referencia nativa
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private SKSurface? _surface;
    private int _timerTicks;

    public event Action? Tick;

    /// <summary>Recebe o id do atalho global (Win32.HOTKEY_*).</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>
    /// Layout de telas mudou e a geometria foi refeita. Quem depende de
    /// coordenadas de tela (mapeador da mesa, tracos ja desenhados) precisa
    /// reagir -- as coordenadas antigas nao valem mais.
    /// </summary>
    public event Action? DesktopChanged;

    public OverlayWindow()
    {
        _wndProc = WindowProc;
        var hInstance = GetModuleHandleW(null);
        var classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                // Sem cursor de classe, o mouse que passa pelo overlay enquanto ele
                // captura herda qualquer forma que estivesse antes -- inclusive nenhuma.
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                lpszClassName = classNamePtr,
            };
            RegisterClassW(ref wc);

            ReadDesktopBounds();

            Handle = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
                ClassName, "Freedraw", WS_POPUP | WS_VISIBLE,
                Left, Top, Width, Height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(classNamePtr); }

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW falhou: {Marshal.GetLastWin32Error()}");

        try
        {
            CreateSurface();
        }
        catch
        {
            // Nao deixar a janela orfa se a superficie falhar.
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
            throw;
        }

        foreach (var (id, key) in new[]
                 {
                     (HOTKEY_CLEAR, 'C'), (HOTKEY_UNDO, 'Z'), (HOTKEY_TOGGLE, 'D'),
                     (HOTKEY_COLOR, 'X'), (HOTKEY_SIZE, 'S'), (HOTKEY_QUIT, 'Q'),
                 })
        {
            if (!RegisterHotKey(Handle, id, MOD_CONTROL | MOD_ALT, key))
                FailedHotkeys.Add($"Ctrl+Alt+{key}");
        }

        SetTimer(Handle, new IntPtr(TimerId), 16, IntPtr.Zero);   // ~60Hz
    }

    private void ReadDesktopBounds()
    {
        Left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        Top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        Width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        Height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    private void CreateSurface()
    {
        // biHeight negativo = top-down, para bater com a ordem de linhas do Skia.
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };

        var screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);
        _dib = CreateDIBSection(screenDc, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screenDc);

        if (_dib == IntPtr.Zero)
        {
            if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            throw new InvalidOperationException("CreateDIBSection falhou");
        }

        _oldBitmap = SelectObject(_memDc, _dib);

        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, bits, Width * 4);
        if (_surface is null)
        {
            DestroySurface();
            throw new InvalidOperationException("SKSurface.Create falhou");
        }
    }

    private void DestroySurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        _oldBitmap = IntPtr.Zero;
    }

    /// <summary>
    /// Refaz janela, DIB e superficie para o novo layout de telas. Sem isso, o
    /// overlay fica preso na geometria da inicializacao e qualquer troca de
    /// resolucao ou de monitor o deixa desalinhado ate reiniciar o app.
    /// </summary>
    private void RebuildForCurrentDesktop()
    {
        int oldLeft = Left, oldTop = Top, oldWidth = Width, oldHeight = Height;
        ReadDesktopBounds();
        if (Left == oldLeft && Top == oldTop && Width == oldWidth && Height == oldHeight)
            return;

        DestroySurface();
        SetWindowPos(Handle, IntPtr.Zero, Left, Top, Width, Height,
                     SWP_NOZORDER | SWP_NOACTIVATE);
        try
        {
            CreateSurface();
        }
        catch
        {
            // Sem superficie nao ha o que compor; o Render vira no-op ate a
            // proxima mudanca de layout dar certo.
            return;
        }
        DesktopChanged?.Invoke();
    }

    /// <summary>
    /// Liga/desliga o click-through.
    ///
    /// ATENCAO -- tirar WS_EX_TRANSPARENT NAO basta. Numa janela layered composta
    /// com UpdateLayeredWindow, o hit-test do Windows usa o CANAL ALFA de cada
    /// pixel: onde alfa e 0 o clique atravessa, tenha WS_EX_TRANSPARENT ou nao.
    ///
    /// Por isso a captura exige as DUAS coisas: estilo sem WS_EX_TRANSPARENT e
    /// um alfa de fundo diferente de zero (ver <see cref="BaseAlpha"/>).
    /// </summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            _clickThrough = value;

            int ex = GetWindowLongW(Handle, GWL_EXSTYLE);
            int updated = value
                ? ex | (int)WS_EX_TRANSPARENT
                : ex & ~(int)WS_EX_TRANSPARENT;
            SetWindowLongW(Handle, GWL_EXSTYLE, updated);

            // Mudanca de estilo so vale depois de SWP_FRAMECHANGED.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }
    private bool _clickThrough = true;

    /// <summary>
    /// Alfa do fundo quando estamos capturando: 1/255 e suficiente para o
    /// hit-test enxergar a janela e imperceptivel a olho (0.4% de opacidade).
    /// </summary>
    private byte BaseAlpha => _clickThrough ? (byte)0 : (byte)1;

    /// <summary>Atalhos que outro app ja tinha registrado. Falhar aqui e comum e
    /// nao e fatal -- so avisa que aquele atalho nao vai funcionar.</summary>
    public List<string> FailedHotkeys { get; } = [];

    /// <summary>
    /// Esconde o cursor do sistema enquanto a caneta esta em alcance -- o app
    /// desenha o proprio, e dois cursores na tela so confundem.
    ///
    /// ShowCursor mantem um CONTADOR, nao um booleano: cada false precisa de um
    /// true correspondente. Por isso o estado e rastreado aqui e restaurado no
    /// Dispose; deixar o contador negativo sumiria com o cursor do usuario
    /// depois que o app fechasse.
    ///
    /// ATENCAO -- isto sozinho NAO esconde nada. ShowCursor e SetCursor so valem
    /// com o cursor sobre uma janela DESTA thread, e a caneta nao move o cursor:
    /// ele fica parado em cima do app onde o mouse o deixou. Por isso quem esconde
    /// tambem precisa trazer o cursor para o overlay (<see cref="MoveSystemCursor"/>),
    /// e o WM_SETCURSOR que chega entao e que o apaga.
    /// </summary>
    public bool HideSystemCursor
    {
        get => _cursorHidden;
        set
        {
            if (_cursorHidden == value) return;
            _cursorHidden = value;
            ShowCursor(!value);
            if (value) SetCursor(IntPtr.Zero);
        }
    }
    private bool _cursorHidden;

    /// <summary>
    /// Leva o cursor do sistema ate o ponto dado, em pixels fisicos de tela. So
    /// faz sentido com o overlay capturando: e o que poe o cursor sobre ele.
    /// SetCursorPos nao gera Raw Input, entao nao dispara a retomada pelo mouse.
    /// </summary>
    public void MoveSystemCursor(int x, int y) => SetCursorPos(x, y);

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                // A janela e NOACTIVATE, entao nunca volta ao topo sozinha: dentro
                // da banda topmost ela so afunda conforme outras janeloas
                // always-on-top sao ativadas. Reafirmar ~1x/s e barato.
                if ((++_timerTicks & 0x3F) == 0)
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                Tick?.Invoke();
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                RebuildForCurrentDesktop();
                return IntPtr.Zero;

            case WM_HOTKEY:
                HotkeyPressed?.Invoke(wParam.ToInt32());
                return IntPtr.Zero;

            case WM_SETCURSOR:
                if (!_cursorHidden) break;   // DefWindowProc aplica a seta da classe
                SetCursor(IntPtr.Zero);
                return new IntPtr(1);

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Desenha e joga na tela.</summary>
    public void Render(Action<SKCanvas> draw)
    {
        if (_surface is null) return;

        // A limpeza mora aqui, e nao no canvas de tracos, porque o alfa de fundo
        // faz parte do mecanismo de captura de input -- nao e detalhe de desenho.
        _surface.Canvas.Clear(new SKColor(0, 0, 0, BaseAlpha));
        draw(_surface.Canvas);
        _surface.Canvas.Flush();

        var dst = new POINT(Left, Top);
        var src = new POINT(0, 0);
        var size = new SIZE(Width, Height);
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        var screenDc = GetDC(IntPtr.Zero);
        UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    /// <summary>Retangulo do monitor primario, em coordenadas de tela.</summary>
    public static (int x, int y, int width, int height) PrimaryMonitor()
        => MonitorRect(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);

    /// <summary>Retangulo do monitor mais proximo do ponto dado.</summary>
    public static (int x, int y, int width, int height) MonitorAt(int x, int y)
        => MonitorRect(new POINT(x, y), MONITOR_DEFAULTTONEAREST);

    private static (int x, int y, int width, int height) MonitorRect(POINT point, uint flags)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromPoint(point, flags);
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info))
            return (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                    GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var r = info.rcMonitor;
        return (r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    public void Dispose()
    {
        // Antes de tudo: nao deixar o contador do cursor negativo.
        HideSystemCursor = false;

        if (Handle != IntPtr.Zero)
        {
            for (int id = HOTKEY_CLEAR; id <= HOTKEY_QUIT; id++)
                UnregisterHotKey(Handle, id);
            KillTimer(Handle, new IntPtr(TimerId));
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        DestroySurface();
    }
}
