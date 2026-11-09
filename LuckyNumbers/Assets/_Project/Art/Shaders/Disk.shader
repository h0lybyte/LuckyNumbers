// Dust disk around a forming star, on a flat quad in the orbital plane (quad uv 0..1 = disk diameter).
// Clumps are fBm in a rotated frame where the rotation angle grows towards the centre (differential
// rotation, ~r^-1.5 like Kepler orbits), so they shear into spiral streaks. Planets clear dark gaps at
// their orbits (_Gaps0/_Gaps1: radii as share of the disk radius, 0 = unused). _Fade is set at runtime.
Shader "LuckyNumbers/Disk"
{
    Properties
    {
        _InnerColor ("Inner Color", Color) = (1, 0.85, 0.6, 1)
        _OuterColor ("Outer Color", Color) = (0.55, 0.25, 0.12, 1)
        _Brightness ("Brightness", Float) = 1.2
        _InnerRadius ("Inner Radius", Range(0, 0.5)) = 0.03
        _ClumpScale ("Clump Scale", Float) = 6
        _Clumpiness ("Clumpiness", Range(0, 1)) = 0.7
        _SpinSpeed ("Spin Speed", Float) = 0.25
        _GapDepth ("Gap Depth", Range(0, 1)) = 0.85
        _GapWidth ("Gap Width", Float) = 0.025
        _Seed ("Seed", Float) = 1
        _Fade ("Fade", Range(0, 1)) = 1
        _Gaps0 ("Gaps 0-3", Vector) = (0, 0, 0, 0)
        _Gaps1 ("Gaps 4-7", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/Noise.hlsl"

            float4 _LnSunColor; // global: the star's light colour (set by SpacePresenter)

            CBUFFER_START(UnityPerMaterial)
                float4 _InnerColor, _OuterColor;
                float _Brightness, _InnerRadius, _ClumpScale, _Clumpiness, _SpinSpeed, _GapDepth, _GapWidth, _Seed, _Fade;
                float4 _Gaps0, _Gaps1;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 p : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.p = (v.uv - 0.5) * 2.0;                                       // -1..1, r = 1 at the edge
                return o;
            }

            float Gap(float r, float g)
            {
                if (g <= 0.0) return 1.0;
                float d = (r - g) / max(_GapWidth, 1e-3);
                return 1.0 - _GapDepth * exp(-d * d);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float r = length(i.p);
                if (r > 1.0 || _Fade <= 0.0) return 0;

                // Differential rotation: inner gas laps the outer gas.
                float angle = _Time.y * _SpinSpeed / pow(max(r, 0.05), 1.5);
                float2 q = ln_rotate(i.p, angle);
                float clumps = ln_fbm(q * _ClumpScale + _Seed * 7.1, 4, 0.5, 0.5, 2.0) * 0.5 + 0.5;
                float streaks = ln_fbm(float2(r * _ClumpScale * 3.0, clumps * 2.0) + _Seed, 2, 0.5, 0.5, 2.0) * 0.5 + 0.5;
                float dust = lerp(1.0, saturate(clumps * 1.4 - 0.2) * (0.6 + 0.6 * streaks), _Clumpiness);

                float profile = smoothstep(_InnerRadius, _InnerRadius * 2.0 + 0.02, r)   // hole at the star
                              * ln_smoothstep_down(0.7, 1.0, r)                          // soft outer edge
                              / (0.15 + 2.5 * r);                                       // lit from the centre

                float gaps = Gap(r, _Gaps0.x) * Gap(r, _Gaps0.y) * Gap(r, _Gaps0.z) * Gap(r, _Gaps0.w)
                           * Gap(r, _Gaps1.x) * Gap(r, _Gaps1.y) * Gap(r, _Gaps1.z) * Gap(r, _Gaps1.w);

                float3 color = lerp(_InnerColor.rgb, _OuterColor.rgb, saturate(sqrt(r)));
                color *= lerp(float3(1, 1, 1), _LnSunColor.rgb, 0.65);          // lit by its star
                return float4(color * dust * profile * gaps * _Brightness * _Fade, 0);
            }
            ENDHLSL
        }
    }
}
