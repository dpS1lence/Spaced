// Draws every object as a constant-pixel-size dot in one procedural draw call.
// _Positions holds (xyz relative to the floating origin, w = category + radius_m / 1000).
// w < 0 means "do not draw". Dots dim in Earth's shadow and fade out once the real object is
// bigger on screen than its dot (the 3D model takes over there).
Shader "Spaced/Satellites"
{
    Properties
    {
        _PointSize ("Point Radius (px)", Float) = 2.2
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Satellites"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            StructuredBuffer<float4> _Positions;
            float4 _Palette[16];
            float _PointSize;
            float _ProjScale;
            int _SelectedIndex;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                float ring : TEXCOORD1;
                half4 color : COLOR;
            };

            static const float2 Corners[6] =
            {
                float2(-1, -1), float2(1, -1), float2(1, 1),
                float2(-1, -1), float2(1, 1), float2(-1, 1)
            };

            Varyings Hidden(Varyings output)
            {
                output.positionCS = float4(2, 2, 2, 1); // outside the clip volume: culled
                output.color = 0;
                return output;
            }

            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings output;
                float4 data = _Positions[instanceID];
                float2 corner = Corners[vertexID % 6];
                output.corner = corner;
                output.ring = 0;
                output.color = 0;
                if (data.w < 0) return Hidden(output);

                int category = (int)floor(data.w);
                float radiusUnits = frac(data.w) * 1000.0 / 1.0e6; // metres → 1000 km units
                half4 color = (half4)_Palette[min(category, 15)];
                if (color.a <= 0) return Hidden(output);

                float size = _PointSize;
                if (category == 3 || category == 9) size *= 1.8; // stations, rockets
                bool selected = (int)instanceID == _SelectedIndex;

                float distance = length(data.xyz - _WorldSpaceCameraPos);
                float trueRadiusPx = radiusUnits / max(distance, 1e-9) * _ProjScale;
                float fade = 1.0 - smoothstep(size * 0.6, size * 2.0, trueRadiusPx);
                if (fade <= 0.001) return Hidden(output);

                if (selected)
                {
                    size = max(size * 4.0, 9.0);
                    output.ring = 1;
                    color = half4(1.0, 0.9, 0.25, 1.0);
                }
                else
                {
                    // Satellites in Earth's shadow are much fainter, as seen from the ground.
                    float lit = SpacedEarthShadow(data.xyz - _SpacedEarthCenterFar, _SpacedEarthRadiusFar);
                    color.rgb *= lerp(0.28, 1.0, lit);
                }
                color.a *= fade;

                float4 clip = TransformWorldToHClip(data.xyz);
                clip.xy += corner * size * 2.0 / _ScreenParams.xy * clip.w;
                output.positionCS = clip;
                output.color = color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float d = length(input.corner);
                if (d > 1.0) discard;
                float alpha = input.ring > 0.5
                    ? saturate(1.0 - abs(d - 0.75) * 8.0) + saturate(1.0 - d * 4.0)
                    : saturate((1.0 - d) * 2.0);
                return half4(input.color.rgb, input.color.a * alpha);
            }
            ENDHLSL
        }
    }
}
