# RVM Unity Camera Test

This macOS-only Unity 6 project runs Robust Video Matting (RVM) continuously on a webcam feed with Core ML and Metal. The left pane shows the normalized 960 × 540 model input and the right pane shows the predicted person alpha matte.

## Setup

```sh
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

The bundled `rvm_mobilenetv3_960x540_s0.25_int8.mlmodel` is a derived model generated from the official MobileNetV3 weights with the upstream `coreml` exporter at revision `b4850905347f4fcc588b5f7ed7cbfd34ae206436`. It uses a fixed 960 × 540 input, downsample ratio 0.25, the deep guided filter, and INT8-quantized weights. The exporter ran with Torch 1.8.1, Torchvision 0.9.1, and CoreMLTools 5.0b1. The generated model's SHA-256 is `6b4ee7da140911480c9c8d334faa875b28ce622952e8b9ec7bb25df92959108c`.

RVM is described in *Robust High-Resolution Video Matting with Temporal Guidance* (Lin et al., WACV 2022). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for upstream licensing information.
