#import "RVMAlphaTexturePool.hpp"

#import "RVMModel.hpp"

#import <Metal/Metal.h>

#include <algorithm>
#include <array>

namespace rvm
{
namespace
{

// Three surfaces allow inference, Unity GPU sampling, and the next available output
// to overlap without Core ML overwriting a texture that Unity still owns.
constexpr int SlotCount = 3;

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

    // A fence from an older use of this slot must not release storage that has since
    // wrapped around to a newer frame.
    uint64_t generation = 0;
};

} // namespace

struct AlphaTexturePool::Impl
{
    CVMetalTextureCacheRef textureCache = nullptr;
    std::array<AlphaSlot, SlotCount> slots;
    int nextSlot = 0;

    ~Impl()
    {
        for (auto &slot : slots)
        {
            slot.texture = nil;
            if (slot.textureView != nullptr) CFRelease(slot.textureView);
            if (slot.pixelBuffer != nullptr) CFRelease(slot.pixelBuffer);
        }
        if (textureCache != nullptr) CFRelease(textureCache);
    }
};

AlphaTexturePool::AlphaTexturePool() : _impl(std::make_unique<Impl>())
{
}

AlphaTexturePool::~AlphaTexturePool() = default;

bool AlphaTexturePool::Initialize(std::string &error)
{
    // Unity's device must be preferred so the native texture handles belong to the
    // same Metal device as the renderer. The fallback also supports validation tools
    // that create the plugin without invoking UnityPluginLoad first.
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
        &_impl->textureCache
    );
    if (status != kCVReturnSuccess || _impl->textureCache == nullptr)
    {
        error = "Could not create the Core Video Metal texture cache.";
        return false;
    }

    NSDictionary *attributes = @{
        (id)kCVPixelBufferMetalCompatibilityKey: @YES,
        (id)kCVPixelBufferIOSurfacePropertiesKey: @{}
    };
    for (auto &slot : _impl->slots)
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
            _impl->textureCache,
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

        // textureView owns the CV-to-Metal relationship. The strong Objective-C
        // reference keeps the exported id<MTLTexture> alive for Unity as well.
        slot.texture = CVMetalTextureGetTexture(slot.textureView);
        if (slot.texture == nil)
        {
            error = "The alpha texture view did not contain a Metal texture.";
            return false;
        }
    }
    return true;
}

int AlphaTexturePool::GetSlotCount() const
{
    return SlotCount;
}

int AlphaTexturePool::GetTextureInfo(
    int slotIndex,
    int *width,
    int *height,
    void **nativeTexture
) const
{
    if (slotIndex < 0 || slotIndex >= SlotCount) return -1;
    const auto &slot = _impl->slots[static_cast<size_t>(slotIndex)];
    if (slot.texture == nil) return -1;
    if (width != nullptr) *width = InputWidth;
    if (height != nullptr) *height = InputHeight;
    if (nativeTexture != nullptr) *nativeTexture = (__bridge void *)slot.texture;
    return 1;
}

bool AlphaTexturePool::HasFreeSlot() const
{
    return std::any_of(
        _impl->slots.begin(),
        _impl->slots.end(),
        [](const auto &slot) { return slot.state == SlotState::Free; }
    );
}

int AlphaTexturePool::Acquire()
{
    // Start after the previous allocation so a fast producer does not repeatedly
    // favor the lowest numbered slot once several GPU fences complete together.
    for (auto offset = 0; offset < SlotCount; offset++)
    {
        auto index = (_impl->nextSlot + offset) % SlotCount;
        auto &slot = _impl->slots[static_cast<size_t>(index)];
        if (slot.state != SlotState::Free) continue;
        slot.state = SlotState::Inferencing;
        slot.generation++;
        _impl->nextSlot = (index + 1) % SlotCount;
        return index;
    }
    return -1;
}

void AlphaTexturePool::Cancel(int slotIndex)
{
    if (slotIndex < 0 || slotIndex >= SlotCount) return;
    _impl->slots[static_cast<size_t>(slotIndex)].state = SlotState::Free;
}

void AlphaTexturePool::MarkReady(int slotIndex)
{
    _impl->slots[static_cast<size_t>(slotIndex)].state = SlotState::Ready;
}

CVPixelBufferRef AlphaTexturePool::GetPixelBuffer(int slotIndex) const
{
    if (slotIndex < 0 || slotIndex >= SlotCount) return nullptr;
    return _impl->slots[static_cast<size_t>(slotIndex)].pixelBuffer;
}

uint64_t AlphaTexturePool::GetGeneration(int slotIndex) const
{
    if (slotIndex < 0 || slotIndex >= SlotCount) return 0;
    return _impl->slots[static_cast<size_t>(slotIndex)].generation;
}

int AlphaTexturePool::MarkGPUInFlight(int slotIndex, uint64_t generation)
{
    if (slotIndex < 0 || slotIndex >= SlotCount) return -1;
    auto &slot = _impl->slots[static_cast<size_t>(slotIndex)];
    if (slot.generation != generation || slot.state != SlotState::Ready) return 0;
    slot.state = SlotState::GPUInFlight;
    return 1;
}

int AlphaTexturePool::Release(int slotIndex, uint64_t generation)
{
    if (slotIndex < 0 || slotIndex >= SlotCount) return -1;
    auto &slot = _impl->slots[static_cast<size_t>(slotIndex)];
    if (slot.generation != generation) return -1;
    if (slot.state != SlotState::Ready && slot.state != SlotState::GPUInFlight) return 0;
    slot.state = SlotState::Free;
    return 1;
}

void SetMetalDevice(id<MTLDevice> device)
{
    s_MetalDevice = device;
}

} // namespace rvm
