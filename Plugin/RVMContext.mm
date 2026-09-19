#import "RVMContext.hpp"

#import "RVMAlphaTexturePool.hpp"
#import "RVMModel.hpp"

#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>

namespace rvm
{
namespace
{

void CopyString(const std::string &source, char *destination, int capacity)
{
    if (destination == nullptr || capacity <= 0) return;

    // The C ABI accepts caller-owned buffers, so always reserve room for the
    // terminator even when a diagnostic must be truncated.
    auto length = std::min(source.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(destination, source.data(), length);
    destination[length] = '\0';
}

} // namespace

struct Context
{
    // Model is confined to queue because its recurrent state is updated by every
    // prediction. The mutex protects only state shared with Unity's polling thread.
    std::unique_ptr<Model> model;
    std::unique_ptr<AlphaTexturePool> alphaTextures;
    dispatch_queue_t queue = nullptr;
    std::mutex mutex;
    std::string error;
    int readySlot = -1;
    uint64_t readyGeneration = 0;
    uint64_t readyFrameNumber = 0;
    uint64_t nextFrameNumber = 1;
    double inferenceMilliseconds = 0;
    bool busy = false;
    bool ready = false;
    bool errorReady = false;
};

namespace
{

void ClearReadyResult(Context *context)
{
    context->ready = false;
    context->readySlot = -1;
    context->readyGeneration = 0;
    context->readyFrameNumber = 0;
}

void StoreError(Context *context, const std::string &message, int slotIndex = -1)
{
    std::lock_guard<std::mutex> lock(context->mutex);

    // Errors are delivered through the same single-result mailbox as successful
    // predictions. Holding a slot after failure would eventually stall submission.
    context->alphaTextures->Cancel(slotIndex);
    context->error = message;
    context->busy = false;
    ClearReadyResult(context);
    context->errorReady = true;
}

} // namespace

Context *CreateContext(
    const char *modelPath,
    ComputeUnits computeUnits,
    char *errorBuffer,
    int errorCapacity
)
{
    @autoreleasepool
    {
        auto model = std::make_unique<Model>();
        std::string error;
        if (!model->Load(modelPath, computeUnits, error))
        {
            CopyString(error, errorBuffer, errorCapacity);
            return nullptr;
        }

        auto alphaTextures = std::make_unique<AlphaTexturePool>();
        if (!alphaTextures->Initialize(error))
        {
            CopyString(error, errorBuffer, errorCapacity);
            return nullptr;
        }

        auto context = new Context();
        context->model = std::move(model);
        context->alphaTextures = std::move(alphaTextures);

        // RVM feeds each prediction's recurrent tensors into the next frame. A serial
        // queue preserves that temporal ordering without blocking Unity's main thread.
        context->queue = dispatch_queue_create(
            "jp.keijiro.rvm.inference",
            DISPATCH_QUEUE_SERIAL
        );
        return context;
    }
}

void DestroyContext(Context *context)
{
    if (context == nullptr) return;

    // The queued block captures Context and its input buffer, so destruction must wait
    // until it has either published a result or released its error path resources.
    dispatch_sync(context->queue, ^{});
    delete context;
}

int GetInputWidth(Context *context)
{
    return context == nullptr ? 0 : InputWidth;
}

int GetInputHeight(Context *context)
{
    return context == nullptr ? 0 : InputHeight;
}

int GetAlphaSlotCount(Context *context)
{
    return context == nullptr ? 0 : context->alphaTextures->GetSlotCount();
}

int GetAlphaTextureInfo(
    Context *context,
    int slotIndex,
    int *width,
    int *height,
    void **nativeTexture
)
{
    if (context == nullptr) return -1;
    return context->alphaTextures->GetTextureInfo(
        slotIndex,
        width,
        height,
        nativeTexture
    );
}

int CanSubmit(Context *context)
{
    if (context == nullptr) return 0;
    std::lock_guard<std::mutex> lock(context->mutex);
    return !context->busy && !context->ready && !context->errorReady &&
           context->alphaTextures->HasFreeSlot();
}

int SubmitBGRA(
    Context *context,
    const uint8_t *bgra,
    int width,
    int height,
    int rowBytes
)
{
    if (context == nullptr || bgra == nullptr) return -1;
    if (width != InputWidth || height != InputHeight || rowBytes < width * 4) return -1;

    int slotIndex;
    uint64_t frameNumber;
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        if (context->busy || context->ready || context->errorReady) return 0;
        slotIndex = context->alphaTextures->Acquire();
        if (slotIndex < 0) return 0;
        frameNumber = context->nextFrameNumber++;
        context->busy = true;
    }

