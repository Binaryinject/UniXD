using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Slicing.Burst
{
    [InitializeOnLoad]
    internal static class BurstSlicer
    {
        // Small textures are faster without native scheduling and buffer setup.
        private const int m_MinPixels = 1024 * 1024;

        static BurstSlicer() => Slicer.accelerator = TrySlice;

        private static (bool handled, SlicedTexture result) TrySlice(Texture2D texture, SliceOptions options, bool skipUnchanged)
        {
            if (texture == null || options == null || options.Tolerate != 0 || options.Margin < 0 || options.CenterSize < 1 ||
                (long)texture.width * texture.height < m_MinPixels || !texture.isReadable || !BurstCompiler.Options.EnableBurstCompilation)
                return (false, null);

            var stride = 4;
            var r = 0; var g = 1; var b = 2; var a = 3;
            switch (texture.format)
            {
                case TextureFormat.RGB24: stride = 3; a = -1; break;
                case TextureFormat.RGBA32: break;
                case TextureFormat.ARGB32: r = 1; g = 2; b = 3; a = 0; break;
                case TextureFormat.BGRA32: r = 2; b = 0; break;
                default: return (false, null);
            }

            var width = texture.width;
            var height = texture.height;
            // Borrow texture-owned readable data; never dispose or mutate it.
            var source = texture.GetPixelData<byte>(0);
            using (var differences = new NativeArray<ulong>(width + height, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
            {
                new ScanLines
                {
                    source = source, differences = differences, width = width, height = height,
                    stride = stride, r = r, g = g, b = b, a = a
                }.Schedule(width + height, 32).Complete();

                var xFlags = new ulong[width];
                var yFlags = new ulong[height];
                differences.GetSubArray(0, width).CopyTo(xFlags);
                differences.GetSubArray(width, height).CopyTo(yFlags);
                var runner = new Slicer.Runner(texture, options);
                var (xStart, xEnd) = runner.CalcLine(xFlags);
                var (yStart, yEnd) = runner.CalcLine(yFlags);
                var skipX = xStart == 0 && xEnd == 0;
                var skipY = yStart == 0 && yEnd == 0;
                if (skipUnchanged && skipX && skipY) return (true, null);
                var xDelta = skipX ? 0 : xEnd - xStart - options.CenterSize + 1;
                var yDelta = skipY ? 0 : yEnd - yStart - options.CenterSize + 1;
                var output = new Texture2D(width - xDelta, height - yDelta);
                try
                {
                    new CopyRows
                    {
                        source = source, output = output.GetPixelData<Color32>(0), sourceWidth = width, width = output.width,
                        stride = stride, xStart = xStart, yStart = yStart, xDelta = xDelta, yDelta = yDelta,
                        r = r, g = g, b = b, a = a
                    }.Schedule(output.height, 8).Complete();
                    return (true, new SlicedTexture(output, new Border(skipX ? 0 : xStart, skipY ? 0 : yStart,
                        skipX ? 0 : width - xEnd - 1, skipY ? 0 : height - yEnd - 1)));
                }
                catch
                {
                    Object.DestroyImmediate(output);
                    throw;
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct ScanLines : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> source;
            [WriteOnly] public NativeArray<ulong> differences;
            public int width, height, stride, r, g, b, a;

            private uint Pixel(int index)
            {
                var p = index * stride;
                var alpha = a < 0 ? 255 : source[p + a];
                return alpha == 0 ? 0 : (uint)(source[p + r] | source[p + g] << 8 | source[p + b] << 16 | alpha << 24);
            }

            public void Execute(int index)
            {
                var columns = index < width;
                var line = columns ? index : index - width;
                if (line == 0) { differences[index] = ulong.MaxValue; return; }
                var offset = columns ? 1 : width;
                var step = columns ? width : 1;
                var length = columns ? height : width;
                var current = line * offset;
                var previous = current - offset;
                ulong different = 0;
                for (var p = 0; p < length; p++, current += step, previous += step)
                    if (Pixel(current) != Pixel(previous)) { different = 1; break; }
                differences[index] = different;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct CopyRows : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> source;
            // Each job owns one disjoint output row, rather than one output element.
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<Color32> output;
            public int sourceWidth, width, stride, xStart, yStart, xDelta, yDelta, r, g, b, a;

            public void Execute(int y)
            {
                var row = (y + (y >= yStart ? yDelta : 0)) * sourceWidth;
                for (var x = 0; x < width; x++)
                {
                    var p = (row + x + (x >= xStart ? xDelta : 0)) * stride;
                    var alpha = (byte)(a < 0 ? 255 : source[p + a]);
                    output[y * width + x] = alpha == 0 ? new Color32() : new Color32(source[p + r], source[p + g], source[p + b], alpha);
                }
            }
        }
    }
}
