// The skipping stone, and the aim ghost.
//
// In Resources/ for the same reason WaterRipple is: Shader.Find only resolves shaders
// included in a player build, and only Unity's built-ins are in Always Included
// Shaders, so a custom shader found that way works in the editor and comes out
// magenta in a build. See PlaceholderArt for the trap and the loader.
//
// EXISTS TO MAKE ROTATION VISIBLE. The stone was a scaled sphere drawn with
// Sprites/Default — unlit, no texture bound, so the mesh's normals and UVs were both
// discarded and it rendered as one flat silhouette. A scaled sphere is also two-fold
// symmetric, so the only cue that it was spinning at all was a ~15% width oscillation
// at TWO cycles per revolution: a strobe at any speed, and no rotation rate could fix
// it because the problem was never the rate.
//
// Two things here fix that. Cheap directional shading gives the surface a highlight
// that sweeps as the stone turns, and a deliberately ASYMMETRIC banding pattern in UV
// space gives it a feature that only returns to where it started after a full turn.
// One revolution then reads as one revolution, which also doubles the aliasing ceiling
// to 10800 deg/s at 60fps — see SkipTuning.VisualSpinDegPerSec.
Shader "RockSkipping/Stone"
{
    Properties
    {
        // Named _Color because Material.color maps to it, and PlaceholderArt sets the
        // colour that way. Rename it and the colour silently stops applying.
        _Color ("Stone Colour", Color) = (0.85, 0.87, 0.95, 1)
        _BandColor ("Marking Colour", Color) = (0.28, 0.32, 0.42, 1)

        _BandStrength ("Marking Strength", Range(0, 1)) = 0.75
        _ShadeStrength ("Shading Strength", Range(0, 1)) = 0.55
        _LightDir ("Light Direction", Vector) = (0.4, 0.85, -0.35, 0)
    }

    SubShader
    {
        // No Queue tag: PlaceholderArt pins it to StoneQueue from C#, which is the
        // single place that owns draw order between water, stone and arc.
        Tags { "RenderType" = "Opaque" }

        // Matches what Sprites/Default was doing. Going opaque with ZWrite On would
        // change the depth behaviour the water and aim arc rely on and undo the
        // draw-order fix documented in PlaceholderArt.
        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            fixed4 _Color;
            fixed4 _BandColor;
            float _BandStrength;
            float _ShadeStrength;
            float4 _LightDir;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                // World space, so the highlight stays put while the stone turns under
                // it. In object space it would rotate with the stone and sit perfectly
                // still on the surface — which is exactly the non-cue we started with.
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Unity's sphere primitive wraps u once around the equator, which is the
                // axis the stone yaws about — so u is the angle we need a feature on.
                float u = frac(i.uv.x);

                // Asymmetric on purpose. A pattern that repeats would reintroduce the
                // original bug at a higher frequency: with two identical marks a half
                // turn is indistinguishable from a whole one, and the apparent spin rate
                // doubles while the apparent revolution count halves. One broad mark and
                // one narrow one, unevenly spaced, means every angle looks different from
                // every other angle and a full turn is unambiguous.
                float broad  = smoothstep(0.16, 0.10, abs(u - 0.30));
                float narrow = smoothstep(0.05, 0.02, abs(u - 0.68));
                float marking = saturate(broad + narrow * 0.85);

                // Fades toward the rim so the marks read as surface, not as a decal, and
                // so the silhouette itself stays clean.
                float rim = smoothstep(0.0, 0.35, min(i.uv.y, 1.0 - i.uv.y));
                marking *= rim;

                fixed3 albedo = lerp(_Color.rgb, _BandColor.rgb, marking * _BandStrength);

                // Half-lambert: never fully black, so the unlit side still shows its
                // markings. There is no real light in the placeholder scene, hence the
                // fixed direction rather than a light lookup.
                float3 n = normalize(i.worldNormal);
                float3 l = normalize(_LightDir.xyz);
                float lambert = dot(n, l) * 0.5 + 0.5;
                float shade = lerp(1.0, lambert, _ShadeStrength);

                return fixed4(albedo * shade, _Color.a);
            }
            ENDCG
        }
    }

    // Flat colour rather than magenta if the pass above will not compile anywhere.
    Fallback "Sprites/Default"
}
