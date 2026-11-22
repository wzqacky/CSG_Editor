Shader "Custom/PixelArt/LabColorQuantize"
{
    Properties
    {   
        [IntRange] _BitsPerChannel ("Bits Per Channel", Range(1, 4)) = 4
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" } // For URP

        Pass
        {
            // These params control how the full-screen triangle is drawn
            ZTest Always // Full-screen drawing pass through the depth test
            ZWrite Off // Do not change the depth buffer
            Cull Off // Disable triangle culling based on facing normals

            // Entering the GPU program
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float _BitsPerChannel;
            CBUFFER_END

            // The four 2-bit sRGB levels are 0, 1/3, 2/3, and 1.
            // These are their corresponding linear RGB values.
            float LevelToLinear(int level, int levels)
            {
                float srgbLevel = (float)level / (levels - 1);
                return srgbLevel <= 0.04045
                    ? srgbLevel / 12.92
                    : pow((srgbLevel + 0.055) / 1.055, 2.4);
            }
    
            float LabF(float value)
            {
                return value > 0.008856452
                    ? pow(value, 1.0 / 3.0)
                    : 7.787037 * value + 16.0 / 116.0;
            }
            
            // Linear RGB -> Lab
            float3 LinearRgbToLab(float3 rgb)
            {
                // Linear RGB → XYZ, using the sRGB/D65 conversion matrix.
                float3 xyz = float3(
                    dot(rgb, float3(0.4124564, 0.3575761, 0.1804375)),
                    dot(rgb, float3(0.2126729, 0.7151522, 0.0721750)),
                    dot(rgb, float3(0.0193339, 0.1191920, 0.9503041))
                );

                // Normalize by the D65 reference white.
                xyz /= float3(0.95047, 1.0, 1.08883);

                // Lab conversion
                float fx = LabF(xyz.x);
                float fy = LabF(xyz.y);
                float fz = LabF(xyz.z);

                return float3(
                    116.0 * fy - 16.0, // Brightness
                    500.0 * (fx - fy), // Green-red
                    200.0 * (fy - fz) // Blue-yellow
                );
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 source = SAMPLE_TEXTURE2D_X(
                    _BlitTexture,
                    sampler_PointClamp,
                    input.texcoord.xy
                );

                // saturate clamps to 0...1
                float3 sourceLab = LinearRgbToLab(saturate(source.rgb));

                float bestDistance = 1e20;
                float3 bestColor = 0.0;

                // Palette is a 2-bit color quantization,
                // so each channel has 2^2=4 candidates, try all 64 combintaions
                int bits = clamp((int)round(_BitsPerChannel), 1, 4);
                int levels = 1 << bits; // 2 raised to the power of bits
                for (int r = 0; r < levels; r++)
                {
                    for (int g = 0; g < levels; g++)
                    {
                        for (int b = 0; b < levels; b++)
                        {
                            float3 candidate = float3(
                                LevelToLinear(r, levels),
                                LevelToLinear(g, levels),
                                LevelToLinear(b, levels)
                            );

                            float3 difference =
                                sourceLab - LinearRgbToLab(candidate);

                            float distanceSquared =
                                dot(difference, difference);

                            if (distanceSquared < bestDistance)
                            {
                                bestDistance = distanceSquared;
                                bestColor = candidate;
                            }
                        }
                    }
                }

                return float4(bestColor, source.a);
            }
            ENDHLSL
        }
    }
}