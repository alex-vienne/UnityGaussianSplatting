using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU (uint key, uint payload) 8 bit-LSD radix sort, using reduce-then-scan
    // Copyright Thomas Smith 2023, MIT license
    // https://github.com/b0nes164/GPUSorting

    public class GpuSortingFFX : GpuSorting
    {
        // These need to match constants in the compute shader
        const uint FFX_PARALLELSORT_ELEMENTS_PER_THREAD = 4;
        const uint FFX_PARALLELSORT_THREADGROUP_SIZE = 128;
        const int FFX_PARALLELSORT_SORT_BITS_PER_PASS = 4;
        const uint FFX_PARALLELSORT_SORT_BIN_COUNT = 1u << FFX_PARALLELSORT_SORT_BITS_PER_PASS;
        // The maximum number of thread groups to run in parallel. Modifying this value can help or hurt GPU occupancy,
        // but is very hardware class specific
        const uint FFX_PARALLELSORT_MAX_THREADGROUPS_TO_RUN = 800;

        public class SupportResourcesFFX : GpuSorting.SupportResources
        {
            public GraphicsBuffer sortScratchBuffer;
            public GraphicsBuffer payloadScratchBuffer;
            public GraphicsBuffer scratchBuffer;
            public GraphicsBuffer scratchBufferTemp;
            public GraphicsBuffer reducedScratchBuffer;
            public GraphicsBuffer reducedScratchBufferTemp;

            public static SupportResourcesFFX Load(uint count)
            {
                uint BlockSize = FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;
                uint NumBlocks = DivRoundUp(count, BlockSize);
                uint NumReducedBlocks = DivRoundUp(NumBlocks, BlockSize);

                uint scratchBufferSize = FFX_PARALLELSORT_SORT_BIN_COUNT * NumBlocks;
                uint reduceScratchBufferSize = FFX_PARALLELSORT_SORT_BIN_COUNT * NumReducedBlocks;

                var target = GraphicsBuffer.Target.Structured;
                var resources = new SupportResourcesFFX
                {
                    sortScratchBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "FfxSortSortScratch" },
                    payloadScratchBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "FfxSortPayloadScratch" },
                    scratchBuffer = new GraphicsBuffer(target | GraphicsBuffer.Target.CopySource, (int)scratchBufferSize, 4) { name = "FfxSortScratch" },
                    reducedScratchBuffer = new GraphicsBuffer(target | GraphicsBuffer.Target.CopySource, (int)reduceScratchBufferSize, 4) { name = "FfxSortReducedScratch" },
                };
                return resources;
            }

            public override void Dispose()
            {
                sortScratchBuffer?.Dispose();
                payloadScratchBuffer?.Dispose();
                scratchBuffer?.Dispose();
                scratchBufferTemp?.Dispose();
                reducedScratchBuffer?.Dispose();
                reducedScratchBufferTemp?.Dispose();

                sortScratchBuffer = null;
                payloadScratchBuffer = null;
                scratchBuffer = null;
                scratchBufferTemp = null;
                reducedScratchBuffer = null;
                reducedScratchBufferTemp = null;
            }
        }

        readonly ComputeShader m_CS;
        readonly int m_KernelReduce = -1;
        readonly int m_KernelScanAdd = -1;
        readonly int m_KernelScan = -1;
        readonly int m_KernelScatter = -1;
        readonly int m_KernelSum = -1;

        readonly bool m_Valid;

        public override bool Valid => m_Valid;

        public GpuSortingFFX(ComputeShader cs)
        {
            m_CS = cs;
            if (cs)
            {
                m_KernelReduce = cs.FindKernel("FfxParallelSortReduce");
                m_KernelScanAdd = cs.FindKernel("FfxParallelSortScanAdd");
                m_KernelScan = cs.FindKernel("FfxParallelSortScan");
                m_KernelScatter = cs.FindKernel("FfxParallelSortScatter");
                m_KernelSum = cs.FindKernel("FfxParallelSortCount");
            }

            m_Valid = m_KernelReduce >= 0 &&
                      m_KernelScanAdd >= 0 &&
                      m_KernelScan >= 0 &&
                      m_KernelScatter >= 0 &&
                      m_KernelSum >= 0;
            if (m_Valid)
            {
                if (!cs.IsSupported(m_KernelReduce) ||
                    !cs.IsSupported(m_KernelScanAdd) ||
                    !cs.IsSupported(m_KernelScan) ||
                    !cs.IsSupported(m_KernelScatter) ||
                    !cs.IsSupported(m_KernelSum))
                {
                    m_Valid = false;
                }
            }
        }

        struct SortConstants
        {
            public uint numKeys;                              // The number of keys to sort
            public uint numBlocksPerThreadGroup;              // How many blocks of keys each thread group needs to process
            public uint numThreadGroups;                      // How many thread groups are being run concurrently for sort
            public uint numThreadGroupsWithAdditionalBlocks;  // How many thread groups need to process additional block data
            public uint numReduceThreadgroupPerBin;           // How many thread groups are summed together for each reduced bin entry
            public uint numScanValues;                        // How many values to perform scan prefix (+ add) on
            public uint shift;                                // What bits are being sorted (4 bit increments)
            public uint padding;                              // Padding - unused
        }

        public override void Dispatch(CommandBuffer cmd, Args args)
        {
            Assert.IsTrue(Valid);

            SupportResourcesFFX resources = args.resources as SupportResourcesFFX;

            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = resources.sortScratchBuffer;
            GraphicsBuffer dstPayloadBuffer = resources.payloadScratchBuffer;

            SortConstants constants = default;
            constants.numKeys = args.count;
            uint BlockSize = FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;
            uint NumBlocks = DivRoundUp(args.count, BlockSize);

            // Figure out data distribution
            uint numThreadGroupsToRun = FFX_PARALLELSORT_MAX_THREADGROUPS_TO_RUN;
            uint BlocksPerThreadGroup = (NumBlocks / numThreadGroupsToRun);
            constants.numThreadGroupsWithAdditionalBlocks = NumBlocks % numThreadGroupsToRun;

            if (NumBlocks < numThreadGroupsToRun)
            {
                BlocksPerThreadGroup = 1;
                numThreadGroupsToRun = NumBlocks;
                constants.numThreadGroupsWithAdditionalBlocks = 0;
            }

            constants.numThreadGroups = numThreadGroupsToRun;
            constants.numBlocksPerThreadGroup = BlocksPerThreadGroup;

            // Calculate the number of thread groups to run for reduction (each thread group can process BlockSize number of entries)
            uint numReducedThreadGroupsToRun = FFX_PARALLELSORT_SORT_BIN_COUNT * ((BlockSize > numThreadGroupsToRun) ? 1 : (numThreadGroupsToRun + BlockSize - 1) / BlockSize);
            constants.numReduceThreadgroupPerBin = numReducedThreadGroupsToRun / FFX_PARALLELSORT_SORT_BIN_COUNT;
            constants.numScanValues = numReducedThreadGroupsToRun;	// The number of reduce thread groups becomes our scan count (as each thread group writes out 1 value that needs scan prefix)

            // Setup overall constants
            cmd.SetComputeIntParam(m_CS, "numKeys", (int)constants.numKeys);
            cmd.SetComputeIntParam(m_CS, "numBlocksPerThreadGroup", (int)constants.numBlocksPerThreadGroup);
            cmd.SetComputeIntParam(m_CS, "numThreadGroups", (int)constants.numThreadGroups);
            cmd.SetComputeIntParam(m_CS, "numThreadGroupsWithAdditionalBlocks", (int)constants.numThreadGroupsWithAdditionalBlocks);
            cmd.SetComputeIntParam(m_CS, "numReduceThreadgroupPerBin", (int)constants.numReduceThreadgroupPerBin);
            cmd.SetComputeIntParam(m_CS, "numScanValues", (int)constants.numScanValues);

            // WebGPU does not allow the same buffer to be bound twice, for both read and write access
            var copyReadBuffers = SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;

            var reducedScratchBufferRead = resources.reducedScratchBuffer;
            if (copyReadBuffers)
            {
                if (resources.reducedScratchBufferTemp == null || resources.reducedScratchBufferTemp.count != resources.reducedScratchBuffer.count)
                {
                    resources.reducedScratchBufferTemp?.Dispose();
                    var src = resources.reducedScratchBuffer;
                    reducedScratchBufferRead = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination,
                        src.count, src.stride);
                    reducedScratchBufferRead.name = "reducedScratchBuffer_READ";
                    resources.reducedScratchBufferTemp = reducedScratchBufferRead;
                }
                reducedScratchBufferRead = resources.reducedScratchBufferTemp;
            }

            var scratchBufferRead = resources.scratchBuffer;
            if (copyReadBuffers)
            {
                if (resources.scratchBufferTemp == null || resources.scratchBufferTemp.count != resources.scratchBufferTemp.count)
                {
                    resources.scratchBufferTemp?.Dispose();
                    var src = resources.scratchBuffer;
                    scratchBufferRead = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination,
                        src.count, src.stride);
                    scratchBufferRead.name = "scratchBuffer_READ";

                    resources.scratchBufferTemp = scratchBufferRead;
                }
                scratchBufferRead = resources.scratchBufferTemp;
            }

            // Execute the sort algorithm in 4-bit increments
            constants.shift = 0;
            for (uint i = 0; constants.shift < 32; constants.shift += FFX_PARALLELSORT_SORT_BITS_PER_PASS, ++i)
            {
                cmd.SetComputeIntParam(m_CS, "shift", (int)constants.shift);

                // Sum
                cmd.SetComputeBufferParam(m_CS, m_KernelSum, "rw_source_keys", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelSum, "rw_sum_table", resources.scratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelSum, (int)numThreadGroupsToRun, 1, 1);

                // Reduce
                cmd.SetComputeBufferParam(m_CS, m_KernelReduce, "rw_sum_table", resources.scratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelReduce, "rw_reduce_table", resources.reducedScratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelReduce, (int)numReducedThreadGroupsToRun, 1, 1);

                // Scan
                if (copyReadBuffers)
                {
                    cmd.CopyBuffer(resources.reducedScratchBuffer, reducedScratchBufferRead);
                }
                cmd.SetComputeBufferParam(m_CS, m_KernelScan, "rw_scan_source", reducedScratchBufferRead);
                cmd.SetComputeBufferParam(m_CS, m_KernelScan, "rw_scan_dest", resources.reducedScratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelScan, 1, 1, 1);

                // Scan add
              if (copyReadBuffers)
                {
                    cmd.CopyBuffer(resources.scratchBuffer, scratchBufferRead);
                }

                cmd.SetComputeBufferParam(m_CS, m_KernelScanAdd, "rw_scan_source", scratchBufferRead);
                cmd.SetComputeBufferParam(m_CS, m_KernelScanAdd, "rw_scan_dest", resources.scratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScanAdd, "rw_scan_scratch", resources.reducedScratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelScanAdd, (int)numReducedThreadGroupsToRun, 1, 1);

                // Scatter
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_source_keys", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_dest_keys", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_sum_table", resources.scratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_source_payloads", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_dest_payloads", dstPayloadBuffer);
                cmd.DispatchCompute(m_CS, m_KernelScatter, (int)numThreadGroupsToRun, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }
    }
}
