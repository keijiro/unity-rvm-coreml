Shader "Hidden/RVM/DemoTintedComposite"
{
    Properties
    {
        _MainTex("Output", 2D) = "black" {}
        _Tint("Background Tint", Color) = (0.1, 0.55, 0.85, 0.65)
    }

HLSLINCLUDE

#include "UnityCG.cginc"

sampler2D _MainTex;
float4 _Tint;

void VertBlit(float4 position : POSITION,
              float2 texCoord : TEXCOORD0,
              out float4 outPosition : SV_Position,
              out float2 outTexCoord : TEXCOORD0)
{
    outPosition = UnityObjectToClipPos(position);
    outTexCoord = texCoord;
}

float4 FragInput(float4 position : SV_Position,
                 float2 texCoord : TEXCOORD0) : SV_Target
{
    return float4(tex2D(_MainTex, texCoord).rgb, 1);
}

float4 FragAlpha(float4 position : SV_Position,
                 float2 texCoord : TEXCOORD0) : SV_Target
{
    float alpha = tex2D(_MainTex, texCoord).a;
    return alpha.xxxx;
}

float4 FragComposite(float4 position : SV_Position,
                     float2 texCoord : TEXCOORD0) : SV_Target
{
    float4 output = tex2D(_MainTex, texCoord);
    float3 input = output.rgb;
    float3 background = lerp(input, _Tint.rgb, _Tint.a);
    return float4(lerp(background, input, output.a), 1);
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
            Name "InputPass"
            HLSLPROGRAM
            #pragma vertex VertBlit
            #pragma fragment FragInput
            ENDHLSL
        }

        Pass
        {
            Name "AlphaPass"
            HLSLPROGRAM
            #pragma vertex VertBlit
            #pragma fragment FragAlpha
            ENDHLSL
        }

        Pass
        {
            Name "CompositePass"
            HLSLPROGRAM
            #pragma vertex VertBlit
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}
