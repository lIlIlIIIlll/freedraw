using System.Runtime.InteropServices;
using static Freedraw.Input.Interop;

namespace Freedraw.Input;

/// <summary>
/// Fonte de input do Freedraw. Roda uma janela oculta com Raw Input em
/// RIDEV_INPUTSINK numa thread propria, e emite amostras de caneta ja
/// normalizadas alem de sinalizar quando o mouse assume.
///
/// Por que Raw Input e nao WM_POINTER/dwExtraInfo: o driver da MT1060 nao usa a
/// stack de Windows Ink -- dwExtraInfo vem 0x00000000 em 100% dos eventos, entao
/// a unica forma de saber a origem e pelo handle do dispositivo. Ver FINDINGS.md.
/// </summary>
public sealed class RawInputPenSource : IDisposable
{
    private const string WindowClassName = "FreedrawRawInputSink";

    public event Action<PenSample>? PenSample;
    public event Action<InputSource>? SourceChanged;

    /// <summary>
    /// Tecla emitida PELA PROPRIA MESA (botao da caneta ou express key), nao pelo
    /// teclado do computador. Distinguido pelo VID/PID do dispositivo de origem.
    ///
    /// Existe porque nesta mesa o botao lateral e uma tecla programavel gravada
    /// no firmware: ele NAO liga o Barrel Switch do report da caneta, manda um
    /// evento de teclado. Ver FINDINGS.md.
    /// </summary>
    public event Action<int>? TabletKeyDown;

    /// <summary>
    /// Botao de mouse emitido PELA MESA. O botao "travado em botao direito" da
    /// caneta sai por aqui -- pela colecao HID de mouse do proprio VID_08F2 --
    /// e nao pelo Barrel Switch do digitizer.
    /// </summary>
    public event Action<TabletButton>? TabletButtonDown;

    /// <summary>VID/PID de todos os dispositivos que sao a mesa, em qualquer
    /// colecao (digitizer, mouse, teclado). Preenchido na inicializacao.</summary>
    private readonly HashSet<(uint vid, uint pid)> _tabletIds = [];
    private readonly Dictionary<IntPtr, (uint vid, uint pid)?> _deviceIds = new();

    /// <summary>Ultima origem de input observada.</summary>
    public InputSource CurrentSource { get; private set; } = InputSource.Unknown;

    /// <summary>Digitizers descobertos, para diagnostico.</summary>
    public IReadOnlyCollection<string> KnownDevices
    {
        // OfType descarta os handles cacheados como null (nao-digitizers).
        get { lock (_gate) return _devices.Values.OfType<PenDevice>().Select(d => d.ToString()).ToList(); }
    }

