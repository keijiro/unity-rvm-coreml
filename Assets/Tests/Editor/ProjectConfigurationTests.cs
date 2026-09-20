using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Rvm;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace RVM.Tests.Editor
{

public sealed class ProjectConfigurationTests
{
    const string MainScenePath = "Assets/Main.unity";
    const string MainUIPath = "Assets/UI/Main.uxml";
    const string PanelSettingsPath = "Assets/UI/DefaultSettings.asset";
    const string OutputTexturePath = "Assets/RVM/Runtime/RVMMatte.renderTexture";
    const string CompositeShaderPath = "Assets/RVM/Runtime/TintedComposite.shader";
    const string PackagePath = "Packages/jp.keijiro.rvm";
    const string PreprocessShaderPath = PackagePath + "/Runtime/Shaders/Preprocess.shader";
    const string OutputShaderPath = PackagePath + "/Runtime/Shaders/VisualizeAlpha.shader";
    const string PluginPath = PackagePath + "/Runtime/Plugins/macOS/RVMPlugin.bundle";
    const string ModelPath = PackagePath +
        "/Runtime/Models/rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel";
    const string ModelSha256 =
        "68efe6e7a23d5337fb4f935f77e83b0ec3cc823803083953eb18f4cc0549d794";
    const int InputWidth = 1280;
    const int InputHeight = 720;

    [Test]
    public void MainUIHasExpectedElements()
    {
        var root = LoadAsset<VisualTreeAsset>(MainUIPath).Instantiate();
        AssertElement<DropdownField>(root, "sourceDropdown");
        AssertElement<Image>(root, "cameraImage");
        AssertElement<Image>(root, "alphaImage");
        AssertElement<Image>(root, "compositeImage");
        AssertElement<Label>(root, "statusLabel");
    }

    [TestCase(PreprocessShaderPath)]
    [TestCase(OutputShaderPath)]
    [TestCase(CompositeShaderPath)]
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
    public void MainSceneIsEnabledForBuild()
    {
        Assert.That(
            EditorBuildSettings.scenes.Any(scene =>
                scene.enabled && scene.path == MainScenePath),
            Is.True,
            $"{MainScenePath} is not enabled in the build settings."
        );
    }

    [Test]
    public void MainSceneHasCompleteDemoController()
    {
        var controller = OpenDemoController();
        Assert.That(controller.isActiveAndEnabled, Is.True);
        Assert.That(controller.GetComponent<PanelRenderer>(), Is.Not.Null);
        Assert.That(controller.GetComponent<VideoPlayer>(), Is.Not.Null);
        Assert.That(controller.GetComponent<MatteGenerator>(), Is.Not.Null);
        Assert.That(controller.GetComponent<MatteGenerator>().enabled, Is.True);
    }

    [Test]
    public void DemoControllerReferencesExpectedAssets()
    {
        var controller = OpenDemoController();
        var demo = new SerializedObject(controller);
        var panel = new SerializedObject(controller.GetComponent<PanelRenderer>());
        var generator = new SerializedObject(controller.GetComponent<MatteGenerator>());

        AssertAssetReference(demo, "_compositeShader", CompositeShaderPath);
        AssertAssetReference(panel, "sourceAsset", MainUIPath);
        AssertAssetReference(panel, "m_PanelSettings", PanelSettingsPath);
        AssertAssetReference(generator, "_preprocessShader", PreprocessShaderPath);
        AssertAssetReference(generator, "_outputShader", OutputShaderPath);
    }

    [Test]
    public void DemoOutputMatchesDisplayContract()
    {
        var output = OpenDemoController().GetComponent<MatteGenerator>().Output;
        Assert.That(output, Is.Not.Null);
        Assert.That(AssetDatabase.GetAssetPath(output), Is.EqualTo(OutputTexturePath));
        Assert.That(output.width, Is.EqualTo(InputWidth));
        Assert.That(output.height, Is.EqualTo(InputHeight));

        // An alpha-capable output contains source RGB and stores the matte only in A.
        // The demo pane needs the no-alpha mode, which displays the matte as grayscale.
        Assert.That(GraphicsFormatUtility.HasAlphaChannel(output.graphicsFormat), Is.False);
    }

    [Test]
    public void MainSceneHasActiveCamera()
    {
        var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        var camera = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Camera>())
            .FirstOrDefault();
        Assert.That(camera, Is.Not.Null, $"{MainScenePath} has no active camera.");
    }

    [Test]
    public void MacOSPlayerSettingsMatchRuntimeRequirements()
    {
        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
        Assert.That(
            EditorUserBuildSettings.activeBuildTarget,
            Is.EqualTo(BuildTarget.StandaloneOSX)
        );
        Assert.That(apis, Is.EqualTo(new[] { GraphicsDeviceType.Metal }));
        Assert.That(
            Version.TryParse(PlayerSettings.macOS.targetOSVersion, out var version) &&
            version >= new Version(13, 0),
            Is.True,
            "The macOS deployment target must be 13.0 or later."
        );
        Assert.That(PlayerSettings.macOS.cameraUsageDescription, Is.Not.Empty);
    }

    [Test]
    public void EditorUsesProjectRenderingConfiguration()
    {
        Assert.That(SystemInfo.graphicsDeviceType, Is.EqualTo(GraphicsDeviceType.Metal));
        Assert.That(
            GraphicsSettings.currentRenderPipeline,
            Is.InstanceOf<UniversalRenderPipelineAsset>()
        );
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
        var path = Path.GetFullPath(ModelPath);
        Assert.That(File.Exists(path), Is.True, $"The RVM model is missing: {path}");

        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = string.Concat(sha256.ComputeHash(stream).Select(value => value.ToString("x2")));
        Assert.That(hash, Is.EqualTo(ModelSha256));
    }

    static RVMDemoController OpenDemoController()
    {
        var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        var controllers = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<RVMDemoController>(true))
            .ToArray();
        Assert.That(
            controllers,
            Has.Length.EqualTo(1),
            $"{MainScenePath} must contain exactly one RVM demo controller."
        );
        return controllers[0];
    }

    static T LoadAsset<T>(string path) where T : UnityEngine.Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        Assert.That(asset, Is.Not.Null, $"{path} could not be loaded as {typeof(T).Name}.");
        return asset;
    }

    static void AssertElement<T>(VisualElement root, string name) where T : VisualElement
    {
        Assert.That(root.Q(name), Is.InstanceOf<T>(), $"UI element '{name}' has the wrong type.");
    }

    static void AssertAssetReference(
        SerializedObject serializedObject,
        string propertyName,
        string expectedPath
    )
    {
        var property = serializedObject.FindProperty(propertyName);
        var actualPath = property == null ? null :
            AssetDatabase.GetAssetPath(property.objectReferenceValue);
        Assert.That(
            actualPath,
            Is.EqualTo(expectedPath),
            $"{serializedObject.targetObject.GetType().Name}.{propertyName} has the wrong asset."
        );
    }
}

} // namespace RVM.Tests.Editor
