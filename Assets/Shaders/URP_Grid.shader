Shader "WaterCity/Grid/URPGrid"
{
    Properties
    {
        _LineColor("Line Color", Color) = (0,0,0,0.7)
        _LineOpacity("Line Opacity", Range(0,1)) = 0.7
        _LineColorMode("Line Color Mode", Float) = 0.0
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
            float  _LineOpacity;
            float  _LineColorMode;
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

            // Sample cell: rgb = color, a = priority (0 = Empty)
            float4 SampleCellData(int2 cell, int2 size)
            {
                cell = clamp(cell, int2(0, 0), size - int2(1, 1));
                float2 uv = (float2(cell) + 0.5) / float2(size);
                return SAMPLE_TEXTURE2D(_CellMap, sampler_CellMap, uv);
            }

            // Get line color based on two adjacent cells
            // Mode: 0 = Solid, 1 = Blend, 2 = Priority
            float3 GetLineColor(float4 cellA, float4 cellB)
            {
                // Solid mode: always use _LineColor
                if (_LineColorMode < 0.5)
                    return _LineColor.rgb;

                // Check if cells are Empty (priority = 0)
                bool aEmpty = cellA.a < 0.01;
                bool bEmpty = cellB.a < 0.01;

                // Both empty: use solid line color
                if (aEmpty && bEmpty)
                    return _LineColor.rgb;

                // One empty: use the non-empty cell's color
                if (aEmpty)
                    return cellB.rgb;
                if (bEmpty)
                    return cellA.rgb;

                // Both have colors
                if (_LineColorMode < 1.5)
                {
                    // Blend mode: average the two colors
                    return (cellA.rgb + cellB.rgb) * 0.5;
                }
                else
                {
                    // Priority mode: use higher priority cell
                    return (cellA.a >= cellB.a) ? cellA.rgb : cellB.rgb;
                }
            }

            float4 frag(Varyings IN) : SV_Target
            {
                int2   size    = (int2)_SizeXY.xy;
                float2 uv01    = saturate(IN.uv);
                float2 gridPos = uv01 * float2(size);
                float2 cellUV  = frac(gridPos);
                int2   cell    = (int2)floor(gridPos);

                float2 distFrac = min(cellUV, 1.0 - cellUV);
                float2 distM    = distFrac * _CellSize;
                float  halfW    = 0.5 * _LineWidth;
                float  dv       = distM.x - halfW;
                float  dh       = distM.y - halfW;

                // Smooth min creates rounded corners at line intersections
                float d        = smin(dv, dh, max(_CornerRadius, 0.0));
                float aa       = max(fwidth(d), 1e-4);
                float lineMask = saturate(0.5 - d / aa);

                // Sample current cell
                float4 cellData = SampleCellData(cell, size);

                // Apply neighbor fade to fill color
                float3 fillRgb = cellData.rgb;
                if (_NeighborFade > 0.001)
                {
                    float4 left  = SampleCellData(cell + int2(-1, 0), size);
                    float4 right = SampleCellData(cell + int2( 1, 0), size);
                    float4 down  = SampleCellData(cell + int2( 0,-1), size);
                    float4 up    = SampleCellData(cell + int2( 0, 1), size);
                    float3 avgN  = (left.rgb + right.rgb + down.rgb + up.rgb) * 0.25;
                    fillRgb = lerp(fillRgb, avgN, _NeighborFade);
                }

                // Sample neighbors for line coloring
                float4 leftCell  = SampleCellData(cell + int2(-1, 0), size);
                float4 rightCell = SampleCellData(cell + int2( 1, 0), size);
                float4 downCell  = SampleCellData(cell + int2( 0,-1), size);
                float4 upCell    = SampleCellData(cell + int2( 0, 1), size);

                // Compute line color
                float3 lineRgb;
                if (_LineColorMode < 0.5)
                {
                    // Solid mode
                    lineRgb = _LineColor.rgb;
                }
                else
                {
                    // Get horizontal neighbor (left or right based on position)
                    float4 hNeighbor = (cellUV.x < 0.5) ? leftCell : rightCell;
                    // Get vertical neighbor (down or up based on position)
                    float4 vNeighbor = (cellUV.y < 0.5) ? downCell : upCell;

                    // Get line colors for each edge direction
                    float3 hLineColor = GetLineColor(cellData, hNeighbor);
                    float3 vLineColor = GetLineColor(cellData, vNeighbor);

                    // At corners, blend both edge colors
                    // Use distance to edge (not signed distance) for weighting
                    float hDist = abs(distM.y);  // Distance to horizontal edge
                    float vDist = abs(distM.x);  // Distance to vertical edge

                    // Closer to vertical edge = more vertical line color
                    // Closer to horizontal edge = more horizontal line color
                    float blend = saturate(vDist / max(vDist + hDist, 0.001));
                    lineRgb = lerp(vLineColor, hLineColor, blend);
                }

                // Fill alpha (0 if fill disabled)
                float fillA = _CellOpacity * (_EnableFill > 0.5 ? 1.0 : 0.0);
                float lineA = _LineOpacity;

                // Blend fill and line
                float3 rgb = lerp(fillRgb, lineRgb, lineMask);
                float  a   = lerp(fillA, lineA, lineMask);

                return float4(rgb, a);
            }
            ENDHLSL
        }
    }
}
