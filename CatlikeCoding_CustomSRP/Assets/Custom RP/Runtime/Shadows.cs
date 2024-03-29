using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class Shadows
{
    private const string bufferName = "Shadow";
    private CommandBuffer buffer = new CommandBuffer() {name = bufferName};

    private const int maxShadowedDirectionalLightCount = 4, maxCascades = 4;

    private ScriptableRenderContext context;
    private CullingResults cullingResults;
    ShadowSettings settings;

    private static int dirShadowAtlasId = Shader.PropertyToID("_DirectionalShadowAtlas"),
        dirShadowMatricesId = Shader.PropertyToID("_DirectionalShadowMatrices"),
        cascadeCountId = Shader.PropertyToID("_CascadeCount"),
        cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres"),
        cascadeDataId = Shader.PropertyToID("_CascadeData"),
        shadowAtlasSizeId = Shader.PropertyToID("_ShadowAtlasSize"),
        shadowDistanceFadeId = Shader.PropertyToID("_ShadowDistanceFade");

    private static Vector4[] cascadeCullingSpheres = new Vector4[maxCascades];
    private static Vector4[] cascadeData = new Vector4[maxCascades];
    private static Matrix4x4[] dirShadowMatrices = new Matrix4x4[maxShadowedDirectionalLightCount * maxCascades];

    private static string[] directionalFilterKeywords =
    {
        "_DIRECTIONAL_PCF3",
        "_DIRECTIONAL_PCF5",
        "_DIRECTIONAL_PCF7",
    };
    
    static string[] cascadeBlendKeywords = {
        "_CASCADE_BLEND_SOFT",
        "_CASCADE_BLEND_DITHER"
    };
    
    struct ShadowedDirectionalLight
    {
        public int visibleLightIndex;
        public float slopeScaleBias;
        public float nearPlaneOffset;
    }

    private ShadowedDirectionalLight[] ShadowedDirectionalLights =
        new ShadowedDirectionalLight[maxShadowedDirectionalLightCount];

    private int shadowedDirectionalLightCount;

    public void Setup(ScriptableRenderContext context, CullingResults cullingResults,
        ShadowSettings shadowSettings)
    {
        this.context = context;
        this.cullingResults = cullingResults;
        this.settings = shadowSettings;
        shadowedDirectionalLightCount = 0;
    }

    public void Render()
    {
        if (shadowedDirectionalLightCount > 0)
        {
            RenderDirectionalShadows();
        }
        else
        {
            buffer.GetTemporaryRT(dirShadowAtlasId, 1, 1,
                32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap);
        }
    }

    void RenderDirectionalShadows()
    {
        int atlasSize = (int) settings.directional.atlasSize;
        buffer.GetTemporaryRT(dirShadowAtlasId, atlasSize, atlasSize,
            32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap);
        buffer.SetRenderTarget(dirShadowAtlasId,
            RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        buffer.ClearRenderTarget(true, false, Color.clear);
        buffer.BeginSample(bufferName);
        ExecuteBuffer();

        int tiles = shadowedDirectionalLightCount * settings.directional.cascadeCount;
        int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
        int tileSize = atlasSize / split;

        for (int i = 0; i < shadowedDirectionalLightCount; i++)
        {
            RenderDirectionalShadows(i, split, tileSize);
        }

        buffer.SetGlobalInt(cascadeCountId, settings.directional.cascadeCount);
        buffer.SetGlobalVectorArray(cascadeCullingSpheresId, cascadeCullingSpheres);
        buffer.SetGlobalVectorArray(cascadeDataId, cascadeData);
        buffer.SetGlobalMatrixArray(dirShadowMatricesId, dirShadowMatrices);
        float f = 1f - settings.directional.cascadeFade;
        buffer.SetGlobalVector(shadowDistanceFadeId, new Vector4(
            1f / settings.maxDistance, 1f / settings.distanceFade,
            1f / (1f - f * f)
        ));
        SetKeywords(directionalFilterKeywords, (int)settings.directional.filter - 1);
        SetKeywords(cascadeBlendKeywords, (int)settings.directional.cascadeBlend - 1);

        buffer.SetGlobalVector(shadowAtlasSizeId, new Vector4(
            atlasSize, 1f/atlasSize
            ));
        buffer.EndSample(bufferName);
        ExecuteBuffer();
    }
    
    void RenderDirectionalShadows(int index, int split, int tileSize)
    {
        ShadowedDirectionalLight light = ShadowedDirectionalLights[index];
        var shadowSettings = new ShadowDrawingSettings(cullingResults, light.visibleLightIndex);

        int cascadeCount = settings.directional.cascadeCount;
        int tileOffset = index * cascadeCount;
        Vector3 ratios = settings.directional.CascadeRatios;
        float cullingFactor = Mathf.Max(0f, 0.8f - settings.directional.cascadeFade);
       
        for (int i = 0; i < cascadeCount; i++)
        {
            cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(
                light.visibleLightIndex, i, cascadeCount, ratios,
                tileSize, light.nearPlaneOffset,
                out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix,
                out ShadowSplitData splitData);
            if (index == 0) // TODO 这里感觉有问题，不同的light，culling sphere的位置应该是不一样的才对
            {
                SetCascadeData(i, splitData.cullingSphere, tileSize);
            }

            splitData.shadowCascadeBlendCullingFactor = cullingFactor;
            shadowSettings.splitData = splitData;
            int tileIndex = tileOffset + i;
            dirShadowMatrices[tileIndex] = ConvertToAtlasMatrix(
                projectionMatrix * viewMatrix, SetTileViewport(tileIndex, split, tileSize), split
            );
            buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);

            // Slop Bias 可以解决问题，但是不够直觉，需要频繁测试来找到合适的值
            buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
            ExecuteBuffer();
            context.DrawShadows(ref shadowSettings);
            buffer.SetGlobalDepthBias(0f, 0f);
        }
    }

    private void SetKeywords(string[] keywords, int enableIndex)
    {
        for (int i = 0; i < keywords.Length; i++) {
            if (i == enableIndex) {
                buffer.EnableShaderKeyword(keywords[i]);
            }
            else {
                buffer.DisableShaderKeyword(keywords[i]);
            }
        }
    }

    void SetCascadeData(int index, Vector4 cullingSphere, float tileSize)
    {
        float texelSize = 2f * cullingSphere.w / tileSize;
        float filterSize = texelSize * ((float)settings.directional.filter + 1f);
        cullingSphere.w -= filterSize;  // 因为增加了filerSize，防止sample到culling Sphere外面
        cullingSphere.w *= cullingSphere.w; // 保存半径的平方值避免在比较的时候开方
        cascadeCullingSpheres[index] = cullingSphere;
        cascadeData[index] = new Vector4(
            1f / cullingSphere.w, filterSize * 1.4142136f);
    }

    Vector2 SetTileViewport(int index, int split, float tileSize)
    {
        Vector2 offset = new Vector2(index % split, index / split);
        buffer.SetViewport(new Rect(offset.x * tileSize, offset.y * tileSize, tileSize, tileSize));
        return offset;
    }

    Matrix4x4 ConvertToAtlasMatrix(Matrix4x4 m, Vector2 offset, int split)
    {
        if (SystemInfo.usesReversedZBuffer)
        {
            m.m20 = -m.m20;
            m.m21 = -m.m21;
            m.m22 = -m.m22;
            m.m23 = -m.m23;
        }

        float scale = 1f / split;
        m.m00 = (0.5f * (m.m00 + m.m30) + offset.x * m.m30) * scale;
        m.m01 = (0.5f * (m.m01 + m.m31) + offset.x * m.m31) * scale;
        m.m02 = (0.5f * (m.m02 + m.m32) + offset.x * m.m32) * scale;
        m.m03 = (0.5f * (m.m03 + m.m33) + offset.x * m.m33) * scale;
        m.m10 = (0.5f * (m.m10 + m.m30) + offset.y * m.m30) * scale;
        m.m11 = (0.5f * (m.m11 + m.m31) + offset.y * m.m31) * scale;
        m.m12 = (0.5f * (m.m12 + m.m32) + offset.y * m.m32) * scale;
        m.m13 = (0.5f * (m.m13 + m.m33) + offset.y * m.m33) * scale;
        m.m20 = 0.5f * (m.m20 + m.m30);
        m.m21 = 0.5f * (m.m21 + m.m31);
        m.m22 = 0.5f * (m.m22 + m.m32);
        m.m23 = 0.5f * (m.m23 + m.m33);
        return m;
    }

    // 另一个更好理解的计算过程 https://github.com/wlgys8/SRPLearn/wiki/MainLightShadow
    // /// <summary>
    // /// 通过ComputeDirectionalShadowMatricesAndCullingPrimitives得到的投影矩阵，其对应的x,y,z范围分别为均为(-1,1).
    // /// 因此我们需要构造坐标变换矩阵，可以将世界坐标转换到ShadowMap齐次坐标空间。对应的xy范围为(0,1),z范围为(1,0)
    // /// </summary>
    // static Matrix4x4 GetWorldToShadowMapSpaceMatrix(Matrix4x4 proj, Matrix4x4 view)
    // {
    //     //检查平台是否zBuffer反转,一般情况下，z轴方向是朝屏幕内，即近小远大。但是在zBuffer反转的情况下，z轴是朝屏幕外，即近大远小。
    //     if (SystemInfo.usesReversedZBuffer)
    //     {
    //         proj.m20 = -proj.m20;
    //         proj.m21 = -proj.m21;
    //         proj.m22 = -proj.m22;
    //         proj.m23 = -proj.m23;
    //     }
    //
    //     // uv_depth = xyz * 0.5 + 0.5. 
    //     // 即将xy从(-1,1)映射到(0,1),z从(-1,1)或(1,-1)映射到(0,1)或(1,0)
    //     Matrix4x4 worldToShadow = proj * view;
    //     var textureScaleAndBias = Matrix4x4.identity;
    //     textureScaleAndBias.m00 = 0.5f;
    //     textureScaleAndBias.m11 = 0.5f;
    //     textureScaleAndBias.m22 = 0.5f;
    //     textureScaleAndBias.m03 = 0.5f;
    //     textureScaleAndBias.m23 = 0.5f;
    //     textureScaleAndBias.m13 = 0.5f;
    //
    //     return textureScaleAndBias * worldToShadow;
    // }

    void ExecuteBuffer()
    {
        context.ExecuteCommandBuffer(buffer);
        buffer.Clear();
    }

    public Vector3 ReserveDirectionalShadows(Light light, int visibleLightIndex)
    {
        if (shadowedDirectionalLightCount < maxShadowedDirectionalLightCount &&
            light.shadows != LightShadows.None && light.shadowStrength > 0 &&
            cullingResults.GetShadowCasterBounds(visibleLightIndex, out Bounds b)
        )
        {
            ShadowedDirectionalLights[shadowedDirectionalLightCount] =
                new ShadowedDirectionalLight
                {
                    visibleLightIndex = visibleLightIndex,
                    slopeScaleBias = light.shadowBias,
                    nearPlaneOffset = light.shadowNearPlane,
                };
            return new Vector3(light.shadowStrength,
                settings.directional.cascadeCount * shadowedDirectionalLightCount++,
                light.shadowNormalBias);
        }

        return Vector3.zero;
    }

    public void Cleanup()
    {
        buffer.ReleaseTemporaryRT(dirShadowAtlasId);
        ExecuteBuffer();
    }
}