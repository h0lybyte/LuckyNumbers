// Bolts and their flashes (Planet/BoltField): flat HDR colour (it blooms), fogged like the URP Unlit it replaces.
// GPU-instanced with the colour per instance, so every bolt in flight is one RenderMeshInstanced draw, no GameObjects.
Shader "LuckyNumbers/Bolt"
{
    Properties
    {
        [HDR] _BaseColor ("Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BaseColor)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float fog : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 color = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor).rgb;
                return float4(MixFog(color, i.fog), 1);
            }
            ENDHLSL
        }
    }
}
