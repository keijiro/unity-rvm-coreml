#pragma once

#import <CoreVideo/CoreVideo.h>

#include <cstdint>
#include <memory>
#include <string>

namespace rvm
{

class AlphaTexturePool final
{
public:
    AlphaTexturePool();
    ~AlphaTexturePool();

    AlphaTexturePool(const AlphaTexturePool &) = delete;
    AlphaTexturePool &operator=(const AlphaTexturePool &) = delete;

    bool Initialize(std::string &error);
    int GetSlotCount() const;
    int GetTextureInfo(int slotIndex, int *width, int *height, void **nativeTexture) const;

    // Context owns the mutex because slot transitions and its ready-result metadata
    // must change atomically. Pool methods intentionally do not lock independently.
    bool HasFreeSlot() const;
    int Acquire();
    void Cancel(int slotIndex);
    void MarkReady(int slotIndex);
    CVPixelBufferRef GetPixelBuffer(int slotIndex) const;
    uint64_t GetGeneration(int slotIndex) const;
    int MarkGPUInFlight(int slotIndex, uint64_t generation);
    int Release(int slotIndex, uint64_t generation);

private:
    struct Impl;
    std::unique_ptr<Impl> _impl;
};

} // namespace rvm
