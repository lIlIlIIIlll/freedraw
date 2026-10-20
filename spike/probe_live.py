"""
Spike 2/2 -- prova ao vivo da arquitetura proposta.

Roda simultaneamente:
  1. Raw Input com RIDEV_INPUTSINK numa janela oculta -> identifica QUAL dispositivo
     fisico gerou cada evento (o LL hook sozinho nao sabe isso).
  2. WH_MOUSE_LL -> ve todo movimento de cursor e le o dwExtraInfo, onde a stack de
     Windows Ink carimba a assinatura 0xFF515700 quando o evento veio de caneta/touch.

Imprime uma linha por evento classificado. Move a caneta, depois o mouse, e compara.

  python spike/probe_live.py            # so observa (seguro)
  python spike/probe_live.py --block    # ENGOLE eventos da caneta -> testa se da pra
                                        # bloquear o cursor. Mouse continua funcionando.

Ctrl+C pra sair.
"""
import ctypes as C
import sys
import time
from ctypes import wintypes as W

user32 = C.WinDLL("user32", use_last_error=True)
kernel32 = C.WinDLL("kernel32", use_last_error=True)

# ---------------------------------------------------------------- constantes
WM_INPUT = 0x00FF
WM_DESTROY = 0x0002
RID_INPUT = 0x10000003
RIDEV_INPUTSINK = 0x00000100
RIDI_DEVICENAME = 0x20000007
RIDI_DEVICEINFO = 0x2000000B
RIM_TYPEMOUSE, RIM_TYPEHID = 0, 2

WH_MOUSE_LL = 14
WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0201, 0x0202
WM_RBUTTONDOWN, WM_RBUTTONUP = 0x0204, 0x0205
MOUSE_MSG = {WM_MOUSEMOVE: "MOVE", WM_LBUTTONDOWN: "LDOWN", WM_LBUTTONUP: "LUP",
             WM_RBUTTONDOWN: "RDOWN", WM_RBUTTONUP: "RUP"}

# Assinatura que a stack de Ink carimba em dwExtraInfo (MI_WP_SIGNATURE).
# (extra & 0xFFFFFF00) == 0xFF515700  -> caneta ou touch
# bit 0x80 ligado -> touch; desligado -> caneta
INK_SIGNATURE = 0xFF515700
INK_MASK = 0xFFFFFF00
INK_TOUCH_BIT = 0x80

LRESULT = C.c_longlong if C.sizeof(C.c_void_p) == 8 else C.c_long
ULONG_PTR = C.c_ulonglong if C.sizeof(C.c_void_p) == 8 else C.c_ulong

QS_ALLINPUT = 0x04FF
PM_REMOVE = 0x0001

BLOCK_PEN = "--block" in sys.argv

# Encerra por EVENTO, nao por relogio: espera ate coletar amostras suficientes
# dos dois tipos de dispositivo. Assim quem roda nao precisa estar na frente da
# maquina no instante exato em que o processo sobe. O tempo e so o teto.
RUN_SECONDS = 180.0
NEED_PEN = 1000     # amostra grande: 30 eventos (~0.15s) e fino demais pra concluir
NEED_MOUSE = 300
for _a in sys.argv[1:]:
    if _a.startswith("--seconds="):
        RUN_SECONDS = float(_a.split("=", 1)[1])


# ---------------------------------------------------------------- structs
class RAWINPUTHEADER(C.Structure):
    _fields_ = [("dwType", W.DWORD), ("dwSize", W.DWORD),
                ("hDevice", W.HANDLE), ("wParam", W.WPARAM)]


class RAWINPUTDEVICE(C.Structure):
    _fields_ = [("usUsagePage", W.USHORT), ("usUsage", W.USHORT),
                ("dwFlags", W.DWORD), ("hwndTarget", W.HWND)]


class RID_DEVICE_INFO_HID(C.Structure):
    _fields_ = [("dwVendorId", W.DWORD), ("dwProductId", W.DWORD),
                ("dwVersionNumber", W.DWORD),
                ("usUsagePage", W.USHORT), ("usUsage", W.USHORT)]


class RID_DEVICE_INFO_MOUSE(C.Structure):
    _fields_ = [("dwId", W.DWORD), ("dwNumberOfButtons", W.DWORD),
                ("dwSampleRate", W.DWORD), ("fHasHorizontalWheel", W.BOOL)]


