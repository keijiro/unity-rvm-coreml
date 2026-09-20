using System;

namespace Rvm.CoreML.Demo
{

internal sealed class PresentationScheduler
{
    public bool MatteTriggeredSync { get; private set; }
    public bool HasBoundary { get; private set; }
    public ulong BoundarySequence { get; private set; }
    public bool BoundaryComplete { get; private set; }
    public bool HasPresentedFrame { get; private set; }
    public ulong PresentedSequence { get; private set; }
    public double NextPresentationTime { get; private set; }

    public bool ShouldPresentFirst =>
        MatteTriggeredSync && HasBoundary && !HasPresentedFrame;

    public bool ShouldCompleteBoundary =>
        HasBoundary && BoundaryComplete &&
        (!MatteTriggeredSync || PresentedSequence == BoundarySequence);

    public void Reset(bool matteTriggeredSync)
    {
        MatteTriggeredSync = matteTriggeredSync;
        HasBoundary = false;
        BoundarySequence = 0;
        BoundaryComplete = false;
        HasPresentedFrame = false;
        PresentedSequence = 0;
        NextPresentationTime = 0;
    }

    public void SubmitBoundary(ulong sequence)
    {
        if (HasBoundary)
            throw new InvalidOperationException(
                "A presentation boundary is already active."
            );

        HasBoundary = true;
        BoundarySequence = sequence;
        BoundaryComplete = false;
    }

    public void MarkBoundaryComplete()
    {
        if (!HasBoundary)
            throw new InvalidOperationException("There is no presentation boundary.");
        BoundaryComplete = true;
    }

    public bool ShouldAdvance(double currentTime) =>
        MatteTriggeredSync && HasBoundary && HasPresentedFrame &&
        PresentedSequence < BoundarySequence && currentTime >= NextPresentationTime;

    public void Present(
        ulong sequence,
        double currentTime,
        double frameInterval,
        bool firstFrame
    )
    {
        if (!HasBoundary || sequence > BoundarySequence)
            throw new InvalidOperationException("The frame exceeds the active boundary.");
        if (HasPresentedFrame && sequence < PresentedSequence)
            throw new InvalidOperationException("Presentation cannot move backwards.");

        PresentedSequence = sequence;
        HasPresentedFrame = true;
        // Advancing from the existing deadline preserves source cadence after a
        // slow render, while the caller still consumes at most one frame per tick.
        NextPresentationTime = firstFrame ?
            currentTime + frameInterval : NextPresentationTime + frameInterval;
    }

    public ulong CompleteBoundary()
    {
        if (!ShouldCompleteBoundary)
            throw new InvalidOperationException("The boundary is not ready to complete.");

        var sequence = BoundarySequence;
        HasBoundary = false;
        BoundaryComplete = false;
        return sequence;
    }
}

} // namespace Rvm.CoreML.Demo
