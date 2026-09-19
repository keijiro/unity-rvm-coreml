#pragma once

#import <CoreVideo/CoreVideo.h>

#include "RVMContext.hpp"

#include <cstdint>
#include <memory>
#include <string>

namespace rvm
{

constexpr int InputWidth = 1280;
constexpr int InputHeight = 720;

class Model final
{
public:
    Model();
    ~Model();

    Model(const Model &) = delete;
    Model &operator=(const Model &) = delete;

    bool Load(const char *path, ComputeUnits computeUnits, std::string &error);
    bool Predict(
        CVPixelBufferRef input,
        CVPixelBufferRef alphaOutput,
        double &inferenceMilliseconds,
        std::string &error
    );
    void ResetState();

private:
    struct Impl;
    std::unique_ptr<Impl> _impl;
};

CVPixelBufferRef CreateInputPixelBuffer(
    const uint8_t *bgra,
    int width,
    int height,
    int rowBytes
);

} // namespace rvm
