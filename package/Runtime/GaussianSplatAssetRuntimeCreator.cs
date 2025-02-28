using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using System.IO;
using System;
using Unity.Mathematics;
using GaussianSplatting.Runtime;
using Unity.Profiling.LowLevel;
using Unity.Profiling;
using UnityEditor;

namespace GaussianSplatting.RuntimeCreator
{
    //[BurstCompile]
    public static class GaussianSplatAssetRuntimeCreator
    {
        // input file splat data is expected to be in this format
        public struct InputSplatData
        {
            public Vector3 pos;
            public Vector3 nor;
            public Vector3 dc0;
            public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
            public float opacity;
            public Vector3 scale;
            public Quaternion rot;
        }

        public unsafe static NativeArray<InputSplatData> LoadPLYSplatFile(string plyPath)
        {
            NativeArray<InputSplatData> data = default;
            if (!File.Exists(plyPath))
            {
                //m_ErrorMessage = $"Did not find {plyPath} file";
                return data;
            }

            int splatCount;
            int vertexStride;
            NativeArray<byte> verticesRawData;
            try
            {
                PLYFileReader.ReadFile(plyPath, out splatCount, out vertexStride, out _, out verticesRawData);
            }
            catch (Exception ex)
            {
                // m_ErrorMessage = ex.Message;
                return data;
            }

            if (UnsafeUtility.SizeOf<InputSplatData>() != vertexStride)
            {
                // m_ErrorMessage = $"PLY vertex size mismatch, expected {UnsafeUtility.SizeOf<InputSplatData>()} but file has {vertexStride}";
                return data;
            }

            // reorder SHs
            NativeArray<float> floatData = verticesRawData.Reinterpret<float>(1);
            ReorderSHs(splatCount, (float*)floatData.GetUnsafePtr());

            data = verticesRawData.Reinterpret<InputSplatData>(1);
            LinearizeData(data);
            return data;
        }

        public unsafe static NativeArray<InputSplatData> LoadPLYSplatFile(TextAsset textAsset)
        {
            NativeArray<InputSplatData> data = default;

            int splatCount;
            int vertexStride;
            NativeArray<byte> verticesRawData;
            try
            {
                PLYFileReader.ReadFile(textAsset, out splatCount, out vertexStride, out _, out verticesRawData);
            }
            catch (Exception ex)
            {
                // m_ErrorMessage = ex.Message;
                return data;
            }

            if (UnsafeUtility.SizeOf<InputSplatData>() != vertexStride)
            {
                // m_ErrorMessage = $"PLY vertex size mismatch, expected {UnsafeUtility.SizeOf<InputSplatData>()} but file has {vertexStride}";
                return data;
            }

            // reorder SHs
            NativeArray<float> floatData = verticesRawData.Reinterpret<float>(1);
            ReorderSHs(splatCount, (float*)floatData.GetUnsafePtr());

            data = verticesRawData.Reinterpret<InputSplatData>(1);
            LinearizeData(data);
            return data;
        }

        static unsafe void ReorderSHs(int splatCount, float* data)
        {
            int splatStride = UnsafeUtility.SizeOf<InputSplatData>() / 4;
            int shStartOffset = 9, shCount = 15;
            float* tmp = stackalloc float[shCount * 3];
            int idx = shStartOffset;
            for (int i = 0; i < splatCount; ++i)
            {
                for (int j = 0; j < shCount; ++j)
                {
                    tmp[j * 3 + 0] = data[idx + j];
                    tmp[j * 3 + 1] = data[idx + j + shCount];
                    tmp[j * 3 + 2] = data[idx + j + shCount * 2];
                }

                for (int j = 0; j < shCount * 3; ++j)
                {
                    data[idx + j] = tmp[j];
                }

                idx += splatStride;
            }
        }


        //[BurstCompile]
        struct LinearizeDataJob : IJobParallelFor
        {
            public NativeArray<InputSplatData> splatData;
            public void Execute(int index)
            {
                var splat = splatData[index];

                // rot
                var q = splat.rot;
                var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
                qq = GaussianUtils.PackSmallest3Rotation(qq);
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                // scale
                splat.scale = GaussianUtils.LinearScale(splat.scale);

                // color
                splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
                splat.opacity = GaussianUtils.Sigmoid(splat.opacity);

                splatData[index] = splat;
            }
        }

        static void LinearizeData(NativeArray<InputSplatData> splatData)
        {
            LinearizeDataJob job = new LinearizeDataJob();
            job.splatData = splatData;
            job.Schedule(splatData.Length, 4096).Complete();
        }

        //[BurstCompile]
        public static unsafe void GatherSHs(int splatCount, InputSplatData* splatData, float* shData)
        {
            for (int i = 0; i < splatCount; ++i)
            {
                UnsafeUtility.MemCpy(shData, ((float*)splatData) + 9, 15 * 3 * sizeof(float));
                splatData++;
                shData += 15 * 3;
            }
        }

        public static int NextMultipleOf(int size, int multipleOf)
        {
            return (size + multipleOf - 1) / multipleOf * multipleOf;
        }

        //[BurstCompile]
        public struct CalcBoundsJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMin;
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMax;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;

