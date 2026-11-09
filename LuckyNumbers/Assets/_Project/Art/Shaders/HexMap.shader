// The planet map's hex globe: vertex colours (per cell, set in code), lit from over the viewer's shoulder
// (view space, so the side you look at is always lit), with an atmosphere rim (_AtmosphereColor).
Shader "LuckyNumbers/HexMap"
{
    Properties
    {
        _AtmosphereColor ("Atmosphere Color", Color) = (0.4, 0.6, 1, 1)
        _Rim ("Rim", Float) = 0.6
        _Ambient ("Ambient", Range(0, 1)) = 0.22
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

            CBUFFER_START(UnityPerMaterial)
                float4 _AtmosphereColor;
                float _Rim, _Ambient;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalVS : TEXCOORD0; float4 color : COLOR; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalVS = mul((float3x3)UNITY_MATRIX_V, TransformObjectToWorldNormal(v.normalOS));
                o.color = v.color;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalVS);                       // view space: +z faces the camera
                float diffuse = saturate(dot(n, normalize(float3(-0.65, 0.55, 0.5))));
                float rim = pow(1.0 - saturate(n.z), 3.0) * _Rim;
                float3 color = i.color.rgb * (_Ambient + (1.0 - _Ambient) * diffuse) + _AtmosphereColor.rgb * rim;
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
