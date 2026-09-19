#import "RVMContext.hpp"

#import <CoreML/CoreML.h>
#import <CoreVideo/CoreVideo.h>
#import <Foundation/Foundation.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>

namespace rvm
{
namespace
{

constexpr int InputWidth = 1280;
constexpr int InputHeight = 720;
constexpr int AlphaSlotCount = 3;
constexpr int RecurrentCount = 4;

__strong id<MTLDevice> s_MetalDevice = nil;

enum class SlotState
{
    Free,
    Inferencing,
    Ready,
    GPUInFlight
};

struct AlphaSlot
{
    CVPixelBufferRef pixelBuffer = nullptr;
    CVMetalTextureRef textureView = nullptr;
    __strong id<MTLTexture> texture = nil;
    SlotState state = SlotState::Free;
    uint64_t generation = 0;
};

const std::array<NSString *, RecurrentCount> InputStateNames =
{
    @"r1i", @"r2i", @"r3i", @"r4i"
};

const std::array<NSString *, RecurrentCount> OutputStateNames =
{
    @"r1o", @"r2o", @"r3o", @"r4o"
};

const std::array<std::array<int, 4>, RecurrentCount> RecurrentShapes =
{{
    {{1, 16, 135, 240}},
    {{1, 20, 68, 120}},
    {{1, 40, 34, 60}},
    {{1, 64, 17, 30}}
}};

void CopyString(const std::string &source, char *destination, int capacity)
{
    if (destination == nullptr || capacity <= 0) return;
    auto length = std::min(source.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(destination, source.data(), length);
    destination[length] = '\0';
}

std::string ErrorString(NSError *error)
{
    if (error == nil) return "Unknown Core ML error.";
    return error.localizedDescription.UTF8String ?: "Unknown Core ML error.";
}

bool TryGetMLComputeUnits(ComputeUnits source, MLComputeUnits &destination)
{
    switch (source)
    {
        case ComputeUnits::CpuOnly:
            destination = MLComputeUnitsCPUOnly;
            return true;
        case ComputeUnits::CpuAndGpu:
            destination = MLComputeUnitsCPUAndGPU;
            return true;
        case ComputeUnits::All:
            destination = MLComputeUnitsAll;
            return true;
        case ComputeUnits::CpuAndNeuralEngine:
            destination = MLComputeUnitsCPUAndNeuralEngine;
            return true;
        default:
            return false;
    }
}

bool MatchesShape(NSArray<NSNumber *> *shape, const std::array<int, 4> &expected)
{
    if (shape == nil || shape.count != expected.size()) return false;
    for (NSUInteger index = 0; index < shape.count; index++)
        if (shape[index].intValue != expected[index]) return false;
    return true;
}

bool ValidateImageFeature(
    NSDictionary<NSString *, MLFeatureDescription *> *features,
    NSString *name,
    int width,
    int height,
    OSType pixelFormat,
    bool optional,
    std::string &error
)
{
    auto description = features[name];
    auto constraint = description.imageConstraint;
    if (description == nil || description.type != MLFeatureTypeImage || constraint == nil ||
        constraint.pixelsWide != width || constraint.pixelsHigh != height ||
        constraint.pixelFormatType != pixelFormat || description.isOptional != optional)
    {
        error = "Unexpected Core ML image feature: " +
                std::string(name.UTF8String ?: "<unknown>") + ".";
        return false;
    }
    return true;
}

bool ValidateMultiArrayFeature(
    NSDictionary<NSString *, MLFeatureDescription *> *features,
    NSString *name,
    const std::array<int, 4> &shape,
    bool optional,
    std::string &error
)
{
    auto description = features[name];
    auto constraint = description.multiArrayConstraint;
    auto shapeMatches = optional ? MatchesShape(constraint.shape, shape) :
                                  (constraint.shape.count == 0 ||
                                   MatchesShape(constraint.shape, shape));
    if (description == nil || description.type != MLFeatureTypeMultiArray || constraint == nil ||
        constraint.dataType != MLMultiArrayDataTypeFloat32 || !shapeMatches ||
        description.isOptional != optional)
    {
        error = "Unexpected Core ML recurrent feature: " +
                std::string(name.UTF8String ?: "<unknown>") + ".";
        return false;
    }
    return true;
}

bool ValidateModelDescription(MLModel *model, std::string &error)
{
    auto inputs = model.modelDescription.inputDescriptionsByName;
    auto outputs = model.modelDescription.outputDescriptionsByName;
    if (inputs.count != 5 || outputs.count != 6)
    {
        error = "The RVM model must have 5 inputs and 6 outputs.";
        return false;
    }
    if (!ValidateImageFeature(
            inputs,
            @"src",
            InputWidth,
            InputHeight,
            kCVPixelFormatType_32BGRA,
            false,
            error
        ))
        return false;
    if (!ValidateImageFeature(
            outputs,
            @"fgr",
            InputWidth,
            InputHeight,
            kCVPixelFormatType_32BGRA,
            false,
            error
        ))
        return false;
    if (!ValidateImageFeature(
            outputs,
            @"pha",
            InputWidth,
            InputHeight,
            kCVPixelFormatType_OneComponent8,
            false,
            error
        ))
        return false;

    for (auto index = 0; index < RecurrentCount; index++)
    {
        if (!ValidateMultiArrayFeature(
                inputs,
                InputStateNames[static_cast<size_t>(index)],
                RecurrentShapes[static_cast<size_t>(index)],
                true,
                error
            ))
            return false;
        if (!ValidateMultiArrayFeature(
                outputs,
                OutputStateNames[static_cast<size_t>(index)],
                RecurrentShapes[static_cast<size_t>(index)],
                false,
                error
            ))
            return false;
    }
    return true;
}

NSURL *ResolveModelURL(NSString *path, NSError **error)
{
    auto sourceURL = [NSURL fileURLWithPath:path];
    if ([path.pathExtension caseInsensitiveCompare:@"mlmodel"] != NSOrderedSame)
        return sourceURL;

    auto manager = NSFileManager.defaultManager;
    auto cacheRoot = [manager URLsForDirectory:NSCachesDirectory
                                      inDomains:NSUserDomainMask].firstObject;
    if (cacheRoot == nil) return [MLModel compileModelAtURL:sourceURL error:error];

    auto cacheDirectory = [cacheRoot URLByAppendingPathComponent:
        @"jp.keijiro.rvm-unity/CoreML" isDirectory:YES];
    auto cacheName = [[path.lastPathComponent stringByDeletingPathExtension]
        stringByAppendingPathExtension:@"mlmodelc"];
    auto cachedURL = [cacheDirectory URLByAppendingPathComponent:cacheName isDirectory:YES];
    BOOL isDirectory = NO;
    if ([manager fileExistsAtPath:cachedURL.path isDirectory:&isDirectory] && isDirectory)
        return cachedURL;

    if (![manager createDirectoryAtURL:cacheDirectory
           withIntermediateDirectories:YES
                            attributes:nil
                                 error:error])
        return nil;

    auto compiledURL = [MLModel compileModelAtURL:sourceURL error:error];
    if (compiledURL == nil) return nil;
    if ([manager copyItemAtURL:compiledURL toURL:cachedURL error:error]) return cachedURL;

    if ([manager fileExistsAtPath:cachedURL.path isDirectory:&isDirectory] && isDirectory)
    {
        if (error != nullptr) *error = nil;
        return cachedURL;
    }
    return nil;
}

CVPixelBufferRef CreateInputPixelBuffer(
    const uint8_t *bgra,
    int width,
    int height,
    int rowBytes
)
{
    NSDictionary *attributes = @{
        (id)kCVPixelBufferMetalCompatibilityKey: @YES,
        (id)kCVPixelBufferIOSurfacePropertiesKey: @{}
    };
    CVPixelBufferRef buffer = nullptr;
    auto status = CVPixelBufferCreate(
        kCFAllocatorDefault,
        width,
        height,
        kCVPixelFormatType_32BGRA,
        (__bridge CFDictionaryRef)attributes,
        &buffer
    );
    if (status != kCVReturnSuccess || buffer == nullptr) return nullptr;

    status = CVPixelBufferLockBaseAddress(buffer, 0);
    if (status != kCVReturnSuccess)
    {
        CFRelease(buffer);
        return nullptr;
    }
    auto destination = static_cast<uint8_t *>(CVPixelBufferGetBaseAddress(buffer));
    auto destinationRowBytes = CVPixelBufferGetBytesPerRow(buffer);
    for (auto y = 0; y < height; y++)
    {
        auto sourceRow = bgra + y * rowBytes;
        auto destinationRow = destination + y * destinationRowBytes;
        std::memcpy(destinationRow, sourceRow, static_cast<size_t>(width * 4));
    }
    CVPixelBufferUnlockBaseAddress(buffer, 0);
    return buffer;
}

} // namespace

struct Context
{
    __strong MLModel *model = nil;
    __strong NSArray<MLMultiArray *> *recurrentStates = nil;
    dispatch_queue_t queue = nullptr;
    std::mutex mutex;
    CVMetalTextureCacheRef textureCache = nullptr;
    std::array<AlphaSlot, AlphaSlotCount> slots;
    std::string error;
    int readySlot = -1;
    int nextSlot = 0;
    uint64_t readyGeneration = 0;
    uint64_t readyFrameNumber = 0;
    uint64_t nextFrameNumber = 1;
    double inferenceMilliseconds = 0;
    bool busy = false;
    bool ready = false;
    bool errorReady = false;

