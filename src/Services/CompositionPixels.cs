using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;

namespace WinDock.Services;

/// <summary>
/// Põe pixels prontos (BGRA, pré-multiplicados) numa superfície da composição do Windows, para o
/// papel de parede com vida mostrar uma imagem atrás dos ícones.
///
/// <para>A composição não lê imagem de arquivo sozinha fora do XAML: a superfície de desenho
/// (<see cref="CompositionDrawingSurface"/>) precisa de um dispositivo de vídeo por trás. Aqui é um
/// Direct3D 11 próprio, e os pixels sobem por <c>UpdateSubresource</c> no pedaço da textura que o
/// <c>BeginDraw</c> devolve. Sem Win2D nem pacote nenhum: as interfaces de interop são COM, e
/// chamadas pela tabela de métodos, como o <c>CreateDesktopWindowTarget</c> do
/// <see cref="AnimatedWallpaper"/>.</para>
///
/// <para>Usado só na thread do papel de parede. A placa pode ser reiniciada (driver atualizado,
/// volta da suspensão); aí <see cref="Lost"/> fica verdadeiro e quem usa refaz tudo.</para>
/// </summary>
internal sealed class CompositionPixels : IDisposable
{
    private nint _device;
    private nint _context;

    public CompositionGraphicsDevice Graphics { get; }

    public CompositionPixels(Compositor compositor)
    {
        var hr = D3D11CreateDevice(0, D3D_DRIVER_TYPE_HARDWARE, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0,
                                   D3D11_SDK_VERSION, out _device, out _, out _context);
        if (hr < 0)
        {
            // sem placa utilizável (sessão remota, driver básico): o processador desenha
            Log.Write($"papel de parede com vida: sem Direct3D na placa (0x{hr:X8}); usando o WARP");
            hr = D3D11CreateDevice(0, D3D_DRIVER_TYPE_WARP, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0,
                                   D3D11_SDK_VERSION, out _device, out _, out _context);
        }
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            // a composição usa o dispositivo também da thread dela, ao montar as superfícies
            var iid = IID_ID3D10Multithread;
            if (Marshal.QueryInterface(_device, ref iid, out var mt) == 0)
            {
                try { Slot<SetMultithreadProtectedFn>(mt, 5)(mt, 1); }
                finally { Marshal.Release(mt); }
            }
            Graphics = CreateGraphicsDevice(compositor);
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>A placa foi reiniciada ou trocada: as superfícies criadas aqui ficaram vazias.</summary>
    public bool Lost => _device != 0 && (_removedReason ??= Slot<GetDeviceRemovedReasonFn>(_device, 39))(_device) != 0;

    private GetDeviceRemovedReasonFn? _removedReason;   // perguntado a cada segundo: o delegado fica guardado

    /// <summary>Uma superfície nova com <paramref name="bgra"/> dentro, de <paramref name="width"/> × <paramref name="height"/>.</summary>
    public CompositionDrawingSurface Surface(byte[] bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("menos pixels do que o tamanho diz", nameof(bgra));
        var s = Graphics.CreateDrawingSurface(new Windows.Foundation.Size(width, height),
                                              DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        try
        {
            Write(s, bgra, width, height);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private CompositionGraphicsDevice CreateGraphicsDevice(Compositor compositor)
    {
        var comp = ((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr;
        var iid = IID_ICompositorInterop;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(comp, ref iid, out var interop));
        try
        {
            Marshal.ThrowExceptionForHR(Slot<CreateGraphicsDeviceFn>(interop, 5)(interop, _device, out var gd));
            try { return CompositionGraphicsDevice.FromAbi(gd); }
            finally { Marshal.Release(gd); }
        }
        finally { Marshal.Release(interop); }
    }

    private void Write(CompositionDrawingSurface surface, byte[] px, int w, int h)
    {
        var abi = ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr;
        var iid = IID_ICompositionDrawingSurfaceInterop;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, ref iid, out var interop));
        try
        {
            var dxgiIid = IID_IDXGISurface;
            Marshal.ThrowExceptionForHR(Slot<BeginDrawFn>(interop, 3)(interop, 0, ref dxgiIid, out var dxgi, out var canto));
            try
            {
                var resIid = IID_ID3D11Resource;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(dxgi, ref resIid, out var recurso));
                try
                {
                    // a superfície pode morar num pedaço de uma textura maior: o canto diz onde
                    var caixa = new D3D11_BOX
                    {
                        left = (uint)canto.X, top = (uint)canto.Y, front = 0,
                        right = (uint)(canto.X + w), bottom = (uint)(canto.Y + h), back = 1,
                    };
                    var fixo = GCHandle.Alloc(px, GCHandleType.Pinned);
                    try { Slot<UpdateSubresourceFn>(_context, 48)(_context, recurso, 0, ref caixa, fixo.AddrOfPinnedObject(), (uint)(w * 4), 0); }
                    finally { fixo.Free(); }
                }
                finally { Marshal.Release(recurso); }
            }
            finally
            {
                Marshal.Release(dxgi);
                Marshal.ThrowExceptionForHR(Slot<EndDrawFn>(interop, 4)(interop));
            }
            Slot<FlushFn>(_context, 111)(_context);
        }
        finally { Marshal.Release(interop); }
    }

    public void Dispose()
    {
        try { Graphics?.Dispose(); } catch { /* a placa pode já ter ido embora */ }
        Release();
    }

    private void Release()
    {
        if (_context != 0) { Marshal.Release(_context); _context = 0; }
        if (_device != 0) { Marshal.Release(_device); _device = 0; }
    }

    /// <summary>O método de número <paramref name="slot"/> da tabela de uma interface COM.</summary>
    private static T Slot<T>(nint obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    // ── Direct3D e interop da composição ──────────────────────

    private const int D3D_DRIVER_TYPE_HARDWARE = 1, D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7;

    private static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IID_IDXGISurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
    private static readonly Guid IID_ID3D11Resource = new("DC8E63F3-D12B-4952-B47B-5E45026A862D");
    private static readonly Guid IID_ID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX { public uint left, top, front, right, bottom, back; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt { public int X, Y; }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
                                                nint featureLevels, uint featureLevelCount, uint sdkVersion,
                                                out nint device, out int featureLevel, out nint context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateGraphicsDeviceFn(nint self, nint renderingDevice, out nint graphicsDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int BeginDrawFn(nint self, nint updateRect, ref Guid iid, out nint updateObject, out PointInt offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndDrawFn(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UpdateSubresourceFn(nint self, nint resource, uint subresource, ref D3D11_BOX box,
                                              nint data, uint rowPitch, uint depthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FlushFn(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceRemovedReasonFn(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMultithreadProtectedFn(nint self, int protect);
}
