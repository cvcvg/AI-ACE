using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public class MainForm : Form
{
    Settings cfg;
    List<Conv> convs = new List<Conv>();
    Conv current;
    bool streaming = false;
    CancellationTokenSource cts = null;
    string attachName = "";
    string attachText = "";

    Panel sidePanel;
    Panel mainPanel;
    SidebarList list;
    TextBox txtSearch;
    ComboBox cboModel;
    Label lblStatus;
    MessageView chat;
    Panel main;
    Panel top;
    TextBox input;
    FlatButton btnSend;
    FlatButton btnAttach;
    FlatButton btnRefresh;
    CheckBox chkTask;
    Label lblAttach;
    NotifyIcon tray;
    bool reallyClose = false;
    bool dataChecked = false;

    public MainForm()
    {
        cfg = Store.LoadSettings();
        convs = Store.LoadConvs();

        Text = "AI-ACE";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 820);
        MinimumSize = new Size(1000, 640);
        Font = Theme.Ui(9.5f);
        DoubleBuffered = true;
        try
        {
            Icon = AppIcon.LoadAppIcon();
        }
        catch { Icon = SystemIcons.Application; }

        BuildUi();
        BuildTray();

        if (convs.Count == 0) NewConv(false);
        else
        {
            for (int i = 0; i < convs.Count; i++) if (convs[i].Title == "") convs[i].Title = "新对话";
            convs.Sort(delegate(Conv a, Conv b) { return string.Compare(b.Updated, a.Updated); });
            current = convs[0];
            RefreshList();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        if (string.IsNullOrEmpty(cfg.Auth()))
        {
            using (AuthForm f = new AuthForm(cfg))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    cfg = f.Result;
                    Store.SaveSettings(cfg);
                }
            }
        }

        Shown += async delegate
        {
            LayoutChat();
            LayoutInput();
            await CheckService();
            await LoadModels();
            StartDataDirChecks();
        };
    }

    void BuildUi()
    {
        Panel side = new Panel();
        sidePanel = side;
        side.SetBounds(0, 0, 272, ClientSize.Height);
        side.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        side.BackColor = Theme.PanelBg;
        side.Padding = new Padding(16, 18, 16, 14);
        Controls.Add(side);

        Label brand = new Label();
        brand.Text = "AI-ACE";
        brand.Font = Theme.Ui(17f, FontStyle.Bold);
        brand.ForeColor = Theme.Accent;
        brand.AutoSize = true;
        brand.Location = new Point(18, 16);
        side.Controls.Add(brand);

        Label sub = new Label();
        sub.Text = "本地智能助手 · 原生客户端";
        sub.Font = Theme.Ui(8.5f);
        sub.ForeColor = Theme.Muted;
        sub.AutoSize = true;
        sub.Location = new Point(19, 46);
        side.Controls.Add(sub);
        side.Paint += delegate(object s2, PaintEventArgs e2)
        {
            using (Pen pen = new Pen(Theme.Border)) e2.Graphics.DrawLine(pen, side.Width - 1, 0, side.Width - 1, side.Height);
        };

        FlatButton btnNew = new FlatButton();
        btnNew.Text = "＋  新对话";
        btnNew.SetBounds(16, 78, 240, 36);
        btnNew.Primary = true;
        btnNew.Click += delegate { NewConv(true); };
        side.Controls.Add(btnNew);

        Panel searchBox = new Panel();
        searchBox.SetBounds(16, 124, 240, 30);
        searchBox.BackColor = Theme.PanelBg2;
        searchBox.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Draw.FillRound(e.Graphics, new Rectangle(0, 0, searchBox.Width - 1, searchBox.Height - 1), 8, Theme.PanelBg2);
        };
        side.Controls.Add(searchBox);

        txtSearch = new TextBox();
        txtSearch.BorderStyle = BorderStyle.None;
        txtSearch.BackColor = Theme.PanelBg2;
        txtSearch.ForeColor = Theme.Text;
        txtSearch.Font = Theme.Ui(9f);
        txtSearch.SetBounds(10, 6, 220, 20);
        txtSearch.TextChanged += delegate { RefreshList(); };
        searchBox.Controls.Add(txtSearch);
        SetPlaceholder(txtSearch, "搜索对话…");

        list = new SidebarList();
        list.SetBounds(12, 164, 248, 520);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        list.SelectedIndexChanged += delegate { if (list.SelectedIndex >= 0) SwitchTo(list.SelectedItem as Conv); };
        list.MouseUp += ListMouseUp;
        side.Controls.Add(list);

        FlatButton btnOut = new FlatButton();
        btnOut.Text = "输出文件夹";
        btnOut.SetBounds(16, 700, 114, 30);
        btnOut.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnOut.Click += delegate { OpenOutput(); };
        side.Controls.Add(btnOut);

        FlatButton btnSet = new FlatButton();
        btnSet.Text = "设置";
        btnSet.SetBounds(138, 700, 114, 30);
        btnSet.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnSet.Click += delegate { OpenSettings(); };
        side.Controls.Add(btnSet);

        Label tip = new Label();
        tip.Text = "关闭窗口会收进托盘";
        tip.ForeColor = Theme.Muted;
        tip.Font = Theme.Ui(8f);
        tip.AutoSize = true;
        tip.SetBounds(16, 738, 200, 18);
        tip.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        side.Controls.Add(tip);

        main = new Panel();
        mainPanel = main;
        main.BackColor = Theme.Bg;
        main.SetBounds(272, 0, ClientSize.Width - 272, ClientSize.Height);
        main.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(main);

        top = new Panel();
        top.Dock = DockStyle.Top;
        top.Height = 56;
        top.BackColor = Theme.PanelBg;
        top.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1);
        };
        main.Controls.Add(top);

        Label lm = new Label();
        lm.Text = "模型";
        lm.ForeColor = Theme.Muted;
        lm.AutoSize = true;
        lm.Location = new Point(20, 19);
        top.Controls.Add(lm);

        cboModel = new ComboBox();
        cboModel.DropDownStyle = ComboBoxStyle.DropDownList;
        cboModel.FlatStyle = FlatStyle.Flat;
        cboModel.BackColor = Theme.PanelBg2;
        cboModel.ForeColor = Theme.Text;
        cboModel.Font = Theme.Ui(9.5f);
        cboModel.SetBounds(58, 15, 220, 26);
        cboModel.SelectedIndexChanged += delegate
        {
            if (cboModel.SelectedItem != null)
            {
                cfg.Model = Convert.ToString(cboModel.SelectedItem);
                Store.SaveSettings(cfg);
            }
        };
        top.Controls.Add(cboModel);

        lblStatus = new Label();
        lblStatus.Text = "正在连接…";
        lblStatus.ForeColor = Theme.Muted;
        lblStatus.AutoSize = false;
        lblStatus.SetBounds(296, 19, 380, 20);
        lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        top.Controls.Add(lblStatus);

        btnRefresh = new FlatButton();
        btnRefresh.Text = "刷新模型";
        btnRefresh.SetBounds(700, 13, 96, 30);
        btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnRefresh.Click += async delegate { await LoadModels(); };
        top.Controls.Add(btnRefresh);

        chat = new MessageView();
        chat.BackColor = Theme.Bg;
        main.Controls.Add(chat);
        chat.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        main.Resize += delegate { LayoutChat(); };

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 150;
        bottom.BackColor = Theme.Bg;
        bottom.Padding = new Padding(20, 8, 20, 12);
        main.Controls.Add(bottom);

        Panel inputWrap = new Panel();
        inputWrap.Dock = DockStyle.Fill;
        inputWrap.BackColor = Theme.PanelBg2;
        inputWrap.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle rr = new Rectangle(0, 0, inputWrap.Width - 1, inputWrap.Height - 1);
            Draw.FillRound(e.Graphics, rr, 12, Theme.PanelBg2);
            Draw.StrokeRound(e.Graphics, rr, 12, Theme.Border, 1f);
        };
        bottom.Controls.Add(inputWrap);

        input = new TextBox();
        input.Multiline = true;
        input.BorderStyle = BorderStyle.None;
        input.BackColor = Theme.PanelBg2;
        input.ForeColor = Theme.Text;
        input.Font = Theme.Ui(10.5f);
        input.SetBounds(16, 14, 640, 62);
        input.KeyDown += InputKeyDown;
        inputWrap.Cursor = Cursors.IBeam;
        inputWrap.MouseDown += delegate { try { input.Focus(); } catch { } };
        inputWrap.Controls.Add(input);
        Label hint = new Label();
        hint.Text = "输入问题，按 Enter 发送…";
        hint.ForeColor = Color.FromArgb(96, 108, 122);
        hint.BackColor = Theme.PanelBg2;
        hint.Font = Theme.Ui(10.5f);
        hint.AutoSize = true;
        hint.Location = new Point(21, 20);
        hint.Cursor = Cursors.IBeam;
        hint.Click += delegate { try { input.Focus(); } catch { } };
        input.TextChanged += delegate { hint.Visible = input.TextLength == 0; };
        input.Enter += delegate { hint.Visible = false; };
        input.Leave += delegate { hint.Visible = input.TextLength == 0; };
        inputWrap.Controls.Add(hint);
        hint.BringToFront();

        btnAttach = new FlatButton();
        btnAttach.Text = "＋ 附件";
        btnAttach.SetBounds(16, 88, 78, 28);
        btnAttach.Click += delegate { PickAttachment(); };
        inputWrap.Controls.Add(btnAttach);

        lblAttach = new Label();
        lblAttach.Text = "";
        lblAttach.ForeColor = Theme.Accent;
        lblAttach.Font = Theme.Ui(8.5f);
        lblAttach.AutoSize = false;
        lblAttach.SetBounds(102, 92, 360, 20);
        inputWrap.Controls.Add(lblAttach);

        chkTask = new CheckBox();
        chkTask.Text = "任务模式（多步执行、产出文件）";
        chkTask.ForeColor = Theme.Muted;
        chkTask.Font = Theme.Ui(8.5f);
        chkTask.AutoSize = true;
        chkTask.SetBounds(470, 90, 300, 24);
        chkTask.CheckedChanged += delegate { UpdateHint(); };
        inputWrap.Controls.Add(chkTask);
        if (Environment.GetEnvironmentVariable("AI_ACE_TASK") == "1") chkTask.Checked = true;
        string autoAttach = Environment.GetEnvironmentVariable("AI_ACE_ATTACH");
        if (!string.IsNullOrEmpty(autoAttach) && File.Exists(autoAttach))
        {
            try
            {
                attachText = Api.ReadLocalText(autoAttach, 20000);
                attachName = Path.GetFileName(autoAttach);
                string inbox2 = Path.Combine(Api.RootDir(), "documents", "input");
                if (!Directory.Exists(inbox2)) Directory.CreateDirectory(inbox2);
                File.Copy(autoAttach, Path.Combine(inbox2, attachName), true);
                lblAttach.Text = "已附加：" + attachName + "（已放入知识库目录）";
            }
            catch { }
        }

        btnSend = new FlatButton();
        btnSend.Text = "发送";
        btnSend.Primary = true;
        btnSend.SetBounds(650, 84, 104, 34);
        btnSend.Click += delegate { OnSendClick(); };
        inputWrap.Controls.Add(btnSend);

        Panel bar = new Panel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 24;
        bar.BackColor = Theme.Bg;
        main.Controls.Add(bar);

        InputHint = new Label();
        InputHint.Text = HintText();
        InputHint.ForeColor = Theme.Muted;
        InputHint.Font = Theme.Ui(8.5f);
        InputHint.AutoSize = true;
        InputHint.SetBounds(22, 3, 700, 18);
        bar.Controls.Add(InputHint);

        KeyPreview = true;
        KeyDown += FormKeyDown;
        Resize += delegate { LayoutInput(); LayoutChat(); };
        inputWrap.Resize += delegate { LayoutInput(); };
        LayoutInput();

        chat.SetMessages(new List<Msg>(), cfg.ShowTime);
    }

    Label InputHint;

    void LayoutChat()
    {
        if (chat == null || top == null) return;
        int h = main.ClientSize.Height - top.Height - 150;
        if (h < 120) h = 120;
        chat.SetBounds(0, top.Height, main.ClientSize.Width, h);
    }

    void LayoutInput()
    {
        if (input == null || btnSend == null) return;
        Panel wrap = input.Parent as Panel;
        if (wrap == null) return;
        int w = wrap.ClientSize.Width;
        if (w < 300) return;

        btnSend.SetBounds(w - 124, 12, 108, 34);
        input.SetBounds(20, 14, Math.Max(160, w - 168), 62);

        int chkW = 268;
        int chkLeft = Math.Max(320, w - 140 - chkW);
        chkTask.SetBounds(chkLeft, 78, chkW, 24);

        btnAttach.SetBounds(16, 78, 84, 26);
        int lblW = chkLeft - 16 - 104;
        lblAttach.SetBounds(104, 82, Math.Max(120, lblW), 20);
    }

    string HintText()
    {
        return chkTask.Checked
            ? "任务模式：AI 会自己拆步骤、连续调用工具，最后把产物写到 documents\\output  ·  Ctrl+Enter 发送"
            : "Ctrl+Enter 发送  ·  Esc 停止  ·  Ctrl+N 新对话  ·  Ctrl+F 搜索";
    }

    void UpdateHint() { if (InputHint != null) InputHint.Text = HintText(); }

    void BuildTray()
    {
        tray = new NotifyIcon();
        tray.Text = "AI-ACE 原生客户端";
        try
        {
            tray.Icon = AppIcon.LoadAppIcon();
        }
        catch { tray.Icon = SystemIcons.Application; }

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("打开窗口", null, delegate { RestoreFromTray(); });
        menu.Items.Add("新对话", null, delegate { RestoreFromTray(); NewConv(true); });
        menu.Items.Add("重启服务", null, delegate { RestartService(); });
        menu.Items.Add("打开输出文件夹", null, delegate { OpenOutput(); });
        menu.Items.Add("查看日志", null, delegate { OpenLog(); });
        menu.Items.Add("数据位置…", null, delegate { OpenDataLocation(); });
        menu.Items.Add("安全自检", null, delegate { ShowSecurityCheck(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { reallyClose = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { RestoreFromTray(); };
        tray.Visible = true;
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            tray.ShowBalloonTip(1200, "AI-ACE 还在运行", "窗口已收进托盘，双击图标可以打开。", ToolTipIcon.None);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        base.OnFormClosed(e);
    }

    // ---------- 会话 ----------

    void NewConv(bool focus)
    {
        Conv c = new Conv(Guid.NewGuid().ToString("N"));
        convs.Insert(0, c);
        current = c;
        RefreshList();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        chat.SetMessages(c.Msgs, cfg.ShowTime);
        Store.SaveConvs(convs);
        if (focus) input.Focus();
    }

    void SwitchTo(Conv c)
    {
        if (c == null) return;
        current = c;
        chat.SetMessages(c.Msgs, cfg.ShowTime);
        ScrollEnd();
    }

    void RefreshList()
    {
        if (list == null) return;
        string q = txtSearch == null ? "" : txtSearch.Text.Trim();
        if (q == "搜索对话…") q = "";
        string selId = current == null ? "" : current.Id;
        list.BeginUpdate();
        list.Items.Clear();
        List<Conv> sorted = new List<Conv>(convs);
        sorted.Sort(delegate(Conv a, Conv b) { return string.Compare(b.Updated, a.Updated); });
        int sel = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            Conv c = sorted[i];
            if (q.Length > 0 && c.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && c.Preview().IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            list.Items.Add(c);
            if (c.Id == selId) sel = list.Items.Count - 1;
        }
        list.EndUpdate();
        if (sel >= 0) { list.SelectedIndex = sel; }
    }

    void ListMouseUp(object sender, MouseEventArgs e)
    {
        int idx = list.IndexFromPoint(e.Location);
        if (idx < 0) return;
        Conv c = list.Items[idx] as Conv;
        if (c == null) return;
        if (e.Button == MouseButtons.Right)
        {
            list.SelectedIndex = idx;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("重命名", null, delegate { RenameConv(c); });
            m.Items.Add("清空消息", null, delegate { c.Msgs.Clear(); c.Title = "新对话"; c.Touch(); Store.SaveConvs(convs); chat.SetMessages(c.Msgs, cfg.ShowTime); RefreshList(); });
            m.Items.Add("删除对话", null, delegate { DeleteConv(c); });
            m.Show(list, e.Location);
        }
    }

    void RenameConv(Conv c)
    {
        using (InputDialog d = new InputDialog("重命名对话", c.Title))
        {
            if (d.ShowDialog(this) == DialogResult.OK && d.Value.Trim().Length > 0)
            {
                c.Title = d.Value.Trim();
                c.Touch();
                Store.SaveConvs(convs);
                RefreshList();
            }
        }
    }

    void DeleteConv(Conv c)
    {
        if (MessageBox.Show(this, "删除这段对话？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        convs.Remove(c);
        Store.SaveConvs(convs);
        if (convs.Count == 0) NewConv(false);
        else { current = convs[0]; chat.SetMessages(current.Msgs, cfg.ShowTime); RefreshList(); }
    }

    // ---------- 输入与发送 ----------

    void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnSendClick(); }
        else if (e.KeyCode == Keys.Escape) { StopStream(); }
    }

    void FormKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.N) { e.SuppressKeyPress = true; NewConv(true); }
        else if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; txtSearch.Focus(); }
        else if (e.Control && e.KeyCode == Keys.Oemcomma) { e.SuppressKeyPress = true; OpenSettings(); }
    }

    void OnSendClick()
    {
        if (streaming) { StopStream(); return; }
        string text = input.Text.Trim();
        if (text.Length == 0) { input.Focus(); return; }
        if (current == null) NewConv(false);

        if (attachText.Length > 0)
        {
            text = "【附件：" + attachName + "】\r\n" + attachText + "\r\n\r\n" + text;
            ClearAttachment();
        }

        input.Text = "";
        Msg user = new Msg("user", text);
        current.Msgs.Add(user);
        if (current.Title == "新对话" || current.Title.Length == 0)
            current.Title = text.Length > 18 ? text.Substring(0, 18) + "…" : text;
        current.Touch();
        Store.SaveConvs(convs);
        RefreshList();

        chat.SetMessages(current.Msgs, cfg.ShowTime);
        ScrollEnd();

        if (chkTask.Checked) RunTaskAsync(text);
        else StreamAnswer(text);
    }

    void SetBusy(bool busy)
    {
        streaming = busy;
        btnSend.Text = busy ? "停止" : "发送";
        input.ReadOnly = busy;
    }

    async void StreamAnswer(string prompt)
    {
        Msg reply = new Msg("assistant", "");
        current.Msgs.Add(reply);
        SetBusy(true);
        cts = new CancellationTokenSource();
        lblStatus.Text = "正在回答…";
        chat.SetMessages(current.Msgs, cfg.ShowTime);
        chat.ScrollToEnd();

        try
        {
            System.Text.StringBuilder think = new System.Text.StringBuilder();
            await Api.StreamChat(cfg, current.Msgs.GetRange(0, current.Msgs.Count - 1), prompt,
                delegate(string delta, bool isThink)
                {
                    if (isThink)
                    {
                        think.Append(delta);
                        lblStatus.Text = "思考中… " + think.Length + " 字";
                        return;
                    }
                    chat.AppendDelta(delta);
                }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            reply.Text += Environment.NewLine + "（已停止）";
        }
        catch (Exception ex)
        {
            reply.Error = true;
            reply.Text = "请求失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            cts = null;
            current.Touch();
            Store.SaveConvs(convs);
            chat.SetMessages(current.Msgs, cfg.ShowTime);
            chat.ScrollToEnd();
            RefreshList();
            lblStatus.Text = "就绪 · " + ModelCount() + " 个模型";
        }
    }

    async void RunTaskAsync(string goal)
    {
        Msg reply = new Msg("assistant", "");
        current.Msgs.Add(reply);
        SetBusy(true);
        lblStatus.Text = "任务已提交，AI 正在多步执行…";
        reply.Text = "任务已提交，等待执行…";
        chat.SetMessages(current.Msgs, cfg.ShowTime);

        try
        {
            string id = await Api.AgentRun(cfg, goal);
            reply.Text = "任务 " + id.Substring(0, Math.Min(8, id.Length)) + " 已开始…";
            chat.SetLastText(reply.Text);

            int guard = 0;
            string status = "running";
            Dictionary<string, object> last = null;
            while (guard++ < 600)
            {
                await Task.Delay(1200);
                last = await Api.AgentStatus(cfg, id);
                if (last == null) continue;
                status = Str(last, "status");
                reply.Text = TaskText(last, id);
                chat.SetLastText(reply.Text);
                lblStatus.Text = "任务执行中 · " + status + " · 步数 " + StepCount(last);
                if (status != "running" && status != "queued") break;
            }
            if (last != null) chat.SetLastText(TaskText(last, id));
        }
        catch (Exception ex)
        {
            reply.Error = true;
            reply.Text = "任务失败：" + ex.Message;
            chat.SetLastText(reply.Text);
        }
        finally
        {
            SetBusy(false);
            current.Touch();
            Store.SaveConvs(convs);
            ScrollEnd();
            RefreshList();
            lblStatus.Text = "就绪 · " + ModelCount() + " 个模型";
        }
    }

    static string Str(Dictionary<string, object> d, string k)
    {
        if (d == null || !d.ContainsKey(k) || d[k] == null) return "";
        return Convert.ToString(d[k]);
    }

    static string StepCount(Dictionary<string, object> d)
    {
        if (d == null) return "0";
        return d.ContainsKey("steps") ? Convert.ToString(d["steps"]) : "0";
    }

    static string TaskText(Dictionary<string, object> d, string id)
    {
        string NL = Environment.NewLine;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string status = Str(d, "status");
        string head = status == "done" ? "已完成" : (status == "failed" ? "失败" : "执行中");
        sb.Append("任务 ").Append(id.Substring(0, Math.Min(8, id.Length))).Append(" · ").Append(head).Append(NL);
        IList steps = d != null && d.ContainsKey("trace") ? d["trace"] as IList : null;
        if (steps != null)
        {
            foreach (object o in steps)
            {
                IDictionary<string, object> step = o as IDictionary<string, object>;
                if (step == null) continue;
                IList calls = step.ContainsKey("calls") ? step["calls"] as IList : null;
                if (calls != null && calls.Count > 0)
                {
                    foreach (object co in calls)
                    {
                        IDictionary<string, object> call = co as IDictionary<string, object>;
                        if (call == null) continue;
                        string nm = call.ContainsKey("name") ? Convert.ToString(call["name"]) : "tool";
                        string code = call.ContainsKey("status") ? Convert.ToString(call["status"]) : "";
                        string ms = call.ContainsKey("ms") ? Convert.ToString(call["ms"]) : "";
                        sb.Append("  ✓ ").Append(nm);
                        if (code != "") sb.Append("  ").Append(code);
                        if (ms != "") sb.Append("  ").Append(ms).Append("ms");
                        sb.Append(NL);
                    }
                }
                else if (step.ContainsKey("final"))
                {
                    sb.Append("  ✓ 全部完成").Append(NL);
                }
            }
        }
        string ans = Str(d, "answer");
        if (ans.Length > 0) sb.Append(NL).Append(ans);
        string rf = Str(d, "result_file");
        if (rf.Length > 0) sb.Append(NL).Append("产物：").Append(rf);
        if (status == "running") sb.Append(NL).Append("（实时刷新中…）");
        return sb.ToString();
    }

    void StopStream()
    {
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }
    }

    void ScrollEnd()
    {
        chat.ScrollToEnd();
    }

    // ---------- 附件 ----------

    void PickAttachment()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = "选择文本文件作为附件";
            d.Filter = "文本文件|*.txt;*.md;*.csv;*.json;*.log;*.py;*.cs;*.js;*.xml;*.yml;*.yaml;*.sql;*.html|所有文件|*.*";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                attachText = Api.ReadLocalText(d.FileName, 20000);
                attachName = Path.GetFileName(d.FileName);
                string inbox = Path.Combine(Api.RootDir(), "documents", "input");
                if (!Directory.Exists(inbox)) Directory.CreateDirectory(inbox);
                string dest = Path.Combine(inbox, attachName);
                File.Copy(d.FileName, dest, true);
                lblAttach.Text = "已附加：" + attachName + "（" + attachText.Length + " 字，已放入知识库目录）";
                lblStatus.ForeColor = Theme.Accent;
                lblStatus.Text = "已加入知识库，后台索引约需 30 秒";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "附件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    void ClearAttachment()
    {
        attachText = "";
        attachName = "";
        lblAttach.Text = "";
    }

    // ---------- 设置 / 服务 ----------

    async Task CheckService()
    {
        if (Bootstrap.DemoMode)
        {
            lblStatus.ForeColor = Theme.Muted;
            lblStatus.Text = "演示模式（未连接服务）";
            return;
        }
        lblStatus.Text = "正在连接服务…";
        bool ok = await Api.Ping(cfg);
        if (!ok)
        {
            lblStatus.Text = "服务未就绪";
            lblStatus.ForeColor = Theme.Danger;
        }
        else
        {
            lblStatus.ForeColor = Theme.Accent;
            lblStatus.Text = "服务已就绪";
        }
    }

    string ModelCount()
    {
        return cboModel == null ? "0" : cboModel.Items.Count.ToString();
    }

    async Task LoadModels()
    {
        if (Bootstrap.DemoMode) return;
        try
        {
            List<string> models = null;
            Exception lastErr = null;
            for (int attempt = 0; attempt < 3 && models == null; attempt++)
            {
                Exception attemptErr = null;
                try { models = await Api.Models(cfg); }
                catch (Exception e0) { attemptErr = e0; }
                if (attemptErr != null)
                {
                    lastErr = attemptErr;
                    lblStatus.ForeColor = Theme.Muted;
                    lblStatus.Text = "正在连接服务…" + (attempt + 1);
                    if (attempt < 2) await Task.Delay(1500);
                }
            }
            if (models == null) throw lastErr ?? new Exception("无法连接服务");
            cboModel.Items.Clear();
            foreach (string m in models) cboModel.Items.Add(m);
            if (models.Count > 0)
            {
                int idx = 0;
                if (!string.IsNullOrEmpty(cfg.Model))
                {
                    for (int i = 0; i < models.Count; i++) if (models[i] == cfg.Model) idx = i;
                }
                cboModel.SelectedIndex = idx;
                cfg.Model = models[idx];
            }
            lblStatus.ForeColor = Theme.Accent;
            lblStatus.Text = "服务已就绪 · " + models.Count + " 个模型";
        }
        catch (Exception ex)
        {
            lblStatus.ForeColor = Theme.Danger;
            lblStatus.Text = "获取模型失败：" + Api.Short(ex.Message);
        }
    }

    void OpenSettings()
    {
        using (SettingsForm f = new SettingsForm(cfg))
        {
            if (f.ShowDialog(this) == DialogResult.OK)
            {
                cfg = f.Result;
                Store.SaveSettings(cfg);
                chat.SetMessages(current == null ? new List<Msg>() : current.Msgs, cfg.ShowTime);
                Task t = LoadModels();
            }
        }
    }

    void OpenOutput()
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        string p = Path.Combine(root, "documents", "output");
        try
        {
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            Process.Start("explorer.exe", p);
        }
        catch { }
    }

    async void RestartService()
    {
        string root = Bootstrap.FindRoot();
        if (root == null)
        {
            lblStatus.ForeColor = Theme.Danger;
            lblStatus.Text = "重启失败：找不到 AI-ACE 目录";
            return;
        }
        lblStatus.ForeColor = Theme.Muted;
        lblStatus.Text = "正在重启服务…";
        Bootstrap.Log("tray: restart service, root=" + root);
        string compose = Path.Combine(Path.Combine(root, "compose"), "docker-compose.yml");
        string output = "";
        bool ok = await Task.Factory.StartNew(delegate
        {
            string o;
            bool r = Bootstrap.RunDocker("compose -f \"" + compose + "\" restart", 600000, out o, root);
            output = o;
            return r;
        });
        Bootstrap.Log("tray: compose restart " + (ok ? "ok" : "failed") + " | " + Api.Short(output));
        if (!ok)
        {
            lblStatus.ForeColor = Theme.Danger;
            lblStatus.Text = "重启失败：" + Api.Short(output);
            MessageBox.Show(this, "重启服务失败：" + Environment.NewLine + Api.Short(output), "重启服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        lblStatus.Text = "服务已重启，正在刷新…";
        await CheckService();
        await LoadModels();
    }

    void OpenLog()
    {
        try
        {
            string p = Bootstrap.LogPath();
            if (!File.Exists(p)) File.WriteAllText(p, "");
            Process.Start("notepad.exe", "\"" + p + "\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "查看日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 托盘「数据位置…」：查看 / 更改数据目录并迁移
    void OpenDataLocation()
    {
        string root = Bootstrap.FindRoot();
        string cur = DataDir.Current(root);
        string preset = cur.Length > 0 && !DataDir.OnCDrive(cur) ? cur : DataDir.DefaultRoot();
        using (DataDirForm f = new DataDirForm(root, cur, preset))
        {
            f.ShowDialog(this);
            if (f.Migrated)
            {
                Bootstrap.Log("数据位置：用户把数据目录改到 " + f.NewDir);
                lblStatus.Text = "数据已迁到：" + f.NewDir;
            }
        }
    }

    // 启动检查：数据目录在 C 盘就问一次；Docker 镜像盘在 C 盘只提示一次
    void StartDataDirChecks()
    {
        if (dataChecked) return;
        dataChecked = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string root = "";
            string cur = "";
            long bytes = 0;
            bool onC = false;
            string docker = "";
            bool dockerOnC = false;
            try
            {
                root = Bootstrap.FindRoot();
                cur = DataDir.Current(root);
                if (cur.Length > 0 && Directory.Exists(cur)) DataDir.UsageBytes(cur, out bytes);
                onC = cur.Length > 0 && DataDir.OnCDrive(cur);
                docker = DataDir.DockerDataFolder();
                dockerOnC = docker.Length > 0 && DataDir.OnCDrive(docker);
            }
            catch { }
            try { BeginInvoke(new Action(delegate { ShowDataDirPrompts(root, cur, bytes, onC, docker, dockerOnC); })); }
            catch { }
        });
    }

    void ShowDataDirPrompts(string root, string cur, long bytes, bool onC, string docker, bool dockerOnC)
    {
        if (onC)
        {
            string target = DataDir.DefaultRoot();
            string msg = "数据目录现在在 C 盘，已经占用 " + DataDir.SizeText(bytes) + "：" + Environment.NewLine + cur +
                         Environment.NewLine + Environment.NewLine +
                         "现在搬到 " + target + " 吗？" + Environment.NewLine + Environment.NewLine +
                         "迁移会先停服务、复制数据、校验文件数与总字节，再把 compose\\.env 指到新位置。旧数据不会删除。";
            Bootstrap.Log("启动检查：数据目录在 C 盘 " + cur + "（" + DataDir.SizeText(bytes) + "）");
            if (MessageBox.Show(this, msg, "数据在 C 盘", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                MigrateData(root, cur, target);
        }
        if (dockerOnC && !DockerHintShown(root))
        {
            MarkDockerHintShown(root);
            Bootstrap.Log("启动检查：Docker 镜像盘在 C 盘 " + docker);
            MessageBox.Show(this,
                "Docker Desktop 的镜像盘还在 C 盘：" + Environment.NewLine + docker + Environment.NewLine + Environment.NewLine +
                "容器镜像会占掉几十 GB 系统盘空间，建议改到 D 盘：" + Environment.NewLine +
                "Docker Desktop → Settings → Resources → Advanced → Disk image location 选到 D:\\DockerData。" + Environment.NewLine + Environment.NewLine +
                "本程序只提示，不代改 Docker 的设置。",
                "Docker 镜像盘在 C 盘", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    void MigrateData(string root, string cur, string target)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            using (DataDirForm f = new DataDirForm(root, cur, target))
            {
                f.AutoStart = true;
                f.ShowDialog(this);
                if (f.Migrated)
                {
                    Bootstrap.Log("启动检查：数据已从 " + cur + " 迁到 " + f.NewDir);
                    lblStatus.Text = "数据已迁到：" + f.NewDir;
                }
            }
        }
        finally { Cursor = Cursors.Default; }
    }

    static string DockerHintPath(string root)
    {
        try { return Path.Combine(Path.Combine(root, "system"), "docker-disk-hint.txt"); }
        catch { return ""; }
    }

    static bool DockerHintShown(string root)
    {
        try { string p = DockerHintPath(root); return p.Length == 0 || File.Exists(p); }
        catch { return true; }
    }

    static void MarkDockerHintShown(string root)
    {
        try
        {
            string p = DockerHintPath(root);
            if (p.Length == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch { }
    }

    // 托盘「安全自检」：后台跑，避免 wsl 超时卡住界面；结果同时落 startup.log
    void ShowSecurityCheck()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            NetGuardResult r = NetGuard.SecurityCheck();
            try
            {
                Bootstrap.Log("安全自检：" + r.Summary);
                Bootstrap.Log("安全自检明细：" + r.Detail.Replace("\r", "").Replace("\n", " | "));
            }
            catch { }
            try { BeginInvoke(new Action(delegate { ShowSecurityCheckResult(r); })); }
            catch { }
        });
    }

    void ShowSecurityCheckResult(NetGuardResult r)
    {
        string detail = r.Detail == null ? "" : r.Detail;
        if (detail.Length > 2000) detail = detail.Substring(0, 2000) + Environment.NewLine + "……（明细超过 2000 字，已截断）";
        MessageBox.Show(this, r.Summary + Environment.NewLine + Environment.NewLine + detail,
            r.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void SetPlaceholder(TextBox box, string text)
    {
        box.ForeColor = Theme.Muted;
        box.Text = text;
        box.GotFocus += delegate
        {
            if (box.Text == text) { box.Text = ""; box.ForeColor = Theme.Text; }
        };
        box.LostFocus += delegate
        {
            if (box.Text.Trim().Length == 0) { box.Text = text; box.ForeColor = Theme.Muted; }
        };
    }
}

public class InputDialog : Form
{
    public string Value = "";
    TextBox box;

    public InputDialog(string title, string initial)
    {
        Text = title;
        ClientSize = new Size(420, 130);
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.Ui(9.5f);

        box = new TextBox();
        box.Text = initial;
        box.BackColor = Theme.PanelBg2;
        box.ForeColor = Theme.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.SetBounds(20, 30, 380, 26);
        Controls.Add(box);

        FlatButton ok = new FlatButton();
        ok.Text = "确定";
        ok.Primary = true;
        ok.SetBounds(220, 76, 84, 32);
        ok.Click += delegate { Value = box.Text; DialogResult = DialogResult.OK; };
        Controls.Add(ok);

        FlatButton cancel = new FlatButton();
        cancel.Text = "取消";
        cancel.SetBounds(316, 76, 84, 32);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}

public class AuthForm : Form
{
    public Settings Result;
    Settings cfg;
    TextBox email, pass, key;

    public AuthForm(Settings s)
    {
        cfg = s;
        Result = s;
        Text = "登录 AI-ACE";
        ClientSize = new Size(460, 300);
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.Ui(9.5f);

        Label t = new Label();
        t.Text = "第一次使用，请登录（或直接填 API Key）";
        t.AutoSize = true;
        t.SetBounds(24, 20, 400, 20);
        t.ForeColor = Theme.Muted;
        Controls.Add(t);

        email = Field("邮箱", 56);
        pass = Field("密码", 118);
        pass.UseSystemPasswordChar = true;
        key = Field("API Key（sk-…，可留空）", 180);

        FlatButton login = new FlatButton();
        login.Text = "登录";
        login.Primary = true;
        login.SetBounds(236, 232, 90, 34);
        login.Click += async delegate
        {
            try
            {
                login.Enabled = false;
                string token = await Api.Signin(cfg, email.Text.Trim(), pass.Text);
                cfg.Token = token;
                cfg.Email = email.Text.Trim();
                if (key.Text.Trim().Length > 0) cfg.ApiKey = key.Text.Trim();
                Store.SaveSettings(cfg);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "登录失败：" + ex.Message, "登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { login.Enabled = true; }
        };
        Controls.Add(login);

        FlatButton skip = new FlatButton();
        skip.Text = "用 API Key";
        skip.SetBounds(334, 232, 100, 34);
        skip.Click += delegate
        {
            if (key.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "请先填写 API Key", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            cfg.ApiKey = key.Text.Trim();
            cfg.Token = "";
            Store.SaveSettings(cfg);
            DialogResult = DialogResult.OK;
        };
        Controls.Add(skip);

        AcceptButton = login;
    }

    TextBox Field(string label, int y)
    {
        Label l = new Label();
        l.Text = label;
        l.AutoSize = true;
        l.ForeColor = Theme.Muted;
        l.SetBounds(24, y - 18, 300, 18);
        Controls.Add(l);

        TextBox b = new TextBox();
        b.BackColor = Theme.PanelBg2;
        b.ForeColor = Theme.Text;
        b.BorderStyle = BorderStyle.FixedSingle;
        b.SetBounds(24, y, 410, 26);
        Controls.Add(b);
        return b;
    }
}

public class SettingsForm : Form
{
    public Settings Result;
    Settings cfg;
    TextBox url, agent, key, temp, turns;
    CheckBox showTime;

    public SettingsForm(Settings s)
    {
        cfg = s;
        Result = s;
        Text = "设置";
        ClientSize = new Size(480, 420);
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.Ui(9.5f);

        url = Row("服务地址（Open WebUI）", 34, cfg.BaseUrl);
        agent = Row("任务服务地址（ace-agent）", 96, cfg.AgentUrl);
        key = Row("API Key / Token", 158, cfg.Auth());
        temp = Row("温度（0-2）", 220, cfg.Temperature.ToString("0.0"));
        turns = Row("上下文轮数", 282, cfg.HistoryTurns.ToString());

        showTime = new CheckBox();
        showTime.Text = "消息显示时间";
        showTime.ForeColor = Theme.Muted;
        showTime.AutoSize = true;
        showTime.SetBounds(24, 340, 200, 22);
        showTime.Checked = cfg.ShowTime;
        Controls.Add(showTime);

        CheckBox think = new CheckBox();
        think.Text = "深度思考（更慢但更严谨）";
        think.ForeColor = Theme.Muted;
        think.AutoSize = true;
        think.SetBounds(210, 340, 240, 22);
        think.Checked = cfg.Thinking;
        Controls.Add(think);

        FlatButton save = new FlatButton();
        save.Text = "保存";
        save.Primary = true;
        save.SetBounds(266, 372, 90, 34);
        save.Click += delegate
        {
            cfg.BaseUrl = url.Text.Trim();
            cfg.AgentUrl = agent.Text.Trim();
            string k = key.Text.Trim();
            if (k.StartsWith("sk-")) { cfg.ApiKey = k; if (cfg.Token.Length > 0) cfg.Token = ""; }
            else cfg.Token = k;
            double t;
            if (double.TryParse(temp.Text.Trim(), out t)) cfg.Temperature = Math.Max(0, Math.Min(2, t));
            int n;
            if (int.TryParse(turns.Text.Trim(), out n)) cfg.HistoryTurns = Math.Max(1, Math.Min(50, n));
            cfg.ShowTime = showTime.Checked;
            cfg.Thinking = think.Checked;
            // 授权结构：换成 v2 后统一走 ApiKey，避免旧 token 失效卡住
            Store.SaveSettings(cfg);
            Result = cfg;
            DialogResult = DialogResult.OK;
        };
        Controls.Add(save);

        FlatButton cancel = new FlatButton();
        cancel.Text = "取消";
        cancel.SetBounds(364, 372, 90, 34);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        Controls.Add(cancel);
    }

    TextBox Row(string label, int y, string value)
    {
        Label l = new Label();
        l.Text = label;
        l.AutoSize = true;
        l.ForeColor = Theme.Muted;
        l.SetBounds(24, y, 420, 18);
        Controls.Add(l);

        TextBox b = new TextBox();
        b.Text = value;
        b.BackColor = Theme.PanelBg2;
        b.ForeColor = Theme.Text;
        b.BorderStyle = BorderStyle.FixedSingle;
        b.SetBounds(24, y + 20, 430, 26);
        Controls.Add(b);
        return b;
    }
}
