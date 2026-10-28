#ifndef DEMO_WIND_FOLIAGE_INCLUDED
#define DEMO_WIND_FOLIAGE_INCLUDED

// Wind for the island's foliage, shared by every pass of OpenMMORPG/Demo/Wind Foliage.
//
// Included after LitInput.hlsl (which brings in Core.hlsl) and before the stock URP pass file. It
// does not rewrite those pass files; it redirects the three position transforms they call to
// versions that push the vertex first. That keeps the shader a thin layer over Lit - ForwardLit,
// GBuffer, DepthOnly, DepthNormals and ShadowCaster all move the same way, which they have to:
// a pass that left the vertex where the model put it would draw its depth, its shadow or its
// SSAO normals a few centimetres away from the leaves it is meant to describe.
//
// Everything the wind needs comes from globals that DemoWind sets each frame. With no DemoWind in
// the scene `_DemoWind.z` is zero and every function here returns at once, so a scene that is not
// meant to blow (the menu hillside, the icon rigs, the animation bench) is exactly plain Lit.

float4 _DemoWind;        // xy = unit direction the wind blows towards on the ground, z = strength, w = phase
float4 _DemoWindTree;    // x = metres a vertex at the reference height leans, y = reference height, z = leaf flutter, w = cap on the height factor
float4 _DemoWindSmall;   // the same for plants and grass
float4 _DemoWindGround;  // x,y = terrain origin on x/z, z = uv scale on x, w = uv offset
float4 _DemoWindGround2; // x = terrain origin on y, y = terrain height range, z = uv scale on z, w = 1 once a heightmap is bound
TEXTURE2D(_DemoWindHeight);
SAMPLER(sampler_DemoWindHeight);

/// How far the wind moves a vertex, in world space.
///
/// Two ideas decide how it looks:
///
/// * A vertex bends by how high it stands above its own base, squared, so the foot stays planted
///   and the tip moves most - a trunk that bends, not a sticker that slides. A tree measures that
///   from its pivot. Grass cannot: the terrain welds every plant in a patch into one mesh, so the
///   pivot of a blade is the pivot of the patch. Anything keyworded _WIND_GROUND therefore measures
///   from the ground itself, read out of the heightmap DemoWind uploads.
///
/// * The push is a wave travelling across the island, not a number per object. Position along the
///   wind picks the phase, so a gust visibly rolls through a stand of grass and neighbours are
///   out of step with each other without any of them having a phase of its own. That is also what
///   makes the combined meshes work: they have no per-plant identity to hang a phase on.
float3 DemoWindOffset(float3 positionWS)
{
    float3 offset = 0;
    float strength = _DemoWind.z;

    // One exit, and the early-out is a condition rather than a return: wind that is off (no
    // DemoWind, or a scene with no terrain bound for the ground-relative kind) costs one branch.
    #if defined(_WIND_GROUND)
        bool active = strength > 0.0001 && _DemoWindGround2.w > 0.5;
    #else
        bool active = strength > 0.0001;
    #endif

    UNITY_BRANCH
    if (active)
    {
        #if defined(_WIND_GROUND)
            float2 uv = (positionWS.xz - _DemoWindGround.xy) * float2(_DemoWindGround.z, _DemoWindGround2.z) + _DemoWindGround.w;
            float groundY = SAMPLE_TEXTURE2D_LOD(_DemoWindHeight, sampler_DemoWindHeight, uv, 0).r * _DemoWindGround2.y + _DemoWindGround2.x;
            float height = positionWS.y - groundY;
            float4 config = _DemoWindSmall;
        #else
            float height = positionWS.y - GetObjectToWorldMatrix()._m13;
            float4 config = _DemoWindTree;
        #endif

        height = max(height, 0.0);
        float bend = height / max(config.y, 0.01);
        // Capped (config.w), because the square would otherwise send a plant twice the reference
        // height four times as far, and the tall ones are exactly the stiff ones. Above the cap the
        // plant leans no further, so the cap also sets how far the tallest thing on the island moves.
        bend = min(bend * bend, max(config.w, 0.01));

        float2 dir = _DemoWind.xy;
        float2 side = float2(-dir.y, dir.x);
        float phase = _DemoWind.w;
        float along = dot(positionWS.xz, dir);
        float across = dot(positionWS.xz, side);

        // A long swell, a quicker sway riding on it, and a slower envelope that decides how hard
        // the sway is blowing. The lean never goes negative for long: wind pushes one way, and what
        // the waves vary is how hard.
        float swell = sin(along * 0.11 - phase * 0.45 + across * 0.04);
        float sway = sin(along * 0.42 - phase * 1.55 + across * 0.19 + 1.3);
        float gust = 0.5 + 0.5 * sin(along * 0.07 - phase * 0.30 + 2.0);
        float lean = 0.55 + 0.30 * swell + 0.35 * gust * sway;
        float lateral = 0.35 * (0.5 + 0.5 * gust) * sin(along * 0.33 - phase * 1.2 + across * 0.31 + 4.0);

        offset.xz = (dir * lean + side * lateral) * (config.x * strength * bend);

        #if defined(_WIND_LEAF)
            // Leaves shiver on top of the bend. Phase comes from where the vertex is in space, so
            // neighbouring leaves are out of step; the amplitude follows the gust so a calm moment
            // is calm. Faded in over the first third of a metre so a blade of grass keeps its foot.
            float rooted = saturate(height * 3.0);
            float f = phase * 3.2 + dot(positionWS, float3(1.9, 2.3, 1.7));
            float g = phase * 2.3 + dot(positionWS, float3(-2.1, 1.7, 2.9)) + 1.7;
            offset += float3(sin(f), 0.5 * sin(g), sin(f * 0.7 + g)) * (config.z * strength * (0.6 + 0.4 * gust) * rooted);
        #endif
    }

    return offset;
}

float3 DemoWindObjectToWorld(float3 positionOS)
{
    float3 positionWS = TransformObjectToWorld(positionOS);
    return positionWS + DemoWindOffset(positionWS);
}

float4 DemoWindObjectToHClip(float3 positionOS)
{
    return TransformWorldToHClip(DemoWindObjectToWorld(positionOS));
}

// A copy of the stock function in URP's ShaderVariablesFunctions.hlsl with the world position
// swapped for the pushed one.
VertexPositionInputs DemoWindVertexPositionInputs(float3 positionOS)
{
    VertexPositionInputs input;
    input.positionWS = DemoWindObjectToWorld(positionOS);
    input.positionVS = TransformWorldToView(input.positionWS);
    input.positionCS = TransformWorldToHClip(input.positionWS);

    float4 ndc = input.positionCS * 0.5f;
    input.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
    input.positionNDC.zw = input.positionCS.zw;
    return input;
}

// From here on, the pass files' calls land on the versions above. The functions above were written
// first, so they still reach the real ones.
#define GetVertexPositionInputs(positionOS) DemoWindVertexPositionInputs(positionOS)
#define TransformObjectToHClip(positionOS) DemoWindObjectToHClip(positionOS)
#define TransformObjectToWorld(positionOS) DemoWindObjectToWorld(positionOS)

#endif
