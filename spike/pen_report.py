"""
Spike 4 -- le os reports HID da caneta de verdade e separa HOVER de CONTATO.

Os spikes anteriores mediram cursor e hook sem distinguir o estado da caneta, e
isso invalidou a conclusao: com a ponta encostada o Windows esconde o cursor e o
GetCursorPos congela, entao "cursor nao mexeu" era artefato do procedimento.

Aqui o estado vem do proprio descritor HID (Tip Switch / In Range), e cursor e
hook sao contados SEPARADAMENTE em cada estado. Tambem imprime pressao real --
e o mesmo parsing que o app final vai usar.

Rodar: python spike/pen_report.py
"""
import ctypes as C
import sys
import time
from ctypes import wintypes as W

user32 = C.WinDLL("user32", use_last_error=True)
kernel32 = C.WinDLL("kernel32", use_last_error=True)
hid = C.WinDLL("hid", use_last_error=True)

WM_INPUT, WM_DESTROY = 0x00FF, 0x0002
RID_INPUT = 0x10000003
RIDEV_INPUTSINK = 0x00000100
RIDI_PREPARSEDDATA = 0x20000005
RIDI_DEVICEINFO = 0x2000000B
RIM_TYPEMOUSE, RIM_TYPEHID = 0, 2
WH_MOUSE_LL = 14
HIDP_INPUT = 0
HIDP_STATUS_SUCCESS = 0x00110000
QS_ALLINPUT, PM_REMOVE = 0x04FF, 0x0001

# usages que nos interessam
UP_GENERIC, UP_DIGI = 0x01, 0x0D
US_X, US_Y = 0x30, 0x31
US_PRESSURE, US_TILT_X, US_TILT_Y = 0x30, 0x3D, 0x3E
US_IN_RANGE, US_TIP, US_BARREL, US_ERASER = 0x32, 0x42, 0x44, 0x45

USAGE = W.USHORT
LRESULT = C.c_longlong if C.sizeof(C.c_void_p) == 8 else C.c_long
ULONG_PTR = C.c_ulonglong if C.sizeof(C.c_void_p) == 8 else C.c_ulong

RUN_SECONDS = 120.0
for _a in sys.argv[1:]:
    if _a.startswith("--seconds="):
        RUN_SECONDS = float(_a.split("=", 1)[1])


class RAWINPUTHEADER(C.Structure):
    _fields_ = [("dwType", W.DWORD), ("dwSize", W.DWORD),
                ("hDevice", W.HANDLE), ("wParam", W.WPARAM)]


class RAWINPUTDEVICE(C.Structure):
    _fields_ = [("usUsagePage", USAGE), ("usUsage", USAGE),
                ("dwFlags", W.DWORD), ("hwndTarget", W.HWND)]


class RID_DEVICE_INFO_HID(C.Structure):
    _fields_ = [("dwVendorId", W.DWORD), ("dwProductId", W.DWORD),
                ("dwVersionNumber", W.DWORD), ("usUsagePage", USAGE), ("usUsage", USAGE)]


class RID_DEVICE_INFO_MOUSE(C.Structure):
    _fields_ = [("dwId", W.DWORD), ("dwNumberOfButtons", W.DWORD),
                ("dwSampleRate", W.DWORD), ("fHasHorizontalWheel", W.BOOL)]


class RID_DEVICE_INFO_KEYBOARD(C.Structure):
    _fields_ = [("dwType", W.DWORD), ("dwSubType", W.DWORD),
                ("dwKeyboardMode", W.DWORD), ("dwNumberOfFunctionKeys", W.DWORD),
                ("dwNumberOfIndicators", W.DWORD), ("dwNumberOfKeysTotal", W.DWORD)]


class _U(C.Union):
    _fields_ = [("mouse", RID_DEVICE_INFO_MOUSE),
                ("keyboard", RID_DEVICE_INFO_KEYBOARD), ("hid", RID_DEVICE_INFO_HID)]


class RID_DEVICE_INFO(C.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("cbSize", W.DWORD), ("dwType", W.DWORD), ("u", _U)]


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


kernel32.GetModuleHandleW.restype = W.HMODULE
kernel32.GetModuleHandleW.argtypes = [W.LPCWSTR]
user32.RegisterClassW.restype = W.ATOM
user32.RegisterClassW.argtypes = [C.POINTER(WNDCLASSW)]
user32.CreateWindowExW.restype = W.HWND
user32.CreateWindowExW.argtypes = [W.DWORD, W.LPCWSTR, W.LPCWSTR, W.DWORD,
                                   C.c_int, C.c_int, C.c_int, C.c_int,
                                   W.HWND, W.HMENU, W.HINSTANCE, W.LPVOID]
