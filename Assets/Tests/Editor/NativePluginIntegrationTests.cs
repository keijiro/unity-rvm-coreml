using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Rvm;

namespace RVM.Tests.Editor
{

[Category("NativeIntegration")]
[Timeout(120000)]
public sealed class NativePluginIntegrationTests
{
    const string LibraryName = "RVMPlugin";
    const string ModelPath = "Packages/jp.keijiro.rvm/Runtime/Models/" +
        "rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";
    const int ErrorCapacity = 1024;
    const int InputWidth = 1280;
    const int InputHeight = 720;
    const int AlphaSlotCount = 3;

    IntPtr _handle;

    // These declarations intentionally duplicate the runtime ABI. Tests must
    // exercise the plugin directly so wrapper changes cannot hide an ABI regression.

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr RVMCreateWithComputeUnits(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        ComputeUnits computeUnits,
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

    [OneTimeSetUp]
    public void CreatePlugin()
    {
        var modelPath = Path.GetFullPath(ModelPath);
        var error = new StringBuilder(ErrorCapacity);
        _handle = RVMCreateWithComputeUnits(
            modelPath,
            ComputeUnits.All,
            error,
            error.Capacity
        );
        Assert.That(_handle, Is.Not.EqualTo(IntPtr.Zero), $"Native model load failed: {error}");
    }

    [SetUp]
    public void ResetPlugin()
    {
        RVMResetState(_handle);
        Assert.That(RVMCanSubmit(_handle), Is.EqualTo(1));
    }

    [OneTimeTearDown]
    public void DestroyPlugin()
    {
        if (_handle == IntPtr.Zero) return;
        RVMDestroy(_handle);
        _handle = IntPtr.Zero;
    }

    [Test]
    public void ModelReportsExpectedInputDimensions()
    {
        Assert.That(RVMGetInputWidth(_handle), Is.EqualTo(InputWidth));
        Assert.That(RVMGetInputHeight(_handle), Is.EqualTo(InputHeight));
    }

    [Test]
    public void AlphaSlotsHaveExpectedLayout()
    {
        var count = RVMGetAlphaSlotCount(_handle);
        Assert.That(count, Is.EqualTo(AlphaSlotCount));
        for (var index = 0; index < count; index++)
        {
            var result = RVMGetAlphaTextureInfo(
                _handle,
                index,
                out var width,
                out var height,
                out var texture
            );
            Assert.That(result, Is.EqualTo(1), $"Alpha slot {index} is unavailable.");
            Assert.That(width, Is.EqualTo(InputWidth));
            Assert.That(height, Is.EqualTo(InputHeight));
            Assert.That(texture, Is.Not.EqualTo(IntPtr.Zero));
        }
    }

    [Test]
    public void SubmissionEnforcesBackpressureAndSlotOwnership()
    {
        var pixels = new byte[InputWidth * InputHeight * 4];
        for (var frame = 1; frame <= 2; frame++)
        {
            FillSyntheticFrame(pixels, frame);
            SubmitFrameAndAssertBackpressure(pixels);
            WaitForOutput(
                out var width,
                out var height,
                out _,
                out var slotIndex,
                out var generation,
                out var frameNumber
            );

            Assert.That(width, Is.EqualTo(InputWidth));
            Assert.That(height, Is.EqualTo(InputHeight));
            Assert.That(frameNumber, Is.EqualTo((ulong)frame));
            Assert.That(RVMCanSubmit(_handle), Is.EqualTo(0));
            Assert.That(
                RVMReleaseAlphaSlot(_handle, slotIndex, generation + 1),
                Is.EqualTo(-1),
                "A mismatched generation was accepted."
            );

            Assert.That(
                RVMMarkAlphaSlotGPUInFlight(_handle, slotIndex, generation),
                Is.EqualTo(1)
            );
            Assert.That(RVMReleaseAlphaSlot(_handle, slotIndex, generation), Is.EqualTo(1));
            Assert.That(
                RVMReleaseAlphaSlot(_handle, slotIndex, generation),
                Is.EqualTo(0),
                "A double release was accepted."
            );
            Assert.That(RVMCanSubmit(_handle), Is.EqualTo(1));
        }
    }

    [Test]
    public void ResetDiscardsPendingOutputAndRestartsFrameNumber()
    {
        var pixels = new byte[InputWidth * InputHeight * 4];
        FillSyntheticFrame(pixels, 3);
        SubmitFrame(pixels);

        // Reset must wait for this in-flight prediction, discard its unpublished
        // mailbox result, and make the leased inference slot reusable.
        RVMResetState(_handle);
        var error = new StringBuilder(ErrorCapacity);
        var result = RVMTryGetOutputInfoEx(
            _handle,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            error,
            error.Capacity
        );
        Assert.That(result, Is.EqualTo(0), "Reset left an old output available.");
        Assert.That(RVMCanSubmit(_handle), Is.EqualTo(1));

        FillSyntheticFrame(pixels, 4);
        SubmitFrame(pixels);
        WaitForOutput(
            out _,
            out _,
            out _,
            out var slotIndex,
            out var generation,
            out var frameNumber
        );
        Assert.That(frameNumber, Is.EqualTo(1));
        Assert.That(
            RVMMarkAlphaSlotGPUInFlight(_handle, slotIndex, generation),
            Is.EqualTo(1)
        );
        Assert.That(RVMReleaseAlphaSlot(_handle, slotIndex, generation), Is.EqualTo(1));

        RVMResetState(_handle);
        Assert.That(RVMCanSubmit(_handle), Is.EqualTo(1));
    }

    void SubmitFrameAndAssertBackpressure(byte[] pixels)
    {
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.That(
                RVMSubmitBGRA(
                    _handle,
                    pin.AddrOfPinnedObject(),
                    InputWidth,
                    InputHeight,
                    InputWidth * 4
                ),
                Is.EqualTo(1)
            );
            Assert.That(RVMCanSubmit(_handle), Is.EqualTo(0));
            Assert.That(
                RVMSubmitBGRA(
                    _handle,
                    pin.AddrOfPinnedObject(),
                    InputWidth,
                    InputHeight,
                    InputWidth * 4
                ),
                Is.EqualTo(0),
                "A concurrent submission was not rejected."
            );
        }
        finally
        {
            pin.Free();
        }
    }

    void SubmitFrame(byte[] pixels)
    {
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.That(
                RVMSubmitBGRA(
                    _handle,
                    pin.AddrOfPinnedObject(),
                    InputWidth,
                    InputHeight,
                    InputWidth * 4
                ),
                Is.EqualTo(1)
            );
        }
        finally
        {
            pin.Free();
        }
    }

    void WaitForOutput(
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
                _handle,
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

        if (result < 0) Assert.Fail($"Native inference failed: {error}");
        Assert.That(result, Is.Not.EqualTo(0), "Native inference timed out.");
    }

    static void FillSyntheticFrame(byte[] pixels, int frame)
    {
        for (var y = 0; y < InputHeight; y++)
        for (var x = 0; x < InputWidth; x++)
        {
            var index = (y * InputWidth + x) * 4;
            pixels[index + 0] = (byte)(64 + frame * 32);
            pixels[index + 1] = (byte)(y * 255 / (InputHeight - 1));
            pixels[index + 2] = (byte)((x + frame * 17) * 255 / (InputWidth + 34));
            pixels[index + 3] = 255;
        }
    }
}

} // namespace RVM.Tests.Editor
