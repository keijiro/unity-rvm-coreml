using Rvm;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace RVM
{

[RequireComponent(typeof(PanelRenderer), typeof(VideoPlayer), typeof(MatteGenerator))]
public sealed partial class RVMDemoController : MonoBehaviour
{
    MatteGenerator _generator;
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
        _generator = GetComponent<MatteGenerator>();

        InitializeSources();
        _panelRenderer = GetComponent<PanelRenderer>();
        _panelRenderer.RegisterUIReloadCallback(OnUIReload);
        SelectSource(_selectedSourceIndex);
    }

    void Update()
    {
        if (TryGetSourceFrame(out var texture, out var mirrorY))
        {
            _generator.InputMirrorY = mirrorY;
            _generator.Process(texture);
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

        _generator = null;
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
        _alphaImage.image = _generator.Output;
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
        if (!string.IsNullOrEmpty(_generator?.LastError))
        {
            _statusLabel.text = _generator.LastError;
            return;
        }

        _statusLabel.text = _generator != null && _generator.InferenceTime > 0 ?
            $"{_generator.InferenceTime:F1} ms · {_statusMessage}" : _statusMessage;
    }
}

} // namespace RVM
