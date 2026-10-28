// The Fantasy Props pack ships six shop signs - Sign_Blacksmith, Sign_Pub, Sign_Armory,
// Sign_Armory_2, Sign_Food, Sign_Potions - and they are the same mesh to the vertex, with
// the same UV0. What tells them apart is their SECOND UV set: on the board's front panel
// alone, each one indexes a different emblem out of a block of symbols tucked into the
// bottom-left corner of the cloth trim sheet - crossed swords, an anvil and hammer, two
// tankards, crossed axes, potion bottles, a bunch of vegetables. The pack gives that panel
// its own material slot, "MI_WoodenSign", precisely so it can be shaded this way.
//
// Point that slot at ordinary Lit - which samples UV0 and nothing else - and all six render
// as the same blank board, which is exactly what they did here for months. The emblem is
// not missing from the pack; it is in a channel Lit never reads.
//
// This is URP's Lit with the emblem composited over the wood.
Shader "OpenMMORPG/Demo/Prop Sign (Emblem)"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Colour", Color) = (1, 1, 1, 1)
        _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1.0
        // The pack's mask map: R is metallic, G is occlusion, A is smoothness.
        _MetallicGlossMap("Mask Map", 2D) = "white" {}
        _Metallic("Metallic", Range(0, 1)) = 1.0
        _Smoothness("Smoothness", Range(0, 1)) = 1.0
        _OcclusionStrength("Occlusion", Range(0, 1)) = 1.0

        // The sheet the emblems are drawn on, read through UV2. They are white symbols on
        // black, so the sheet is a mask and its own colour is never used.
        _EmblemMap("Emblem Sheet", 2D) = "black" {}
        _EmblemColor("Emblem Paint", Color) = (0.09, 0.07, 0.06, 1)
        _EmblemCutoff("Emblem Cutoff", Range(0, 1)) = 0.5
        // Which part of the sheet is the emblem block, as (uMin, vMin, uMax, vMax). See
        // the fragment shader for why a rectangle and not just a threshold.
        _EmblemRect("Emblem Block", Vector) = (0, 0, 0.625, 0.489)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            // 3.0 for the fragment derivatives the emblem mask needs.
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            // Forward+ hands the torches and lamps to a shader through this keyword and not through _ADDITIONAL_LIGHTS, which
            // is off under it. Without it a variant has no additional lights at all, and the demo's renderers are Forward+:
            // the board was lit by the sun, the moon and the ambient and by nothing that was burning, so at night it stood
            // black beside a torch-lit wall.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _EmblemMap_ST;
                half4 _BaseColor;
                half4 _EmblemColor;
                float4 _EmblemRect;
                half _BumpScale;
                half _Metallic;
                half _Smoothness;
                half _OcclusionStrength;
                half _EmblemCutoff;
            CBUFFER_END

            TEXTURE2D(_BaseMap);            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_EmblemMap);          SAMPLER(sampler_EmblemMap);

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float4 tangentOS    : TANGENT;
                float2 uv           : TEXCOORD0;
                // The emblem channel. This is also where Unity puts a baked lightmap's UVs,
                // so these signs must never be lightmapped: generating lightmap UVs on
                // import overwrites the very data the emblem is stored in.
                float2 emblemUV     : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float2 emblemUV     : TEXCOORD1;
                float3 positionWS   : TEXCOORD2;
                half3 normalWS      : TEXCOORD3;
                half4 tangentWS     : TEXCOORD4;
                float4 shadowCoord  : TEXCOORD5;
                half fogFactor      : TEXCOORD6;
                half3 vertexSH      : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = normals.normalWS;
                output.tangentWS = half4(normals.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.emblemUV = TRANSFORM_TEX(input.emblemUV, _EmblemMap);
                output.shadowCoord = GetShadowCoord(positions);
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                output.vertexSH = SampleSH(normals.normalWS);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 mask = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, input.uv);
                half4 emblem = SAMPLE_TEXTURE2D(_EmblemMap, sampler_EmblemMap, input.emblemUV);

                // The symbols are drawn white on black and the sheet is sRGB, so the green
                // channel is the mask. It is stepped rather than blended: these are flat
                // stencilled shapes with hard edges, and a smooth ramp over a 2K atlas
                // sampled at sign size turns the anvil's legs into grey mush.
                //
                // The rectangle is not belt and braces. The emblem block is one corner of a
                // sheet that is otherwise cloth, and the panels do not all sit inside it:
                // Sign_Food's UV2 overruns the block's right edge, and what lies past it is
                // pale banner cloth reading 0.99 in green - BRIGHTER than the symbols' own
                // 0.81 - so no threshold can tell the two apart and the board came out with
                // a black bar painted down its side. Measured on the sheet, the block is
                // u 0..0.625, v 0..0.489; outside that, nothing is emblem.
                float2 e = input.emblemUV;
                half inside = step(_EmblemRect.x, e.x) * step(e.x, _EmblemRect.z) *
                              step(_EmblemRect.y, e.y) * step(e.y, _EmblemRect.w);

                // Only the inner rectangle of the board is the emblem. The border around it,
                // the strip along the top and the skirt across the bottom are part of the
                // same mesh and the same material slot, and the pack gives all of them a
                // SINGLE PINNED UV2 - every vertex of the skirt reads (0.779, 0.256) of the
                // cell, every vertex of the border reads (0.787, 1.000). Those spots are
                // black in all six of the pack's own emblems, so it never shows there; put
                // new art on the board without knowing that and the skirt samples whatever
                // now covers that one texel and paints itself across the bottom of the sign.
                //
                // Pinned geometry has a UV derivative of exactly zero, and real emblem
                // surface never does, so that is what separates them - no constant to keep
                // in step with the art, and it is the same answer for any emblem sheet.
                half spread = fwidth(e.x) + fwidth(e.y);
                half mapped = step(1e-9h, spread);

                half painted = step(_EmblemCutoff, emblem.g) * inside * mapped;

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = lerp(baseMap.rgb * _BaseColor.rgb, _EmblemColor.rgb, painted);
                surface.alpha = 1.0h;
                surface.metallic = mask.r * _Metallic;
                // Paint sits flatter than bare wood, and letting the wood's gloss through
                // the emblem is what makes a decal read as a sticker rather than as paint.
                surface.smoothness = lerp(mask.a * _Smoothness, 0.08h, painted);
                surface.occlusion = lerp(1.0h, mask.g, _OcclusionStrength);
                surface.normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                surface.emission = 0.0h;
                surface.specular = 0.0h;
                surface.clearCoatMask = 0.0h;
                surface.clearCoatSmoothness = 0.0h;

                InputData lighting = (InputData)0;
                lighting.positionWS = input.positionWS;
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half sgn = input.tangentWS.w;
                half3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
                half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent, input.normalWS.xyz);
                lighting.normalWS = TransformTangentToWorld(surface.normalTS, tangentToWorld);
                lighting.normalWS = NormalizeNormalPerPixel(lighting.normalWS);
                lighting.viewDirectionWS = viewDirWS;
                lighting.shadowCoord = input.shadowCoord;
                lighting.fogCoord = input.fogFactor;
                lighting.vertexLighting = 0.0h;
                lighting.bakedGI = input.vertexSH;
                lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.shadowMask = half4(1, 1, 1, 1);

                half4 colour = UniversalFragmentPBR(lighting, surface);
                colour.rgb = MixFog(colour.rgb, input.fogFactor);
                return colour;
            }
            ENDHLSL
        }

        // Shadows and depth are the stock ones: none of them care about the emblem.
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
        UsePass "Universal Render Pipeline/Lit/Meta"
    }

    FallBack "Universal Render Pipeline/Lit"
}