    // Unity owns the source pointer only for the duration of this call. Copy it into
    // a retained pixel buffer before the asynchronous block outlives that pointer.
    auto input = CreateInputPixelBuffer(bgra, width, height, rowBytes);
    if (input == nullptr)
    {
        StoreError(context, "Could not allocate the Core Video input buffer.", slotIndex);
        return -1;
    }

    dispatch_async(context->queue, ^{
        @autoreleasepool
        {
            double milliseconds;
            std::string error;
            auto alpha = context->alphaTextures->GetPixelBuffer(slotIndex);
            auto succeeded = context->model->Predict(
                input,
                alpha,
                milliseconds,
                error
            );
            CFRelease(input);
            if (!succeeded)
            {
                StoreError(context, error, slotIndex);
                return;
            }

            std::lock_guard<std::mutex> lock(context->mutex);
            context->alphaTextures->MarkReady(slotIndex);
            context->inferenceMilliseconds = milliseconds;
            context->readySlot = slotIndex;
            context->readyGeneration = context->alphaTextures->GetGeneration(slotIndex);
            context->readyFrameNumber = frameNumber;
            context->busy = false;
            context->ready = true;
        }
    });
    return 1;
}

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
)
{
    if (width != nullptr) *width = 0;
    if (height != nullptr) *height = 0;
    if (inferenceMilliseconds != nullptr) *inferenceMilliseconds = 0;
    if (slotIndex != nullptr) *slotIndex = -1;
    if (generation != nullptr) *generation = 0;
    if (frameNumber != nullptr) *frameNumber = 0;
    if (context == nullptr) return -1;

    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->errorReady)
    {
        CopyString(context->error, errorBuffer, errorCapacity);
        context->errorReady = false;
        return -1;
    }
    if (!context->ready) return 0;

    // Polling is non-destructive: Unity must first create/queue its external texture
    // work, then explicitly transfer or release the slot using its generation token.
    if (width != nullptr) *width = InputWidth;
    if (height != nullptr) *height = InputHeight;
    if (inferenceMilliseconds != nullptr)
        *inferenceMilliseconds = context->inferenceMilliseconds;
    if (slotIndex != nullptr) *slotIndex = context->readySlot;
    if (generation != nullptr) *generation = context->readyGeneration;
    if (frameNumber != nullptr) *frameNumber = context->readyFrameNumber;
    return 1;
}

int MarkAlphaSlotGPUInFlight(Context *context, int slotIndex, uint64_t generation)
{
    if (context == nullptr) return -1;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (!context->ready || context->readySlot != slotIndex ||
        context->readyGeneration != generation)
        return 0;

    auto result = context->alphaTextures->MarkGPUInFlight(slotIndex, generation);
    if (result != 1) return result;
    ClearReadyResult(context);
    return 1;
}

int ReleaseAlphaSlot(Context *context, int slotIndex, uint64_t generation)
{
    if (context == nullptr) return -1;
    std::lock_guard<std::mutex> lock(context->mutex);
    auto result = context->alphaTextures->Release(slotIndex, generation);
    if (result != 1) return result;
    if (context->readySlot == slotIndex && context->readyGeneration == generation)
        ClearReadyResult(context);
    return 1;
}

} // namespace rvm
