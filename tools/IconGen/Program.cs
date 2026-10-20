using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Generates the app icon: three fingers + a mouse cursor on a gradient rounded square.
// Usage: dotnet run -c Release -- <output.ico> [preview.png]

string icoPath = args.Length > 0 ? args[0] : "app.ico";
int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

var frames = sizes.Select(RenderPng).ToList();
WriteIco(icoPath, sizes, frames);

// validate: System.Drawing.Icon parses the ICO structure
using (var validate = new Icon(icoPath)) {
    Console.WriteLine($"ico written: {icoPath} ({new FileInfo(icoPath).Length} bytes, valid, {validate.Size.Width}px base frame)");
}

if (args.Length > 1) {
    File.WriteAllBytes(args[1], RenderPng(512));
    Console.WriteLine($"preview written: {args[1]}");
}

static byte[] RenderPng(int size) {
    using var bmp = new Bitmap(size, size);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    float k = size / 256f; // design space: 256x256

    // background: rounded square with a diagonal blue→cyan gradient
    var rect = new RectangleF(6 * k, 6 * k, 244 * k, 244 * k);
    using (var path = Rounded(rect, 58 * k)) {
        using var brush = new LinearGradientBrush(rect, Color.FromArgb(37, 99, 235), Color.FromArgb(6, 182, 212), 45f);
        g.FillPath(brush, path);
    }

    using var white = new SolidBrush(Color.White);

    // side fingers: two rounded bars
    foreach (float x in new[] { 44 * k, 186 * k }) {
        using var bar = Rounded(new RectangleF(x, 82 * k, 30 * k, 112 * k), 15 * k);
        g.FillPath(white, bar);
    }

    // middle element: the mouse cursor (classic arrow polygon, unit coords, tip at 0,0)
    float[][] cursor = {
        new[] { 0f, 0f }, new[] { 0f, 17f }, new[] { 4.2f, 13.2f }, new[] { 6.8f, 19f },
        new[] { 9.6f, 17.8f }, new[] { 7f, 12.2f }, new[] { 12.6f, 12.2f },
    };
    float scale = 6.4f * k;
    float tipX = 106 * k, tipY = 62 * k;
    using (var path = new GraphicsPath()) {
        path.AddPolygon(cursor.Select(p => new PointF(tipX + p[0] * scale, tipY + p[1] * scale)).ToArray());
        g.FillPath(white, path);
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static GraphicsPath Rounded(RectangleF r, float rad) {
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, 2 * rad, 2 * rad, 180, 90);
    p.AddArc(r.Right - 2 * rad, r.Y, 2 * rad, 2 * rad, 270, 90);
    p.AddArc(r.Right - 2 * rad, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90);
    p.AddArc(r.X, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90);
    p.CloseFigure();
    return p;
}

static void WriteIco(string path, int[] sizes, List<byte[]> pngs) {
    using var fs = new FileStream(path, FileMode.Create);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    uint offset = 6 + (uint)(16 * sizes.Length);
    for (int i = 0; i < sizes.Length; i++) {
        byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
        w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write((uint)pngs[i].Length); w.Write(offset);
        offset += (uint)pngs[i].Length;
    }
    foreach (var png in pngs) w.Write(png);
}
