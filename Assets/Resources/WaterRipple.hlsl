#ifndef ROCKSKIPPING_WATERRIPPLE_INCLUDED
#define ROCKSKIPPING_WATERRIPPLE_INCLUDED

// Expanding rings at each skip, as a Shader Graph Custom Function payload.
//
// This is the surf() body from WaterRipple.shader, ported verbatim. None of the maths
// is pipeline-specific — only the lighting was, and that is exactly what moving to a
// URP Lit master stack hands back. The original shader's remarks explain why it defers
// to real PBR lighting rather than hand-rolling lambert (skybox SH on the diffuse plus
// a grazing-angle specular off the default skybox, which a hand-rolled version misses
// and which is precisely how much darker the lake looked); a Lit master stack is the
// URP equivalent of `#pragma surface surf Standard`, so that reasoning carries over
// unchanged.
//
// Ripples are computed in world XZ, never UV: the plane is 2000 world units square, so
// a UV-space radius would be measured in units of the whole lake and no ring would be
// the size a bounce actually makes. The mesh is also only 121 vertices, so this has to
// be a fragment effect — there is nowhere near enough geometry to displace.
//
// ---------------------------------------------------------------------------
// Wiring this into a Shader Graph
// ---------------------------------------------------------------------------
// Custom Function node, Type = File, File = this asset, Name = RippleWave.
// Shader Graph appends the precision suffix itself, so the node's Name field is
// "RippleWave" with no _float on the end.
//
//   Inputs                          Output
//     WorldPos          Vector3       Wave    Float
//     RippleLife        Float
//     RippleSpeed       Float
//     RippleWidth       Float
//     RippleWavelength  Float
//
// The names and order above must match the parameter list below exactly — Shader Graph
// binds Custom Function ports positionally and by type, and a mismatch fails to compile
// with an error that points at the generated code rather than at the node.

// MUST match WaterRipples.MaxRipples on the C# side. The two are checked against each
// other nowhere, so changing one and not the other silently drops rings off the end.
#define MAX_RIPPLES 16

// xy = world x/z of the bounce, z = age in seconds, w = strength 0..1.
//
// Age arrives already computed from the CPU rather than being derived from _Time here.
// That way the shader keeps no clock of its own and cannot drift from the game's, and
// scene loads resetting _Time cannot strand a ripple whose start time was recorded
// against the old one.
//
// Declared at file scope, which makes these global uniforms rather than per-material
// properties, and that is deliberate. Shader Graph has no array property type, so an
// array can never live inside the UnityPerMaterial CBUFFER that the SRP Batcher
// requires. Feeding it with Material.SetVectorArray therefore lands in an ambiguous
// spot — it is a global set that only looks per-material. WaterRipples pushes these
// with Shader.SetGlobalVectorArray instead, which is unambiguous and costs nothing:
// there is exactly one water plane in the game, so global scope is not a restriction.
//
// Note this file must be attached to the Custom Function node in FILE mode, not the
// inline string mode. Inline strings are pasted inside a function body, so a
// declaration here would become a local and Shader.SetGlobalVectorArray would have
// nothing to bind to.
float4 _Ripples[MAX_RIPPLES];
int _RippleCount;