class RID_DEVICE_INFO_KEYBOARD(C.Structure):
    _fields_ = [("dwType", W.DWORD), ("dwSubType", W.DWORD),
                ("dwKeyboardMode", W.DWORD), ("dwNumberOfFunctionKeys", W.DWORD),
                ("dwNumberOfIndicators", W.DWORD), ("dwNumberOfKeysTotal", W.DWORD)]


class _RID_UNION(C.Union):
    _fields_ = [("mouse", RID_DEVICE_INFO_MOUSE),
                ("keyboard", RID_DEVICE_INFO_KEYBOARD),
                ("hid", RID_DEVICE_INFO_HID)]


class RID_DEVICE_INFO(C.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("cbSize", W.DWORD), ("dwType", W.DWORD), ("u", _RID_UNION)]


class POINT(C.Structure):
    _fields_ = [("x", W.LONG), ("y", W.LONG)]


class MSLLHOOKSTRUCT(C.Structure):
    _fields_ = [("pt", POINT), ("mouseData", W.DWORD), ("flags", W.DWORD),
                ("time", W.DWORD), ("dwExtraInfo", ULONG_PTR)]


class WNDCLASSW(C.Structure):
    _fields_ = [("style", W.UINT), ("lpfnWndProc", C.c_void_p),
                ("cbClsExtra", C.c_int), ("cbWndExtra", C.c_int),
                ("hInstance", W.HINSTANCE), ("hIcon", W.HICON),
                ("hCursor", W.HANDLE), ("hbrBackground", W.HANDLE),
                ("lpszMenuName", W.LPCWSTR), ("lpszClassName", W.LPCWSTR)]


# --- prototipos ------------------------------------------------------------
# Obrigatorio em x64: sem restype explicito o ctypes assume c_int e TRUNCA
# qualquer retorno que seja ponteiro/handle (HWND, HHOOK, HMODULE, LRESULT).
kernel32.GetModuleHandleW.restype = W.HMODULE
kernel32.GetModuleHandleW.argtypes = [W.LPCWSTR]

user32.RegisterClassW.restype = W.ATOM
user32.RegisterClassW.argtypes = [C.POINTER(WNDCLASSW)]

user32.CreateWindowExW.restype = W.HWND
user32.CreateWindowExW.argtypes = [
    W.DWORD, W.LPCWSTR, W.LPCWSTR, W.DWORD,
    C.c_int, C.c_int, C.c_int, C.c_int,
    W.HWND, W.HMENU, W.HINSTANCE, W.LPVOID,
]

user32.DefWindowProcW.restype = LRESULT
user32.DefWindowProcW.argtypes = [W.HWND, W.UINT, W.WPARAM, W.LPARAM]

user32.SetWindowsHookExW.restype = W.HHOOK
user32.SetWindowsHookExW.argtypes = [C.c_int, C.c_void_p, W.HMODULE, W.DWORD]
user32.CallNextHookEx.restype = LRESULT
user32.CallNextHookEx.argtypes = [W.HHOOK, C.c_int, W.WPARAM, W.LPARAM]
user32.UnhookWindowsHookEx.restype = W.BOOL
user32.UnhookWindowsHookEx.argtypes = [W.HHOOK]

user32.GetRawInputData.restype = W.UINT
user32.GetRawInputData.argtypes = [W.HANDLE, W.UINT, C.c_void_p,
                                   C.POINTER(W.UINT), W.UINT]
user32.GetRawInputDeviceInfoW.restype = W.UINT
user32.GetRawInputDeviceInfoW.argtypes = [W.HANDLE, W.UINT, C.c_void_p,
                                          C.POINTER(W.UINT)]
user32.RegisterRawInputDevices.restype = W.BOOL
user32.RegisterRawInputDevices.argtypes = [C.c_void_p, W.UINT, W.UINT]

user32.MsgWaitForMultipleObjectsEx.restype = W.DWORD
user32.MsgWaitForMultipleObjectsEx.argtypes = [W.DWORD, C.c_void_p, W.DWORD,
                                               W.DWORD, W.DWORD]
user32.PeekMessageW.restype = W.BOOL
user32.PeekMessageW.argtypes = [C.POINTER(W.MSG), W.HWND, W.UINT, W.UINT, W.UINT]
user32.TranslateMessage.argtypes = [C.POINTER(W.MSG)]
user32.DispatchMessageW.restype = LRESULT
user32.DispatchMessageW.argtypes = [C.POINTER(W.MSG)]
user32.GetCursorPos.restype = W.BOOL


