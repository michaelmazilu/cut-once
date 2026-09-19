// The room-scan look: a blue holographic grid over every known surface, edges brightened by
// fresnel, revealed by an expanding pulse ring (RoomGlow.cs drives _PulseOrigin/_PulseRadius).
//
// AGENTS rule 4: both passes carry the single-pass-instanced stereo macros (without them the Quest draws this in one
// eye only). Rule 5: the alpha channel blends One / OneMinusSrcAlpha. The colour is additive, but over passthrough the
// frame buffer's ALPHA decides how much of the glow shows, and with joint factors a 0.35 glow's alpha would be squared
// to 0.12: bright in the Game view, nearly invisible in the headset.
//
// Two SubShaders, same look: Unity picks the first when URP is active (fresh Unity 6 templates)
// and falls back to the Built-in one otherwise, so the material never shows up pink.
Shader "CutOnce/SheikahGlow"
{
    Properties
    {
        _Tint ("Tint", Color) = (0.15, 0.55, 1.0, 0.35)
        _GridScale ("Grid cells per metre", Float) = 4.0
        _GridLine ("Grid line width", Range(0.01, 0.3)) = 0.06
        _PulseOrigin ("Pulse origin (world)", Vector) = (0, 0, 0, 0)
        _PulseRadius ("Pulse radius (m)", Float) = 999.0
    }

    // ── URP ─────────────────────────────────────────────────────────────────────
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Blend SrcAlpha One, One OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _GridScale, _GridLine, _PulseRadius;
                float4 _PulseOrigin;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 normal : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.world = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.world);
                o.normal = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            float grid(float3 p, float3 n)
            {
                float3 an = abs(n);
                float2 uv = an.y > 0.5 ? p.xz : (an.x > 0.5 ? p.zy : p.xy);
                float2 cells = abs(frac(uv * _GridScale) - 0.5);
                return smoothstep(_GridLine, 0.0, min(cells.x, cells.y));
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);   // _WorldSpaceCameraPos is per eye
                float3 n = normalize(i.normal);
                float3 view = normalize(_WorldSpaceCameraPos - i.world);
                float fresnel = pow(1.0 - saturate(abs(dot(n, view))), 2.0);
                float g = grid(i.world, n);

                float dist = distance(i.world, _PulseOrigin.xyz);
                float revealed = smoothstep(_PulseRadius, _PulseRadius - 0.4, dist);
                float ring = smoothstep(0.35, 0.0, abs(dist - _PulseRadius)) * 2.0;

                float glow = (g * 0.9 + fresnel * 0.8 + 0.08) * revealed + ring;
                half4 c = _Tint;
                c.rgb *= glow;
                c.a = saturate(_Tint.a * glow);
                return c;
            }
            ENDHLSL
        }
    }

    // ── Built-in pipeline ───────────────────────────────────────────────────────
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Blend SrcAlpha One, One OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Tint;
            float _GridScale, _GridLine, _PulseRadius;
            float4 _PulseOrigin;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 view : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.view = WorldSpaceViewDir(v.vertex);
                return o;
            }

            float grid(float3 p, float3 n)
            {
                float3 an = abs(n);
                float2 uv = an.y > 0.5 ? p.xz : (an.x > 0.5 ? p.zy : p.xy);
                float2 cells = abs(frac(uv * _GridScale) - 0.5);
                return smoothstep(_GridLine, 0.0, min(cells.x, cells.y));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float fresnel = pow(1.0 - saturate(abs(dot(n, normalize(i.view)))), 2.0);
                float g = grid(i.world, n);

                float dist = distance(i.world, _PulseOrigin.xyz);
                float revealed = smoothstep(_PulseRadius, _PulseRadius - 0.4, dist);
                float ring = smoothstep(0.35, 0.0, abs(dist - _PulseRadius)) * 2.0;

                float glow = (g * 0.9 + fresnel * 0.8 + 0.08) * revealed + ring;
                fixed4 c = _Tint;
                c.rgb *= glow;
                c.a = saturate(_Tint.a * glow);
                return c;
            }
            ENDCG
        }
    }
}
