using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("BIEngine.UniXD.Slicing.Burst.Editor")]

namespace BIEngine.UniXD.Slicing
{
	public static class Slicer
	{
		internal static System.Func<Texture2D, SliceOptions, bool, (bool handled, SlicedTexture result)> accelerator { get; set; }

		public static SlicedTexture Slice(Texture2D texture, SliceOptions options)
		{
			return Process(texture, options, false);
		}

		public static bool TrySlice(Texture2D texture, SliceOptions options, out SlicedTexture sliced)
		{
			sliced = Process(texture, options, true);
			return sliced != null;
		}

		private static SlicedTexture Process(Texture2D texture, SliceOptions options, bool skipUnchanged)
		{
			if (accelerator != null)
			{
				var accelerated = accelerator(texture, options, skipUnchanged);
				if (accelerated.handled) return accelerated.result;
			}
			return new Runner(texture, options).Run(skipUnchanged);
		}

		internal class Runner
		{
			private readonly Texture2D _texture;
			private readonly SliceOptions _options;
			private int _width;
			private int _height;
			private Color32[] _pixels;

			public Runner(Texture2D texture, SliceOptions options)
			{
				_texture = texture;
				_options = options;
			}

			public SlicedTexture Run(bool skipUnchanged = false)
			{
				_width = _texture.width;
				_height = _texture.height;
				_pixels = _texture.GetPixels32();
				for (var i = 0; i < _pixels.Length; ++i)
					if (_pixels[i].a == 0) _pixels[i] = Color.clear;

				var xDiffList = CalcDiffList(_width, _height, 1, _width);
				var (xStart, xEnd) = CalcLine(xDiffList);

				var yDiffList = CalcDiffList(1, _width, _width, _height);
				var (yStart, yEnd) = CalcLine(yDiffList);

				var skipX = (xStart == 0 && xEnd == 0);
				var skipY = (yStart == 0 && yEnd == 0);
				if (skipUnchanged && skipX && skipY) return null;
				var output = GenerateSlicedTexture(xStart, xEnd, yStart, yEnd, skipX, skipY);

				var left = xStart;
				var bottom = yStart;
				var right = (_width - xEnd) - 1;
				var top = (_height - yEnd) - 1;

				if (skipX)
				{
					left = 0;
					right = 0;
				}

				if (skipY)
				{
					top = 0;
					bottom = 0;
				}

				return new SlicedTexture(output, new Border(left, bottom, right, top));
			}

			private ulong[] CalcDiffList(int lineDelta, int lineLength, int lineSeek, int length)
			{
				var diffList = new ulong[length];
				diffList[0] = ulong.MaxValue;
				if (_options.Tolerate == 0)
				{
					// Exact equality is transitive; stop at the first mismatch.
					for (var i = 1; i < length; ++i)
						for (var j = 0; j < lineLength; ++j)
						{
							var a = _pixels[(i - 1) * lineSeek + j * lineDelta];
							var b = _pixels[i * lineSeek + j * lineDelta];
							if (a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a) continue;
							diffList[i] = 1;
							break;
						}
					return diffList;
				}
				var minimum = new Color32[lineLength];
				var maximum = new Color32[lineLength];
				for (var j = 0; j < lineLength; ++j)
					minimum[j] = maximum[j] = _pixels[j * lineDelta];

				for (var i = 1; i < length; ++i)
				{
					ulong diff = 0;
					var current = i * lineSeek;
					for (var j = 0; j < lineLength; ++j)
					{
						var pixel = _pixels[current];
						var low = minimum[j];
						var high = maximum[j];
						low = new Color32((byte) Mathf.Min(low.r, pixel.r), (byte) Mathf.Min(low.g, pixel.g),
							(byte) Mathf.Min(low.b, pixel.b), (byte) Mathf.Min(low.a, pixel.a));
						high = new Color32((byte) Mathf.Max(high.r, pixel.r), (byte) Mathf.Max(high.g, pixel.g),
							(byte) Mathf.Max(high.b, pixel.b), (byte) Mathf.Max(high.a, pixel.a));
						minimum[j] = low;
						maximum[j] = high;
						// Bound the whole run, not each adjacent step: otherwise a smooth
						// gradient can drift arbitrarily far while every step passes tolerance.
						if (Diff(low, high) != 0) { diff = 1; break; }
						current += lineDelta;
					}
					diffList[i] = diff;
					if (diff != 0)
						for (var j = 0; j < lineLength; ++j)
							minimum[j] = maximum[j] = _pixels[i * lineSeek + j * lineDelta];
				}

				return diffList;
			}

			private int Diff(Color32 a, Color32 b)
			{
				var rd = Mathf.Abs(a.r - b.r);
				var gd = Mathf.Abs(a.g - b.g);
				var bd = Mathf.Abs(a.b - b.b);
				var ad = Mathf.Abs(a.a - b.a);
				if (rd <= _options.Tolerate) rd = 0;
				if (gd <= _options.Tolerate) gd = 0;
				if (bd <= _options.Tolerate) bd = 0;
				if (ad <= _options.Tolerate) ad = 0;
				return rd + gd + bd + ad;
			}

			internal (int Start, int End) CalcLine(ulong[] list)
			{
				var start = 0;
				var end = 0;
				var tmpStart = 0;
				var tmpEnd = 0;
				for (var i = 0; i < list.Length; ++i)
				{
					if (list[i] == 0)
					{
						tmpEnd = i;
						continue;
					}

					if (end - start < tmpEnd - tmpStart)
					{
						start = tmpStart;
						end = tmpEnd;
					}

					tmpStart = i;
					tmpEnd = i;
				}

				if (end - start < tmpEnd - tmpStart)
				{
					start = tmpStart;
					end = tmpEnd;
				}

				start += _options.Margin;
				end -= _options.Margin;

				if (end <= start)
				{
					start = 0;
					end = 0;
				}

				return (start, end);
			}

			private Texture2D GenerateSlicedTexture(int xStart, int xEnd, int yStart, int yEnd, bool skipX, bool skipY)
			{
				var outputWidth = _width - (xEnd - xStart) + (skipX ? 0 : _options.CenterSize - 1);
				var outputHeight = _height - (yEnd - yStart) + (skipY ? 0 : _options.CenterSize - 1);
				var outputPixels = new Color32[outputWidth * outputHeight];
				if (skipX && skipY) System.Array.Copy(_pixels, outputPixels, _pixels.Length);
				else for (var y = 0; y < outputHeight; ++y)
				{
					var originalY = y + (!skipY && y >= yStart ? (yEnd - yStart) - _options.CenterSize + 1 : 0);
					var sourceRow = originalY * _width;
					var outputRow = y * outputWidth;
					if (skipX) System.Array.Copy(_pixels, sourceRow, outputPixels, outputRow, outputWidth);
					else
					{
						System.Array.Copy(_pixels, sourceRow, outputPixels, outputRow, xStart);
						var sourceX = xEnd - _options.CenterSize + 1;
						System.Array.Copy(_pixels, sourceRow + sourceX, outputPixels, outputRow + xStart, outputWidth - xStart);
					}
				}

				var output = new Texture2D(outputWidth, outputHeight);
				output.SetPixels32(outputPixels);
				return output;
			}
		}
	}
}
