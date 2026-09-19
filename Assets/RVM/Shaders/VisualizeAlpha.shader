Shader "Hidden/RVM/VisualizeAlpha"
{
    Properties
    {
        _MainTex("Alpha", 2D) = "black" {}
    }

HLSLINCLUDE

#include "UnityCG.cginc"

sampler2D _MainTex;

void VertBlit(float4 position : POSITION,
              float2 texCoord : TEXCOORD0,
              out float4 outPosition : SV_Position,
              out float2 outTexCoord : TEXCOORD0)
{
    outPosition = UnityObjectToClipPos(position);
    outTexCoord = texCoord;
}

float4 FragVisualize(float4 position : SV_Position,
                     float2 texCoord : TEXCOORD0) : SV_Target
{
    texCoord.y = 1 - texCoord.y;
    float alpha = tex2D(_MainTex, texCoord).r;
    return float4(alpha, alpha, alpha, 1);
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
            Name "VisualizeAlphaPass"
            HLSLPROGRAM
            #pragma vertex VertBlit
            #pragma fragment FragVisualize
            ENDHLSL
        }
    }
}
