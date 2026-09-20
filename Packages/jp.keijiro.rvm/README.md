# RVM

This package provides `Rvm.MatteGenerator`, a macOS implementation of Robust Video
Matting using Core ML and Metal. It includes the native plugin, shaders, and the
fixed-shape 1280 x 720 MobileNetV3 model required by the generator.

Add `MatteGenerator` to a GameObject and assign a texture to `Input`. The processed
result is written to `Output`; when no output is assigned, the component creates
and owns a 1280 x 720 render texture.

The Editor loads the model directly from the package. Player builds copy it to
`StreamingAssets/Models` automatically.
