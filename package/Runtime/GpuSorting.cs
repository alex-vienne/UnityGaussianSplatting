using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU (uint key, uint payload) 8 bit-LSD radix sort, using reduce-then-scan
    // Copyright Thomas Smith 2023, MIT license
    // https://github.com/b0nes164/GPUSorting

    public class GpuSorting
    {
        public struct Args
        {
            public uint count;
            public GraphicsBuffer inputKeys;
            public GraphicsBuffer inputValues;
            public SupportResources resources;
            internal int workGroupCount;
        }

        public class SupportResources
        {
            public virtual void Dispose() {}
        }

        public virtual bool Valid => false;

        public virtual void Dispatch(CommandBuffer cmd, Args args) { }

        public virtual void DisposeTempBuffers() {}

        protected static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;
    }
}
