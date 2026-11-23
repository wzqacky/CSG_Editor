Shader "Hidden/TexelSplat/CopyLinearDepth"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float rawDepth = SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_PointClamp, input.texcoord.xy).r;

                // Zero is the invalid-texel marker in the splat shader.
                #if UNITY_REVERSED_Z
                    if (rawDepth <= 0.000001) return 0.0;
                #else
                    if (rawDepth >= 0.999999) return 0.0;
                #endif

                // At 90 degrees this face-forward eye depth equals cubemap
                // Chebyshev depth: the face ray's dominant component is 1.
                return LinearEyeDepth(rawDepth, _ZBufferParams);
            }
            ENDHLSL
        }
    }
}
