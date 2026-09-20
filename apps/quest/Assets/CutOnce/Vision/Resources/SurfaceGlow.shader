// The cube is only a rasterization proxy: colour is evaluated on the measured real
// surface on each eye ray. This is depth-constrained highlighting, not a semantic
// instance mask or a reconstruction of hidden geometry.
Shader "CutOnce/SurfaceGlow"
{
    Properties
    {
        _Tint ("Surface tint", Color) = (0.05, 0.65, 1.0, 0.22)
        _GridSpacing ("Surface grid spacing (metres)", Float) = 0.06
        _GridStrength ("Surface grid strength", Range(0,1)) = 0.22
        _EdgeStrength ("Measured silhouette strength", Range(0,1)) = 0.75
        _MinDepth ("Nearest reliable surface (metres)", Float) = 0.2
        _MaxDepth ("Farthest surface (metres)", Float) = 6.0
        _DepthTolerance ("Virtual surface depth tolerance (metres)", Float) = 0.002
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Blend SrcAlpha One, One OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Front

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _GridSpacing, _GridStrength, _EdgeStrength, _MinDepth, _MaxDepth, _DepthTolerance;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };
            struct SurfaceOutput
            {
                half4 colour : SV_Target;
                float depth : SV_Depth;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPos = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.worldPos);
                return o;
            }

            // Meta uses texture [0,1] and NDC [-1,1] on every graphics API.
            // UNITY_REVERSED_Z applies only to the Unity SV_Depth output below.
            bool ReadSurface(float3 eye, float3 ray, float3 seed, out float3 surface, out float2 uv)
            {
                float4 a = mul(_EnvironmentDepthReprojectionMatrices[unity_StereoEyeIndex], float4(eye, 1));
                float4 b = mul(_EnvironmentDepthReprojectionMatrices[unity_StereoEyeIndex], float4(ray, 0));
                float t = dot(seed - eye, ray);
                surface = seed;
                uv = 0.5;
                // Bounded reprojection work: depth camera and eye are not co-located.
                [unroll] for (int iteration = 0; iteration < 3; iteration++)
                {
                    float4 p = a + t * b;
                    if (p.w <= 1e-5) return false;
                    uv = p.xy / p.w * 0.5 + 0.5;
                    if (any(uv <= 0) || any(uv >= 1)) return false;
                    float raw = SampleEnvironmentDepth(uv);
                    if (!(raw > 0 && raw < 0.99999)) return false;
                    float ndc = raw * 2 - 1;
                    float denominator = b.z - ndc * b.w;
                    if (abs(denominator) < 1e-6) return false;
                    t = (ndc * a.w - a.z) / denominator;
                    if (!(t >= _MinDepth && t <= _MaxDepth)) return false;
                }
                surface = eye + ray * t;
                float4 finalProjection = a + t * b;
                if (finalProjection.w <= 1e-5) return false;
                float2 finalUv = finalProjection.xy / finalProjection.w * 0.5 + 0.5;
                if (any(finalUv <= 0) || any(finalUv >= 1)) return false;
                // Reject correspondences that alternate foreground/background at an edge.
                return all(abs(finalUv - uv) <= max(_EnvironmentDepthTexture_TexelSize.xy, 0.0001) * 1.5);
            }

            float SurfaceGrid(float3 surfacePosition, float3 normal)
            {
                float3 coord = surfacePosition / max(_GridSpacing, 0.01);
                float3 width = max(fwidth(coord), 0.0001);
                float3 gridLine = 1 - smoothstep(width * 0.5, width * 1.5, abs(frac(coord + 0.5) - 0.5));
                // Triplanar projection keeps a horizontal tabletop from becoming a solid stripe.
                float3 weights = pow(abs(normal), 8);
                weights /= max(weights.x + weights.y + weights.z, 0.0001);
                return dot(float3(max(gridLine.y, gridLine.z), max(gridLine.x, gridLine.z), max(gridLine.x, gridLine.y)), weights);
            }

            SurfaceOutput frag(Varyings i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                SurfaceOutput o;
                o.colour = 0;
                o.depth = 0;
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                float3 eye = _WorldSpaceCameraPos;
                float3 ray = normalize(i.worldPos - eye);
                float3 surfacePosition;
                float2 depthUv;
                bool valid = ReadSurface(eye, ray, i.worldPos, surfacePosition, depthUv);
                // Derivatives before discard keep valid neighbours in each raster quad.
                float3 tangentX = ddx(surfacePosition), tangentY = ddy(surfacePosition);
                float3 normal = normalize(cross(tangentX, tangentY) + 1e-12);
                float grid = SurfaceGrid(surfacePosition, normal);
                if (!valid) discard;
                float3 local = TransformWorldToObject(surfacePosition);
                if (any(abs(local) >= 0.5)) discard;

                float centre = SampleEnvironmentDepthLinear(depthUv);
                float2 texel = max(_EnvironmentDepthTexture_TexelSize.xy, 0.0001);
                float neighbours = max(max(
                    abs(SampleEnvironmentDepthLinear(depthUv + float2(texel.x, 0)) - centre),
                    abs(SampleEnvironmentDepthLinear(depthUv - float2(texel.x, 0)) - centre)), max(
                    abs(SampleEnvironmentDepthLinear(depthUv + float2(0, texel.y)) - centre),
                    abs(SampleEnvironmentDepthLinear(depthUv - float2(0, texel.y)) - centre)));
                float edge = smoothstep(max(0.025, centre * 0.015), max(0.06, centre * 0.04), neighbours);
                float strength = saturate(_Tint.a * (1 + grid * _GridStrength + edge * _EdgeStrength));
                o.colour = half4(lerp(_Tint.rgb, half3(0.6, 0.92, 1), edge * 0.5), strength);

                // Passthrough itself writes no Unity depth. Test against virtual geometry at
                // the measured surface, with only a small bias for coincident depth values.
                // Surface selection above still uses the unmodified measured position.
                float4 clipPosition = TransformWorldToHClip(surfacePosition - ray * max(_DepthTolerance, 0.001));
                o.depth = clipPosition.z / clipPosition.w;
            #if !UNITY_REVERSED_Z
                o.depth = (o.depth - UNITY_NEAR_CLIP_VALUE) / (1 - UNITY_NEAR_CLIP_VALUE);
            #endif
                return o;
            #else
                discard;
                return o;
            #endif
            }
            ENDHLSL
        }
    }
}
