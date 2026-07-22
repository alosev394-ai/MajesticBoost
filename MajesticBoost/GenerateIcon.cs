using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class GenerateIcon
{
    private static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("Output .ico path is required.");
        }

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (Bitmap bitmap = DrawIcon(size))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                images.Add(stream.ToArray());
            }
        }

        using (var output = new FileStream(args[0], FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int dataOffset = 6 + sizes.Length * 16;
            for (int index = 0; index < sizes.Length; index++)
            {
                writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[index].Length);
                writer.Write(dataOffset);
                dataOffset += images[index].Length;
            }
            foreach (byte[] image in images)
            {
                writer.Write(image);
            }
        }
    }

    private static Bitmap DrawIcon(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            float scale = size / 256F;
            using (GraphicsPath frame = RoundedRectangle(new RectangleF(9 * scale, 9 * scale, 238 * scale, 238 * scale), 31 * scale))
            using (var background = new SolidBrush(Color.FromArgb(27, 27, 27)))
            using (var border = new Pen(Color.FromArgb(232, 28, 90), Math.Max(1F, 5F * scale)))
            {
                graphics.FillPath(background, frame);
                graphics.DrawPath(border, frame);
            }

            GraphicsState state = graphics.Save();
            graphics.TranslateTransform(size / 2F, size / 2F);
            graphics.RotateTransform(43F);
            graphics.ScaleTransform(scale, scale);

            using (var flame = new GraphicsPath())
            {
                flame.AddBezier(-18, 49, -30, 72, -16, 93, 0, 101);
                flame.AddBezier(0, 101, 16, 93, 30, 72, 18, 49);
                flame.AddLine(18, 49, -18, 49);
                using (var brush = new LinearGradientBrush(new RectangleF(-28, 48, 56, 55), Color.FromArgb(255, 209, 102), Color.FromArgb(232, 28, 90), 90F))
                {
                    graphics.FillPath(brush, flame);
                }
            }

            using (var leftFin = new GraphicsPath())
            using (var rightFin = new GraphicsPath())
            using (var finBrush = new SolidBrush(Color.FromArgb(232, 28, 90)))
            using (var darkFinBrush = new SolidBrush(Color.FromArgb(128, 20, 58)))
            {
                leftFin.AddPolygon(new[] { new PointF(-24, 25), new PointF(-48, 52), new PointF(-18, 47) });
                rightFin.AddPolygon(new[] { new PointF(24, 25), new PointF(48, 52), new PointF(18, 47) });
                graphics.FillPath(finBrush, leftFin);
                graphics.FillPath(darkFinBrush, rightFin);
            }

            using (var body = new GraphicsPath())
            {
                body.StartFigure();
                body.AddBezier(0, -76, -31, -51, -38, 9, -24, 51);
                body.AddBezier(-24, 51, -17, 66, 17, 66, 24, 51);
                body.AddBezier(24, 51, 38, 9, 31, -51, 0, -76);
                body.CloseFigure();
                using (var brush = new LinearGradientBrush(new RectangleF(-40, -76, 80, 142), Color.White, Color.FromArgb(194, 194, 198), 28F))
                using (var pen = new Pen(Color.FromArgb(245, 245, 245), 2F))
                {
                    graphics.FillPath(brush, body);
                    graphics.DrawPath(pen, body);
                }
            }

            using (var windowBrush = new SolidBrush(Color.FromArgb(232, 28, 90)))
            using (var windowPen = new Pen(Color.White, 3F))
            {
                graphics.FillEllipse(windowBrush, -14, -27, 28, 28);
                graphics.DrawEllipse(windowPen, -14, -27, 28, 28);
            }

            graphics.Restore(state);
        }
        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