# ---------------------------------------------------------------- estado global
device_cache = {}      # hDevice -> rotulo legivel
current_mode = "?"     # "PEN" | "MOUSE" | "?"
mode_source = ""
last_print = 0.0
last_extra = [0]
counters = {"pen_raw": 0, "mouse_raw": 0, "hook_ink": 0, "hook_plain": 0,
            "blocked": 0, "hook_during_pen": 0, "hook_during_mouse": 0,
            "cursor_moved_pen": 0, "cursor_moved_mouse": 0}


def describe_device(hdev):
    """Traduz um handle de dispositivo num rotulo, com cache."""
    if hdev in device_cache:
        return device_cache[hdev]

    size = W.UINT(0)
    user32.GetRawInputDeviceInfoW(hdev, RIDI_DEVICENAME, None, C.byref(size))
    name = ""
    if size.value:
        buf = C.create_unicode_buffer(size.value + 1)
        user32.GetRawInputDeviceInfoW(hdev, RIDI_DEVICENAME, buf, C.byref(size))
        name = buf.value

    info = RID_DEVICE_INFO()
    info.cbSize = C.sizeof(RID_DEVICE_INFO)
    isize = W.UINT(C.sizeof(RID_DEVICE_INFO))
    user32.GetRawInputDeviceInfoW(hdev, RIDI_DEVICEINFO, C.byref(info), C.byref(isize))

    if info.dwType == RIM_TYPEHID:
        up, us = info.hid.usUsagePage, info.hid.usUsage
        is_digitizer = (up == 0x0D)
        label = (f"HID VID_{info.hid.dwVendorId:04X}&PID_{info.hid.dwProductId:04X} "
                 f"usage {up:02X}/{us:02X}")
        kind = "PEN" if is_digitizer else "MOUSE"
    else:
        label = f"MOUSE {name.split('#')[1] if '#' in name else name}"
        kind = "MOUSE"

    device_cache[hdev] = (kind, label, name)
    return device_cache[hdev]


# ---------------------------------------------------------------- Raw Input
def wnd_proc(hwnd, msg, wparam, lparam):
    global current_mode, mode_source
    if msg == WM_INPUT:
        size = W.UINT(0)
        user32.GetRawInputData(W.HANDLE(lparam), RID_INPUT, None,
                               C.byref(size), C.sizeof(RAWINPUTHEADER))
        if size.value:
            buf = (C.c_byte * size.value)()
            got = user32.GetRawInputData(W.HANDLE(lparam), RID_INPUT, buf,
                                         C.byref(size), C.sizeof(RAWINPUTHEADER))
            if got and got != C.c_uint(-1).value:
                hdr = C.cast(buf, C.POINTER(RAWINPUTHEADER)).contents
                if hdr.hDevice:
                    kind, label, _ = describe_device(hdr.hDevice)
                    if kind != current_mode:
                        print(f"\n  ### MODO -> {kind}   ({label})\n")
                    current_mode, mode_source = kind, label
                    counters["pen_raw" if kind == "PEN" else "mouse_raw"] += 1
    elif msg == WM_DESTROY:
        user32.PostQuitMessage(0)
        return 0
    return user32.DefWindowProcW(hwnd, msg, wparam, lparam)


WNDPROC = C.WINFUNCTYPE(LRESULT, W.HWND, W.UINT, W.WPARAM, W.LPARAM)
_wnd_proc_ref = WNDPROC(wnd_proc)   # manter referencia viva ou o GC derruba o callback


def create_hidden_window():
    """Janela oculta REGULAR -- message-only (HWND_MESSAGE) nao e confiavel
    com RIDEV_INPUTSINK."""
    hinst = kernel32.GetModuleHandleW(None)
    wc = WNDCLASSW()
    wc.lpfnWndProc = C.cast(_wnd_proc_ref, C.c_void_p)
    wc.hInstance = hinst
    wc.lpszClassName = "FreedrawSpikeSink"
    if not user32.RegisterClassW(C.byref(wc)):
        raise C.WinError(C.get_last_error())
    hwnd = user32.CreateWindowExW(0, "FreedrawSpikeSink", "spike", 0,
                                  0, 0, 0, 0, None, None, hinst, None)
    if not hwnd:
        raise C.WinError(C.get_last_error())
    return hwnd


