// The props in the Fantasy Props pack are painted with vertex colours over a neutral
// trim sheet: the sheet carries the wear, the scratches and the shading, and the mesh
// carries the hue. The pack names that material "MI_Trim_Props_Vertex" and ships no
// shader for it, and URP's own Lit ignores vertex colour entirely — so pointed at Lit,
// every potion, jar, vegetable and book renders as the bare grey band of the sheet.
//
// This is URP's Lit with one line added: albedo is multiplied by the vertex colour.
Shader "OpenMMORPG/Demo/Prop Trim (Vertex Colour)"
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
        // See the comment on surface.albedo below for what this is for.
        _SheetGain("Sheet Gain", Float) = 1.0
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
            #pragma target 2.0
            #pragma vertex Vertex
            #pragma fragment Fragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            // Forward+ hands the torches and lamps to a shader through this keyword and not through _ADDITIONAL_LIGHTS, which
            // is off under it. Without it a variant has no additional lights at all, and the demo's renderers are Forward+.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _BumpScale;
                half _Metallic;
                half _Smoothness;
                half _OcclusionStrength;
                half _SheetGain;
            CBUFFER_END

            TEXTURE2D(_BaseMap);            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float4 tangentOS    : TANGENT;
                float2 uv           : TEXCOORD0;
                float2 lightmapUV   : TEXCOORD1;
                half4 color         : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS               : SV_POSITION;
                float2 uv                       : TEXCOORD0;
                float3 positionWS               : TEXCOORD1;
                half3 normalWS                  : TEXCOORD2;
                half4 tangentWS                 : TEXCOORD3;
                half4 color                     : TEXCOORD4;
                float4 shadowCoord              : TEXCOORD5;
                half fogFactor                  : TEXCOORD6;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 7);
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
                output.color = input.color;
                output.shadowCoord = GetShadowCoord(positions);
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
                OUTPUT_SH(normals.normalWS, output.vertexSH);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 mask = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, input.uv);

                SurfaceData surface = (SurfaceData)0;
                // The one line this shader exists for, plus a gain.
                //
                // The gain is there because the sheet's paintable region is a mid grey — it
                // carries scratches and shading around a neutral, and the vertex colour is
                // meant to supply the actual colour. Multiplying the two therefore lands at
                // about half the brightness the paint asks for. On a dielectric that reads
                // as shading and is fine; on a metal it is not, because a metal's albedo IS
                // its reflected colour, so gold multiplied down to a quarter reads as dirty
                // iron. Dividing by the region's own grey puts the paint back where it was
                // authored - and note that this multiply happens in LINEAR space, so the
                // divisor is the linear value of that grey, not the sRGB one. Left at 1 it
                // changes nothing.
                surface.albedo = baseMap.rgb * _BaseColor.rgb * input.color.rgb * _SheetGain;
                surface.alpha = 1.0h;
                surface.metallic = mask.r * _Metallic;
                surface.smoothness = mask.a * _Smoothness;
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
                lighting.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, lighting.normalWS);
                lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.shadowMask = SAMPLE_SHADOWMASK(input.lightmapUV);

                half4 colour = UniversalFragmentPBR(lighting, surface);
                colour.rgb = MixFog(colour.rgb, input.fogFactor);
                return colour;
            }
            ENDHLSL
        }

        // Shadows and depth are the stock ones: neither cares about colour, and a prop
        // that lit correctly but cast no shadow would be its own kind of wrong.
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
        UsePass "Universal Render Pipeline/Lit/Meta"
    }

    FallBack "Universal Render Pipeline/Lit"
}
