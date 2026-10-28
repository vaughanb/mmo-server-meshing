// A six-sided skybox that holds two skies at once and fades between them.
//
// Unity's own Skybox/6 Sided draws one set of six faces, so a day/night cycle built on it
// can only cut from one material to another, and the cut is visible. This is that shader
// with a second set of faces and a blend, which is what lets dusk actually happen.
//
// The structure is deliberately identical to the built-in one — same pass order, same
// per-face UVs, same HDR decode and tint. Unity draws a skybox by issuing one quad per
// pass in that fixed order, so the order here is not a matter of taste: get it wrong and
// the sky is inside out.
Shader "Demo/DayNightSkybox"
{
    Properties
    {
        _Tint ("Tint Colour", Color) = (0.5, 0.5, 0.5, 0.5)
        [Gamma] _Exposure ("Exposure", Range(0, 8)) = 1.0
        _Rotation ("Rotation", Range(0, 360)) = 0
        _Blend ("Night", Range(0, 1)) = 0

        // Overcast, driven by DemoSkyCycle from the weather. At 0 the sky is exactly what it was
        // before this existed. Towards 1 the sky is washed to its own luminance in a flat cool grey
        // and dimmed: a sky that is still blue is not an overcast sky however dark it is made, which
        // is why this is a desaturation and not just a tint.
        _Overcast ("Overcast", Range(0, 1)) = 0
        _OvercastTint ("Overcast Grey", Color) = (0.86, 0.90, 0.96, 1)
        _OvercastDim ("Overcast Brightness", Range(0, 1)) = 0.55

        [NoScaleOffset] _FrontTex ("Day Front [+Z]", 2D) = "grey" {}
        [NoScaleOffset] _BackTex ("Day Back [-Z]", 2D) = "grey" {}
        [NoScaleOffset] _LeftTex ("Day Left [+X]", 2D) = "grey" {}
        [NoScaleOffset] _RightTex ("Day Right [-X]", 2D) = "grey" {}
        [NoScaleOffset] _UpTex ("Day Up [+Y]", 2D) = "grey" {}
        [NoScaleOffset] _DownTex ("Day Down [-Y]", 2D) = "grey" {}

        [NoScaleOffset] _NightFrontTex ("Night Front [+Z]", 2D) = "grey" {}
        [NoScaleOffset] _NightBackTex ("Night Back [-Z]", 2D) = "grey" {}
        [NoScaleOffset] _NightLeftTex ("Night Left [+X]", 2D) = "grey" {}
        [NoScaleOffset] _NightRightTex ("Night Right [-X]", 2D) = "grey" {}
        [NoScaleOffset] _NightUpTex ("Night Up [+Y]", 2D) = "grey" {}
        [NoScaleOffset] _NightDownTex ("Night Down [-Y]", 2D) = "grey" {}
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        CGINCLUDE
        #include "UnityCG.cginc"

        half4 _Tint;
        half _Exposure;
        float _Rotation;
        half _Blend;
        half _Overcast;
        half4 _OvercastTint;
        half _OvercastDim;

        float3 RotateAroundYInDegrees (float3 vertex, float degrees)
        {
            float alpha = degrees * UNITY_PI / 180.0;
            float sina, cosa;
            sincos(alpha, sina, cosa);
            float2x2 m = float2x2(cosa, -sina, sina, cosa);
            return float3(mul(m, vertex.xz), vertex.y).xzy;
        }

        struct appdata_t
        {
            float4 vertex : POSITION;
            float2 texcoord : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 vertex : SV_POSITION;
            float2 texcoord : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        v2f vert (appdata_t v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 rotated = RotateAroundYInDegrees(v.vertex, _Rotation);
            o.vertex = UnityObjectToClipPos(rotated);
            o.texcoord = v.texcoord;
            return o;
        }

        half4 skybox_frag (v2f i, sampler2D dayTex, half4 dayTexHDR, sampler2D nightTex, half4 nightTexHDR)
        {
            half4 dayRaw = tex2D(dayTex, i.texcoord);
            half4 nightRaw = tex2D(nightTex, i.texcoord);
            half3 day = DecodeHDR(dayRaw, dayTexHDR);
            half3 night = DecodeHDR(nightRaw, nightTexHDR);
            half3 c = lerp(day, night, _Blend);
            c = c * _Tint.rgb * unity_ColorSpaceDouble.rgb;
            c *= _Exposure;
            // The same grade DemoSkyCycle.Grade applies to the fog colour, so the haze the sea fades
            // into stays the colour of the sky it meets at the horizon.
            const half3 lumaWeights = half3(0.2126, 0.7152, 0.0722);
            half3 grey = _OvercastTint.rgb / max(dot(_OvercastTint.rgb, lumaWeights), 1e-4);
            c = lerp(c, dot(c, lumaWeights) * grey, _Overcast) * lerp(1.0, _OvercastDim, _Overcast);
            return half4(c, 1);
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            sampler2D _FrontTex; half4 _FrontTex_HDR;
            sampler2D _NightFrontTex; half4 _NightFrontTex_HDR;
            half4 frag (v2f i) : SV_Target { return skybox_frag(i, _FrontTex, _FrontTex_HDR, _NightFrontTex, _NightFrontTex_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            sampler2D _BackTex; half4 _BackTex_HDR;
            sampler2D _NightBackTex; half4 _NightBackTex_HDR;
            half4 frag (v2f i) : SV_Target { return skybox_frag(i, _BackTex, _BackTex_HDR, _NightBackTex, _NightBackTex_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            sampler2D _LeftTex; half4 _LeftTex_HDR;
            sampler2D _NightLeftTex; half4 _NightLeftTex_HDR;
            half4 frag (v2f i) : SV_Target { return skybox_frag(i, _LeftTex, _LeftTex_HDR, _NightLeftTex, _NightLeftTex_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            sampler2D _RightTex; half4 _RightTex_HDR;
            sampler2D _NightRightTex; half4 _NightRightTex_HDR;
            half4 frag (v2f i) : SV_Target { return skybox_frag(i, _RightTex, _RightTex_HDR, _NightRightTex, _NightRightTex_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            sampler2D _UpTex; half4 _UpTex_HDR;
            sampler2D _NightUpTex; half4 _NightUpTex_HDR;
            half4 frag (v2f i) : SV_Target { return skybox_frag(i, _UpTex, _UpTex_HDR, _NightUpTex, _NightUpTex_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            sampler2D _DownTex; half4 _DownTex_HDR;
            sampler2D _NightDownTex; half4 _NightDownTex_HDR;
            half4 frag (v2f i) : SV_Target { return skybox_frag(i, _DownTex, _DownTex_HDR, _NightDownTex, _NightDownTex_HDR); }
            ENDCG
        }
    }

    Fallback Off
}
