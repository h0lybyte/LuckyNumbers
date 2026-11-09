// Procedural skybox, everything on the celestial sphere and driven by _Seed:
//  - galactic band: a great circle (normal = _BandNormal) where stars, glow and gas gather, like the Milky Way
//  - nebula: domain-warped fBm gas in three colours, brighter fronts where the gas thins
//  - dust lanes: ridged noise darkening the band
//  - emission knots: a few bright blooms inside the gas
//  - three star layers, denser in the band
// Use as RenderSettings.skybox with the camera clearing to Skybox.
Shader "LuckyNumbers/Starfield"
{
    Properties
    {
        _SpaceColor ("Space Color", Color) = (0.01, 0.01, 0.03, 1)
        _NebulaColorA ("Nebula Color A", Color) = (0.25, 0.08, 0.35, 1)
        _NebulaColorB ("Nebula Color B", Color) = (0.05, 0.15, 0.35, 1)
        _NebulaColorC ("Nebula Color C", Color) = (0.6, 0.2, 0.25, 1)
        _NebulaIntensity ("Nebula Intensity", Range(0, 2)) = 0.6
        _NebulaScale ("Nebula Scale", Float) = 1.2
        _Warp ("Warp", Float) = 1.2
        _BandNormal ("Galactic Band Normal", Vector) = (0.3, 1, 0.2, 0)
        _BandWidth ("Band Width", Float) = 0.35
        _BandStrength ("Band Strength (0 = even sky)", Range(0, 1)) = 0.8
        _Glow ("Band Glow", Float) = 0.08
        _Dust ("Dust Lanes", Range(0, 1)) = 0.6
        _KnotDensity ("Knot Density", Float) = 6
        _KnotBrightness ("Knot Brightness", Float) = 0.8
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
                float4 _SpaceColor, _NebulaColorA, _NebulaColorB, _NebulaColorC;
                float _NebulaIntensity, _NebulaScale, _Warp;
                float4 _BandNormal;
                float _BandWidth, _BandStrength, _Glow, _Dust, _KnotDensity, _KnotBrightness;
                float _StarDensity, _StarBrightness, _TwinkleSpeed;
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
            float StarLayer(float3 dir, float density, float seed, float t, float keep)
            {
                float3 g = dir * density;
                float3 cell = floor(g);
                float h = ln_hash31(cell + seed);
                if (h < keep) return 0.0;                                    // share (1 - keep) of cells hold a star
                float3 pos = ln_hash33(cell + seed + 1.7);
                float d = length(frac(g) - pos);
                float size = lerp(0.04, 0.14, ln_hash31(cell + seed + 4.1));
                float core = ln_smoothstep_down(0.0, size, d);
                float twinkle = 0.6 + 0.4 * sin(t * _TwinkleSpeed * (0.5 + h) + h * 6.2831);
                return core * twinkle * saturate((h - keep) / (1.0 - keep) + 0.3);
            }

            // Bright blooms: one candidate per coarse cell, kept only where there is gas.
            float Knots(float3 dir, float gas)
            {
                float3 g = dir * _KnotDensity;
                float3 cell = floor(g);
                float h = ln_hash31(cell + _Seed * 0.37 + 5.3);
                if (h < 0.8) return 0.0;
                float3 pos = 0.25 + 0.5 * ln_hash33(cell + _Seed * 0.37 + 9.1);
                float d = length(frac(g) - pos);
                float size = lerp(0.05, 0.16, ln_hash31(cell + 2.9));
                return exp(-(d * d) / (size * size)) * gas;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float t = _Time.y;
                float3 dir = Drift(normalize(i.dir), t);

                // Galactic band: 1 on the plane, falling off with galactic latitude.
                float3 bn = normalize(_BandNormal.xyz + float3(0, 1e-4, 0));
                float lat = dot(dir, bn);
                float band = exp(-(lat * lat) / max(_BandWidth * _BandWidth, 1e-4));
                float region = lerp(1.0, band, _BandStrength);                // where gas and stars may gather

                // Domain-warped gas.
                float3 p = dir * _NebulaScale + _Seed;
                float3 q = float3(ln_fbm(p, 3, 0.5, 0.5, 2.0),
                                  ln_fbm(p + float3(5.2, 1.3, 2.8), 3, 0.5, 0.5, 2.0),
                                  ln_fbm(p + float3(9.7, 4.1, 7.3), 3, 0.5, 0.5, 2.0));
                float3 pw = p + _Warp * q;
                float density = ln_fbm(pw, 5, 0.5, 0.55, 2.0) * 0.5 + 0.5;
                float gas = smoothstep(0.5, 0.95, density * (0.5 + 0.7 * region)) * region;
                gas *= gas * (3.0 - 2.0 * gas);                               // more contrast: clouds and gaps
                float front = gas * (1.0 - gas) * 4.0;                        // glowing edges of the clouds

                float3 hue = saturate(q * 0.9 + 0.5);
                float3 color = lerp(lerp(_NebulaColorA.rgb, _NebulaColorB.rgb, hue.x), _NebulaColorC.rgb, hue.y * hue.y);
                float3 nebula = color * (gas + front * 0.5) * _NebulaIntensity;

                // Unresolved starlight along the band.
                float3 glow = lerp(float3(1.0, 0.92, 0.85), color, 0.4) * band * band * _Glow * (0.4 + 1.2 * density);

                // Dust lanes: broad soft ridges of low-frequency noise, strongest in the band.
                float ridge = abs(ln_fbm(pw * 1.3 + 17.0, 3, 0.5, 0.5, 2.0));
                float lane = 1.0 - smoothstep(0.0, 0.45, ridge);
                float dust = 1.0 - _Dust * band * lane * lane;

                float3 knots = lerp(color, float3(1, 1, 1), 0.35) * Knots(dir, gas) * _KnotBrightness;

                float bandStars = 0.6 + 1.4 * band * _BandStrength;
                float stars = StarLayer(dir, _StarDensity, _Seed, t, 0.9) * 0.6
                            + StarLayer(dir, _StarDensity * 0.6, _Seed + 11.0, t, 0.9) * 0.8
                            + StarLayer(dir, _StarDensity * 0.35, _Seed + 23.0, t, 0.93)
                            + StarLayer(dir, _StarDensity * 1.6, _Seed + 31.0, t, lerp(0.99, 0.86, band)) * 0.35;

                float3 sky = _SpaceColor.rgb + (nebula + glow) * dust + knots * dust
                           + stars * _StarBrightness * bandStars * lerp(1.0, dust, 0.6);
                return float4(sky, 1.0);
            }
            ENDHLSL
        }
    }
}
