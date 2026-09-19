#import "RVMModel.hpp"

#import <CoreML/CoreML.h>
#import <Foundation/Foundation.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cstring>

namespace rvm
{
namespace
{

constexpr int RecurrentCount = 4;

// Names and shapes describe the exact converted model contract, not arbitrary RVM
// variants. Load rejects incompatible models before the first asynchronous request.
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

    // Core ML may omit a fixed output shape from the model description, so outputs
    // accept an empty constraint and are checked again against actual prediction data.
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

    // Compilation is expensive enough to disrupt iteration in the Editor. A stable
    // app-specific cache keeps that cost out of subsequent context creations.
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

    // Another context or process may have populated the cache after the initial
    // existence check. Its completed directory is equivalent to our compiled copy.
    if ([manager fileExistsAtPath:cachedURL.path isDirectory:&isDirectory] && isDirectory)
    {
        if (error != nullptr) *error = nil;
        return cachedURL;
    }
    return nil;
}

} // namespace

struct Model::Impl
{
    __strong MLModel *model = nil;
    __strong NSArray<MLMultiArray *> *recurrentStates = nil;
};

Model::Model() : _impl(std::make_unique<Impl>())
{
}

Model::~Model() = default;

bool Model::Load(const char *path, ComputeUnits computeUnits, std::string &error)
{
    if (path == nullptr)
    {
        error = "The model path is null.";
        return false;
    }

    MLComputeUnits mlComputeUnits;
    if (!TryGetMLComputeUnits(computeUnits, mlComputeUnits))
    {
        error = "Invalid Core ML compute units value.";
        return false;
    }

    NSError *loadError = nil;
    auto modelURL = ResolveModelURL([NSString stringWithUTF8String:path], &loadError);
    if (modelURL == nil)
    {
        error = ErrorString(loadError);
        return false;
    }

    auto configuration = [MLModelConfiguration new];
    configuration.computeUnits = mlComputeUnits;
    auto model = [MLModel modelWithContentsOfURL:modelURL
                                  configuration:configuration
                                          error:&loadError];
    if (model == nil)
    {
        error = ErrorString(loadError);
        return false;
    }
    if (!ValidateModelDescription(model, error)) return false;

    _impl->model = model;
    _impl->recurrentStates = nil;
    return true;
}

bool Model::Predict(
    CVPixelBufferRef input,
    CVPixelBufferRef alphaOutput,
    double &inferenceMilliseconds,
    std::string &error
)
{
    NSError *predictionError = nil;
    auto sourceValue = [MLFeatureValue featureValueWithPixelBuffer:input];
    auto inputs = [NSMutableDictionary<NSString *, id> dictionaryWithObject:sourceValue
                                                                     forKey:@"src"];
    if (_impl->recurrentStates != nil)
    {
        // Recurrent inputs are optional only for the first frame. Thereafter each
        // successful prediction feeds its validated outputs into the next request.
        for (auto index = 0; index < RecurrentCount; index++)
        {
            auto state = _impl->recurrentStates[static_cast<NSUInteger>(index)];
            inputs[InputStateNames[static_cast<size_t>(index)]] =
                [MLFeatureValue featureValueWithMultiArray:state];
        }
    }

    auto provider = [[MLDictionaryFeatureProvider alloc]
        initWithDictionary:inputs
        error:&predictionError];
    if (provider == nil)
    {
        error = ErrorString(predictionError);
        return false;
    }

    // Binding Core ML directly to the pool's IOSurface avoids a full-frame copy and
    // lets Unity sample the same storage through its Metal texture view.
    auto options = [MLPredictionOptions new];
    options.outputBackings = @{ @"pha": (__bridge id)alphaOutput };
    auto start = std::chrono::steady_clock::now();
    auto prediction = [_impl->model predictionFromFeatures:provider
                                             options:options
                                               error:&predictionError];
    auto end = std::chrono::steady_clock::now();
    if (prediction == nil)
    {
        error = ErrorString(predictionError);
        return false;
    }

    auto alpha = [prediction featureValueForName:@"pha"].imageBufferValue;
    if (alpha != alphaOutput)
    {
        error = "Core ML rejected the requested alpha output backing.";
        return false;
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
            error = "The model returned an invalid recurrent state.";
            return false;
        }
        [nextStates addObject:state];
    }

    // Commit all four states together only after validation, so a malformed result
    // cannot partially advance the temporal state used by the following frame.
    _impl->recurrentStates = [nextStates copy];
    inferenceMilliseconds =
        std::chrono::duration<double, std::milli>(end - start).count();
    return true;
}

void Model::ResetState()
{
    _impl->recurrentStates = nil;
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

    // Core Video may pad destination rows. Copy only active BGRA pixels and advance
    // source and destination independently to preserve both row-stride contracts.
    for (auto y = 0; y < height; y++)
    {
        auto sourceRow = bgra + y * rowBytes;
        auto destinationRow = destination + y * destinationRowBytes;
        std::memcpy(destinationRow, sourceRow, static_cast<size_t>(width * 4));
    }
    CVPixelBufferUnlockBaseAddress(buffer, 0);
    return buffer;
}

} // namespace rvm
