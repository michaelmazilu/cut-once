Shader "Hidden/CutOnce/YoloLetterbox"
{
    Properties
    {
        _MainTex ("Camera RGB", 2D) = "white" {}
        _ContentRect ("Image UV rectangle", Vector) = (0, 0, 1, 1)
        _EncodeSrgb ("Undo source sRGB sampling", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _ContentRect;
                float _EncodeSrgb;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
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
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }
            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = (input.uv - _ContentRect.xy) / _ContentRect.zw;
                if (any(uv < 0.0) || any(uv > 1.0)) return float4(114.0 / 255.0, 114.0 / 255.0, 114.0 / 255.0, 1.0);
                float3 rgb = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                if (_EncodeSrgb > 0.5)
                {
                    // Matches TextureToTensor.compute in the pinned Inference Engine package.
                    rgb = lerp(12.92 * rgb, 1.055 * pow(abs(rgb), 0.41666666666) - 0.055, rgb > 0.0031308);
                }
                return float4(rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
