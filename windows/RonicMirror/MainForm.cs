using System.Drawing;
using QRCoder;

namespace RonicMirror;

/// <summary>
/// Janela principal com três abas: Conexão (link, QR, status), Aparelhos (autorizar, desconectar,
/// bloquear) e Telas (editor visual de posição). Ao fechar, continua na bandeja do sistema.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Cyan = Color.FromArgb(0, 180, 216);
    private const int ContentWidth = 520;

    private readonly AppController _app = new();
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayUrl = new() { Enabled = false };
    private readonly ToolStripMenuItem _trayPending = new() { Visible = false };
    private readonly ToolStripMenuItem _trayPosition = new("Posição das telas");

    private readonly TabControl _tabs = new();
    private readonly TabPage _tabDevices = new("Aparelhos");

    // Conexão
    private readonly Label _url = new();
    private readonly Label _others = new();
    private readonly PictureBox _qr = new();
    private readonly Button _copy = new();
    private readonly Label _serverStatus = new();
    private readonly Label _displayStatus = new();
    private readonly Label _viewers = new();
    private readonly Panel _driverPanel = new();
    private readonly Button _installDriver = new();

    // Aparelhos
    private readonly CheckBox _requireApproval = new();
    private readonly FlowLayoutPanel _devicesList = new();

    // Telas
    private readonly ArrangementControl _arrangement = new();
    private readonly Dictionary<DisplaySide, Button> _sideButtons = new();
    private readonly Label _customLabel = new();
    private readonly ComboBox _screenPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly Label _noScreens = new();
    private string? _selectedScreen; // DeviceName do monitor virtual selecionado

    private string _qrFor = "";
    private string _devicesSignature = "";
    private bool _exiting;
    private bool _trayHintShown;
    private ApprovalForm? _approval;
    private readonly Queue<DeviceInfo> _approvalQueue = new();

    public MainForm()
    {
        Text = "Ronic Mirror";
        Icon = LoadIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(ContentWidth + 40, 680);
        Padding = new Padding(16);

        BuildLayout();
        BuildTray();

        _app.Changed += RefreshUi;
        _app.ApprovalRequested += d => { _approvalQueue.Enqueue(d); BeginInvoke(ShowNextApproval); };
        Load += (_, _) => { _app.Start(); RefreshUi(); };
    }

    // ---------------- Layout ----------------

    private void BuildLayout()
    {
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, WrapContents = false };
        header.Controls.Add(new PictureBox { Image = LoadLogo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(52, 52), Margin = new Padding(0, 0, 12, 0) });
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        titles.Controls.Add(new Label { Text = "Ronic Mirror", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true });
        titles.Controls.Add(new Label { Text = "Use o iPad, tablet ou celular como segundo monitor via Wi-Fi", ForeColor = Color.DimGray, AutoSize = true });
        header.Controls.Add(titles);

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            ForeColor = Color.DimGray,
            Text = "ⓘ Ao fechar esta janela, o Ronic Mirror continua rodando na bandeja do sistema (perto do relógio). " +
                   "Para encerrar, clique com o botão direito no ícone e escolha Sair.",
        };

        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(14, 5);
        _tabs.TabPages.Add(BuildConnectionTab());
        _tabs.TabPages.Add(BuildDevicesTab());
        _tabs.TabPages.Add(BuildDisplaysTab());
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex == 2) RefreshArrangement(); };

        Controls.Add(_tabs);
        Controls.Add(footer);
        Controls.Add(header);
    }

    private TabPage BuildConnectionTab()
    {
        var page = new TabPage("Conexão") { BackColor = Color.White, Padding = new Padding(12) };
        var root = TopDown();
        page.Controls.Add(root);

        // Aviso do driver
        _driverPanel.BackColor = Color.FromArgb(255, 244, 229);
        _driverPanel.Size = new Size(ContentWidth - 20, 118);
        _driverPanel.Margin = new Padding(0, 0, 0, 12);
        var driverText = new Label
        {
            Text = "Para criar o monitor virtual, o Windows precisa de um driver de vídeo virtual (Parsec VDD, gratuito e assinado). " +
                   "Clique em Instalar driver e confirme o pedido de administrador. Isso só é feito uma vez.",
            MaximumSize = new Size(ContentWidth - 40, 0),
            AutoSize = true,
            Location = new Point(10, 8),
        };
        _installDriver.Text = "Instalar driver";
        _installDriver.AutoSize = true;
        _installDriver.Location = new Point(10, 78);
        _installDriver.Click += async (_, _) => await _app.InstallDriverAsync();
        var retry = new Button { Text = "Tentar novamente", AutoSize = true, Location = new Point(130, 78) };
        retry.Click += async (_, _) => await _app.CheckDriverAsync();
        _driverPanel.Controls.AddRange(new Control[] { driverText, _installDriver, retry });
        root.Controls.Add(_driverPanel);

        root.Controls.Add(SectionTitle("No iPad, tablet ou celular, abra no navegador"));
        var linkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        _qr.Size = new Size(120, 120);
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.Margin = new Padding(0, 0, 14, 0);
        linkRow.Controls.Add(_qr);
        var linkCol = TopDown();
        _url.Font = new Font("Consolas", 14f, FontStyle.Bold);
        _url.AutoSize = true;
        _url.Margin = new Padding(0, 8, 0, 8);
        linkCol.Controls.Add(_url);
        _copy.Text = "Copiar link";
        _copy.AutoSize = true;
        StylePrimary(_copy);
        _copy.Click += (_, _) => CopyUrl();
        linkCol.Controls.Add(_copy);
        linkCol.Controls.Add(new Label { Text = "Ou aponte a câmera do aparelho para o QR code.", ForeColor = Color.DimGray, AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        linkRow.Controls.Add(linkCol);
        root.Controls.Add(linkRow);
        _others.ForeColor = Color.DimGray;
        _others.AutoSize = true;
        _others.MaximumSize = new Size(ContentWidth - 20, 0);
        root.Controls.Add(_others);

        root.Controls.Add(SectionTitle("Status"));
        foreach (var l in new[] { _serverStatus, _displayStatus, _viewers })
        {
            l.AutoSize = true;
            l.MaximumSize = new Size(ContentWidth - 20, 0);
            l.Margin = new Padding(0, 2, 0, 2);
            root.Controls.Add(l);
        }
        return page;
    }

    private TabPage BuildDevicesTab()
    {
        _tabDevices.BackColor = Color.White;
        _tabDevices.Padding = new Padding(12);
        var root = TopDown();
        root.AutoScroll = true;
        root.Dock = DockStyle.Fill;
        root.AutoSize = false;
        _tabDevices.Controls.Add(root);

        _requireApproval.Text = "Pedir minha autorização para aparelhos novos";
        _requireApproval.AutoSize = true;
        _requireApproval.Font = new Font("Segoe UI Semibold", 10f);
        _requireApproval.CheckedChanged += (_, _) => { if (_requireApproval.Checked != _app.RequireApproval) _app.SetRequireApproval(_requireApproval.Checked); };
        root.Controls.Add(_requireApproval);
        root.Controls.Add(new Label
        {
            Text = "Ninguém vê sua tela sem você clicar em Aceitar. Aparelhos marcados como confiáveis entram direto.",
            ForeColor = Color.DimGray,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 30, 0),
            Margin = new Padding(20, 0, 0, 8),
        });

        _devicesList.FlowDirection = FlowDirection.TopDown;
        _devicesList.WrapContents = false;
        _devicesList.AutoSize = true;
        root.Controls.Add(_devicesList);
        return _tabDevices;
    }

    private TabPage BuildDisplaysTab()
    {
        var page = new TabPage("Telas") { BackColor = Color.White, Padding = new Padding(12) };
        var root = TopDown();
        page.Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "Cada aparelho conectado tem a sua tela (em azul). Arraste-as para onde ficam em relação aos seus monitores. Ao soltar, elas grudam na borda mais próxima, como em Configurações › Sistema › Tela.",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 20, 0),
            Margin = new Padding(0, 0, 0, 10),
        });
        _arrangement.Size = new Size(ContentWidth - 20, 300);
        _arrangement.Moved += (device, origin) => _app.MoveTo(device, origin);
        _arrangement.Selected += device => { _selectedScreen = device; RefreshUi(); };
        root.Controls.Add(_arrangement);

        var quick = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        quick.Controls.Add(new Label { Text = "Posição rápida:", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 7, 6, 0) });
        _screenPicker.Margin = new Padding(0, 3, 8, 0);
        _screenPicker.SelectedIndexChanged += (_, _) =>
        {
            if (_screenPicker.SelectedItem is ScreenItem item && item.Screen.Monitor.DeviceName != _selectedScreen)
            {
                _selectedScreen = item.Screen.Monitor.DeviceName;
                RefreshUi();
            }
        };
        quick.Controls.Add(_screenPicker);
        foreach (var (side, label) in new[] { (DisplaySide.Left, "← Esquerda"), (DisplaySide.Right, "Direita →"), (DisplaySide.Top, "↑ Acima"), (DisplaySide.Bottom, "↓ Abaixo") })
        {
            var b = new Button { Text = label, AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            b.Click += (_, _) => { if (SelectedScreen() is { } screen) _app.SetPosition(screen, side); };
            _sideButtons[side] = b;
            quick.Controls.Add(b);
        }
        root.Controls.Add(quick);
        _customLabel.Text = "Posição personalizada";
        _customLabel.ForeColor = Color.DimGray;
        _customLabel.AutoSize = true;
        root.Controls.Add(_customLabel);
        _noScreens.Text = "Conecte um aparelho para posicionar a tela dele.";
        _noScreens.ForeColor = Color.DimGray;
        _noScreens.AutoSize = true;
        root.Controls.Add(_noScreens);
        return page;
    }

    private static FlowLayoutPanel TopDown() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 10.5f),
        AutoSize = true,
        Margin = new Padding(0, 12, 0, 4),
    };

    private static void StylePrimary(Button b)
    {
        b.BackColor = Cyan;
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.Padding = new Padding(8, 2, 8, 2);
    }

    // ---------------- Lista de aparelhos ----------------

    private void RebuildDevices()
    {
        // Só reconstrói quando algo mudou (evita piscar a cada atualização).
        string sig = string.Join("|", _app.Devices.Select(d => $"{d.Conn}:{d.State}:{d.Codec}:{d.Width}x{d.Height}:{_app.IsTrusted(d)}"))
                     + "#" + string.Join(",", _app.Trusted) + "#" + string.Join(",", _app.Blocked);
        if (sig == _devicesSignature) return;
        _devicesSignature = sig;

        _devicesList.SuspendLayout();
        foreach (Control c in _devicesList.Controls) c.Dispose();
        _devicesList.Controls.Clear();

        var pending = _app.Devices.Where(d => d.IsPending).ToList();
        var active = _app.Devices.Where(d => !d.IsPending).ToList();

        if (pending.Count > 0)
        {
            _devicesList.Controls.Add(SectionTitle("Aguardando autorização"));
            foreach (var d in pending)
                _devicesList.Controls.Add(DeviceRow(d, ("Aceitar", () => _app.Accept(d, false), true),
                    ("Confiar sempre", () => _app.Accept(d, true), false), ("Recusar", () => _app.Reject(d), false),
                    ("Bloquear", () => _app.Block(d), false)));
        }

        _devicesList.Controls.Add(SectionTitle("Conectados"));
        if (active.Count == 0)
            _devicesList.Controls.Add(new Label { Text = "Nenhum aparelho conectado. Abra o link da aba Conexão no aparelho.", ForeColor = Color.DimGray, AutoSize = true });
        foreach (var d in active)
            _devicesList.Controls.Add(DeviceRow(d, ("Desconectar", () => _app.Disconnect(d), false), ("Bloquear", () => _app.Block(d), false)));

        if (_app.Trusted.Count > 0)
        {
            _devicesList.Controls.Add(SectionTitle($"Aparelhos confiáveis ({_app.Trusted.Count})"));
            foreach (var s in _app.Trusted.ToList())
                _devicesList.Controls.Add(SavedRow(s, "Esquecer", () => _app.Forget(s)));
        }
        if (_app.Blocked.Count > 0)
        {
            _devicesList.Controls.Add(SectionTitle($"Aparelhos bloqueados ({_app.Blocked.Count})"));
            foreach (var s in _app.Blocked.ToList())
                _devicesList.Controls.Add(SavedRow(s, "Desbloquear", () => _app.Unblock(s)));
        }
        _devicesList.ResumeLayout();
    }

    private Control DeviceRow(DeviceInfo d, params (string Text, Action Action, bool Primary)[] actions)
    {
        var row = new Panel { Size = new Size(ContentWidth - 30, 64), BackColor = Color.FromArgb(244, 246, 248), Margin = new Padding(0, 0, 0, 6) };
        string detail = d.IsPending
            ? $"IP {d.Ip} · quer se conectar"
            : $"IP {d.Ip} · desde {d.Since:HH:mm} · {d.Codec}" + (d.Width > 0 ? $" · {d.Width}×{d.Height}" : "");
        row.Controls.Add(new Label
        {
            Text = (d.IsPending ? "⏳ " : "📱 ") + d.Name + (_app.IsTrusted(d) ? "  (confiável)" : ""),
            Font = new Font("Segoe UI Semibold", 10f),
            AutoSize = true,
            Location = new Point(10, 8),
        });
        row.Controls.Add(new Label { Text = detail, ForeColor = Color.DimGray, AutoSize = true, Location = new Point(10, 32) });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        foreach (var (text, action, primary) in actions.Reverse())
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4, 0, 0, 0) };
            if (primary) StylePrimary(b);
            if (text == "Bloquear") b.ForeColor = Color.Firebrick;
            b.Click += (_, _) => action();
            buttons.Controls.Add(b);
        }
        row.Controls.Add(buttons);
        buttons.Location = new Point(row.Width - buttons.PreferredSize.Width - 8, 16);
        return row;
    }

    private Control SavedRow(SavedDevice s, string actionText, Action action)
    {
        var row = new Panel { Size = new Size(ContentWidth - 30, 40), Margin = new Padding(0, 0, 0, 2) };
        row.Controls.Add(new Label { Text = $"{s.Name}  ·  IP {s.Ip}", AutoSize = true, Location = new Point(4, 10) });
        var b = new Button { Text = actionText, AutoSize = true };
        b.Click += (_, _) => action();
        row.Controls.Add(b);
        b.Location = new Point(row.Width - b.PreferredSize.Width - 8, 6);
        return row;
    }

    // ---------------- Pedido de autorização ----------------

    private void ShowNextApproval()
    {
        if (_approval != null) return;
        while (_approvalQueue.Count > 0)
        {
            var d = _approvalQueue.Dequeue();
            if (!_app.IsStillPending(d.Conn)) continue;

            _tray.ShowBalloonTip(4000, "Ronic Mirror", $"{d.Name} quer usar esta tela.", ToolTipIcon.Info);
            _approval = new ApprovalForm(d, Icon);
            _approval.ShowDialog();
            var form = _approval;
            _approval = null;

            if (!_app.IsStillPending(d.Conn)) continue; // o aparelho desistiu
            var current = _app.Devices.First(x => x.Conn == d.Conn);
            switch (form.Choice)
            {
                case ApprovalChoice.Accept: _app.Accept(current, form.Trust); break;
                case ApprovalChoice.Reject: _app.Reject(current); break;
                case ApprovalChoice.Block: _app.Block(current); break;
            }
            form.Dispose();
        }
    }

    // ---------------- Bandeja do sistema (segundo plano) ----------------

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_trayUrl);
        _trayPending.Click += (_, _) => { ShowWindow(); _tabs.SelectedTab = _tabDevices; };
        menu.Items.Add(_trayPending);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir Ronic Mirror", null, (_, _) => ShowWindow());
        menu.Items.Add("Copiar link", null, (_, _) => CopyUrl());
        // Montado na hora de abrir: um submenu por aparelho conectado.
        _trayPosition.DropDownItems.Add("(nenhum aparelho)");
        menu.Opening += (_, _) => BuildTrayPositions();
        menu.Items.Add(_trayPosition);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair do Ronic Mirror", null, (_, _) => ExitApp());

        _tray.Icon = Icon;
        _tray.Text = "Ronic Mirror";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // Fechar = esconder. O monitor virtual e o servidor continuam ativos.
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "Ronic Mirror continua rodando",
                    "O aparelho conectado segue funcionando. Use o ícone na bandeja para abrir ou sair.", ToolTipIcon.Info);
            }
            return;
        }
        _approval?.Close();
        _app.Dispose();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    // ---------------- Atualização da interface ----------------

    private void RefreshUi()
    {
        if (InvokeRequired) { BeginInvoke(RefreshUi); return; }

        // Conexão
        _url.Text = _app.Url;
        _trayUrl.Text = _app.Url;
        _others.Text = _app.OtherUrls.Length > 0 ? "Outros endereços: " + string.Join("  ·  ", _app.OtherUrls) : "";
        _others.Visible = _app.OtherUrls.Length > 0;
        if (_qrFor != _app.Url && _app.Url.StartsWith("http"))
        {
            _qrFor = _app.Url;
            _qr.Image?.Dispose();
            _qr.Image = MakeQr(_app.Url);
        }
        SetStatus(_serverStatus, "Servidor local", _app.ServerStatus, _app.ServerError);
        string screens = _app.Screens.Count == 0
            ? "Telas virtuais: nenhum aparelho conectado (cada aparelho aceito ganha a sua)"
            : "Telas virtuais: " + string.Join(" · ", _app.Screens.Select(s => $"{s.Name} {s.SizeLabel}"));
        SetStatus(_displayStatus, screens, _app.DisplayStatus, _app.DisplayError);
        _viewers.Text = _app.Viewers == 1 ? "📱 1 aparelho conectado" : $"📱 {_app.Viewers} aparelhos conectados";
        _viewers.ForeColor = _app.Viewers > 0 ? Color.SeaGreen : Color.DimGray;
        _driverPanel.Visible = _app.DriverMissing;
        _installDriver.Enabled = !_app.InstallingDriver;
        _installDriver.Text = _app.InstallingDriver ? "Instalando…" : "Instalar driver";

        // Aparelhos
        int pending = _app.Devices.Count(d => d.IsPending);
        int active = _app.Devices.Count - pending;
        _tabDevices.Text = pending > 0 ? $"Aparelhos ({pending} aguardando)" : active > 0 ? $"Aparelhos ({active})" : "Aparelhos";
        _requireApproval.Checked = _app.RequireApproval;
        RebuildDevices();
        // Fecha o pedido de autorização se o aparelho desistiu.
        if (_approval != null && !_app.IsStillPending(_approval.Conn)) _approval.Close();

        // Telas
        var current = SelectedScreen();
        var items = _app.Screens.Select(sc => new ScreenItem(sc)).ToArray();
        if (!_screenPicker.Items.Cast<ScreenItem>().Select(i => i.Screen).SequenceEqual(items.Select(i => i.Screen)))
        {
            _screenPicker.Items.Clear();
            _screenPicker.Items.AddRange(items);
        }
        if (current != null)
            _screenPicker.SelectedItem = _screenPicker.Items.Cast<ScreenItem>().FirstOrDefault(i => i.Screen == current);
        var placement = current != null ? _app.PlacementOf(current) : null;
        foreach (var (side, b) in _sideButtons)
        {
            b.Enabled = current != null;
            bool selected = placement != null && !placement.Custom && placement.Side == side;
            b.BackColor = selected ? Color.FromArgb(214, 245, 251) : SystemColors.Control;
        }
        _screenPicker.Visible = _app.Screens.Count > 1;
        _customLabel.Visible = placement?.Custom == true;
        _noScreens.Visible = _app.Screens.Count == 0;
        _arrangement.SelectedDevice = current?.Monitor.DeviceName;
        RefreshArrangement();

        // Bandeja
        _trayPending.Visible = pending > 0;
        _trayPending.Text = $"⚠ {pending} aparelho(s) aguardando autorização";
        _tray.Text = _app.Viewers > 0 ? $"Ronic Mirror — {_app.Viewers} aparelho(s)" : "Ronic Mirror";
    }

    private DeviceScreen? SelectedScreen() =>
        _app.Screens.FirstOrDefault(s => s.Monitor.DeviceName == _selectedScreen) ?? _app.Screens.FirstOrDefault();

    private sealed record ScreenItem(DeviceScreen Screen)
    {
        public override string ToString() => Screen.Name;
    }

    private void BuildTrayPositions()
    {
        _trayPosition.DropDownItems.Clear();
        foreach (var screen in _app.Screens)
        {
            var sub = new ToolStripMenuItem(screen.Name);
            var placement = _app.PlacementOf(screen);
            foreach (var (side, label) in new[] { (DisplaySide.Left, "Esquerda"), (DisplaySide.Right, "Direita"), (DisplaySide.Top, "Acima"), (DisplaySide.Bottom, "Abaixo") })
            {
                var item = new ToolStripMenuItem(label) { Checked = !placement.Custom && placement.Side == side };
                var sc = screen;
                item.Click += (_, _) => _app.SetPosition(sc, side);
                sub.DropDownItems.Add(item);
            }
            _trayPosition.DropDownItems.Add(sub);
        }
        if (_app.Screens.Count == 0) _trayPosition.DropDownItems.Add(new ToolStripMenuItem("Nenhum aparelho conectado") { Enabled = false });
        _trayPosition.DropDownItems.Add(new ToolStripSeparator());
        _trayPosition.DropDownItems.Add("Ajustar arrastando…", null, (_, _) => { ShowWindow(); _tabs.SelectedIndex = 2; });
    }

    private void RefreshArrangement()
    {
        if (_tabs.SelectedIndex == 2) _arrangement.SetDisplays(_app.DisplayLayout());
    }

    private static void SetStatus(Label label, string title, Status status, string? error)
    {
        label.Text = "● " + title + (status == Status.Error && error != null ? "\n   " + error : "");
        label.ForeColor = status switch
        {
            Status.Ok => Color.SeaGreen,
            Status.Waiting => Color.DarkGoldenrod,
            _ => Color.Firebrick,
        };
    }

    private void CopyUrl()
    {
        if (!_app.Url.StartsWith("http")) return;
        Clipboard.SetText(_app.Url);
        _copy.Text = "Copiado!";
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { _copy.Text = "Copiar link"; t.Dispose(); };
        t.Start();
    }

    private static Image MakeQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return Image.FromStream(new MemoryStream(png));
    }

    private static Icon LoadIcon()
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream("RonicMirror.ronic.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    private static Image? LoadLogo()
    {
        var s = typeof(MainForm).Assembly.GetManifestResourceStream("RonicMirror.logo.png");
        return s != null ? Image.FromStream(s) : null;
    }
}
