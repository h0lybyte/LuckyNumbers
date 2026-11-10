// Window glass for the station's portholes (and any kit window): the kits paint stars on their glass;
// StationView swaps those faces to this, so the window looks out at the real scene behind it (the sky, the
// planet the station orbits, the rest of the station). Mostly clear: a faint cold tint, stronger at grazing
// angles (Fresnel), and a soft diagonal sheen so it still reads as glass. Drawn after opaques, no depth write.
Shader "LuckyNumbers/StationGlass"
{
    Properties
    {
        _Tint ("Tint (rgb, a = base opacity)", Color) = (0.55, 0.7, 0.85, 0.06)
        _Edge ("Grazing opacity", Range(0, 1)) = 0.35
        _Sheen ("Sheen", Range(0, 1)) = 0.12
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Edge, _Sheen;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 view = normalize(GetCameraPositionWS() - i.positionWS);
                float facing = saturate(abs(dot(normalize(i.normalWS), view)));
                float fresnel = pow(1.0 - facing, 3.0);
                // A diagonal band across the pane, fixed in the world so it slides as you walk past.
                float band = sin((i.positionWS.x + i.positionWS.y + i.positionWS.z) * 3.1);
                float sheen = _Sheen * smoothstep(0.75, 1.0, band);
                float alpha = saturate(_Tint.a + _Edge * fresnel + sheen);
                float3 color = _Tint.rgb + sheen;
                return float4(color, alpha);
            }
            ENDHLSL
        }
    }
}