user32.DefWindowProcW.restype = LRESULT
user32.DefWindowProcW.argtypes = [W.HWND, W.UINT, W.WPARAM, W.LPARAM]
user32.SetWindowsHookExW.restype = W.HHOOK
user32.SetWindowsHookExW.argtypes = [C.c_int, C.c_void_p, W.HMODULE, W.DWORD]
user32.CallNextHookEx.restype = LRESULT
user32.CallNextHookEx.argtypes = [W.HHOOK, C.c_int, W.WPARAM, W.LPARAM]
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
user32.GetCursorPos.restype = W.BOOL
user32.GetCursorPos.argtypes = [C.POINTER(POINT)]

hid.HidP_GetUsageValue.argtypes = [C.c_int, USAGE, W.USHORT, USAGE,
                                   C.POINTER(W.ULONG), C.c_void_p,
                                   C.c_char_p, W.ULONG]
hid.HidP_GetUsages.argtypes = [C.c_int, USAGE, W.USHORT, C.POINTER(USAGE),
                               C.POINTER(W.ULONG), C.c_void_p,
                               C.c_char_p, W.ULONG]
hid.HidP_MaxUsageListLength.restype = W.ULONG
hid.HidP_MaxUsageListLength.argtypes = [C.c_int, USAGE, C.c_void_p]

INVALID = C.c_uint(-1).value

# ------------------------------------------------------------------ estado
preparsed = {}       # hDevice -> buffer de preparsed data
pen_state = {"contact": False, "in_range": False,
             "x": 0, "y": 0, "p": 0, "tx": 0, "ty": 0}
mode = ["?"]         # "PEN" | "MOUSE"
samples = []         # amostras pra imprimir no loop principal
cnt = {
    "reports": 0, "mouse_raw": 0,
    "hook_hover": 0, "hook_contact": 0, "hook_mouse": 0,
    "cur_hover": 0, "cur_contact": 0, "cur_mouse": 0,
    "pmax": 0, "errors": 0,
}
last_err = [""]


def preparsed_for(h):
    if h in preparsed:
        return preparsed[h]
    size = W.UINT(0)
    user32.GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, None, C.byref(size))
    buf = None
    if size.value:
        b = (C.c_byte * size.value)()
        if user32.GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, b, C.byref(size)) != INVALID:
            buf = b
    preparsed[h] = buf
    return buf


def is_digitizer(h):
    i = RID_DEVICE_INFO()
    i.cbSize = C.sizeof(RID_DEVICE_INFO)
    s = W.UINT(C.sizeof(RID_DEVICE_INFO))
    if user32.GetRawInputDeviceInfoW(h, RIDI_DEVICEINFO, C.byref(i), C.byref(s)) == INVALID:
        return False
    return i.dwType == RIM_TYPEHID and i.hid.usUsagePage == UP_DIGI


def value_of(pp, report, rlen, page, usage):
    v = W.ULONG(0)
    if hid.HidP_GetUsageValue(HIDP_INPUT, page, 0, usage, C.byref(v),
                              pp, report, rlen) == HIDP_STATUS_SUCCESS:
        return v.value
    return None


def buttons_of(pp, report, rlen):
    maxn = hid.HidP_MaxUsageListLength(HIDP_INPUT, UP_DIGI, pp)
    if not maxn:
        return set()
    arr = (USAGE * maxn)()
    n = W.ULONG(maxn)
    if hid.HidP_GetUsages(HIDP_INPUT, UP_DIGI, 0, arr, C.byref(n),
                          pp, report, rlen) != HIDP_STATUS_SUCCESS:
        return set()
    return set(arr[:n.value])


