using System.Runtime.InteropServices;
using static RonicMirror.Native;

namespace RonicMirror;

/// <summary>
/// Cliente do Parsec Virtual Display Driver (IddCx, assinado), que cria monitores virtuais REAIS no Windows.
/// Porte para C# de https://github.com/nomi-san/parsec-vdd (core/parsec-vdd.h).
/// O driver remove todos os monitores se não receber um "ping" (UPDATE) a cada ~100 ms.
/// </summary>
internal sealed class ParsecVdd : IDisposable
{
    public const string AdapterName = "Parsec Virtual Display Adapter";
    public const string DriverDownloadUrl = "https://builds.parsec.app/vdd/parsec-vdd-0.41.0.0.exe"; // 0.41 = versão estável (Windows 10 19H2+)

    private static Guid AdapterGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");

    private const uint IOCTL_ADD = 0x0022e004;
    private const uint IOCTL_REMOVE = 0x0022a008;
    private const uint IOCTL_UPDATE = 0x0022a00c;
    private const uint IOCTL_VERSION = 0x0022e010;

    private readonly IntPtr _handle;
    private readonly object _ioLock = new();
    private Thread? _keepAlive;
    private volatile bool _running;

    private ParsecVdd(IntPtr handle) => _handle = handle;

    /// <summary>Abre o driver. Retorna null se ele não estiver instalado.</summary>
    public static ParsecVdd? TryOpen()
    {
        var handle = OpenDeviceHandle();
        return handle == INVALID_HANDLE_VALUE ? null : new ParsecVdd(handle);
    }

    public int Version => (int)IoControl(IOCTL_VERSION, null);

    /// <summary>Adiciona um monitor virtual e devolve seu índice (ou -1).</summary>
    public int AddDisplay()
    {
        int index = (int)IoControl(IOCTL_ADD, null);
        Update();
        StartKeepAlive();
        return index;
    }

    public void RemoveDisplay(int index)
    {
        // Índice em 16 bits big-endian.
        ushort be = (ushort)(((index & 0xFF) << 8) | ((index >> 8) & 0xFF));
        IoControl(IOCTL_REMOVE, BitConverter.GetBytes(be));
        Update();
    }

    public void Update() => IoControl(IOCTL_UPDATE, null);

    private void StartKeepAlive()
    {
        if (_running) return;
        _running = true;
        _keepAlive = new Thread(() =>
        {
            while (_running)
            {
                Update();
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "ParsecVdd keep-alive", Priority = ThreadPriority.AboveNormal };
        _keepAlive.Start();
    }

    public void Dispose()
    {
        _running = false;
        _keepAlive?.Join(500);
        CloseHandle(_handle);
    }

    /// <summary>DeviceIoControl com OVERLAPPED e timeout de 5 s, igual à implementação de referência.</summary>
    private uint IoControl(uint code, byte[]? data)
    {
        lock (_ioLock)
        {
            const int inSize = 32;
            int ovSize = Marshal.SizeOf<NativeOverlapped>();
            IntPtr inBuf = Marshal.AllocHGlobal(inSize);
            IntPtr outBuf = Marshal.AllocHGlobal(sizeof(uint));
            IntPtr ov = Marshal.AllocHGlobal(ovSize);
            IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
            try
            {
                var zero = new byte[inSize];
                if (data != null) Array.Copy(data, zero, Math.Min(data.Length, inSize));
                Marshal.Copy(zero, 0, inBuf, inSize);
                Marshal.WriteInt32(outBuf, 0);
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = evt }, ov, false);

                DeviceIoControl(_handle, code, inBuf, inSize, outBuf, sizeof(uint), IntPtr.Zero, ov);
                if (!GetOverlappedResultEx(_handle, ov, out _, 5000, false))
                {
                    CancelIoEx(_handle, ov);
                    GetOverlappedResultEx(_handle, ov, out _, 1000, false);
                    return uint.MaxValue;
                }
                return (uint)Marshal.ReadInt32(outBuf);
            }
            finally
            {
                CloseHandle(evt);
                Marshal.FreeHGlobal(ov);
                Marshal.FreeHGlobal(outBuf);
                Marshal.FreeHGlobal(inBuf);
            }
        }
    }

    private static IntPtr OpenDeviceHandle()
    {
        IntPtr devInfo = SetupDiGetClassDevsW(ref AdapterGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == INVALID_HANDLE_VALUE) return INVALID_HANDLE_VALUE;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref AdapterGuid, i, ref iface); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(devInfo, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 em 64 bits, 6 em 32 bits.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref iface, detail, required, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4)!;
                    IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED | FILE_FLAG_WRITE_THROUGH,
                        IntPtr.Zero);
                    if (h != IntPtr.Zero && h != INVALID_HANDLE_VALUE) return h;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            return INVALID_HANDLE_VALUE;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }
}