def register_raw_input(hwnd):
    targets = [
        (0x01, 0x02, "Generic Desktop / Mouse"),
        (0x0D, 0x01, "Digitizers / Digitizer"),
        (0x0D, 0x02, "Digitizers / Pen"),
        (0x0D, 0x20, "Digitizers / Stylus"),
    ]
    ok = []
    for up, us, name in targets:
        rid = RAWINPUTDEVICE(up, us, RIDEV_INPUTSINK, hwnd)
        if user32.RegisterRawInputDevices(C.byref(rid), 1, C.sizeof(RAWINPUTDEVICE)):
            ok.append(name)
        else:
            print(f"  ! falhou registrar {name}: erro {C.get_last_error()}")
    return ok


# ---------------------------------------------------------------- LL hook
def ll_mouse_proc(ncode, wparam, lparam):
    """ZERO I/O aqui dentro.

    Um callback lento faz o Windows entregar o evento SEM chamar o hook
    (LowLevelHooksTimeout, ~300ms) -- o que produz exatamente o falso negativo
    que invalidou a primeira medicao. Nada de print, format, ou time() aqui:
    so incremento de contador. A impressao acontece no loop principal.
    """
    if ncode < 0:
        return user32.CallNextHookEx(None, ncode, wparam, lparam)

    ms = C.cast(lparam, C.POINTER(MSLLHOOKSTRUCT)).contents
    extra = ms.dwExtraInfo

    if (extra & INK_MASK) == INK_SIGNATURE:
        counters["hook_ink"] += 1
    else:
        counters["hook_plain"] += 1
    last_extra[0] = extra

    # A METRICA DECISIVA: o hook e chamado enquanto a caneta esta ativa?
    if current_mode == "PEN":
        counters["hook_during_pen"] += 1
        if BLOCK_PEN:
            counters["blocked"] += 1
            return 1   # engole: cursor nao deve se mover, app abaixo nao recebe
    elif current_mode == "MOUSE":
        counters["hook_during_mouse"] += 1

    return user32.CallNextHookEx(None, ncode, wparam, lparam)


HOOKPROC = C.WINFUNCTYPE(LRESULT, C.c_int, W.WPARAM, W.LPARAM)
_hook_proc_ref = HOOKPROC(ll_mouse_proc)


