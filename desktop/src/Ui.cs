using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public static class Theme
{
    public static Color Bg = Color.FromArgb(15, 20, 25);
    public static Color PanelBg = Color.FromArgb(18, 24, 31);
    public static Color PanelBg2 = Color.FromArgb(23, 30, 38);
    public static Color Border = Color.FromArgb(35, 45, 56);
    public static Color Text = Color.FromArgb(226, 232, 240);
    public static Color Muted = Color.FromArgb(138, 152, 168);
    public static Color Accent = Color.FromArgb(34, 211, 166);
    public static Color AccentDark = Color.FromArgb(16, 148, 116);
    public static Color UserText = Color.FromArgb(147, 197, 253);
    public static Color AiText = Color.FromArgb(226, 232, 240);
    public static Color BtnBg = Color.FromArgb(28, 40, 52);
    public static Color BtnHover = Color.FromArgb(38, 53, 68);
    public static Color Danger = Color.FromArgb(239, 120, 110);

    public static Font Ui(float size, FontStyle style)
    {
        try { return new Font("Microsoft YaHei UI", size, style); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style); }
    }

    public static Font Ui(float size) { return Ui(size, FontStyle.Regular); }
    public static Font Mono(float size) { return new Font("Consolas", size, FontStyle.Regular); }
}

public static class Draw
{
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        GraphicsPath p = new GraphicsPath();
        if (d <= 0 || d > Math.Min(r.Width, r.Height)) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
    {
        using (GraphicsPath p = Round(r, radius))
        using (SolidBrush b = new SolidBrush(c))
            g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, Rectangle r, int radius, Color c, float w)
    {
        using (GraphicsPath p = Round(r, radius))
        using (Pen pen = new Pen(c, w))
            g.DrawPath(pen, p);
    }
}

