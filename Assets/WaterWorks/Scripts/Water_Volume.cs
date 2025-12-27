using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace WaterWorks.Scripts
{
    /// <summary>
    /// Water Volume post-processing effect using Unity 6's RenderGraph API.
    /// Applies a fullscreen water distortion/tint effect.
    /// </summary>
    public class Water_Volume : ScriptableRendererFeature
    {
        [System.Serializable]
        public class Settings
        {
            public Material material;
            public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
        }

        public Settings settings = new();
        private WaterVolumePass _waterVolumePass;

        public override void Create()
        {
            if (settings.material == null)
            {
                settings.material = Resources.Load<Material>("Water_Volume");
            }

            _waterVolumePass = new WaterVolumePass(settings.material)
            {
                renderPassEvent = settings.renderPassEvent
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Skip if material is missing or camera is a preview/reflection camera
            if (settings.material == null) return;
            if (renderingData.cameraData.cameraType == CameraType.Preview) return;
            if (renderingData.cameraData.cameraType == CameraType.Reflection) return;

            renderer.EnqueuePass(_waterVolumePass);
        }

        protected override void Dispose(bool disposing)
        {
            _waterVolumePass?.Dispose();
        }

        /// <summary>
        /// Water Volume render pass using RenderGraph API
        /// </summary>
        private class WaterVolumePass : ScriptableRenderPass
        {
            private const string PassName = "Water Volume Pass";
            private readonly Material _material;

            public WaterVolumePass(Material material)
            {
                _material = material;
                // Use ProfilingSampler for GPU profiling
                profilingSampler = new ProfilingSampler(PassName);
            }

            public void Dispose()
            {
                // No managed resources to dispose in RenderGraph mode
            }

            /// <summary>
            /// Pass data container for RenderGraph
            /// </summary>
            private class PassData
            {
                public TextureHandle Source;
                public TextureHandle Destination;
                public Material Material;
            }

            /// <summary>
            /// Records the render graph pass - this is the new Unity 6 RenderGraph API
            /// </summary>
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_material == null) return;

                // Get frame resources from URP
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                // Get the active color texture (camera target)
                TextureHandle source = resourceData.activeColorTexture;

                // Create a temporary texture for the blit operation
                RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;

                TextureHandle destination = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    descriptor,
                    "_WaterVolumeTemp",
                    false
                );

                // Add the blit pass: source -> destination (with material)
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(PassName, out var passData, profilingSampler))
                {
                    passData.Source = source;
                    passData.Destination = destination;
                    passData.Material = _material;

                    // Declare texture usage
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        // Set the source texture for the shader
                        data.Material.SetTexture("_MainTex", data.Source);
                        
                        // Draw fullscreen quad with material
                        Blitter.BlitTexture(context.cmd, data.Source, new Vector4(1, 1, 0, 0), data.Material, 0);
                    });
                }

                // Blit back: destination -> source (copy result back to camera target)
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Water Volume Copy Back", out var passData, profilingSampler))
                {
                    passData.Source = destination;
                    passData.Destination = source;
                    passData.Material = null;

                    builder.UseTexture(destination, AccessFlags.Read);
                    builder.SetRenderAttachment(source, 0, AccessFlags.Write);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        Blitter.BlitTexture(context.cmd, data.Source, new Vector4(1, 1, 0, 0), 0, false);
                    });
                }
            }
        }
    }
}
