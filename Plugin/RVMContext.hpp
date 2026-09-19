#pragma once

#import <Metal/Metal.h>

#include <cstdint>

namespace rvm
{

struct Context;

// These values are part of the C ABI consumed by RVMComputeUnits in Unity. Keep
// their numeric representation synchronized when adding a new Core ML mode.
enum class ComputeUnits
{
    CpuOnly = 0,
    CpuAndGpu = 1,
    All = 2,
    CpuAndNeuralEngine = 3
};

void SetMetalDevice(id<MTLDevice> device);

Context *CreateContext(
    const char *modelPath,
    ComputeUnits computeUnits,
    char *errorBuffer,
    int errorCapacity
);
void DestroyContext(Context *context);
void ResetState(Context *context);

int GetInputWidth(Context *context);
int GetInputHeight(Context *context);
int GetAlphaSlotCount(Context *context);
int GetAlphaTextureInfo(
    Context *context,
    int slotIndex,
    int *width,
    int *height,
    void **nativeTexture
);

int CanSubmit(Context *context);

// Submission and output polling use 1 for success, 0 for backpressure/no result,
// and -1 for invalid input or an inference error. A successful output remains
// owned by Context until the caller marks it in flight or releases it.
int SubmitBGRA(
    Context *context,
    const uint8_t *bgra,
    int width,
    int height,
    int rowBytes
);
int TryGetOutputInfo(
    Context *context,
    int *width,
    int *height,
    double *inferenceMilliseconds,
    int *slotIndex,
    uint64_t *generation,
    uint64_t *frameNumber,
    char *errorBuffer,
    int errorCapacity
);
int MarkAlphaSlotGPUInFlight(Context *context, int slotIndex, uint64_t generation);
int ReleaseAlphaSlot(Context *context, int slotIndex, uint64_t generation);

} // namespace rvm
