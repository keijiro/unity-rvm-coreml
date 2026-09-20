Shader "Hidden/RVM/DemoTintedComposite"
{
    Properties
    {
        _MainTex("Output", 2D) = "black" {}
        _ColorTex("Presented Color", 2D) = "black" {}
        _Tint("Background Tint", Color) = (0.1, 0.55, 0.85, 0.65)
    }

HLSLINCLUDE

#include "UnityCG.cginc"

sampler2D _MainTex;
sampler2D _ColorTex;
float4 _Tint;

void VertBlit(float4 position : POSITION,
              float2 texCoord : TEXCOORD0,
              out float4 outPosition : SV_Position,
              out float2 outTexCoord : TEXCOORD0)
{
    outPosition = UnityObjectToClipPos(position);
    outTexCoord = texCoord;
}

float3 SamplePresentedColor(float2 texCoord)
{
    // Presented Color contains the model-space result of the preprocess pass.
    // Match the Y conversion performed by the RVM output shader before display.
    texCoord.y = 1 - texCoord.y;
    return tex2D(_ColorTex, texCoord).rgb;
}

float4 FragInput(float4 position : SV_Position,
                 float2 texCoord : TEXCOORD0) : SV_Target
{
    return float4(SamplePresentedColor(texCoord), 1);
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
    float alpha = tex2D(_MainTex, texCoord).a;
    float3 input = SamplePresentedColor(texCoord);
    float3 background = lerp(input, _Tint.rgb, _Tint.a);
    return float4(lerp(background, input, alpha), 1);
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
