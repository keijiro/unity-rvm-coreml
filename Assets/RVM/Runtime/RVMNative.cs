using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RVM
{

public enum RVMComputeUnits
{
    CpuOnly = 0,
    CpuAndGpu = 1,
    All = 2,
    CpuAndNeuralEngine = 3
}

internal static class RVMNative
{
    const string LibraryName = "RVMPlugin";
    const int ErrorCapacity = 1024;

    internal readonly struct CreationResult
    {
        public IntPtr Handle { get; }
        public string Error { get; }

        public CreationResult(IntPtr handle, string error)
        {
            Handle = handle;
            Error = error;
        }
    }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr RVMCreateWithComputeUnits(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        RVMComputeUnits computeUnits,
        StringBuilder errorBuffer,
        int errorCapacity
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RVMDestroy(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMGetInputWidth(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMGetInputHeight(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMGetAlphaSlotCount(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMGetAlphaTextureInfo(
        IntPtr handle,
        int slotIndex,
        out int width,
        out int height,
        out IntPtr nativeTexture
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMCanSubmit(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMSubmitBGRA(
        IntPtr handle,
        IntPtr bgra,
        int width,
        int height,
        int rowBytes
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMTryGetOutputInfoEx(
        IntPtr handle,
        out int width,
        out int height,
        out double inferenceMilliseconds,
        out int slotIndex,
        out ulong generation,
        out ulong frameNumber,
        StringBuilder errorBuffer,
        int errorCapacity
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMMarkAlphaSlotGPUInFlight(
        IntPtr handle,
        int slotIndex,
        ulong generation
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RVMReleaseAlphaSlot(
        IntPtr handle,
        int slotIndex,
        ulong generation
    );

    internal static CreationResult Create(string modelPath, RVMComputeUnits computeUnits)
    {
        var error = new StringBuilder(ErrorCapacity);
        var handle = RVMCreateWithComputeUnits(
            modelPath,
            computeUnits,
            error,
            error.Capacity
        );
        return new CreationResult(handle, error.ToString());
    }

    internal static void EnsureLoaded() => RVMGetAlphaSlotCount(IntPtr.Zero);

    internal static int TryGetOutputInfo(
        IntPtr handle,
        out int width,
        out int height,
        out double inferenceMilliseconds,
        out int slotIndex,
        out ulong generation,
        out ulong frameNumber,
        out string message
    )
    {
        var error = new StringBuilder(ErrorCapacity);
        var result = RVMTryGetOutputInfoEx(
            handle,
            out width,
            out height,
            out inferenceMilliseconds,
            out slotIndex,
            out generation,
            out frameNumber,
            error,
            error.Capacity
        );
        message = error.ToString();
        return result;
    }
#else
    internal static void EnsureLoaded() { }

    internal static CreationResult Create(string modelPath, RVMComputeUnits computeUnits) =>
        new(IntPtr.Zero, "RVM inference is supported only on macOS.");

    internal static void RVMDestroy(IntPtr handle) { }
    internal static int RVMGetInputWidth(IntPtr handle) => 0;
    internal static int RVMGetInputHeight(IntPtr handle) => 0;
    internal static int RVMGetAlphaSlotCount(IntPtr handle) => 0;

    internal static int RVMGetAlphaTextureInfo(
        IntPtr handle,
        int slotIndex,
        out int width,
        out int height,
        out IntPtr nativeTexture
    )
    {
        width = 0;
        height = 0;
        nativeTexture = IntPtr.Zero;
        return -1;
    }

    internal static int RVMCanSubmit(IntPtr handle) => 0;

    internal static int RVMSubmitBGRA(
        IntPtr handle,
        IntPtr bgra,
        int width,
        int height,
        int rowBytes
    ) => -1;

    internal static int RVMMarkAlphaSlotGPUInFlight(
        IntPtr handle,
        int slotIndex,
        ulong generation
    ) => -1;

    internal static int RVMReleaseAlphaSlot(
        IntPtr handle,
        int slotIndex,
        ulong generation
    ) => -1;

    internal static int TryGetOutputInfo(
        IntPtr handle,
        out int width,
        out int height,
        out double inferenceMilliseconds,
        out int slotIndex,
        out ulong generation,
        out ulong frameNumber,
        out string message
    )
    {
        width = 0;
        height = 0;
        inferenceMilliseconds = 0;
        slotIndex = -1;
        generation = 0;
        frameNumber = 0;
        message = "RVM inference is supported only on macOS.";
        return -1;
    }
#endif
}

} // namespace RVM