public class FlatButton : Button
{
    public bool Primary = false;

    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.BtnBg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9f);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color bg = Primary ? Theme.Accent : (ClientRectangle.Contains(PointToClient(Cursor.Position)) ? Theme.BtnHover : BackColor);
        Color fg = Primary ? Color.FromArgb(9, 22, 20) : ForeColor;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.FillRound(e.Graphics, r, 8, bg);
        TextRenderer.DrawText(e.Graphics, Text, Font, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

public class SidebarList : ListBox
{
    public SidebarList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        ItemHeight = 56;
        IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        Conv c = Items[e.Index] as Conv;
        if (c == null) return;
        bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        Rectangle r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 3, e.Bounds.Width - 12, e.Bounds.Height - 6);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (sel) Draw.FillRound(e.Graphics, r, 8, Color.FromArgb(30, 45, 58));
        Rectangle inner = new Rectangle(r.X + 10, r.Y + 8, r.Width - 20, r.Height - 16);
        TextRenderer.DrawText(e.Graphics, c.Title, Theme.Ui(9.5f, FontStyle.Bold), new Rectangle(inner.X, inner.Y, inner.Width, 20),
            sel ? Theme.Text : Color.FromArgb(200, 212, 226), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, c.Preview(), Theme.Ui(8.5f), new Rectangle(inner.X, inner.Y + 21, inner.Width, 18),
            Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

public static class Md
{
    static Regex Bold = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Singleline);
    static Regex InlineCode = new Regex(@"`([^`\r\n]+)`");

    public static void Render(RichTextBox box, List<Msg> msgs, bool showTime)
    {
        box.SuspendLayout();
        box.Clear();
        box.BackColor = Theme.Bg;
        if (msgs.Count == 0)
        {
            box.SelectionFont = Theme.Ui(10f);
            box.SelectionColor = Theme.Muted;
            box.AppendText("开始一段新对话吧。\r\n\r\n可以：\r\n  · 直接提问，或按 Ctrl+Enter 发送\r\n  · 点左下角「附件」把 txt 资料带进来\r\n  · 勾选「任务模式」，让 AI 自己多步执行并产出文件\r\n");
            box.ResumeLayout();
            return;
        }
        System.Text.StringBuilder dbg = new System.Text.StringBuilder("RENDER n=" + msgs.Count);
        for (int i = 0; i < msgs.Count; i++)
        {
            Msg m = msgs[i];
            dbg.Append(" [").Append(m.Role).Append(":").Append(m.Text == null ? -1 : m.Text.Length).Append("]");
            bool user = m.Role == "user";
            string name = user ? "你" : "AI-ACE";
            Box(box, user ? "你" : "AI-ACE", user ? Color.FromArgb(96, 165, 250) : Theme.Accent, 9.5f, FontStyle.Bold);
            if (user)
            {
                box.SelectionStart = box.TextLength;
                box.SelectionBackColor = Theme.Bg;
            }
            if (showTime && m.Time.Length > 0)
                Box(box, "  " + m.Time, Theme.Muted, 8f, FontStyle.Regular);
            NL(box);
            Body(box, m.Text, user ? Theme.UserText : (m.Error ? Theme.Danger : Theme.AiText));
            NL(box);
            NL(box);
        }
        System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "render.log", dbg.ToString() + Environment.NewLine);
        box.SelectionStart = box.TextLength;
        box.ResumeLayout();
    }

    static void Box(RichTextBox b, string s, Color c, float size, FontStyle st)
    {
        b.SelectionStart = b.TextLength;
        b.SelectionLength = 0;
        b.SelectionFont = Theme.Ui(size, st);
        b.SelectionColor = c;
        b.AppendText(s);
    }

    static void NL(RichTextBox b) { b.AppendText("\r\n"); }

    static void Body(RichTextBox b, string text, Color color)
    {
        if (text == null) text = "";
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        bool inCode = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                b.SelectionStart = b.TextLength;
                b.SelectionFont = Theme.Mono(9f);
                b.SelectionColor = Color.FromArgb(190, 230, 210);
                b.SelectionBackColor = Color.FromArgb(24, 32, 40);
                b.AppendText(line + "\r\n");
                continue;
            }
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#"))
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                string head = trimmed.Substring(level).Trim();
                b.SelectionStart = b.TextLength;
                b.SelectionFont = Theme.Ui(level <= 2 ? 11.5f : 10.5f, FontStyle.Bold);
                b.SelectionColor = Theme.Text;
                b.AppendText(head + "\r\n");
                continue;
            }
            string prefix = "";
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("· "))
            {
                prefix = "  · ";
                line = prefix + trimmed.Substring(2);
            }
            else if (Regex.IsMatch(trimmed, @"^\d+\.\s"))
            {
                line = "  " + trimmed;
            }
            Inline(b, line, color);
            b.AppendText("\r\n");
        }
    }

    static void Inline(RichTextBox b, string line, Color color)
    {
        b.SelectionStart = b.TextLength;
        b.SelectionFont = Theme.Ui(10f);
        b.SelectionColor = color;

        int pos = 0;
        while (pos < line.Length)
        {
            Match mb = Bold.Match(line, pos);
            Match mc = InlineCode.Match(line, pos);
            Match next = null;
            bool isBold = false;
            if (mb.Success && mc.Success) { if (mb.Index <= mc.Index) { next = mb; isBold = true; } else next = mc; }
            else if (mb.Success) { next = mb; isBold = true; }
            else if (mc.Success) next = mc;

            if (next == null)
            {
                b.AppendText(line.Substring(pos));
                break;
            }
            if (next.Index > pos) b.AppendText(line.Substring(pos, next.Index - pos));
            string inner = next.Groups[1].Value;
            b.SelectionFont = isBold ? Theme.Ui(10f, FontStyle.Bold) : Theme.Mono(9.5f);
            b.SelectionColor = isBold ? color : Color.FromArgb(190, 230, 210);
            if (!isBold) b.SelectionBackColor = Color.FromArgb(24, 32, 40);
            b.AppendText(inner);
            b.SelectionFont = Theme.Ui(10f);
            b.SelectionColor = color;
            b.SelectionBackColor = Theme.Bg;
            pos = next.Index + next.Length;
        }
    }
}

public static class AppIcon
{
    public static Icon LoadAppIcon()
    {
        try
        {
            string file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(file))
            {
                try { return new Icon(file); }
                catch { }
            }
            try
            {
                Icon exe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exe != null) return exe;
            }
            catch { }
        }
        catch { }
        return SystemIcons.Application;
    }
}
