using System.Diagnostics;
using System.Text.RegularExpressions;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceDictate {

public static class Mic {
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)] public struct PropertyKey { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    // Endpoint ids look like "{0.0.1.00000000}.{e374517a-8a5b-40f9-8fb3-e1ba78a2b1cc}"; the server wants the last GUID.
    public static bool TryGetDefault(out string guid, out string name) {
        guid = null;
        name = null;
        IMMDeviceEnumerator e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        IMMDevice d;
        if (e.GetDefaultAudioEndpoint(1, 0, out d) != 0) return false;   // eCapture, eConsole
        string id;
        d.GetId(out id);
        int open = id.LastIndexOf('{');
        guid = id.Substring(open + 1, id.Length - open - 2);
        IPropertyStore ps;
        d.OpenPropertyStore(0, out ps);
        PropertyKey key = new PropertyKey();
        key.fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");   // PKEY_Device_FriendlyName
        key.pid = 14;
        PropVariant v;
        ps.GetValue(ref key, out v);
        name = Marshal.PtrToStringUni(v.ptr);
        PropVariantClear(ref v);
        return true;
    }
}

public enum Kind { Listening, Busy, Message }

// Pure drawing of the dictation pill. Visual language from Lovable's prompt input
// (claude-directory 3d-games/lovable-webgl-hero); meter ported from react-bits SlicedWaves,
// the "识别中" sheen from react-bits ShinyText.
public class Look {
    public const float H = 48f;      // pill height, logical px
    public const float M = 36f;      // transparent margin that holds the drop shadow
    const float ChipX = 24f, ChipR = 16f, ContentX = 52f, Gap = 14f, PadRight = 20f;
    const float MeterW = 120f, MeterH = 26f, SlatGap = 1.5f;

    static readonly Font TitleFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    static readonly Font HintFont = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    static readonly Font IconFont = new Font("Segoe Fluent Icons", 14f, FontStyle.Regular, GraphicsUnit.Pixel);

    public Kind Kind;
    public string Text = "";
    public string Hint = "";
    public double Level;        // smoothed 0..1
    public double Time;         // seconds since the pill appeared
    public double StateTime;    // seconds since the current state began
    public float Width = 200f;  // logical pill width

    public static float WidthFor(Kind kind, string text, string hint) {
        using (Bitmap b = new Bitmap(1, 1))
        using (Graphics g = Graphics.FromImage(b)) {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float hintW = hint.Length > 0 ? Measure(g, hint, HintFont).Width : 0f;
            float textW = text.Length > 0 ? Measure(g, text, TitleFont).Width : 0f;
            if (kind == Kind.Listening) return ContentX + MeterW + Gap + hintW + PadRight;
            if (kind == Kind.Busy) return ContentX + textW + Gap + hintW + PadRight;
            return ContentX + textW + PadRight;
        }
    }

