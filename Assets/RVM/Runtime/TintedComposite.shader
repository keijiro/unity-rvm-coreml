Shader "Hidden/RVM/DemoTintedComposite"
{
    Properties
    {
        _MainTex("Input", 2D) = "black" {}
        _MatteTex("Matte", 2D) = "black" {}
        _Tint("Background Tint", Color) = (0.1, 0.55, 0.85, 0.65)
    }

HLSLINCLUDE

#include "UnityCG.cginc"

sampler2D _MainTex;
sampler2D _MatteTex;
float4 _Tint;

void VertBlit(float4 position : POSITION,
              float2 texCoord : TEXCOORD0,
              out float4 outPosition : SV_Position,
              out float2 outTexCoord : TEXCOORD0)
{
    outPosition = UnityObjectToClipPos(position);
    outTexCoord = texCoord;
}

float4 FragComposite(float4 position : SV_Position,
                     float2 texCoord : TEXCOORD0) : SV_Target
{
    float matte = tex2D(_MatteTex, texCoord).r;
    float3 input = tex2D(_MainTex, float2(texCoord.x, 1 - texCoord.y)).rgb;
    float3 background = lerp(input, _Tint.rgb, _Tint.a);
    return float4(lerp(background, input, matte), 1);
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
            Name "CompositePass"
            HLSLPROGRAM
            #pragma vertex VertBlit
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}
