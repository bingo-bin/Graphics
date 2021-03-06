using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        // String values
        const string m_RayGenIntegrationName = "RayGenIntegration";
        const string m_RayGenIntegrationTransparentName = "RayGenIntegrationTransparent";

        int m_RaytracingReflectionsFullResKernel;
        int m_RaytracingReflectionsHalfResKernel;
        int m_RaytracingReflectionsTransparentFullResKernel;
        int m_RaytracingReflectionsTransparentHalfResKernel;

        void InitRayTracedReflections()
        {
            ComputeShader reflectionShaderCS = m_Asset.renderPipelineRayTracingResources.reflectionRaytracingCS;

            m_RaytracingReflectionsFullResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsFullRes");
            m_RaytracingReflectionsHalfResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsHalfRes");
            m_RaytracingReflectionsTransparentFullResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsTransparentFullRes");
            m_RaytracingReflectionsTransparentHalfResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsTransparentHalfRes");
        }

        static RTHandle ReflectionHistoryBufferAllocatorFunction(string viewName, int frameIndex, RTHandleSystem rtHandleSystem)
        {
            return rtHandleSystem.Alloc(Vector2.one, TextureXR.slices, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, dimension: TextureXR.dimension,
                                        enableRandomWrite: true, useMipMap: false, autoGenerateMips: false,
                                        name: string.Format("{0}_ReflectionHistoryBuffer{1}", viewName, frameIndex));
        }

        void ReleaseRayTracedReflections()
        {
        }

        void RenderRayTracedReflections(HDCamera hdCamera, CommandBuffer cmd, RTHandle outputTexture, ScriptableRenderContext renderContext, int frameCount, bool transparent = false)
        {
            ScreenSpaceReflection reflectionSettings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();

            // Based on what the asset supports, follow the volume or force the right mode.
            if (m_Asset.currentPlatformRenderPipelineSettings.supportedRayTracingMode == RenderPipelineSettings.SupportedRayTracingMode.Both)
            {
                switch (reflectionSettings.mode.value)
                {
                    case RayTracingMode.Performance:
                    {
                        RenderReflectionsPerformance(hdCamera, cmd, outputTexture, renderContext, frameCount, transparent);
                    }
                    break;
                    case RayTracingMode.Quality:
                    {
                        RenderReflectionsQuality(hdCamera, cmd, outputTexture, renderContext, frameCount, transparent);
                    }
                    break;
                }
            }
            else if (m_Asset.currentPlatformRenderPipelineSettings.supportedRayTracingMode == RenderPipelineSettings.SupportedRayTracingMode.Quality)
            {
                RenderReflectionsQuality(hdCamera, cmd, outputTexture, renderContext, frameCount, transparent);
            }
            else
            {
                RenderReflectionsPerformance(hdCamera, cmd, outputTexture, renderContext, frameCount, transparent);
            }
        }

        void BindRayTracedReflectionData(CommandBuffer cmd, HDCamera hdCamera, RayTracingShader reflectionShader, ScreenSpaceReflection settings, LightCluster lightClusterSettings,
                                            RTHandle outputLightingBuffer, RTHandle outputHitPointBuffer)
        {
            // Grab the acceleration structures and the light cluster to use
            RayTracingAccelerationStructure accelerationStructure = RequestAccelerationStructure();
            HDRaytracingLightCluster lightCluster = RequestLightCluster();
            BlueNoise blueNoise = GetBlueNoiseManager();

            // Define the shader pass to use for the reflection pass
            cmd.SetRayTracingShaderPass(reflectionShader, "IndirectDXR");

            // Set the acceleration structure for the pass
            cmd.SetRayTracingAccelerationStructure(reflectionShader, HDShaderIDs._RaytracingAccelerationStructureName, accelerationStructure);

            // Global reflection parameters
            m_ShaderVariablesRayTracingCB._RaytracingIntensityClamp = settings.clampValue;
            m_ShaderVariablesRayTracingCB._RaytracingIncludeSky = settings.reflectSky.value ? 1 : 0;
            // Inject the ray generation data
            m_ShaderVariablesRayTracingCB._RaytracingRayMaxLength = settings.rayLength;
            m_ShaderVariablesRayTracingCB._RaytracingNumSamples = settings.sampleCount.value;
            // Set the number of bounces for reflections
            m_ShaderVariablesRayTracingCB._RaytracingMaxRecursion = settings.bounceCount.value;
            ConstantBuffer.PushGlobal(cmd, m_ShaderVariablesRayTracingCB, HDShaderIDs._ShaderVariablesRaytracing);

            // Inject the ray-tracing sampling data
            blueNoise.BindDitheredRNGData8SPP(cmd);

            // Set the data for the ray generation
            cmd.SetRayTracingTextureParam(reflectionShader, HDShaderIDs._SsrLightingTextureRW, outputLightingBuffer);
            cmd.SetRayTracingTextureParam(reflectionShader, HDShaderIDs._SsrHitPointTexture, outputHitPointBuffer);
            cmd.SetRayTracingTextureParam(reflectionShader, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
            cmd.SetRayTracingTextureParam(reflectionShader, HDShaderIDs._NormalBufferTexture, m_SharedRTManager.GetNormalBuffer());
            cmd.SetGlobalTexture(HDShaderIDs._StencilTexture, sharedRTManager.GetDepthStencilBuffer(), RenderTextureSubElement.Stencil);
            cmd.SetRayTracingIntParams(reflectionShader, HDShaderIDs._SsrStencilBit, (int)StencilUsage.TraceReflectionRay);

            // Set ray count tex
            RayCountManager rayCountManager = GetRayCountManager();
            cmd.SetRayTracingTextureParam(reflectionShader, HDShaderIDs._RayCountTexture, rayCountManager.GetRayCountTexture());

            // Bind the lightLoop data
            lightCluster.BindLightClusterData(cmd);

            // Note: Just in case, we rebind the directional light data (in case they were not)
            cmd.SetGlobalBuffer(HDShaderIDs._DirectionalLightDatas, m_LightLoopLightData.directionalLightData);

            // Evaluate the clear coat mask texture based on the lit shader mode
            RenderTargetIdentifier clearCoatMaskTexture = hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred ? m_GbufferManager.GetBuffersRTI()[2] : TextureXR.GetBlackTexture();
            cmd.SetRayTracingTextureParam(reflectionShader, HDShaderIDs._SsrClearCoatMaskTexture, clearCoatMaskTexture);

            // Set the data for the ray miss
            cmd.SetRayTracingTextureParam(reflectionShader, HDShaderIDs._SkyTexture, m_SkyManager.GetSkyReflection(hdCamera));
        }

        DeferredLightingRTParameters PrepareReflectionDeferredLightingRTParameters(HDCamera hdCamera)
        {
            DeferredLightingRTParameters deferredParameters = new DeferredLightingRTParameters();

            // Fetch the GI volume component
            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();
            RayTracingSettings rTSettings = hdCamera.volumeStack.GetComponent<RayTracingSettings>();

            // Make sure the binning buffer has the right size
            CheckBinningBuffersSize(hdCamera);

            // Generic attributes
            deferredParameters.rayBinning = true;
            deferredParameters.layerMask.value = (int)RayTracingRendererFlag.Reflection;
            deferredParameters.diffuseLightingOnly = false;
            deferredParameters.halfResolution = !settings.fullResolution;
            deferredParameters.rayCountType = (int)RayCountValues.ReflectionDeferred;

            // Camera data
            deferredParameters.width = hdCamera.actualWidth;
            deferredParameters.height = hdCamera.actualHeight;
            deferredParameters.viewCount = hdCamera.viewCount;

            // Compute buffers
            deferredParameters.rayBinResult = m_RayBinResult;
            deferredParameters.rayBinSizeResult = m_RayBinSizeResult;
            deferredParameters.accelerationStructure = RequestAccelerationStructure();
            deferredParameters.lightCluster = RequestLightCluster();

            // Shaders
            deferredParameters.gBufferRaytracingRT = m_Asset.renderPipelineRayTracingResources.gBufferRaytracingRT;
            deferredParameters.deferredRaytracingCS = m_Asset.renderPipelineRayTracingResources.deferredRaytracingCS;
            deferredParameters.rayBinningCS = m_Asset.renderPipelineRayTracingResources.rayBinningCS;

            // XRTODO: add ray binning support for single-pass
            if (deferredParameters.viewCount > 1 && deferredParameters.rayBinning)
            {
                deferredParameters.rayBinning = false;
            }

            deferredParameters.globalCB = m_ShaderVariablesRayTracingCB;
            deferredParameters.globalCB._RaytracingRayMaxLength = settings.rayLength;
            deferredParameters.globalCB._RaytracingIntensityClamp = settings.clampValue;
            deferredParameters.globalCB._RaytracingIncludeSky = settings.reflectSky.value ? 1 : 0;
            deferredParameters.globalCB._RaytracingPreExposition = 0;
            deferredParameters.globalCB._RaytracingDiffuseRay = 0;

            return deferredParameters;
        }

        void RenderReflectionsPerformance(HDCamera hdCamera, CommandBuffer cmd, RTHandle outputTexture, ScriptableRenderContext renderContext, int frameCount, bool transparent)
        {
            // Fetch the required resources
            BlueNoise blueNoise = GetBlueNoiseManager();
            RayTracingShader reflectionShaderRT = m_Asset.renderPipelineRayTracingResources.reflectionRaytracingRT;
            ComputeShader reflectionShaderCS = m_Asset.renderPipelineRayTracingResources.reflectionRaytracingCS;
            ComputeShader reflectionFilter = m_Asset.renderPipelineRayTracingResources.reflectionBilateralFilterCS;
            RTHandle intermediateBuffer0 = GetRayTracingBuffer(InternalRayTracingBuffers.RGBA0);
            RTHandle intermediateBuffer1 = GetRayTracingBuffer(InternalRayTracingBuffers.RGBA1);

            CoreUtils.SetRenderTarget(cmd, intermediateBuffer1, m_SharedRTManager.GetDepthStencilBuffer(), ClearFlag.Color, clearColor: Color.black);

            // Fetch all the settings
            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();
            LightCluster lightClusterSettings = hdCamera.volumeStack.GetComponent<LightCluster>();
            RayTracingSettings rtSettings = hdCamera.volumeStack.GetComponent<RayTracingSettings>();

            // Texture dimensions
            int texWidth = hdCamera.actualWidth;
            int texHeight = hdCamera.actualHeight;

            // Evaluate the dispatch parameters
            int areaTileSize = 8;
            int numTilesXHR = 0, numTilesYHR = 0;
            int currentKernel = 0;
            RenderTargetIdentifier clearCoatMaskTexture;

            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.RaytracingIntegrateReflection)))
            {
                // Fetch the new sample kernel
                if (settings.fullResolution)
                {
                    currentKernel = transparent ? m_RaytracingReflectionsTransparentFullResKernel : m_RaytracingReflectionsFullResKernel;
                }
                else
                {
                    currentKernel = transparent ? m_RaytracingReflectionsTransparentHalfResKernel : m_RaytracingReflectionsHalfResKernel;
                }

                // Inject the ray-tracing sampling data
                blueNoise.BindDitheredRNGData8SPP(cmd);

                // Bind all the required textures
                cmd.SetComputeTextureParam(reflectionShaderCS, currentKernel, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                cmd.SetComputeTextureParam(reflectionShaderCS, currentKernel, HDShaderIDs._NormalBufferTexture, m_SharedRTManager.GetNormalBuffer());
                clearCoatMaskTexture = hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred ? m_GbufferManager.GetBuffersRTI()[2] : TextureXR.GetBlackTexture();
                cmd.SetComputeTextureParam(reflectionShaderCS, currentKernel, HDShaderIDs._SsrClearCoatMaskTexture, clearCoatMaskTexture);
                cmd.SetComputeIntParam(reflectionShaderCS, HDShaderIDs._SsrStencilBit, (int)StencilUsage.TraceReflectionRay);
                cmd.SetComputeTextureParam(reflectionShaderCS, currentKernel, HDShaderIDs._StencilTexture, m_SharedRTManager.GetDepthStencilBuffer(), 0, RenderTextureSubElement.Stencil);

                // Bind all the required scalars
                m_ShaderVariablesRayTracingCB._RaytracingIntensityClamp = settings.clampValue;
                m_ShaderVariablesRayTracingCB._RaytracingIncludeSky = settings.reflectSky.value ? 1 : 0;
                ConstantBuffer.PushGlobal(cmd, m_ShaderVariablesRayTracingCB, HDShaderIDs._ShaderVariablesRaytracing);

                // Bind the output buffers
                cmd.SetComputeTextureParam(reflectionShaderCS, currentKernel, HDShaderIDs._RaytracingDirectionBuffer, intermediateBuffer1);

                if (settings.fullResolution)
                {
                    // Evaluate the dispatch parameters
                    numTilesXHR = (texWidth + (areaTileSize - 1)) / areaTileSize;
                    numTilesYHR = (texHeight + (areaTileSize - 1)) / areaTileSize;
                }
                else
                {
                    // Evaluate the dispatch parameters
                    numTilesXHR = (texWidth / 2 + (areaTileSize - 1)) / areaTileSize;
                    numTilesYHR = (texHeight / 2 + (areaTileSize - 1)) / areaTileSize;
                }

                // Compute the directions
                cmd.DispatchCompute(reflectionShaderCS, currentKernel, numTilesXHR, numTilesYHR, hdCamera.viewCount);

                // Prepare the components for the deferred lighting
                DeferredLightingRTParameters deferredParamters = PrepareReflectionDeferredLightingRTParameters(hdCamera);
                DeferredLightingRTResources deferredResources = PrepareDeferredLightingRTResources(hdCamera, intermediateBuffer1, intermediateBuffer0);

                // Evaluate the deferred lighting
                RenderRaytracingDeferredLighting(cmd, deferredParamters, deferredResources);

                // Fetch the right filter to use
                if (settings.fullResolution)
                {
                    currentKernel = reflectionFilter.FindKernel("ReflectionIntegrationUpscaleFullRes");
                }
                else
                {
                    currentKernel = reflectionFilter.FindKernel("ReflectionIntegrationUpscaleHalfRes");
                }

                // Inject all the parameters for the compute
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._SsrLightingTextureRW, intermediateBuffer0);
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._SsrHitPointTexture, intermediateBuffer1);
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._NormalBufferTexture, m_SharedRTManager.GetNormalBuffer());
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._BlueNoiseTexture, blueNoise.textureArray16RGB);
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_RaytracingReflectionTexture", outputTexture);
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._ScramblingTexture, m_Asset.renderPipelineResources.textures.scramblingTex);
                cmd.SetComputeIntParam(reflectionFilter, HDShaderIDs._SpatialFilterRadius, settings.upscaleRadius);
                cmd.SetComputeIntParam(reflectionFilter, HDShaderIDs._RaytracingDenoiseRadius, settings.denoise ? settings.denoiserRadius : 0);

                numTilesXHR = (texWidth + (areaTileSize - 1)) / areaTileSize;
                numTilesYHR = (texHeight + (areaTileSize - 1)) / areaTileSize;

                // Bind the right texture for clear coat support
                clearCoatMaskTexture = hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred ? m_GbufferManager.GetBuffersRTI()[2] : TextureXR.GetBlackTexture();
                cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._SsrClearCoatMaskTexture, clearCoatMaskTexture);

                // Compute the texture
                cmd.DispatchCompute(reflectionFilter, currentKernel, numTilesXHR, numTilesYHR, hdCamera.viewCount);
            }

            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.RaytracingFilterReflection)))
            {
                if (settings.denoise && !transparent)
                {
                    // Grab the history buffer
                    RTHandle reflectionHistory = hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection)
                        ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection, ReflectionHistoryBufferAllocatorFunction, 1);

                    float historyValidity = 1.0f;
