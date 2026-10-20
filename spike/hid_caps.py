"""
Spike 3 -- a mesa expoe pressao como usage HID padrao?

Se sim, da pra ler os 8192 niveis pelo MESMO Raw Input que ja classifica o
dispositivo, usando o parser da propria Microsoft (HidP_*), sem depender do
wintab32.dll de terceiro. Se nao, Wintab vira obrigatorio.

Le RIDI_PREPARSEDDATA de cada digitizer e despeja as capabilities.
Rodar: python spike/hid_caps.py    (so leitura, nao abre o device)
"""
import ctypes as C
from ctypes import wintypes as W

user32 = C.WinDLL("user32", use_last_error=True)
hid = C.WinDLL("hid", use_last_error=True)

RIDI_DEVICENAME = 0x20000007
RIDI_DEVICEINFO = 0x2000000B
RIDI_PREPARSEDDATA = 0x20000005
RIM_TYPEHID = 2
HIDP_INPUT = 0
HIDP_STATUS_SUCCESS = 0x00110000

USAGE = W.USHORT

# Usages que definem uma caneta (HID Digitizers, usage page 0x0D)
DIGI_VALUE = {
    0x30: "Tip Pressure      <<< PRESSAO",
    0x31: "Barrel Pressure",
    0x3D: "X Tilt",
    0x3E: "Y Tilt",
    0x3F: "Azimuth",
    0x40: "Altitude",
    0x41: "Twist",
    0x48: "Width",
    0x49: "Height",
}
DIGI_BUTTON = {
    0x32: "In Range",
    0x42: "Tip Switch",
    0x44: "Barrel Switch",
    0x45: "Eraser",
    0x46: "Tablet Pick",
    0x47: "Confidence",
    0x3C: "Invert",
}
GENERIC_VALUE = {0x30: "X", 0x31: "Y", 0x32: "Z", 0x38: "Wheel"}


class RAWINPUTDEVICELIST(C.Structure):
    _fields_ = [("hDevice", W.HANDLE), ("dwType", W.DWORD)]


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


class HIDP_CAPS(C.Structure):
    _fields_ = [
        ("Usage", USAGE), ("UsagePage", USAGE),
        ("InputReportByteLength", W.USHORT),
        ("OutputReportByteLength", W.USHORT),
        ("FeatureReportByteLength", W.USHORT),
        ("Reserved", W.USHORT * 17),
        ("NumberLinkCollectionNodes", W.USHORT),
        ("NumberInputButtonCaps", W.USHORT),
        ("NumberInputValueCaps", W.USHORT),
        ("NumberInputDataIndices", W.USHORT),
        ("NumberOutputButtonCaps", W.USHORT),
        ("NumberOutputValueCaps", W.USHORT),
        ("NumberOutputDataIndices", W.USHORT),
        ("NumberFeatureButtonCaps", W.USHORT),
        ("NumberFeatureValueCaps", W.USHORT),
        ("NumberFeatureDataIndices", W.USHORT),
    ]


class _RANGE(C.Structure):
    _fields_ = [("UsageMin", USAGE), ("UsageMax", USAGE),
                ("StringMin", W.USHORT), ("StringMax", W.USHORT),
                ("DesignatorMin", W.USHORT), ("DesignatorMax", W.USHORT),
                ("DataIndexMin", W.USHORT), ("DataIndexMax", W.USHORT)]


class _NOTRANGE(C.Structure):
    _fields_ = [("Usage", USAGE), ("Reserved1", USAGE),
                ("StringIndex", W.USHORT), ("Reserved2", W.USHORT),
                ("DesignatorIndex", W.USHORT), ("Reserved3", W.USHORT),
                ("DataIndex", W.USHORT), ("Reserved4", W.USHORT)]


class _RANGE_UNION(C.Union):
    _fields_ = [("Range", _RANGE), ("NotRange", _NOTRANGE)]


