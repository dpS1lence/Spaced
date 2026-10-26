// The Sun: an HDR disk with a soft corona, far behind everything; bloom turns it into glare.
// Drawn before the Earth without writing depth, so the planet eclipses it naturally.
Shader "Spaced/Sun"
{
    Properties
    {
        _DiskIntensity ("Disk Intensity", Float) = 60
        _GlowIntensity ("Glow Intensity", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Background+10" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Sun"
            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _DiskIntensity;
                float _GlowIntensity;
            CBUFFER_END

            static const float QuadAngle = 0.12;      // radians covered by the quad's half-width
            static const float DiskAngle = 0.00465;   // the Sun's angular radius

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 corner : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float4 clip = TransformWorldToHClip(GetCameraPositionWS() + _SpacedSunDir * 15000.0);
                float2 scale = float2(UNITY_MATRIX_P[0][0], UNITY_MATRIX_P[1][1]) * tan(QuadAngle);
                clip.xy += input.corner * scale * clip.w;
                output.positionCS = clip;
                output.corner = input.corner;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float angle = length(input.corner) * QuadAngle;
                float disk = 1.0 - smoothstep(DiskAngle * 0.9, DiskAngle * 1.05, angle);
                float glow = exp(-angle / 0.012) * 0.6 + exp(-angle / 0.045) * 0.08;
                float3 color = float3(1.0, 0.95, 0.88) * (disk * _DiskIntensity + glow * _GlowIntensity);
                return half4(color * saturate(1.0 - length(input.corner)), 1);
            }
            ENDHLSL
        }
    }
}
