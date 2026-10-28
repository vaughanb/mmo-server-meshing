// The tint drawn over the screen while the camera is under the sea.
//
// Most of the underwater look is not here - it is the fog, which DemoUnderwater pulls
// in to a few metres and recolours, and the camera's background, which it swaps off the
// skybox. Fog is what makes the water read as a volume you are inside rather than a
// colour laid over the picture, and it costs nothing because the scene already has it.
//
// What fog cannot do is the near field. At a metre or two it has barely tinted anything,
// so the player's own back and the sand under them stay dry-looking while the horizon is
// deep green. This pass covers that: a flat tint over everything, heavier toward the edges
// of the screen, with a slow shimmer so it is not a dead sheet of colour.
//
// It is drawn as a screen-space quad **written straight in clip space**, ignoring its own
// transform. The camera's field of view changes with the kit's zoom and its aspect with
// the window, and a quad sized in metres to fit the frustum would have to be resized for
// both; one that writes its corners to the corners of clip space never has to care.
Shader "Demo/Underwater"
{
    Properties
    {
        _Tint ("Tint", Color) = (0.055, 0.29, 0.34, 0.72)
        // Driven by DemoUnderwater, not authored: 0 above the surface, 1 under it, with
        // the crossing blended over a fraction of a second.
        _Strength ("Submersion", Range(0, 1)) = 0
        _CentreDensity ("Centre Density", Range(0, 1)) = 0.45
        _EdgeDensity ("Edge Density", Range(0, 1)) = 0.78
        _ShimmerStrength ("Shimmer", Range(0, 0.5)) = 0.07
        _ShimmerScale ("Shimmer Scale", Float) = 7
        _ShimmerSpeed ("Shimmer Speed", Float) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Overlay"
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Underwater"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            // Always, because the quad is pinned to the near plane and must not be sorted
            // or depth-tested against the scene it is covering.
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // One block, or the SRP batcher drops this shader.
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half _Strength;
                half _CentreDensity;
                half _EdgeDensity;
                half _ShimmerStrength;
                float _ShimmerScale;
                float _ShimmerSpeed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // The mesh is a unit quad about its own origin, so doubling its corners
                // lands them exactly on the corners of clip space. UNITY_NEAR_CLIP_VALUE
                // rather than a literal: it is 1 on reversed-Z and -1 on OpenGL, and the
                // wrong one puts the quad outside the clip volume, where it is simply
                // never drawn and looks like the effect failing to switch on.
                output.positionCS = float4(input.positionOS.xy * 2, UNITY_NEAR_CLIP_VALUE, 1);
                output.uv = input.positionOS.xy + 0.5;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // 0 in the middle of the screen, 1 in the corners.
                float2 fromCentre = input.uv - 0.5;
                half toEdge = saturate(length(fromCentre) * 1.41421);
                half density = lerp(_CentreDensity, _EdgeDensity, toEdge * toEdge);

                // Two crossed sines at slightly different scales and speeds, so the tint
                // breathes instead of sitting still. Deliberately slow and shallow - this
                // is the suggestion of light moving through water, and anything stronger
                // reads as a screen effect rather than as being underneath something.
                float t = _Time.y * _ShimmerSpeed;
                half shimmer = sin(input.uv.x * _ShimmerScale + t * 1.3)
                             * sin(input.uv.y * _ShimmerScale * 0.83 - t);

                half alpha = saturate(_Tint.a * density + shimmer * _ShimmerStrength) * _Strength;
                return half4(_Tint.rgb, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
