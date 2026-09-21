# RVM Core ML Package for Unity

This macOS-only Unity package runs Robust Video Matting (RVM) using Core ML
and Metal. This repository also contains a demo project that continuously
processes camera and MP4 inputs. Its left pane shows the normalized 1280 × 720
model input, and its right pane shows the predicted person alpha matte.

## Running the demo

Open the project with Unity `6000.6.1f1`, then run `Assets/Main.unity`. The
Editor and standalone player require Metal and macOS 13 or newer.

The model and native plugin are included in the repository. To download a
fresh copy of the model and rebuild the plugin from source, run:

```sh
./prepare-model.sh
./build-native-plugin.sh
```

## Matte-triggered synchronization

The demo separates source capture, RVM inference, and presentation because
camera and `VideoPlayer` textures can change while an inference is in flight.
`RvmPresentationPipeline` therefore copies every accepted source frame into an
immutable snapshot, assigns it a sequence number, and stores its source frame
interval. The same snapshot supplies both the displayed color and the input to
RVM, so a completed matte always has an exact color-frame counterpart.

When the **Matte-Triggered Sync** toggle is on (the default), the newest queued
snapshot that RVM accepts becomes a *presentation boundary*. The first boundary
is displayed immediately. For later boundaries, `PresentationScheduler`
advances the queued color snapshots from the previously displayed sequence at
the source cadence, at most once per Unity update. New frames may continue to
enter the queue, but presentation never passes the active boundary.

RVM output is asynchronous. The pipeline records `MatteGenerator.OutputVersion`
when it submits a boundary and treats a version change as completion. This is a
CPU-visible signal that the matching output blit has entered Unity's graphics
stream; copying the result afterwards stays correctly ordered on the GPU and
does not require a synchronous readback. Even if inference finishes early, the
pipeline holds that matte until color presentation reaches the same boundary.
It then copies the matte into a presentation-owned texture before the generator
can reuse its output and releases snapshots through the completed boundary.

Turning the toggle off selects a lower-latency, boundary-only mode. The demo
waits for inference to complete, then presents the boundary color and matte
together, discarding intermediate snapshots instead of replaying them. This
keeps each displayed pair exact but can make motion jump when inference is
slower than the source. The enabled mode preserves more of the source cadence,
although intermediate color frames are temporarily shown with the preceding
matte while the presentation catches up.

The queue is normally limited to 16 snapshots or 64 MiB. Its active boundary
and newest snapshot are retained as synchronization anchors, so it can briefly
exceed the byte limit when both must survive. Changing the source or toggle,
looping a video, changing frame dimensions, or handling an inference error
resets the queue, the RVM recurrent state, and the displayed textures. The
toggle setting itself is persisted in `PlayerPrefs`.

## MatteGenerator

Add `Rvm.CoreML.MatteGenerator` to a GameObject to use the inference pipeline
independently of the demo. `ComputeUnits` selects `CpuOnly`, `CpuAndGpu`, `All`,
or `CpuAndNeuralEngine`; changes take effect the next time the component is
enabled.

Assign a `Texture` to `Input` in the Inspector or from C# for continuous
processing. The generator center-crops each available frame to the model's
1280 × 720 aspect ratio. Inputs must be oriented correctly before submission;
camera integrations are responsible for handling vertically mirrored frames.

For one-shot input, call `Process` without changing `Input`:

```csharp
if (generator.IsReady && generator.Process(sourceTexture))
    Debug.Log("Frame accepted");
```

`Process` returns `false` while the model is loading, inference or reset is in
progress, or the input is invalid. It copies accepted input immediately and
does not retain the supplied texture reference. After the one-shot frame
completes, a configured continuous `Input` resumes automatically.

`Output` accepts an externally owned `RenderTexture` of any size. If it is
omitted, the component creates and owns a 1280 × 720 alpha-capable output,
available through the same property. An output format with an alpha channel
receives normalized input RGB plus the matte in A. A format without alpha
receives the matte in every available color channel. The component never
destroys an externally supplied output.

Call `Reset()` when changing sources or looping a video to discard pending
frames and clear recurrent state. `InferenceTime` reports the most recently
completed Core ML inference in milliseconds; `IsReady` and `LastError` expose
load and error state.

To run the import, shader, UI, model-signature, recurrent-inference, and native
slot tests from the command line:

```sh
unity test . --editor-version 6000.6.1f1 --mode EditMode \
  --output Logs/test-results.xml --timeout 300
```

## Model and attribution

The bundled model is
`rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel` from
[PeterL1n/RobustVideoMatting][rvm], release `v1.0.0`. It uses MobileNetV3, a
fixed 1280 × 720 input, downsample ratio 0.375, and INT8-quantized weights.
Its SHA-256 is:

```text
68efe6e7a23d5337fb4f935f77e83b0ec3cc823803083953eb18f4cc0549d794
```

RVM is described in *Robust High-Resolution Video Matting with Temporal
Guidance* (Lin et al., WACV 2022). See [THIRD_PARTY_NOTICES.md][notices] for
upstream licensing information.

[rvm]: https://github.com/PeterL1n/RobustVideoMatting
[notices]: Packages/jp.keijiro.rvm-coreml/THIRD_PARTY_NOTICES.md
