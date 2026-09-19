#include "RVMContext.hpp"

#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"

// Hide every implementation symbol by default and expose only this C ABI. Keeping
// Objective-C++ and STL types behind opaque handles avoids an ABI dependency in C#.
#define RVM_EXPORT extern "C" __attribute__((visibility("default")))

RVM_EXPORT void UnityPluginLoad(IUnityInterfaces *interfaces)
{
    // The texture pool must use Unity's Metal device so its native texture handles
    // can be wrapped directly by Texture2D.CreateExternalTexture.
    auto metal = interfaces == nullptr ? nullptr : interfaces->Get<IUnityGraphicsMetalV2>();
    rvm::SetMetalDevice(metal == nullptr ? nil : metal->MetalDevice());
}

RVM_EXPORT void UnityPluginUnload()
{
    rvm::SetMetalDevice(nil);
}

RVM_EXPORT void *RVMCreate(const char *modelPath, char *errorBuffer, int errorCapacity)
{
    return rvm::CreateContext(
        modelPath,
        rvm::ComputeUnits::All,
        errorBuffer,
        errorCapacity
    );
}

RVM_EXPORT void *RVMCreateWithComputeUnits(
    const char *modelPath,
    int computeUnits,
    char *errorBuffer,
    int errorCapacity
)
{
    return rvm::CreateContext(
        modelPath,
        static_cast<rvm::ComputeUnits>(computeUnits),
        errorBuffer,
        errorCapacity
    );
}

RVM_EXPORT void RVMDestroy(void *handle)
{
    rvm::DestroyContext(static_cast<rvm::Context *>(handle));
}

RVM_EXPORT void RVMResetState(void *handle)
{
    rvm::ResetState(static_cast<rvm::Context *>(handle));
}

RVM_EXPORT int RVMGetInputWidth(void *handle)
{
    return rvm::GetInputWidth(static_cast<rvm::Context *>(handle));
}

RVM_EXPORT int RVMGetInputHeight(void *handle)
{
    return rvm::GetInputHeight(static_cast<rvm::Context *>(handle));
}

RVM_EXPORT int RVMGetAlphaSlotCount(void *handle)
{
    return rvm::GetAlphaSlotCount(static_cast<rvm::Context *>(handle));
}

RVM_EXPORT int RVMGetAlphaTextureInfo(
    void *handle,
    int slotIndex,
    int *width,
    int *height,
    void **nativeTexture
)
{
    return rvm::GetAlphaTextureInfo(
        static_cast<rvm::Context *>(handle),
        slotIndex,
        width,
        height,
        nativeTexture
    );
}

RVM_EXPORT int RVMCanSubmit(void *handle)
{
    return rvm::CanSubmit(static_cast<rvm::Context *>(handle));
}

RVM_EXPORT int RVMSubmitBGRA(
    void *handle,
    const uint8_t *bgra,
    int width,
    int height,
    int rowBytes
)
{
    return rvm::SubmitBGRA(
        static_cast<rvm::Context *>(handle),
        bgra,
        width,
        height,
        rowBytes
    );
}

RVM_EXPORT int RVMTryGetOutputInfoEx(
    void *handle,
    int *width,
    int *height,
    double *inferenceMilliseconds,
    int *slotIndex,
    uint64_t *generation,
    uint64_t *frameNumber,
    char *errorBuffer,
    int errorCapacity
)
{
    return rvm::TryGetOutputInfo(
        static_cast<rvm::Context *>(handle),
        width,
        height,
        inferenceMilliseconds,
        slotIndex,
        generation,
        frameNumber,
        errorBuffer,
        errorCapacity
    );
}

RVM_EXPORT int RVMMarkAlphaSlotGPUInFlight(
    void *handle,
    int slotIndex,
    uint64_t generation
)
{
    return rvm::MarkAlphaSlotGPUInFlight(
        static_cast<rvm::Context *>(handle),
        slotIndex,
        generation
    );
}

RVM_EXPORT int RVMReleaseAlphaSlot(void *handle, int slotIndex, uint64_t generation)
{
    return rvm::ReleaseAlphaSlot(
        static_cast<rvm::Context *>(handle),
        slotIndex,
        generation
    );
}