def parse_hid(hdev, buf, total):
    """Extrai X/Y/pressao/tilt/botoes de cada report dentro do RAWHID."""
    pp = preparsed_for(hdev)
    if pp is None:
        return
    off = C.sizeof(RAWINPUTHEADER)
    size_hid = C.cast(C.byref(buf, off), C.POINTER(W.DWORD)).contents.value
    count = C.cast(C.byref(buf, off + 4), C.POINTER(W.DWORD)).contents.value
    data_off = off + 8
    if not size_hid or not count:
        return

    for i in range(count):
        start = data_off + i * size_hid
        if start + size_hid > total:
            break
        # string_at le direto da memoria. Nao fatiar o array de c_byte: ele e
        # COM SINAL, entao byte > 127 vira negativo e bytearray() recusa.
        raw = C.string_at(C.byref(buf, start), size_hid)
        btns = buttons_of(pp, raw, size_hid)
        x = value_of(pp, raw, size_hid, UP_GENERIC, US_X)
        y = value_of(pp, raw, size_hid, UP_GENERIC, US_Y)
        p = value_of(pp, raw, size_hid, UP_DIGI, US_PRESSURE)
        tx = value_of(pp, raw, size_hid, UP_DIGI, US_TILT_X)
        ty = value_of(pp, raw, size_hid, UP_DIGI, US_TILT_Y)

        pen_state["contact"] = US_TIP in btns
        pen_state["in_range"] = US_IN_RANGE in btns
        if x is not None:
            pen_state["x"] = x
        if y is not None:
            pen_state["y"] = y
        if p is not None:
            pen_state["p"] = p
            cnt["pmax"] = max(cnt["pmax"], p)
        if tx is not None:
            pen_state["tx"] = tx
        if ty is not None:
            pen_state["ty"] = ty
        cnt["reports"] += 1

        if len(samples) < 4000:
            samples.append((pen_state["contact"], pen_state["in_range"],
                            pen_state["x"], pen_state["y"], pen_state["p"],
                            pen_state["tx"], pen_state["ty"], sorted(btns)))


def wnd_proc(hwnd, msg, wparam, lparam):
    if msg == WM_INPUT:
        size = W.UINT(0)
        user32.GetRawInputData(W.HANDLE(lparam), RID_INPUT, None,
                               C.byref(size), C.sizeof(RAWINPUTHEADER))
        if size.value:
            buf = (C.c_byte * size.value)()
            got = user32.GetRawInputData(W.HANDLE(lparam), RID_INPUT, buf,
                                         C.byref(size), C.sizeof(RAWINPUTHEADER))
            if got and got != INVALID:
                hdr = C.cast(buf, C.POINTER(RAWINPUTHEADER)).contents
                if hdr.dwType == RIM_TYPEHID and is_digitizer(hdr.hDevice):
                    mode[0] = "PEN"
                    # Um erro aqui e chamado milhares de vezes por segundo;
                    # sem isso o traceback inunda o console e esconde o dado.
                    try:
                        parse_hid(hdr.hDevice, buf, got)
                    except Exception as e:
                        cnt["errors"] += 1
                        if cnt["errors"] == 1:
                            last_err[0] = f"{type(e).__name__}: {e}"
                elif hdr.dwType == RIM_TYPEMOUSE:
                    mode[0] = "MOUSE"
                    pen_state["contact"] = False
                    cnt["mouse_raw"] += 1
    elif msg == WM_DESTROY:
        user32.PostQuitMessage(0)
        return 0
    return user32.DefWindowProcW(hwnd, msg, wparam, lparam)


WNDPROC = C.WINFUNCTYPE(LRESULT, W.HWND, W.UINT, W.WPARAM, W.LPARAM)
_wp = WNDPROC(wnd_proc)


def ll_mouse_proc(ncode, wparam, lparam):
    # zero I/O: callback lento faz o Windows pular o hook
    if ncode >= 0:
        if mode[0] == "PEN":
            if pen_state["contact"]:
                cnt["hook_contact"] += 1
            else:
                cnt["hook_hover"] += 1
        elif mode[0] == "MOUSE":
            cnt["hook_mouse"] += 1
    return user32.CallNextHookEx(None, ncode, wparam, lparam)


HOOKPROC = C.WINFUNCTYPE(LRESULT, C.c_int, W.WPARAM, W.LPARAM)
_hp = HOOKPROC(ll_mouse_proc)


