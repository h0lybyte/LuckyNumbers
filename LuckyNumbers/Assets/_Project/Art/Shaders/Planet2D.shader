// 2D planet on a quad: fBm surface, rotation, directional light/shadow, atmosphere rim.
// Ported from "Simple 2D Planet shader" by Ikkemcwood (https://www.shadertoy.com/view/Wcf3W7), see CREDITS.md.
// Port changes: quad UVs instead of fragCoord; asin input clamped (no NaN outside the disc);
// reversed smoothstep edges replaced with ln_smoothstep_down (undefined in WGSL); premultiplied alpha.
Shader "LuckyNumbers/Planet2D"
{
    Properties
    {
        _PlanetColor ("Planet Color", Color) = (1, 0.34, 0, 1)
        _AtmosphereColor ("Atmosphere Color", Color) = (0.41, 0.6, 1, 1)
        _LightColor ("Light Color", Color) = (1, 1, 1, 1)
        _PlanetSize ("Planet Size", Range(0.1, 1)) = 0.9
        _EdgeSmoothness ("Edge Smoothness", Range(0, 0.1)) = 0.02
        _AtmosphereThickness ("Atmosphere Thickness", Range(0, 0.1)) = 0.01
        _RotationSpeed ("Rotation Speed", Float) = 0.03
        _RotationAxis ("Rotation Axis", Float) = -0.2
        _LightOrigin ("Light Origin (xy)", Vector) = (-0.1, 0.1, 0, 0)
        _LightShadowHardness ("Light/Shadow Hardness", Range(0, 1)) = 0.5
        _LightSize ("Light Size", Float) = 2
        _LightIntensity ("Light Intensity", Float) = 0.9
        _ShadowSize ("Shadow Size", Float) = 1.8
        _ShadowIntensity ("Shadow Intensity", Float) = 1
        _NoiseOffset ("Noise Offset (xy, per-planet variation)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend One OneMinusSrcAlpha
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
                float4 _PlanetColor, _AtmosphereColor, _LightColor;
                float _PlanetSize, _EdgeSmoothness, _AtmosphereThickness;
                float _RotationSpeed, _RotationAxis;
                float4 _LightOrigin;
                float _LightShadowHardness, _LightSize, _LightIntensity, _ShadowSize, _ShadowIntensity;
                float4 _NoiseOffset;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float2 Spherify(float2 uv)
            {
                uv = (uv - 0.5) * 2.0;
                float r = length(uv / _PlanetSize);
                uv *= asin(min(r, 1.0)) / (max(r, 1e-5) * sqrt(2.0));
                return uv;
            }

            float4 frag(Varyings i) : SV_Target
            {
                // Same framing as the original's square viewport.
                float2 uv = (i.uv - 0.5) * (2.0 - _PlanetSize) - (1.0 - _PlanetSize) * 0.5 + 0.5;
                float2 light = _LightOrigin.xy;

                float2 sphericUV = Spherify(uv);
                float dist = length(uv - 0.5);

                float planet = ln_smoothstep_down(0.459 * _PlanetSize - _EdgeSmoothness, 0.46 * _PlanetSize, dist);
                float shadow = ln_smoothstep_down(0.459 * _LightShadowHardness * _ShadowSize, 0.46 * _ShadowSize,
                                                  length(uv - light));
                float lit = ln_smoothstep_down(0.459 * _LightShadowHardness * _LightSize, 0.46 * _LightSize,
                                               length(uv + light - 1.0));

                float atmosphere = ln_smoothstep_down(0.459 * _PlanetSize, 0.46 * _PlanetSize + _AtmosphereThickness, dist);
                atmosphere *= 1.0 - planet;
                atmosphere *= 1.0 - shadow * _ShadowIntensity;
                atmosphere *= 1.0 + lit * _LightIntensity;

                float2 surfaceUV = ln_rotate(sphericUV, _RotationAxis) + float2(_Time.y * _RotationSpeed, 0.0) + _NoiseOffset.xy;
                float4 color = float4(_PlanetColor.rgb * ln_fbm(surfaceUV, 7, 1.0, 0.7, 2.0), 1.0);

                color.rgb += lit * _LightIntensity * _LightColor.rgb;
                color = saturate(color);
                color.rgb -= shadow * _ShadowIntensity;

                color *= planet;                       // premultiplied: rgb and alpha masked together
                color += _AtmosphereColor * atmosphere;
                return saturate(color);
            }
            ENDHLSL
        }
    }
}
