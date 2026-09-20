using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.Scripting.APIUpdating;

namespace Rvm
{

[MovedFrom(true, "RVM", "RVM.Runtime", "RVMComputeUnits")]
public enum ComputeUnits
{
    CpuOnly = 0,
    CpuAndGpu = 1,
    All = 2,
    CpuAndNeuralEngine = 3
}

internal static class NativePlugin
{
    // Keep these declarations aligned with RVMPlugin's C ABI. In particular, the
    // integer return values encode success, retry, and error states rather than bools.
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
    [DllImport(
        LibraryName,
        EntryPoint = "RVMCreateWithComputeUnits",
        CallingConvention = CallingConvention.Cdecl
    )]
    static extern IntPtr RvmCreateWithComputeUnits(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        ComputeUnits computeUnits,
        StringBuilder errorBuffer,
        int errorCapacity
    );

    [DllImport(
        LibraryName,
        EntryPoint = "RVMDestroy",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern void RvmDestroy(IntPtr handle);

    [DllImport(
        LibraryName,
        EntryPoint = "RVMResetState",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern void RvmResetState(IntPtr handle);

    [DllImport(
        LibraryName,
        EntryPoint = "RVMGetInputWidth",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmGetInputWidth(IntPtr handle);

    [DllImport(
        LibraryName,
        EntryPoint = "RVMGetInputHeight",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmGetInputHeight(IntPtr handle);

    [DllImport(
        LibraryName,
        EntryPoint = "RVMGetAlphaSlotCount",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmGetAlphaSlotCount(IntPtr handle);

    [DllImport(
        LibraryName,
        EntryPoint = "RVMGetAlphaTextureInfo",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmGetAlphaTextureInfo(
        IntPtr handle,
        int slotIndex,
        out int width,
        out int height,
        out IntPtr nativeTexture
    );

    [DllImport(
        LibraryName,
        EntryPoint = "RVMCanSubmit",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmCanSubmit(IntPtr handle);

    [DllImport(
        LibraryName,
        EntryPoint = "RVMSubmitBGRA",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmSubmitBgra(
        IntPtr handle,
        IntPtr bgra,
        int width,
        int height,
        int rowBytes
    );

    [DllImport(
        LibraryName,
        EntryPoint = "RVMTryGetOutputInfoEx",
        CallingConvention = CallingConvention.Cdecl
    )]
    static extern int RvmTryGetOutputInfoEx(
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

    [DllImport(
        LibraryName,
        EntryPoint = "RVMMarkAlphaSlotGPUInFlight",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmMarkAlphaSlotGpuInFlight(
        IntPtr handle,
        int slotIndex,
        ulong generation
    );

    [DllImport(
        LibraryName,
        EntryPoint = "RVMReleaseAlphaSlot",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int RvmReleaseAlphaSlot(
        IntPtr handle,
        int slotIndex,
        ulong generation
    );

    internal static CreationResult Create(string modelPath, ComputeUnits computeUnits)
    {
        var error = new StringBuilder(ErrorCapacity);
        var handle = RvmCreateWithComputeUnits(
            modelPath,
            computeUnits,
            error,
            error.Capacity
        );
        return new CreationResult(handle, error.ToString());
    }

    internal static void EnsureLoaded() => RvmGetAlphaSlotCount(IntPtr.Zero);

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
        var result = RvmTryGetOutputInfoEx(
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
    // Non-macOS stubs keep callers platform-agnostic and let the same scenes and
    // assemblies import on unsupported build targets.
    internal static void EnsureLoaded() { }

    internal static CreationResult Create(string modelPath, ComputeUnits computeUnits) =>
        new(IntPtr.Zero, "RVM inference is supported only on macOS.");

    internal static void RvmDestroy(IntPtr handle) { }
    internal static void RvmResetState(IntPtr handle) { }
    internal static int RvmGetInputWidth(IntPtr handle) => 0;
    internal static int RvmGetInputHeight(IntPtr handle) => 0;
    internal static int RvmGetAlphaSlotCount(IntPtr handle) => 0;

    internal static int RvmGetAlphaTextureInfo(
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

    internal static int RvmCanSubmit(IntPtr handle) => 0;

    internal static int RvmSubmitBgra(
        IntPtr handle,
        IntPtr bgra,
        int width,
        int height,
        int rowBytes
    ) => -1;

    internal static int RvmMarkAlphaSlotGpuInFlight(
        IntPtr handle,
        int slotIndex,
        ulong generation
    ) => -1;

    internal static int RvmReleaseAlphaSlot(
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

} // namespace Rvm
