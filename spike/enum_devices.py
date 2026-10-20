"""
Spike 1/2 -- enumera todos os dispositivos de Raw Input com usage page/usage.

Isso responde: "a mesa se apresenta ao Windows como digitizer (usage page 0x0D)
ou como um mouse genérico?" -- que e a pergunta que decide a arquitetura inteira.

Rodar: python spike/enum_devices.py
Seguro: so le, nao instala hook nenhum.
"""
import ctypes as C
from ctypes import wintypes as W

user32 = C.WinDLL("user32", use_last_error=True)

RIDI_DEVICENAME = 0x20000007
RIDI_DEVICEINFO = 0x2000000B

RIM_TYPEMOUSE, RIM_TYPEKEYBOARD, RIM_TYPEHID = 0, 1, 2
TYPE_NAME = {RIM_TYPEMOUSE: "MOUSE", RIM_TYPEKEYBOARD: "KEYBOARD", RIM_TYPEHID: "HID"}

# HID usage pages/usages que importam pra nos
USAGE_NAMES = {
    (0x01, 0x02): "Generic Desktop / Mouse",
    (0x01, 0x06): "Generic Desktop / Keyboard",
    (0x01, 0x0C): "Generic Desktop / Wireless Radio Controls",
    (0x0C, 0x01): "Consumer / Consumer Control",
    (0x0D, 0x01): "Digitizers / Digitizer          <<< MESA",
    (0x0D, 0x02): "Digitizers / Pen                <<< MESA",
    (0x0D, 0x03): "Digitizers / Light Pen",
    (0x0D, 0x04): "Digitizers / Touch Screen",
    (0x0D, 0x05): "Digitizers / Touch Pad",
    (0x0D, 0x07): "Digitizers / Whiteboard",
    (0x0D, 0x20): "Digitizers / Stylus             <<< MESA",
    (0x0D, 0x21): "Digitizers / Puck",
    (0x0D, 0x22): "Digitizers / Finger",
}


class RAWINPUTDEVICELIST(C.Structure):
    _fields_ = [("hDevice", W.HANDLE), ("dwType", W.DWORD)]


class RID_DEVICE_INFO_MOUSE(C.Structure):
    _fields_ = [("dwId", W.DWORD), ("dwNumberOfButtons", W.DWORD),
                ("dwSampleRate", W.DWORD), ("fHasHorizontalWheel", W.BOOL)]


class RID_DEVICE_INFO_KEYBOARD(C.Structure):
    _fields_ = [("dwType", W.DWORD), ("dwSubType", W.DWORD),
                ("dwKeyboardMode", W.DWORD), ("dwNumberOfFunctionKeys", W.DWORD),
                ("dwNumberOfIndicators", W.DWORD), ("dwNumberOfKeysTotal", W.DWORD)]


class RID_DEVICE_INFO_HID(C.Structure):
    _fields_ = [("dwVendorId", W.DWORD), ("dwProductId", W.DWORD),
                ("dwVersionNumber", W.DWORD),
                ("usUsagePage", W.USHORT), ("usUsage", W.USHORT)]


class _RID_UNION(C.Union):
    _fields_ = [("mouse", RID_DEVICE_INFO_MOUSE),
                ("keyboard", RID_DEVICE_INFO_KEYBOARD),
                ("hid", RID_DEVICE_INFO_HID)]


class RID_DEVICE_INFO(C.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("cbSize", W.DWORD), ("dwType", W.DWORD), ("u", _RID_UNION)]


def get_device_list():
    count = W.UINT(0)
    size = C.sizeof(RAWINPUTDEVICELIST)
    user32.GetRawInputDeviceList(None, C.byref(count), size)
    if count.value == 0:
        return []
    buf = (RAWINPUTDEVICELIST * count.value)()
    n = user32.GetRawInputDeviceList(buf, C.byref(count), size)
    if n == C.c_uint(-1).value:
        raise C.WinError(C.get_last_error())
    return list(buf[:n])


def get_device_name(hdev):
    size = W.UINT(0)
    user32.GetRawInputDeviceInfoW(hdev, RIDI_DEVICENAME, None, C.byref(size))
    if size.value == 0:
        return "<sem nome>"
    buf = C.create_unicode_buffer(size.value + 1)
    user32.GetRawInputDeviceInfoW(hdev, RIDI_DEVICENAME, buf, C.byref(size))
    return buf.value


def get_device_info(hdev):
    info = RID_DEVICE_INFO()
    info.cbSize = C.sizeof(RID_DEVICE_INFO)
    size = W.UINT(C.sizeof(RID_DEVICE_INFO))
    r = user32.GetRawInputDeviceInfoW(hdev, RIDI_DEVICEINFO, C.byref(info), C.byref(size))
    if r == C.c_uint(-1).value:
        return None
    return info


def main():
    devices = get_device_list()
    print(f"{len(devices)} dispositivos de Raw Input\n")

    interesting = []
    for d in devices:
        name = get_device_name(d.hDevice)
        info = get_device_info(d.hDevice)
        kind = TYPE_NAME.get(d.dwType, str(d.dwType))

        # Dispositivos internos do Windows so poluem a saida
        if "RDP_" in name or "ROOT#" in name.upper():
            continue

        print(f"[{kind:8}] handle=0x{d.hDevice:X}")
        print(f"           {name}")

        if info is None:
            print("           <sem RIDI_DEVICEINFO>\n")
            continue

        if d.dwType == RIM_TYPEHID:
            up, us = info.hid.usUsagePage, info.hid.usUsage
            label = USAGE_NAMES.get((up, us), "desconhecido")
            print(f"           VID={info.hid.dwVendorId:04X} PID={info.hid.dwProductId:04X} "
                  f"ver={info.hid.dwVersionNumber}")
            print(f"           usage page=0x{up:02X} usage=0x{us:02X}  -> {label}")
            if up == 0x0D:
                interesting.append((name, info.hid.dwVendorId, info.hid.dwProductId, up, us))
        elif d.dwType == RIM_TYPEMOUSE:
            print(f"           id={info.mouse.dwId} botoes={info.mouse.dwNumberOfButtons} "
                  f"taxa={info.mouse.dwSampleRate}Hz hwheel={bool(info.mouse.fHasHorizontalWheel)}")
        print()

    print("=" * 70)
    if interesting:
        print("DIGITIZERS ENCONTRADOS (usage page 0x0D):")
        for name, vid, pid, up, us in interesting:
            print(f"  VID_{vid:04X}&PID_{pid:04X}  usage 0x{up:02X}/0x{us:02X}")
        print("\n=> A mesa e classificavel via Raw Input. Arquitetura Raw Input + LL hook OK.")
    else:
        print("NENHUM digitizer (usage page 0x0D) presente.")
        print("=> Ou a mesa nao esta conectada, ou o driver a expoe so como mouse HID.")
        print("   Se for o segundo caso, a deteccao tera que sair por VID/PID ou Wintab.")


if __name__ == "__main__":
    main()
