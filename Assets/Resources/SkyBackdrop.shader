// Gradient sky with drifting clouds, drawn as a camera-parented quad rather than a skybox.
//
// WHY A QUAD AND NOT RenderSettings.skybox - this is the whole reason the file exists.
// A skybox was tried and it deleted the lake. URP's skybox pass runs AFTER opaque geometry and
// uses the depth buffer to skip pixels already covered; WaterRipples keeps the water's ZWrite
// OFF except during a sink (PlaceholderArt.SetWaterDepthWrite explains why - with depth on, the
// water swallows the stone), so the water writes no depth and the skybox painted straight over
// it. A quad at the Background queue draws BEFORE the water instead, so the water lands on top
// of it the same way it lands on the camera's clear colour today.
//
// URP-tagged and under Resources/, loaded with Resources.Load<Shader>: a Built-in surface shader
// renders MAGENTA in this project, and Shader.Find only resolves what a build happened to
// include. Same contract as PlaceholderArt.cs.
Shader "RockSkipping/SkyBackdrop"
{
    Properties
    {
        _SkyTop ("Sky - top", Color) = (0.16, 0.34, 0.62, 1)
        _SkyHorizon ("Sky - horizon", Color) = (0.42, 0.58, 0.74, 1)

        // lerp(horizon, top, y^exponent). SMALL exponent -> the top colour takes over close to
        // the horizon, giving a tight horizon band. LARGE -> the horizon colour smears over the
        // whole sky. An earlier attempt shipped 2.2 believing it did the former; it does the
        // latter, and the sky rendered as one flat colour. Below 1 is what a sky wants.
        _Exponent ("Horizon falloff", Range(0.1, 6)) = 0.55

        _CloudColour ("Cloud colour", Color) = (1, 1, 1, 1)

        // How much of the cloud shows. 0 is a clear sky.
        _CloudAmount ("Cloud amount", Range(0, 1)) = 0.55

        // Higher packs more, smaller clouds into the same sky.
        _CloudScale ("Cloud scale", Range(0.05, 4)) = 0.55

        // Cloud cover. Higher leaves less sky - it is the noise cutoff, so small changes here
        // move the coverage a lot.
        _CloudCover ("Cloud cover", Range(0, 1)) = 0.52

        _CloudSpeed ("Cloud drift", Range(0, 0.2)) = 0.012
    }

    SubShader
    {
        // Background: before the water (2000), the shore (1900) and everything else, so all of
        // them draw over it. ZWrite Off so it never occludes them, and never contributes depth
        // that something else might test against.
        //
        // Cull Off because Unity's built-in Quad faces one way and this is parented to a camera
        // - rather than depending on which way that is, draw both faces.
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Background"
            "RenderType" = "Background"
        }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            float4 _SkyTop;
            float4 _SkyHorizon;
            float _Exponent;
            float4 _CloudColour;
            float _CloudAmount;
            float _CloudScale;
            float _CloudCover;
            float _CloudSpeed;

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                return output;
            }

            // Cheap value noise. Not gradient noise - clouds are soft and low-contrast here, so
            // the visual difference does not survive the smoothstep below, and this costs less.
            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);

                // Smoothstep the interpolant, not the result: linear interpolation between cell
                // corners leaves visible diagonal creases along the cell edges.
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash(i);
                float b = Hash(i + float2(1, 0));
                float c = Hash(i + float2(0, 1));
                float d = Hash(i + float2(1, 1));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float Clouds(float2 p)
            {
                // Three octaves. Two reads as obviously procedural, four costs more than a
                // backdrop is worth.
                float n = ValueNoise(p) * 0.5;
                n += ValueNoise(p * 2.03) * 0.3;
                n += ValueNoise(p * 4.01) * 0.2;
                return n;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // The view ray, exactly as a skybox would compute it. Using the world direction
                // rather than the quad's own UVs is what keeps this behaving like sky: the quad
                // is parented to the camera, so a UV gradient would be locked to the screen and
                // would not tilt when the chase camera pitches to follow a throw.
                float3 direction = normalize(input.positionWS - _WorldSpaceCameraPos);

                // saturate before pow: below the horizon y is negative, and a negative base with
                // a fractional exponent is undefined - NaN speckle on some GPUs, nothing on
                // others, and a miserable bug to track down.
                float height = saturate(direction.y);
                float3 sky = lerp(_SkyHorizon.rgb, _SkyTop.rgb, pow(height, _Exponent));

                // Clouds live on a flat plane overhead, so the ray is projected onto it: at
                // height 1, the plane is hit at direction.xz / direction.y. That divide is what
                // makes them converge and compress toward the horizon like real cloud cover,
                // instead of sitting at a uniform size across the dome.
                //
                // The guard matters. As direction.y approaches 0 the projection runs to
                // infinity, and sampling noise at astronomical coordinates returns garbage that
                // flickers frame to frame.
                float horizonSafe = max(direction.y, 0.06);
                float2 cloudUv = direction.xz / horizonSafe * _CloudScale;
                cloudUv += _Time.y * _CloudSpeed * float2(1.0, 0.35);

                float n = Clouds(cloudUv);

                // Fade the clouds out into the horizon band. Without this they run right down to
                // the waterline, which reads as fog on the lake rather than cloud in the sky.
                float horizonFade = smoothstep(0.0, 0.35, direction.y);

                float cloud = smoothstep(_CloudCover, _CloudCover + 0.22, n);
                cloud *= horizonFade * _CloudAmount;

                float3 colour = lerp(sky, _CloudColour.rgb, saturate(cloud));
                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    // No Fallback: a fallback here would be a Built-in shader, which is the magenta this file
    // exists to avoid. Failing visibly beats a pink sky nobody can trace to a fallback line.
}
