using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace RVM
{

[RequireComponent(typeof(PanelRenderer), typeof(VideoPlayer))]
public sealed partial class RVMDemoController : MonoBehaviour
{
    [field:SerializeField]
    public RVMComputeUnits ComputeUnits { get; set; } = RVMComputeUnits.All;

    [SerializeField, HideInInspector] Shader _preprocessShader = null;
    [SerializeField, HideInInspector] Shader _visualizeShader = null;

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
    Task<RVMNative.CreationResult> _creationTask;
    RenderTexture _inputTexture;
    RenderTexture _alphaDisplayTexture;
    Texture2D[] _alphaTextures;
    readonly List<AlphaLease> _alphaLeases = new();
    Material _preprocessMaterial;
    Material _visualizeMaterial;
    bool _readbackPending;
    bool _disposed;
    int _inputWidth;
    int _inputHeight;
    int _uiVersion = -1;
    string _statusMessage;

    PanelRenderer _panelRenderer;
    Image _cameraImage;
    Image _alphaImage;
    Label _statusLabel;

    void OnEnable()
    {
        _disposed = false;
        _uiVersion = -1;
        InitializeSources();
        _panelRenderer = GetComponent<PanelRenderer>();
        _panelRenderer.RegisterUIReloadCallback(OnUIReload);

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
        {
            SetStatus("Unsupported graphics API · RVM requires Metal.");
            return;
        }

        CreateMaterials();
        if (_preprocessMaterial == null || _visualizeMaterial == null) return;
        SelectSource(_selectedSourceIndex);

        var modelPath = Path.Combine(
            Application.streamingAssetsPath,
            "Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel"
        );
        var computeUnits = ComputeUnits;
        SetStatus("Loading RVM MobileNetV3 model…");
        RVMNative.EnsureLoaded();
        _creationTask = Task.Run(() => RVMNative.Create(modelPath, computeUnits));
    }

    void Update()
    {
        ReleaseCompletedAlphaSlots();
        CompleteInitialization();
        if (_plugin == IntPtr.Zero) return;

        ReceiveAlphaMatte();
        ScheduleFrame();
    }

    void OnDisable()
    {
        _disposed = true;
        _sourceGeneration++;
        StopAllCoroutines();
        _switchCoroutine = null;
        StopCurrentSource();
        if (_panelRenderer != null)
            _panelRenderer.UnregisterUIReloadCallback(OnUIReload);
        _panelRenderer = null;
        UnbindUI();

        if (_readbackPending)
        {
            AsyncGPUReadback.WaitAllRequests();
            _readbackPending = false;
        }

        if (_plugin != IntPtr.Zero) ReleaseAllAlphaSlots();
        ReleaseTexture(ref _inputTexture);
        ReleaseTexture(ref _alphaDisplayTexture);
        DestroyAlphaTextures();
        Destroy(_preprocessMaterial);
        Destroy(_visualizeMaterial);
        _preprocessMaterial = null;
        _visualizeMaterial = null;

        if (_plugin != IntPtr.Zero)
        {
            RVMNative.RVMDestroy(_plugin);
            _plugin = IntPtr.Zero;
        }

        if (_creationTask == null) return;
        var task = _creationTask;
        _creationTask = null;
        task.ContinueWith(completed =>
        {
            if (completed.Status != TaskStatus.RanToCompletion) return;
            if (completed.Result.Handle != IntPtr.Zero)
                RVMNative.RVMDestroy(completed.Result.Handle);
        });
    }

    void OnUIReload(PanelRenderer renderer, VisualElement root, int version)
    {
        if (root == null || version == _uiVersion) return;
        _uiVersion = version;

        UnbindUI();
        _cameraImage = root.Q<Image>("cameraImage");
        _alphaImage = root.Q<Image>("alphaImage");
        _statusLabel = root.Q<Label>("statusLabel");
        _sourceDropdown = root.Q<DropdownField>("sourceDropdown");

        _cameraImage.scaleMode = ScaleMode.ScaleToFit;
        _alphaImage.scaleMode = ScaleMode.ScaleToFit;
        BindSourceDropdown();
        UpdateInputImage();
        _alphaImage.image = _alphaDisplayTexture;
        SetStatus(_statusMessage);
    }

    void UnbindUI()
    {
        if (_sourceDropdown != null)
            _sourceDropdown.UnregisterValueChangedCallback(OnSourceDropdownChanged);
        _sourceDropdown = null;
        _cameraImage = null;
        _alphaImage = null;
        _statusLabel = null;
    }

    void CreateMaterials()
    {
        if (_preprocessShader != null)
            _preprocessMaterial = new Material(_preprocessShader);
        if (_visualizeShader != null)
            _visualizeMaterial = new Material(_visualizeShader);
        if (_preprocessMaterial == null || _visualizeMaterial == null)
            SetStatus("Required RVM shaders could not be loaded.");
    }

    void CompleteInitialization()
    {
        if (_creationTask == null || !_creationTask.IsCompleted) return;

        if (_creationTask.IsFaulted)
        {
            SetStatus($"Model load failed: {_creationTask.Exception?.GetBaseException().Message}");
            _creationTask = null;
            return;
        }

        var result = _creationTask.Result;
        _creationTask = null;
        if (_disposed)
        {
            if (result.Handle != IntPtr.Zero) RVMNative.RVMDestroy(result.Handle);
            return;
        }
        if (result.Handle == IntPtr.Zero)
        {
            SetStatus($"Model load failed: {result.Error}");
            return;
        }

        _plugin = result.Handle;
        _inputWidth = RVMNative.RVMGetInputWidth(_plugin);
        _inputHeight = RVMNative.RVMGetInputHeight(_plugin);
        if (_inputWidth != 1280 || _inputHeight != 720)
        {
            SetStatus($"Unexpected model input size: {_inputWidth} × {_inputHeight}.");
            RVMNative.RVMDestroy(_plugin);
            _plugin = IntPtr.Zero;
            return;
        }

        _inputTexture = new RenderTexture(
            _inputWidth,
            _inputHeight,
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
        CreateAlphaDisplayTexture();
        if (!CreateAlphaTextures())
        {
            RVMNative.RVMDestroy(_plugin);
            _plugin = IntPtr.Zero;
            return;
        }

        UpdateInputImage();
        if (_alphaImage != null) _alphaImage.image = _alphaDisplayTexture;
        if (!string.IsNullOrEmpty(_sourceError))
            SetStatus(_sourceError);
        else
            SetStatus(
                $"Ready · {_inputWidth} × {_inputHeight} input · " +
                $"{_alphaTextures.Length} alpha slots"
            );
    }

    void ScheduleFrame()
    {
        if (_readbackPending || RVMNative.RVMCanSubmit(_plugin) == 0) return;
        if (_preprocessMaterial == null) return;
        if (!TryGetSourceFrame(
                out var texture,
                out var width,
                out var height,
                out var rotation,
                out var mirrorY
            ))
            return;

        _preprocessMaterial.SetVector(
            "_SourceSize",
            new Vector4(width, height, 0, 0)
        );
        _preprocessMaterial.SetFloat("_Rotation", rotation);
        _preprocessMaterial.SetFloat("_MirrorY", mirrorY ? 1 : 0);
        _preprocessMaterial.SetFloat("_TargetAspect", (float)_inputWidth / _inputHeight);
        Graphics.Blit(texture, _inputTexture, _preprocessMaterial);

        var generation = _sourceGeneration;
        _readbackPending = true;
        AsyncGPUReadback.Request(
            _inputTexture,
            0,
            TextureFormat.BGRA32,
            request => OnReadback(request, generation)
        );
    }

    unsafe void OnReadback(AsyncGPUReadbackRequest request, int generation)
    {
        _readbackPending = false;
        if (_disposed || generation != _sourceGeneration || _plugin == IntPtr.Zero) return;
        if (request.hasError)
        {
            SetStatus("GPU readback failed.");
            return;
        }

        var source = request.GetData<byte>();
        var pointer = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(source);
        var result = RVMNative.RVMSubmitBGRA(
            _plugin,
            pointer,
            _inputWidth,
            _inputHeight,
            _inputWidth * 4
        );
        if (result < 0) SetStatus("Could not submit the input frame.");
        if (result == 0) SetStatus("Frame dropped · inference busy or no alpha slot.");
    }

    void ReceiveAlphaMatte()
    {
        var result = RVMNative.TryGetOutputInfo(
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
            SetStatus($"Inference failed: {error}");
            return;
        }
        if (result == 0) return;
        if (width != _inputWidth || height != _inputHeight ||
            slotIndex < 0 || slotIndex >= _alphaTextures.Length)
        {
            SetStatus("The native plugin returned invalid alpha output metadata.");
            if (slotIndex >= 0)
                RVMNative.RVMReleaseAlphaSlot(_plugin, slotIndex, generation);
            return;
        }

        Graphics.Blit(_alphaTextures[slotIndex], _alphaDisplayTexture, _visualizeMaterial);
        var fence = Graphics.CreateGraphicsFence(
            GraphicsFenceType.CPUSynchronisation,
            SynchronisationStageFlags.AllGPUOperations
        );
        _alphaLeases.Add(new AlphaLease(slotIndex, generation, fence));
        if (RVMNative.RVMMarkAlphaSlotGPUInFlight(
                _plugin,
                slotIndex,
                generation
            ) != 1)
            SetStatus("Could not transfer the GPU alpha slot.");

        if (_alphaImage != null) _alphaImage.image = _alphaDisplayTexture;
        _alphaImage?.MarkDirtyRepaint();
        SetStatus(
            $"{milliseconds:F1} ms · {_inputWidth} × {_inputHeight} input · " +
            $"frame {frameNumber}"
        );
    }

    bool CreateAlphaTextures()
    {
        var count = RVMNative.RVMGetAlphaSlotCount(_plugin);
        if (count != 3)
        {
            SetStatus($"Unexpected native alpha slot count: {count}.");
            return false;
        }

        _alphaTextures = new Texture2D[count];
        for (var index = 0; index < count; index++)
        {
            var result = RVMNative.RVMGetAlphaTextureInfo(
                _plugin,
                index,
                out var width,
                out var height,
                out var pointer
            );
            if (result != 1 || width != 1280 || height != 720 || pointer == IntPtr.Zero)
            {
                DestroyAlphaTextures();
                SetStatus($"Could not obtain GPU alpha slot {index}.");
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

    void CreateAlphaDisplayTexture()
    {
        _alphaDisplayTexture = new RenderTexture(
            _inputWidth,
            _inputHeight,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear
        )
        {
            name = "RVM Alpha Matte Display",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _alphaDisplayTexture.Create();
    }

    void DestroyAlphaTextures()
    {
        if (_alphaTextures == null) return;
        foreach (var texture in _alphaTextures) Destroy(texture);
        _alphaTextures = null;
    }

    void ReleaseCompletedAlphaSlots()
    {
        if (_plugin == IntPtr.Zero) return;
        for (var index = _alphaLeases.Count - 1; index >= 0; index--)
        {
            var lease = _alphaLeases[index];
            if (!lease.Fence.passed) continue;
            RVMNative.RVMReleaseAlphaSlot(
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
            if (!lease.Fence.passed) Graphics.WaitOnAsyncGraphicsFence(lease.Fence);
            RVMNative.RVMReleaseAlphaSlot(
                _plugin,
                lease.SlotIndex,
                lease.Generation
            );
        }
        _alphaLeases.Clear();
    }

    void SetStatus(string message)
    {
        _statusMessage = message;
        if (_statusLabel != null) _statusLabel.text = message;
    }

    static void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Destroy(texture);
        texture = null;
    }
}

} // namespace RVM
