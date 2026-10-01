using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    public enum PreviewMode
    {
        Color = 0,
        Red = 1,
        Green = 2,
        Blue = 3,
        Alpha = 4
    }

    /// <summary>Turns an evaluated (downscaled) image into a texture for display. Display only, never saved.</summary>
    sealed class PreviewBuilder
    {
        Texture2D m_Texture;
        Color32[] m_Pixels;

        public Texture2D Texture => m_Texture;

        public void Build(PixelImage image, PreviewMode mode)
        {
            if (image == null)
                return;
            int w = image.Width, h = image.Height;
            if (m_Texture == null || m_Texture.width != w || m_Texture.height != h)
            {
                Dispose();
                // sRGB storage: values are shown like any image viewer shows the file.
                m_Texture = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                m_Pixels = new Color32[w * h];
            }

            for (int y = 0; y < h; y++)
            {
                int src = y * w;
                int dst = (h - 1 - y) * w; // Unity textures are bottom-up.
                for (int x = 0; x < w; x++)
                {
                    int i = src + x;
                    byte r, g, b;
                    switch (mode)
                    {
                        case PreviewMode.Red:
                            r = g = b = Q(image.R[i]);
                            break;
                        case PreviewMode.Green:
                            r = g = b = Q(image.G[i]);
                            break;
                        case PreviewMode.Blue:
                            r = g = b = Q(image.B[i]);
                            break;
                        case PreviewMode.Alpha:
                            r = g = b = image.A != null ? Q(image.A[i]) : (byte)255;
                            break;
                        default:
                            r = Q(image.R[i]);
                            g = Q(image.G[i]);
                            b = Q(image.B[i]);
                            break;
                    }
                    m_Pixels[dst + x] = new Color32(r, g, b, 255);
                }
            }
            m_Texture.SetPixels32(m_Pixels);
            m_Texture.Apply(false, false);
        }

        static byte Q(float v)
        {
            return (byte)ColorMath.Quantize(v, 255);
        }

        public void Dispose()
        {
            if (m_Texture != null)
                Object.DestroyImmediate(m_Texture);
            m_Texture = null;
            m_Pixels = null;
        }
    }
}
