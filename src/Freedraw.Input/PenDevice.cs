using System.Runtime.InteropServices;
using static Freedraw.Input.Interop;

namespace Freedraw.Input;

/// <summary>
/// Um coletor de digitizer, com seus preparsed data e escalas.
///
/// A MT1060 expoe DOIS coletores com resolucoes diferentes (report 9 com pressao
/// 0..16383, report 5 com 0..2047). Em vez de fixar um report, cada dispositivo
/// normaliza pelo proprio LogicalMax -- assim qualquer coletor produz 0..1
/// correto, e o codigo nao quebra em outra mesa.
/// </summary>
internal sealed class PenDevice : IDisposable
{
    public IntPtr Handle { get; }
    public IntPtr PreparsedData { get; private set; }
    public uint VendorId { get; }
    public uint ProductId { get; }
    public string DeviceName { get; }

    public int MinX { get; private set; }
    public int MinY { get; private set; }
    public int MinPressure { get; private set; }
    public int MaxX { get; private set; } = 1;
    public int MaxY { get; private set; } = 1;
    public int MaxPressure { get; private set; } = 1;
    public bool HasPressure { get; private set; }
    public bool HasTilt { get; private set; }

    private readonly ushort[] _usageScratch;

    private PenDevice(IntPtr handle, IntPtr preparsed, RID_DEVICE_INFO_HID hid, string name)
    {
        Handle = handle;
        PreparsedData = preparsed;
        VendorId = hid.dwVendorId;
        ProductId = hid.dwProductId;
        DeviceName = name;

        ReadScales();

        var max = HidP_MaxUsageListLength(HIDP_INPUT, UsagePageDigitizer, PreparsedData);
        _usageScratch = new ushort[Math.Max(1, (int)max)];
    }