    private readonly Dictionary<IntPtr, PenDevice?> _devices = new();
    private readonly object _gate = new();
    private readonly int _headerSize = Marshal.SizeOf<RAWINPUTHEADER>();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private WndProc? _wndProc;          // manter vivo: o GC nao ve a referencia nativa
    private byte[] _reportScratch = new byte[64];
    private IntPtr _rawBuffer;
    private int _rawBufferSize;
    private volatile bool _running;
    private Exception? _startupError;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("ja iniciado");

        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => PumpThread(ready))
        {
            IsBackground = true,
            Name = "Freedraw.RawInput",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();

        if (_startupError is not null)
            throw new InvalidOperationException("falha ao iniciar Raw Input", _startupError);
    }

    private void PumpThread(ManualResetEventSlim ready)
    {
        try
        {
            _threadId = GetCurrentThreadId();
            ScanTabletDevices();
            CreateSinkWindow();
            RegisterDevices();
            _running = true;
        }
        catch (Exception ex)
        {
            _startupError = ex;
            ready.Set();
            return;
        }
        ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            DispatchMessageW(ref msg);

        Cleanup();
    }

    private void CreateSinkWindow()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProc = WindowProc;

        var classNamePtr = Marshal.StringToHGlobalUni(WindowClassName);
        try
        {
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = classNamePtr,
            };
            // Classe ja registrada por uma instancia anterior nao e erro fatal.
            RegisterClassW(ref wc);

            // Janela REGULAR oculta, nao message-only: HWND_MESSAGE e conhecido
            // por nao receber WM_INPUT de forma confiavel com RIDEV_INPUTSINK.
            _hwnd = CreateWindowExW(0, WindowClassName, "Freedraw", 0,
                                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new Win32Exception_(Marshal.GetLastWin32Error(), "CreateWindowExW");
        }
        finally { Marshal.FreeHGlobal(classNamePtr); }
    }

    private void RegisterDevices()
    {
        var devices = new[]
        {
            NewDevice(UsagePageGeneric, UsageMouse),
            NewDevice(UsagePageGeneric, UsageKeyboard),
            NewDevice(UsagePageDigitizer, UsageDigitizer),
            NewDevice(UsagePageDigitizer, UsagePen),
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length,
                                     (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new Win32Exception_(Marshal.GetLastWin32Error(), "RegisterRawInputDevices");

        // DEVNOTIFY somado a INPUTSINK, nao no lugar dele: sem a notificacao,
        // uma mesa conectada depois do app sobe sem entrar em _tabletIds e os
        // botoes dela passam a ser tratados como mouse do usuario.
        RAWINPUTDEVICE NewDevice(ushort page, ushort usage) => new()
        {
            usUsagePage = page,
            usUsage = usage,
            dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
            hwndTarget = _hwnd,
        };
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                try { HandleRawInput(lParam); }
                catch { /* um report malformado nao pode derrubar a bomba de mensagens */ }
                break;

            case WM_INPUT_DEVICE_CHANGE:
                // Handles de Raw Input sao RECICLADOS pelo Windows. Um handle
                // cacheado como "nao e digitizer" pode, depois de um replug,
                // apontar para o coletor bom -- e o cache faria a caneta parar
                // de funcionar em silencio, para sempre.
                OnDeviceChanged(wParam.ToInt32());
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnDeviceChanged(int change)
    {
        lock (_gate)
        {
            foreach (var device in _devices.Values) device?.Dispose();
            _devices.Clear();
            _deviceIds.Clear();
        }

        if (change == GIDC_ARRIVAL)
        {
            _tabletIds.Clear();
            ScanTabletDevices();
        }
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)_headerSize) != 0 || size == 0)
            return;

        EnsureRawBuffer((int)size);
        uint written = GetRawInputData(hRawInput, RID_INPUT, _rawBuffer, ref size, (uint)_headerSize);
        if (written == 0 || written == unchecked((uint)-1))
            return;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_rawBuffer);

        if (header.dwType == RIM_TYPEMOUSE)
        {
            // Botao vindo da MESA nao e o mouse do usuario: e o botao da caneta.
            if (IsTabletDevice(header.hDevice))
            {
                // RAWMOUSE: usFlags(2) + padding(2) + usButtonFlags(2)
                ushort flags = (ushort)Marshal.ReadInt16(_rawBuffer, _headerSize + 4);
                if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0)
                    TabletButtonDown?.Invoke(TabletButton.Right);
                else if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
                    TabletButtonDown?.Invoke(TabletButton.Middle);
                else if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
                    TabletButtonDown?.Invoke(TabletButton.Left);
                return;
            }
            // hDevice 0 = mouse INJETADO, sem dispositivo fisico por tras. Medido:
            // com a caneta em uso chega um desses para CADA report HID dela, 1:1.
            // Contar isso como "o usuario pegou o mouse" derrubava a captura a cada
            // report -- o cursor do sistema piscava de volta e o toque da caneta
            // atravessava o overlay. Ver FINDINGS.md.
            if (header.hDevice == IntPtr.Zero)
                return;
            SetSource(InputSource.Mouse);
            return;
        }

        if (header.dwType == RIM_TYPEKEYBOARD)
        {
            // Tecla vinda da MESA e o botao programavel da caneta / express key.
            if (IsTabletDevice(header.hDevice))
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(_rawBuffer + _headerSize);
                if ((kb.Flags & RI_KEY_BREAK) == 0)
                    TabletKeyDown?.Invoke(kb.VKey);
            }
            return;
        }

        if (header.dwType != RIM_TYPEHID)
            return;

        var device = ResolveDevice(header.hDevice);
        if (device is null)
            return;

        SetSource(InputSource.Pen);
        EmitReports(device, written);
    }

    private void EmitReports(PenDevice device, uint totalBytes)
    {
        // RAWHID { DWORD dwSizeHid; DWORD dwCount; BYTE bRawData[1]; }
        int sizeHid = Marshal.ReadInt32(_rawBuffer, _headerSize);
        int count = Marshal.ReadInt32(_rawBuffer, _headerSize + 4);
        int dataOffset = _headerSize + 8;

        if (sizeHid <= 0 || count <= 0)
            return;

        if (_reportScratch.Length < sizeHid)
            _reportScratch = new byte[sizeHid];

        for (int i = 0; i < count; i++)
        {
            int start = dataOffset + i * sizeHid;
            if (start + sizeHid > totalBytes)
                break;

            Marshal.Copy(_rawBuffer + start, _reportScratch, 0, sizeHid);
            var sample = device.Parse(_reportScratch, (uint)sizeHid);
            if (sample.HasValue)
                PenSample?.Invoke(sample.Value);
        }
    }

    /// <summary>
    /// Varre os dispositivos e guarda o VID/PID de todo digitizer presente.
    /// Feito na inicializacao para que um botao apertado ANTES do primeiro
    /// movimento da caneta ja seja reconhecido como sendo da mesa.
    /// </summary>
    private void ScanTabletDevices()
    {
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) == unchecked((uint)-1) || count == 0)
            return;

        var list = new RAWINPUTDEVICELIST[count];
        uint got = GetRawInputDeviceList(list, ref count, size);
        if (got == unchecked((uint)-1)) return;

        for (int i = 0; i < got; i++)
        {
            if (list[i].dwType != RIM_TYPEHID) continue;
            var ids = ReadDeviceIds(list[i].hDevice);
            if (ids is null) continue;

            var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
            uint infoSize = info.cbSize;
            var buffer = Marshal.AllocHGlobal((int)infoSize);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (GetRawInputDeviceInfoW(list[i].hDevice, RIDI_DEVICEINFO, buffer, ref infoSize)
                    == unchecked((uint)-1)) continue;
                info = Marshal.PtrToStructure<RID_DEVICE_INFO>(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }

            if (info.hid.usUsagePage == UsagePageDigitizer)
                _tabletIds.Add(ids.Value);
        }
    }

    private bool IsTabletDevice(IntPtr hDevice)
    {
        if (_tabletIds.Count == 0) return false;
        if (!_deviceIds.TryGetValue(hDevice, out var ids))
        {
            ids = ReadDeviceIds(hDevice);
            _deviceIds[hDevice] = ids;
        }
        return ids is not null && _tabletIds.Contains(ids.Value);
    }

    /// <summary>Extrai VID/PID do caminho do dispositivo, que tem a forma
    /// <c>\\?\HID#VID_08F2&amp;PID_6811&amp;MI_02...</c>.</summary>
    private static (uint vid, uint pid)? ReadDeviceIds(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref size) == unchecked((uint)-1))
                return null;
            string name = Marshal.PtrToStringUni(buffer) ?? string.Empty;

            uint? vid = Extract(name, "VID_");
            uint? pid = Extract(name, "PID_");
            return vid is null || pid is null ? null : (vid.Value, pid.Value);
        }
        finally { Marshal.FreeHGlobal(buffer); }

        static uint? Extract(string text, string prefix)
        {
            int at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || at + prefix.Length + 4 > text.Length) return null;
            return uint.TryParse(text.AsSpan(at + prefix.Length, 4),
                                 System.Globalization.NumberStyles.HexNumber, null, out uint value)
                ? value
                : null;
        }
    }

    private PenDevice? ResolveDevice(IntPtr hDevice)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(hDevice, out var cached))
                return cached;

            // null tambem e cacheado: evita re-sondar dispositivos irrelevantes
            // a cada report, e eles chegam as centenas por segundo.
            var device = PenDevice.TryCreate(hDevice);
            _devices[hDevice] = device;
            return device;
        }
    }

    private void SetSource(InputSource source)
    {
        if (CurrentSource == source) return;
        CurrentSource = source;
        SourceChanged?.Invoke(source);
    }

    private void EnsureRawBuffer(int size)
    {
        if (_rawBufferSize >= size) return;
        if (_rawBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_rawBuffer);
        _rawBuffer = Marshal.AllocHGlobal(size);
        _rawBufferSize = size;
    }

    private void Cleanup()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_rawBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_rawBuffer);
            _rawBuffer = IntPtr.Zero;
            _rawBufferSize = 0;
        }
        lock (_gate)
        {
            foreach (var device in _devices.Values) device?.Dispose();
            _devices.Clear();
        }
    }

    public void Dispose()
    {
        if (_thread is null) return;

        if (_running)
        {
            _running = false;
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _wndProc = null;
    }

    /// <summary>Win32Exception sem arrastar a dependencia de System.ComponentModel.</summary>
    private sealed class Win32Exception_(int code, string api)
        : Exception($"{api} falhou com erro {code}");
}
