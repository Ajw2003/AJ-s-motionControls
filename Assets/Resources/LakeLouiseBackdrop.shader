// The Lake Louise scenery: valley, forest and boathouse, and their mirrored copies under the
// water. Flat-shaded vertex colour, lit by a fixed sun and ambient, hazed with distance.
//
// Everything the look depends on comes from LakeLouiseLook.json via LakeLouiseEnvironment.cs;
// tools/art/lake-louise/shaders.py is the Blender stand-in with the same maths, used to judge
// the look in a render. Change one, change the other.
//
// _Reflection = 1 is the mirrored copy (the same mesh at Y scale -1, see LakeLouiseEnvironment):
//   - the sun is mirrored too, so each reflected face is lit exactly as the face it mirrors;
//   - it keeps only what is BELOW the water, and the real copy keeps only what is ABOVE. Without
//     that, the real lake bed (y < 0) sits nearer the camera than the mirrored mountains and
//     hides them wherever the water is see-through;
//   - it is multiplied by _ReflectionTint, the same tint the sky applies below the horizon.
// The mirrored copy is drawn double-sided (_Cull = Off); see the _Cull property for why.
Shader "RockSkipping/LakeLouiseBackdrop"
{
    Properties
    {
        _SunDirection ("Direction TOWARD the sun (world)", Vector) = (-0.62, 0.70, -0.36, 0)
        _SunColour ("Sun colour", Color) = (1, 0.945, 0.863, 1)
        _SunStrength ("Sun strength", Range(0, 2)) = 0.85
        _AmbientColour ("Ambient colour", Color) = (0.514, 0.592, 0.741, 1)
        _AmbientStrength ("Ambient strength", Range(0, 2)) = 0.5

        _HazeColour ("Haze colour", Color) = (0.706, 0.804, 0.937, 1)
        _HazeStart ("Haze start (m)", Float) = 380
        _HazeDensity ("Haze density (per m)", Float) = 0.0022
        _HazeMax ("Haze max", Range(0, 1)) = 0.4

        _ReflectionTint ("Reflection tint", Color) = (0.435, 0.596, 0.659, 1)
        [Toggle] _Reflection ("Is the mirrored copy", Float) = 0
        _WaterY ("Water height", Float) = 0

        // FBX vertex colours arrive as the sRGB bytes Blender wrote; Unity does not convert
        // them, and this project renders in linear space. If the scenery looks washed out or
        // too dark after an importer change, this is the switch to check first.
        [Toggle] _VertexColourIsSrgb ("Vertex colour is sRGB", Float) = 1

        // Back for the real scenery, Off for the mirrored copy (LakeLouiseEnvironment sets it).
        // Culled, the mirrored copy dropped every facet that happens to face away from the
        // camera in the mirror; on faceted terrain at a grazing angle those alternate, and the
        // gaps showed as thin dark horizontal lines across the reflection (2026-09-28). The
        // Blender preview's mirror never culls, which is why it never showed them.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            // 1900 (PlaceholderArt.ShoreQueue): before the water, which writes no depth.
            // The mirrored copy's material overrides this to 1890.
            "Queue" = "Geometry-100"
        }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _SunDirection;
                float4 _SunColour;
                float _SunStrength;
                float4 _AmbientColour;
                float _AmbientStrength;
                float4 _HazeColour;
                float _HazeStart;
                float _HazeDensity;
                float _HazeMax;
                float4 _ReflectionTint;
                float _Reflection;
                float _WaterY;
                float _VertexColourIsSrgb;
                float _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 colour : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 colour : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.colour = input.colour;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                bool mirrored = _Reflection > 0.5;
                float aboveWater = input.positionWS.y - _WaterY;
                clip((mirrored ? -aboveWater : aboveWater) + 0.05);

                float3 albedo = input.colour.rgb;
                if (_VertexColourIsSrgb > 0.5) albedo = SRGBToLinear(albedo);

                float3 towardSun = normalize(_SunDirection.xyz);
                if (mirrored) towardSun.y = -towardSun.y;
                float lambert = saturate(dot(normalize(input.normalWS), towardSun));

                float3 light = _AmbientColour.rgb * _AmbientStrength
                             + _SunColour.rgb * _SunStrength * lambert;
                float3 colour = albedo * light;

                float distanceToCamera = distance(input.positionWS, _WorldSpaceCameraPos);
                float beyond = max(distanceToCamera - _HazeStart, 0.0);
                float haze = _HazeMax * (1.0 - exp(-beyond * _HazeDensity));
                colour = lerp(colour, _HazeColour.rgb, haze);

                if (mirrored) colour *= _ReflectionTint.rgb;
                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    // No Fallback, as SkyBackdrop.shader: a Built-in fallback renders magenta under URP.
}
