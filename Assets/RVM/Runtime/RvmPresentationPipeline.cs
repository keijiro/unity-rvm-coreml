using System.Collections.Generic;
using UnityEngine;

namespace Rvm
{

[RequireComponent(typeof(MatteGenerator))]
public sealed class RvmPresentationPipeline : MonoBehaviour
{
    const int MaxQueuedFrameCount = 16;
    const long MaxQueuedFrameBytes = 64 * 1024 * 1024;
    const string MatteTriggeredSyncPreferenceKey = "RVM.MatteTriggeredSync";

    sealed class QueuedFrame
    {
        public ulong Sequence { get; }
        public RenderTexture Snapshot { get; }
        public double FrameInterval { get; }
        public long ByteCount { get; }

        public QueuedFrame(
            ulong sequence,
            RenderTexture snapshot,
            double frameInterval
        )
        {
            Sequence = sequence;
            Snapshot = snapshot;
            FrameInterval = frameInterval;
            ByteCount = (long)snapshot.width * snapshot.height * 4;
        }
    }

    readonly List<QueuedFrame> _presentationQueue = new();
    readonly PresentationScheduler _presentationScheduler = new();
    MatteGenerator _generator;
    RvmOutputDisplay _display;
    QueuedFrame _submittedBoundary;
    RenderTexture _submittedOutput;
    ulong _submittedOutputVersion;
    ulong _nextSequence;
    long _queuedFrameBytes;
    bool _matteTriggeredSync;
    string _handledInferenceError;

    internal bool MatteTriggeredSync => _matteTriggeredSync;
    internal string LastError => _generator?.LastError;
    internal double InferenceTime => _generator?.InferenceTime ?? 0;

    internal void Initialize(RvmOutputDisplay display)
    {
        _generator = GetComponent<MatteGenerator>();
        _display = display;
        _handledInferenceError = null;
        _matteTriggeredSync = PlayerPrefs.GetInt(
            MatteTriggeredSyncPreferenceKey,
            1
        ) != 0;
        ResetPresentation();
    }

    internal void Release()
    {
        ResetPresentation();
        _generator = null;
        _display = null;
    }

    internal void SetMatteTriggeredSync(bool enabled)
    {
        _matteTriggeredSync = enabled;
        PlayerPrefs.SetInt(MatteTriggeredSyncPreferenceKey, enabled ? 1 : 0);
        PlayerPrefs.Save();
        ResetSynchronization();
    }

