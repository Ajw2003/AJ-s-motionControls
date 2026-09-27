// The Lake Louise water: turquoise glacial body up close, a mirror further out.
//
// There is no reflection texture. What shows through where this is see-through is the mirrored
// scenery (LakeLouiseBackdrop.shader, _Reflection = 1, queue 1890) and the sky quad, which
// mirrors itself below the horizon (SkyBackdrop.shader, _MirrorBelowHorizon = 1). So the
// fresnel term here decides OPACITY: looking steeply down you see into the water; at grazing
// angles you see what the water reflects.
//
//   cos   = view ray's steepness (1 straight down, 0 grazing)
//   F     = f0 + (1 - f0) * (1 - cos)^power                   Schlick fresnel
//   F    *= 1 + (streakNoise - 0.5) * 2 * streakStrength       wind lanes across the lake
//   alpha = (1 - F) * bodyOpacity
//
// Same maths as add_water in tools/art/lake-louise/shaders.py. The streak noise is this file's
// value noise there and Blender's Perlin noise here, so streak SHAPES differ; strength matches.
//
// Keeps the contract the game's water scripts rely on:
//   _RippleLife, _RippleSpeed (WaterRipples.PushTuning) and the global _Ripples/_RippleCount
//   (WaterRipple.hlsl) - skip rings draw as on the Bootstrap water, and also raise opacity so
//   they still read on the mirror-like far water;
//   _ZWrite (PlaceholderArt.SetWaterDepthWrite) - off normally, on during a sink.
Shader "RockSkipping/LakeLouiseWater"
{
    Properties
    {
        _BodyColour ("Body colour, near", Color) = (0.122, 0.604, 0.639, 1)
        _BodyFarColour ("Body colour, far", Color) = (0.247, 0.663, 0.690, 1)
        _BodyFarDistance ("Distance to far colour (m)", Float) = 300
        _BodyOpacity ("Body opacity", Range(0, 1)) = 0.97
        _F0 ("Fresnel at normal incidence", Range(0, 1)) = 0.02
        _FresnelPower ("Fresnel power", Range(1, 8)) = 4
        _StreakStrength ("Streak strength", Range(0, 1)) = 0.18
        _StreakScale ("Streak scale (across, along)", Vector) = (0.016, 0.24, 0, 0)

        _HazeColour ("Haze colour", Color) = (0.706, 0.804, 0.937, 1)
        _HazeStart ("Haze start (m)", Float) = 380
        _HazeDensity ("Haze density (per m)", Float) = 0.0022
        _HazeMax ("Haze max", Range(0, 1)) = 0.4

        _CrestColour ("Ripple crest colour", Color) = (0.75, 0.92, 0.94, 1)
        _TroughDarkness ("Ripple trough darkening", Range(0, 1)) = 0.35
        _RippleOpacity ("Ripple opacity boost", Range(0, 1)) = 0.6
        _RippleLife ("Ripple life (s)", Float) = 2.5
        _RippleSpeed ("Ripple front speed (m/s)", Float) = 6
        _RippleWidth ("Ripple band width (m)", Float) = 3
        _RippleWavelength ("Ripple wavelength (m)", Float) = 2.5

        [HideInInspector] _ZWrite ("ZWrite", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            // PlaceholderArt.WaterQueue. Deliberately not the Transparent queue: the scenery
            // (1900) and its reflection (1890) must be drawn first, and the stones (3100) after.
            "Queue" = "Geometry"
        }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Declares the global _Ripples/_RippleCount and RippleWave(); shared with the
            // Shader Graph water so both draw identical rings.
            #include "WaterRipple.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BodyColour;
                float4 _BodyFarColour;
                float _BodyFarDistance;
                float _BodyOpacity;
                float _F0;
                float _FresnelPower;
                float _StreakStrength;
                float4 _StreakScale;
                float4 _HazeColour;
                float _HazeStart;
                float _HazeDensity;
                float _HazeMax;
                float4 _CrestColour;
                float _TroughDarkness;
                float _RippleOpacity;
                float _RippleLife;
                float _RippleSpeed;
                float _RippleWidth;
                float _RippleWavelength;
                float _ZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash(i);
                float b = Hash(i + float2(1, 0));
                float c = Hash(i + float2(0, 1));
                float d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 toCamera = _WorldSpaceCameraPos - input.positionWS;
                float distanceToCamera = length(toCamera);
                float steepness = saturate(toCamera.y / max(distanceToCamera, 1e-4));

                float fresnel = _F0 + (1.0 - _F0) * pow(1.0 - steepness, _FresnelPower);

                // Streaks drift slowly along the lake; x is stretched so they lie across it.
                float2 streakUv = input.positionWS.xz * _StreakScale.xy + float2(0.0, _Time.y * 0.05);
                float streak = 1.0 + (ValueNoise(streakUv) - 0.5) * 2.0 * _StreakStrength;
                fresnel = saturate(fresnel * streak);

                float wave;
                RippleWave(input.positionWS, _RippleLife, _RippleSpeed, _RippleWidth, _RippleWavelength, wave);

                float3 body = lerp(_BodyColour.rgb, _BodyFarColour.rgb,
                                   saturate(distanceToCamera / _BodyFarDistance));
                body = lerp(body, _CrestColour.rgb, saturate(wave));
                body *= 1.0 - saturate(-wave) * _TroughDarkness;

                float beyond = max(distanceToCamera - _HazeStart, 0.0);
                body = lerp(body, _HazeColour.rgb, _HazeMax * (1.0 - exp(-beyond * _HazeDensity)));

                float alpha = (1.0 - fresnel) * _BodyOpacity;
                alpha = saturate(alpha + abs(wave) * _RippleOpacity);
                return half4(body, alpha);
            }
            ENDHLSL
        }
    }
}
