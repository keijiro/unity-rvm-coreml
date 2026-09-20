using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RVM;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.Video;
using Debug = UnityEngine.Debug;

namespace ProjectBootstrap
{

public static class ProjectValidator
{
    const string LibraryName = "RVMPlugin";
    const string MainScenePath = "Assets/Main.unity";
    const string MainUIPath = "Assets/UI/Main.uxml";
    const string PanelSettingsPath = "Assets/UI/DefaultSettings.asset";
    const string OutputTexturePath = "Assets/RVM/Runtime/RVMMatte.renderTexture";
    const string PreprocessShaderPath = "Assets/RVM/Shaders/Preprocess.shader";
    const string OutputShaderPath = "Assets/RVM/Shaders/VisualizeAlpha.shader";
    const string PluginPath = "Assets/Plugins/macOS/RVMPlugin.bundle";
    const string ModelRelativePath =
        "Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";
    const string ModelSha256 =
        "68efe6e7a23d5337fb4f935f77e83b0ec3cc823803083953eb18f4cc0549d794";
    const int ErrorCapacity = 1024;
    const int InputWidth = 1280;
    const int InputHeight = 720;
    const int AlphaSlotCount = 3;

    // These declarations intentionally duplicate the runtime ABI. Validation must
    // exercise the plugin directly so wrapper changes cannot hide an ABI regression.

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
    static extern void RVMResetState(IntPtr handle);

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
            ValidateDemoScene();
            ValidatePlatform();
            ValidateNativeAssets();
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
        var tree = LoadAsset<VisualTreeAsset>(MainUIPath);
        var root = tree.Instantiate();
        RequireElement<DropdownField>(root, "sourceDropdown");
        RequireElement<Image>(root, "cameraImage");
        RequireElement<Image>(root, "alphaImage");
        RequireElement<Label>(root, "statusLabel");
    }

