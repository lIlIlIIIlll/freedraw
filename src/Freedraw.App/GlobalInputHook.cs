using System.Runtime.InteropServices;

namespace Freedraw.App;

/// <summary>
/// Hooks globais de teclado e mouse, ambos condicionados a caneta estar em
/// alcance.
///
/// Por que assim e nao filtrando o dispositivo no Raw Input: os dois botoes
/// desta caneta nao passam pelo Barrel Switch -- um virou tecla, o outro sai
/// pela colecao de mouse da mesa. Daria para identificar os dois por VID/PID,
/// mas Raw Input so OBSERVA. O hook tambem ENGOLE, e sem engolir o botao
/// direito continuaria abrindo menu de contexto nos apps de baixo.
///
/// A condicao "caneta em alcance" e o que evita sequestrar Ctrl+Z e botao
/// direito do resto do sistema.
///
/// Os dois callbacks precisam ser triviais: passar de LowLevelHooksTimeout
/// (~300ms) faz o Windows entregar o evento sem chamar o hook.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;

    private const int VK_CONTROL = 0x11;
    private const int VK_Z = 0x5A;

    private readonly HookProc _keyProc;      // campos: o GC nao ve o lado nativo
    private readonly HookProc _mouseProc;
    private readonly IntPtr _keyHook;
    private readonly IntPtr _mouseHook;
    private readonly Func<bool> _armed;

    private int _pendingUndos;
    private int _pendingRightClicks;

    /// <param name="armed">Consultado dentro dos callbacks -- so leitura de campo.</param>
    public GlobalInputHook(Func<bool> armed)
    {
        _armed = armed;
        _keyProc = KeyboardProc;
        _mouseProc = MouseProc;

        var module = GetModuleHandleW(null);

        // O codigo de erro tem que ser capturado logo apos CADA chamada: o
        // runtime guarda so o do ultimo P/Invoke, entao ler depois das duas
        // sempre reportaria o do hook de mouse -- inclusive quando quem falhou
        // foi o de teclado, e inclusive "0" quando o segundo teve sucesso.
        _keyHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyProc, module, 0);
        int keyError = Marshal.GetLastWin32Error();

        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);
        int mouseError = Marshal.GetLastWin32Error();

        if (_keyHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            var failed = _keyHook == IntPtr.Zero
                ? $"WH_KEYBOARD_LL (erro {keyError})"
                : $"WH_MOUSE_LL (erro {mouseError})";
            Dispose();
            throw new InvalidOperationException($"SetWindowsHookEx falhou em {failed}");
        }
    }

    public int TakePendingUndos() => Interlocked.Exchange(ref _pendingUndos, 0);
    public int TakePendingRightClicks() => Interlocked.Exchange(ref _pendingRightClicks, 0);

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) && _armed())
        {
            int vk = Marshal.ReadInt32(lParam);
            if (vk == VK_Z && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
            {
                Interlocked.Increment(ref _pendingUndos);
                return new IntPtr(1);   // engole: o app de baixo nao desfaz junto
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _armed())
        {
            if (wParam == WM_RBUTTONDOWN)
            {
                Interlocked.Increment(ref _pendingRightClicks);
                return new IntPtr(1);
            }
            // Engolir so o DOWN deixaria o UP orfao, e alguns apps abrem o menu
            // de contexto no UP.
            if (wParam == WM_RBUTTONUP)
                return new IntPtr(1);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
