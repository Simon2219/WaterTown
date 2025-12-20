Shader "WaterCity/Grid/URPGrid"
{
    Properties
    {
        _LineColor("Line Color", Color) = (0,0,0,0.7)
        _LineOpacity("Line Opacity", Range(0,1)) = 0.7
        _LineColorMode("Line Color Mode", Float) = 0.0
        _LineNeighborFade("Line Neighbor Fade", Range(0,1)) = 0.0
        _LinePriorityOverride("Line Priority Override", Float) = 1.0
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
            float  _LineNeighborFade;
            float  _LinePriorityOverride;
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

                // Apply neighbor fade to fill color (includes diagonals)
                float3 fillRgb = cellData.rgb;
                if (_NeighborFade > 0.001)
                {
                    float4 n0 = SampleCellData(cell + int2(-1, 0), size);
                    float4 n1 = SampleCellData(cell + int2( 1, 0), size);
                    float4 n2 = SampleCellData(cell + int2( 0,-1), size);
                    float4 n3 = SampleCellData(cell + int2( 0, 1), size);
                    float4 n4 = SampleCellData(cell + int2(-1,-1), size);
                    float4 n5 = SampleCellData(cell + int2( 1,-1), size);
                    float4 n6 = SampleCellData(cell + int2(-1, 1), size);
                    float4 n7 = SampleCellData(cell + int2( 1, 1), size);
                    float3 avgN = (n0.rgb + n1.rgb + n2.rgb + n3.rgb + n4.rgb + n5.rgb + n6.rgb + n7.rgb) * 0.125;
                    fillRgb = lerp(fillRgb, avgN, _NeighborFade);
                }

                // Compute line color
                float3 lineRgb = _LineColor.rgb;
                
                if (_LineColorMode > 0.5)
                {
                    // Priority mode with neighbor bleeding
                    // Position within grid in cell units (0.5 = cell center)
                    float2 posInCell = cellUV;
                    
                    // Max bleed distance in cell units (1.0 = can reach 1 cell away)
                    float maxBleed = _LineNeighborFade;
                    
                    if (maxBleed > 0.001)
                    {
                        // Sample all 9 cells (current + 8 neighbors including diagonals)
                        float4 cells[9];
                        float2 offsets[9];
                        
                        cells[0] = cellData;                                    offsets[0] = float2(0, 0);
                        cells[1] = SampleCellData(cell + int2(-1, 0), size);    offsets[1] = float2(-1, 0);
                        cells[2] = SampleCellData(cell + int2( 1, 0), size);    offsets[2] = float2( 1, 0);
                        cells[3] = SampleCellData(cell + int2( 0,-1), size);    offsets[3] = float2( 0,-1);
                        cells[4] = SampleCellData(cell + int2( 0, 1), size);    offsets[4] = float2( 0, 1);
                        cells[5] = SampleCellData(cell + int2(-1,-1), size);    offsets[5] = float2(-1,-1);
                        cells[6] = SampleCellData(cell + int2( 1,-1), size);    offsets[6] = float2( 1,-1);
                        cells[7] = SampleCellData(cell + int2(-1, 1), size);    offsets[7] = float2(-1, 1);
                        cells[8] = SampleCellData(cell + int2( 1, 1), size);    offsets[8] = float2( 1, 1);
                        
                        // Position relative to current cell center
                        float2 relPos = posInCell - 0.5;
                        
                        if (_LinePriorityOverride > 0.5)
                        {
                            // Priority Override: highest priority cell in range wins entirely
                            float bestPriority = -1.0;
                            float3 bestColor = _LineColor.rgb;
                            
                            for (int i = 0; i < 9; i++)
                            {
                                if (cells[i].a < 0.01) continue; // Skip empty cells
                                
                                // Distance from this pixel to cell center (in cell units)
                                float2 cellCenter = offsets[i];
                                float dist = length(relPos - cellCenter);
                                
                                // Bleed strength: 1 at cell center, 0 at maxBleed distance
                                float bleed = saturate(1.0 - dist / maxBleed);
                                
                                if (bleed > 0.001 && cells[i].a > bestPriority)
                                {
                                    bestPriority = cells[i].a;
                                    bestColor = cells[i].rgb;
                                }
                            }
                            
                            lineRgb = (bestPriority > 0) ? bestColor : _LineColor.rgb;
                        }
                        else
                        {
                            // Distance-weighted blend with priority as tiebreaker
                            float3 colorSum = float3(0, 0, 0);
                            float weightSum = 0;
                            float maxPriorityInRange = 0;
                            
                            // First pass: find max priority in range
                            for (int i = 0; i < 9; i++)
                            {
                                if (cells[i].a < 0.01) continue;
                                
                                float2 cellCenter = offsets[i];
                                float dist = length(relPos - cellCenter);
                                float bleed = saturate(1.0 - dist / maxBleed);
                                
                                if (bleed > 0.001)
                                    maxPriorityInRange = max(maxPriorityInRange, cells[i].a);
                            }
                            
                            // Second pass: blend colors, weighting by distance
                            // Only include cells with priority >= max - small epsilon (tiebreaker)
                            float prioThreshold = maxPriorityInRange - 0.01;
                            
                            for (int j = 0; j < 9; j++)
                            {
                                if (cells[j].a < prioThreshold) continue;
                                
                                float2 cellCenter = offsets[j];
                                float dist = length(relPos - cellCenter);
                                float bleed = saturate(1.0 - dist / maxBleed);
                                
                                if (bleed > 0.001)
                                {
                                    colorSum += cells[j].rgb * bleed;
                                    weightSum += bleed;
                                }
                            }
                            
                            lineRgb = (weightSum > 0.001) ? (colorSum / weightSum) : _LineColor.rgb;
                        }
                    }
                    else
                    {
                        // No bleeding, just use current cell color or default
                        lineRgb = (cellData.a > 0.01) ? cellData.rgb : _LineColor.rgb;
                    }
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