            public unsafe void Execute()
            {
                float3 boundsMin = float.PositiveInfinity;
                float3 boundsMax = float.NegativeInfinity;

                for (int i = 0; i < m_SplatData.Length; ++i)
                {
                    float3 pos = m_SplatData[i].pos;
                    boundsMin = math.min(boundsMin, pos);
                    boundsMax = math.max(boundsMax, pos);
                }
                *m_BoundsMin = boundsMin;
                *m_BoundsMax = boundsMax;
            }
        }

        //[BurstCompile]
        public struct ReorderMortonJob : IJobParallelFor
        {
            const float kScaler = (float)((1 << 21) - 1);
            public float3 m_BoundsMin;
            public float3 m_InvBoundsSize;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;
            public NativeArray<(ulong, int)> m_Order;

            public void Execute(int index)
            {
                float3 pos = ((float3)m_SplatData[index].pos - m_BoundsMin) * m_InvBoundsSize * kScaler;
                uint3 ipos = (uint3)pos;
                ulong code = GaussianUtils.MortonEncode3(ipos);
                m_Order[index] = (code, index);
            }
        }

        public struct OrderComparer : IComparer<(ulong, int)>
        {
            public int Compare((ulong, int) a, (ulong, int) b)
            {
                if (a.Item1 < b.Item1) return -1;
                if (a.Item1 > b.Item1) return +1;
                return a.Item2 - b.Item2;
            }
        }

        public static void ReorderMorton(NativeArray<InputSplatData> splatData, float3 boundsMin, float3 boundsMax)
        {

            unsafe{
                ReorderMortonJob order = new ReorderMortonJob
                {
                    m_SplatData = splatData,
                    m_BoundsMin = boundsMin,
                    m_InvBoundsSize = 1.0f / (boundsMax - boundsMin),
                    m_Order = new NativeArray<(ulong, int)>(splatData.Length, Allocator.TempJob)
                };
                order.Schedule(splatData.Length, 4096).Complete();

                var orderArray = order.m_Order.ToArray();
                Array.Sort(orderArray, new OrderComparer());
                //order.m_Order.Sort(new OrderComparer());

                order.m_Order.Dispose();
                order.m_Order = new NativeArray<(ulong, int)>(orderArray, Allocator.TempJob);

                NativeArray <InputSplatData> copy = new(order.m_SplatData, Allocator.TempJob);
                for (int i = 0; i < copy.Length; ++i)
                    order.m_SplatData[i] = copy[order.m_Order[i].Item2];
                copy.Dispose();

                order.m_Order.Dispose();
            }           
        }

