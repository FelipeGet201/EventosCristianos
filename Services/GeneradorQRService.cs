using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace RedAJP.Servicios // Cambia este namespace según la estructura de tu proyecto
{
    public enum FormaOjos
    {
        Cuadrado,
        Redondeado,
        Inflado
    }

    public enum FormaModulos
    {
        Rellenar,
        Cuadrado,
        Redondeado,
        Inflado
    }
    public class GeneradorQRService
    {
        public string GenerarImagenQR(string url, string hexColorFrente, string hexColorFondo, IFormFile logo, FormaOjos formaOjos, FormaModulos formaModulos)
        {
            QRCodeGenerator qrGenerator = new QRCodeGenerator();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.H);

            Rgba32 cFrente = ParseColor(hexColorFrente);
            Rgba32 cFondo = ParseColor(hexColorFondo);

            int ppm = 30;
            int matrixSize = qrCodeData.ModuleMatrix.Count;

            int offset = 0;
            while (offset < matrixSize && !qrCodeData.ModuleMatrix[offset][offset])
            {
                offset++;
            }
            if (offset >= matrixSize) offset = 0;

            int extraMargin = (offset >= 4) ? 0 : 4;
            int imgSize = (matrixSize + 2 * extraMargin) * ppm;

            using (var img = new Image<Rgba32>(imgSize, imgSize, cFondo))
            {
                var brushModule = CreateModuleBrush(ppm, formaModulos, cFrente);
                var brushEye = CreateEyeBrush(ppm * 7, formaOjos, cFrente, cFondo);

                for (int y = 0; y < matrixSize; y++)
                {
                    for (int x = 0; x < matrixSize; x++)
                    {
                        bool isEye =
                            (x >= offset && x < offset + 7 && y >= offset && y < offset + 7) ||
                            (x >= matrixSize - offset - 7 && x < matrixSize - offset && y >= offset && y < offset + 7) ||
                            (x >= offset && x < offset + 7 && y >= matrixSize - offset - 7 && y < matrixSize - offset);

                        if (isEye) continue;

                        if (qrCodeData.ModuleMatrix[y][x])
                        {
                            int drawX = (x + extraMargin) * ppm;
                            int drawY = (y + extraMargin) * ppm;
                            img.Mutate(ctx => ctx.DrawImage(brushModule, new SixLabors.ImageSharp.Point(drawX, drawY), 1f));
                        }
                    }
                }

                int topEyeY = (offset + extraMargin) * ppm;
                int bottomEyeY = (matrixSize - offset - 7 + extraMargin) * ppm;
                int leftEyeX = (offset + extraMargin) * ppm;
                int rightEyeX = (matrixSize - offset - 7 + extraMargin) * ppm;

                img.Mutate(ctx => ctx.DrawImage(brushEye, new SixLabors.ImageSharp.Point(leftEyeX, topEyeY), 1f));
                img.Mutate(ctx => ctx.DrawImage(brushEye, new SixLabors.ImageSharp.Point(rightEyeX, topEyeY), 1f));
                img.Mutate(ctx => ctx.DrawImage(brushEye, new SixLabors.ImageSharp.Point(leftEyeX, bottomEyeY), 1f));

                if (logo != null && logo.Length > 0)
                {
                    using (var logoStream = logo.OpenReadStream())
                    using (var uploadedLogo = SixLabors.ImageSharp.Image.Load(logoStream))
                    {
                        int logoDestSize = (int)(imgSize * 0.27f);
                        int innerLogoSize = (int)(logoDestSize * 0.90f);

                        var canvasBgColor = new Rgba32(cFondo.R, cFondo.G, cFondo.B, 255);
                        using (var logoBg = new Image<Rgba32>(logoDestSize, logoDestSize, canvasBgColor))
                        {
                            uploadedLogo.Mutate(x => x.Resize(new ResizeOptions
                            {
                                Size = new SixLabors.ImageSharp.Size(innerLogoSize, innerLogoSize),
                                Mode = ResizeMode.Max,
                                Sampler = KnownResamplers.Lanczos3
                            }));

                            int posX = (logoDestSize - uploadedLogo.Width) / 2;
                            int posY = (logoDestSize - uploadedLogo.Height) / 2;
                            logoBg.Mutate(x => x.DrawImage(uploadedLogo, new SixLabors.ImageSharp.Point(posX, posY), 1f));

                            int centerDestX = (imgSize - logoDestSize) / 2;
                            int centerDestY = (imgSize - logoDestSize) / 2;
                            img.Mutate(ctx => ctx.DrawImage(logoBg, new SixLabors.ImageSharp.Point(centerDestX, centerDestY), 1f));
                        }
                    }
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    img.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        public void GenerarYGuardarPrevisualizaciones()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "img", "qr-formas");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            // Generar dinámicamente todas las formas de Ojos que existan en el Enum
            foreach (FormaOjos forma in Enum.GetValues(typeof(FormaOjos)))
            {
                GuardarFormaFisicaOjo(forma, Path.Combine(path, $"Ojo_{forma}.png"));
            }

            // Generar dinámicamente todas las formas de Módulos que existan en el Enum
            foreach (FormaModulos forma in Enum.GetValues(typeof(FormaModulos)))
            {
                GuardarFormaFisicaModulo(forma, Path.Combine(path, $"Modulos_{forma}.png"));
            }
        }
        private void GuardarFormaFisicaOjo(FormaOjos forma, string rutaCompleta)
        {
            int size = 60;
            Rgba32 cFrente = new Rgba32(0, 0, 0, 255);
            Rgba32 cFondo = new Rgba32(255, 255, 255, 255);

            using (var img = new Image<Rgba32>(size, size, cFondo))
            {
                int eyeSize = size - 10;
                var brushEye = CreateEyeBrush(eyeSize, forma, cFrente, cFondo);

                int pos = (size - eyeSize) / 2;
                img.Mutate(ctx => ctx.DrawImage(brushEye, new SixLabors.ImageSharp.Point(pos, pos), 1f));

                img.Save(rutaCompleta, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
            }
        }

        private void GuardarFormaFisicaModulo(FormaModulos forma, string rutaCompleta)
        {
            int size = 60;
            Rgba32 cFrente = new Rgba32(0, 0, 0, 255);
            Rgba32 cFondo = new Rgba32(255, 255, 255, 255);

            using (var img = new Image<Rgba32>(size, size, cFondo))
            {
                int ppm = 20;
                var brushModule = CreateModuleBrush(ppm, forma, cFrente);

                int pos = (size - ppm) / 2;
                img.Mutate(ctx => ctx.DrawImage(brushModule, new SixLabors.ImageSharp.Point(pos, pos), 1f));

                img.Save(rutaCompleta, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
            }
        }

        private void GuardarFormaFisica(string tipo, string forma, string rutaCompleta)
        {
            int size = 60;
            Rgba32 cFrente = new Rgba32(0, 0, 0, 255);
            Rgba32 cFondo = new Rgba32(255, 255, 255, 255);

            using (var img = new Image<Rgba32>(size, size, cFondo))
            {
                if (tipo == "Ojo")
                {
                    int eyeSize = size - 10;
                    // Convertimos el string al Enum FormaOjos
                    FormaOjos enumOjo = Enum.Parse<FormaOjos>(forma);
                    var brushEye = CreateEyeBrush(eyeSize, enumOjo, cFrente, cFondo);

                    int pos = (size - eyeSize) / 2;
                    img.Mutate(ctx => ctx.DrawImage(brushEye, new SixLabors.ImageSharp.Point(pos, pos), 1f));
                }
                else
                {
                    int ppm = 20;
                    // Convertimos el string al Enum FormaModulos
                    FormaModulos enumModulo = Enum.Parse<FormaModulos>(forma);
                    var brushModule = CreateModuleBrush(ppm, enumModulo, cFrente);

                    int pos = (size - ppm) / 2;
                    img.Mutate(ctx => ctx.DrawImage(brushModule, new SixLabors.ImageSharp.Point(pos, pos), 1f));
                }

                img.Save(rutaCompleta, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
            }
        }

        private Rgba32 ParseColor(string hex)
        {
            hex = hex.Replace("#", "");
            byte r = 0, g = 0, b = 0;
            if (hex.Length == 6)
            {
                r = Convert.ToByte(hex.Substring(0, 2), 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
            }
            return new Rgba32(r, g, b, 255);
        }

        private bool IsInsideRoundedRect(float dx, float dy, float halfWidth, float radius)
        {
            if (dx <= halfWidth - radius && dy <= halfWidth) return true;
            if (dx <= halfWidth && dy <= halfWidth - radius) return true;
            float px = dx - (halfWidth - radius);
            float py = dy - (halfWidth - radius);
            if (px > 0 && py > 0) return (px * px + py * py) <= radius * radius;
            return false;
        }

        private bool IsInsideInflado(float dx, float dy, float radius)
        {
            float c = 0.18f; // Factor de curvatura parabólica hacia afuera
            float nx = dx / radius;
            float ny = dy / radius;
            // El límite en X disminuye conforme aumenta Y, creando la "panza" o inflado.
            return nx <= 1f - c * ny * ny && ny <= 1f - c * nx * nx;
        }

        private Image<Rgba32> CreateModuleBrush(int size, FormaModulos forma, Rgba32 cFrente)
        {
            int scale = 4;
            int bigSize = size * scale;

            var img = new Image<Rgba32>(bigSize, bigSize, new Rgba32(0, 0, 0, 0));
            float hw = bigSize / 2f;

            img.ProcessPixelRows(accessor => {
                for (int y = 0; y < bigSize; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < bigSize; x++)
                    {
                        float dx = Math.Abs(x - hw + 0.5f);
                        float dy = Math.Abs(y - hw + 0.5f);
                        bool inside = false;

                        if (forma == FormaModulos.Redondeado)
                        {
                            inside = IsInsideRoundedRect(dx, dy, hw * 0.90f, bigSize * 0.25f);
                        }
                        else if (forma == FormaModulos.Inflado)
                        {
                            inside = IsInsideInflado(dx, dy, hw * 0.90f);
                        }
                        else if (forma == FormaModulos.Cuadrado)
                        {
                            inside = dx <= hw * 0.90f && dy <= hw * 0.90f;
                        }
                        else // FormaModulos.Rellenar
                        {
                            inside = dx <= hw && dy <= hw;
                        }

                        if (inside) row[x] = cFrente;
                    }
                }
            });

            img.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(size, size),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3
            }));

            return img;
        }

        private Image<Rgba32> CreateEyeBrush(int size, FormaOjos forma, Rgba32 cFrente, Rgba32 cFondo)
        {
            int scale = 4;
            int bigSize = size * scale;

            var img = new Image<Rgba32>(bigSize, bigSize, new Rgba32(0, 0, 0, 0));
            float ppm = bigSize / 7f;
            float hw = bigSize / 2f;

            img.ProcessPixelRows(accessor => {
                for (int y = 0; y < bigSize; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < bigSize; x++)
                    {
                        float dx = Math.Abs(x - hw + 0.5f);
                        float dy = Math.Abs(y - hw + 0.5f);

                        if (forma == FormaOjos.Redondeado)
                        {
                            bool inOuter = IsInsideRoundedRect(dx, dy, 3.5f * ppm, 1.2f * ppm);
                            bool inSpace = IsInsideRoundedRect(dx, dy, 2.5f * ppm, 0.4f * ppm);
                            bool inDot = IsInsideRoundedRect(dx, dy, 1.25f * ppm, 0.4f * ppm);

                            if (inDot) row[x] = cFrente;
                            else if (inSpace) row[x] = cFondo;
                            else if (inOuter) row[x] = cFrente;
                        }
                        else if (forma == FormaOjos.Inflado)
                        {
                            bool inOuter = IsInsideInflado(dx, dy, 3.5f * ppm);
                            bool inSpace = IsInsideInflado(dx, dy, 2.5f * ppm);
                            bool inDot = IsInsideInflado(dx, dy, 1.35f * ppm);

                            if (inDot) row[x] = cFrente;
                            else if (inSpace) row[x] = cFondo;
                            else if (inOuter) row[x] = cFrente;
                        }
                        else // FormaOjos.Cuadrado
                        {
                            bool inOuter = dx <= 3.5f * ppm && dy <= 3.5f * ppm;
                            bool inSpace = dx <= 2.5f * ppm && dy <= 2.5f * ppm;
                            bool inDot = dx <= 1.35f * ppm && dy <= 1.35f * ppm;

                            if (inDot) row[x] = cFrente;
                            else if (inSpace) row[x] = cFondo;
                            else if (inOuter) row[x] = cFrente;
                        }
                    }
                }
            });

            img.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(size, size),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3
            }));

            return img;
        }
    }
}