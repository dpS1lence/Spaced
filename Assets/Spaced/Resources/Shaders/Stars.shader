// Background stars. Each star is a small quad whose position is a direction (POSITION) and corner (TEXCOORD0);
// it is placed at a fixed distance from the camera so stars never show parallax.
Shader "Spaced/Stars"
{
    Properties
    {
        _SizePx ("Size (px)", Float) = 1.4
        _Distance ("Distance", Float) = 2000
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Background" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Stars"
            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SizePx;
                float _Distance;
            CBUFFER_END

            struct Attributes
            {
                float3 direction : POSITION;
                float2 corner : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                half4 color : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float4 clip = TransformWorldToHClip(GetCameraPositionWS() + normalize(input.direction) * _Distance);
                float size = _SizePx * (0.6 + input.color.a);
                clip.xy += input.corner * size * 2.0 / _ScreenParams.xy * clip.w;
                output.positionCS = clip;
                output.corner = input.corner;
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float d = length(input.corner);
                return half4(input.color.rgb, input.color.a * saturate(1.0 - d));
            }
            ENDHLSL
        }
    }
}