    static SizeF Measure(Graphics g, string s, Font f) {
        return g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);
    }

    // The pill body and its shadow depend only on the width and cost more than half of a frame,
    // so they are painted once and reused; each frame only adds the chip, the meter and the text.
    Bitmap shell;
    int shellWidth;
    float shellScale;

    Bitmap Shell(float scale) {
        int w = (int)Math.Round(Width);
        if (shell != null && shellWidth == w && shellScale == scale) return shell;
        if (shell != null) shell.Dispose();
        shellWidth = w;
        shellScale = scale;
        shell = new Bitmap((int)Math.Ceiling((w + 2 * M) * scale), (int)Math.Ceiling((H + 2 * M) * scale), PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(shell)) {
            Prepare(g, scale);
            RectangleF pill = new RectangleF(0f, 0f, w, H);
            using (GraphicsPath shape = Capsule(pill)) {
                Shadow(g, shape, pill);
                Surface(g, shape, pill);
            }
        }
        return shell;
    }

    static void Prepare(Graphics g, float scale) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(M, M);
    }

    public Bitmap Render(float scale) {
        Bitmap body = Shell(scale);
        Bitmap bmp = new Bitmap(body.Width, body.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp)) {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(body, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            Prepare(g, scale);
            using (GraphicsPath shape = Capsule(new RectangleF(0f, 0f, shellWidth, H))) {
                g.SetClip(shape);   // keeps content inside while the width animates
                Chip(g);
                Content(g);
                g.ResetClip();
            }
        }
        return bmp;
    }

    static GraphicsPath Capsule(RectangleF r) {
        GraphicsPath p = new GraphicsPath();
        float d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 90f, 180f);
        p.AddArc(r.Right - d, r.Y, d, d, 270f, 180f);
        p.CloseFigure();
        return p;
    }

    // Lovable's three stacked drop shadows, softened: that card sits on a black page, this pill floats over any app.
    static void Shadow(Graphics g, GraphicsPath shape, RectangleF r) {
        GraphicsState saved = g.Save();
        using (Region outside = new Region(new RectangleF(-M, -M, r.Width + 2 * M, r.Height + 2 * M))) {
            outside.Exclude(shape);
            g.SetClip(outside, CombineMode.Replace);
            ShadowLayer(g, r, 0.16, 8, 2f);
            ShadowLayer(g, r, 0.14, 16, 6f);
            ShadowLayer(g, r, 0.12, 24, 10f);
        }
        g.Restore(saved);
    }

    // `blur` capsules grown 1px apart compose to `alpha` at the edge and fade out `blur` px away.
    static void ShadowLayer(Graphics g, RectangleF r, double alpha, int blur, float dy) {
        int a = (int)Math.Round((1 - Math.Pow(1 - alpha, 1.0 / blur)) * 255);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(a, 1), 0, 0, 0))) {
            for (int i = 1; i <= blur; i++) {
                using (GraphicsPath p = Capsule(new RectangleF(r.X - i, r.Y + dy - i, r.Width + 2 * i, r.Height + 2 * i)))
                    g.FillPath(b, p);
            }
        }
    }

    static void Surface(Graphics g, GraphicsPath shape, RectangleF r) {
        using (SolidBrush fill = new SolidBrush(Color.FromArgb(171, 43, 38, 38)))
            g.FillPath(fill, shape);
        using (LinearGradientBrush tint = new LinearGradientBrush(new PointF(0f, r.Top - 1f), new PointF(0f, r.Bottom + 1f), Color.Black, Color.Black)) {
            ColorBlend cb = new ColorBlend();
            cb.Colors = new Color[] { Color.FromArgb(59, 118, 100, 50), Color.FromArgb(179, 53, 53, 56), Color.FromArgb(179, 38, 38, 39) };
            cb.Positions = new float[] { 0f, 0.6634f, 1f };
            tint.InterpolationColors = cb;
            g.FillPath(tint, shape);
        }
        using (LinearGradientBrush rimBrush = new LinearGradientBrush(new PointF(0f, r.Top - 1f), new PointF(0f, r.Bottom + 1f), Color.FromArgb(82, 255, 255, 255), Color.FromArgb(13, 255, 255, 255)))
        using (Pen rim = new Pen(rimBrush, 1f))
        using (GraphicsPath inner = Capsule(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f)))
            g.DrawPath(rim, inner);
    }

    void Chip(Graphics g) {
        float cx = ChipX, cy = H / 2f;
        if (Kind == Kind.Message) {
            Disc(g, cx, cy, ChipR, Color.FromArgb(38, 111, 111, 111));
            Glyph(g, "", cx, cy, Color.FromArgb(254, 123, 2));
            return;
        }
        Disc(g, cx, cy, 20f, Color.FromArgb(38, 95, 126, 167));
        RectangleF disc = new RectangleF(cx - ChipR, cy - ChipR, ChipR * 2, ChipR * 2);
        float cssAngle = (float)(Time / 4.0 % 1.0 * 360.0);   // send-btn-bg-rotate: 4s linear infinite
        using (LinearGradientBrush b = new LinearGradientBrush(disc, Color.FromArgb(255, 102, 14), Color.FromArgb(100, 106, 237), cssAngle - 90f))
            g.FillEllipse(b, disc);
        using (GraphicsPath p = new GraphicsPath()) {
            p.AddEllipse(disc);
            using (PathGradientBrush glow = new PathGradientBrush(p)) {
                glow.CenterColor = Color.FromArgb(0, 173, 208, 255);
                glow.SurroundColors = new Color[] { Color.FromArgb(51, 173, 208, 255) };
                glow.FocusScales = new PointF(0.55f, 0.55f);
                g.FillPath(glow, p);
            }
        }
        using (LinearGradientBrush hb = new LinearGradientBrush(new PointF(0f, disc.Top), new PointF(0f, disc.Bottom + 1f), Color.FromArgb(204, 222, 236, 255), Color.FromArgb(0, 222, 236, 255)))
        using (Pen hi = new Pen(hb, 1.5f))
            g.DrawEllipse(hi, cx - ChipR + 1.5f, cy - ChipR + 1.5f, ChipR * 2 - 3f, ChipR * 2 - 3f);
        using (Pen border = new Pen(Color.FromArgb(158, 199, 255), 1f))
            g.DrawEllipse(border, cx - ChipR + 0.5f, cy - ChipR + 0.5f, ChipR * 2 - 1f, ChipR * 2 - 1f);
        if (Kind == Kind.Busy) ConicRing(g, cx, cy, 18f, 2f, (float)(StateTime / 1.5 % 1.0 * 360.0));
        Glyph(g, "", cx, cy, Color.White);
    }

    static void Disc(Graphics g, float cx, float cy, float r, Color c) {
        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
    }

    static void Glyph(Graphics g, string glyph, float cx, float cy, Color color) {
        using (StringFormat sf = new StringFormat())
        using (SolidBrush b = new SolidBrush(color)) {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            g.DrawString(glyph, IconFont, b, new RectangleF(cx - 12f, cy - 12f, 24f, 24f), sf);
        }
    }

    // Lovable's send-button ring: conic white -> #9EC7FF highlight, turned once per 1.5s.
    static void ConicRing(Graphics g, float cx, float cy, float radius, float width, float rotation) {
        RectangleF rc = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
        for (int a = 0; a < 200; a += 4) {
            using (Pen p = new Pen(ConicColor(a + 2f), width))
                g.DrawArc(p, rc, rotation + a - 90f, 4.6f);
        }
    }

    static Color ConicColor(float deg) {
        Color white = Color.White, blue = Color.FromArgb(158, 199, 255);
        if (deg < 60f) return Lerp(Color.FromArgb(0, white), white, deg / 60f);
        if (deg < 120f) return Lerp(white, blue, (deg - 60f) / 60f);
        return Lerp(blue, Color.FromArgb(0, blue), (deg - 120f) / 80f);
    }

    void Content(Graphics g) {
        float cy = H / 2f;
        Color dim = Color.FromArgb(128, 255, 255, 255);
        if (Kind == Kind.Listening) {
            Meter(g, ContentX, cy - MeterH / 2f, MeterW, MeterH);
            Label(g, Hint, HintFont, ContentX + MeterW + Gap, cy, dim);
        } else if (Kind == Kind.Busy) {
            float w = Shiny(g, Text, ContentX, cy);
            Label(g, Hint, HintFont, ContentX + w + Gap, cy, dim);
        } else {
            Label(g, Text, TitleFont, ContentX, cy, Color.FromArgb(235, 255, 255, 255));
        }
    }

    static void Label(Graphics g, string s, Font f, float x, float cy, Color c) {
        if (s.Length == 0) return;
        SizeF sz = Measure(g, s, f);
        using (SolidBrush b = new SolidBrush(c))
            g.DrawString(s, f, b, x, cy - sz.Height / 2f, StringFormat.GenericTypographic);
    }

    // react-bits ShinyText: 120deg gradient #b5b5b5 -> #fff at 50% -> #b5b5b5, background-size 200%,
    // background-position sweeping 150% -> -50% every 2s.
    float Shiny(Graphics g, string s, float x, float cy) {
        SizeF sz = Measure(g, s, TitleFont);
        double p = StateTime / 2.0 % 1.0;
        float offset = -sz.Width * (float)(1.5 - 2.0 * p);
        RectangleF band = new RectangleF(x + offset, cy - sz.Height / 2f, sz.Width * 2f, sz.Height);
        Color baseColor = Color.FromArgb(181, 181, 181);
        using (LinearGradientBrush b = new LinearGradientBrush(band, baseColor, baseColor, 30f)) {
            ColorBlend cb = new ColorBlend();
            cb.Colors = new Color[] { baseColor, baseColor, Color.White, baseColor, baseColor };
            cb.Positions = new float[] { 0f, 0.35f, 0.5f, 0.65f, 1f };
            b.InterpolationColors = cb;
            g.DrawString(s, TitleFont, b, x, cy - sz.Height / 2f, StringFormat.GenericTypographic);
        }
        return sz.Width;
    }

    // react-bits SlicedWaves with a single row: each column's slat rides a sine wave, and
    // `travel` (how far a slat may move) follows the mic level, so silence is a calm line.
    void Meter(Graphics g, float x0, float y0, float w, float h) {
        const int columns = 20;
        const double thickness = 0.14, speed = 3.2, waveSpread = 0.9;
        Color c1 = Color.FromArgb(160, 228, 255), c2 = Color.FromArgb(156, 164, 251), c3 = Color.FromArgb(255, 102, 244);
        double travel = 0.10 + 0.90 * Level;
        double start = (0.5 - thickness * 0.5) * travel, end = (-0.5 + thickness * 0.5) * travel;
        float cell = w / columns;
        float th = (float)(thickness * h);
        for (int i = 0; i < columns; i++) {
            double mv = Math.Sin(Time * speed + i * waveSpread + 1.0) * 0.5 + 0.5;   // + cos(row 0 * rowOffset)
            float cy = y0 + h / 2f + (float)((start + (end - start) * mv) * h);
            Color col = Mix(Mix(c2, c1, mv), c3, (i + 0.5) / columns * 0.45);
            float left = x0 + i * cell + SlatGap / 2f, width = cell - SlatGap;
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(46, col)))
                g.FillRectangle(glow, left, cy - th / 2f - 2.5f, width, th + 5f);
            using (SolidBrush core = new SolidBrush(Color.FromArgb(242, col)))
                g.FillRectangle(core, left, cy - th / 2f, width, th);
        }
    }

    static Color Mix(Color a, Color b, double t) {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static Color Lerp(Color a, Color b, float t) {
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}

// Layered (per-pixel alpha) always-on-top window that never takes focus.
public class Overlay : Form {
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    const int ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    readonly Look look = new Look();
    readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer();
    readonly float scale;
    float targetWidth;
    bool wanted;                  // should be on screen
    double appear;                // 0 hidden .. 1 fully shown
    DateTime shownAt, stateAt, hideAt = DateTime.MaxValue;
    public volatile int Level;    // 0..100 from the server
    public event EventHandler Clicked;

    public Overlay() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        frame.Interval = 28;   // ~36fps: smooth enough for the meter, a third of the redraw cost
        frame.Tick += delegate { Tick(); };
    }

    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    public void ShowListening(string hint) { Post(Kind.Listening, "", hint, 0); }
    public void ShowBusy(string text, string hint) { Post(Kind.Busy, text, hint, 0); }
    public void ShowMessage(string text, int ms) { Post(Kind.Message, text, "", ms); }
    public void HideNow() { BeginInvoke((MethodInvoker)delegate { wanted = false; }); }

    void Post(Kind kind, string text, string hint, int ms) {
        BeginInvoke((MethodInvoker)delegate {
            DateTime now = DateTime.UtcNow;
            look.Kind = kind;
            look.Text = text;
            look.Hint = hint;
            stateAt = now;
            targetWidth = Look.WidthFor(kind, text, hint);
            if (appear <= 0) { look.Width = targetWidth; look.Level = 0; shownAt = now; }
            hideAt = ms > 0 ? now.AddMilliseconds(ms) : DateTime.MaxValue;
            wanted = true;
            if (!Visible) Show();
            frame.Start();
            Tick();
        });
    }

    void Tick() {
        DateTime now = DateTime.UtcNow;
        if (wanted && now >= hideAt) wanted = false;
        // Lovable's prompt input enters with opacity 0->1, y +16->0 over 0.6s ease-out; a HUD has to answer a keypress faster.
        appear = wanted ? Math.Min(1.0, appear + frame.Interval / 180.0) : Math.Max(0.0, appear - frame.Interval / 140.0);
        if (appear <= 0) { frame.Stop(); Hide(); return; }

        double target = Math.Max(0.0, Math.Min(1.0, (Level - 12) / 55.0));
        look.Level += (target - look.Level) * (target > look.Level ? 0.5 : 0.15);   // per frame, retuned for ~36fps
        look.Width += (targetWidth - look.Width) * 0.45f;
        look.Time = (now - shownAt).TotalSeconds;
        look.StateTime = (now - stateAt).TotalSeconds;

        double ease = 1 - Math.Pow(1 - appear, 3);
        using (Bitmap bmp = look.Render(scale)) {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = wa.Left + (wa.Width - bmp.Width) / 2;
            int y = wa.Bottom - bmp.Height + (int)((Look.M - 72f + (1 - ease) * 12f) * scale);
            Push(bmp, x, y, (byte)Math.Round(255 * ease));
        }
    }

    void Push(Bitmap bmp, int x, int y, byte alpha) {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try {
            POINT dst = new POINT();
            dst.X = x;
            dst.Y = y;
            POINT src = new POINT();
            SIZE size = new SIZE();
            size.W = bmp.Width;
            size.H = bmp.Height;
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.SourceConstantAlpha = alpha;
            blend.AlphaFormat = 1;   // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        } finally {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (Clicked != null) Clicked(this, EventArgs.Empty);
    }
}

public class App : ApplicationContext {
    const int WH_MOUSE_LL = 14;
    const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_TOGGLE = 1, HOTKEY_CANCEL = 2;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    const uint VK_SPACE = 0x20, VK_ESCAPE = 0x1B;
    const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hk, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    class Resp { public int Status; public string Body; }

    Overlay overlay;
    HookProc hookRef;          // keep alive; the GC would otherwise collect the delegate
    IntPtr hook;
    HotkeyWindow hotkeyWin;
    int port;
    ushort wantButton;         // 1 = back side button, 2 = forward side button

    // Set by the mouse hook / hotkeys on the UI thread, consumed by the single worker thread.
    volatile bool want;        // user wants to be recording
    volatile bool cancel;      // user asked to abandon the current session
    volatile bool viaHotkey;   // session started from the keyboard, so there is no key-up to end it
    volatile bool active;      // a session is running
    volatile bool recording;   // server is capturing audio

    public App(int port, string mouseButton) {
        // Without this Windows bitmap-stretches the pill on scaled displays and it renders blurry.
        SetProcessDPIAware();
        this.port = port;
        this.wantButton = (ushort)(mouseButton == "side1" ? 1 : (mouseButton == "none" ? 0 : 2));

        overlay = new Overlay();
        IntPtr forceHandle = overlay.Handle;   // BeginInvoke() needs the window to exist before first Show()
        overlay.Clicked += delegate { Cancel(); };

        if (wantButton != 0) {
            Thread hookThread = new Thread(new ThreadStart(HookLoop));
            hookThread.IsBackground = true;
            hookThread.SetApartmentState(ApartmentState.STA);
            hookThread.Start();
        }

        hotkeyWin = new HotkeyWindow(this);
        RegisterHotKey(hotkeyWin.Handle, HOTKEY_TOGGLE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE);

        Thread levelThread = new Thread(new ThreadStart(LevelLoop));
        levelThread.IsBackground = true;
        levelThread.Start();

        Thread workerThread = new Thread(new ThreadStart(WorkerLoop));
        workerThread.IsBackground = true;
        workerThread.Start();
    }

    class HotkeyWindow : NativeWindow {
        App app;
        public HotkeyWindow(App a) { app = a; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_HOTKEY) {
                if (m.WParam.ToInt32() == HOTKEY_TOGGLE) app.Toggle();
                else if (m.WParam.ToInt32() == HOTKEY_CANCEL) app.Cancel();
            }
            base.WndProc(ref m);
        }
    }

    // The hook gets a thread of its own: every mouse event in the system waits for this callback to
    // return, and sharing a thread with the overlay's redraw (17ms a frame) made the pointer stutter.
    void HookLoop() {
        hookRef = new HookProc(MouseHook);
        hook = SetWindowsHookEx(WH_MOUSE_LL, hookRef, GetModuleHandle(null), 0);
        Application.Run();   // a low-level hook only fires while its own thread pumps messages
    }

    IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam) {
        if (code >= 0) {
            int msg = wParam.ToInt32();
            if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP) {
                MSLLHOOKSTRUCT s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if ((ushort)(s.mouseData >> 16) == wantButton) {
                    if (msg == WM_XBUTTONUP) want = false;
                    else if (!want) { viaHotkey = false; want = true; }
                    return (IntPtr)1;   // swallow so the app underneath doesn't navigate back/forward
                }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Toggle() {
        if (want) { want = false; }
        else { viaHotkey = true; want = true; }
    }

    public void Cancel() {
        if (active) { want = false; cancel = true; }
        overlay.HideNow();
    }

    // Esc only belongs to us while a session is on screen.
    void SetEscape(bool on) {
        overlay.BeginInvoke((MethodInvoker)delegate {
            if (on) RegisterHotKey(hotkeyWin.Handle, HOTKEY_CANCEL, MOD_NOREPEAT, VK_ESCAPE);
            else UnregisterHotKey(hotkeyWin.Handle, HOTKEY_CANCEL);
        });
    }

    // Polls the server's current input level on its own thread so the UI never blocks.
    void LevelLoop() {
        while (true) {
            if (recording) {
                Resp r = Call("/level", 1000);
                int lv;
                if (r.Status == 200 && int.TryParse(r.Body, out lv)) overlay.Level = lv;
                Thread.Sleep(70);
            } else {
                Thread.Sleep(150);
            }
        }
    }

    // One worker serializes every server call, so start/stop/cancel can never interleave.
    void WorkerLoop() {
        while (true) {
            if (!want) { Thread.Sleep(20); continue; }
            cancel = false;
            active = true;
            SetEscape(true);
            try {
                RunSession();
            } catch (Exception ex) {
                Console.Error.WriteLine(ex);
                want = false;
                overlay.ShowMessage("出错了：" + ex.Message, 3000);
            }
            recording = false;
            active = false;
            SetEscape(false);
        }
    }

    void RunSession() {
        string guid, name;
        if (!Mic.TryGetDefault(out guid, out name)) {
            want = false;
            overlay.ShowMessage("没有找到麦克风", 2500);
            return;
        }

        overlay.ShowListening(viaHotkey ? "再按 Ctrl+Alt+Space 结束 · Esc 取消" : "松开结束 · Esc 取消");
        Resp r = Call("/start?device=" + guid, 8000);
        if (cancel) {
            if (r.Status == 200) Call("/cancel", 3000);
            return;
        }
        if (r.Status != 200) {
            Console.Error.WriteLine(r.Body);
            want = false;
            overlay.ShowMessage("麦克风打不开：" + name, 3000);
            return;
        }

        recording = true;
        while (want && !cancel) Thread.Sleep(20);
        recording = false;
        if (cancel) {
            Call("/cancel", 3000);
            return;
        }

        overlay.ShowBusy("识别中…", "Esc 取消");
        r = Call("/stop", 60000);
        if (cancel) return;
        if (r.Status == 200 && r.Body.Length > 0) {
            overlay.HideNow();
            TypeText(r.Body.Replace('\r', ' ').Replace('\n', ' '));
        } else if (r.Status == 200) {
            overlay.ShowMessage("没识别到内容", 1800);
        } else if (r.Status == 422) {
            overlay.ShowMessage("没听到声音，检查「" + name + "」是否静音", 4000);
        } else {
            Console.Error.WriteLine(r.Body);
            overlay.ShowMessage("识别失败", 2500);
        }
    }

    Resp Call(string path, int timeoutMs) {
        Resp result = new Resp();
        try {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Proxy = null;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse()) {
                result.Status = (int)res.StatusCode;
                result.Body = ReadBody(res);
            }
        } catch (WebException e) {
            HttpWebResponse res = e.Response as HttpWebResponse;
            if (res != null) {
                using (res) {
                    result.Status = (int)res.StatusCode;
                    result.Body = ReadBody(res);
                }
            } else {
                result.Status = 0;
                result.Body = e.Message;
            }
        }
        return result;
    }

    static string ReadBody(HttpWebResponse res) {
        using (StreamReader sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd().Trim();
    }

    void TypeText(string text) {
        System.Collections.Generic.List<INPUT> list = new System.Collections.Generic.List<INPUT>();
        foreach (char c in text) {
            for (int up = 0; up < 2; up++) {
                INPUT i = new INPUT();
                i.type = 1;
                i.ki.wScan = c;
                i.ki.dwFlags = KEYEVENTF_UNICODE | (uint)(up == 1 ? KEYEVENTF_KEYUP : 0);
                list.Add(i);
            }
        }
        if (list.Count > 0) SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
    }
}
}

