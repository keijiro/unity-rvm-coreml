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
    [SerializeField, HideInInspector] Shader _preprocessShader = null;

    Material _compositeMaterial;
    Material _preprocessMaterial;
    RenderTexture _presentedColorTexture;
    RenderTexture _presentedMatteTexture;
    RenderTexture _inputDisplayTexture;
    RenderTexture _alphaDisplayTexture;
    RenderTexture _compositeTexture;
    bool _displayDirty;

    void InitializeOutputDisplay()
    {
        if (_compositeShader == null) return;
        _compositeMaterial = new Material(_compositeShader);
        _compositeMaterial.SetColor("_Tint", BackgroundTint);
        if (_preprocessShader != null)
            _preprocessMaterial = new Material(_preprocessShader);
    }

    void UpdateOutputDisplay()
    {
        if (!_displayDirty || _compositeMaterial == null ||
            _presentedColorTexture == null || _presentedMatteTexture == null)
            return;

        _displayDirty = false;
        _compositeMaterial.SetTexture("_ColorTex", _presentedColorTexture);
        Graphics.Blit(
            _presentedMatteTexture,
            _inputDisplayTexture,
            _compositeMaterial,
            InputPass
        );
        Graphics.Blit(
            _presentedMatteTexture,
            _alphaDisplayTexture,
            _compositeMaterial,
            AlphaPass
        );
        Graphics.Blit(
            _presentedMatteTexture,
            _compositeTexture,
            _compositeMaterial,
            CompositePass
        );

        if (_cameraImage != null) _cameraImage.image = _inputDisplayTexture;
        if (_alphaImage != null) _alphaImage.image = _alphaDisplayTexture;
        if (_compositeImage != null) _compositeImage.image = _compositeTexture;
        _cameraImage?.MarkDirtyRepaint();
        _alphaImage?.MarkDirtyRepaint();
        _compositeImage?.MarkDirtyRepaint();
    }

    void EnsureDisplayTextures(int width, int height)
    {
        if (MatchesSize(_presentedColorTexture, width, height) &&
            MatchesSize(_presentedMatteTexture, width, height) &&
            MatchesSize(_inputDisplayTexture, width, height) &&
            MatchesSize(_alphaDisplayTexture, width, height) &&
            MatchesSize(_compositeTexture, width, height))
            return;

        ReleaseDisplayTextures();
        _presentedColorTexture = CreateDisplayTexture(width, height, "Presented Color");
        _presentedMatteTexture = CreateDisplayTexture(width, height, "Presented Matte");
        _inputDisplayTexture = CreateDisplayTexture(width, height, "RVM Model Input");
        _alphaDisplayTexture = CreateDisplayTexture(width, height, "RVM Alpha Matte");
        _compositeTexture = CreateDisplayTexture(width, height, "RVM Tinted Composite");
        ClearTexture(_presentedColorTexture);
        ClearTexture(_presentedMatteTexture);
    }

    void PresentColor(Texture source, int width, int height)
    {
        if (_preprocessMaterial == null) return;

        EnsureDisplayTextures(width, height);
        _preprocessMaterial.SetVector(
            "_SourceSize",
            new Vector4(source.width, source.height, 0, 0)
        );
        _preprocessMaterial.SetFloat("_TargetAspect", (float)width / height);
        Graphics.Blit(source, _presentedColorTexture, _preprocessMaterial);
        _displayDirty = true;
    }

    void PresentMatte(Texture source, int width, int height)
    {
        EnsureDisplayTextures(width, height);
        Graphics.Blit(source, _presentedMatteTexture);
        _displayDirty = true;
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
        Destroy(_preprocessMaterial);
        _compositeMaterial = null;
        _preprocessMaterial = null;
    }

    void ReleaseDisplayTextures()
    {
        ReleaseTexture(ref _presentedColorTexture);
        ReleaseTexture(ref _presentedMatteTexture);
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
