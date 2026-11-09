// Procedural skybox: three star layers + faint fBm nebula on the celestial sphere, slow drift.
// Use as RenderSettings.skybox with the camera clearing to Skybox.
Shader "LuckyNumbers/Starfield"
{
    Properties
    {
        _SpaceColor ("Space Color", Color) = (0.01, 0.01, 0.03, 1)
        _NebulaColorA ("Nebula Color A", Color) = (0.25, 0.08, 0.35, 1)
        _NebulaColorB ("Nebula Color B", Color) = (0.05, 0.15, 0.35, 1)
        _NebulaIntensity ("Nebula Intensity", Range(0, 1)) = 0.35
        _NebulaScale ("Nebula Scale", Float) = 1.2
        _StarDensity ("Star Density", Float) = 140
        _StarBrightness ("Star Brightness", Float) = 1.2
        _TwinkleSpeed ("Twinkle Speed", Float) = 2
        _Drift ("Drift (radians/s around y, x)", Vector) = (0.003, 0, 0, 0)
        _Seed ("Seed", Float) = 7
    }

    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
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
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }

            float3 Drift(float3 d, float t)
            {
                float a = _Drift.x * t, b = _Drift.y * t;
                float sa = sin(a), ca = cos(a), sb = sin(b), cb = cos(b);
                d = float3(d.x * ca - d.z * sa, d.y, d.x * sa + d.z * ca);       // around y
                return float3(d.x, d.y * cb - d.z * sb, d.y * sb + d.z * cb);     // around x
            }

            // One star layer: a candidate star per 3D grid cell near the sphere, random size/brightness.
            float StarLayer(float3 dir, float density, float seed, float t)
            {
                float3 g = dir * density;
                float3 cell = floor(g);
                float h = ln_hash31(cell + seed);
                if (h < 0.9) return 0.0;                                     // ~10% of cells hold a star
                float3 pos = ln_hash33(cell + seed + 1.7);
                float d = length(frac(g) - pos);
                float size = lerp(0.04, 0.14, ln_hash31(cell + seed + 4.1));
                float core = ln_smoothstep_down(0.0, size, d);
                float twinkle = 0.6 + 0.4 * sin(t * _TwinkleSpeed * (0.5 + h) + h * 6.2831);
                return core * twinkle * (h - 0.9) / 0.1;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float t = _Time.y;
                float3 dir = Drift(normalize(i.dir), t);

                float n  = ln_fbm(dir * _NebulaScale + _Seed, 5, 0.5, 0.55, 2.0) * 0.5 + 0.5;
                float n2 = ln_fbm(dir * _NebulaScale * 1.7 - _Seed + 3.1, 3, 0.5, 0.5, 2.0) * 0.5 + 0.5;
                float3 nebula = lerp(_NebulaColorA.rgb, _NebulaColorB.rgb, saturate(n2)) * pow(saturate(n), 3.0) * _NebulaIntensity;

                float stars = StarLayer(dir, _StarDensity, _Seed, t) * 0.6
                            + StarLayer(dir, _StarDensity * 0.6, _Seed + 11.0, t) * 0.8
                            + StarLayer(dir, _StarDensity * 0.35, _Seed + 23.0, t);

                return float4(_SpaceColor.rgb + nebula + stars * _StarBrightness, 1.0);
            }
            ENDHLSL
        }
    }
}
