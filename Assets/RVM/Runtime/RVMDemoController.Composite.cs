using UnityEngine;

namespace RVM
{

public sealed partial class RVMDemoController
{
    static readonly Color BackgroundTint = new(0.1f, 0.55f, 0.85f, 0.65f);

    [SerializeField, HideInInspector] Shader _compositeShader = null;

    Material _compositeMaterial;
    RenderTexture _compositeTexture;

    void InitializeComposite()
    {
        if (_compositeShader == null) return;
        _compositeMaterial = new Material(_compositeShader);
        _compositeMaterial.SetColor("_Tint", BackgroundTint);
    }

    void UpdateComposite()
    {
        var input = _generator?.ModelInput;
        var matte = _generator?.Output;
        if (input == null || matte == null || _compositeMaterial == null) return;

        EnsureCompositeTexture(input.width, input.height);
        _compositeMaterial.SetTexture("_MatteTex", matte);
        Graphics.Blit(input, _compositeTexture, _compositeMaterial);
        if (_compositeImage == null) return;
        _compositeImage.image = _compositeTexture;
        _compositeImage.MarkDirtyRepaint();
    }

    void EnsureCompositeTexture(int width, int height)
    {
        if (_compositeTexture != null && _compositeTexture.width == width &&
            _compositeTexture.height == height)
            return;

        ReleaseCompositeTexture();
        _compositeTexture = new RenderTexture(
            width,
            height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB
        )
        {
            name = "RVM Tinted Composite",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _compositeTexture.Create();
    }

    void ReleaseComposite()
    {
        ReleaseCompositeTexture();
        Destroy(_compositeMaterial);
        _compositeMaterial = null;
    }

    void ReleaseCompositeTexture()
    {
        if (_compositeTexture == null) return;
        _compositeTexture.Release();
        Destroy(_compositeTexture);
        _compositeTexture = null;
    }
}

} // namespace RVM