    /// <summary>Devolve null se o dispositivo nao for um digitizer utilizavel.</summary>
    public static PenDevice? TryCreate(IntPtr hDevice)
    {
        var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
        uint size = info.cbSize;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(info, buf, false);
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICEINFO, buf, ref size) == unchecked((uint)-1))
                return null;
            info = Marshal.PtrToStructure<RID_DEVICE_INFO>(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }

        if (info.dwType != RIM_TYPEHID || info.hid.usUsagePage != UsagePageDigitizer)
            return null;

        var preparsed = ReadPreparsedData(hDevice);
        if (preparsed == IntPtr.Zero)
            return null;

        var device = new PenDevice(hDevice, preparsed, info.hid, ReadDeviceName(hDevice));
        if (!device.HasPressure)
        {
            // Sem Tip Pressure nao serve para o que este app faz.
            device.Dispose();
            return null;
        }
        return device;
    }

    private static IntPtr ReadPreparsedData(IntPtr hDevice)
    {
        uint size = 0;
        if (GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0)
            return IntPtr.Zero;

        var buf = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, buf, ref size) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(buf);
            return IntPtr.Zero;
        }
        return buf;
    }

    private static string ReadDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return string.Empty;

        var buf = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            return GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size) == unchecked((uint)-1)
                ? string.Empty
                : Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private void ReadScales()
    {
        if (HidP_GetCaps(PreparsedData, out var caps) != HIDP_STATUS_SUCCESS)
            return;

        ushort count = caps.NumberInputValueCaps;
        if (count == 0) return;

        var valueCaps = new HIDP_VALUE_CAPS[count];
        if (HidP_GetValueCaps(HIDP_INPUT, valueCaps, ref count, PreparsedData) != HIDP_STATUS_SUCCESS)
            return;

        for (int i = 0; i < count; i++)
        {
            var v = valueCaps[i];
            // Na variante NotRange da union, UsageMin carrega o Usage.
            ushort usage = v.UsageMin;
            var (min, max) = LogicalRange(v);

            if (v.UsagePage == UsagePageGeneric && usage == UsageX && max > min)
                (MinX, MaxX) = (min, max);
            else if (v.UsagePage == UsagePageGeneric && usage == UsageY && max > min)
                (MinY, MaxY) = (min, max);
            else if (v.UsagePage == UsagePageDigitizer && usage == UsageTipPressure && max > min)
            {
                (MinPressure, MaxPressure) = (min, max);
                HasPressure = true;
            }
            else if (v.UsagePage == UsagePageDigitizer && (usage == UsageXTilt || usage == UsageYTilt))
                HasTilt = true;
        }
    }

    /// <summary>
    /// Faixa logica declarada pelo descritor.
    ///
    /// O parser HID do Windows SIGN-ESTENDE o item Logical Maximum: um descritor
    /// que declara 65535 num item de 2 bytes volta como -1. Sem reinterpretar,
    /// uma mesa assim seria rejeitada por completo ou ficaria com o ponteiro
    /// cravado num canto. Quando o minimo e nao-negativo e o maximo veio
    /// negativo, o valor e relido como sem sinal no BitSize do proprio campo.
    /// </summary>
    private static (int min, int max) LogicalRange(in HIDP_VALUE_CAPS caps)
    {
        int min = caps.LogicalMin;
        int max = caps.LogicalMax;
        if (min >= 0 && max < 0 && caps.BitSize is > 0 and < 32)
            max = (int)(unchecked((uint)max) & ((1u << caps.BitSize) - 1));
        return (min, max);
    }

    /// <summary>Converte um report cru numa amostra normalizada.</summary>
    public PenSample? Parse(byte[] report, uint length)
    {
        var buttons = ReadButtons(report, length);

        int rawX = ReadValue(report, length, UsagePageGeneric, UsageX) ?? 0;
        int rawY = ReadValue(report, length, UsagePageGeneric, UsageY) ?? 0;
        int rawP = ReadValue(report, length, UsagePageDigitizer, UsageTipPressure) ?? 0;
        int tiltX = ReadValue(report, length, UsagePageDigitizer, UsageXTilt) ?? 0;
        int tiltY = ReadValue(report, length, UsagePageDigitizer, UsageYTilt) ?? 0;

        return new PenSample
        {
            X = Normalize(rawX, MinX, MaxX),
            Y = Normalize(rawY, MinY, MaxY),
            Pressure = Normalize(rawP, MinPressure, MaxPressure),
            TiltX = SignExtendTilt(tiltX),
            TiltY = SignExtendTilt(tiltY),
            TipDown = buttons.tip,
            InRange = buttons.inRange,
            BarrelDown = buttons.barrel,
            Eraser = buttons.eraser,
            RawX = rawX,
            RawY = rawY,
            RawPressure = rawP,
            TimestampTicks = Environment.TickCount64,
        };
    }

    /// <summary>
    /// HidP devolve o valor cru sem sinal; tilt e declarado -127..127, entao
    /// valores acima de 127 sao negativos em complemento de dois de 8 bits.
    /// </summary>
    private static int SignExtendTilt(int raw) => raw > 127 ? raw - 256 : raw;

    /// <summary>Nem todo eixo comeca em zero: normalizar pela faixa declarada.</summary>
    private static float Normalize(int raw, int min, int max)
    {
        int span = max - min;
        return span <= 0 ? 0f : Math.Clamp((raw - min) / (float)span, 0f, 1f);
    }

    private int? ReadValue(byte[] report, uint length, ushort page, ushort usage)
    {
        return HidP_GetUsageValue(HIDP_INPUT, page, 0, usage, out uint value,
                                  PreparsedData, report, length) == HIDP_STATUS_SUCCESS
            ? (int)value
            : null;
    }

    private (bool tip, bool inRange, bool barrel, bool eraser) ReadButtons(byte[] report, uint length)
    {
        uint count = (uint)_usageScratch.Length;
        if (HidP_GetUsages(HIDP_INPUT, UsagePageDigitizer, 0, _usageScratch, ref count,
                           PreparsedData, report, length) != HIDP_STATUS_SUCCESS)
            return (false, false, false, false);

        bool tip = false, inRange = false, barrel = false, eraser = false;
        for (int i = 0; i < count; i++)
        {
            switch (_usageScratch[i])
            {
                case UsageTipSwitch: tip = true; break;
                case UsageInRange: inRange = true; break;
                case UsageBarrelSwitch: barrel = true; break;
                case UsageEraser: eraser = true; break;
            }
        }
        return (tip, inRange, barrel, eraser);
    }

    public override string ToString() =>
        $"VID_{VendorId:X4}&PID_{ProductId:X4} X:0..{MaxX} Y:0..{MaxY} " +
        $"P:0..{MaxPressure}{(HasTilt ? " +tilt" : "")}";

    public void Dispose()
    {
        if (PreparsedData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(PreparsedData);
            PreparsedData = IntPtr.Zero;
        }
    }
}
