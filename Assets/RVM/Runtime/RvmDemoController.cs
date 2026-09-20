using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Rvm.CoreML.Demo
{

[MovedFrom(true, "Rvm", "Rvm.Demo")]
public sealed class RvmDemoController : MonoBehaviour
{
    [SerializeField] RvmInputSource _source = null;
    [SerializeField] RvmPresentationPipeline _presentation = null;
    [SerializeField] RvmOutputDisplay _display = null;
    [SerializeField] RvmDemoView _view = null;

    // MonoBehaviour implementation

    void OnEnable()
    {
        _display.Initialize();
        _presentation.Initialize(_display);
        _source.SynchronizationResetRequested += OnSynchronizationResetRequested;
        _source.Initialize();

        _view.SourceSelectionChanged += OnSourceSelectionChanged;
        _view.MatteTriggeredSyncChanged += OnMatteTriggeredSyncChanged;
        _view.SetSources(_source.SourceNames, _source.SelectedSourceIndex);
        _view.SetMatteTriggeredSync(_presentation.MatteTriggeredSync);
        _view.SetImages(
            _display.InputTexture,
            _display.AlphaTexture,
            _display.CompositeTexture,
            false
        );
        _view.Initialize();

        _source.SelectSource(_source.SelectedSourceIndex);
    }

    void Update()
    {
        if (_source.TryGetFrame(out var frame))
            _presentation.Enqueue(frame);

        _presentation.Tick();
        var displayUpdated = _display.Tick();
        _view.SetImages(
            _display.InputTexture,
            _display.AlphaTexture,
            _display.CompositeTexture,
            displayUpdated
        );
        _view.SetStatus(
            _source.StatusMessage,
            _presentation.LastError,
            _presentation.InferenceTime
        );
    }

    void OnDisable()
    {
        if (_view != null)
        {
            _view.SourceSelectionChanged -= OnSourceSelectionChanged;
            _view.MatteTriggeredSyncChanged -= OnMatteTriggeredSyncChanged;
            _view.Release();
        }

        if (_source != null)
        {
            _source.SynchronizationResetRequested -= OnSynchronizationResetRequested;
            _source.Release();
        }

        _presentation?.Release();
        _display?.Release();
    }

    // Event handlers

    void OnSourceSelectionChanged(int index)
    {
        _source.SelectSource(index);
        _view.SetSelectedSource(_source.SelectedSourceIndex);
    }

    void OnMatteTriggeredSyncChanged(bool enabled)
    {
        _presentation.SetMatteTriggeredSync(enabled);
        _view.SetMatteTriggeredSync(_presentation.MatteTriggeredSync);
    }

    void OnSynchronizationResetRequested() =>
        _presentation.ResetSynchronization();
}

} // namespace Rvm.CoreML.Demo
