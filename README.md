# RVM Unity Camera Test

This macOS-only Unity 6 project runs Robust Video Matting (RVM) continuously on a webcam feed with Core ML and Metal. The left pane shows the normalized 1280 × 720 model input and the right pane shows the predicted person alpha matte.

## Setup

```sh
./prepare-model.sh
./build-native-plugin.sh
```

Open the project with Unity `6000.6.1f1` and run `Assets/Main.unity`. The Editor and standalone player require Metal and macOS 13 or newer.

`RVMDemoController.ComputeUnits` selects the Core ML compute units when the model is
created. It can be set from the Inspector or C# to `CpuOnly`, `CpuAndGpu`, `All`, or
`CpuAndNeuralEngine`. Changing it takes effect the next time the component is enabled;
the native plugin does not need to be rebuilt when switching modes.

To perform the import, shader, UI, model-signature, recurrent-inference, and native slot validation from the command line:

```sh
unity run . --editor-version 6000.6.1f1 --timeout 300 -- \
  -executeMethod ProjectBootstrap.ProjectValidator.Validate
```

## Model and attribution

The bundled model is `rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel` from [PeterL1n/RobustVideoMatting](https://github.com/PeterL1n/RobustVideoMatting), release `v1.0.0`. It uses MobileNetV3, a fixed 1280 × 720 input, downsample ratio 0.375, and INT8-quantized weights. Its SHA-256 is `68efe6e7a23d5337fb4f935f77e83b0ec3cc823803083953eb18f4cc0549d794`.

RVM is described in *Robust High-Resolution Video Matting with Temporal Guidance* (Lin et al., WACV 2022). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for upstream licensing information.
