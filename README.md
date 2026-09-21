# RVM Core ML Package for Unity

![demo](https://github.com/user-attachments/assets/c5ba4619-9b60-475c-9606-0896ef572131)

This macOS-only Unity package runs Robust Video Matting (RVM) using Core ML
and Metal.

## Installation

The RVM Core ML package (`jp.keijiro.rvm-coreml`) can be installed via the
"Keijiro" scoped registry using Package Manager. To add the registry to your
project, please follow [these instructions].

[these instructions]:
  https://gist.github.com/keijiro/f8c7e8ff29bfe63d86b888901b82644c

## Running the demo

Open the project with Unity `6000.6.1f1`, then run `Assets/Main.unity`. The
Editor and standalone player require Metal and macOS 13 or newer.

To use a video file as the source, place it in `Assets/StreamingAssets`. The
demo scans this folder automatically and lists available videos in the source
dropdown.

## MatteGenerator

Add `Rvm.CoreML.MatteGenerator` to a GameObject to use the inference pipeline.
`ComputeUnits` selects `CpuOnly`, `CpuAndGpu`, `All`, or `CpuAndNeuralEngine`;
changes take effect the next time the component is enabled.

Assign a `Texture` to `Input` in the Inspector or from C# for continuous
processing. The generator center-crops each available frame to the model's
1280 × 720 aspect ratio.

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

## Demo matte-triggered synchronization

The demo implements matte-triggered synchronization to keep video presentation
from slowing down to the RVM inference rate. The demo snapshots each accepted
source frame so that video can advance independently while each completed matte
remains paired with the exact frame used for inference.

With **Matte-Triggered Sync** enabled (the default), the demo presents queued
color frames at the source cadence while RVM runs asynchronously. Intermediate
frames use the preceding matte, and presentation does not advance past the frame
currently being processed. When disabled, the demo waits for inference before
presenting each matched color and matte, so video advances at the matte
generation rate and intermediate frames are dropped.

[rvm]: https://github.com/PeterL1n/RobustVideoMatting
[notices]: Packages/jp.keijiro.rvm-coreml/THIRD_PARTY_NOTICES.md
