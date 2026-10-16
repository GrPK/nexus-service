#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Creates the D3D11 device ANGLE renders on, when the driver's own worker thread is to be switched off.</summary>
internal static unsafe class D3dDeviceFactory
{
    /// <summary>D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS.</summary>
    public const uint PreventInternalThreading = 0x8;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint levels, uint nLevels, uint sdk, out nint device, out uint level, out nint context);

    private static nint VTable(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static uint ComRelease(nint obj) => ((delegate* unmanaged<nint, uint>)VTable(obj, 2))(obj);

    public static void Release(nint device)
    {
        if (device != 0)
        {
            ComRelease(device);
        }
    }

    /// <summary>A hardware device on the adapter with the given LUID ("high:low"), created with <paramref name="flags"/>; 0 when anything fails.</summary>
    public static nint Create(string? luid, uint flags)
    {
        nint factory = 0;
        nint adapter = 0;
        try
        {
            if (!AngleAdapterSelector.TryParseLuid(luid, out var high, out var low))
            {
                return 0;
            }
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out factory) < 0)
            {
                return 0;
            }
            var desc = stackalloc byte[320];
            for (uint i = 0; ; i++)
            {
                nint a;
                if (((delegate* unmanaged<nint, uint, nint*, int>)VTable(factory, 12))(factory, i, &a) < 0)
                {
                    break;
                }
                // DXGI_ADAPTER_DESC1 puts the LUID after the description, ids, and three sizes.
                if (((delegate* unmanaged<nint, byte*, int>)VTable(a, 10))(a, desc) >= 0
                    && *(uint*)(desc + 296) == low && *(int*)(desc + 300) == high)
                {
                    adapter = a;
                    break;
                }
                ComRelease(a);
            }
            if (adapter == 0)
            {
                return 0;
            }
            if (D3D11CreateDevice(adapter, 0, 0, flags, 0, 0, 7, out var device, out _, out var context) < 0)
            {
                return 0;
            }
            ComRelease(context);
            return device;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            if (adapter != 0)
            {
                ComRelease(adapter);
            }
            if (factory != 0)
            {
                ComRelease(factory);
            }
        }
    }
}
#endif