# ---------------------------------------------------------------- main
def main():
    print("=" * 74)
    print("FREEDRAW -- spike de deteccao de origem do ponteiro")
    print("=" * 74)
    if BLOCK_PEN:
        print("MODO --block: eventos da CANETA serao ENGOLIDOS.")
        print("Se o cursor congelar ao mover a caneta, o bloqueio funciona.")
        print("O mouse continua normal -- use ele pra dar Ctrl+C aqui.\n")
    else:
        print("Modo observacao (nao bloqueia nada).\n")

    hwnd = create_hidden_window()
    ok = register_raw_input(hwnd)
    print(f"  Raw Input registrado: {', '.join(ok)}")

    user32.SetWindowsHookExW.restype = W.HHOOK
    hhook = user32.SetWindowsHookExW(WH_MOUSE_LL, _hook_proc_ref,
                                     kernel32.GetModuleHandleW(None), 0)
    if not hhook:
        raise C.WinError(C.get_last_error())
    print("  WH_MOUSE_LL instalado\n")
    print("  >>> Mova a CANETA na mesa, depois mova o MOUSE. Ctrl+C pra sair.\n")

    msg = W.MSG()
    cur = POINT()
    prev_pos = [-1, -1]
    deadline = time.monotonic() + RUN_SECONDS
    next_tick = time.monotonic() + 15.0
    try:
        while time.monotonic() < deadline:
            # Acorda a cada 100ms mesmo sem eventos, senao o Ctrl+C ficaria
            # preso dentro da chamada nativa ate o proximo movimento.
            user32.MsgWaitForMultipleObjectsEx(0, None, 100, QS_ALLINPUT, 0)
            while user32.PeekMessageW(C.byref(msg), None, 0, 0, PM_REMOVE):
                user32.TranslateMessage(C.byref(msg))
                user32.DispatchMessageW(C.byref(msg))

            # O DISCRIMINADOR: o cursor se mexeu de fato? Comparado com quantas
            # vezes o hook foi chamado, isso separa "a caneta contorna o hook"
            # de "a caneta nem mexe o cursor".
            if user32.GetCursorPos(C.byref(cur)):
                if (cur.x, cur.y) != (prev_pos[0], prev_pos[1]):
                    prev_pos[0], prev_pos[1] = cur.x, cur.y
                    if current_mode == "PEN":
                        counters["cursor_moved_pen"] += 1
                    elif current_mode == "MOUSE":
                        counters["cursor_moved_mouse"] += 1

            if counters["pen_raw"] >= NEED_PEN and counters["mouse_raw"] >= NEED_MOUSE:
                print("\n  >>> amostras suficientes dos dois dispositivos, encerrando.")
                break

            now = time.monotonic()
            if now >= next_tick:
                next_tick = now + 15.0
                restam = int(deadline - now)
                print(f"  ... aguardando  caneta {counters['pen_raw']}/{NEED_PEN}  "
                      f"mouse {counters['mouse_raw']}/{NEED_MOUSE}  ({restam}s restantes)")
    except KeyboardInterrupt:
        pass
    finally:
        user32.UnhookWindowsHookEx(hhook)
        print("\n" + "=" * 74)
        print("RESUMO")
        print(f"  Raw Input  -> eventos de digitizer : {counters['pen_raw']}")
        print(f"  Raw Input  -> eventos de mouse     : {counters['mouse_raw']}")
        print(f"  LL hook    -> com assinatura Ink   : {counters['hook_ink']}")
        print(f"  LL hook    -> sem assinatura       : {counters['hook_plain']}")
        if BLOCK_PEN:
            print(f"  LL hook    -> eventos engolidos    : {counters['blocked']}")
        print("\n  Dispositivos vistos:")
        for kind, label, name in device_cache.values():
            print(f"    [{kind:5}] {label}")
        if not device_cache:
            print("    (nenhum)")

        # A razao decisiva: de cada evento de caneta visto pelo Raw Input,
        # quantos chegaram ao WH_MOUSE_LL?
        pr, hp = counters["pen_raw"], counters["hook_during_pen"]
        cmp_, cmm = counters["cursor_moved_pen"], counters["cursor_moved_mouse"]
        print(f"\n  Durante CANETA -> hook chamado {hp}x, cursor mexeu {cmp_}x")
        print(f"  Durante MOUSE  -> hook chamado {counters['hook_during_mouse']}x, "
              f"cursor mexeu {cmm}x")
        print(f"  Ultimo dwExtraInfo visto: 0x{last_extra[0]:X}")

        if pr < NEED_PEN * 0.5:
            print(f"\n  AMOSTRA DE CANETA INSUFICIENTE ({pr}) -- nao da pra concluir nada.")
        elif cmp_ == 0:
            print("\n  A caneta NAO mexe o cursor. Nao ha o que suprimir --")
            print("  o desenho pode simplesmente usar as coordenadas do Raw Input.")
        elif hp == 0:
            print("\n  PROVADO: o cursor se move com a caneta mas o WH_MOUSE_LL nunca")
            print("  e chamado. A caneta contorna o hook -> supressao via hook e")
            print("  inviavel. Plano B: ClipCursor durante o traco, ou overlay")
            print("  hit-testable capturando o input de verdade.")
        elif hp < pr * 0.5:
            print(f"\n  PARCIAL: hook ve {100.0 * hp / pr:.1f}% dos eventos da caneta.")
            print("  Supressao seria furada -- parte dos eventos vazaria pros apps.")
        else:
            print("\n  OK -- o hook ve o movimento da caneta e pode suprimi-lo.")
            print("  Arquitetura Raw Input (dados) + LL hook (supressao) confirmada.")

        print("\n  VEREDITO")
        total = counters["pen_raw"] + counters["mouse_raw"]
        if total == 0:
            print("    INCONCLUSIVO -- zero eventos, incluindo zero de mouse.")
            print("    Ninguem mexeu em nada durante a janela. Nao testou a deteccao;")
            print("    rode de novo e mexa na caneta E no mouse enquanto roda.")
        elif counters["pen_raw"] == 0:
            print("    So chegou evento de mouse. Ou a caneta nao foi movida, ou os")
            print("    coletores de digitizer nao emitem -- nesse caso a mesa esta em")
            print("    modo mouse e a deteccao tem que sair por VID/PID, nao por usage.")
        elif counters["mouse_raw"] == 0:
            print("    So chegou evento de digitizer. Falta a metade de controle:")
            print("    mexa o mouse tambem pra confirmar que os dois se distinguem.")
        else:
            print("    OK -- os dois dispositivos emitiram e foram distinguidos.")
            if counters["hook_ink"] == 0:
                print("    Porem NENHUM evento trouxe a assinatura Ink (0xFF515700):")
                print("    o driver nao usa Windows Ink. Raw Input vira OBRIGATORIO.")
            else:
                print(f"    E {counters['hook_ink']} eventos trouxeram assinatura Ink ->")
                print("    da pra classificar so com o hook, Raw Input vira redundancia.")


if __name__ == "__main__":
    main()
