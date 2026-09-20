// Paints the REAL surface inside a detection box, using the Quest's live depth. The result reads as
// segmentation — the whole laptop glows, the wall behind it does not — with no segmentation model:
//
//   for each pixel this box covers, ask Meta's environment depth twice:
//     is the real surface BEHIND the box's front face?   (visFront = 1)
//     is the real surface IN FRONT of the box's back face? (visBack = 0)
//   paint = visFront * (1 - visBack): blue exactly where something real sits inside the box.
//
// Occlusion semantics verified against the SDK 205 source (EnvironmentOcclusion.cginc):
// CalculateEnvironmentDepthOcclusion(P) is 1 when the environment is farther than P, 0 when nearer.
// With neither occlusion keyword (Editor, Link, no depth) both calls return 1, paint would be 0 —
// so that case draws a faint volume instead, and C# falls back to the box look anyway.
//
// AGENTS rule 4: single-pass instanced stereo macros throughout (the reprojection uses
// unity_StereoEyeIndex, so the stereo setup is not optional here). Rule 5: alpha blends
// One / OneMinusSrcAlpha. Colour is additive so the real object stays fully visible under the glow.
Shader "CutOnce/SurfaceGlow"
{
    Properties
    {
        _Tint ("Tint (a = how much passthrough the glow replaces)", Color) = (0.13, 0.83, 0.93, 0.35)
        _FrontBias ("Front sample bias", Float) = 0.0
        _BackBias ("Back sample bias (excludes the desk the object stands on)", Float) = 0.05
        _MinFront ("Nearest front sample (m)", Float) = 0.15
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Blend SrcAlpha One, One OneMinusSrcAlpha
        ZWrite Off
        Cull Front      // rasterise the BACK faces: every covered pixel knows where the ray leaves the box

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            // Pulls in URP Core.hlsl and the depth reprojection + sampling for the active eye.
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _FrontBias, _BackBias, _MinFront;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 objectPos : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 world = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(world);
                o.objectPos = v.positionOS.xyz;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // This fragment is on the box's back face. Walk the view ray back to where it ENTERED
                // the unit cube (slab test in object space; t = 1 is the back face itself). A camera
                // inside the box clamps to its own position.
                float3 camObj = TransformWorldToObject(_WorldSpaceCameraPos);
                float3 dir = i.objectPos - camObj;
                float3 inv = 1.0 / dir;                       // infinities from axis-parallel rays fall out of min/max
                float3 tA = (-0.5 - camObj) * inv;
                float3 tB = (0.5 - camObj) * inv;
                float3 nearT = min(tA, tB);
                float tEntry = clamp(max(max(nearT.x, nearT.y), nearT.z), 0.0, 1.0);

                float3 frontWorld = TransformObjectToWorld(camObj + dir * tEntry);
                float3 backWorld = TransformObjectToWorld(i.objectPos);

                // With the eye inside the box (leaning over a table, a person right in front of you)
                // the entry point is the eye itself, and reprojecting a point at zero depth is
                // meaningless. Keep the front sample at least _MinFront in front of the eye.
                float3 toBack = backWorld - _WorldSpaceCameraPos;
                float backDist = length(toBack);
                float frontDist = distance(frontWorld, _WorldSpaceCameraPos);
                if (frontDist < _MinFront) frontWorld = _WorldSpaceCameraPos + toBack * (min(_MinFront, backDist) / max(backDist, 1e-4));

            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                float visFront = CalculateEnvironmentDepthOcclusion(frontWorld, _FrontBias);
                // A positive bias on the back sample treats a surface lying ON the back face — the desk
                // the bottle stands on, the wall a poster hangs on — as "behind", so it is not painted.
                float visBack = CalculateEnvironmentDepthOcclusion(backWorld, _BackBias);
                float paint = saturate(visFront * (1.0 - visBack));
            #else
                // Depth supported but not delivering yet (first seconds, waking from sleep): draw
                // nothing rather than a solid cube. C# swaps to the box look meanwhile.
                float paint = 0.0;
            #endif

                // Additive colour, linear in paint; alpha is how much passthrough the glow replaces,
                // kept low so the real object stays visible under it (AGENTS rules 5 and 6).
                half4 c;
                c.rgb = _Tint.rgb;
                c.a = saturate(_Tint.a * paint);
                return c;
            }
            ENDHLSL
        }
    }
}
