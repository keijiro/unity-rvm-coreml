using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RVM;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace ProjectBootstrap
{

public static class ProjectValidator
{
    const string LibraryName = "RVMPlugin";
    const int ErrorCapacity = 1024;
    const int InputWidth = 1280;
    const int InputHeight = 720;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr RVMCreateWithComputeUnits(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        RVMComputeUnits computeUnits,
        StringBuilder errorBuffer,
        int errorCapacity
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern void RVMDestroy(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMGetInputWidth(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMGetInputHeight(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMGetAlphaSlotCount(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMGetAlphaTextureInfo(
        IntPtr handle,
        int slotIndex,
        out int width,
        out int height,
        out IntPtr nativeTexture
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMCanSubmit(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMSubmitBGRA(
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
    static extern int RVMMarkAlphaSlotGPUInFlight(
        IntPtr handle,
        int slotIndex,
        ulong generation
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern int RVMReleaseAlphaSlot(
        IntPtr handle,
        int slotIndex,
        ulong generation
    );

    public static void Validate()
    {
        try
        {
            ValidateUI();
            ValidateShaders();
            ValidateMetal();
            ValidateNativePlugin();
            Debug.Log("[ProjectValidator] All checks passed.");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    static void ValidateUI()
    {
        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/Main.uxml");
        if (tree == null) throw new InvalidOperationException("Main.uxml could not be loaded.");
        var root = tree.Instantiate();
        foreach (var name in new[] { "cameraImage", "alphaImage", "statusLabel" })
            if (root.Q(name) == null)
                throw new InvalidOperationException($"UI element '{name}' is missing.");
    }

    static void ValidateShaders()
    {
        foreach (var path in new[]
                 {
                     "Assets/RVM/Shaders/Preprocess.shader",
                     "Assets/RVM/Shaders/VisualizeAlpha.shader"
                 })
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
                throw new InvalidOperationException($"{path} could not be loaded.");
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity == ShaderCompilerMessageSeverity.Error)
                    throw new InvalidOperationException($"{path}: {message.message}");
        }
    }

    static void ValidateMetal()
    {
        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
        if (!apis.Contains(GraphicsDeviceType.Metal))
            throw new InvalidOperationException("Metal is not enabled for macOS standalone.");
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
            throw new InvalidOperationException(
                $"The validation Editor is using {SystemInfo.graphicsDeviceType}, not Metal."
            );
    }

    static void ValidateNativePlugin()
    {
        var modelPath = Path.Combine(
            Application.streamingAssetsPath,
            "Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel"
        );
        var error = new StringBuilder(ErrorCapacity);
        var stopwatch = Stopwatch.StartNew();
        var handle = RVMCreateWithComputeUnits(
            modelPath,
            RVMComputeUnits.All,
            error,
            error.Capacity
        );
        stopwatch.Stop();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Native model load failed: {error}");
        try
        {
            var width = RVMGetInputWidth(handle);
            var height = RVMGetInputHeight(handle);
            if (width != InputWidth || height != InputHeight)
                throw new InvalidOperationException($"Unexpected model input: {width} × {height}.");
            ValidateAlphaSlots(handle);
            Debug.Log($"[ProjectValidator] Core ML model loaded in {stopwatch.ElapsedMilliseconds} ms.");
            ValidateInferenceSequence(handle, width, height);
        }
        finally
        {
            RVMDestroy(handle);
        }
    }

    static void ValidateAlphaSlots(IntPtr handle)
    {
        var count = RVMGetAlphaSlotCount(handle);
        if (count != 3)
            throw new InvalidOperationException($"Unexpected alpha slot count: {count}.");
        for (var index = 0; index < count; index++)
        {
            var result = RVMGetAlphaTextureInfo(
                handle,
                index,
                out var width,
                out var height,
                out var texture
            );
            if (result != 1 || width != InputWidth || height != InputHeight ||
                texture == IntPtr.Zero)
                throw new InvalidOperationException($"Alpha slot {index} is invalid.");
        }
    }

    static void ValidateInferenceSequence(IntPtr handle, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var frame = 1; frame <= 2; frame++)
        {
            FillSyntheticFrame(pixels, width, height, frame);
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                if (RVMSubmitBGRA(
                        handle,
                        pin.AddrOfPinnedObject(),
                        width,
                        height,
                        width * 4
                    ) != 1)
                    throw new InvalidOperationException($"Synthetic frame {frame} was rejected.");
                if (RVMCanSubmit(handle) != 0)
                    throw new InvalidOperationException("CanSubmit accepted a concurrent frame.");
                if (RVMSubmitBGRA(
                        handle,
                        pin.AddrOfPinnedObject(),
                        width,
                        height,
                        width * 4
                    ) != 0)
                    throw new InvalidOperationException("Concurrent submission was not rejected.");
            }
            finally
            {
                pin.Free();
            }

            WaitForOutput(
                handle,
                out var outputWidth,
                out var outputHeight,
                out var milliseconds,
                out var slotIndex,
                out var generation,
                out var frameNumber
            );
            if (outputWidth != InputWidth || outputHeight != InputHeight)
                throw new InvalidOperationException(
                    $"Unexpected alpha output: {outputWidth} × {outputHeight}."
                );
            if (frameNumber != (ulong)frame)
                throw new InvalidOperationException($"Unexpected processed frame: {frameNumber}.");
            if (RVMCanSubmit(handle) != 0)
                throw new InvalidOperationException("CanSubmit accepted an unconsumed output.");
            if (RVMReleaseAlphaSlot(handle, slotIndex, generation + 1) != -1)
                throw new InvalidOperationException("A mismatched generation was accepted.");
            if (RVMMarkAlphaSlotGPUInFlight(handle, slotIndex, generation) != 1)
                throw new InvalidOperationException("The alpha slot could not enter GPU flight.");
            if (RVMReleaseAlphaSlot(handle, slotIndex, generation) != 1)
                throw new InvalidOperationException("The alpha slot could not be released.");
            if (RVMReleaseAlphaSlot(handle, slotIndex, generation) != 0)
                throw new InvalidOperationException("A double release was accepted.");
            if (RVMCanSubmit(handle) != 1)
                throw new InvalidOperationException("The released slot was not reusable.");

            Debug.Log(
                $"[ProjectValidator] Frame {frameNumber} produced a {outputWidth} × " +
                $"{outputHeight} R8 alpha slot in {milliseconds:F1} ms."
            );
        }
    }

    static void WaitForOutput(
        IntPtr handle,
        out int width,
        out int height,
        out double milliseconds,
        out int slotIndex,
        out ulong generation,
        out ulong frameNumber
    )
    {
        var timeout = Stopwatch.StartNew();
        var error = new StringBuilder(ErrorCapacity);
        int result;
        do
        {
            result = RVMTryGetOutputInfoEx(
                handle,
                out width,
                out height,
                out milliseconds,
                out slotIndex,
                out generation,
                out frameNumber,
                error,
                error.Capacity
            );
            if (result == 0) Thread.Sleep(10);
        }
        while (result == 0 && timeout.Elapsed.TotalSeconds < 60);

        if (result < 0) throw new InvalidOperationException($"Native inference failed: {error}");
        if (result == 0) throw new TimeoutException("Native inference timed out.");
    }

    static void FillSyntheticFrame(byte[] pixels, int width, int height, int frame)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            pixels[index + 0] = (byte)(64 + frame * 32);
            pixels[index + 1] = (byte)(y * 255 / (height - 1));
            pixels[index + 2] = (byte)((x + frame * 17) * 255 / (width + 34));
            pixels[index + 3] = 255;
        }
    }
}

} // namespace ProjectBootstrap
