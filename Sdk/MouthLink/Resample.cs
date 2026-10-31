namespace UnifiedPicoModule.Sdk.MouthLink;

/// <summary>
/// 8-bit grayscale resize that reproduces Pillow's <c>Image.resize(size, Image.BILINEAR)</c> bit for bit
/// (libImaging/Resample.c: triangle filter whose support grows with the downscale factor, 22-bit fixed-point
/// coefficients, horizontal pass first, rounding to uint8 between passes). Training and the Python tools
/// resize with Pillow, so the model must see exactly the same pixels here.
/// </summary>
internal sealed class PillowBilinear
{
    private const int PrecisionBits = 32 - 8 - 2;
    private readonly int inW, inH, outW, outH;
    private readonly int kH, kV;
    private readonly int[] boundsH, boundsV, coeffH, coeffV;
    private readonly int firstRow, rows;
    private readonly byte[] temp;

    public int InWidth => inW;
    public int InHeight => inH;

    public PillowBilinear(int inWidth, int inHeight, int outWidth, int outHeight)
    {
        inW = inWidth; inH = inHeight; outW = outWidth; outH = outHeight;
        kH = Coefficients(inW, outW, out boundsH, out coeffH);
        kV = Coefficients(inH, outH, out boundsV, out coeffV);
        firstRow = boundsV[0];
        int lastRow = boundsV[outH * 2 - 2] + boundsV[outH * 2 - 1];
        rows = lastRow - firstRow;
        temp = new byte[outW * rows];
    }

    private static double Triangle(double x)
    {
        if (x < 0) x = -x;
        return x < 1 ? 1 - x : 0;
    }

    // precompute_coeffs + normalize_coeffs_8bpp for the whole input range (box 0..inSize).
    private static int Coefficients(int inSize, int outSize, out int[] bounds, out int[] coeffs)
    {
        double scale = (double)inSize / outSize;
        double filterScale = scale < 1 ? 1 : scale;
        double support = 1.0 * filterScale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var k = new double[outSize * ksize];
        bounds = new int[outSize * 2];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            double ww = 0, ss = 1.0 / filterScale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            int o = xx * ksize;
            for (int x = 0; x < xmax; x++)
            {
                double w = Triangle((x + xmin - center + 0.5) * ss);
                k[o + x] = w; ww += w;
            }
            for (int x = 0; x < xmax; x++) if (ww != 0) k[o + x] /= ww;
            bounds[xx * 2] = xmin; bounds[xx * 2 + 1] = xmax;
        }
        coeffs = new int[k.Length];
        for (int i = 0; i < k.Length; i++)
            coeffs[i] = k[i] < 0 ? (int)(-0.5 + k[i] * (1 << PrecisionBits)) : (int)(0.5 + k[i] * (1 << PrecisionBits));
        return ksize;
    }

    private static byte Clip8(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>Resizes <paramref name="src"/> (inWidth×inHeight, row-major) into <paramref name="dst"/> as 0..1 floats.</summary>
    public void Run(ReadOnlySpan<byte> src, Span<float> dst)
    {
        bool horizontal = outW != inW, vertical = outH != inH;
        ReadOnlySpan<byte> rowsSrc;
        int stride, rowOffset;
        if (horizontal)
        {
            for (int yy = 0; yy < rows; yy++)
            {
                var line = src.Slice((yy + firstRow) * inW, inW);
                for (int xx = 0; xx < outW; xx++)
                {
                    int xmin = boundsH[xx * 2], xmax = boundsH[xx * 2 + 1], o = xx * kH;
                    int ss = 1 << (PrecisionBits - 1);
                    for (int x = 0; x < xmax; x++) ss += line[x + xmin] * coeffH[o + x];
                    temp[yy * outW + xx] = Clip8(ss);
                }
            }
            rowsSrc = temp; stride = outW; rowOffset = 0;
        }
        else { rowsSrc = src; stride = inW; rowOffset = firstRow; }

        if (!vertical)
        {
            for (int yy = 0; yy < outH; yy++)
                for (int xx = 0; xx < outW; xx++)
                    dst[yy * outW + xx] = rowsSrc[(yy + (horizontal ? 0 : rowOffset)) * stride + xx] / 255f;
            return;
        }
        for (int yy = 0; yy < outH; yy++)
        {
            int ymin = boundsV[yy * 2] - firstRow + rowOffset, ymax = boundsV[yy * 2 + 1], o = yy * kV;
            for (int xx = 0; xx < outW; xx++)
            {
                int ss = 1 << (PrecisionBits - 1);
                for (int y = 0; y < ymax; y++) ss += rowsSrc[(y + ymin) * stride + xx] * coeffV[o + y];
                dst[yy * outW + xx] = Clip8(ss) / 255f;
            }
        }
    }
}
