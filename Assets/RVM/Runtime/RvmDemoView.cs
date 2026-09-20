using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Rvm
{

[RequireComponent(typeof(PanelRenderer))]
public sealed class RvmDemoView : MonoBehaviour
{
    readonly List<string> _sourceChoices = new();
    PanelRenderer _panelRenderer;
    Image _cameraImage;
    Image _alphaImage;
    Image _compositeImage;
    Label _statusLabel;
    DropdownField _sourceDropdown;
    Toggle _matteTriggeredSyncToggle;
    Texture _inputTexture;
    Texture _alphaTexture;
    Texture _compositeTexture;
    int _uiVersion = -1;
    int _selectedSourceIndex = -1;
    bool _matteTriggeredSync;
    string _statusMessage;
    string _errorMessage;
    double _inferenceTime;

    internal event Action<int> SourceSelectionChanged;
    internal event Action<bool> MatteTriggeredSyncChanged;

    internal void Initialize()
    {
        _uiVersion = -1;
        _panelRenderer = GetComponent<PanelRenderer>();
        _panelRenderer.RegisterUIReloadCallback(OnUIReload);
    }

    internal void Release()
    {
        if (_panelRenderer != null)
            _panelRenderer.UnregisterUIReloadCallback(OnUIReload);
        _panelRenderer = null;
        UnbindUI();
    }

    internal void SetSources(IReadOnlyList<string> choices, int selectedIndex)
    {
        _sourceChoices.Clear();
        if (choices != null)
        {
            for (var index = 0; index < choices.Count; index++)
                _sourceChoices.Add(choices[index]);
        }
        _selectedSourceIndex = selectedIndex;
        BindSourceDropdown();
    }

    internal void SetSelectedSource(int index)
    {
        _selectedSourceIndex = index;
        if (_sourceDropdown == null) return;
        if (index >= 0 && index < _sourceChoices.Count)
            _sourceDropdown.SetValueWithoutNotify(_sourceChoices[index]);
        else
            _sourceDropdown.SetValueWithoutNotify(string.Empty);
    }

    internal void SetMatteTriggeredSync(bool enabled)
    {
        _matteTriggeredSync = enabled;
        _matteTriggeredSyncToggle?.SetValueWithoutNotify(enabled);
    }

    internal void SetImages(
        Texture input,
        Texture alpha,
        Texture composite,
        bool repaint
    )
    {
        _inputTexture = input;
        _alphaTexture = alpha;
        _compositeTexture = composite;
        ApplyImage(_cameraImage, input, repaint);
        ApplyImage(_alphaImage, alpha, repaint);
        ApplyImage(_compositeImage, composite, repaint);
    }

    internal void SetStatus(string status, string error, double inferenceTime)
    {
        _statusMessage = status;
        _errorMessage = error;
        _inferenceTime = inferenceTime;
        RefreshStatus();
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
        _cameraImage.uv = new Rect(0, 0, 1, 1);
        BindSourceDropdown();
        BindMatteTriggeredSyncToggle();
        SetImages(_inputTexture, _alphaTexture, _compositeTexture, true);
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

    void BindSourceDropdown()
    {
        if (_sourceDropdown == null) return;

        _sourceDropdown.UnregisterValueChangedCallback(OnSourceDropdownChanged);
        _sourceDropdown.choices = new List<string>(_sourceChoices);
        _sourceDropdown.SetEnabled(_sourceChoices.Count > 0);
        SetSelectedSource(_selectedSourceIndex);
        _sourceDropdown.RegisterValueChangedCallback(OnSourceDropdownChanged);
    }

    void BindMatteTriggeredSyncToggle()
    {
        if (_matteTriggeredSyncToggle == null) return;

        _matteTriggeredSyncToggle.UnregisterValueChangedCallback(
            OnMatteTriggeredSyncChanged
        );
        _matteTriggeredSyncToggle.SetValueWithoutNotify(_matteTriggeredSync);
        _matteTriggeredSyncToggle.RegisterValueChangedCallback(
            OnMatteTriggeredSyncChanged
        );
    }

    void OnSourceDropdownChanged(ChangeEvent<string> change)
    {
        _selectedSourceIndex = _sourceDropdown.index;
        SourceSelectionChanged?.Invoke(_selectedSourceIndex);
    }

    void OnMatteTriggeredSyncChanged(ChangeEvent<bool> change)
    {
        _matteTriggeredSync = change.newValue;
        MatteTriggeredSyncChanged?.Invoke(change.newValue);
    }

    void RefreshStatus()
    {
        if (_statusLabel == null) return;
        _statusLabel.tooltip = string.IsNullOrEmpty(_errorMessage) ?
            _statusMessage : _errorMessage;
        _statusLabel.text = string.IsNullOrEmpty(_errorMessage) && _inferenceTime > 0 ?
            $"{_inferenceTime:F1} ms" : "—";
    }

    static void ApplyImage(Image image, Texture texture, bool repaint)
    {
        if (image == null) return;
        var changed = image.image != texture;
        if (changed) image.image = texture;
        if (changed || repaint) image.MarkDirtyRepaint();
    }
}

} // namespace Rvm
