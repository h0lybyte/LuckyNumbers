// The planet map's hex globe: vertex colours (per cell, set in code), lit by the system's star (globals
// _LnSunPosition / _LnSunColor, set to the map's star while it is open), with an atmosphere rim.
Shader "LuckyNumbers/HexMap"
{
    Properties
    {
        _AtmosphereColor ("Atmosphere Color", Color) = (0.4, 0.6, 1, 1)
        _Rim ("Rim", Float) = 0.6
        _Ambient ("Ambient", Range(0, 1)) = 0.12
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

            float4 _LnSunPosition, _LnSunColor; // globals

            CBUFFER_START(UnityPerMaterial)
                float4 _AtmosphereColor;
                float _Rim, _Ambient;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1; float4 color : COLOR; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 l = _LnSunPosition.w > 0.5 ? normalize(_LnSunPosition.xyz - i.positionWS) : normalize(float3(0.4, 0.8, 0.3));
                float3 sun = _LnSunPosition.w > 0.5 ? _LnSunColor.rgb : float3(1, 1, 1);
                float3 v = normalize(GetCameraPositionWS() - i.positionWS);
                float diffuse = saturate(dot(n, l));
                float day = smoothstep(-0.15, 0.25, dot(n, l));                        // the rim glows on the day side
                float rim = pow(1.0 - saturate(dot(n, v)), 3.0) * _Rim * (0.25 + 0.75 * day);
                float3 color = i.color.rgb * (_Ambient + (1.0 - _Ambient) * diffuse * sun) + _AtmosphereColor.rgb * rim;
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