    static void ValidateShaders()
    {
        foreach (var path in new[]
                 {
                     PreprocessShaderPath,
                     OutputShaderPath
                 })
        {
            var shader = LoadAsset<Shader>(path);
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity == ShaderCompilerMessageSeverity.Error)
                    throw new InvalidOperationException($"{path}: {message.message}");
        }
    }

    static void ValidateDemoScene()
    {
        if (!EditorBuildSettings.scenes.Any(scene =>
                scene.enabled && scene.path == MainScenePath))
            throw new InvalidOperationException(
                $"{MainScenePath} is not enabled in the build settings."
            );

        var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        var controllers = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<RVMDemoController>(true))
            .ToArray();
        if (controllers.Length != 1)
            throw new InvalidOperationException(
                $"{MainScenePath} must contain exactly one RVM demo controller."
            );

        var controller = controllers[0];
        var panelRenderer = controller.GetComponent<PanelRenderer>();
        var videoPlayer = controller.GetComponent<VideoPlayer>();
        var processor = controller.GetComponent<RVMProcessor>();
        if (!controller.isActiveAndEnabled || panelRenderer == null || videoPlayer == null ||
            processor == null || !processor.enabled)
            throw new InvalidOperationException(
                "The RVM demo controller and its required components are not active and complete."
            );

        var panel = new SerializedObject(panelRenderer);
        ValidateAssetReference(panel, "sourceAsset", MainUIPath);
        ValidateAssetReference(panel, "m_PanelSettings", PanelSettingsPath);

        var processorData = new SerializedObject(processor);
        ValidateAssetReference(processorData, "_preprocessShader", PreprocessShaderPath);
        ValidateAssetReference(processorData, "_outputShader", OutputShaderPath);

        var output = processor.Output;
        if (output == null || AssetDatabase.GetAssetPath(output) != OutputTexturePath ||
            output.width != InputWidth || output.height != InputHeight)
            throw new InvalidOperationException(
                "The demo processor has no valid 1280 × 720 output RenderTexture."
            );

        // An alpha-capable output contains source RGB and stores the matte only in A.
        // The demo pane needs the no-alpha mode, which displays the matte as grayscale.
        if (GraphicsFormatUtility.HasAlphaChannel(output.graphicsFormat))
            throw new InvalidOperationException(
                "The demo output RenderTexture must use a format without alpha."
            );

        if (UnityEngine.Object.FindAnyObjectByType<Camera>() == null)
            throw new InvalidOperationException($"{MainScenePath} has no active camera.");
    }

    static void ValidatePlatform()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
            throw new InvalidOperationException("The active build target is not macOS standalone.");

        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
        if (apis.Length != 1 || apis[0] != GraphicsDeviceType.Metal)
            throw new InvalidOperationException("Metal must be the only macOS graphics API.");
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
            throw new InvalidOperationException(
                $"The validation Editor is using {SystemInfo.graphicsDeviceType}, not Metal."
            );
        if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset)
            throw new InvalidOperationException("The active render pipeline is not URP.");
        if (!Version.TryParse(PlayerSettings.macOS.targetOSVersion, out var targetVersion) ||
            targetVersion < new Version(13, 0))
            throw new InvalidOperationException("The macOS deployment target must be 13.0 or later.");
        if (string.IsNullOrWhiteSpace(PlayerSettings.macOS.cameraUsageDescription))
            throw new InvalidOperationException("The macOS camera usage description is empty.");
    }

    static void ValidateNativeAssets()
    {
        var importer = AssetImporter.GetAtPath(PluginPath) as PluginImporter;
        if (importer == null || importer.GetCompatibleWithAnyPlatform() ||
            !importer.GetCompatibleWithEditor() ||
            !importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX))
            throw new InvalidOperationException(
                $"{PluginPath} is not configured as a macOS Editor/player plugin."
            );

        var modelPath = Path.Combine(Application.streamingAssetsPath, ModelRelativePath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The RVM model is missing.", modelPath);

        using var stream = File.OpenRead(modelPath);
        using var sha256 = SHA256.Create();
        var hash = string.Concat(sha256.ComputeHash(stream).Select(value => value.ToString("x2")));
        if (hash != ModelSha256)
            throw new InvalidOperationException($"The RVM model SHA-256 is {hash}.");
    }

    static T LoadAsset<T>(string path) where T : UnityEngine.Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null)
            throw new InvalidOperationException($"{path} could not be loaded as {typeof(T).Name}.");
        return asset;
    }

    static void RequireElement<T>(VisualElement root, string name) where T : VisualElement
    {
        var element = root.Q(name);
        if (element == null)
            throw new InvalidOperationException($"UI element '{name}' is missing.");
        if (element is not T)
            throw new InvalidOperationException(
                $"UI element '{name}' is not a {typeof(T).Name}."
            );
    }

    static void ValidateAssetReference(
        SerializedObject serializedObject,
        string propertyName,
        string expectedPath
    )
    {
        var property = serializedObject.FindProperty(propertyName);
        var actualPath = property == null ? null :
            AssetDatabase.GetAssetPath(property.objectReferenceValue);
        if (actualPath != expectedPath)
        {
            var type = serializedObject.targetObject.GetType().Name;
            throw new InvalidOperationException(
                $"{type}.{propertyName} must reference {expectedPath}."
            );
        }
    }

    static void ValidateNativePlugin()
    {
        var modelPath = Path.Combine(Application.streamingAssetsPath, ModelRelativePath);
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
            ValidateReset(handle, width, height);
        }
        finally
        {
            RVMDestroy(handle);
        }
    }

    static void ValidateAlphaSlots(IntPtr handle)
    {
        var count = RVMGetAlphaSlotCount(handle);
        if (count != AlphaSlotCount)
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

    static void ValidateReset(IntPtr handle, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        FillSyntheticFrame(pixels, width, height, 3);
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
                throw new InvalidOperationException("The pre-reset frame was rejected.");

            // Reset must wait for this in-flight prediction, discard its unpublished
            // mailbox result, and make the leased inference slot reusable.
            RVMResetState(handle);
            var error = new StringBuilder(ErrorCapacity);
            var result = RVMTryGetOutputInfoEx(
                handle,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                error,
                error.Capacity
            );
            if (result != 0)
                throw new InvalidOperationException("Reset left an old output available.");
            if (RVMCanSubmit(handle) != 1)
                throw new InvalidOperationException("Reset did not restore submission capacity.");

            FillSyntheticFrame(pixels, width, height, 4);
            if (RVMSubmitBGRA(
                    handle,
                    pin.AddrOfPinnedObject(),
                    width,
                    height,
                    width * 4
                ) != 1)
                throw new InvalidOperationException("The post-reset frame was rejected.");
        }
        finally
        {
            pin.Free();
        }

        WaitForOutput(
            handle,
            out _,
            out _,
            out _,
            out var slotIndex,
            out var generation,
            out var frameNumber
        );
        if (frameNumber != 1)
            throw new InvalidOperationException("Reset did not restart frame numbering.");
        if (RVMMarkAlphaSlotGPUInFlight(handle, slotIndex, generation) != 1 ||
            RVMReleaseAlphaSlot(handle, slotIndex, generation) != 1)
            throw new InvalidOperationException("Post-reset alpha slot ownership failed.");

        RVMResetState(handle);
        if (RVMCanSubmit(handle) != 1)
            throw new InvalidOperationException("The final reset broke submission capacity.");
        Debug.Log("[ProjectValidator] Recurrent reset and slot reuse passed.");
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