// Signed, roughly -1..1. Positive is a crest, negative a trough; the graph turns that
// into colour. Returned raw rather than pre-coloured so a later foam or specular term
// can read the same value without this file having to know about it.
void RippleWave(
    float3 worldPos,
    float rippleLife,
    float rippleSpeed,
    float rippleWidth,
    float rippleWavelength,
    out float wave)
{
    wave = 0.0;

    // How much world space one screen pixel covers here, in the water plane. This is
    // what makes the rings anti-alias, and it is the whole fix for the "ripples look
    // like badly rendered particles" bug.
    //
    // The lake is 2000 units across and the camera looks along it at a grazing angle,
    // so a pixel near the horizon spans many world units. Once a pixel is wider than
    // half a wavelength the sine below is being point-sampled far under Nyquist, and
    // consecutive pixels land on unrelated points of the wave — which is not a ring any
    // more, it is white noise shaped like a ring. That is exactly what it looked like.
    //
    // Computed ONCE, here, and deliberately not inside the loop. Screen-space
    // derivatives are only defined under uniform control flow, and the loop below has
    // two `continue`s in it — taking ddx/ddy in there is undefined behaviour that
    // happens to work on some hardware and returns garbage on the rest.
    float2 dpdx = ddx(worldPos.xz);
    float2 dpdy = ddy(worldPos.xz);
    float footprint = max(length(dpdx), length(dpdy));

    // Hold the ring pattern at a roughly constant SCREEN size instead of a constant world
    // size. This is the anti-aliasing fix, and it is the third attempt at it — the first
    // two are worth recording because both failure modes are visible in a recording.
    //
    // The rings are authored at a 1-2.5 unit wavelength on a lake 2000 units across, seen
    // at a grazing angle. Past the near field one pixel spans more than a whole cycle, so
    // point-sampling the sine there returns unrelated values per pixel: that was the
    // salt-and-pepper "poorly rendered particles" look. Fading the wave out by how
    // resolvable it was deleted the ripples entirely instead, because almost no pixel on
    // the lake clears that bar. Falling back to a flat crest where the sine died turned
    // them into soft white blobs, because almost every pixel took the fallback.
    //
    // Clamping the wavelength and the band width to the pixel footprint fixes the cause
    // rather than the symptom: the pattern simply gets physically larger with distance, so
    // it stays a resolvable sine everywhere and never needs to be faded, substituted or
    // point-sampled below Nyquist. Distant rings are wider than they would "really" be —
    // an honest trade against noise, blobs, or nothing at all.
    //
    // Near the camera both maxes pick the authored value, so close-up rings are exactly as
    // tuned and none of this is doing anything.
    float wavelength = max(rippleWavelength, footprint * 6.0);
    float width      = max(rippleWidth,      footprint * 4.0);

    int count = min(_RippleCount, MAX_RIPPLES);
    for (int r = 0; r < count; r++)
    {
        float4 rip = _Ripples[r];

        float age = rip.z;
        float life = saturate(1.0 - age / max(rippleLife, 0.0001));
        if (life <= 0.0) continue;

        float d = distance(worldPos.xz, rip.xy);
        float front = age * rippleSpeed;

        // Windowed to the neighbourhood of the expanding front. Without this every
        // ripple rings the entire lake at once.
        float band = saturate(1.0 - abs(d - front) / max(width, 0.0001));
        if (band <= 0.0) continue;

        // Trailing wavefronts behind the leading edge. Wavelength alone spans the look:
        // large is one clean shockwave, small is concentric rings.
        float phase = sin((front - d) * (6.28318530718 / max(wavelength, 0.0001)));

        // band and life squared so rings ease out at both edges rather than ending on a
        // hard line.
        wave += phase * band * band * life * life * rip.w;
    }
}

// Shader Graph resolves Custom Function entry points by precision suffix and will ask
// for whichever the graph is set to. Both forward to the one implementation rather than
// duplicating it — a half-precision copy that drifted from the float one would show up
// only on the platforms nobody tests on.

void RippleWave_float(
    float3 WorldPos,
    float RippleLife,
    float RippleSpeed,
    float RippleWidth,
    float RippleWavelength,
    out float Wave)
{
    RippleWave(WorldPos, RippleLife, RippleSpeed, RippleWidth, RippleWavelength, Wave);
}

void RippleWave_half(
    half3 WorldPos,
    half RippleLife,
    half RippleSpeed,
    half RippleWidth,
    half RippleWavelength,
    out half Wave)
{
    float w;
    RippleWave(WorldPos, RippleLife, RippleSpeed, RippleWidth, RippleWavelength, w);
    Wave = (half)w;
}

#endif // ROCKSKIPPING_WATERRIPPLE_INCLUDED
