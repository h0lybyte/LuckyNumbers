// Star for a sphere mesh: boiling fBm surface with limb darkening, plus an additive corona shell.
// The first star in the universe also lights the planets (SpacePresenter sets _LnSunPosition/_LnSunColor).
Shader "LuckyNumbers/Star"
{
    Properties
    {
        _CoreColor ("Core Color", Color) = (1, 0.78, 0.42, 1)
        _CoronaColor ("Corona Color", Color) = (1, 0.55, 0.2, 1)
        _LightColor ("Light Color (lights planets)", Color) = (1, 0.95, 0.85, 1)
        _Intensity ("Intensity", Float) = 1.6
        _CoronaSize ("Corona Size (share of radius)", Range(0, 4)) = 1.2
        _CoronaPower ("Corona Power", Float) = 2.2
        _CoronaIntensity ("Corona Intensity", Float) = 1.3
        _NoiseScale ("Noise Scale", Float) = 3
        _BoilSpeed ("Boil Speed", Float) = 0.05
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Include/Noise.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _CoreColor, _CoronaColor, _LightColor;
            float _Intensity, _CoronaSize, _CoronaPower, _CoronaIntensity, _NoiseScale, _BoilSpeed;
        CBUFFER_END

        struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 normalOS   : TEXCOORD0;
            float3 normalWS   : TEXCOORD1;
            float3 viewWS     : TEXCOORD2;
        };

        Varyings Vert(Attributes v, float inflate)
        {
            Varyings o;
            float3 pos = v.positionOS.xyz + normalize(v.normalOS) * length(v.positionOS.xyz) * inflate;
            float3 ws = TransformObjectToWorld(pos);
            o.positionCS = TransformWorldToHClip(ws);
            o.normalOS = v.normalOS;
            o.normalWS = TransformObjectToWorldNormal(v.normalOS);
            o.viewWS = GetWorldSpaceViewDir(ws);
            return o;
        }
        ENDHLSL

        // Corona: big inflated back-face shell, additive, fading out from the surface.
        Pass
        {
            Name "Corona"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One One
            ZWrite Off
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            Varyings vert(Attributes v) { return Vert(v, _CoronaSize); }

            float4 frag(Varyings i) : SV_Target
            {
                float rim = pow(saturate(abs(dot(normalize(i.normalWS), normalize(i.viewWS)))), _CoronaPower);
                float flicker = 0.9 + 0.1 * ln_simplex(normalize(i.normalOS) * 4.0 + _Time.y * _BoilSpeed * 4.0);
                return float4(_CoronaColor.rgb * rim * flicker * _CoronaIntensity, 0);
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
            Varyings vert(Attributes v) { return Vert(v, 0.0); }

            float4 frag(Varyings i) : SV_Target
            {
                float3 p = normalize(i.normalOS) * _NoiseScale + _Time.y * _BoilSpeed * float3(1.0, 0.7, 0.3);
                float h = saturate(ln_fbm(p, 4, 0.5, 0.55, 2.0) * 0.5 + 0.5);
                float mu = saturate(dot(normalize(i.normalWS), normalize(i.viewWS)));
                float limb = 0.55 + 0.45 * mu;                                  // darker towards the edge
                float3 color = _CoreColor.rgb * _Intensity * lerp(0.7, 1.2, h) * limb;
                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