def main():
    print("=" * 74)
    print("FREEDRAW -- reports HID da caneta, HOVER vs CONTATO")
    print("=" * 74)
    print("  1) Paire a caneta SEM ENCOSTAR e mova ~10s")
    print("  2) ENCOSTE e rabisque ~10s")
    print("  3) Mova o MOUSE ~5s")
    print(f"  (encerra sozinho em {int(RUN_SECONDS)}s)\n")

    hinst = kernel32.GetModuleHandleW(None)
    wc = WNDCLASSW()
    wc.lpfnWndProc = C.cast(_wp, C.c_void_p)
    wc.hInstance = hinst
    wc.lpszClassName = "FreedrawPenReport"
    user32.RegisterClassW(C.byref(wc))
    hwnd = user32.CreateWindowExW(0, "FreedrawPenReport", "spike", 0,
                                  0, 0, 0, 0, None, None, hinst, None)
    if not hwnd:
        raise C.WinError(C.get_last_error())

    # Sem essa checagem, uma falha de registro produz "0 eventos" identico a
    # "ninguem mexeu" -- dois diagnosticos muito diferentes.
    ok = 0
    for up, us in ((UP_GENERIC, 0x02), (UP_DIGI, 0x01), (UP_DIGI, 0x02)):
        rid = RAWINPUTDEVICE(up, us, RIDEV_INPUTSINK, hwnd)
        if user32.RegisterRawInputDevices(C.byref(rid), 1, C.sizeof(RAWINPUTDEVICE)):
            ok += 1
        else:
            print(f"  ! falhou registrar usage {up:02X}/{us:02X}: "
                  f"erro {C.get_last_error()}")
    if ok == 0:
        raise RuntimeError("nenhuma usage registrada -- nao ha o que medir")
    print(f"  Raw Input: {ok}/3 usages registradas")

    hhook = user32.SetWindowsHookExW(WH_MOUSE_LL, _hp, hinst, 0)
    if not hhook:
        raise C.WinError(C.get_last_error())
    print("  WH_MOUSE_LL instalado\n")

    msg = W.MSG()
    cur = POINT()
    prev = [-1, -1]
    shown = 0
    deadline = time.monotonic() + RUN_SECONDS
    tick = time.monotonic() + 10.0
    try:
        while time.monotonic() < deadline:
            user32.MsgWaitForMultipleObjectsEx(0, None, 50, QS_ALLINPUT, 0)
            drained = 0
            while drained < 200 and user32.PeekMessageW(C.byref(msg), None, 0, 0, PM_REMOVE):
                user32.DispatchMessageW(C.byref(msg))
                drained += 1

            # poll do cursor SEPARADO por estado -- o ponto que faltava antes
            if user32.GetCursorPos(C.byref(cur)):
                if (cur.x, cur.y) != (prev[0], prev[1]):
                    prev[0], prev[1] = cur.x, cur.y
                    if mode[0] == "PEN":
                        cnt["cur_contact" if pen_state["contact"] else "cur_hover"] += 1
                    elif mode[0] == "MOUSE":
                        cnt["cur_mouse"] += 1

            while shown < len(samples) and shown < 12:
                c, r, x, y, p, tx, ty, b = samples[shown]
                shown += 1
                est = "CONTATO" if c else ("hover" if r else "fora")
                print(f"  {est:<8} x={x:<6} y={y:<6} pressao={p:<6} "
                      f"tilt=({tx},{ty}) usages={[hex(u) for u in b]}")

            now = time.monotonic()
            if now >= tick:
                tick = now + 10.0
                print(f"  ... reports={cnt['reports']}  mouse={cnt['mouse_raw']}  "
                      f"pressao_max={cnt['pmax']}  ({int(deadline - now)}s)")
    except KeyboardInterrupt:
        pass
    finally:
        user32.UnhookWindowsHookEx(hhook)

    print("\n" + "=" * 74)
    print("RESUMO")
    print(f"  reports HID da caneta : {cnt['reports']}")
    print(f"  eventos de mouse      : {cnt['mouse_raw']}")
    print(f"  pressao maxima vista  : {cnt['pmax']}")
    print("\n  ESTADO           hook chamado   cursor mexeu")
    print(f"  hover (sem tocar)  {cnt['hook_hover']:>8}   {cnt['cur_hover']:>10}")
    print(f"  CONTATO            {cnt['hook_contact']:>8}   {cnt['cur_contact']:>10}")
    print(f"  mouse              {cnt['hook_mouse']:>8}   {cnt['cur_mouse']:>10}")

    if cnt["errors"]:
        print(f"\n  ! {cnt['errors']} erros no parsing. Primeiro: {last_err[0]}")

    print("\n  LEITURA")
    if cnt["reports"] == 0:
        print("    Nenhum report da caneta -- ela nao foi movida.")
        return
    if cnt["pmax"] == 0:
        print("    Pressao sempre 0: a ponta nunca encostou de verdade.")
    else:
        print(f"    Pressao chega ao app (max {cnt['pmax']}). Leitura via HID confirmada.")
    if cnt["hook_contact"] == 0 and cnt["reports"] > 100:
        print("    Durante o CONTATO o hook nao e chamado -> nada a suprimir enquanto")
        print("    voce desenha; o traco nao vaza clique pros apps de baixo.")
    elif cnt["hook_contact"]:
        print(f"    Durante o CONTATO o hook foi chamado {cnt['hook_contact']}x -> HA o que")
        print("    suprimir, e da pra suprimir (o hook enxerga).")


if __name__ == "__main__":
    main()
