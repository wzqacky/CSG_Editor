Shader "Hidden/TexelSplat/Splat"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZTest LEqual
            ZWrite On
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_ProbeColor);
            TEXTURE2D(_ProbeDepth);

            float3 _ProbeOrigin;
            float3 _FaceForward;
            float3 _FaceRight;
            float3 _FaceUp;
            int _FaceSize;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float4 color : COLOR0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                uint size = (uint)_FaceSize;
                // instanceID chooses a pixel in the current face's texture
                uint2 cell = uint2(input.instanceID % size, input.instanceID / size);
                float d = LOAD_TEXTURE2D(_ProbeDepth, int2(cell)).r;

                if (d <= 0.0)
                {
                    output.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                    output.color = 0.0;
                    return output;
                }
                // The six rect vertices, vertexID selects which to draw
                float2 corners[6] =
                {
                    float2(0, 0), float2(1, 0), float2(0, 1),
                    float2(0, 1), float2(1, 0), float2(1, 1)
                };
                // A slight overlap hides holes between samples on one surface.
                float2 corner = (corners[input.vertexID] - 0.5) * 1.08;
                // URP renders the probe faces into bottom-left-origin textures.
                float2 uv = (float2(cell) + 0.5 + corner) / _FaceSize;
                float2 faceXY = uv * 2.0 - 1.0;
                float3 ray = _FaceForward +
                    faceXY.x * _FaceRight + faceXY.y * _FaceUp;
                float3 worldPosition = _ProbeOrigin + d * ray;

                output.positionCS = TransformWorldToHClip(worldPosition);
                output.color = LOAD_TEXTURE2D(_ProbeColor, int2(cell));
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return input.color;
            }
            ENDHLSL
        }
    }
}
