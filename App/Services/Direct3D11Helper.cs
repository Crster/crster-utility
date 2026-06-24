using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace App.Services
{
    public enum D3D_DRIVER_TYPE : uint
    {
        UNKNOWN = 0,
        HARDWARE = 1,
        REFERENCE = 2,
        NULL = 3,
        SOFTWARE = 4,
        WARP = 5
    }

    [Flags]
    public enum D3D11_CREATE_DEVICE_FLAG : uint
    {
        None = 0x0,
        Debug = 0x2,
        BgraSupport = 0x20
    }

    public enum D3D_FEATURE_LEVEL : uint
    {
        LEVEL_9_1 = 0x9100,
        LEVEL_9_2 = 0x9200,
        LEVEL_9_3 = 0x9300,
        LEVEL_10_0 = 0xa000,
        LEVEL_10_1 = 0xa100,
        LEVEL_11_0 = 0xb000,
        LEVEL_11_1 = 0xb100
    }

    [ComImport]
    [Guid("00000000-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUnknown
    {
        [PreserveSig] int QueryInterface(in Guid riid, out IntPtr ppvObject);
        [PreserveSig] uint AddRef();
        [PreserveSig] uint Release();
    }

    public static class Direct3D11Helper
    {
        private const uint D3D11_SDK_VERSION = 7;
        private static readonly Guid IDXGIDeviceIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice",
            CallingConvention = CallingConvention.StdCall,
            ExactSpelling = true, PreserveSig = true)]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            D3D_DRIVER_TYPE driverType,
            IntPtr software,
            D3D11_CREATE_DEVICE_FLAG flags,
            IntPtr pFeatureLevels,
            uint featureLevels,
            uint sdkVersion,
            out IntPtr ppDevice,
            out D3D_FEATURE_LEVEL pFeatureLevel,
            out IntPtr ppImmediateContext);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
            CallingConvention = CallingConvention.StdCall,
            ExactSpelling = true, PreserveSig = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static IDirect3DDevice CreateDevice()
        {
            // Build feature levels array as raw bytes and pin it.
            var featureLevels = new D3D_FEATURE_LEVEL[]
            {
                D3D_FEATURE_LEVEL.LEVEL_11_1,
                D3D_FEATURE_LEVEL.LEVEL_11_0,
                D3D_FEATURE_LEVEL.LEVEL_10_1
            };

            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3D_DRIVER_TYPE.HARDWARE,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_FLAG.BgraSupport,
                IntPtr.Zero,
                0,
                D3D11_SDK_VERSION,
                out IntPtr d3dDevice,
                out _,
                out IntPtr d3dContext);

            if (hr < 0)
                throw new COMException("Failed creating D3D11 Device", hr);

            // Release the immediate context; we do not need it.
            Marshal.Release(d3dContext);

            // The D3D11 device IS an IDXGIDevice (DXGI device is the base of the D3D11 device vtable).
            // The D3D11 device's QueryInterface for IID_IDXGIDevice should return the SAME pointer.
            int qiHr = Marshal.QueryInterface(d3dDevice, in IDXGIDeviceIid, out IntPtr dxgiDevice);

            if (qiHr < 0)
            {
                Marshal.Release(d3dDevice);
                throw new COMException($"Failed to query IDXGIDevice from ID3D11Device: 0x{qiHr:X8}", qiHr);
            }

            // Release the D3D11 device ref; the DXGI device holds its own ref.
            Marshal.Release(d3dDevice);

            try
            {
                int wrHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr winrtDevice);
                if (wrHr < 0)
                {
                    Marshal.Release(dxgiDevice);
                    throw new COMException($"Failed WinRT DXGI Device Mapping: 0x{wrHr:X8}", wrHr);
                }

                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevice);
                }
                finally
                {
                    Marshal.Release(winrtDevice);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
    }
}
