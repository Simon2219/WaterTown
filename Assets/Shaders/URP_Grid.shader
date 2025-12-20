Shader "WaterCity/Grid/URPGrid"
{
    Properties
    {
        _LineColor("Line Color", Color) = (0,0,0,0.7)
        _LineWidth("Line Width (m)", Float) = 0.05
        _CellSize("Cell Size (m)", Float) = 1.0
        _EnableFill("Enable Cell Fill", Float) = 1.0
        _CellOpacity("Cell Opacity", Range(0,1)) = 0.35
        _NeighborFade("Neighbor Fade", Range(0,1)) = 0.0
        _CellMap("Cell Map (sizeX x sizeY)", 2D) = "white" {}
        _GridOrigin("World Origin", Vector) = (0,0,0,0)
        _SizeXY("Grid Size (X,Y)", Vector) = (16,16,0,0)
        _LevelY("Level World Y", Float) = 0.0

        // Line intersection rounding
        _CornerRadius("Corner Radius (m)", Float) = 0.05

        // Depth helpers
        _YBias("Vertical Bias (m)", Float) = 0.005
        // Unity CompareFunction enum: 0 Disabled, 1 Never, 2 Less, 3 Equal, 4 LEqual, 5 Greater, 6 NotEqual, 7 GEqual, 8 Always
        _ZTestMode("ZTest Mode", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        ZTest [_ZTestMode]

        Pass
        {
            Name "URPGrid"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
            };

            TEXTURE2D(_CellMap);
            SAMPLER(sampler_CellMap);

            float4 _LineColor;
            float  _LineWidth;
            float  _CellSize;
            float4 _GridOrigin;
            float4 _SizeXY;
            float  _LevelY;
            float  _EnableFill;
            float  _CellOpacity;
            float  _NeighborFade;

            float  _CornerRadius;

            float  _YBias;

            // Smooth union for SDFs
            float smin(float a, float b, float k)
            {
                float h = saturate(0.5 + 0.5 * (b - a) / max(k, 1e-5));
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                // Correct: transform object space to world space
                float3 world = TransformObjectToWorld(IN.positionOS.xyz);
                world.y = _LevelY + _YBias; // lift a tiny bit to avoid z-fight

                OUT.worldPos = world;
                OUT.positionCS = TransformWorldToHClip(world);
                OUT.uv = IN.uv; // 0..1 across the quad
                return OUT;
            }

            float4 SampleCellColor(int2 cell)
            {
                float2 size = _SizeXY.xy;
                float2 uv = (float2(cell) + 0.5) / size;
                return SAMPLE_TEXTURE2D(_CellMap, sampler_CellMap, uv);
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float2 size = _SizeXY.xy;
                float2 uv01 = saturate(IN.uv);
                float2 gridPos = uv01 * size;   // [0..size)
                float2 cellUV  = frac(gridPos); // 0..1 in cell
                int2   cell    = (int2)floor(gridPos);

                float2 distFrac  = min(cellUV, 1.0 - cellUV);   // distance to cell edges (0..0.5)
                float2 distM     = distFrac * _CellSize;        // meters
                float  halfW     = 0.5 * _LineWidth;
                float  dv        = distM.x - halfW;
                float  dh        = distM.y - halfW;

                // Smooth min creates rounded corners at line intersections
                float  d         = smin(dv, dh, max(_CornerRadius, 0.0));
                float  aa        = max(fwidth(d), 1e-4);
                float  lineMask  = saturate(0.5 - d / aa);

                float4 cellCol = SampleCellColor(cell);
                if (_NeighborFade > 0.001)
                {
                    int2 sz = (int2)size;
                    int2 cx = cell;
                    int2 left  = int2(max(cx.x - 1, 0), cx.y);
                    int2 right = int2(min(cx.x + 1, sz.x - 1), cx.y);
                    int2 down  = int2(cx.x, max(cx.y - 1, 0));
                    int2 up    = int2(cx.x, min(cx.y + 1, sz.y - 1));
                    float4 avgN = (SampleCellColor(left)+SampleCellColor(right)+SampleCellColor(down)+SampleCellColor(up))*0.25;
                    cellCol = lerp(cellCol, avgN, _NeighborFade);
                }

                float4 fillCol = cellCol;
                float4 lineCol = _LineColor;

                // Fill alpha: base alpha * cell opacity (0 if fill disabled)
                float fillA = _CellOpacity * (_EnableFill > 0.5 ? 1.0 : 0.0);

                // Blend colors and alphas based on line mask
                float3 rgb = lerp(fillCol.rgb, lineCol.rgb, lineMask);
                float  a   = lerp(fillA, lineCol.a, lineMask);
                
                return float4(rgb, a);
            }
            ENDHLSL
        }
    }
}
