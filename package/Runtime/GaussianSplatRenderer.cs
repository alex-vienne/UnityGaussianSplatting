// SPDX-License-Identifier: MIT
using GaussianSplatting.RuntimeCreator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort them by depth from camera
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                if (gs.m_FrameCounter % gs.m_SortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.m_FrameCounter;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetColor(GaussianSplatRenderer.Props.SplatOverColor, gs.m_OverColor);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSaturation, gs.m_Saturation);
                mpb.SetInt(GaussianSplatRenderer.Props.SplatIsBlackAndWhite, gs.m_IsBlackAndWhite ? 1 : 0);
                mpb.SetInt(GaussianSplatRenderer.Props.SplatIslightened, gs.m_Islightened ? 1 : 0);                
                mpb.SetInt(GaussianSplatRenderer.Props.SplatIsOutlined, gs.m_IsOutlined ? 1 : 0);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam, matrix);
                cmb.EndSample(s_ProfCalcView);

                // draw
                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            InitialClearCmdBuffer(cam);

            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        public enum SortMode
        {
            Radix,
            FFX
        }

        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }

        public bool m_IsInitialized = false;

        [HideInInspector]
        public byte[] positionArray = null;

        public GaussianSplatAsset m_Asset;

        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;

        [Tooltip("Additional color factor for the splats")]
        public Color m_OverColor;

        [Range(0.0f, 10.0f)]
        [Tooltip("Set saturation")]
        public float m_Saturation = 1.0f;

        [Tooltip("Set black and white")]
        public bool m_IsBlackAndWhite = false;

        [Tooltip("Set scene light")]
        public bool m_Islightened = false;        

        [Tooltip("Set black and white")]
        public bool m_IsOutlined = false;

        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;

        public SortMode m_SortMode = SortMode.Radix;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilitiesRadix;
        public ComputeShader m_CSSplatUtilitiesFFX;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        public GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuPosDataTemp;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal int m_FrameCounter;
        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatOverColor = Shader.PropertyToID("_SplatOverColor");
            public static readonly int SplatSaturation = Shader.PropertyToID("_SplatSaturation");
            public static readonly int SplatIsBlackAndWhite = Shader.PropertyToID("_SplatIsBlackAndWhite");
            public static readonly int SplatIslightened = Shader.PropertyToID("_SplatIslightened");
            public static readonly int SplatIsOutlined = Shader.PropertyToID("_SplatIsOutlined");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }
        public bool HasValidAsset =>
           (m_Asset != null &&
           m_Asset.splatCount > 0 &&
           m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
           m_Asset.posData != null &&
           m_Asset.otherData != null &&
           m_Asset.shData != null &&
           m_Asset.colorData != null) || HasValidPLY;

        public bool HasValidPLY = false;

        //public bool HasValidAsset = true;

        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        //public bool HasValidRenderSetup =true;

        const int kGpuViewDataSize = 40;

        public void CreateResourcesForAsset(byte[] plyByte)
        {
            CreateResourcesForAsset(null, plyByte);
        }

        public void CreateResourcesForAsset(string plyFile, byte[] plyByte = null)
        {
            HasValidPLY = false;
            Tuple<int, int> widthHeight;
            NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> splats;
            GraphicsFormat texFormat;
            if (!HasValidAsset)
            {
                // todo
                if (plyFile != null)
                {
                    ReadDatafromPLY(plyFile, out widthHeight, out splats, out texFormat);
                }
                else if(plyByte != null)
                {
                    ReadDatafromPLY(plyByte, out widthHeight, out splats, out texFormat);
                }
                return;
            }
            else
            {
                m_SplatCount = asset.splatCount;
                //splats = GetDataFromPLY();
                //GetSplatPositionsFromInputSplatData(splats);
                m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
                m_GpuPosData.SetData(asset.posData.GetData<uint>());

                //GetSplatOthersFromInputSplatData(splats);
                m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
                m_GpuOtherData.SetData(asset.otherData.GetData<uint>());

                //GetSplatSHFromInputSplatData(splats);
                m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(asset.shData.dataSize), 4) { name = "GaussianSHData" };
                m_GpuSHData.SetData(asset.shData.GetData<uint>());

                //widthHeight = GaussianSplatAsset.CalcTextureSize(splatCount).ToTuple();
                //texFormat = GaussianSplatAsset.ColorFormatToGraphics(GaussianSplatAsset.ColorFormat.Float16x4);
                widthHeight = GaussianSplatAsset.CalcTextureSize(asset.splatCount).ToTuple();
                texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            }


            //var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);

            if (HasValidAsset)
            {
                //GetSplatColorFromInputSplatData(splats);
                var tex = new Texture2D(widthHeight.Item1, widthHeight.Item2, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
                tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
                tex.Apply(false, true);
                m_GpuColorData = tex;

                if (asset.chunkData != null && asset.chunkData.dataSize != 0)
                {
                    m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                        (int)(asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                        UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                    { name = "GaussianChunkData" };
                    m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                    m_GpuChunksValid = true;
                }
                else
                {
                    // just a dummy chunk buffer
                    m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                        UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                    { name = "GaussianChunkData" };
                    m_GpuChunksValid = false;
                }
            }
            if (HasValidAsset)
            {
                m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);
            }
            else
            {
                m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);
            }
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            InitSortBuffers(splatCount);
        }

        private void ReadDatafromPLY(byte[] plyBytes, out Tuple<int, int> widthHeight, out NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> splats, out GraphicsFormat texFormat)
        {
            ReadDatafromPLY(null, plyBytes, out widthHeight, out splats, out texFormat);
        }

        private void ReadDatafromPLY(string plyFile, out Tuple<int, int> widthHeight, out NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> splats, out GraphicsFormat texFormat)
        {
            ReadDatafromPLY(plyFile, null, out widthHeight, out splats, out texFormat);
        }

        private void ReadDatafromPLY(string plyFile, byte[] plyBytes, out Tuple<int, int> widthHeight, out NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> splats, out GraphicsFormat texFormat)
        {
            widthHeight = new Tuple<int, int>(0, 0);
            texFormat = new GraphicsFormat();
            if (plyFile != null)
            {
                splats = GetDataFromPLY(plyFile);
            }
            else if (plyBytes != null)
            {
                splats = GetDataFromPLY(null, plyBytes);
            }
            else
            {
                splats = new NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> { };
            }

            if (splats.Length > 0)
            {
                GetSplatPositionsFromInputSplatData(splats);
                GetSplatOthersFromInputSplatData(splats);
                GetSplatSHFromInputSplatData(splats);
                GetSplatColorFromInputSplatData(splats);

                widthHeight = GaussianSplatAsset.CalcTextureSize(splatCount).ToTuple();
                texFormat = GaussianSplatAsset.ColorFormatToGraphics(GaussianSplatAsset.ColorFormat.Float16x4);
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
                m_GpuChunksValid = false;

                m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);

                m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
                // cube indices, most often we use only the first quad
                m_GpuIndexBuffer.SetData(new ushort[]
                {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
                });

                InitSortBuffers(splatCount);
                HasValidPLY = true;
            }
            //Reset(0);
        }

        bool IsRadixSupported => (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan);

        internal SortMode GetSortMode() => !IsRadixSupported ? SortMode.FFX : m_SortMode;

        ComputeShader m_CSSplatUtilities => GetSortMode() == SortMode.Radix ? m_CSSplatUtilitiesRadix : m_CSSplatUtilitiesFFX;

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();
            m_SorterArgs.resources?.Dispose();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            // init keys buffer to splat indices
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter.Valid)
            {
                if (GetSortMode() == SortMode.Radix)
                    m_SorterArgs.resources = GpuSortingRadix.SupportResourcesRadix.Load((uint)count);
                else
                    m_SorterArgs.resources = GpuSortingFFX.SupportResourcesFFX.Load((uint)count);
            }
        }

        public void Reset(int value)
        {
            m_SortMode = value == 0 ? SortMode.Radix : SortMode.FFX;
            OnDisable();
            OnEnable();
        }

        public void OnEnable()
        {
            InitializeGS(null);
        }

        public void InitializeGS(string plyFile)
        {
            m_FrameCounter = 0;
            if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_CSSplatUtilities == null)
                return;

            if (!SystemInfo.supportsComputeShaders)
                return;

            m_MatSplats = new Material(m_ShaderSplats) { name = "GaussianSplats" };
            m_MatComposite = new Material(m_ShaderComposite) { name = "GaussianClearDstAlpha" };
            m_MatDebugPoints = new Material(m_ShaderDebugPoints) { name = "GaussianDebugPoints" };
            m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) { name = "GaussianDebugBoxes" };

            if (GetSortMode() == SortMode.Radix)
                m_Sorter = new GpuSortingRadix(m_CSSplatUtilitiesRadix);
            else
                m_Sorter = new GpuSortingFFX(m_CSSplatUtilitiesFFX);
            GaussianSplatRenderSystem.instance.RegisterSplat(this);

            CreateResourcesForAsset(plyFile);
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = (int) kernel;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);

            // WebGPU does not allow the same buffer to be bound twice, for both read and write access
            var copyReadBuffers = SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
            bool tempBufferCopied = false;

            if (copyReadBuffers && m_GpuEditSelected == null)
            {
                if (m_GpuPosDataTemp == null || m_GpuPosDataTemp.count != m_GpuPosData.count)
                {
                    DisposeBuffer(ref m_GpuPosDataTemp);
                    var src = m_GpuPosData;
                    m_GpuPosDataTemp = new GraphicsBuffer(
                        src.target | GraphicsBuffer.Target.CopyDestination,
                        src.count, src.stride);
                }
                tempBufferCopied = true;
                cmb.CopyBuffer(m_GpuPosData, m_GpuPosDataTemp);
                cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuPosDataTemp);
            }
            else
            {
                cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            }

            if (copyReadBuffers && m_GpuEditDeleted == null)
            {
                if (m_GpuPosDataTemp == null || m_GpuPosDataTemp.count != m_GpuPosData.count)
                {
                    DisposeBuffer(ref m_GpuPosDataTemp);
                    var src = m_GpuPosData;
                    m_GpuPosDataTemp = new GraphicsBuffer(
                        src.target | GraphicsBuffer.Target.CopyDestination,
                        src.count, src.stride);
                }
                if (!tempBufferCopied)
                {
                    cmb.CopyBuffer(m_GpuPosData, m_GpuPosDataTemp);
                }

                cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuPosDataTemp);
            }
            else
            {
                cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            }
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)GaussianSplatAsset.VectorFormat.Float32 | ((uint)GaussianSplatAsset.VectorFormat.Float32 << 8) | ((uint)GaussianSplatAsset.SHFormat.Float32 << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)GaussianSplatAsset.VectorFormat.Float32 | ((uint)GaussianSplatAsset.VectorFormat.Float32 << 8) | ((uint)GaussianSplatAsset.SHFormat.Float32 << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            if (m_GpuPosDataTemp != null)
              DisposeBuffer(ref m_GpuPosDataTemp);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SorterArgs.resources?.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        public void OnDisable()
        {
            UnInitializeGS();
        }

        private void UnInitializeGS()
        {
            DisposeResourcesForAsset();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            // calculate view dependent data for each splat
            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetGlobalColor(Props.SplatOverColor, m_OverColor);
            cmb.SetComputeIntParam(m_CSSplatUtilities,Props.SplatIsBlackAndWhite, m_IsBlackAndWhite? 1:0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatIslightened, m_Islightened ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatIsOutlined, m_IsOutlined ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);

            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)GaussianSplatAsset.VectorFormat.Float32);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            // sort the splats
            m_Sorter.Dispatch(cmd, m_SorterArgs);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                DisposeResourcesForAsset();
                CreateResourcesForAsset(null);
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        public static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;

        #region PLY_LOADER
        private void GetSplatPositionsFromInputSplatData(NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> inputSplats)
        {
            // pos
            DisposeBuffer(ref m_GpuPosData);
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(m_SplatCount * 4), 4) { name = "GaussianPosData" };

            var pos = inputSplats.Select(x => x.pos).ToArray();
            NativeArray<byte> m_OutputFloat32 = new NativeArray<byte>(splatCount * 12, Allocator.Temp);

            for (int i = 0; i < splatCount; i++)
            {   
                unsafe
                {
                    byte* outputPtr2 = (byte*)m_OutputFloat32.GetUnsafePtr() + i * 12;
                    *(float*)outputPtr2 = pos[i].x;
                    *(float*)(outputPtr2 + 4) = pos[i].y;
                    *(float*)(outputPtr2 + 8) = pos[i].z;            
                }  
            }

            positionArray = m_OutputFloat32.ToRawBytes();

            var array = m_OutputFloat32.Reinterpret<uint>(1);
            m_GpuPosData.SetData(array);
        }

        private void GetSplatOthersFromInputSplatData(NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> inputSplats)
        {
            //other
            NativeArray<int> splatSHIndices = default;

            var m_FormatScale = GaussianSplatAsset.VectorFormat.Float32;
            var m_ScaleFormat = GaussianSplatAsset.VectorFormat.Float32;
            var m_FormatSH = GaussianSplatAsset.SHFormat.Float32;

            int formatSize = GaussianSplatAsset.GetOtherSizeNoSHIndex(m_FormatScale);
            int m_FormatSize = formatSize;
            if (splatSHIndices.IsCreated)
                formatSize += 2;
            int dataLen = inputSplats.Length * formatSize;


            DisposeBuffer(ref m_GpuOtherData);
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(dataLen/*splatCount*10*/ ), 4) { name = "GaussianOtherData" };
            unsafe
            {
                NativeArray<byte> m_OutputFloat32 = new NativeArray<byte>(splatCount * 16, Allocator.Temp);
                for (int i = 0; i < splatCount; i++)
                {
                    byte* outputPtr = (byte*)m_OutputFloat32.GetUnsafePtr() + i * m_FormatSize;

                    // rotation: 4 bytes
                    {
                        Quaternion rotQ = inputSplats[i].rot;
                        float4 rot = new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w);
                        uint enc = GaussianSplatAssetRuntimeCreator.EncodeQuatToNorm10(rot);
                        *(uint*)outputPtr = enc;
                        outputPtr += 4;
                    }

                    // scale: 6, 4 or 2 bytes
                    float3 scale = inputSplats[i].scale;

                    GaussianSplatAssetRuntimeCreator.EmitEncodedVector( scale, outputPtr, m_ScaleFormat);
                    outputPtr += GaussianSplatAsset.GetVectorSize(m_ScaleFormat);

                    // cluster SHs
                    NativeArray<GaussianSplatAsset.SHTableItemFloat16> clusteredSHs = default;
                    if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
                    {
                        //EditorUtility.DisplayProgressBar(kProgressTitle, "Cluster SHs", 0.2f);
                        GaussianSplatAssetRuntimeCreator.ClusterSHs(inputSplats, m_FormatSH, out clusteredSHs, out splatSHIndices);
                    }
                    //var m_SplatSHIndices = new NativeArray<int>(splatSHIndices, Allocator.Persistent);
                    // SH index
                    //if (m_SplatSHIndices.IsCreated)
                    //    *(ushort*)outputPtr = (ushort)m_SplatSHIndices[i];
                }

                var array = m_OutputFloat32.Reinterpret<uint>(1);
                m_GpuOtherData.SetData(array);
            }
        }

        private void GetSplatSHFromInputSplatData(NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> inputSplats)
        {
            DisposeBuffer(ref m_GpuSHData);

            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, splatCount * 12 * 16, 4) { name = "GaussianSHData" };

            unsafe
            {
                var m_Format = GaussianSplatAsset.SHFormat.Float32;

                NativeArray<byte> m_OutputFloat32 = new NativeArray<byte>(splatCount * 16 *12, Allocator.Temp);

                for (int i = 0; i < splatCount; i++)
                {
                    var splat = inputSplats[i];

                    switch (m_Format)
                    {
                        case GaussianSplatAsset.SHFormat.Float32:
                            {
                                GaussianSplatAsset.SHTableItemFloat32 res;
                                res.sh1 = splat.sh1;
                                res.sh2 = splat.sh2;
                                res.sh3 = splat.sh3;
                                res.sh4 = splat.sh4;
                                res.sh5 = splat.sh5;
                                res.sh6 = splat.sh6;
                                res.sh7 = splat.sh7;
                                res.sh8 = splat.sh8;
                                res.sh9 = splat.sh9;
                                res.shA = splat.shA;
                                res.shB = splat.shB;
                                res.shC = splat.shC;
                                res.shD = splat.shD;
                                res.shE = splat.shE;
                                res.shF = splat.shF;
                                res.shPadding = default;
                                ((GaussianSplatAsset.SHTableItemFloat32*)m_OutputFloat32.GetUnsafePtr())[i] = res;
                            }
                            break;
                        case GaussianSplatAsset.SHFormat.Float16:
                            {
                                GaussianSplatAsset.SHTableItemFloat16 res;
                                res.sh1 = new half3(splat.sh1);
                                res.sh2 = new half3(splat.sh2);
                                res.sh3 = new half3(splat.sh3);
                                res.sh4 = new half3(splat.sh4);
                                res.sh5 = new half3(splat.sh5);
                                res.sh6 = new half3(splat.sh6);
                                res.sh7 = new half3(splat.sh7);
                                res.sh8 = new half3(splat.sh8);
                                res.sh9 = new half3(splat.sh9);
                                res.shA = new half3(splat.shA);
                                res.shB = new half3(splat.shB);
                                res.shC = new half3(splat.shC);
                                res.shD = new half3(splat.shD);
                                res.shE = new half3(splat.shE);
                                res.shF = new half3(splat.shF);
                                res.shPadding = default;
                                ((GaussianSplatAsset.SHTableItemFloat16*)m_OutputFloat32.GetUnsafePtr())[i] = res;
                            }
                            break;
                        case GaussianSplatAsset.SHFormat.Norm11:
                            {
                                GaussianSplatAsset.SHTableItemNorm11 res;
                                res.sh1 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh1);
                                res.sh2 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh2);
                                res.sh3 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh3);
                                res.sh4 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh4);
                                res.sh5 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh5);
                                res.sh6 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh6);
                                res.sh7 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh7);
                                res.sh8 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh8);
                                res.sh9 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.sh9);
                                res.shA = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.shA);
                                res.shB = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.shB);
                                res.shC = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.shC);
                                res.shD = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.shD);
                                res.shE = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.shE);
                                res.shF = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm11(splat.shF);
                                ((GaussianSplatAsset.SHTableItemNorm11*)m_OutputFloat32.GetUnsafePtr())[i] = res;
                            }
                            break;
                        case GaussianSplatAsset.SHFormat.Norm6:
                            {
                                GaussianSplatAsset.SHTableItemNorm6 res;
                                res.sh1 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh1);
                                res.sh2 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh2);
                                res.sh3 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh3);
                                res.sh4 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh4);
                                res.sh5 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh5);
                                res.sh6 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh6);
                                res.sh7 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh7);
                                res.sh8 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh8);
                                res.sh9 = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.sh9);
                                res.shA = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.shA);
                                res.shB = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.shB);
                                res.shC = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.shC);
                                res.shD = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.shD);
                                res.shE = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.shE);
                                res.shF = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm565(splat.shF);
                                res.shPadding = default;
                                ((GaussianSplatAsset.SHTableItemNorm6*)m_OutputFloat32.GetUnsafePtr())[i] = res;
                            }
                            break;
                        default:
                            break;
                    }
                }

                var array = m_OutputFloat32.Reinterpret<uint>(1);
                m_GpuSHData.SetData(array);
            }
        }

        private void GetSplatColorFromInputSplatData(NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> inputSplats)
        {            
            var (width, height) = GaussianSplatAsset.CalcTextureSize(inputSplats.Length);
            NativeArray<float4> m_OutputFloat4 = new NativeArray<float4>(width *height, Allocator.Temp);
            for (int i = 0; i < splatCount; i++)
            {
                var splat = inputSplats[i];
                int j = GaussianSplatAssetRuntimeCreator.SplatIndexToTextureIndex((uint)i);
                m_OutputFloat4[j] = new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
            }

            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(GaussianSplatAsset.ColorFormat.Float32x4);

            var tex = new Texture2D(width, height, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(m_OutputFloat4, 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
        }

        private NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> GetDataFromPLY(string plyFile, byte[] plyBytes = null)
        {
            //TextAsset plyFile = Resources.Load("gs_primevere_long-edit") as TextAsset;

            NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData> inputSplats = new NativeArray<GaussianSplatAssetRuntimeCreator.InputSplatData>( );
            //inputSplats = GaussianSplatAssetRuntimeCreator.LoadPLYSplatFile("./Assets/GaussianAssets/gs_primevere_long-edit.ply");

            if (plyFile == null && plyBytes != null)
            {
                inputSplats = GaussianSplatAssetRuntimeCreator.LoadPLYSplatFile(plyBytes);
                
                //inputSplats = GaussianSplatAssetRuntimeCreator.LoadPLYSplatFile(Application.streamingAssetsPath + "/gs_primevere_long-edit.ply");
                //inputSplats = GaussianSplatAssetRuntimeCreator.LoadPLYSplatFile(plyFile);
                //inputSplats = GaussianSplatAssetRuntimeCreator.LoadPLYSplatFile("./Assets/GaussianAssets/gs_Kaizen_inside_and_outside-edit.ply");
            }
            else
            {
                inputSplats = GaussianSplatAssetRuntimeCreator.LoadPLYSplatFile(plyFile);
            }

            if(inputSplats.Length == 0)
            {
                return inputSplats;
            }

            m_SplatCount = inputSplats.Length;

            Debug.Log("SplatCount : " + m_SplatCount.ToString());

            unsafe
            {
                float3 boundsMin, boundsMax;
                var boundsJob = new GaussianSplatAssetRuntimeCreator.CalcBoundsJob
                {
                    m_BoundsMin = &boundsMin,
                    m_BoundsMax = &boundsMax,
                    m_SplatData = inputSplats
                };

                boundsJob.Schedule().Complete();
                GaussianSplatAssetRuntimeCreator.ReorderMorton(inputSplats, boundsMin, boundsMax);
            }

            // cluster SHs
            NativeArray<int> splatSHIndices = default;
            NativeArray<GaussianSplatAsset.SHTableItemFloat16> clusteredSHs = default;

            //Todo
            var m_FormatSH = GaussianSplatAsset.SHFormat.Norm11;
            if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
            {
                GaussianSplatAssetRuntimeCreator.ClusterSHs(inputSplats, m_FormatSH, out clusteredSHs, out splatSHIndices);
            }


            return inputSplats;
        }
        #endregion
    }
}