class HIDP_VALUE_CAPS(C.Structure):
    _anonymous_ = ("u",)
    _fields_ = [
        ("UsagePage", USAGE), ("ReportID", C.c_ubyte), ("IsAlias", C.c_ubyte),
        ("BitField", W.USHORT), ("LinkCollection", W.USHORT),
        ("LinkUsage", USAGE), ("LinkUsagePage", USAGE),
        ("IsRange", C.c_ubyte), ("IsStringRange", C.c_ubyte),
        ("IsDesignatorRange", C.c_ubyte), ("IsAbsolute", C.c_ubyte),
        ("HasNull", C.c_ubyte), ("Reserved", C.c_ubyte),
        ("BitSize", W.USHORT), ("ReportCount", W.USHORT),
        ("Reserved2", W.USHORT * 5),
        ("UnitsExp", W.ULONG), ("Units", W.ULONG),
        ("LogicalMin", W.LONG), ("LogicalMax", W.LONG),
        ("PhysicalMin", W.LONG), ("PhysicalMax", W.LONG),
        ("u", _RANGE_UNION),
    ]


class HIDP_BUTTON_CAPS(C.Structure):
    _anonymous_ = ("u",)
    _fields_ = [
        ("UsagePage", USAGE), ("ReportID", C.c_ubyte), ("IsAlias", C.c_ubyte),
        ("BitField", W.USHORT), ("LinkCollection", W.USHORT),
        ("LinkUsage", USAGE), ("LinkUsagePage", USAGE),
        ("IsRange", C.c_ubyte), ("IsStringRange", C.c_ubyte),
        ("IsDesignatorRange", C.c_ubyte), ("IsAbsolute", C.c_ubyte),
        ("Reserved", W.ULONG * 10),
        ("u", _RANGE_UNION),
    ]


user32.GetRawInputDeviceList.restype = W.UINT
user32.GetRawInputDeviceInfoW.restype = W.UINT
user32.GetRawInputDeviceInfoW.argtypes = [W.HANDLE, W.UINT, C.c_void_p,
                                          C.POINTER(W.UINT)]
hid.HidP_GetCaps.argtypes = [C.c_void_p, C.POINTER(HIDP_CAPS)]
hid.HidP_GetValueCaps.argtypes = [C.c_int, C.POINTER(HIDP_VALUE_CAPS),
                                  C.POINTER(W.USHORT), C.c_void_p]
hid.HidP_GetButtonCaps.argtypes = [C.c_int, C.POINTER(HIDP_BUTTON_CAPS),
                                   C.POINTER(W.USHORT), C.c_void_p]

INVALID = C.c_uint(-1).value


def device_list():
    count = W.UINT(0)
    size = C.sizeof(RAWINPUTDEVICELIST)
    user32.GetRawInputDeviceList(None, C.byref(count), size)
    buf = (RAWINPUTDEVICELIST * count.value)()
    n = user32.GetRawInputDeviceList(buf, C.byref(count), size)
    return list(buf[:n]) if n != INVALID else []


def name_of(h):
    size = W.UINT(0)
    user32.GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, None, C.byref(size))
    if not size.value:
        return ""
    b = C.create_unicode_buffer(size.value + 1)
    user32.GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, b, C.byref(size))
    return b.value


def info_of(h):
    i = RID_DEVICE_INFO()
    i.cbSize = C.sizeof(RID_DEVICE_INFO)
    s = W.UINT(C.sizeof(RID_DEVICE_INFO))
    if user32.GetRawInputDeviceInfoW(h, RIDI_DEVICEINFO, C.byref(i), C.byref(s)) == INVALID:
        return None
    return i


def preparsed_of(h):
    size = W.UINT(0)
    user32.GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, None, C.byref(size))
    if not size.value:
        return None
    buf = (C.c_byte * size.value)()
    if user32.GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, buf, C.byref(size)) == INVALID:
        return None
    return buf


def label(page, usage, table):
    if page == 0x0D:
        return DIGI_VALUE.get(usage) or DIGI_BUTTON.get(usage) or f"usage 0x{usage:02X}"
    if page == 0x01:
        return GENERIC_VALUE.get(usage, f"usage 0x{usage:02X}")
    return f"page 0x{page:02X} usage 0x{usage:02X}"


