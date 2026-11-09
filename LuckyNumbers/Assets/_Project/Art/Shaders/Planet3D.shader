// 3D planet for a sphere mesh: fBm surface sampled on the sphere, spin around a tilted axis,
// sun lighting with a soft terminator, plus an atmosphere glow shell around the silhouette.
// Look based on "Simple 2D Planet shader" by Ikkemcwood (https://www.shadertoy.com/view/Wcf3W7), see CREDITS.md.
// Lit by the universe's first star: globals _LnSunPosition (xyz, w=1 when set) and _LnSunColor.
Shader "LuckyNumbers/Planet3D"
{
    Properties
    {
        _PlanetColor ("Planet Color", Color) = (1, 0.34, 0, 1)
        _AtmosphereColor ("Atmosphere Color", Color) = (0.41, 0.6, 1, 1)
        _LightColor ("Light Color", Color) = (1, 1, 1, 1)
        _LightIntensity ("Light Intensity", Float) = 0.9
        _ShadowIntensity ("Shadow Intensity", Range(0, 1)) = 1
        _TerminatorSoftness ("Terminator Softness", Range(0.01, 1)) = 0.15
        _RotationSpeed ("Rotation Speed (turns/s)", Float) = 0.01
        _AxialTiltDegrees ("Axial Tilt (degrees)", Float) = -12
        _NoiseScale ("Noise Scale", Float) = 1.6
        _NoiseOffset ("Noise Offset (xyz)", Vector) = (0, 0, 0, 0)
        _NoiseRemap ("Noise Remap (0 original, 1 true colour)", Range(0, 1)) = 0
        _AtmosphereThickness ("Atmosphere Thickness (share of radius)", Range(0, 0.5)) = 0.08
        _AtmosphereIntensity ("Atmosphere Intensity", Float) = 1.4
        _AtmospherePower ("Atmosphere Power", Float) = 2.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Include/Noise.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _PlanetColor, _AtmosphereColor, _LightColor;
            float _LightIntensity, _ShadowIntensity, _TerminatorSoftness;
            float _RotationSpeed, _AxialTiltDegrees, _NoiseScale;
            float4 _NoiseOffset;
            float _NoiseRemap, _AtmosphereThickness, _AtmosphereIntensity, _AtmospherePower;
        CBUFFER_END
        float4 _LnSunPosition, _LnSunColor; // globals set by SpacePresenter

        // Point light at the sun; falls back to a fixed direction when no star exists.
        float3 SunDir(float3 positionWS)
        {
            return _LnSunPosition.w > 0.5 ? normalize(_LnSunPosition.xyz - positionWS) : normalize(float3(0.6, 0.3, -0.7));
        }

        float3 SunColor() { return _LnSunPosition.w > 0.5 ? _LnSunColor.rgb : float3(1, 1, 1); }

        struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 normalOS   : TEXCOORD0;
            float3 normalWS   : TEXCOORD1;
            float3 viewWS     : TEXCOORD2;
            float3 positionWS : TEXCOORD3;
        };
        ENDHLSL

        // Atmosphere: an inflated back-face shell, additive, fading out from the planet's edge.
        Pass
        {
            Name "Atmosphere"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One One
            ZWrite Off
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pos = v.positionOS.xyz + normalize(v.normalOS) * length(v.positionOS.xyz) * _AtmosphereThickness;
                float3 ws = TransformObjectToWorld(pos);
                o.positionCS = TransformWorldToHClip(ws);
                o.normalOS = v.normalOS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceViewDir(ws);
                o.positionWS = ws;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                float rim = pow(saturate(abs(dot(n, v))), _AtmospherePower);   // 0 at the shell's outer edge
                float lit = 0.25 + 0.75 * saturate(dot(n, SunDir(i.positionWS)) * 0.5 + 0.5); // brighter on the day side
                return float4(_AtmosphereColor.rgb * rim * lit * _AtmosphereIntensity, 0);
            }
            ENDHLSL
        }

        // Surface.
        Pass
        {
            Name "Surface"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                o.normalOS = v.normalOS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceViewDir(ws);
                o.positionWS = ws;
                return o;
            }

            float3 Spin(float3 p)
            {
                // Tilt around Z, then spin around the tilted Y axis.
                float t = radians(_AxialTiltDegrees), a = _Time.y * _RotationSpeed * 6.28318;
                float st = sin(t), ct = cos(t), sa = sin(a), ca = cos(a);
                p = float3(p.x * ct + p.y * st, -p.x * st + p.y * ct, p.z);
                return float3(p.x * ca - p.z * sa, p.y, p.x * sa + p.z * ca);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                float ndl = dot(n, SunDir(i.positionWS));
                float day = smoothstep(-_TerminatorSoftness, _TerminatorSoftness, ndl);

                float3 p = Spin(normalize(i.normalOS)) * _NoiseScale + _NoiseOffset.xyz;
                float h = ln_fbm(p, 7, 1.0, 0.7, 2.0);
                h = lerp(h, saturate(h * 0.5 + 0.5), _NoiseRemap);

                float3 color = _PlanetColor.rgb * h;
                color += day * saturate(ndl) * _LightIntensity * _LightColor.rgb * SunColor() * 0.6;
                color = saturate(color) * lerp(1.0 - _ShadowIntensity, 1.0, day);

                float fresnel = pow(1.0 - saturate(dot(n, v)), 3.0);           // thin rim on the disc itself
                color += _AtmosphereColor.rgb * fresnel * (0.2 + 0.8 * day) * 0.6;
                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
