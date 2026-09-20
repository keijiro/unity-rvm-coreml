# RVM native plugin

This directory contains the macOS native half of the RVM integration. It loads and
runs the fixed-shape Core ML model, exposes its alpha output as Metal textures, and
presents a C ABI that Unity calls through `RVMNative.cs`.

## Structure

The implementation is split into a C entry point and three internal components:

- `RVMPlugin.mm` is the exported C ABI. It receives Unity's Metal device and converts
  opaque `void *` handles into the internal API without exposing C++ or Objective-C
  types to managed code.
- `RVMContext.hpp` and `RVMContext.mm` coordinate the asynchronous request lifecycle.
  A context owns one model, one texture pool, a serial inference queue, and the
  result mailbox polled by Unity.
- `RVMModel.hpp` and `RVMModel.mm` own Core ML loading, signature validation,
  recurrent tensors, prediction, and conversion of Unity's BGRA input into a Core
  Video pixel buffer.
- `RVMAlphaTexturePool.hpp` and `RVMAlphaTexturePool.mm` own the IOSurface-backed
  alpha buffers and their Metal texture views. The pool prevents Core ML from
  overwriting a surface while Unity's GPU is still sampling it.

`Info.plist` supplies the bundle metadata used when the compiled binary is installed
under `Packages/jp.keijiro.rvm/Runtime/Plugins/macOS/RVMPlugin.bundle`.

## Frame lifecycle

Unity first checks `RVMCanSubmit`, then passes a 1280 x 720 BGRA frame to
`RVMSubmitBGRA`. The context copies the caller-owned pixels and schedules Core ML on
its serial queue. Serialization is required because RVM carries four recurrent
tensors from one successful prediction into the next.

Core ML writes the alpha plane directly into one of three IOSurface-backed pool
slots. Unity polls `RVMTryGetOutputInfoEx` until it receives the slot index and its
generation token, then wraps the corresponding Metal handle as an external texture.
After queuing render work, Unity marks the slot as GPU-in-flight and releases it only
after the graphics fence completes.

`RVMResetState` drains the inference queue, discards an unpublished result, and clears
the four recurrent tensors before another source or video loop begins. Slots already
owned by Unity's GPU are still released through their normal fence and generation
tokens before the reset call.

The pool state machine is:

```text
Free -> Inferencing -> Ready -> GPUInFlight -> Free
                       +------------------------> Free
```

The direct `Ready -> Free` path handles results Unity elects not to render. A
generation token distinguishes successive uses of the same slot, preventing a late
fence callback from releasing a newer frame.

The polling API uses `1` for success, `0` when work or a result is not currently
available, and `-1` for invalid input or an inference error. A successful poll does
not consume the result; ownership changes only through
`RVMMarkAlphaSlotGPUInFlight` or `RVMReleaseAlphaSlot`.

## Ownership and threading

The serial dispatch queue is the sole caller of `Model::Predict` and therefore the
sole mutator of recurrent model state. Unity calls submission, polling, and slot
release functions from outside that queue. `Context::mutex` makes its mailbox and
the texture-pool state one atomic unit; pool methods deliberately do not lock on
their own.

Core Foundation objects are released explicitly, while Objective-C references use
ARC. `DestroyContext` drains the inference queue before deleting the context because
queued blocks retain raw pointers to context-owned state.

## Building

From the repository root, run:

```sh
./build-native-plugin.sh
```

The script reads the Unity version from `ProjectSettings/ProjectVersion.txt`, uses
that Editor's native plugin headers, and builds a universal arm64/x86_64 bundle for
macOS 13 or newer. It installs and ad-hoc signs the result at
`Packages/jp.keijiro.rvm/Runtime/Plugins/macOS/RVMPlugin.bundle`.

The source is Objective-C++17 compiled with ARC and links Foundation, Core ML, Core
Video, IOSurface, and Metal. Loading a source `.mlmodel` compiles it into the user's
cache at `~/Library/Caches/jp.keijiro.rvm-unity/CoreML`; remove the matching cached
`.mlmodelc` directory when replacing a model without changing its filename.