namespace VoiceDictate {

// Entry point of the compiled app. A GUI-subsystem exe has no console window, so there is
// nothing to close by accident, and it starts without recompiling the C# on every launch.
public static class Program {
    static bool Ping(int port) {
        try {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/ping");
            req.Proxy = null;
            req.Timeout = 2000;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse()) return (int)res.StatusCode == 200;
        } catch {
            return false;
        }
    }

    static void EnsureServer(string root, int port) {
        if (Ping(port)) return;
        ProcessStartInfo psi = new ProcessStartInfo("node", "\"" + Path.Combine(root, "stt-server.js") + "\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = root;
        Process.Start(psi);
        for (int i = 0; i < 60 && !Ping(port); i++) Thread.Sleep(500);
    }

    static string MouseButton(string configPath) {
        try {
            if (File.Exists(configPath)) {
                Match m = Regex.Match(File.ReadAllText(configPath), "\"mouseButton\"\\s*:\\s*\"(\\w+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
        } catch { }
        return "side2";
    }

    [STAThread]
    public static void Main() {
        bool mine;
        using (Mutex only = new Mutex(true, "VoiceDictate.SingleInstance", out mine)) {
            if (!mine) return;   // autostart and a manual launch must not both install a mouse hook
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            const int port = 8377;
            EnsureServer(root, port);
            Application.Run(new App(port, MouseButton(Path.Combine(root, "config.json"))));
        }
    }
}
}