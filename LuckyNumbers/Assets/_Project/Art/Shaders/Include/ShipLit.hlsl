// The Ship shader's lighting, shared with MonsterVat (instanced monsters keep the skinned model's look exactly):
// the universe's star (globals _LnSunPosition / _LnSunColor), wrapped diffuse + ambient floor + a soft fill from the
// camera side, saturated bright texels glowing, a thin rim, the planet's sky / ground ambient (_LnSkyColor /
// _LnGroundColor, w = 1 when set), a sun highlight (_Spec) and the hot fresnel of `heat` (entry heat, a hit flash).
// The including shader declares the material's _Ambient, _Fill, _Glow, _Rim, _Lift, _GlowMin, _Spec, _SpecPower.
#ifndef LN_SHIP_LIT_INCLUDED
#define LN_SHIP_LIT_INCLUDED

float4 _LnSunPosition, _LnSunColor; // globals set by SpacePresenter / CrashSitePresenter
float4 _LnSkyColor, _LnGroundColor; // globals set by CrashSitePresenter (w = 1), else neutral

float3 ShipShade(float3 albedo, float3 n, float3 positionWS, float heat)
{
    bool sunSet = _LnSunPosition.w > 0.5;
    float3 l = sunSet ? normalize(_LnSunPosition.xyz - positionWS) : normalize(float3(0.6, 0.3, -0.7));
    float3 sun = sunSet ? _LnSunColor.rgb : float3(1, 1, 1);
    float diffuse = saturate(dot(n, l) * 0.8 + 0.2);                       // wrapped: softer terminator
    float3 v = normalize(GetCameraPositionWS() - positionWS);
    float rim = pow(1.0 - saturate(dot(n, v)), 3.0) * _Rim;

    float maxc = max(albedo.r, max(albedo.g, albedo.b)), minc = min(albedo.r, min(albedo.g, albedo.b));
    float glow = smoothstep(0.35, 0.8, maxc - minc) * smoothstep(_GlowMin, min(_GlowMin + 0.4, 1.0), maxc);   // lights & engines
    float fill = saturate(dot(n, v)) * _Fill;                                  // soft key from the viewer
    // Dark skins: lift the albedo floor a little so panel detail survives.
    float3 base = lerp(albedo, max(albedo, 0.06 + albedo * 0.4), _Lift);
    // Ambient: sky from above, ground bounce from below (white when the scene sets neither).
    float3 hemi = _LnSkyColor.w > 0.5 ? lerp(_LnGroundColor.rgb, _LnSkyColor.rgb, n.y * 0.5 + 0.5) : float3(1, 1, 1);
    // Sun highlight on the lit side: painted metal catching the light (brighter panels shine more).
    float3 h = normalize(l + v);
    float spec = pow(saturate(dot(n, h)), _SpecPower) * _Spec * saturate(dot(n, l) * 4.0) * (0.4 + maxc);
    float3 color = base * (sun * diffuse + hemi * _Ambient + fill) + albedo * glow * _Glow + sun * (rim * 0.5 + spec);
    // Atmospheric entry: hot orange-white fresnel that swallows the hull as heat rises.
    float hot = pow(1.0 - saturate(dot(n, v)), 1.5);
    return lerp(color, float3(1.0, 0.45, 0.12) * 3.0, saturate(heat * (0.35 + 0.9 * hot)));
}

#endif
