using System.IO;
using UnityEditor.Build;
using UnityEditor.PackageManager;

namespace RVM.Editor
{

internal sealed class RVMBuildPlayerProcessor : BuildPlayerProcessor
{
    const string ModelPackagePath =
        "Runtime/Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";
    const string ModelStreamingAssetsPath =
        "Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";

    public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
    {
        var package = PackageInfo.FindForAssembly(
            typeof(RVMBuildPlayerProcessor).Assembly
        );
        var sourcePath = Path.Combine(package.resolvedPath, ModelPackagePath);
        buildPlayerContext.AddAdditionalPathToStreamingAssets(
            sourcePath,
            ModelStreamingAssetsPath
        );
    }
}

} // namespace RVM.Editor
