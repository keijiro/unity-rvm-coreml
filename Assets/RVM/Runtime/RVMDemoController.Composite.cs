using UnityEngine;

namespace RVM
{

public sealed partial class RVMDemoController
{
    const int InputPass = 0;
    const int AlphaPass = 1;
    const int CompositePass = 2;

    static readonly Color BackgroundTint = new(0.1f, 0.55f, 0.85f, 0.65f);

    [SerializeField, HideInInspector] Shader _compositeShader = null;

    Material _compositeMaterial;
    RenderTexture _inputDisplayTexture;
    RenderTexture _alphaDisplayTexture;
    RenderTexture _compositeTexture;

    void InitializeOutputDisplay()
    {
        if (_compositeShader == null) return;
        _compositeMaterial = new Material(_compositeShader);
        _compositeMaterial.SetColor("_Tint", BackgroundTint);
    }

    void UpdateOutputDisplay()
    {
        var output = _generator?.Output;
        if (output == null || _compositeMaterial == null) return;

        // Output contains the submitted RGB and its matching matte in alpha.
        // Deriving every pane from it prevents ModelInput reuse from advancing
        // the displayed color before asynchronous inference has caught up.
        EnsureDisplayTextures(output.width, output.height);
        Graphics.Blit(output, _inputDisplayTexture, _compositeMaterial, InputPass);
        Graphics.Blit(output, _alphaDisplayTexture, _compositeMaterial, AlphaPass);
        Graphics.Blit(output, _compositeTexture, _compositeMaterial, CompositePass);

        if (_cameraImage != null) _cameraImage.image = _inputDisplayTexture;
        if (_alphaImage != null) _alphaImage.image = _alphaDisplayTexture;
        if (_compositeImage != null) _compositeImage.image = _compositeTexture;
        _cameraImage?.MarkDirtyRepaint();
        _alphaImage?.MarkDirtyRepaint();
        _compositeImage?.MarkDirtyRepaint();
    }

    void EnsureDisplayTextures(int width, int height)
    {
        if (MatchesSize(_inputDisplayTexture, width, height) &&
            MatchesSize(_alphaDisplayTexture, width, height) &&
            MatchesSize(_compositeTexture, width, height))
            return;

        ReleaseDisplayTextures();
        _inputDisplayTexture = CreateDisplayTexture(width, height, "RVM Model Input");
        _alphaDisplayTexture = CreateDisplayTexture(width, height, "RVM Alpha Matte");
        _compositeTexture = CreateDisplayTexture(width, height, "RVM Tinted Composite");
    }

    static bool MatchesSize(RenderTexture texture, int width, int height) =>
        texture != null && texture.width == width && texture.height == height;

    static RenderTexture CreateDisplayTexture(int width, int height, string name)
    {
        var texture = new RenderTexture(
            width,
            height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB
        )
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.Create();
        return texture;
    }

    void ReleaseOutputDisplay()
    {
        ReleaseDisplayTextures();
        Destroy(_compositeMaterial);
        _compositeMaterial = null;
    }

    void ReleaseDisplayTextures()
    {
        ReleaseTexture(ref _inputDisplayTexture);
        ReleaseTexture(ref _alphaDisplayTexture);
        ReleaseTexture(ref _compositeTexture);
    }

    static void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Destroy(texture);
        texture = null;
    }
}

} // namespace RVM