        struct ConvertSHClustersJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> m_Input;
            public NativeArray<GaussianSplatAsset.SHTableItemFloat16> m_Output;
            public void Execute(int index)
            {
                var addr = index * 15;
                GaussianSplatAsset.SHTableItemFloat16 res;
                res.sh1 = new half3(m_Input[addr + 0]);
                res.sh2 = new half3(m_Input[addr + 1]);
                res.sh3 = new half3(m_Input[addr + 2]);
                res.sh4 = new half3(m_Input[addr + 3]);
                res.sh5 = new half3(m_Input[addr + 4]);
                res.sh6 = new half3(m_Input[addr + 5]);
                res.sh7 = new half3(m_Input[addr + 6]);
                res.sh8 = new half3(m_Input[addr + 7]);
                res.sh9 = new half3(m_Input[addr + 8]);
                res.shA = new half3(m_Input[addr + 9]);
                res.shB = new half3(m_Input[addr + 10]);
                res.shC = new half3(m_Input[addr + 11]);
                res.shD = new half3(m_Input[addr + 12]);
                res.shE = new half3(m_Input[addr + 13]);
                res.shF = new half3(m_Input[addr + 14]);
                res.shPadding = default;
                m_Output[index] = res;
            }
        }


        public static unsafe void ClusterSHs(NativeArray<InputSplatData> splatData, GaussianSplatAsset.SHFormat format, out NativeArray<GaussianSplatAsset.SHTableItemFloat16> shs, out NativeArray<int> shIndices)
        {
            shs = default;
            shIndices = default;

            int shCount = GaussianSplatAsset.GetSHCount(format, splatData.Length);
            if (shCount >= splatData.Length) // no need to cluster, just use raw data
                return;

            const int kShDim = 15 * 3;
            const int kBatchSize = 2048;
            float passesOverData = format switch
            {
                GaussianSplatAsset.SHFormat.Cluster64k => 0.3f,
                GaussianSplatAsset.SHFormat.Cluster32k => 0.4f,
                GaussianSplatAsset.SHFormat.Cluster16k => 0.5f,
                GaussianSplatAsset.SHFormat.Cluster8k => 0.8f,
                GaussianSplatAsset.SHFormat.Cluster4k => 1.2f,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };

            float t0 = Time.realtimeSinceStartup;
            NativeArray<float> shData = new(splatData.Length * kShDim, Allocator.Persistent);
            GatherSHs(splatData.Length, (InputSplatData*)splatData.GetUnsafeReadOnlyPtr(), (float*)shData.GetUnsafePtr());

            NativeArray<float> shMeans = new(shCount * kShDim, Allocator.Persistent);
            shIndices = new(splatData.Length, Allocator.Persistent);

            KMeansClustering.Calculate(kShDim, shData, kBatchSize, passesOverData, ClusterSHProgress, shMeans, shIndices);
            shData.Dispose();

            shs = new NativeArray<GaussianSplatAsset.SHTableItemFloat16>(shCount, Allocator.Persistent);

            ConvertSHClustersJob job = new ConvertSHClustersJob
            {
                m_Input = shMeans.Reinterpret<float3>(4),
                m_Output = shs
            };
            job.Schedule(shCount, 256).Complete();
            shMeans.Dispose();
            float t1 = Time.realtimeSinceStartup;
            Debug.Log($"GS: clustered {splatData.Length / 1000000.0:F2}M SHs into {shCount / 1024}K ({passesOverData:F1}pass/{kBatchSize}batch) in {t1 - t0:F0}s");
        }

        public static bool ClusterSHProgress(float val)
        {            
            return true;
        }

        public static float4 DecodeNorm10ToQuat(uint encoded)
        {
            // Extraction des différentes composantes
            float x = (encoded & 0x3FF) / 1023.5f;        // 10 bits pour x
            float y = ((encoded >> 10) & 0x3FF) / 1023.5f; // 10 bits pour y
            float z = ((encoded >> 20) & 0x3FF) / 1023.5f; // 10 bits pour z
            float w = ((encoded >> 30) & 0x03) / 3.5f;     // 2 bits pour w

            return new float4(x, y, z, w);
        }

        public static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
        {
            return (uint)(v.x * 1023.5f) | ((uint)(v.y * 1023.5f) << 10) | ((uint)(v.z * 1023.5f) << 20) | ((uint)(v.w * 3.5f) << 30);
        }

        public static uint decodeByteArrayToUint(byte[] buffer)
        {
            return ((uint)buffer[3] << 24) | ((uint)buffer[2] << 16) | ((uint)buffer[1] << 8) | (uint)buffer[0];
        }

        public static unsafe void EmitEncodedVector(float3 v, byte* outputPtr, GaussianSplatAsset.VectorFormat format)
        {
            switch (format)
            {
                case GaussianSplatAsset.VectorFormat.Float32:
                    {
                        *(float*)outputPtr = v.x;
                        *(float*)(outputPtr + 4) = v.y;
                        *(float*)(outputPtr + 8) = v.z;
                    }
                    break;
                case GaussianSplatAsset.VectorFormat.Norm16:
                    {
                        ulong enc = EncodeFloat3ToNorm16(math.saturate(v));
                        *(uint*)outputPtr = (uint)enc;
                        *(ushort*)(outputPtr + 4) = (ushort)(enc >> 32);
                    }
                    break;
                case GaussianSplatAsset.VectorFormat.Norm11:
                    {
                        uint enc = EncodeFloat3ToNorm11(math.saturate(v));
                        *(uint*)outputPtr = enc;
                    }
                    break;
                case GaussianSplatAsset.VectorFormat.Norm6:
                    {
                        ushort enc = EncodeFloat3ToNorm655(math.saturate(v));
                        *(ushort*)outputPtr = enc;
                    }
                    break;
            }
        }

        public static uint EncodeFloat3ToNorm11(float3 v) // 32 bits: 11.10.11
        {
            return (uint)(v.x * 2047.5f) | ((uint)(v.y * 1023.5f) << 11) | ((uint)(v.z * 2047.5f) << 21);
        }

        public static ushort EncodeFloat3ToNorm655(float3 v) // 16 bits: 6.5.5
        {
            return (ushort)((uint)(v.x * 63.5f) | ((uint)(v.y * 31.5f) << 6) | ((uint)(v.z * 31.5f) << 11));
        }

        public static ulong EncodeFloat3ToNorm16(float3 v) // 48 bits: 16.16.16
        {
            return (ulong)(v.x * 65535.5f) | ((ulong)(v.y * 65535.5f) << 16) | ((ulong)(v.z * 65535.5f) << 32);
        }

        public static float3 DecodeNorm16ToFloat3(ulong encoded)
        {
            float x = (encoded & 0xFFFF) / 65535.5f;
            float y = ((encoded >> 16) & 0xFFFF) / 65535.5f;
            float z = ((encoded >> 32) & 0xFFFF) / 65535.5f;
            return new float3(x, y, z);
        }

        public static ushort EncodeFloat3ToNorm565(float3 v) // 16 bits: 5.6.5
        {
            return (ushort)((uint)(v.x * 31.5f) | ((uint)(v.y * 63.5f) << 5) | ((uint)(v.z * 31.5f) << 11));
        }

        public static int SplatIndexToTextureIndex(uint idx)
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            uint width = GaussianSplatAsset.kTextureWidth / 16;
            idx >>= 8;
            uint x = (idx % width) * 16 + xy.x;
            uint y = (idx / width) * 16 + xy.y;
            return (int)(y * GaussianSplatAsset.kTextureWidth + x);
        }
    }
}