    internal void Enqueue(SourceFrame source)
    {
        var texture = source.Texture;
        if (texture == null || texture.width <= 0 || texture.height <= 0) return;
        if (_presentationQueue.Count > 0)
        {
            var previous = _presentationQueue[^1].Snapshot;
            if (previous.width != texture.width || previous.height != texture.height)
                ResetSynchronization();
        }

        var snapshot = new RenderTexture(
            texture.width,
            texture.height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB
        )
        {
            name = $"RVM Queued Input {_nextSequence}",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        snapshot.Create();

        // The source textures are mutable camera/video surfaces. A private copy
        // makes the queued color immutable and gives presentation and RVM the
        // exact same frame even when the source advances asynchronously.
        Graphics.Blit(texture, snapshot);

        var frame = new QueuedFrame(
            _nextSequence++,
            snapshot,
            source.FrameInterval
        );
        _presentationQueue.Add(frame);
        _queuedFrameBytes += frame.ByteCount;
        PrunePresentationQueue();
    }

    internal void Tick()
    {
        if (HandleInferenceError()) return;

        // OutputVersion advances when the package queues the matching output blit.
        // Copying the matte afterwards is ordered behind that blit on Unity's
        // graphics stream, so no CPU/GPU stall is needed here.
        if (_submittedBoundary != null &&
            !_presentationScheduler.BoundaryComplete &&
            _generator.OutputVersion != _submittedOutputVersion)
            _presentationScheduler.MarkBoundaryComplete();

        if (_matteTriggeredSync)
            AdvanceTriggeredPresentation();
        else
            AdvanceBoundaryOnlyPresentation();

        TrySubmitBoundary();
        PrunePresentationQueue();
    }

    internal void ResetSynchronization()
    {
        _generator?.Reset();
        ResetPresentation();
        ClearTexture(_generator?.ModelInput);
        ClearTexture(_generator?.Output);
        _display?.Clear();
    }

    bool HandleInferenceError()
    {
        var error = _generator?.LastError;
        if (string.IsNullOrEmpty(error))
        {
            _handledInferenceError = null;
            return false;
        }
        if (_submittedBoundary == null || error == _handledInferenceError) return false;

        _handledInferenceError = error;
        ResetSynchronization();
        return true;
    }

    void AdvanceTriggeredPresentation()
    {
        if (_submittedBoundary == null) return;

        if (_presentationScheduler.ShouldPresentFirst)
            PresentFrame(_submittedBoundary, true);
        else if (_presentationScheduler.ShouldAdvance(Time.unscaledTimeAsDouble))
            PresentNextFrame();

        if (_presentationScheduler.ShouldCompleteBoundary)
            CompleteBoundary();
    }

    void AdvanceBoundaryOnlyPresentation()
    {
        if (_submittedBoundary == null ||
            !_presentationScheduler.BoundaryComplete)
            return;

        PresentFrame(_submittedBoundary, false);
        CompleteBoundary();
    }

    void PresentNextFrame()
    {
        foreach (var frame in _presentationQueue)
        {
            if (frame.Sequence <= _presentationScheduler.PresentedSequence ||
                frame.Sequence > _submittedBoundary.Sequence)
                continue;
            PresentFrame(frame, false);
            return;
        }
    }

    void PresentFrame(QueuedFrame frame, bool firstFrame)
    {
        var output = _generator?.Output;
        if (output == null || output.width <= 0 || output.height <= 0) return;

        _display.PresentColor(frame.Snapshot, output.width, output.height);
        _presentationScheduler.Present(
            frame.Sequence,
            Time.unscaledTimeAsDouble,
            frame.FrameInterval,
            firstFrame
        );
        ReleasePresentedFrames();
    }

    void CompleteBoundary()
    {
        // Preserve the finished matte before a later submission can reuse the
        // configured RVM output texture.
        _display.PresentMatte(
            _submittedOutput,
            _submittedOutput.width,
            _submittedOutput.height
        );
        var completedSequence = _presentationScheduler.CompleteBoundary();
        _submittedBoundary = null;
        _submittedOutput = null;
        ReleaseFramesThrough(completedSequence);
    }

    void TrySubmitBoundary()
    {
        if (_submittedBoundary != null || _presentationQueue.Count == 0) return;

        var frame = _presentationQueue[^1];
        if (!_generator.Process(frame.Snapshot)) return;

        var output = _generator.Output;
        if (output == null) return;
        _submittedBoundary = frame;
        _submittedOutput = output;
        _submittedOutputVersion = _generator.OutputVersion;
        _presentationScheduler.SubmitBoundary(frame.Sequence);

        if (_presentationScheduler.ShouldPresentFirst)
            PresentFrame(frame, true);
        if (!_matteTriggeredSync)
            DiscardBoundaryOnlyIntermediateFrames();
    }

    void PrunePresentationQueue()
    {
        // Boundary and latest frames are the two synchronization anchors. When
        // both must survive, the queue may temporarily exceed the byte budget.
        while (_presentationQueue.Count > MaxQueuedFrameCount ||
               _queuedFrameBytes > MaxQueuedFrameBytes)
        {
            var latest = _presentationQueue[^1];
            var removeIndex = -1;
            for (var index = 0; index < _presentationQueue.Count; index++)
            {
                var frame = _presentationQueue[index];
                if (frame == _submittedBoundary || frame == latest) continue;
                removeIndex = index;
                break;
            }
            if (removeIndex < 0) break;
            ReleaseFrameAt(removeIndex);
        }
    }

    void ReleasePresentedFrames()
    {
        for (var index = _presentationQueue.Count - 1; index >= 0; index--)
        {
            var frame = _presentationQueue[index];
            if (!_presentationScheduler.HasPresentedFrame ||
                frame.Sequence > _presentationScheduler.PresentedSequence ||
                frame == _submittedBoundary)
                continue;
            ReleaseFrameAt(index);
        }
    }

    void ReleaseFramesThrough(ulong sequence)
    {
        for (var index = _presentationQueue.Count - 1; index >= 0; index--)
        {
            if (_presentationQueue[index].Sequence <= sequence)
                ReleaseFrameAt(index);
        }
    }

    void DiscardBoundaryOnlyIntermediateFrames()
    {
        var latest = _presentationQueue[^1];
        for (var index = _presentationQueue.Count - 1; index >= 0; index--)
        {
            var frame = _presentationQueue[index];
            if (frame == _submittedBoundary || frame == latest) continue;
            ReleaseFrameAt(index);
        }
    }

    void ReleaseFrameAt(int index)
    {
        var frame = _presentationQueue[index];
        _queuedFrameBytes -= frame.ByteCount;
        frame.Snapshot.Release();
        Destroy(frame.Snapshot);
        _presentationQueue.RemoveAt(index);
    }

    void ResetPresentation()
    {
        for (var index = _presentationQueue.Count - 1; index >= 0; index--)
            ReleaseFrameAt(index);
        _submittedBoundary = null;
        _submittedOutput = null;
        _submittedOutputVersion = 0;
        _nextSequence = 0;
        _queuedFrameBytes = 0;
        _presentationScheduler.Reset(_matteTriggeredSync);
    }

    static void ClearTexture(RenderTexture texture)
    {
        if (texture == null) return;
        var previous = RenderTexture.active;
        RenderTexture.active = texture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = previous;
    }
}

} // namespace Rvm
