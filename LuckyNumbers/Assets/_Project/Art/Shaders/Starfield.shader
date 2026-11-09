// Procedural space background: parallax star layers with twinkle + drift, faint fBm nebula.
// Screen-space, so it fills any aspect ratio. Put it on a quad behind everything (Queue Background).
Shader "LuckyNumbers/Starfield"
{
    Properties
    {
        _SpaceColor ("Space Color", Color) = (0.01, 0.01, 0.03, 1)
        _NebulaColorA ("Nebula Color A", Color) = (0.25, 0.08, 0.35, 1)
        _NebulaColorB ("Nebula Color B", Color) = (0.05, 0.15, 0.35, 1)
        _NebulaIntensity ("Nebula Intensity", Range(0, 1)) = 0.35
        _NebulaScale ("Nebula Scale", Float) = 1.5
        _StarDensity ("Star Density (cells across)", Float) = 60
        _StarBrightness ("Star Brightness", Float) = 1.2
        _TwinkleSpeed ("Twinkle Speed", Float) = 2
        _Drift ("Drift (xy per second)", Vector) = (0.004, 0.001, 0, 0)
        _Seed ("Seed", Float) = 7
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Background" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/Noise.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _SpaceColor, _NebulaColorA, _NebulaColorB;
                float _NebulaIntensity, _NebulaScale, _StarDensity, _StarBrightness, _TwinkleSpeed;
                float4 _Drift;
                float _Seed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float4 screenPos : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.screenPos = ComputeScreenPos(o.positionCS);
                return o;
            }

            // One layer of stars: one candidate star per grid cell, random position/size/brightness.
            float StarLayer(float2 uv, float density, float seed, float time)
            {
                float2 grid = uv * density;
                float2 cell = floor(grid);
                float2 local = frac(grid) - 0.5;
                float h = ln_hash21(cell + seed);
                if (h < 0.85) return 0.0;                       // ~15% of cells hold a star
                float2 pos = float2(ln_hash21(cell + seed + 1.3), ln_hash21(cell + seed + 2.7)) - 0.5;
                float size = lerp(0.02, 0.08, ln_hash21(cell + seed + 4.1));
                float d = length(local - pos * 0.7);
                float core = ln_smoothstep_down(0.0, size, d);
                float twinkle = 0.6 + 0.4 * sin(time * _TwinkleSpeed * (0.5 + h) + h * 6.2831);
                return core * twinkle * (h - 0.85) / 0.15;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.screenPos.xy / i.screenPos.w;
                uv.x *= _ScreenParams.x / _ScreenParams.y;      // square cells at any aspect
                float t = _Time.y;

                float n = ln_fbm(uv * _NebulaScale + _Seed + _Drift.xy * t * 0.5, 5, 0.5, 0.55, 2.0) * 0.5 + 0.5;
                float n2 = ln_fbm(uv * _NebulaScale * 1.7 - _Seed + 3.1, 4, 0.5, 0.5, 2.0) * 0.5 + 0.5;
                float3 nebula = lerp(_NebulaColorA.rgb, _NebulaColorB.rgb, saturate(n2)) * pow(saturate(n), 3.0) * _NebulaIntensity;

                float stars = 0.0;                              // far -> near: denser, slower
                stars += StarLayer(uv + _Drift.xy * t * 0.3, _StarDensity,        _Seed,        t) * 0.6;
                stars += StarLayer(uv + _Drift.xy * t * 0.6, _StarDensity * 0.6,  _Seed + 11.0, t) * 0.8;
                stars += StarLayer(uv + _Drift.xy * t * 1.0, _StarDensity * 0.35, _Seed + 23.0, t);

                return float4(_SpaceColor.rgb + nebula + stars * _StarBrightness, 1.0);
            }
            ENDHLSL
        }
    }
}
