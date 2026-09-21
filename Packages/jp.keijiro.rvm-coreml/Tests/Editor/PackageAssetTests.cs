using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Rvm.CoreML.Tests.Editor
{

public sealed class PackageAssetTests
{
    const string PackagePath = "Packages/jp.keijiro.rvm-coreml";
    const string PreprocessShaderPath = PackagePath + "/Runtime/Shaders/Preprocess.shader";
    const string OutputShaderPath = PackagePath + "/Runtime/Shaders/VisualizeAlpha.shader";
    const string PluginPath = PackagePath + "/Runtime/Plugins/macOS/RVMPlugin.bundle";
    const string ModelPackagePath =
        "Runtime/Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";
    const string ModelSha256 =
        "68efe6e7a23d5337fb4f935f77e83b0ec3cc823803083953eb18f4cc0549d794";

    [TestCase(PreprocessShaderPath)]
    [TestCase(OutputShaderPath)]
    public void ShaderCompilesWithoutErrors(string path)
    {
        var shader = LoadAsset<Shader>(path);
        var errors = ShaderUtil.GetShaderMessages(shader)
            .Where(message => message.severity == ShaderCompilerMessageSeverity.Error)
            .Select(message => message.message)
            .ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    [Test]
    public void NativePluginImporterTargetsMacOS()
    {
        var importer = AssetImporter.GetAtPath(PluginPath) as PluginImporter;
        Assert.That(importer, Is.Not.Null);
        Assert.That(importer.GetCompatibleWithAnyPlatform(), Is.False);
        Assert.That(importer.GetCompatibleWithEditor(), Is.True);
        Assert.That(
            importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX),
            Is.True
        );
    }

    [Test]
    public void ModelMatchesExpectedDigest()
    {
        var package = PackageInfo.FindForAssembly(typeof(MatteGenerator).Assembly);
        Assert.That(package, Is.Not.Null, "The RVM Core ML package could not be resolved.");
        var path = Path.Combine(package.resolvedPath, ModelPackagePath);
        Assert.That(File.Exists(path), Is.True, $"The RVM model is missing: {path}");

        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = string.Concat(sha256.ComputeHash(stream).Select(value => value.ToString("x2")));
        Assert.That(hash, Is.EqualTo(ModelSha256));
    }

    static T LoadAsset<T>(string path) where T : Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        Assert.That(asset, Is.Not.Null, $"{path} could not be loaded as {typeof(T).Name}.");
        return asset;
    }
}

} // namespace Rvm.CoreML.Tests.Editor
