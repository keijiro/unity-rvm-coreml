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
    Image _compositeImage;
    Label _statusLabel;
    Toggle _matteTriggeredSyncToggle;

    // MonoBehaviour implementation

    void OnEnable()
    {
        _uiVersion = -1;
        _generator = GetComponent<MatteGenerator>();

        InitializePresentation();
        InitializeSources();
        InitializeOutputDisplay();
        _panelRenderer = GetComponent<PanelRenderer>();
        _panelRenderer.RegisterUIReloadCallback(OnUIReload);
        SelectSource(_selectedSourceIndex);
    }

    void Update()
    {
        if (TryGetSourceFrame(out var texture))
            EnqueuePresentationFrame(texture);

        UpdatePresentation();
        UpdateOutputDisplay();
        RefreshStatus();
    }

    void OnDisable()
    {
        _sourceGeneration++;
        StopAllCoroutines();
        _switchCoroutine = null;
        StopCurrentSource();
        ReleasePresentation();
        ReleaseOutputDisplay();
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
        _compositeImage = root.Q<Image>("compositeImage");
        _statusLabel = root.Q<Label>("statusLabel");
        _sourceDropdown = root.Q<DropdownField>("sourceDropdown");
        _matteTriggeredSyncToggle = root.Q<Toggle>("matteTriggeredSyncToggle");

        _cameraImage.scaleMode = ScaleMode.ScaleToFit;
        _alphaImage.scaleMode = ScaleMode.ScaleToFit;
        _compositeImage.scaleMode = ScaleMode.ScaleToFit;
        _cameraImage.image = _inputDisplayTexture;
        _alphaImage.image = _alphaDisplayTexture;
        _compositeImage.image = _compositeTexture;
        BindSourceDropdown();
        BindMatteTriggeredSyncToggle();
        UpdateInputImage();
        RefreshStatus();
    }

    void UnbindUI()
    {
        if (_sourceDropdown != null)
            _sourceDropdown.UnregisterValueChangedCallback(OnSourceDropdownChanged);
        if (_matteTriggeredSyncToggle != null)
            _matteTriggeredSyncToggle.UnregisterValueChangedCallback(
                OnMatteTriggeredSyncChanged
            );
        _sourceDropdown = null;
        _matteTriggeredSyncToggle = null;
        _cameraImage = null;
        _alphaImage = null;
        _compositeImage = null;
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