    ~Context()
    {
        recurrentStates = nil;
        model = nil;
        for (auto &slot : slots)
        {
            slot.texture = nil;
            if (slot.textureView != nullptr) CFRelease(slot.textureView);
            if (slot.pixelBuffer != nullptr) CFRelease(slot.pixelBuffer);
        }
        if (textureCache != nullptr) CFRelease(textureCache);
    }
};

namespace
{

bool CreateAlphaSlots(Context *context, std::string &error)
{
    auto device = s_MetalDevice ?: MTLCreateSystemDefaultDevice();
    if (device == nil)
    {
        error = "Could not obtain a Metal device.";
        return false;
    }

    auto status = CVMetalTextureCacheCreate(
        kCFAllocatorDefault,
        nullptr,
        device,
        nullptr,
        &context->textureCache
    );
    if (status != kCVReturnSuccess || context->textureCache == nullptr)
    {
        error = "Could not create the Core Video Metal texture cache.";
        return false;
    }

    NSDictionary *attributes = @{
        (id)kCVPixelBufferMetalCompatibilityKey: @YES,
        (id)kCVPixelBufferIOSurfacePropertiesKey: @{}
    };
    for (auto &slot : context->slots)
    {
        status = CVPixelBufferCreate(
            kCFAllocatorDefault,
            InputWidth,
            InputHeight,
            kCVPixelFormatType_OneComponent8,
            (__bridge CFDictionaryRef)attributes,
            &slot.pixelBuffer
        );
        if (status != kCVReturnSuccess || slot.pixelBuffer == nullptr ||
            CVPixelBufferGetIOSurface(slot.pixelBuffer) == nullptr)
        {
            error = "Could not create an IOSurface-backed alpha buffer.";
            return false;
        }

        status = CVMetalTextureCacheCreateTextureFromImage(
            kCFAllocatorDefault,
            context->textureCache,
            slot.pixelBuffer,
            nullptr,
            MTLPixelFormatR8Unorm,
            InputWidth,
            InputHeight,
            0,
            &slot.textureView
        );
        if (status != kCVReturnSuccess || slot.textureView == nullptr)
        {
            error = "Could not create a Metal alpha texture view.";
            return false;
        }
        slot.texture = CVMetalTextureGetTexture(slot.textureView);
        if (slot.texture == nil)
        {
            error = "The alpha texture view did not contain a Metal texture.";
            return false;
        }
    }
    return true;
}

int AcquireSlot(Context *context)
{
    for (auto offset = 0; offset < AlphaSlotCount; offset++)
    {
        auto index = (context->nextSlot + offset) % AlphaSlotCount;
        auto &slot = context->slots[static_cast<size_t>(index)];
        if (slot.state != SlotState::Free) continue;
        slot.state = SlotState::Inferencing;
        slot.generation++;
        context->nextSlot = (index + 1) % AlphaSlotCount;
        return index;
    }
    return -1;
}

void StoreError(Context *context, const std::string &message, int slotIndex = -1)
{
    std::lock_guard<std::mutex> lock(context->mutex);
    if (slotIndex >= 0 && slotIndex < AlphaSlotCount)
        context->slots[static_cast<size_t>(slotIndex)].state = SlotState::Free;
    context->error = message;
    context->busy = false;
    context->ready = false;
    context->readySlot = -1;
    context->readyGeneration = 0;
    context->readyFrameNumber = 0;
    context->errorReady = true;
}

} // namespace

void SetMetalDevice(id<MTLDevice> device)
{
    s_MetalDevice = device;
}

Context *CreateContext(
    const char *modelPath,
    ComputeUnits computeUnits,
    char *errorBuffer,
    int errorCapacity
)
{
    @autoreleasepool
    {
        if (modelPath == nullptr)
        {
            CopyString("The model path is null.", errorBuffer, errorCapacity);
            return nullptr;
        }

        MLComputeUnits mlComputeUnits;
        if (!TryGetMLComputeUnits(computeUnits, mlComputeUnits))
        {
            CopyString("Invalid Core ML compute units value.", errorBuffer, errorCapacity);
            return nullptr;
        }

        NSError *error = nil;
        auto path = [NSString stringWithUTF8String:modelPath];
        auto modelURL = ResolveModelURL(path, &error);
        if (modelURL == nil)
        {
            CopyString(ErrorString(error), errorBuffer, errorCapacity);
            return nullptr;
        }

        auto configuration = [MLModelConfiguration new];
        configuration.computeUnits = mlComputeUnits;
        auto model = [MLModel modelWithContentsOfURL:modelURL
                                      configuration:configuration
                                              error:&error];
        if (model == nil)
        {
            CopyString(ErrorString(error), errorBuffer, errorCapacity);
            return nullptr;
        }

        std::string validationError;
        if (!ValidateModelDescription(model, validationError))
        {
            CopyString(validationError, errorBuffer, errorCapacity);
            return nullptr;
        }

        auto context = new Context();
        context->model = model;
        context->queue = dispatch_queue_create(
            "jp.keijiro.rvm.inference",
            DISPATCH_QUEUE_SERIAL
        );
        std::string slotError;
        if (!CreateAlphaSlots(context, slotError))
        {
            CopyString(slotError, errorBuffer, errorCapacity);
            delete context;
            return nullptr;
        }
        return context;
    }
}

void DestroyContext(Context *context)
{
    if (context == nullptr) return;
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
    return context == nullptr ? 0 : AlphaSlotCount;
}

int GetAlphaTextureInfo(
    Context *context,
    int slotIndex,
    int *width,
    int *height,
    void **nativeTexture
)
{
    if (context == nullptr || slotIndex < 0 || slotIndex >= AlphaSlotCount) return -1;
    const auto &slot = context->slots[static_cast<size_t>(slotIndex)];
    if (slot.texture == nil) return -1;
    if (width != nullptr) *width = InputWidth;
    if (height != nullptr) *height = InputHeight;
    if (nativeTexture != nullptr) *nativeTexture = (__bridge void *)slot.texture;
    return 1;
}

int CanSubmit(Context *context)
{
    if (context == nullptr) return 0;
    std::lock_guard<std::mutex> lock(context->mutex);
    auto hasFreeSlot = std::any_of(
        context->slots.begin(),
        context->slots.end(),
        [](const auto &slot) { return slot.state == SlotState::Free; }
    );
    return !context->busy && !context->ready && !context->errorReady && hasFreeSlot;
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
        slotIndex = AcquireSlot(context);
        if (slotIndex < 0) return 0;
        frameNumber = context->nextFrameNumber++;
        context->busy = true;
    }

