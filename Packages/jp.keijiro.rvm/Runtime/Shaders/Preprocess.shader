Shader "Hidden/RVM/Preprocess"
{
    Properties
    {
        _MainTex("Source", 2D) = "white" {}
        _TargetAspect("Target Aspect", Float) = 1.7777778
    }

HLSLINCLUDE

#include "UnityCG.cginc"

sampler2D _MainTex;
float4 _MainTex_TexelSize;
float4 _SourceSize;
float _Rotation;
float _MirrorY;
float _TargetAspect;

void VertBlit(float4 position : POSITION,
              float2 texCoord : TEXCOORD0,
              out float4 outPosition : SV_Position,
              out float2 outTexCoord : TEXCOORD0)
{
    outPosition = UnityObjectToClipPos(position);
    outTexCoord = texCoord;
}

float2 RotateToSource(float2 uv)
{
    if (_Rotation < 45) return uv;
    if (_Rotation < 135) return float2(uv.y, 1 - uv.x);
    if (_Rotation < 225) return 1 - uv;
    if (_Rotation < 315) return float2(1 - uv.y, uv.x);
    return uv;
}

float4 FragPreprocess(float4 position : SV_Position,
                      float2 texCoord : TEXCOORD0) : SV_Target
{
    texCoord.y = 1 - texCoord.y;
    float rotation = fmod(_Rotation + 360, 360);
    bool swapDimensions = rotation > 45 && rotation < 315 &&
                          !(rotation > 135 && rotation < 225);
    float2 size = swapDimensions ? _SourceSize.yx : _SourceSize.xy;
    float aspect = size.x / size.y;
    float2 cropScale = aspect > _TargetAspect ?
                       float2(_TargetAspect / aspect, 1) :
                       float2(1, aspect / _TargetAspect);
    float2 uv = (texCoord - 0.5) * cropScale + 0.5;
    uv = RotateToSource(uv);
    if (_MirrorY > 0.5) uv.y = 1 - uv.y;
#if UNITY_UV_STARTS_AT_TOP
    if (_MainTex_TexelSize.y < 0) uv.y = 1 - uv.y;
#endif
    return tex2D(_MainTex, uv);
}

ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "PreprocessPass"
            HLSLPROGRAM
            #pragma vertex VertBlit
            #pragma fragment FragPreprocess
            ENDHLSL
        }
    }
}
