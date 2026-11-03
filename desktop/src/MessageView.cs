using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

public class MessageView : Panel
{
    List<Msg> msgs = new List<Msg>();
    bool showTime = true;
    int totalH = 0;
    Font fBody, fBodySmall, fName, fTime;

    public MessageView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        AutoScroll = true;
    }

    void EnsureFonts()
    {
        if (fBody == null) fBody = Theme.Ui(10.2f);
        if (fBodySmall == null) fBodySmall = Theme.Ui(9.4f);
        if (fName == null) fName = Theme.Ui(9.2f, FontStyle.Bold);
        if (fTime == null) fTime = Theme.Ui(8.2f);
    }

    public void SetMessages(List<Msg> list, bool show)
    {
        msgs = list == null ? new List<Msg>() : list;
        showTime = show;
        Invalidate();
    }

    public void SetLastText(string text)
    {
        if (msgs.Count == 0) return;
        msgs[msgs.Count - 1].Text = text;
        Invalidate();
        ScrollToEnd();
    }

    public void AppendDelta(string t)
    {
        if (msgs.Count == 0) return;
        Msg m = msgs[msgs.Count - 1];
        m.Text = (m.Text == null ? "" : m.Text) + t;
        Invalidate();
        ScrollToEnd();
    }

    public void ScrollToEnd()
    {
        AutoScrollPosition = new Point(0, Math.Max(0, totalH));
    }

    static string Clean(string s)
    {
        if (s == null) return "";
        s = s.Replace("**", "");
        s = s.Replace("`", "");
        s = s.Replace("### ", "");
        s = s.Replace("## ", "");
        s = s.Replace("# ", "");
        return s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        GraphicsPath p = new GraphicsPath();
        if (rad <= 0) { p.AddRectangle(r); return p; }
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EnsureFonts();
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int y = 0;
        if (AutoScrollPosition.Y != 0) y = AutoScrollPosition.Y;
        y = -y;
        y = 20;

        if (msgs.Count == 0)
        {
            DrawHero(g);
            totalH = ClientSize.Height;
            return;
        }

        int width = ClientSize.Width;
        foreach (Msg m in msgs)
        {
            bool user = m.Role == "user";
            string text = Clean(m.Text);
            if (text.Length == 0 && !user) { y += 26; continue; }
            int maxW = (int)(width * 0.66);
            if (maxW < 240) maxW = 240;
            Size ts = TextRenderer.MeasureText(g, text.Length == 0 ? " " : text, fBody,
                new Size(maxW - 34, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int bw = Math.Min(maxW, ts.Width + 34);
            if (bw < 90) bw = 90;
            int bh = ts.Height + 24;
            int bx = user ? width - 40 - bw : 78;
            int hy = y;
            Rectangle bubble = new Rectangle(bx, hy + 24, bw, bh);

            if (user)
            {
                string nm = showTime && m.Time.Length > 0 ? "你  " + m.Time : "你";
                Size ns = TextRenderer.MeasureText(g, nm, fTime, new Size(maxW, 40), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, nm, fTime, new Rectangle(bx + bw - ns.Width, hy, ns.Width + 4, 18), Theme.Muted, TextFormatFlags.NoPadding);
            }
            else
            {
                Rectangle av = new Rectangle(40, hy + 22, 30, 30);
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, av);
                TextRenderer.DrawText(g, "AI", Theme.Ui(8.5f, FontStyle.Bold), av, Color.FromArgb(15, 23, 20),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                string nm = showTime && m.Time.Length > 0 ? "AI-ACE  " + m.Time : "AI-ACE";
                TextRenderer.DrawText(g, nm, fTime, new Rectangle(78, hy, 260, 18), Theme.Muted, TextFormatFlags.NoPadding);
            }

            Color fill = user ? Color.FromArgb(30, 58, 92) : Color.FromArgb(22, 29, 38);
            Color line = user ? Color.FromArgb(46, 88, 130) : Color.FromArgb(34, 46, 58);
            using (GraphicsPath p = Round(bubble, 12))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(line)) g.DrawPath(pen, p);
            }
            Rectangle tr = new Rectangle(bubble.X + 17, bubble.Y + 12, bubble.Width - 34, bubble.Height - 24);
            TextRenderer.DrawText(g, text, fBody, tr,
                m.Error ? Theme.Danger : (user ? Color.FromArgb(226, 238, 252) : Theme.Text),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y = bubble.Bottom + 18;
        }
        totalH = y + 10;
        if (AutoScrollMinSize.Height != totalH)
            AutoScrollMinSize = new Size(0, totalH);
    }

    void DrawHero(Graphics g)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        Rectangle logo = new Rectangle(w / 2 - 30, h / 2 - 150, 60, 60);
        using (GraphicsPath p = Round(logo, 16))
        {
            using (LinearGradientBrush b = new LinearGradientBrush(logo, Color.FromArgb(45, 212, 168), Color.FromArgb(22, 138, 173), 45f))
                g.FillPath(b, p);
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(12, 20, 18)))
            {
                using (Font f = Theme.Ui(17f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "AI", f, logo, Color.FromArgb(12, 20, 18),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        using (Font f = Theme.Ui(17f, FontStyle.Bold))
            TextRenderer.DrawText(g, "今天想让我做点什么？", f, new Rectangle(0, h / 2 - 74, w, 40), Theme.Text,
                TextFormatFlags.HorizontalCenter);
        using (Font f = Theme.Ui(9.6f))
            TextRenderer.DrawText(g, "直接提问，或勾选下方「任务模式」让我自己多步执行并产出文件", f,
                new Rectangle(0, h / 2 - 30, w, 24), Theme.Muted, TextFormatFlags.HorizontalCenter);

        string[] tips = new string[] { "读 documents 里的资料，整理成一份 Word", "把这份 Excel 按部门汇总一下", "总结这份 PDF 的五个要点" };
        int cardW = Math.Min(230, (w - 120) / 3);
        int startX = w / 2 - (cardW * 3 + 24) / 2;
        for (int i = 0; i < 3; i++)
        {
            Rectangle r = new Rectangle(startX + i * (cardW + 12), h / 2 + 16, cardW, 62);
            using (GraphicsPath p = Round(r, 10))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 27, 35))) g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(32, 43, 55))) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, tips[i], Theme.Ui(9f), new Rectangle(r.X + 14, r.Y + 10, r.Width - 28, r.Height - 20),
                Theme.Muted, TextFormatFlags.WordBreak);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}