def main():
    found_pressure = []
    print("Procurando digitizers e lendo capabilities HID...\n")

    for d in device_list():
        if d.dwType != RIM_TYPEHID:
            continue
        info = info_of(d.hDevice)
        if not info or info.hid.usUsagePage != 0x0D:
            continue

        nm = name_of(d.hDevice)
        print("=" * 72)
        print(f"VID_{info.hid.dwVendorId:04X}&PID_{info.hid.dwProductId:04X}  "
              f"usage {info.hid.usUsagePage:02X}/{info.hid.usUsage:02X}")
        print(f"  {nm}")

        pp = preparsed_of(d.hDevice)
        if pp is None:
            print("  ! sem preparsed data\n")
            continue

        caps = HIDP_CAPS()
        if hid.HidP_GetCaps(pp, C.byref(caps)) != HIDP_STATUS_SUCCESS:
            print("  ! HidP_GetCaps falhou\n")
            continue

        print(f"  input report = {caps.InputReportByteLength} bytes, "
              f"{caps.NumberInputValueCaps} values, {caps.NumberInputButtonCaps} buttons")

        # ---- valores (X, Y, pressao, tilt...)
        n = W.USHORT(caps.NumberInputValueCaps)
        if n.value:
            arr = (HIDP_VALUE_CAPS * n.value)()
            if hid.HidP_GetValueCaps(HIDP_INPUT, arr, C.byref(n), pp) == HIDP_STATUS_SUCCESS:
                print("\n  VALORES:")
                for v in arr[:n.value]:
                    usage = v.Range.UsageMin if v.IsRange else v.NotRange.Usage
                    nome = label(v.UsagePage, usage, DIGI_VALUE)
                    span = v.LogicalMax - v.LogicalMin + 1
                    print(f"    rpt{v.ReportID:<3} {nome:<28} "
                          f"logico {v.LogicalMin}..{v.LogicalMax} "
                          f"({span} niveis, {v.BitSize} bits)")
                    if v.UsagePage == 0x0D and usage == 0x30:
                        found_pressure.append(
                            (info.hid.dwVendorId, info.hid.dwProductId, span, v.BitSize))

        # ---- botoes (tip switch, eraser, in range...)
        nb = W.USHORT(caps.NumberInputButtonCaps)
        if nb.value:
            arrb = (HIDP_BUTTON_CAPS * nb.value)()
            if hid.HidP_GetButtonCaps(HIDP_INPUT, arrb, C.byref(nb), pp) == HIDP_STATUS_SUCCESS:
                print("\n  BOTOES:")
                for b in arrb[:nb.value]:
                    if b.IsRange:
                        print(f"    rpt{b.ReportID:<3} page 0x{b.UsagePage:02X} "
                              f"usages 0x{b.Range.UsageMin:02X}..0x{b.Range.UsageMax:02X}")
                    else:
                        print(f"    rpt{b.ReportID:<3} "
                              f"{label(b.UsagePage, b.NotRange.Usage, DIGI_BUTTON)}")
        print()

    print("=" * 72)
    if found_pressure:
        print("PRESSAO DISPONIVEL VIA HID PADRAO:")
        for vid, pid, span, bits in found_pressure:
            print(f"  VID_{vid:04X}&PID_{pid:04X} -> {span} niveis ({bits} bits)")
        print("\n=> Da pra ler pressao pelo mesmo Raw Input da classificacao.")
        print("   Wintab passa a ser plano B, nao fundacao.")
    else:
        print("NENHUM Tip Pressure (0x0D/0x30) exposto como usage HID padrao.")
        print("=> A pressao so sai pelo canal vendor-defined (0xFFA0) ou por Wintab.")
        print("   Wintab vira OBRIGATORIO -- o que muda a arquitetura de input.")


if __name__ == "__main__":
    main()
