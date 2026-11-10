// Engine exhaust (Core/ShipView, content/engines.txtpb): additive flame on ShipView's plume mesh, one per jet.
// The mesh is a unit tube along +Z (radius 1 at the nozzle, z 0..1) plus a quad at the nozzle (uv2.x = 1) that
// faces the camera as a glow. The jet's transform scales it to the nozzle radius (x, y) and the full flame
// length (z); _Thrust (per renderer, 0..1, more with boost) sets how far it reaches and how bright it burns.
//
// Tube: brightest along the axis, fading towards its rim as seen from the camera (so it reads as a volume from
// any side), hotter core colour near the nozzle cooling to the edge colour at the tail, simplex turbulence
// flowing out, shock diamonds (bright bands) at high thrust, a quick flicker. Halo: a soft round glow on the
// nozzle, which is what you see from straight behind, where the tube is edge-on.
Shader "LuckyNumbers/Exhaust"
{
    Properties
    {
        _CoreColor ("Core Colour", Color) = (0.75, 0.9, 1, 1)
        _EdgeColor ("Edge Colour", Color) = (0.15, 0.35, 1, 1)
        _Intensity ("Intensity", Float) = 2
        _Softness ("Rim Softness", Float) = 1.5
        _Falloff ("Length Falloff", Float) = 1.4
        _Taper ("Tail Radius (x nozzle)", Float) = 0.3
        _NoiseScale ("Noise Scale", Float) = 6
        _FlowSpeed ("Flow Speed", Float) = 8
        _Turbulence ("Turbulence", Range(0, 1)) = 0.45
        _Diamonds ("Shock Diamonds", Float) = 5
        _DiamondStrength ("Diamond Strength", Range(0, 1)) = 0.5
        _HaloSize ("Halo Size (x nozzle)", Float) = 2.5
        _HaloIntensity ("Halo Intensity", Float) = 1.2
        _Flicker ("Flicker", Range(0, 1)) = 0.12
        _Thrust ("Thrust (set at runtime)", Float) = 1
        _Seed ("Seed (set at runtime)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Blend One One
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/Noise.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CoreColor, _EdgeColor;
                float _Intensity, _Softness, _Falloff, _Taper, _NoiseScale, _FlowSpeed, _Turbulence;
                float _Diamonds, _DiamondStrength, _HaloSize, _HaloIntensity, _Flicker, _Thrust, _Seed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;   // tube: (around 0..1, along 0..1); halo: corner -1..1
                float2 kind : TEXCOORD1; // x: 0 tube, 1 halo
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float kind : TEXCOORD3;
            };

            // How far the flame reaches (share of its full length): a stub at idle, all of it at full thrust,
            // longer with boost (_Thrust > 1).
            float Reach() { return 0.12 + 0.88 * pow(saturate(_Thrust), 0.8) + max(_Thrust - 1.0, 0.0); }

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.uv = v.uv;
                o.kind = v.kind.x;
                if (v.kind.x > 0.5)
                {
                    // Halo: a camera-facing quad on the nozzle, sized from the nozzle's world radius.
                    float3 centre = TransformObjectToWorld(float3(0, 0, 0.02));
                    float radius = length(float3(UNITY_MATRIX_M._m00, UNITY_MATRIX_M._m10, UNITY_MATRIX_M._m20));
                    float size = radius * _HaloSize * (0.6 + 0.4 * saturate(_Thrust));
                    float3 right = UNITY_MATRIX_V._m00_m01_m02, up = UNITY_MATRIX_V._m10_m11_m12;
                    o.positionWS = centre + (right * v.uv.x + up * v.uv.y) * size;
                    o.normalWS = -UNITY_MATRIX_V._m20_m21_m22;
                }
                else
                {
                    // Tube: stretch along z with thrust; the radius narrows to _Taper at the tail (or swells, > 1).
                    float z = v.positionOS.z * Reach();
                    float t = v.positionOS.z;
                    float radius = lerp(1.0, _Taper, t) * (1.0 + 0.15 * sin(t * 3.14159));
                    float3 p = float3(v.positionOS.xy * radius, z);
                    o.positionWS = TransformObjectToWorld(p);
                    // Outward normal of the tapered tube (the slope tilts it along z).
                    float slope = (_Taper - 1.0) / max(Reach(), 1e-3);
                    o.normalWS = TransformObjectToWorldNormal(normalize(float3(v.positionOS.xy, -slope)));
                }
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float thrust = saturate(_Thrust);
                float time = _Time.y + _Seed * 17.0;
                float flicker = 1.0 - _Flicker * (0.5 + 0.5 * ln_simplex(float2(time * 23.0, _Seed * 7.0)));
                float boost = 1.0 + 0.5 * max(_Thrust - 1.0, 0.0);
                float3 color;
                if (i.kind > 0.5)
                {
                    // Halo: soft round glow, core colour in the middle.
                    float r2 = dot(i.uv, i.uv);
                    float g = exp(-r2 * 5.0) * saturate(1.0 - r2);
                    color = lerp(_EdgeColor.rgb, _CoreColor.rgb, saturate(g * 1.5)) * g * _HaloIntensity * (0.35 + 0.65 * thrust);
                }
                else
                {
                    float along = i.uv.y;
                    float3 n = normalize(i.normalWS);
                    float3 view = normalize(GetCameraPositionWS() - i.positionWS);
                    // Facing the camera = looking through the middle of the flame; edge-on = its rim.
                    float facing = saturate(abs(dot(n, view)));
                    float body = pow(facing, _Softness);
                    float fade = pow(saturate(1.0 - along), _Falloff) * smoothstep(0.0, 0.04, along + 0.02);
                    // Turbulence flowing out of the nozzle; around the tube it wraps (uv.x is 0..1 around).
                    float a = i.uv.x * 6.28318;
                    float2 st = float2(cos(a) * 1.5 + _Seed * 3.0, along * _NoiseScale - time * _FlowSpeed);
                    float churn = ln_simplex(st) * 0.6 + ln_simplex(st * 2.3 + 5.1) * 0.4;
                    float turb = 1.0 + _Turbulence * churn;
                    // Shock diamonds: standing bands, brighter at high thrust, fading down the flame.
                    float bands = 0.5 + 0.5 * cos(along * _Diamonds * 6.28318);
                    float diamonds = 1.0 + _DiamondStrength * thrust * thrust * pow(bands, 4.0) * (1.0 - along) * step(0.001, _Diamonds);
                    // Hot near the nozzle and on the axis, cooling outwards and down the tail.
                    float heat = saturate(facing * facing * (1.0 - along * 1.2) * (0.6 + 0.4 * thrust) * boost);
                    float3 tint = lerp(_EdgeColor.rgb, _CoreColor.rgb, heat);
                    color = tint * body * fade * turb * diamonds * (0.25 + 0.75 * thrust);
                }
                color *= _Intensity * flicker * boost;
                return float4(max(color, 0.0), 1.0);
            }
            ENDHLSL
        }
    }
}
