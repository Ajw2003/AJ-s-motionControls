// Placeholder water with expanding rings at each skip.
//
// In Resources/ on purpose. Shader.Find only resolves shaders included in a player
// build, and only Unity's built-ins are in Always Included Shaders — a custom shader
// found that way works in the editor and comes out magenta in a build. Anything under
// a Resources folder is included unconditionally, so WaterRipples loads it by name.
// See PlaceholderArt for the same trap in the material factory.
//
// Ripples are computed in world XZ, never UV: the plane is 2000 world units square, so
// a UV-space radius would be measured in units of the whole lake and no ring would be
// the size a bounce actually makes. The mesh is also only 121 vertices, so this has to
// be a fragment effect — there is nowhere near enough geometry to displace.
//
// A Standard surface shader rather than the unlit pass this used to be. The lake ran
// through Shader.Find("Standard") while the ripples were gone, and going back to unlit
// visibly darkened it: the scene's ambient mode is Skybox, so Standard was picking up
// skybox SH on the diffuse *and* a grazing-angle specular reflection off the default
// skybox. Hand-rolled lambert plus ShadeSH9 reproduces the first and misses the second
// entirely, which is exactly how much darker it looked. Deferring to surf/Standard runs
// the same lighting Unity was already running, so the ripples ride on the albedo and
// nothing about the lighting changes at all.
Shader "RockSkipping/WaterRipple"
{
    Properties
    {
        // Named _Color because Material.color maps to it, and PlaceholderArt sets the
        // water's colour that way. Rename it and the colour silently stops applying.
        _Color ("Water Colour", Color) = (0.06, 0.16, 0.28, 1)
        _CrestColor ("Crest Colour", Color) = (0.55, 0.75, 0.95, 1)

        // Standard's own defaults, deliberately. PlaceholderArt built the water with
        // new Material(Shader.Find("Standard")) and never touched either, so matching
        // the defaults here is what makes the unrippled lake identical to before.
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0.0

        _RippleLife ("Ripple Life (seconds)", Float) = 2.5
        _RippleSpeed ("Front Speed (world units/sec)", Float) = 6.0
        _RippleWidth ("Ring Band Width (world units)", Float) = 3.0
        _RippleWavelength ("Wavelength (world units)", Float) = 2.5
        _TroughDarkness ("Trough Darkening", Range(0, 1)) = 0.35
    }

    SubShader
    {
        // No Queue tag here: WaterRipples pins it from C# to PlaceholderArt.WaterQueue,
        // which is the single place that owns draw order. No LightMode tag either — the
        // surface shader generates its own ForwardBase/ForwardAdd passes and setting one
        // by hand here would fight it.
        Tags { "RenderType" = "Opaque" }

        // Both match what the water was already doing. Going opaque with ZWrite On would
        // change the depth behaviour the stone and aim arc rely on and undo the
        // draw-order fix documented in PlaceholderArt. Declared at SubShader level so the
        // generated passes inherit them.
        ZWrite Off
        Cull Off

        CGPROGRAM
        // fullforwardshadows so the water still receives the stone's shadow, as it did
        // through Standard. No addshadow: the lake is a 2000-unit plane and has nothing
        // beneath it to cast onto.
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        #define MAX_RIPPLES 16

        // xy = world x/z of the bounce, z = age in seconds, w = strength 0..1.
        //
        // Age arrives already computed from the CPU rather than being derived from
        // _Time here. That way the shader keeps no clock of its own and cannot drift
        // from the game's, and scene loads resetting _Time cannot strand a ripple
        // whose start time was recorded against the old one.
        float4 _Ripples[MAX_RIPPLES];
        int _RippleCount;

        fixed4 _Color;
        fixed4 _CrestColor;
        half _Glossiness;
        half _Metallic;
        float _RippleLife;
        float _RippleSpeed;
        float _RippleWidth;
        float _RippleWavelength;
        float _TroughDarkness;

        struct Input
        {
            float3 worldPos;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float wave = 0.0;

            int count = min(_RippleCount, MAX_RIPPLES);
            for (int r = 0; r < count; r++)
            {
                float4 rip = _Ripples[r];

                float age = rip.z;
                float life = saturate(1.0 - age / max(_RippleLife, 0.0001));
                if (life <= 0.0) continue;

                float d = distance(IN.worldPos.xz, rip.xy);
                float front = age * _RippleSpeed;

                // Windowed to the neighbourhood of the expanding front. Without this
                // every ripple rings the entire lake at once.
                float band = saturate(1.0 - abs(d - front) / max(_RippleWidth, 0.0001));
                if (band <= 0.0) continue;

                // Trailing wavefronts behind the leading edge. Wavelength alone spans
                // the look: large is one clean shockwave, small is concentric rings.
                float phase = sin((front - d) * (6.28318530718 / max(_RippleWavelength, 0.0001)));

                // band and life squared so rings ease out at both edges rather than
                // ending on a hard line.
                wave += phase * band * band * life * life * rip.w;
            }

            // Rings modulate albedo only. Everything below this line is what Standard
            // would have done to the flat colour on its own, so with no live ripples the
            // lake shades exactly as it did before.
            fixed3 albedo = lerp(_Color.rgb, _CrestColor.rgb, saturate(wave));
            albedo *= 1.0 - saturate(-wave) * _TroughDarkness;

            o.Albedo = albedo;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1.0;
        }
        ENDCG
    }

    // Covers hardware that cannot run the shader above — not syntax errors, which still
    // show magenta and log to the console. Sprites/Default is always-included, so this
    // fallback is safe to reach in a player build.
    Fallback "Sprites/Default"
}
