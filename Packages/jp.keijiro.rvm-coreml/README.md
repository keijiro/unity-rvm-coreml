# RVM Core ML

This package provides `Rvm.CoreML.MatteGenerator`, a macOS implementation of Robust Video
Matting using Core ML and Metal. It includes the native plugin, shaders, and the
fixed-shape 1280 x 720 MobileNetV3 model required by the generator.

## Usage

Add `MatteGenerator` to a GameObject and assign a texture to `Input`. The processed
result is written to `Output`; when no output is assigned, the component creates
and owns a 1280 x 720 render texture. Input orientation is the caller's
responsibility; vertically mirrored sources must be corrected before submission.

`MatteGenerator` processes `Input` automatically while the component is active.
Alternatively, call `Process` to submit a texture explicitly. Submission is
non-blocking and returns `false` while initialization, another frame, or a reset is
in progress. Use `IsReady`, `LastError`, and `InferenceTime` to observe its state.

RVM is recurrent: each successful inference carries temporal state into the next
frame. Call `Reset` after a discontinuity such as seeking or switching input sources.

## Architecture

The package keeps Unity-facing orchestration in C# and model execution in a native
Objective-C++ plugin:

| Layer | Responsibility |
| --- | --- |
| `MatteGenerator.cs` | Public component properties and methods. |
| `MatteGenerator.Internal.cs` | Component lifetime, frame scheduling, Unity GPU work, and resource ownership. |
| `NativePlugin.cs` | Managed declarations for the plugin's narrow C ABI. |
| `RVMPlugin.bundle` | Core ML model execution, recurrent state, result synchronization, and the shared alpha texture pool. |
| `Preprocess.shader` / `VisualizeAlpha.shader` | Input normalization and final color/alpha composition. |

The native boundary uses an opaque context handle and plain C values. Objective-C,
Core ML, Metal, and C++ types therefore never become part of the managed ABI. The C#
side owns Unity object lifetime and rendering order; the native side owns objects
that must remain close to Core ML, including the loaded model, recurrent tensors,
serial inference queue, and IOSurface-backed alpha buffers.

### Frame data flow

1. `Preprocess.shader` center-crops and scales the source into a
   fixed 1280 x 720 `RenderTexture`. This normalized image is retained as the RGB
   source for the final result.
2. `AsyncGPUReadback` returns that texture as BGRA bytes. The C# layer passes a
   pointer to the plugin only for the duration of the submission call, so the plugin
   immediately copies the pixels into a Core Video input buffer.
3. The plugin schedules Core ML prediction on a serial queue. Serial execution is
   required because the four recurrent output tensors from one frame become the
   recurrent inputs to the next.
4. Core ML writes the alpha result directly into a one-channel Core Video buffer
   backed by an IOSurface. The same storage has a Metal texture view, which C# wraps
   with `Texture2D.CreateExternalTexture`; no full-frame copy is needed on the return
   path.
5. C# polls a single-result mailbox and composites the normalized RGB texture with
   the returned alpha texture. If `Output` has no alpha channel, all available color
   channels receive the matte instead. The model's foreground output is not used.

Only one input is submitted at a time. Frame numbers and state generations reject
stale results after resets or component lifetime changes.

### Resource ownership and synchronization

The component owns its preprocessing texture, materials, external `Texture2D`
wrappers, and any output texture it creates itself. A caller-assigned `Output`
remains caller-owned. Disabling the component drains pending readbacks before these
Unity resources and the native context are destroyed.

The native context owns three alpha slots. A slot moves from inference to a ready
result and then to Unity GPU use. After queuing the composite, C# attaches a graphics
fence and keeps the slot leased until the fence passes. Generation tokens prevent a
late release from freeing a slot that has already been reused. This protocol lets
inference and rendering overlap without allowing Core ML to overwrite a texture that
Unity is still sampling.

`Reset` is deferred until pending readback and GPU leases have completed. It then
clears the recurrent state and frame sequence while keeping the loaded model and
allocated textures available for subsequent frames.

### Model deployment

In the Editor, the model is loaded directly from `Runtime/Models`. During a Player
build, `ModelBuildProcessor` adds it to `StreamingAssets/Models`, where the runtime
can address it by file path. Core ML compiles the `.mlmodel` on first use and the
native layer reuses the compiled model from the user cache on later loads.
