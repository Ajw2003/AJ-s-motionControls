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
Shader "RockSkipping/WaterRipple"
{
    Properties
    {
        // Named _Color because Material.color maps to it, and PlaceholderArt sets the
        // water's colour that way. Rename it and the colour silently stops applying.
        _Color ("Water Colour", Color) = (0.06, 0.16, 0.28, 1)
        _CrestColor ("Crest Colour", Color) = (0.55, 0.75, 0.95, 1)

        _RippleLife ("Ripple Life (seconds)", Float) = 2.5
        _RippleSpeed ("Front Speed (world units/sec)", Float) = 6.0
        _RippleWidth ("Ring Band Width (world units)", Float) = 3.0
        _RippleWavelength ("Wavelength (world units)", Float) = 2.5
        _TroughDarkness ("Trough Darkening", Range(0, 1)) = 0.35
    }

    SubShader
    {
        // No Queue tag here: WaterRipples pins it from C# to PlaceholderArt.WaterQueue,
        // which is the single place that owns draw order.
        //
        // LightMode ForwardBase is what makes Unity fill _LightColor0 and
        // _WorldSpaceLightPos0 for the pass. Without it the lighting below silently
        // multiplies by black and the lake goes flat.
        Tags { "RenderType" = "Opaque" "LightMode" = "ForwardBase" }

        // Both match what Sprites/Default was doing. Going opaque with ZWrite On would
        // change the depth behaviour the stone and aim arc rely on and undo the
        // draw-order fix documented in PlaceholderArt.
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

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
            float _RippleLife;
            float _RippleSpeed;
            float _RippleWidth;
            float _RippleWavelength;
            float _TroughDarkness;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 normal : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float wave = 0.0;

                int count = min(_RippleCount, MAX_RIPPLES);
                for (int r = 0; r < count; r++)
                {
                    float4 rip = _Ripples[r];

                    float age = rip.z;
                    float life = saturate(1.0 - age / max(_RippleLife, 0.0001));
                    if (life <= 0.0) continue;

                    float d = distance(i.world.xz, rip.xy);
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

                // Gamma colour space project: blend the authored values directly. A manual
                // pow(c, 2.2) here would make the water diverge from the stone and arc,
                // which are plain gamma-space colours through the same factory.
                fixed4 col = _Color;
                col.rgb = lerp(col.rgb, _CrestColor.rgb, saturate(wave));
                col.rgb *= 1.0 - saturate(-wave) * _TroughDarkness;

                // Lit rather than unlit, unlike the original version of this shader. The
                // stone and the ghost went through Standard when the scene gained a
                // directional light, and flat water beside lit props reads as a hole in
                // the render. Plain lambert plus the ambient probe is enough to sit with
                // them — ShadeSH9 specifically, so the water samples the same ambient the
                // Standard material does instead of a constant that would drift from it.
                float3 n = normalize(i.normal);
                float ndotl = saturate(dot(n, _WorldSpaceLightPos0.xyz));
                col.rgb *= _LightColor0.rgb * ndotl + ShadeSH9(float4(n, 1));

                return col;
            }
            ENDCG
        }
    }

    // Covers hardware that cannot run the pass above — not syntax errors, which still
    // show magenta and log to the console. Sprites/Default is always-included, so this
    // fallback is safe to reach in a player build.
    Fallback "Sprites/Default"
}
