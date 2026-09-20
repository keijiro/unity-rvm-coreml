Shader "Hidden/RVM/Output"
{
    Properties
    {
        _MainTex("Alpha", 2D) = "black" {}
        _ColorTex("Color", 2D) = "black" {}
        _OutputHasAlpha("Output Has Alpha", Float) = 0
    }

HLSLINCLUDE

#include "UnityCG.cginc"

sampler2D _MainTex;
sampler2D _ColorTex;
float _OutputHasAlpha;

void VertBlit(float4 position : POSITION,
              float2 texCoord : TEXCOORD0,
              out float4 outPosition : SV_Position,
              out float2 outTexCoord : TEXCOORD0)
{
    outPosition = UnityObjectToClipPos(position);
    outTexCoord = texCoord;
}

float4 FragOutput(float4 position : SV_Position,
                  float2 texCoord : TEXCOORD0) : SV_Target
{
    texCoord.y = 1 - texCoord.y;
    float alpha = tex2D(_MainTex, texCoord).r;
    if (_OutputHasAlpha > 0.5)
        return float4(tex2D(_ColorTex, texCoord).rgb, alpha);
    return alpha.xxxx;
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
            Name "OutputPass"
            HLSLPROGRAM
            #pragma vertex VertBlit
            #pragma fragment FragOutput
            ENDHLSL
        }
    }
}
