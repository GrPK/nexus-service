#if WINDOWS
using System;

namespace Nexus.Service.Lighting.Engine.Gpu;

internal static unsafe partial class AngleContext
{
    private const int EglPlatformDeviceExt = 0x313F;
    private const int EglD3D11DeviceAngle = 0x33A1;

    /// <summary>NEXUS_D3D_PREVENT_THREADING=1 asks for the D3D11 device flag that keeps the driver from running its own worker thread. Off unless set.</summary>
    internal static bool PreventDriverThreading { get; } = Environment.GetEnvironmentVariable("NEXUS_D3D_PREVENT_THREADING") == "1";

    internal static string ClientExtensions()
    {
        EnsureLoaded();
        return Str(_eglQueryString(null, EglExtensions));
    }

    /// <summary>An initialized ANGLE display over a caller-created D3D11 device, or null (nothing left open) when the build lacks the extension or any step fails.</summary>
    internal static void* DisplayOnDevice(nint d3d11Device, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        void* create;
        fixed (byte* name = "eglCreateDeviceANGLE\0"u8)
        {
            create = _eglGetProcAddress(name);
        }
        if (create == null || !ClientExtensions().Contains("EGL_ANGLE_device_creation_d3d11", StringComparison.Ordinal))
        {
            return null;
        }
        var eglDevice = ((delegate* unmanaged[Cdecl]<int, void*, nint*, void*>)create)(EglD3D11DeviceAngle, (void*)d3d11Device, null);
        if (eglDevice == null)
        {
            return null;
        }
        var display = _eglGetPlatformDisplayExt(EglPlatformDeviceExt, eglDevice, null);
        if (display == null)
        {
            return null;
        }
        int mj, mn;
        if (_eglInitialize(display, &mj, &mn) == 0)
        {
            return null;
        }
        major = mj;
        minor = mn;
        return display;
    }

    /// <summary>
    /// With the switch on, an initialized display over a device created with the prevent-threading flag on the target adapter.
    /// Null on any failure, with the device released, so the caller creates its display the ordinary way.
    /// </summary>
    private static void* TryPreventThreadingDisplay(AngleTarget target, out nint device, out int major, out int minor)
    {
        device = 0;
        major = 0;
        minor = 0;
        if (!PreventDriverThreading || target.Warp)
        {
            return null;
        }
        try
        {
            device = D3dDeviceFactory.Create(target.Luid, D3dDeviceFactory.PreventInternalThreading);
            if (device == 0)
            {
                GpuContext.Log("[gpu] D3D11 prevent-threading device could not be created; using ANGLE's own device");
                return null;
            }
            var display = DisplayOnDevice(device, out major, out minor);
            if (display == null)
            {
                GpuContext.Log("[gpu] ANGLE refused the prevent-threading device; using its own device");
                D3dDeviceFactory.Release(device);
                device = 0;
                return null;
            }
            GpuContext.Log("[gpu] D3D11 device created with the driver's internal threading off");
            return display;
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] prevent-threading device path failed: {ex.Message}");
            if (device != 0)
            {
                D3dDeviceFactory.Release(device);
                device = 0;
            }
            return null;
        }
    }
}
#endif
