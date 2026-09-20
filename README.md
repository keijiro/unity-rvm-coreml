# RVM Unity Camera Test

This macOS-only Unity 6 project runs Robust Video Matting (RVM) continuously on camera and MP4 inputs with Core ML and Metal. The left pane shows the normalized 1280 × 720 model input and the right pane shows the predicted person alpha matte.

## Setup

```sh
./prepare-model.sh
./build-native-plugin.sh
```

Open the project with Unity `6000.6.1f1` and run `Assets/Main.unity`. The Editor and standalone player require Metal and macOS 13 or newer.

## MatteGenerator

Add `Rvm.MatteGenerator` to a GameObject to use the inference pipeline independently of
the demo. `ComputeUnits` selects `CpuOnly`, `CpuAndGpu`, `All`, or
`CpuAndNeuralEngine`; changes take effect the next time the component is enabled.

Assign a `Texture` to `Input` in the Inspector or from C# for continuous processing.
The generator center-crops each available frame to the model's 1280 × 720 aspect
ratio. Inputs must be oriented correctly before submission; camera integrations are
responsible for handling vertically mirrored frames.

For one-shot input, call `Process` without changing `Input`:

```csharp
if (generator.IsReady && generator.Process(sourceTexture))
    Debug.Log("Frame accepted");
```

`Process` returns `false` while the model is loading, inference or reset is in
progress, or the input is invalid. It copies accepted input immediately and does not
retain the supplied texture reference. After the one-shot frame completes, a
configured continuous `Input` resumes automatically.

`Output` accepts an externally owned `RenderTexture` of any size. If it is omitted,
the component creates and owns a 1280 × 720 alpha-capable output, available through
the same property. An output format with an alpha channel receives normalized input
RGB plus the matte in A. A format without alpha receives the matte in every available
color channel. The component never destroys an externally supplied output.

Call `Reset()` when changing sources or looping a video to discard pending frames and
clear recurrent state. `InferenceTime` reports the most recently completed Core ML
inference in milliseconds; `IsReady` and `LastError` expose load and error state.

To run the import, shader, UI, model-signature, recurrent-inference, and native slot tests from the command line:

```sh
unity test . --editor-version 6000.6.1f1 --mode EditMode \
  --output Logs/test-results.xml --timeout 300
```

## Model and attribution

The bundled model is `rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel` from [PeterL1n/RobustVideoMatting](https://github.com/PeterL1n/RobustVideoMatting), release `v1.0.0`. It uses MobileNetV3, a fixed 1280 × 720 input, downsample ratio 0.375, and INT8-quantized weights. Its SHA-256 is `68efe6e7a23d5337fb4f935f77e83b0ec3cc823803083953eb18f4cc0549d794`.

RVM is described in *Robust High-Resolution Video Matting with Temporal Guidance* (Lin et al., WACV 2022). See [THIRD_PARTY_NOTICES.md](Packages/jp.keijiro.rvm/THIRD_PARTY_NOTICES.md) for upstream licensing information.
