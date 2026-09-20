using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Rvm.CoreML
{

public sealed partial class MatteGenerator
{
    const int ModelInputWidth = 1280;
    const int ModelInputHeight = 720;
    const int AlphaSlotCount = 3;
    const string ModelStreamingAssetsPath =
        "Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";
#if UNITY_EDITOR
    const string ModelPackagePath =
        "Runtime/Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";
#endif

    internal RenderTexture ModelInput => _inputTexture;
    internal ulong OutputVersion { get; private set; }

    // Serialized resources

    [SerializeField, HideInInspector] Shader _preprocessShader = null;
    [SerializeField, HideInInspector] Shader _outputShader = null;

    // Native alpha textures remain leased until the command stream reaches the
    // corresponding fence. Releasing one earlier lets native inference overwrite
    // memory that Unity may still be sampling.
    readonly struct AlphaLease
    {
        public int SlotIndex { get; }
        public ulong Generation { get; }
        public GraphicsFence Fence { get; }

        public AlphaLease(int slotIndex, ulong generation, GraphicsFence fence)
        {
            SlotIndex = slotIndex;
            Generation = generation;
            Fence = fence;
        }
    }

    IntPtr _plugin;
    Task<NativePlugin.CreationResult> _creationTask;
    RenderTexture _inputTexture;
    RenderTexture _ownedOutput;
    RenderTexture _submittedOutput;
    Texture2D[] _alphaTextures;
    readonly List<AlphaLease> _alphaLeases = new();
    Material _preprocessMaterial;
    Material _outputMaterial;
    bool _readbackPending;
    bool _frameInFlight;
    bool _resetRequested;
    bool _disposed;
    int _stateGeneration;
    ulong _expectedFrameNumber = 1;

    // Frame submission

    bool ProcessFrame(Texture input)
    {
        if (!IsReady || _disposed || _resetRequested || _readbackPending ||
            _frameInFlight || input == null || input.width <= 0 || input.height <= 0 ||
            NativePlugin.RvmCanSubmit(_plugin) == 0 || !EnsureOutput())
            return false;

        _preprocessMaterial.SetVector(
            "_SourceSize",
            new Vector4(input.width, input.height, 0, 0)
        );
        _preprocessMaterial.SetFloat(
            "_TargetAspect",
            (float)ModelInputWidth / ModelInputHeight
        );
        Graphics.Blit(input, _inputTexture, _preprocessMaterial);

        // This single normalized texture is also the RGB source for the result. It
        // stays locked until the matching alpha frame has queued its composite; a
        // later copy is then ordered after that draw by Unity's graphics stream.
        _submittedOutput = Output;
        _frameInFlight = true;
        _readbackPending = true;
        var generation = _stateGeneration;
        AsyncGPUReadback.Request(
            _inputTexture,
            0,
            TextureFormat.BGRA32,
            request => OnReadback(request, generation)
        );
        return true;
    }

    // MonoBehaviour implementation

    void OnEnable()
    {
        _disposed = false;
        _readbackPending = false;
        _frameInFlight = false;
        _resetRequested = false;
        _stateGeneration++;
        _expectedFrameNumber = 1;
        OutputVersion = 0;
        InferenceTime = 0;
        LastError = null;
        EnsureOutput();

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
        {
            SetError("Unsupported graphics API. RVM requires Metal.");
            return;
        }

        CreateMaterials();
        if (_preprocessMaterial == null || _outputMaterial == null) return;

        var modelPath = GetModelPath();
        var computeUnits = ComputeUnits;
        NativePlugin.EnsureLoaded();
        _creationTask = Task.Run(() => NativePlugin.Create(modelPath, computeUnits));
    }

    void Update()
    {
        ReleaseCompletedAlphaSlots();
        CompleteInitialization();
        if (_plugin == IntPtr.Zero) return;

        if (_resetRequested)
        {
            CompleteReset();
            return;
        }

        ReceiveOutput();
        if (!_frameInFlight && !_readbackPending && Input != null)
            Process(Input);
    }

    void OnDisable()
    {
        _disposed = true;
        _stateGeneration++;

        // The extra readback is queued after every composite and therefore provides
        // a CPU-visible completion point before native external textures are freed.
        if (_alphaLeases.Count > 0 && _inputTexture != null)
            AsyncGPUReadback.Request(_inputTexture);
        if (_readbackPending || _alphaLeases.Count > 0)
            AsyncGPUReadback.WaitAllRequests();
        _readbackPending = false;

        if (_plugin != IntPtr.Zero) ReleaseAllAlphaSlots();
        ReleaseTexture(ref _inputTexture);
        ReleaseOwnedOutput();
        DestroyAlphaTextures();
        Destroy(_preprocessMaterial);
        Destroy(_outputMaterial);
        _preprocessMaterial = null;
        _outputMaterial = null;
        _submittedOutput = null;

        if (_plugin != IntPtr.Zero)
        {
            NativePlugin.RvmDestroy(_plugin);
            _plugin = IntPtr.Zero;
        }

        if (_creationTask == null) return;
        var task = _creationTask;
        _creationTask = null;
        task.ContinueWith(completed =>
        {
            if (completed.Status != TaskStatus.RanToCompletion) return;
            if (completed.Result.Handle != IntPtr.Zero)
                NativePlugin.RvmDestroy(completed.Result.Handle);
        });
    }

    // Initialization

    static string GetModelPath()
    {
#if UNITY_EDITOR
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
            typeof(MatteGenerator).Assembly
        );
        return Path.Combine(package.resolvedPath, ModelPackagePath);
#else
        return Path.Combine(Application.streamingAssetsPath, ModelStreamingAssetsPath);
#endif
    }

    void CreateMaterials()
    {
        if (_preprocessShader != null)
            _preprocessMaterial = new Material(_preprocessShader);
        if (_outputShader != null)
            _outputMaterial = new Material(_outputShader);
        if (_preprocessMaterial == null || _outputMaterial == null)
            SetError("Required RVM shaders could not be loaded.");
    }

    void CompleteInitialization()
    {
        if (_creationTask == null || !_creationTask.IsCompleted) return;

        if (_creationTask.IsFaulted)
        {
            SetError($"Model load failed: {_creationTask.Exception?.GetBaseException().Message}");
            _creationTask = null;
            return;
        }

        var result = _creationTask.Result;
        _creationTask = null;
        if (_disposed)
        {
            if (result.Handle != IntPtr.Zero) NativePlugin.RvmDestroy(result.Handle);
            return;
        }
        if (result.Handle == IntPtr.Zero)
        {
            SetError($"Model load failed: {result.Error}");
            return;
        }

        _plugin = result.Handle;
        var width = NativePlugin.RvmGetInputWidth(_plugin);
        var height = NativePlugin.RvmGetInputHeight(_plugin);
        if (width != ModelInputWidth || height != ModelInputHeight)
        {
            SetError($"Unexpected model input size: {width} × {height}.");
            NativePlugin.RvmDestroy(_plugin);
            _plugin = IntPtr.Zero;
            return;
        }

        _inputTexture = new RenderTexture(
            width,
            height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB
        )
        {
            name = "RVM Model Input",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _inputTexture.Create();
        if (!CreateAlphaTextures())
        {
            NativePlugin.RvmDestroy(_plugin);
            _plugin = IntPtr.Zero;
        }
    }

    bool EnsureOutput()
    {
        if (_ownedOutput != null && Output != _ownedOutput)
            ReleaseOwnedOutput();
        if (Output != null)
            return Output.width > 0 && Output.height > 0;

        _ownedOutput = new RenderTexture(
            ModelInputWidth,
            ModelInputHeight,
            0,
            GraphicsFormat.R8G8B8A8_UNorm
        )
        {
            name = "RVM Output",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _ownedOutput.Create();
        Output = _ownedOutput;
        return true;
    }

    bool CreateAlphaTextures()
    {
        var count = NativePlugin.RvmGetAlphaSlotCount(_plugin);
        if (count != AlphaSlotCount)
        {
            SetError($"Unexpected native alpha slot count: {count}.");
            return false;
        }

        _alphaTextures = new Texture2D[count];
        for (var index = 0; index < count; index++)
        {
            var result = NativePlugin.RvmGetAlphaTextureInfo(
                _plugin,
                index,
                out var width,
                out var height,
                out var pointer
            );
            if (result != 1 || width != ModelInputWidth ||
                height != ModelInputHeight || pointer == IntPtr.Zero)
            {
                DestroyAlphaTextures();
                SetError($"Could not obtain GPU alpha slot {index}.");
                return false;
            }

            _alphaTextures[index] = Texture2D.CreateExternalTexture(
                width,
                height,
                TextureFormat.R8,
                false,
                true,
                pointer
            );
            _alphaTextures[index].name = $"RVM Alpha Slot {index}";
            _alphaTextures[index].filterMode = FilterMode.Bilinear;
            _alphaTextures[index].wrapMode = TextureWrapMode.Clamp;
        }
        return true;
    }

    // Frame processing

    unsafe void OnReadback(AsyncGPUReadbackRequest request, int generation)
    {
        _readbackPending = false;
        if (_disposed || generation != _stateGeneration || _plugin == IntPtr.Zero) return;
        if (request.hasError)
        {
            _frameInFlight = false;
            SetError("GPU readback failed.");
            return;
        }

        var source = request.GetData<byte>();
        var pointer = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(source);
        var result = NativePlugin.RvmSubmitBgra(
            _plugin,
            pointer,
            ModelInputWidth,
            ModelInputHeight,
            ModelInputWidth * 4
        );
        if (result == 1)
        {
            LastError = null;
            return;
        }

        if (result < 0)
        {
            // Allocation failures are published through the native result mailbox.
            // Keep polling so that error state is consumed instead of permanently
            // blocking RvmCanSubmit with an unread failure.
            SetError("Could not submit the input frame.");
            return;
        }

        _frameInFlight = false;
        SetError("Frame submission was rejected because inference is busy.");
    }

    void ReceiveOutput()
    {
        if (!_frameInFlight) return;
        var result = NativePlugin.TryGetOutputInfo(
            _plugin,
            out var width,
            out var height,
            out var milliseconds,
            out var slotIndex,
            out var generation,
            out var frameNumber,
            out var error
        );
        if (result < 0)
        {
            _frameInFlight = false;
            _expectedFrameNumber++;
            SetError($"Inference failed: {error}");
            return;
        }
        if (result == 0) return;
        if (width != ModelInputWidth || height != ModelInputHeight ||
            slotIndex < 0 || slotIndex >= _alphaTextures.Length ||
            frameNumber != _expectedFrameNumber)
        {
            _frameInFlight = false;
            SetError("The native plugin returned mismatched alpha output metadata.");
            if (slotIndex >= 0)
                NativePlugin.RvmReleaseAlphaSlot(_plugin, slotIndex, generation);
            return;
        }

        var output = _submittedOutput;
        if (output == null || output.width <= 0 || output.height <= 0)
        {
            _frameInFlight = false;
            SetError("The output RenderTexture is no longer valid.");
            NativePlugin.RvmReleaseAlphaSlot(_plugin, slotIndex, generation);
            return;
        }

        // Both samples use one UV so scaling to an arbitrary output size cannot
        // introduce a color/matte alignment difference. The format decides whether
        // the color survives or every available color channel receives the matte.
        _outputMaterial.SetTexture("_ColorTex", _inputTexture);
        _outputMaterial.SetFloat(
            "_OutputHasAlpha",
            GraphicsFormatUtility.HasAlphaChannel(output.graphicsFormat) ? 1 : 0
        );
        Graphics.Blit(_alphaTextures[slotIndex], output, _outputMaterial);
        // RenderTexture.updateCount doesn't change for render-target writes. This
        // counter advances when the matching composite enters Unity's graphics
        // stream, giving the demo a CPU-visible completion signal without making
        // synchronization part of the public package API.
        OutputVersion++;
        var fence = Graphics.CreateGraphicsFence(
            GraphicsFenceType.CPUSynchronisation,
            SynchronisationStageFlags.AllGPUOperations
        );
        _alphaLeases.Add(new AlphaLease(slotIndex, generation, fence));
        if (NativePlugin.RvmMarkAlphaSlotGpuInFlight(
                _plugin,
                slotIndex,
                generation
            ) != 1)
            SetError("Could not transfer the GPU alpha slot.");
        else
            LastError = null;

        InferenceTime = milliseconds;
        _expectedFrameNumber++;
        _submittedOutput = null;
        _frameInFlight = false;
    }

    void CompleteReset()
    {
        if (_readbackPending) return;
        if (_alphaLeases.Count > 0) return;

        ReleaseAllAlphaSlots();
        NativePlugin.RvmResetState(_plugin);
        _submittedOutput = null;
        _frameInFlight = false;
        _expectedFrameNumber = 1;
        OutputVersion = 0;
        InferenceTime = 0;
        LastError = null;
        _resetRequested = false;
    }

    // Resource release

    void ReleaseCompletedAlphaSlots()
    {
        if (_plugin == IntPtr.Zero) return;
        for (var index = _alphaLeases.Count - 1; index >= 0; index--)
        {
            var lease = _alphaLeases[index];
            if (!lease.Fence.passed) continue;
            NativePlugin.RvmReleaseAlphaSlot(
                _plugin,
                lease.SlotIndex,
                lease.Generation
            );
            _alphaLeases.RemoveAt(index);
        }
    }

    void ReleaseAllAlphaSlots()
    {
        foreach (var lease in _alphaLeases)
        {
            NativePlugin.RvmReleaseAlphaSlot(
                _plugin,
                lease.SlotIndex,
                lease.Generation
            );
        }
        _alphaLeases.Clear();
    }

    void DestroyAlphaTextures()
    {
        if (_alphaTextures == null) return;
        foreach (var texture in _alphaTextures) Destroy(texture);
        _alphaTextures = null;
    }

    void ReleaseOwnedOutput()
    {
        if (_ownedOutput == null) return;
        if (Output == _ownedOutput) Output = null;
        _ownedOutput.Release();
        Destroy(_ownedOutput);
        _ownedOutput = null;
    }

    static void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Destroy(texture);
        texture = null;
    }

    void SetError(string message) => LastError = message;
}

} // namespace Rvm.CoreML
