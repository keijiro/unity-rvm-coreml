using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace RVM
{

[RequireComponent(typeof(PanelRenderer), typeof(VideoPlayer), typeof(RVMProcessor))]
public sealed partial class RVMDemoController : MonoBehaviour
{
    RVMProcessor _processor;
    int _uiVersion = -1;
    string _statusMessage;

    PanelRenderer _panelRenderer;
    Image _cameraImage;
    Image _alphaImage;
    Label _statusLabel;

    // MonoBehaviour implementation

    void OnEnable()
    {
        _uiVersion = -1;
        _processor = GetComponent<RVMProcessor>();

        InitializeSources();
        _panelRenderer = GetComponent<PanelRenderer>();
        _panelRenderer.RegisterUIReloadCallback(OnUIReload);
        SelectSource(_selectedSourceIndex);
    }

    void Update()
    {
        if (TryGetSourceFrame(
                out var texture,
                out var rotation,
                out var mirrorY
            ))
        {
            _processor.InputRotation = rotation;
            _processor.InputMirrorY = mirrorY;
            _processor.Process(texture);
        }

        UpdateInputImage();
        _alphaImage?.MarkDirtyRepaint();
        RefreshStatus();
    }

    void OnDisable()
    {
        _sourceGeneration++;
        StopAllCoroutines();
        _switchCoroutine = null;
        StopCurrentSource();
        if (_panelRenderer != null)
            _panelRenderer.UnregisterUIReloadCallback(OnUIReload);
        _panelRenderer = null;
        UnbindUI();

        _processor = null;
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
        _alphaImage.image = _processor.Output;
        BindSourceDropdown();
        UpdateInputImage();
        RefreshStatus();
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

    void SetStatus(string message)
    {
        _statusMessage = message;
        RefreshStatus();
    }

    void RefreshStatus()
    {
        if (_statusLabel == null) return;
        if (!string.IsNullOrEmpty(_processor?.LastError))
        {
            _statusLabel.text = _processor.LastError;
            return;
        }

        _statusLabel.text = _processor != null && _processor.InferenceTime > 0 ?
            $"{_processor.InferenceTime:F1} ms · {_statusMessage}" : _statusMessage;
    }
}

} // namespace RVM