#if UNITY_HDRP_DXR_TESTS_DEFINE
                    if (Application.isPlaying)
                        historyValidity = 0.0f;
                    else
#endif
                        // We need to check if something invalidated the history buffers
                        historyValidity *= ValidRayTracingHistory(hdCamera) ? 1.0f : 0.0f;

                    HDReflectionDenoiser reflectionDenoiser = GetReflectionDenoiser();
                    reflectionDenoiser.DenoiseBuffer(cmd, hdCamera, settings.denoiserRadius, outputTexture, reflectionHistory, intermediateBuffer0, historyValidity: historyValidity);
                    HDUtils.BlitCameraTexture(cmd, intermediateBuffer0, outputTexture);
                }
            }
        }

        void RenderReflectionsQuality(HDCamera hdCamera, CommandBuffer cmd, RTHandle outputTexture, ScriptableRenderContext renderContext, int frameCount, bool transparent)
        {
            // Fetch the shaders that we will be using
            ComputeShader reflectionFilter = m_Asset.renderPipelineRayTracingResources.reflectionBilateralFilterCS;
            RayTracingShader reflectionShader = m_Asset.renderPipelineRayTracingResources.reflectionRaytracingRT;

            // Request the buffers we shall be using
            RTHandle intermediateBuffer0 = GetRayTracingBuffer(InternalRayTracingBuffers.RGBA0);
            RTHandle intermediateBuffer1 = GetRayTracingBuffer(InternalRayTracingBuffers.RGBA1);

            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();
            LightCluster lightClusterSettings = hdCamera.volumeStack.GetComponent<LightCluster>();

            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.RaytracingIntegrateReflection)))
            {
                // Bind all the required data for ray tracing
                BindRayTracedReflectionData(cmd, hdCamera, reflectionShader, settings, lightClusterSettings, intermediateBuffer0, intermediateBuffer1);

                // Only use the shader variant that has multi bounce if the bounce count > 1
                CoreUtils.SetKeyword(cmd, "MULTI_BOUNCE_INDIRECT", settings.bounceCount.value > 1);

                // We are not in the diffuse only case
                m_ShaderVariablesRayTracingCB._RayTracingDiffuseLightingOnly = 0;
                ConstantBuffer.PushGlobal(cmd, m_ShaderVariablesRayTracingCB, HDShaderIDs._ShaderVariablesRaytracing);

                // Run the computation
                cmd.DispatchRays(reflectionShader, transparent ? m_RayGenIntegrationTransparentName : m_RayGenIntegrationName, (uint)hdCamera.actualWidth, (uint)hdCamera.actualHeight, (uint)hdCamera.viewCount);

                // Disable multi-bounce
                CoreUtils.SetKeyword(cmd, "MULTI_BOUNCE_INDIRECT", false);
            }

            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.RaytracingFilterReflection)))
            {
                if (settings.denoise && !transparent)
                {
                    // Grab the history buffer
                    RTHandle reflectionHistory = hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection)
                        ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection, ReflectionHistoryBufferAllocatorFunction, 1);

                    float historyValidity = 1.0f;
#if UNITY_HDRP_DXR_TESTS_DEFINE
                    if (Application.isPlaying)
                        historyValidity = 0.0f;
                    else
#endif
                        // We need to check if something invalidated the history buffers
                        historyValidity *= ValidRayTracingHistory(hdCamera) ? 1.0f : 0.0f;

                    HDReflectionDenoiser reflectionDenoiser = GetReflectionDenoiser();
                    reflectionDenoiser.DenoiseBuffer(cmd, hdCamera, settings.denoiserRadius, intermediateBuffer0, reflectionHistory, outputTexture, historyValidity: historyValidity);
                }
                else
                {
                    HDUtils.BlitCameraTexture(cmd, intermediateBuffer0, outputTexture);
                }
            }
        }
    }
}
