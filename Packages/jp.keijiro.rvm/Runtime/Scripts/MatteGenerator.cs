using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Rvm
{

[MovedFrom(true, "RVM", "RVM.Runtime", "RVMProcessor")]
public sealed partial class MatteGenerator : MonoBehaviour
{
    // Public properties

    [field:SerializeField]
    public Texture Input { get; set; }

    [field:SerializeField]
    public RenderTexture Output { get; set; }

    public double InferenceTime { get; private set; }

    [field:SerializeField]
    public ComputeUnits ComputeUnits { get; set; } = ComputeUnits.All;

    [field:SerializeField]
    public float InputRotation { get; set; }

    [field:SerializeField]
    public bool InputMirrorY { get; set; }

    public bool IsReady =>
        _plugin != IntPtr.Zero && _inputTexture != null && _alphaTextures != null;

    public string LastError { get; private set; }

    // Public methods

    public bool Process(Texture input) => ProcessFrame(input);

    public void Reset()
    {
        _stateGeneration++;
        _resetRequested = true;
    }
}

} // namespace Rvm
