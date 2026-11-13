// Ambient motion (Planet/AmbientLayer, docs/ambience.md): one static mesh per layer, built once; everything moves
// here, from _Time, so a layer costs one draw and nothing on the CPU per frame.
//   _Mode 0, motes: quads (uv0 = corner -0.5..0.5, uv1 = xyz a random spot in the box 0..1, w a random phase). Each
//     drifts (_Drift m/s), wanders (_Shape.y metres at _Shape.z), and wraps round a box (_Box metres) centred on the
//     camera, or on _Anchor when _Anchor.w = 1 (the wreck's embers: the box's floor at the anchor). Shrinks to nothing
//     at the box's faces, so none pops. Gusts (global _LnWind) roll across and push them downwind (_Extra.y metres).
//     _Shape.w (glow) 0 = dust, lit by _LnSunColor + _LnSkyColor, soft alpha; 1 = embers, self-lit HDR, additive (alpha
//     out 0 under premultiplied blending), dimming as they rise. _Extra.x twinkle.
//   _Mode 1, fliers: per bird six vertices (uv0.x -1/0/1 wing tip, body, wing tip; uv0.y -1/1 trailing/leading edge;
//     uv1 = flock index, three randoms). A flock wheels round a centre that wraps in a square round the camera (side
//     2 x _Flock.y, kept at least _Flock.x out), _Flock.zw metres above _LnClouds.z (the ground under the camera, set
//     from code). Wings flap (_Flock2.w beats/s) with glides; dark silhouettes hazed to _HazeColor by distance.
// Premultiplied alpha, no depth write. Plain arithmetic, no textures: WebGL 2 and WebGPU alike.
Shader "LuckyNumbers/Ambient"
{
    Properties
    {
        [HDR] _Color ("Color (a = opacity)", Color) = (1, 1, 1, 0.6)
        _Box ("Box (metres)", Vector) = (24, 12, 24, 0)
        _Drift ("Drift (m/s)", Vector) = (0.5, 0, 0.2, 0)
        _Shape ("Size, flutter, flutter speed, glow", Vector) = (0.05, 0.5, 1, 0)
        _Extra ("Twinkle, gust, mode, -", Vector) = (0.3, 1, 0, 0)
        _Anchor ("Anchor (xyz, w = 1: box on it, not the camera)", Vector) = (0, 0, 0, 0)
        _Flock ("Distance min, max, height min, max", Vector) = (110, 260, 40, 85)
        _Flock2 ("Circle radius, speed, wingspan, flaps/s", Vector) = (26, 10, 2.6, 4.5)
        _Flock3 ("Flocks, -, -, -", Vector) = (3, 0, 0, 0)
        _HazeColor ("Haze colour (w = fully hazed at, m)", Vector) = (0.5, 0.6, 0.7, 600)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color, _Box, _Drift, _Shape, _Extra, _Anchor, _Flock, _Flock2, _Flock3, _HazeColor;
            CBUFFER_END
            float4 _LnSunPosition, _LnSunColor, _LnSkyColor;   // globals (crash site / hold)
            float4 _LnWind;     // global: xy wind m/s, z cloud scale, w cloud shadow
            float4 _LnClouds;   // global: x cover, y softness, z ground height under the camera

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 seed : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 q : TEXCOORD0;       // motes: -1..1 across; fliers: wing (x) and chord (y)
                float4 color : TEXCOORD1;   // premultiplied rgb, a = alpha out
            };

            float Hash(float n) { return frac(sin(n) * 43758.5453); }

            float3 Lit(float3 albedo)
            {
                float3 sun = _LnSunPosition.w > 0.5 ? _LnSunColor.rgb : float3(1, 1, 1);
                float3 sky = _LnSkyColor.w > 0.5 ? _LnSkyColor.rgb : float3(0.6, 0.6, 0.6);
                return albedo * (sun * 0.75 + sky * 0.35);
            }

            Varyings Motes(Attributes v)
            {
                Varyings o;
                float t = _Time.y;
                float phase = v.seed.w * 6.2831853;
                float3 box = max(_Box.xyz, 0.1);
                bool anchored = _Anchor.w > 0.5;
                float3 centre = anchored ? _Anchor.xyz + float3(0, box.y * 0.5, 0) : _WorldSpaceCameraPos;
                float wander = t * _Shape.z;
                float3 p = v.seed.xyz * box + _Drift.xyz * t
                         + _Shape.y * float3(sin(wander + phase), sin(wander * 1.31 + phase * 2.0) * 0.6, cos(wander * 0.87 + phase * 3.0));
                float3 local = (frac((p - centre) / box) - 0.5) * box;   // wrapped into the box round the centre
                float3 world = centre + local;

                // Gusts: bands rolling downwind (spaced like the clouds), pushing motes along as they pass.
                float windSpeed = length(_LnWind.xy);
                if (windSpeed > 0.01 && _Extra.y > 0.0)
                {
                    float2 dir = _LnWind.xy / windSpeed;
                    float band = dot(world.xz, dir) / max(_LnWind.z * 0.35, 8.0) - t * windSpeed / max(_LnWind.z * 0.35, 8.0) + v.seed.w * 0.6;
                    float gust = pow(saturate(sin(band * 6.2831853)), 3.0) * _Extra.y;
                    world.xz += dir * gust;
                    world.y += gust * 0.2;
                }

                float3 edge = abs(local) / (box * 0.5);
                float fade = saturate((1.0 - max(edge.x, max(edge.y, edge.z))) * 4.0);
                float rise = saturate(local.y / box.y + 0.5);   // 0 at the box's floor, 1 at its top
                float glow = saturate(_Shape.w);
                float twinkle = 1.0 - _Extra.x * (0.5 + 0.5 * sin(t * (5.0 + v.seed.x * 6.0) + phase * 7.0)) * (0.6 + 0.4 * sin(t * 13.0 + phase * 3.0));
                float bright = twinkle * lerp(1.0, 1.0 - rise * rise, glow * (_Drift.y > 0.0 ? 1.0 : 0.0));
                float size = _Shape.x * fade * lerp(1.0, 1.0 - rise * 0.5, glow);

                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                world += (right * v.uv.x + up * v.uv.y) * size;
                o.positionCS = TransformWorldToHClip(world);
                o.q = v.uv * 2.0;
                float3 rgb = lerp(Lit(_Color.rgb), _Color.rgb, glow) * bright;
                float a = _Color.a * saturate(bright);
                o.color = float4(rgb * a, a * (1.0 - glow));   // glow: alpha out 0, so it adds
                return o;
            }

            Varyings Fliers(Attributes v)
            {
                Varyings o;
                float t = _Time.y;
                float flock = v.seed.x;
                float side = max(_Flock.y * 2.0, 1.0);
                // The flock's centre: a random spot drifting slowly downwind, wrapped round the camera, kept out past min.
                float2 home = float2(Hash(flock * 12.9898 + 1.0), Hash(flock * 78.233 + 2.0)) * side + _LnWind.xy * 0.25 * t;
                float2 rel = (frac((home - _WorldSpaceCameraPos.xz) / side) - 0.5) * side;
                float d = length(rel);
                rel += rel / max(d, 1e-3) * max(0.0, _Flock.x - d);
                float edge = max(abs(rel.x), abs(rel.y)) / (side * 0.5);
                float fade = saturate((1.0 - edge) * 5.0);
                float fh = Hash(flock * 39.425 + 3.0);
                float height = _LnClouds.z + lerp(_Flock.z, _Flock.w, fh) + (v.seed.w - 0.5) * 8.0;

                // Round the circle (each flock its own way round), each bird its own spot and a little off the line.
                float turn = fh > 0.5 ? 1.0 : -1.0;
                float radius = max(_Flock2.x, 1.0) * (0.7 + 0.6 * v.seed.y);
                float angle = v.seed.z * 6.2831853 + turn * t * _Flock2.y / radius;
                float s, c;
                sincos(angle, s, c);
                float3 centre = float3(_WorldSpaceCameraPos.x + rel.x + c * radius,
                                       height + sin(t * 0.35 + v.seed.z * 9.0) * 3.0,
                                       _WorldSpaceCameraPos.z + rel.y + s * radius);
                float3 forward = float3(-s, 0, c) * turn;
                float3 right = float3(forward.z, 0, -forward.x);
                // Bank into the turn.
                float3 up = float3(0, 1, 0);
                float3 wingDir = normalize(right + up * (0.35 * turn));

                // Flap with glides: beats come and go.
                float beat = sin(t * _Flock2.w * 6.2831853 + v.seed.y * 40.0);
                float flapping = smoothstep(-0.2, 0.4, sin(t * 0.45 + v.seed.z * 17.0));
                float lift = lerp(0.12, beat * 0.45, flapping);   // gliding: wings held in a shallow V
                float span = _Flock2.z * fade;
                float wing = v.uv.x;                    // -1, 0, 1
                float chord = v.uv.y;                   // -1 trailing, 1 leading
                float3 world = centre + wingDir * (wing * span * 0.5)
                             + up * (abs(wing) * lift * span * 0.5)
                             + forward * (chord * span * lerp(0.16, 0.09, abs(wing)) + (1.0 - abs(wing)) * span * 0.08);
                o.positionCS = TransformWorldToHClip(world);
                o.q = float2(wing, chord);
                float haze = saturate(distance(_WorldSpaceCameraPos, world) / max(_HazeColor.w, 1.0));
                float3 rgb = lerp(_Color.rgb, _HazeColor.rgb, haze * haze);
                o.color = float4(rgb, 1.0);
                return o;
            }

            Varyings vert(Attributes v)
            {
                if (_Extra.z > 0.5) return Fliers(v);
                return Motes(v);
            }

            float4 frag(Varyings i) : SV_Target
            {
                if (_Extra.z > 0.5)
                {
                    // A swept wing: the chord narrows toward the tip.
                    float w = abs(i.q.x);
                    clip(1.0 - w * 0.65 - abs(i.q.y) * (0.55 + 0.45 * w) + 0.05);
                    return i.color;
                }
                float r2 = dot(i.q, i.q);
                float soft = saturate(1.0 - r2);
                return i.color * (soft * soft);
            }
            ENDHLSL
        }
    }
}