    auto pixelBuffer = CreateInputPixelBuffer(bgra, width, height, rowBytes);
    if (pixelBuffer == nullptr)
    {
        StoreError(context, "Could not allocate the Core Video input buffer.", slotIndex);
        return -1;
    }

    dispatch_async(context->queue, ^{
        @autoreleasepool
        {
            auto &slot = context->slots[static_cast<size_t>(slotIndex)];
            NSError *error = nil;
            auto sourceValue = [MLFeatureValue featureValueWithPixelBuffer:pixelBuffer];
            auto inputs = [NSMutableDictionary<NSString *, id> dictionaryWithObject:sourceValue
                                                                             forKey:@"src"];
            if (context->recurrentStates != nil)
            {
                for (auto index = 0; index < RecurrentCount; index++)
                {
                    auto state = context->recurrentStates[static_cast<NSUInteger>(index)];
                    inputs[InputStateNames[static_cast<size_t>(index)]] =
                        [MLFeatureValue featureValueWithMultiArray:state];
                }
            }
            auto provider = [[MLDictionaryFeatureProvider alloc]
                initWithDictionary:inputs
                error:&error];
            if (provider == nil)
            {
                CFRelease(pixelBuffer);
                StoreError(context, ErrorString(error), slotIndex);
                return;
            }

            auto options = [MLPredictionOptions new];
            options.outputBackings = @{ @"pha": (__bridge id)slot.pixelBuffer };
            auto start = std::chrono::steady_clock::now();
            auto prediction = [context->model predictionFromFeatures:provider
                                                       options:options
                                                         error:&error];
            auto end = std::chrono::steady_clock::now();
            CFRelease(pixelBuffer);
            if (prediction == nil)
            {
                StoreError(context, ErrorString(error), slotIndex);
                return;
            }

            auto alpha = [prediction featureValueForName:@"pha"].imageBufferValue;
            if (alpha != slot.pixelBuffer)
            {
                StoreError(
                    context,
                    "Core ML rejected the requested alpha output backing.",
                    slotIndex
                );
                return;
            }

            auto nextStates = [NSMutableArray<MLMultiArray *> arrayWithCapacity:RecurrentCount];
            for (auto index = 0; index < RecurrentCount; index++)
            {
                auto state = [prediction
                    featureValueForName:OutputStateNames[static_cast<size_t>(index)]
                ].multiArrayValue;
                if (state == nil || state.dataType != MLMultiArrayDataTypeFloat32 ||
                    !MatchesShape(state.shape, RecurrentShapes[static_cast<size_t>(index)]))
                {
                    StoreError(context, "The model returned an invalid recurrent state.", slotIndex);
                    return;
                }
                [nextStates addObject:state];
            }

            auto milliseconds = std::chrono::duration<double, std::milli>(end - start).count();
            std::lock_guard<std::mutex> lock(context->mutex);
            context->recurrentStates = [nextStates copy];
            context->inferenceMilliseconds = milliseconds;
            slot.state = SlotState::Ready;
            context->readySlot = slotIndex;
            context->readyGeneration = slot.generation;
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
    if (context == nullptr || slotIndex < 0 || slotIndex >= AlphaSlotCount) return -1;
    std::lock_guard<std::mutex> lock(context->mutex);
    auto &slot = context->slots[static_cast<size_t>(slotIndex)];
    if (!context->ready || context->readySlot != slotIndex ||
        context->readyGeneration != generation || slot.generation != generation)
        return 0;
    if (slot.state != SlotState::Ready) return 0;
    slot.state = SlotState::GPUInFlight;
    context->ready = false;
    context->readySlot = -1;
    context->readyGeneration = 0;
    context->readyFrameNumber = 0;
    return 1;
}

int ReleaseAlphaSlot(Context *context, int slotIndex, uint64_t generation)
{
    if (context == nullptr || slotIndex < 0 || slotIndex >= AlphaSlotCount) return -1;
    std::lock_guard<std::mutex> lock(context->mutex);
    auto &slot = context->slots[static_cast<size_t>(slotIndex)];
    if (slot.generation != generation) return -1;
    if (slot.state != SlotState::Ready && slot.state != SlotState::GPUInFlight) return 0;
    slot.state = SlotState::Free;
    if (context->readySlot == slotIndex && context->readyGeneration == generation)
    {
        context->ready = false;
        context->readySlot = -1;
        context->readyGeneration = 0;
        context->readyFrameNumber = 0;
    }
    return 1;
}

} // namespace rvm
