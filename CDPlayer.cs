// CD Player 98 —— Win98 风格的“正在播放”小窗
//  * 当前播放：Windows 系统媒体会话 (SMTC)，QQ音乐/网易云/Spotify/浏览器 等通用
//  * 频谱/波形：WASAPI 回环采集系统输出 + 自己实现的 FFT
//  * 音量：Core Audio 系统主音量
// 纯 C# 5 + .NET Framework 4 + Win32/COM，零第三方依赖。编译：build.bat
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Media.Control;
using Timer = System.Windows.Forms.Timer;

[assembly: System.Reflection.AssemblyTitle("CD Player 98")]
[assembly: System.Reflection.AssemblyDescription("Windows 98 style now-playing widget")]
[assembly: System.Reflection.AssemblyProduct("CD Player 98")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 Buck008, MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace CDPlayer98
{
    static class Program
    {
        // 写到 %TEMP%\CDPlayer98-error.log，方便排查
        static void LogError(Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CDPlayer98-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--snapshot")
            {
                using (var f = new PlayerForm(true)) f.Snapshot(args[1]);
                return;
            }
            if (args.Length == 2 && args[0] == "--export-icon")
            {
                // 开发用：生成 CDPlayer.ico 和 README 用的 icon.png
                CdIconArt.Export(args[1]);
                return;
            }
            bool created;
            using (var m = new Mutex(true, "CDPlayer98_SingleInstance", out created))
            {
                if (!created) return;
                Native.EnablePerMonitorDpi();
                // 兜底：界面线程上的意外异常写入日志，不再弹出 .NET 的错误对话框
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => LogError(e.Exception);
                Application.Run(new PlayerForm(false));
            }
        }
    }

    static class C98
    {
        public static readonly Color Face = Color.FromArgb(192, 192, 192);
        public static readonly Color Light = Color.FromArgb(223, 223, 223);
        public static readonly Color White = Color.White;
        public static readonly Color Shadow = Color.FromArgb(128, 128, 128);
        public static readonly Color Dark = Color.Black;
        public static readonly Color Navy = Color.FromArgb(0, 0, 128);
        public static readonly Color TitleB = Color.FromArgb(16, 132, 208);
        public static readonly Color InactA = Color.FromArgb(128, 128, 128);
        public static readonly Color InactB = Color.FromArgb(192, 192, 192);
        public static readonly Color Led = Color.FromArgb(0, 230, 0);
        public static readonly Color LedPeak = Color.FromArgb(150, 255, 150);
        public static readonly Color LedOff = Color.FromArgb(0, 38, 0);
    }

    enum Hit { None, Title, Min, Max, Close, Menu0, Menu1, Menu2, Menu3, Prev, Play, Pause, Next, Speaker, Slider, ArtistDrop, TrackDrop, Vis, Grip }

    class PlayerForm : Form
    {
        const int LW = 340, LH = 230;
        // 布局（逻辑像素，1x）
        static readonly Rectangle RTitle = new Rectangle(3, 3, LW - 6, 18);
        static readonly Rectangle RClose = new Rectangle(LW - 21, 5, 16, 14);
        static readonly Rectangle RMax = new Rectangle(LW - 39, 5, 16, 14);
        static readonly Rectangle RMin = new Rectangle(LW - 55, 5, 16, 14);
        static readonly Rectangle RVis = new Rectangle(8, 44, LW - 16, 72);
        static readonly Rectangle RVisIn = new Rectangle(10, 46, LW - 20, 68);
        static readonly Rectangle RPrev = new Rectangle(8, 122, 25, 22);
        static readonly Rectangle RPlay = new Rectangle(35, 122, 25, 22);
        static readonly Rectangle RPause = new Rectangle(62, 122, 25, 22);
        static readonly Rectangle RNext = new Rectangle(89, 122, 25, 22);
        static readonly Rectangle RSpeaker = new Rectangle(122, 124, 18, 18);
        static readonly Rectangle RSlider = new Rectangle(144, 123, LW - 190, 20);
        static readonly Rectangle RVolText = new Rectangle(LW - 44, 122, 36, 22);
        static readonly Rectangle RArtistLbl = new Rectangle(8, 150, 46, 21);
        static readonly Rectangle RArtist = new Rectangle(54, 150, LW - 62, 21);
        static readonly Rectangle RTrackLbl = new Rectangle(8, 176, 46, 21);
        static readonly Rectangle RTrack = new Rectangle(54, 176, LW - 62, 21);
        static readonly Rectangle RStat1 = new Rectangle(4, 206, 112, 19);
        static readonly Rectangle RStat2 = new Rectangle(118, 206, 104, 19);
        static readonly Rectangle RStat3 = new Rectangle(224, 206, LW - 228, 19);
        static readonly string[] MenuNames = { "&Disc", "&View", "&Options", "&Help" };
        readonly Rectangle[] rMenu = new Rectangle[4];

        // 缩放：整数倍直接最近邻放大；非整数倍先最近邻放大到 bigK 倍（big），再平滑缩到目标大小
        float zoom = 2f;
        Bitmap big; Graphics bg; int bigK;
        static readonly float[] ZoomPresets = { 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f };
        bool resizing;
        NotifyIcon tray;
        Bitmap canvas;
        Graphics cg;
        IntPtr fNormal, fBold;
        readonly Dictionary<Color, SolidBrush> brushes = new Dictionary<Color, SolidBrush>();

        // 状态
        string artist = "Data or no disc loaded", title = "Please insert an audio compact disc", appName = "";
        bool playing, hasSession, active = true;
        TimeSpan posBase, duration;
        DateTimeOffset posTime = DateTimeOffset.Now;
        float volume; bool muted, volOk;
        int visMode; bool topMost;
        string sourceAumid = "";
        string stat1 = "", stat2 = "";

        Hit hover = Hit.None, pressed = Hit.None;
        int openMenu = -1;
        bool sliding;

        // 子系统
        GlobalSystemMediaTransportControlsSessionManager mgr;
        GlobalSystemMediaTransportControlsSession session;
        readonly Loopback loop = new Loopback();
        readonly Spectrum spec = new Spectrum();
        readonly VolumeCtl vol = new VolumeCtl();
        readonly float[] samples = new float[Spectrum.N];
        float waveGain = 1f;
        bool visIdle;
        Timer timer;
        int tick, pollSoon = -1;
        readonly bool preview;

        public PlayerForm(bool preview)
        {
            this.preview = preview;
            fNormal = Native.CreateFontW(-11, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 3, 0, "MS Sans Serif");
            fBold = Native.CreateFontW(-11, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 3, 0, "MS Sans Serif");
            canvas = new Bitmap(LW, LH, PixelFormat.Format24bppRgb);
            cg = Graphics.FromImage(canvas);
            int mx = 6;
            for (int i = 0; i < 4; i++)
            {
                int w = TextW(MenuNames[i], fNormal) + 12;
                rMenu[i] = new Rectangle(mx, 23, w, 18);
                mx += w;
            }
            int nb = (RVisIn.Width - 2 + 2) / 7;
            spec.Setup(nb);
            if (preview) return;

            Text = "CD Player";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            ShowInTaskbar = false; // 只在托盘显示
            Icon smallIcon;
            LoadAppIcons(out smallIcon);
            LoadSettings();
            SetZoom(zoom);
            TopMost = topMost;
            CreateTray(smallIcon);

            vol.Refresh();
            ReadVolume();
            DrawAll();
            loop.Start();

            timer = new Timer();
            timer.Interval = 33;
            timer.Tick += OnTick;
            timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000 /*MINIMIZEBOX*/ | 0x00080000 /*SYSMENU*/;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InitMedia();
        }

        // ======================= 绘制基础 =======================
        SolidBrush B(Color c)
        {
            SolidBrush b;
            if (!brushes.TryGetValue(c, out b)) { b = new SolidBrush(c); brushes[c] = b; }
            return b;
        }
        void Fill(Color c, int x, int y, int w, int h) { cg.FillRectangle(B(c), x, y, w, h); }
        void Fill(Color c, Rectangle r) { cg.FillRectangle(B(c), r); }
        void HL(Color c, int x, int y, int w) { cg.FillRectangle(B(c), x, y, w, 1); }
        void VL(Color c, int x, int y, int h) { cg.FillRectangle(B(c), x, y, 1, h); }

        void Bevel(Rectangle r, Color tlOut, Color tlIn, Color brIn, Color brOut)
        {
            HL(tlOut, r.X, r.Y, r.Width - 1); VL(tlOut, r.X, r.Y, r.Height - 1);
            HL(brOut, r.X, r.Bottom - 1, r.Width); VL(brOut, r.Right - 1, r.Y, r.Height);
            if (tlIn.IsEmpty) return;
            HL(tlIn, r.X + 1, r.Y + 1, r.Width - 3); VL(tlIn, r.X + 1, r.Y + 1, r.Height - 3);
            HL(brIn, r.X + 1, r.Bottom - 2, r.Width - 2); VL(brIn, r.Right - 2, r.Y + 1, r.Height - 2);
        }
        void Raised(Rectangle r) { Bevel(r, C98.White, C98.Light, C98.Shadow, C98.Dark); }
        void Sunken(Rectangle r) { Bevel(r, C98.Shadow, C98.Dark, C98.Light, C98.White); }
        void PressedBtn(Rectangle r) { Bevel(r, C98.Dark, C98.Shadow, C98.Light, C98.White); }
        void Thin(Rectangle r, bool raised)
        {
            Bevel(r, raised ? C98.White : C98.Shadow, Color.Empty, Color.Empty, raised ? C98.Shadow : C98.White);
        }

        const uint DT_CENTER = 1, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;

        void Txt(string s, IntPtr font, Rectangle r, Color c, uint fmt)
        {
            IntPtr hdc = cg.GetHdc();
            try
            {
                IntPtr old = Native.SelectObject(hdc, font);
                Native.SetBkMode(hdc, 1);
                Native.SetTextColor(hdc, ColorTranslator.ToWin32(c));
                var rc = new Native.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
                Native.DrawTextW(hdc, s, s.Length, ref rc, fmt | DT_SINGLELINE);
                Native.SelectObject(hdc, old);
            }
            finally { cg.ReleaseHdc(hdc); }
        }

        int TextW(string s, IntPtr font)
        {
            IntPtr hdc = cg.GetHdc();
            try
            {
                IntPtr old = Native.SelectObject(hdc, font);
                var rc = new Native.RECT();
                Native.DrawTextW(hdc, s, s.Length, ref rc, DT_CALCRECT | DT_SINGLELINE);
                Native.SelectObject(hdc, old);
                return rc.Right - rc.Left;
            }
            finally { cg.ReleaseHdc(hdc); }
        }

        // 像素三角形
        void TriRight(Color c, int x, int y, int h) { for (int i = 0; i <= h / 2; i++) VL(c, x + i, y + i, h - 2 * i); }
        void TriLeft(Color c, int x, int y, int h) { int w = h / 2; for (int i = 0; i <= w; i++) VL(c, x + w - i, y + i, h - 2 * i); }
        void TriDown(Color c, int x, int y, int w) { for (int i = 0; i <= w / 2; i++) HL(c, x + i, y + i, w - 2 * i); }

        // ======================= 整体绘制 =======================
        // 关闭流程中 / 释放后不再绘制（失焦、菜单关闭等通知可能在窗口销毁时才到达）
        bool Dead { get { return closing || cg == null || IsDisposed; } }
        bool closing;

        void DrawAll()
        {
            if (Dead) return;
            Fill(C98.Face, 0, 0, LW, LH);
            Bevel(new Rectangle(0, 0, LW, LH), C98.Light, C98.White, C98.Shadow, C98.Dark);
            DrawTitle();
            DrawMenuBar();
            Sunken(RVis);
            DrawVis();
            DrawTransport();
            DrawCombo(RArtistLbl, "&Artist:", RArtist, artist, Hit.ArtistDrop);
            DrawCombo(RTrackLbl, "Trac&k:", RTrack, title, Hit.TrackDrop);
            DrawStatus();
        }

        void DrawTitle()
        {
            Color a = active ? C98.Navy : C98.InactA, b = active ? C98.TitleB : C98.InactB;
            using (var lg = new LinearGradientBrush(new Rectangle(RTitle.X - 1, 0, RTitle.Width + 2, 1), a, b, 0f))
                cg.FillRectangle(lg, RTitle);
            DrawCdIcon(5, 4);
            Txt("CD Player", fBold, new Rectangle(24, RTitle.Y, 200, RTitle.Height), active ? C98.White : C98.Light, DT_VCENTER | DT_NOPREFIX);
            DrawCaptionBtn(RMin, Hit.Min);
            DrawCaptionBtn(RMax, Hit.Max);
            DrawCaptionBtn(RClose, Hit.Close);
        }

        Bitmap titleIcon;
        void DrawCdIcon(int x, int y)
        {
            if (titleIcon == null) titleIcon = CdIconArt.Draw(16);
            cg.DrawImage(titleIcon, x, y, 16, 16);
        }

        void DrawCaptionBtn(Rectangle r, Hit h)
        {
            Fill(C98.Face, r);
            bool down = pressed == h && hover == h;
            if (down) PressedBtn(r); else Raised(r);
            int o = down ? 1 : 0;
            if (h == Hit.Close)
            {
                int x = r.X + 4 + o, y = r.Y + 3 + o;
                for (int i = 0; i < 7; i++) { HL(C98.Dark, x + i, y + i, 2); HL(C98.Dark, x + 6 - i, y + i, 2); }
            }
            else if (h == Hit.Min)
            {
                HL(C98.Dark, r.X + 4 + o, r.Y + 9 + o, 6); HL(C98.Dark, r.X + 4 + o, r.Y + 10 + o, 6);
            }
            else // 最大化：固定大小窗口，灰掉
            {
                int x = r.X + 3, y = r.Y + 2;
                foreach (var pair in new[] { new[] { 1, C98.White.ToArgb() }, new[] { 0, C98.Shadow.ToArgb() } })
                {
                    Color c = Color.FromArgb(pair[1]); int d = pair[0];
                    HL(c, x + d, y + d, 9); HL(c, x + d, y + 1 + d, 9); HL(c, x + d, y + 8 + d, 9);
                    VL(c, x + d, y + d, 9); VL(c, x + 8 + d, y + d, 9);
                }
            }
        }

        void DrawMenuBar()
        {
            Fill(C98.Face, 3, 22, LW - 6, 20);
            for (int i = 0; i < 4; i++)
            {
                Rectangle r = rMenu[i];
                if (openMenu == i) Thin(r, false);
                else if (hover == Hit.Menu0 + i && openMenu < 0) Thin(r, true);
                int o = openMenu == i ? 1 : 0;
                Txt(MenuNames[i], fNormal, new Rectangle(r.X + o, r.Y + o, r.Width, r.Height), C98.Dark, DT_CENTER | DT_VCENTER);
            }
            // 菜单栏下的蚀刻分隔线
            HL(C98.Shadow, 3, 41, LW - 6); HL(C98.White, 3, 42, LW - 6);
        }

        void DrawTransport()
        {
            Fill(C98.Face, 4, 118, LW - 8, 28);
            DrawButton(RPrev, Hit.Prev, false);
            DrawButton(RPlay, Hit.Play, playing);
            DrawButton(RPause, Hit.Pause, hasSession && !playing);
            DrawButton(RNext, Hit.Next, false);
            DrawSpeaker();
            DrawSlider();
            Txt(volOk ? ((int)Math.Round(volume * 100)) + "%" : "--", fNormal, RVolText, C98.Dark, DT_VCENTER | DT_CENTER | DT_NOPREFIX);
        }

        void DrawButton(Rectangle r, Hit h, bool latched)
        {
            Fill(C98.Face, r);
            bool down = (pressed == h && hover == h) || latched;
            if (down) PressedBtn(r); else Raised(r);
            if (latched && !(pressed == h && hover == h))
            {
                // 按下锁定态：Win98 的点状背景
                for (int y = r.Y + 2; y < r.Bottom - 2; y++)
                    for (int x = r.X + 2 + (y & 1); x < r.Right - 2; x += 2) Fill(C98.White, x, y, 1, 1);
            }
            int o = down ? 1 : 0;
            int cx = r.X + r.Width / 2 + o, cy = r.Y + r.Height / 2 + o;
            Color k = C98.Dark;
            switch (h)
            {
                case Hit.Prev:
                    Fill(k, cx - 6, cy - 3, 2, 7); TriLeft(k, cx - 4, cy - 3, 7); TriLeft(k, cx, cy - 3, 7); break;
                case Hit.Next:
                    TriRight(k, cx - 5, cy - 3, 7); TriRight(k, cx - 1, cy - 3, 7); Fill(k, cx + 3, cy - 3, 2, 7); break;
                case Hit.Play:
                    TriRight(k, cx - 2, cy - 4, 9); break;
                case Hit.Pause:
                    Fill(k, cx - 4, cy - 4, 3, 9); Fill(k, cx + 1, cy - 4, 3, 9); break;
            }
        }

        void DrawSpeaker()
        {
            Rectangle r = RSpeaker;
            Fill(C98.Face, r);
            if (pressed == Hit.Speaker && hover == Hit.Speaker) Thin(r, false);
            else if (hover == Hit.Speaker) Thin(r, true);
            int x = r.X + 3, y = r.Y + 4;
            Fill(C98.Dark, x, y + 3, 3, 4);
            for (int i = 0; i < 4; i++) VL(C98.Dark, x + 3 + i, y + 3 - i, 4 + 2 * i);
            if (muted || !volOk)
            {
                Color red = Color.FromArgb(200, 0, 0);
                for (int i = 0; i < 5; i++) { Fill(red, x + 8 + i, y + 3 + i, 1, 1); Fill(red, x + 12 - i, y + 3 + i, 1, 1); }
            }
            else
            {
                VL(C98.Dark, x + 9, y + 3, 4);
                VL(C98.Dark, x + 11, y + 1, 8);
            }
        }

        void DrawSlider()
        {
            Rectangle r = RSlider;
            Fill(C98.Face, r);
            int cy = r.Y + r.Height / 2;
            Bevel(new Rectangle(r.X, cy - 2, r.Width, 4), C98.Shadow, C98.Dark, C98.Light, C98.White);
            int tx = ThumbX();
            var t = new Rectangle(tx - 5, r.Y, 11, r.Height);
            Fill(C98.Face, t);
            Raised(t);
            // 刻度
            for (int i = 0; i <= 10; i++) VL(C98.Dark, r.X + 5 + i * (r.Width - 11) / 10, r.Bottom, 2);
        }

        int ThumbX() { return RSlider.X + 5 + (int)Math.Round((volOk ? volume : 0) * (RSlider.Width - 11)); }

        void DrawCombo(Rectangle lbl, string label, Rectangle r, string text, Hit drop)
        {
            Fill(C98.Face, lbl);
            Txt(label, fNormal, lbl, C98.Dark, DT_VCENTER);
            Fill(C98.White, r);
            Sunken(r);
            var btn = new Rectangle(r.Right - 18, r.Y + 2, 16, r.Height - 4);
            Fill(C98.Face, btn);
            bool down = pressed == drop && hover == drop;
            if (down) PressedBtn(btn); else Raised(btn);
            TriDown(C98.Dark, btn.X + 4 + (down ? 1 : 0), btn.Y + 6 + (down ? 1 : 0), 7);
            Txt(text, fNormal, new Rectangle(r.X + 4, r.Y + 2, r.Width - 26, r.Height - 4), C98.Dark, DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
        }

        void DrawStatus()
        {
            Fill(C98.Face, 3, RStat1.Y - 1, LW - 6, RStat1.Height + 2);
            foreach (Rectangle r in new[] { RStat1, RStat2, RStat3 }) Thin(r, false);
            Txt(stat1, fNormal, new Rectangle(RStat1.X + 4, RStat1.Y, RStat1.Width - 6, RStat1.Height), C98.Dark, DT_VCENTER | DT_NOPREFIX);
            Txt(stat2, fNormal, new Rectangle(RStat2.X + 4, RStat2.Y, RStat2.Width - 6, RStat2.Height), C98.Dark, DT_VCENTER | DT_NOPREFIX);
            Txt(appName, fNormal, new Rectangle(RStat3.X + 4, RStat3.Y, RStat3.Width - 18, RStat3.Height), C98.Dark, DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            // 右下角缩放手柄纹理
            int gx = LW - 15, gy = LH - 15;
            for (int i = 0; i < 3; i++)
            {
                int d = i * 4;
                for (int j = 0; j < 10 - d; j++) { Fill(C98.White, gx + d + j + 1, gy + 10 - j - 1, 1, 1); Fill(C98.Shadow, gx + d + j + 2, gy + 10 - j - 1, 1, 1); Fill(C98.Shadow, gx + d + j + 3, gy + 10 - j - 1, 1, 1); }
            }
        }

        // ======================= 可视化 =======================
        void DrawVis()
        {
            Rectangle v = RVisIn;
            Fill(Color.Black, v);
            if (visMode == 0) DrawBars(v); else DrawWave(v);
        }

        void DrawBars(Rectangle v)
        {
            const int bw = 5, gap = 2, segH = 2, segGap = 1;
            int nb = spec.Bars.Length;
            int totalW = nb * (bw + gap) - gap;
            int x0 = v.X + (v.Width - totalW) / 2;
            int nseg = (v.Height - 2 + segGap) / (segH + segGap);
            int totalH = nseg * (segH + segGap) - segGap;
            int yb = v.Y + (v.Height - totalH) / 2 + totalH; // 底边
            for (int i = 0; i < nb; i++)
            {
                int x = x0 + i * (bw + gap);
                int lit = (int)Math.Round(spec.Bars[i] * nseg);
                int litPx = lit * (segH + segGap);
                Fill(C98.LedOff, x, yb - totalH, bw, totalH - litPx);
                if (lit > 0) Fill(C98.Led, x, yb - litPx, bw, litPx);
                int p = (int)Math.Round(spec.Peaks[i] * nseg);
                if (p > lit && p > 0)
                    Fill(C98.LedPeak, x, yb - p * (segH + segGap), bw, segH);
            }
            // 段与段之间的黑缝
            for (int s = 1; s <= nseg; s++)
                HL(Color.Black, x0, yb - s * (segH + segGap) + segH, totalW);
        }

        void DrawWave(Rectangle v)
        {
            Color grid = Color.FromArgb(0, 50, 0);
            for (int gx = v.X + v.Width / 8; gx < v.Right; gx += v.Width / 8)
                for (int gy = v.Y + 1; gy < v.Bottom; gy += 3) Fill(grid, gx, gy, 1, 1);
            int mid = v.Y + v.Height / 2;
            for (int gx = v.X; gx < v.Right; gx += 3) Fill(grid, gx, mid, 1, 1);

            int n = v.Width;
            int count = n * 2;
            int start = Spectrum.N - count - n;
            // 找上升过零点做触发，波形更稳定
            int trig = start;
            for (int i = start; i < start + n; i++)
                if (samples[i] <= 0 && samples[i + 1] > 0) { trig = i; break; }
            float peak = 0.02f;
            for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(samples[trig + i]));
            float target = Math.Min(8f, 0.85f / peak);
            waveGain += (target - waveGain) * (target < waveGain ? 0.5f : 0.05f);
            float half = v.Height / 2f - 2;
            int prevY = mid;
            for (int x = 0; x < n; x++)
            {
                float s = samples[trig + x * 2] * waveGain;
                int y = mid - (int)Math.Round(Math.Max(-1, Math.Min(1, s)) * half);
                int y0 = Math.Min(prevY, y), y1 = Math.Max(prevY, y);
                if (x == 0) { y0 = y1 = y; }
                VL(C98.Led, v.X + x, y0, y1 - y0 + 1);
                prevY = y;
            }
        }

        void UpdateVis()
        {
            if (Dead) return;
            bool fresh = loop.HasRecentData;
            if (fresh) loop.CopyLatest(samples);
            else Array.Clear(samples, 0, samples.Length);
            spec.Process(samples, loop.SampleRate);
            bool idle = !fresh && spec.AllZero();
            if (idle && visIdle) return; // 静音时停止重绘，省 CPU
            visIdle = idle;
            DrawVis();
            InvalidateLogical(RVisIn);
        }

        // ======================= 主循环 =======================
        void OnTick(object sender, EventArgs e)
        {
            tick++;
            if (Visible && WindowState != FormWindowState.Minimized) UpdateVis(); // 藏在托盘时不做频谱计算
            if (tick % 8 == 0) UpdateStatusText();
            if (pollSoon > 0 && --pollSoon == 0) PollMedia();
            if (tick % 30 == 0)
            {
                PollMedia();
                if (!sliding) { if (tick % 90 == 0) vol.Refresh(); ReadVolume(); }
            }
        }

        void UpdateStatusText()
        {
            if (Dead) return;
            TimeSpan p = CurrentPos();
            string s1 = hasSession && duration > TimeSpan.Zero ? "Time: " + Fmt(p) : hasSession ? "Time: --:--" : "Stopped";
            string s2 = hasSession && duration > TimeSpan.Zero ? "Track: " + Fmt(duration) : "Track: --:--";
            if (s1 != stat1 || s2 != stat2)
            {
                stat1 = s1; stat2 = s2;
                DrawStatus();
                InvalidateLogical(new Rectangle(0, RStat1.Y - 1, LW, LH - RStat1.Y + 1));
            }
        }

        TimeSpan CurrentPos()
        {
            TimeSpan p = posBase;
            if (playing)
            {
                TimeSpan d = DateTimeOffset.Now - posTime;
                if (d > TimeSpan.Zero && d < TimeSpan.FromHours(6)) p += d;
            }
            if (duration > TimeSpan.Zero && p > duration) p = duration;
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            return p;
        }

        static string Fmt(TimeSpan t)
        {
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        void ReadVolume()
        {
            if (Dead) return;
            bool ok = vol.Ok;
            float v = ok ? vol.Get() : 0; bool m = ok && vol.GetMute();
            if (ok != volOk || Math.Abs(v - volume) > 0.004f || m != muted)
            {
                volOk = ok; volume = v; muted = m;
                DrawTransport();
                InvalidateLogical(new Rectangle(0, 118, LW, 30));
            }
        }

        void SetVolume(float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            vol.Set(v);
            if (muted && v > 0) { vol.SetMute(false); }
            ReadVolume();
        }

        // ======================= 媒体会话 =======================
        void InitMedia()
        {
            try
            {
                IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> op = GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                op.Completed = (o, s) =>
                {
                    if (s != AsyncStatus.Completed) return;
                    try
                    {
                        var m = o.GetResults();
                        BeginInvoke(new Action(() => { mgr = m; PollMedia(); }));
                    }
                    catch { }
                };
            }
            catch { }
        }

        void PollMedia()
        {
            if (Dead) return;
            GlobalSystemMediaTransportControlsSession s = null;
            try
            {
                if (mgr != null)
                {
                    if (sourceAumid != "")
                        foreach (var x in mgr.GetSessions())
                            if (x.SourceAppUserModelId == sourceAumid) { s = x; break; }
                    if (s == null)
                    {
                        // 自动模式：系统“当前会话”可能停在一个暂停的视频上，优先选正在播放的
                        var cur = mgr.GetCurrentSession();
                        if (cur != null && IsPlaying(cur)) s = cur;
                        else
                        {
                            foreach (var x in mgr.GetSessions())
                                if (IsPlaying(x)) { s = x; break; }
                            if (s == null && session != null)
                                foreach (var x in mgr.GetSessions())
                                    if (x.SourceAppUserModelId == session.SourceAppUserModelId) { s = x; break; } // 暂停后不乱跳
                            if (s == null) s = cur;
                        }
                    }
                }
            }
            catch { s = null; }
            session = s;
            if (s == null)
            {
                SetMedia(false, false, "Data or no disc loaded", "Please insert an audio compact disc", "", TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.Now);
                return;
            }
            try
            {
                var pb = s.GetPlaybackInfo();
                bool pl = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var tl = s.GetTimelineProperties();
                DateTimeOffset upd = tl.LastUpdatedTime;
                if (upd.Year < 2000 || upd > DateTimeOffset.Now.AddMinutes(1)) upd = DateTimeOffset.Now;
                TimeSpan dur = tl.EndTime - tl.StartTime;
                string app = FriendlyApp(s.SourceAppUserModelId);
                SetMedia(true, pl, artist, title, app, tl.Position, dur, upd);

                var op = s.TryGetMediaPropertiesAsync();
                op.Completed = (o, st) =>
                {
                    if (st != AsyncStatus.Completed) return;
                    try
                    {
                        var p = o.GetResults();
                        string a = p.Artist, t = p.Title;
                        if (string.IsNullOrEmpty(a)) a = p.AlbumArtist;
                        BeginInvoke(new Action(() =>
                        {
                            if (session != s) return;
                            SetMedia(true, playing, string.IsNullOrEmpty(a) ? "(Unknown Artist)" : a, string.IsNullOrEmpty(t) ? "(Untitled)" : t, appName, posBase, duration, posTime);
                        }));
                    }
                    catch { }
                };
            }
            catch { }
        }

        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        void SetMedia(bool has, bool pl, string a, string t, string app, TimeSpan pos, TimeSpan dur, DateTimeOffset upd)
        {
            if (Dead) return;
            bool changed = has != hasSession || pl != playing || a != artist || t != title || app != appName;
            hasSession = has; playing = pl; artist = a; title = t; appName = app;
            posBase = pos; duration = dur; posTime = upd;
            if (changed) { Redraw(); UpdateTrayText(); }
            UpdateStatusText();
        }

        static string FriendlyApp(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";
            string s = aumid;
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            switch (s.ToLowerInvariant())
            {
                case "qqmusic": return "QQ音乐";
                case "cloudmusic": return "网易云音乐";
                case "kugou": return "酷狗音乐";
                case "msedge": return "Edge";
                case "zunemusic": return "Media Player";
            }
            return s;
        }

        void Cmd(Hit h)
        {
            var s = session;
            bool done = false;
            if (s != null)
            {
                try
                {
                    switch (h)
                    {
                        case Hit.Prev: s.TrySkipPreviousAsync(); break;
                        case Hit.Next: s.TrySkipNextAsync(); break;
                        case Hit.Play: s.TryPlayAsync(); break;
                        case Hit.Pause: s.TryPauseAsync(); break;
                        default: s.TryTogglePlayPauseAsync(); break;
                    }
                    done = true;
                }
                catch { }
            }
            if (!done)
            {
                byte vk = h == Hit.Prev ? (byte)0xB1 : h == Hit.Next ? (byte)0xB0 : (byte)0xB3;
                Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
                Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
            }
            pollSoon = 10; // ~0.3s 后刷新
        }

        // ======================= 输入 =======================
        Point L(Point p) { return new Point((int)Math.Floor(p.X / zoom), (int)Math.Floor(p.Y / zoom)); }

        static readonly Rectangle RGrip = new Rectangle(LW - 17, LH - 17, 15, 15);

        Hit HitTest(Point p)
        {
            if (RGrip.Contains(p)) return Hit.Grip;
            if (RClose.Contains(p)) return Hit.Close;
            if (RMax.Contains(p)) return Hit.Max;
            if (RMin.Contains(p)) return Hit.Min;
            if (RTitle.Contains(p)) return Hit.Title;
            for (int i = 0; i < 4; i++) if (rMenu[i].Contains(p)) return Hit.Menu0 + i;
            if (RPrev.Contains(p)) return Hit.Prev;
            if (RPlay.Contains(p)) return Hit.Play;
            if (RPause.Contains(p)) return Hit.Pause;
            if (RNext.Contains(p)) return Hit.Next;
            if (RSpeaker.Contains(p)) return Hit.Speaker;
            if (new Rectangle(RSlider.X - 4, RSlider.Y, RSlider.Width + 8, RSlider.Height + 3).Contains(p)) return Hit.Slider;
            if (new Rectangle(RArtist.Right - 20, RArtist.Y, 20, RArtist.Height).Contains(p)) return Hit.ArtistDrop;
            if (new Rectangle(RTrack.Right - 20, RTrack.Y, 20, RTrack.Height).Contains(p)) return Hit.TrackDrop;
            if (RVis.Contains(p)) return Hit.Vis;
            return Hit.None;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84 /*WM_NCHITTEST*/)
            {
                int x = (short)((long)m.LParam & 0xFFFF), y = (short)(((long)m.LParam >> 16) & 0xFFFF);
                Point p = L(PointToClient(new Point(x, y)));
                if (HitTest(p) == Hit.Title) { m.Result = (IntPtr)2; return; } // HTCAPTION：拖动窗口
            }
            base.WndProc(ref m);
        }

        void SetHover(Hit h)
        {
            if (h == hover) return;
            hover = h;
            Cursor = h == Hit.Grip ? Cursors.SizeNWSE : Cursors.Default;
            Redraw();
        }

        void Redraw() { if (Dead) return; DrawAll(); InvalidateLogical(new Rectangle(0, 0, LW, LH)); }

        // 菜单点击里不要直接 Close：等菜单自己收尾完再退出
        void ExitApp() { if (!Dead) BeginInvoke(new Action(Close)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = L(e.Location);
            if (resizing)
            {
                // 拖右下角等比缩放：取横竖两个方向里更大的比例
                Point c = Cursor.Position;
                float zx = (c.X - Left + 2) / (float)LW, zy = (c.Y - Top + 2) / (float)LH;
                SetZoom(Math.Max(zx, zy));
                return;
            }
            if (sliding) { SetVolume((p.X - RSlider.X - 5) / (float)(RSlider.Width - 11)); return; }
            SetHover(HitTest(p));
        }

        protected override void OnMouseLeave(EventArgs e) { SetHover(Hit.None); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Point p = L(e.Location);
            Hit h = HitTest(p);
            hover = h;
            if (h == Hit.Grip) { resizing = true; return; }
            if (h >= Hit.Menu0 && h <= Hit.Menu3) { ShowMenu(h - Hit.Menu0); return; }
            if (h == Hit.Slider) { sliding = true; SetVolume((p.X - RSlider.X - 5) / (float)(RSlider.Width - 11)); return; }
            if (h == Hit.ArtistDrop || h == Hit.TrackDrop)
            {
                pressed = h; Redraw();
                Rectangle r = h == Hit.ArtistDrop ? RArtist : RTrack;
                ShowDrop(BuildSourceMenu(), r.X, r.Bottom);
                return;
            }
            if (h == Hit.Vis) { visMode = 1 - visMode; SaveSettings(); DrawVis(); InvalidateLogical(RVisIn); return; }
            pressed = h;
            Redraw();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (sliding) { sliding = false; return; }
            if (resizing) { resizing = false; SaveSettings(); return; }
            Hit h = HitTest(L(e.Location));
            Hit was = pressed;
            pressed = Hit.None;
            Redraw();
            if (h != was) return;
            switch (h)
            {
                case Hit.Close: ExitApp(); break;
                case Hit.Min: Hide(); break; // 最小化到托盘
                case Hit.Prev: case Hit.Next: case Hit.Play: case Hit.Pause: Cmd(h); break;
                case Hit.Speaker: vol.SetMute(!muted); ReadVolume(); break;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0) { SetZoom(zoom + 0.1f * e.Delta / 120f); SaveSettings(); return; }
            SetVolume(volume + 0.02f * e.Delta / 120f);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Space: Cmd(Hit.None); return true;
                case Keys.Left: Cmd(Hit.Prev); return true;
                case Keys.Right: Cmd(Hit.Next); return true;
                case Keys.Up: SetVolume(volume + 0.02f); return true;
                case Keys.Down: SetVolume(volume - 0.02f); return true;
                case Keys.M: vol.SetMute(!muted); ReadVolume(); return true;
                case Keys.V: visMode = 1 - visMode; SaveSettings(); DrawVis(); InvalidateLogical(RVisIn); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnActivated(EventArgs e) { active = true; Redraw(); base.OnActivated(e); }
        protected override void OnDeactivate(EventArgs e) { active = false; Redraw(); base.OnDeactivate(e); }

        // ======================= 菜单 =======================
        ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new Win98Renderer();
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            m.Font = new Font("Microsoft Sans Serif", 8.25f);
            return m;
        }

        ToolStripMenuItem Item(string text, string keys, bool check, EventHandler click)
        {
            var it = new ToolStripMenuItem(text, null, click);
            it.Checked = check;
            if (keys != null) it.ShortcutKeyDisplayString = keys;
            return it;
        }

        void ShowDrop(ContextMenuStrip m, int lx, int ly)
        {
            m.Closed += delegate
            {
                openMenu = -1; pressed = Hit.None; hover = Hit.None;
                if (Dead) return;
                Redraw();
                BeginInvoke(new Action(() => m.Dispose()));
            };
            m.Show(this, new Point((int)Math.Round(lx * zoom), (int)Math.Round(ly * zoom)));
        }

        void ShowMenu(int idx)
        {
            var m = NewMenu();
            switch (idx)
            {
                case 0:
                    m.Items.Add(Item(playing ? "&Pause" : "&Play", "Space", false, delegate { Cmd(Hit.None); }));
                    m.Items.Add(Item("Pre&vious Track", "←", false, delegate { Cmd(Hit.Prev); }));
                    m.Items.Add(Item("&Next Track", "→", false, delegate { Cmd(Hit.Next); }));
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(Item("E&xit", null, false, delegate { ExitApp(); }));
                    break;
                case 1:
                    m.Items.Add(Item("&Spectrum", null, visMode == 0, delegate { visMode = 0; SaveSettings(); Redraw(); }));
                    m.Items.Add(Item("&Waveform", null, visMode == 1, delegate { visMode = 1; SaveSettings(); Redraw(); }));
                    m.Items.Add(new ToolStripSeparator());
                    var z = new ToolStripMenuItem("&Zoom");
                    StyleSub(z);
                    foreach (float preset in ZoomPresets)
                    {
                        float zz = preset;
                        z.DropDownItems.Add(Item((int)Math.Round(zz * 100) + "%", null, Math.Abs(zoom - zz) < 0.01f, delegate { SetZoom(zz); SaveSettings(); }));
                    }
                    z.DropDownItems.Add(new ToolStripSeparator());
                    var tip = new ToolStripMenuItem("Drag corner or Ctrl+Wheel");
                    tip.Enabled = false;
                    z.DropDownItems.Add(tip);
                    m.Items.Add(z);
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(Item("Always on &Top", null, topMost, delegate { topMost = !topMost; TopMost = topMost; SaveSettings(); }));
                    break;
                case 2:
                    var src = new ToolStripMenuItem("Media &Source");
                    StyleSub(src);
                    foreach (ToolStripItem it in BuildSourceItems()) src.DropDownItems.Add(it);
                    m.Items.Add(src);
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(Item("&Mute", "M", muted, delegate { vol.SetMute(!muted); ReadVolume(); }));
                    m.Items.Add(Item("&Volume Control...", null, false, delegate { try { System.Diagnostics.Process.Start("sndvol.exe"); } catch { } }));
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(Item("Start with &Windows", null, AutoStart.Enabled, delegate { AutoStart.Enabled = !AutoStart.Enabled; }));
                    break;
                case 3:
                    m.Items.Add(Item("&About CD Player", null, false, delegate { ShowAbout(); }));
                    break;
            }
            openMenu = idx;
            Redraw();
            ShowDrop(m, rMenu[idx].X, rMenu[idx].Bottom);
        }

        void StyleSub(ToolStripMenuItem it)
        {
            var dd = (ToolStripDropDownMenu)it.DropDown;
            dd.Renderer = new Win98Renderer();
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = true;
        }

        List<ToolStripItem> BuildSourceItems()
        {
            var list = new List<ToolStripItem>();
            list.Add(Item("&Auto (current system media)", null, sourceAumid == "", delegate { sourceAumid = ""; SaveSettings(); PollMedia(); }));
            if (mgr != null)
            {
                try
                {
                    var seen = new HashSet<string>();
                    foreach (var s in mgr.GetSessions())
                    {
                        string id = s.SourceAppUserModelId;
                        if (!seen.Add(id)) continue;
                        list.Add(Item(FriendlyApp(id) + "   (" + id + ")", null, id == sourceAumid, delegate { sourceAumid = id; SaveSettings(); PollMedia(); }));
                    }
                }
                catch { }
            }
            if (list.Count == 1) { var none = new ToolStripMenuItem("(no media apps found)"); none.Enabled = false; list.Add(none); }
            return list;
        }

        ContextMenuStrip BuildSourceMenu()
        {
            var m = NewMenu();
            foreach (ToolStripItem it in BuildSourceItems()) m.Items.Add(it);
            return m;
        }

        void ShowAbout()
        {
            MessageBox.Show(this,
                "CD Player 98  v1.0.0\r\n\r\n" +
                "Shows what's playing on your PC, Windows 98 style.\r\n\r\n" +
                "Space\tPlay / Pause\r\n" +
                "← →\tPrevious / Next\r\n" +
                "↑ ↓\tVolume\r\n" +
                "M\tMute\r\n" +
                "V\tSpectrum / Waveform\r\n\r\n" +
                "github.com/Buck008/Win98CDPlayer",
                "About CD Player", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void SetZoom(float z)
        {
            z = Math.Max(1f, Math.Min(4f, z));
            if (Math.Abs(z - Math.Round(z)) < 0.05f) z = (float)Math.Round(z); // 靠近整数倍时吸附，保证像素锐利
            zoom = z;
            ClientSize = new Size((int)Math.Round(LW * zoom), (int)Math.Round(LH * zoom));
            // 非整数倍：准备一张 ceil(zoom) 倍的最近邻放大图，绘制时再平滑缩小
            if (zoom == (float)Math.Round(zoom)) { FreeBig(); }
            else
            {
                int k = (int)Math.Ceiling(zoom);
                if (big == null || bigK != k)
                {
                    FreeBig();
                    big = new Bitmap(LW * k, LH * k, PixelFormat.Format24bppRgb);
                    bg = Graphics.FromImage(big);
                    bg.InterpolationMode = InterpolationMode.NearestNeighbor;
                    bg.PixelOffsetMode = PixelOffsetMode.Half;
                    bigK = k;
                }
                SyncBig(new Rectangle(0, 0, LW, LH));
            }
            Invalidate();
        }

        void FreeBig()
        {
            if (bg != null) { bg.Dispose(); bg = null; }
            if (big != null) { big.Dispose(); big = null; }
        }

        void SyncBig(Rectangle r)
        {
            if (big == null) return;
            r.Intersect(new Rectangle(0, 0, LW, LH));
            bg.DrawImage(canvas, new Rectangle(r.X * bigK, r.Y * bigK, r.Width * bigK, r.Height * bigK), r, GraphicsUnit.Pixel);
        }

        // ======================= 输出 =======================
        void InvalidateLogical(Rectangle r)
        {
            if (preview) return;
            SyncBig(r);
            int x0 = (int)Math.Floor(r.X * zoom) - 1, y0 = (int)Math.Floor(r.Y * zoom) - 1;
            int x1 = (int)Math.Ceiling(r.Right * zoom) + 1, y1 = (int)Math.Ceiling(r.Bottom * zoom) + 1;
            Invalidate(new Rectangle(x0, y0, x1 - x0, y1 - y0));
        }

        readonly ImageAttributes clampAttr = MakeClampAttr();
        static ImageAttributes MakeClampAttr() { var a = new ImageAttributes(); a.SetWrapMode(WrapMode.TileFlipXY); return a; }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (zoom == 1f) { g.DrawImageUnscaled(canvas, 0, 0); return; }
            Rectangle c = e.ClipRectangle;
            // 覆盖重绘区域的逻辑像素范围（多留 1px 余量，边缘插值不会露馅）
            int sx = Math.Max(0, (int)Math.Floor(c.X / zoom) - 1), sy = Math.Max(0, (int)Math.Floor(c.Y / zoom) - 1);
            int ex = Math.Min(LW, (int)Math.Ceiling(c.Right / zoom) + 1), ey = Math.Min(LH, (int)Math.Ceiling(c.Bottom / zoom) + 1);
            if (ex <= sx || ey <= sy) return;
            var dst = new Rectangle((int)Math.Round(sx * zoom), (int)Math.Round(sy * zoom),
                                    (int)Math.Round(ex * zoom) - (int)Math.Round(sx * zoom), (int)Math.Round(ey * zoom) - (int)Math.Round(sy * zoom));
            if (big == null)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(canvas, dst, sx, sy, ex - sx, ey - sy, GraphicsUnit.Pixel);
            }
            else
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(big, dst, sx * bigK, sy * bigK, (ex - sx) * bigK, (ey - sy) * bigK, GraphicsUnit.Pixel, clampAttr);
            }
        }

        // ======================= 托盘 =======================
        void LoadAppIcons(out Icon small)
        {
            small = null;
            try
            {
                IntPtr[] large = new IntPtr[1], sm = new IntPtr[1];
                if (Native.ExtractIconEx(Application.ExecutablePath, 0, large, sm, 1) > 0)
                {
                    if (large[0] != IntPtr.Zero) Icon = Icon.FromHandle(large[0]);
                    if (sm[0] != IntPtr.Zero) small = Icon.FromHandle(sm[0]);
                }
            }
            catch { }
            if (small == null) { Icon = MakeIcon(); small = Icon; }
        }

        void CreateTray(Icon icon)
        {
            tray = new NotifyIcon();
            tray.Icon = icon;
            tray.Text = "CD Player";
            var m = NewMenu();
            m.Opening += delegate
            {
                m.Items.Clear();
                var show = Item(Visible ? "&Hide CD Player" : "&Show CD Player", null, false, delegate { ToggleWindow(); });
                show.Font = new Font(show.Font, FontStyle.Bold);
                m.Items.Add(show);
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Item(playing ? "&Pause" : "&Play", null, false, delegate { Cmd(Hit.None); }));
                m.Items.Add(Item("Pre&vious Track", null, false, delegate { Cmd(Hit.Prev); }));
                m.Items.Add(Item("&Next Track", null, false, delegate { Cmd(Hit.Next); }));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Item("Start with &Windows", null, AutoStart.Enabled, delegate { AutoStart.Enabled = !AutoStart.Enabled; }));
                m.Items.Add(Item("&About CD Player", null, false, delegate { ShowWindow(); ShowAbout(); }));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Item("E&xit", null, false, delegate { ExitApp(); }));
            };
            m.Items.Add(new ToolStripMenuItem("...")); // 占位，保证 Opening 会触发
            tray.ContextMenuStrip = m;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
            tray.Visible = true;
        }

        void ToggleWindow()
        {
            if (Visible && WindowState != FormWindowState.Minimized) Hide();
            else ShowWindow();
        }

        void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void UpdateTrayText()
        {
            if (tray == null) return;
            string s = hasSession ? title + " - " + artist : "CD Player";
            if (s.Length > 63) s = s.Substring(0, 62) + "…"; // NotifyIcon 文本上限 63 字符
            tray.Text = s;
        }

        Icon MakeIcon()
        {
            using (Bitmap bmp = CdIconArt.Draw(32))
                return Icon.FromHandle(bmp.GetHicon());
        }

        public void Snapshot(string path)
        {
            artist = "ヲタみん (KUMI)"; title = "rain stops,good-bye"; appName = "QQ音乐";
            hasSession = true; playing = true; volOk = true; volume = 0.35f;
            stat1 = "Time: 1:14"; stat2 = "Track: 5:35";
            var r = new Random(3);
            for (int i = 0; i < spec.Bars.Length; i++)
            {
                double f = i / (double)spec.Bars.Length;
                spec.Bars[i] = (float)Math.Max(0, Math.Min(1, 0.75 - 0.35 * f + 0.25 * (r.NextDouble() - 0.5)));
                spec.Peaks[i] = Math.Min(1, spec.Bars[i] + 0.08f);
            }
            DrawAll();
            using (var big = new Bitmap(LW * 2, LH * 2))
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(canvas, new Rectangle(0, 0, LW * 2, LH * 2), 0, 0, LW, LH, GraphicsUnit.Pixel);
                big.Save(path, ImageFormat.Png);
            }
            visMode = 1;
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)(0.5 * Math.Sin(i * 0.05) + 0.2 * Math.Sin(i * 0.31));
            DrawAll();
            canvas.Save(path.Replace(".png", "_wave.png"), ImageFormat.Png);
        }

        // ======================= 设置 =======================
        void LoadSettings()
        {
            int x = int.MinValue, y = int.MinValue;
            zoom = Math.Max(1f, (float)Math.Floor(Native.DpiAt(Screen.PrimaryScreen.Bounds.X + 10, Screen.PrimaryScreen.Bounds.Y + 10) + 0.5));
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\CDPlayer98"))
                {
                    int pct = (int)k.GetValue("ZoomPct", (int)k.GetValue("Zoom", (int)zoom) * 100);
                    zoom = Math.Max(1f, Math.Min(4f, pct / 100f));
                    visMode = (int)k.GetValue("Mode", 0) == 1 ? 1 : 0;
                    topMost = (int)k.GetValue("TopMost", 0) != 0;
                    sourceAumid = (k.GetValue("Source", "") as string) ?? "";
                    x = (int)k.GetValue("X", int.MinValue);
                    y = (int)k.GetValue("Y", int.MinValue);
                }
            }
            catch { }
            Rectangle want = new Rectangle(x, y, (int)(LW * zoom), (int)(LH * zoom));
            bool onScreen = false;
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.Contains(new Point(want.X + 40, want.Y + 10))) onScreen = true;
            if (!onScreen)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                want.X = wa.X + (wa.Width - want.Width) / 2;
                want.Y = wa.Y + (wa.Height - want.Height) / 2;
            }
            Location = want.Location;
        }

        void SaveSettings()
        {
            if (preview) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\CDPlayer98"))
                {
                    k.SetValue("ZoomPct", (int)Math.Round(zoom * 100));
                    k.DeleteValue("Zoom", false);
                    k.SetValue("Mode", visMode);
                    k.SetValue("TopMost", topMost ? 1 : 0);
                    k.SetValue("Source", sourceAumid);
                    if (WindowState == FormWindowState.Normal) { k.SetValue("X", Location.X); k.SetValue("Y", Location.Y); }
                }
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            SaveSettings();
            loop.Stop();
            if (timer != null) timer.Stop();
            if (tray != null) tray.Visible = false; // 真正释放放到 Dispose 里
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (cg != null) { cg.Dispose(); cg = null; }
                if (canvas != null) { canvas.Dispose(); canvas = null; }
                FreeBig();
                if (tray != null) { tray.Dispose(); tray = null; }
                if (titleIcon != null) { titleIcon.Dispose(); titleIcon = null; }
                foreach (var b in brushes.Values) b.Dispose();
                brushes.Clear();
            }
            if (fNormal != IntPtr.Zero) { Native.DeleteObject(fNormal); fNormal = IntPtr.Zero; }
            if (fBold != IntPtr.Zero) { Native.DeleteObject(fBold); fBold = IntPtr.Zero; }
            base.Dispose(disposing);
        }
    }

    // ======================= 98 风格像素图标 =======================
    // 只用 VGA 16 色，1px 黑描边，反光用棋盘格抖动；小尺寸逐像素生成，大尺寸整数倍放大保持颗粒感
    static class CdIconArt
    {
        static readonly Color K = Color.Black, Dk = Color.FromArgb(128, 128, 128), Si = Color.FromArgb(192, 192, 192),
            Wh = Color.White, Cy = Color.FromArgb(0, 255, 255), Mg = Color.FromArgb(255, 0, 255), Ye = Color.FromArgb(255, 255, 0);

        static bool In(double a, double lo, double hi) { return a >= lo && a < hi; }

        public static Bitmap Draw(int s)
        {
            var b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            double c = (s - 1) / 2.0, outer = s / 2.0 - 0.25;
            double hole = Math.Max(1.2, s * 0.085), hub = s * 0.25;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    double dx = x - c, dy = y - c, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > outer || d < hole) continue;               // 外部和中心孔透明
                    bool chk = ((x + y) & 1) == 0;
                    double a = Math.Atan2(dy, dx) * 180 / Math.PI; if (a < 0) a += 360; // 0=右, 90=下
                    Color col;
                    if (d > outer - 1 || d < hole + 1) col = K;        // 外圈、孔边描边
                    else if (d < hub) col = d > hub - 1.2 ? Dk : Si;   // 中心透明塑料圈
                    else if (d > outer - 2 && In(a, 0, 100)) col = Dk; // 右下边缘暗部
                    else if (In(a, 205, 245)) col = Wh;                // 左上高光
                    else if (In(a, 190, 205) || In(a, 245, 260)) col = chk ? Wh : Si;
                    else if (In(a, 290, 335)) col = chk ? Cy : Si;     // 右上青色反光
                    else if (In(a, 110, 150)) col = chk ? Mg : Si;     // 左下洋红反光
                    else if (In(a, 150, 168)) col = chk ? Ye : Si;
                    else if (In(a, 20, 80)) col = chk ? Dk : Si;       // 右下阴影
                    else col = Si;
                    b.SetPixel(x, y, col);
                }
            return b;
        }

        static Bitmap Upscale(Bitmap src, int k)
        {
            var b = new Bitmap(src.Width * k, src.Height * k, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, b.Width, b.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            return b;
        }

        static byte[] Png(Bitmap b)
        {
            using (var ms = new System.IO.MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        public static void Export(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            var images = new List<Bitmap>();
            using (Bitmap b32 = Draw(32))
            {
                images.Add(Draw(16)); images.Add(Draw(24)); images.Add(Draw(32)); images.Add(Draw(48));
                images.Add(Upscale(b32, 2)); images.Add(Upscale(b32, 8)); // 64, 256
                using (Bitmap readme = Upscale(b32, 4)) readme.Save(System.IO.Path.Combine(dir, "icon.png"), ImageFormat.Png);
            }
            // ICO：文件头 + 目录 + PNG 数据（Vista 起支持 PNG 压缩条目）
            var pngs = new List<byte[]>();
            foreach (Bitmap b in images) pngs.Add(Png(b));
            using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "CDPlayer.ico")))
            using (var w = new System.IO.BinaryWriter(fs))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
                int off = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    int s = images[i].Width;
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
                    w.Write(pngs[i].Length); w.Write(off);
                    off += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
            foreach (Bitmap b in images) b.Dispose();
        }
    }

    // ======================= 开机自启（HKCU\...\Run，默认关闭） =======================
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "CDPlayer98";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        string v = k == null ? null : k.GetValue(Name) as string;
                        return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                        else k.DeleteValue(Name, false);
                    }
                }
                catch { }
            }
        }
    }

    // ======================= Win98 风格菜单 =======================
    class Win98Renderer : ToolStripRenderer
    {
        static void Px(Graphics g, Color c, int x, int y, int w, int h) { using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, h); }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(C98.Face); }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Graphics g = e.Graphics; Size s = e.ToolStrip.Size;
            Px(g, C98.Light, 0, 0, s.Width - 1, 1); Px(g, C98.Light, 0, 0, 1, s.Height - 1);
            Px(g, C98.White, 1, 1, s.Width - 3, 1); Px(g, C98.White, 1, 1, 1, s.Height - 3);
            Px(g, C98.Shadow, 1, s.Height - 2, s.Width - 2, 1); Px(g, C98.Shadow, s.Width - 2, 1, 1, s.Height - 2);
            Px(g, C98.Dark, 0, s.Height - 1, s.Width, 1); Px(g, C98.Dark, s.Width - 1, 0, 1, s.Height);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
                Px(e.Graphics, C98.Navy, 0, 0, e.Item.Width, e.Item.Height);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
            {
                Rectangle r = e.TextRectangle; r.Offset(1, 1);
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, C98.White, e.TextFormat);
                e.TextColor = C98.Shadow;
            }
            else e.TextColor = e.Item.Selected ? C98.White : C98.Dark;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2 - 1, w = e.Item.Width;
            Px(e.Graphics, C98.Shadow, 1, y, w - 2, 1);
            Px(e.Graphics, C98.White, 1, y + 1, w - 2, 1);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            Color c = e.Item.Selected ? C98.White : C98.Dark;
            int u = Math.Max(1, r.Height / 14);
            int x = r.X + (r.Width - 7 * u) / 2, y = r.Y + (r.Height - 7 * u) / 2;
            // 7x7 像素对勾
            int[] ys = { 2, 3, 4, 3, 2, 1, 0 };
            for (int i = 0; i < 7; i++) Px(e.Graphics, c, x + i * u, y + ys[i] * u, u, 3 * u);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && e.Item.Selected ? C98.White : C98.Dark;
            base.OnRenderArrow(e);
        }
    }

    // ======================= 频谱分析 =======================
    class Spectrum
    {
        public const int N = 2048;
        readonly float[] win = new float[N], re = new float[N], im = new float[N], cosT = new float[N / 2], sinT = new float[N / 2];
        readonly int[] rev = new int[N];
        public float[] Bars = new float[0], Peaks = new float[0];
        int[] hold = new int[0];

        public Spectrum()
        {
            int bits = 0; while ((1 << bits) < N) bits++;
            for (int i = 0; i < N; i++)
            {
                win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)));
                int r = 0; for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                rev[i] = r;
            }
            for (int k = 0; k < N / 2; k++) { cosT[k] = (float)Math.Cos(2 * Math.PI * k / N); sinT[k] = (float)Math.Sin(2 * Math.PI * k / N); }
        }

        public void Setup(int nb) { Bars = new float[nb]; Peaks = new float[nb]; hold = new int[nb]; }

        public bool AllZero()
        {
            for (int i = 0; i < Bars.Length; i++) if (Bars[i] > 0 || Peaks[i] > 0) return false;
            return true;
        }

        public void Process(float[] x, int rate)
        {
            for (int i = 0; i < N; i++) { re[rev[i]] = x[i] * win[i]; im[rev[i]] = 0; }
            for (int size = 2; size <= N; size <<= 1)
            {
                int half = size >> 1, step = N / size;
                for (int i = 0; i < N; i += size)
                    for (int j = 0; j < half; j++)
                    {
                        int a = i + j, b = a + half, k = j * step;
                        float c = cosT[k], s = sinT[k];
                        float tr = re[b] * c + im[b] * s, ti = im[b] * c - re[b] * s;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
            }
            int nb = Bars.Length;
            const double fmin = 40, fmax = 16000;
            for (int bi = 0; bi < nb; bi++)
            {
                double f0 = fmin * Math.Pow(fmax / fmin, bi / (double)nb), f1 = fmin * Math.Pow(fmax / fmin, (bi + 1) / (double)nb);
                int k0 = Math.Max(1, (int)(f0 * N / rate)), k1 = Math.Min(N / 2, Math.Max(k0 + 1, (int)Math.Ceiling(f1 * N / rate)));
                float m = 0;
                for (int k = k0; k < k1; k++) m = Math.Max(m, re[k] * re[k] + im[k] * im[k]);
                double amp = Math.Sqrt(m) / (N / 4.0);
                double fc = Math.Sqrt(f0 * f1);
                double db = 20 * Math.Log10(amp + 1e-9) + 3.0 * Math.Log(fc / 1000.0, 2); // 粉噪倾斜补偿
                float v = (float)Math.Max(0, Math.Min(1, (db + 62) / 58));
                if (v > Bars[bi]) Bars[bi] = Bars[bi] * 0.3f + v * 0.7f;
                else Bars[bi] = Math.Max(v, Bars[bi] - 0.04f);
                if (Bars[bi] < 0.01f) Bars[bi] = 0;
                if (Bars[bi] >= Peaks[bi]) { Peaks[bi] = Bars[bi]; hold[bi] = 18; }
                else if (hold[bi] > 0) hold[bi]--;
                else Peaks[bi] = Math.Max(0, Peaks[bi] - 0.015f);
            }
        }
    }

    // ======================= WASAPI 回环采集 =======================
    class Loopback
    {
        const int Size = 16384, Mask = Size - 1;
        readonly float[] ring = new float[Size];
        int widx;
        volatile int lastData = Environment.TickCount - 100000;
        volatile bool running = true;
        public volatile int SampleRate = 48000;

        public bool HasRecentData { get { return Environment.TickCount - lastData < 150; } }

        public void Start()
        {
            var th = new Thread(Loop);
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
        }

        public void Stop() { running = false; }

        public void CopyLatest(float[] dst)
        {
            lock (ring)
            {
                int start = widx - dst.Length;
                for (int i = 0; i < dst.Length; i++) dst[i] = ring[(start + i) & Mask];
            }
        }

        void Loop()
        {
            while (running)
            {
                try { RunOnce(); } catch { }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(50);
            }
        }

        void RunOnce()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            IMMDevice dev = null; IAudioClient ac = null; IAudioCaptureClient cc = null;
            try
            {
                Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev));
                string id; dev.GetId(out id);
                Guid iid = typeof(IAudioClient).GUID; object o;
                Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
                ac = (IAudioClient)o;
                IntPtr fmt;
                Marshal.ThrowExceptionForHR(ac.GetMixFormat(out fmt));
                int ch = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4), bits = Marshal.ReadInt16(fmt, 14);
                int tag = (ushort)Marshal.ReadInt16(fmt, 0);
                bool isFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    var g = new byte[16];
                    Marshal.Copy(fmt + 24, g, 0, 16);
                    isFloat = new Guid(g) == new Guid("00000003-0000-0010-8000-00aa00389b71");
                }
                int hr = ac.Initialize(0, 0x00020000 /*LOOPBACK*/, 2000000, 0, fmt, IntPtr.Zero);
                Marshal.FreeCoTaskMem(fmt);
                Marshal.ThrowExceptionForHR(hr);
                Guid iidc = typeof(IAudioCaptureClient).GUID; object co;
                Marshal.ThrowExceptionForHR(ac.GetService(ref iidc, out co));
                cc = (IAudioCaptureClient)co;
                SampleRate = rate;
                Marshal.ThrowExceptionForHR(ac.Start());

                float[] fb = new float[0]; short[] sb = new short[0];
                int tick = 0;
                while (running)
                {
                    Thread.Sleep(15);
                    uint packet;
                    Marshal.ThrowExceptionForHR(cc.GetNextPacketSize(out packet));
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong p1, p2;
                        Marshal.ThrowExceptionForHR(cc.GetBuffer(out data, out frames, out flags, out p1, out p2));
                        int nf = (int)frames, n = nf * ch;
                        bool silent = (flags & 2) != 0 || data == IntPtr.Zero;
                        if (!silent && isFloat && bits == 32) { if (fb.Length < n) fb = new float[n]; Marshal.Copy(data, fb, 0, n); }
                        else if (!silent && bits == 16) { if (sb.Length < n) sb = new short[n]; Marshal.Copy(data, sb, 0, n); }
                        else silent = true;
                        lock (ring)
                        {
                            for (int f = 0; f < nf; f++)
                            {
                                float sum = 0;
                                if (!silent)
                                {
                                    if (isFloat) for (int c = 0; c < ch; c++) sum += fb[f * ch + c];
                                    else for (int c = 0; c < ch; c++) sum += sb[f * ch + c] / 32768f;
                                    sum /= ch;
                                }
                                ring[widx & Mask] = sum;
                                widx++;
                            }
                        }
                        lastData = Environment.TickCount;
                        cc.ReleaseBuffer(frames);
                        Marshal.ThrowExceptionForHR(cc.GetNextPacketSize(out packet));
                    }
                    // 默认输出设备变了（插耳机等）就重新开始采集
                    if (++tick >= 130)
                    {
                        tick = 0;
                        IMMDevice d2;
                        if (en.GetDefaultAudioEndpoint(0, 1, out d2) == 0)
                        {
                            string id2; d2.GetId(out id2);
                            Marshal.ReleaseComObject(d2);
                            if (id2 != id) break;
                        }
                    }
                }
                ac.Stop();
            }
            finally
            {
                if (cc != null) Marshal.ReleaseComObject(cc);
                if (ac != null) Marshal.ReleaseComObject(ac);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
        }
    }

    // ======================= 系统音量 =======================
    class VolumeCtl
    {
        IMMDeviceEnumerator en;
        IAudioEndpointVolume ep;
        string id;
        public bool Ok { get { return ep != null; } }

        public void Refresh()
        {
            try
            {
                if (en == null) en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDevice d;
                if (en.GetDefaultAudioEndpoint(0, 1, out d) != 0) { ep = null; return; }
                string nid; d.GetId(out nid);
                if (nid != id || ep == null)
                {
                    Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
                    if (d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0)
                    {
                        if (ep != null) Marshal.ReleaseComObject(ep);
                        ep = (IAudioEndpointVolume)o; id = nid;
                    }
                }
                Marshal.ReleaseComObject(d);
            }
            catch { ep = null; }
        }

        public float Get() { float v = 0; try { if (ep != null) ep.GetMasterVolumeLevelScalar(out v); } catch { ep = null; } return v; }
        public void Set(float v) { Guid g = Guid.Empty; try { if (ep != null) ep.SetMasterVolumeLevelScalar(v, ref g); } catch { ep = null; } }
        public bool GetMute() { bool m = false; try { if (ep != null) ep.GetMute(out m); } catch { ep = null; } return m; }
        public void SetMute(bool m) { Guid g = Guid.Empty; try { if (ep != null) ep.SetMute(m, ref g); } catch { ep = null; } }
    }

    // ======================= COM 接口 =======================
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
            uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr hdc, int color);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string s, int n, ref RECT r, uint fmt);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

        public static void EnablePerMonitorDpi()
        {
            try { if (SetProcessDpiAwarenessContext((IntPtr)(-4))) return; } catch { }
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static float DpiAt(int x, int y)
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT(x, y), 2);
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96f;
            }
            catch { }
            return 1f;
        }
    }
}
