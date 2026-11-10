// The crash site's ground: the same colour formula as Planet3D (planet_color * fBm height), at walking
// scale, lit by the sun (globals _LnSunPosition / _LnSunColor), with atmosphere haze over distance.
// _PlanetColor, _AtmosphereColor, _NoiseOffset, _NoiseRemap come from the planet's PlanetLook (set in code).
// Far ring (GroundView): _Sink lowers vertices inside the square of detailed chunks (_NearCentre = centre xz,
// half-size, blend band), so the coarse horizon mesh never pokes through them; past _HazeHorizon the ground
// is all atmosphere, so the ring's edge never shows.
Shader "LuckyNumbers/Ground"
{
    Properties
    {
        _PlanetColor ("Planet Color", Color) = (0.5, 0.5, 0.5, 1)
        _AtmosphereColor ("Atmosphere Color", Color) = (0.4, 0.6, 1, 1)
        _NoiseOffset ("Noise Offset", Vector) = (0, 0, 0, 0)
        _NoiseRemap ("Noise Remap", Range(0, 1)) = 1
        _SurfaceScale ("Surface Noise Scale", Float) = 0.35
        _Haze ("Haze", Range(0, 1)) = 0.5
        _HazeDistance ("Haze Distance", Float) = 90
        _HazeHorizon ("Haze Horizon (fully hazed)", Float) = 0
        _Sink ("Sink Under Near Chunks", Float) = 0
        _NearCentre ("Near Square (x, z, half, band)", Vector) = (0, 0, 0, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/Noise.hlsl"

            float4 _LnSunPosition, _LnSunColor; // globals

            CBUFFER_START(UnityPerMaterial)
                float4 _PlanetColor, _AtmosphereColor, _NoiseOffset;
                float _NoiseRemap, _SurfaceScale, _Haze, _HazeDistance, _HazeHorizon, _Sink;
                float4 _NearCentre;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                if (_Sink > 0.0)
                {
                    float d = max(abs(o.positionWS.x - _NearCentre.x), abs(o.positionWS.z - _NearCentre.y));
                    o.positionWS.y -= _Sink * (1.0 - smoothstep(_NearCentre.z - _NearCentre.w, _NearCentre.z, d));
                }
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 l = _LnSunPosition.w > 0.5 ? normalize(_LnSunPosition.xyz - i.positionWS) : normalize(float3(0.4, 0.8, 0.3));
                float3 sun = _LnSunPosition.w > 0.5 ? _LnSunColor.rgb : float3(1, 1, 1);

                float3 p = i.positionWS * _SurfaceScale + _NoiseOffset.xyz;
                float h = ln_fbm(p, 6, 1.0, 0.6, 2.0);
                h = lerp(h, saturate(h * 0.5 + 0.5), _NoiseRemap);
                float grit = ln_fbm(i.positionWS * _SurfaceScale * 6.0, 2, 1.0, 0.5, 2.0) * 0.035;  // close-up detail
                // Calmer than from orbit: less contrast, a little desaturated (dust), so the ground isn't busy.
                float3 albedo = saturate(_PlanetColor.rgb * (0.55 + 0.45 * h) + grit);
                albedo = lerp(albedo, dot(albedo, float3(0.3, 0.59, 0.11)).xxx, 0.3);

                float diffuse = saturate(dot(n, l));
                float3 ambient = _AtmosphereColor.rgb * 0.28 + 0.04;                                 // sky light
                float3 color = albedo * (sun * diffuse * 0.95 + ambient);

                float dist = distance(GetCameraPositionWS(), i.positionWS);
                float haze = _Haze * saturate(dist / max(_HazeDistance, 1.0));
                color = lerp(color, _AtmosphereColor.rgb * 0.7, haze * haze);
                if (_HazeHorizon > 0.0)   // all atmosphere by the far edge
                    color = lerp(color, _AtmosphereColor.rgb * 0.7, smoothstep(_HazeHorizon * 0.45, _HazeHorizon * 0.92, dist));
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
