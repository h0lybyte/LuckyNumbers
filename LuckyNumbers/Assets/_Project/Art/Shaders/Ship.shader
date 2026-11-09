// Textured ship lit by the universe's star (globals _LnSunPosition / _LnSunColor, like Planet3D):
// wrapped diffuse + ambient floor, bright saturated texels (lights, engines) glow regardless of light,
// thin rim so the silhouette reads against dark space.
Shader "LuckyNumbers/Ship"
{
    Properties
    {
        _MainTex ("Skin", 2D) = "white" {}
        _Ambient ("Ambient", Range(0, 1)) = 0.18
        _Glow ("Light Glow", Float) = 1.5
        _Rim ("Rim", Float) = 0.35
        _Heat ("Entry Heat (0..1, set at runtime)", Range(0, 1)) = 0
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

            float4 _LnSunPosition, _LnSunColor; // globals set by SpacePresenter
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Ambient, _Glow, _Rim, _Heat;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 positionWS : TEXCOORD2; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb;
                float3 n = normalize(i.normalWS);
                bool sunSet = _LnSunPosition.w > 0.5;
                float3 l = sunSet ? normalize(_LnSunPosition.xyz - i.positionWS) : normalize(float3(0.6, 0.3, -0.7));
                float3 sun = sunSet ? _LnSunColor.rgb : float3(1, 1, 1);
                float diffuse = saturate(dot(n, l) * 0.8 + 0.2);                       // wrapped: softer terminator
                float3 v = normalize(GetCameraPositionWS() - i.positionWS);
                float rim = pow(1.0 - saturate(dot(n, v)), 3.0) * _Rim;

                float maxc = max(albedo.r, max(albedo.g, albedo.b)), minc = min(albedo.r, min(albedo.g, albedo.b));
                float glow = smoothstep(0.35, 0.8, maxc - minc) * smoothstep(0.5, 0.9, maxc);   // lights & engines
                float3 color = albedo * (sun * diffuse + _Ambient) + albedo * glow * _Glow + sun * rim * 0.5;
                // Atmospheric entry: hot orange-white fresnel that swallows the hull as heat rises.
                float hot = pow(1.0 - saturate(dot(n, v)), 1.5);
                color = lerp(color, float3(1.0, 0.45, 0.12) * 3.0, saturate(_Heat * (0.35 + 0.9 * hot)));
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
