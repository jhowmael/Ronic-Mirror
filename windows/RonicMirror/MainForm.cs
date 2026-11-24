using System.Drawing;
using QRCoder;

namespace RonicMirror;

/// <summary>
/// Janela principal: link, QR code, Copiar, Esquerda/Direita e status.
/// Ao fechar, o app continua rodando na bandeja do sistema (perto do relógio).
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Cyan = Color.FromArgb(0, 180, 216);

    private readonly AppController _app = new();
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayUrl = new() { Enabled = false };
    private readonly ToolStripMenuItem _trayLeft = new("Esquerda");
    private readonly ToolStripMenuItem _trayRight = new("Direita");

    private readonly Label _url = new();
    private readonly Label _others = new();
    private readonly PictureBox _qr = new();
    private readonly Button _copy = new();
    private readonly RadioButton _left = new();
    private readonly RadioButton _right = new();
    private readonly Label _serverStatus = new();
    private readonly Label _displayStatus = new();
    private readonly Label _viewers = new();
    private readonly Panel _driverPanel = new();
    private readonly Label _driverText = new();
    private readonly Button _installDriver = new();
    private readonly Button _retry = new();

    private string _qrFor = "";
    private bool _exiting;
    private bool _trayHintShown;

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
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);

        BuildLayout();
        BuildTray();

        _app.Changed += RefreshUi;
        Load += (_, _) => { _app.Start(); RefreshUi(); };
    }

    // ---------------- Layout ----------------

    private void BuildLayout()
    {
        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        Controls.Add(root);
        const int width = 440;

        // Cabeçalho
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
        header.Controls.Add(new PictureBox
        {
            Image = LoadLogo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(52, 52), Margin = new Padding(0, 0, 12, 0),
        });
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        titles.Controls.Add(new Label { Text = "Ronic Mirror", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true });
        titles.Controls.Add(new Label { Text = "Use o iPad como monitor estendido via Wi-Fi", ForeColor = Color.DimGray, AutoSize = true });
        header.Controls.Add(titles);
        root.Controls.Add(header);

        // Aviso do driver
        _driverPanel.BackColor = Color.FromArgb(255, 244, 229);
        _driverPanel.Size = new Size(width, 118);
        _driverPanel.Margin = new Padding(0, 0, 0, 14);
        _driverPanel.Padding = new Padding(10);
        _driverText.Text = "Para criar o monitor virtual, o Windows precisa de um driver de vídeo virtual (Parsec VDD, gratuito e assinado). " +
                           "Clique em Instalar driver e confirme o pedido de administrador. Isso só é feito uma vez.";
        _driverText.MaximumSize = new Size(width - 20, 0);
        _driverText.AutoSize = true;
        _driverText.Location = new Point(10, 8);
        _installDriver.Text = "Instalar driver";
        _installDriver.AutoSize = true;
        _installDriver.Location = new Point(10, 78);
        _installDriver.Click += async (_, _) => await _app.InstallDriverAsync();
        _retry.Text = "Tentar novamente";
        _retry.AutoSize = true;
        _retry.Location = new Point(130, 78);
        _retry.Click += async (_, _) => await _app.CreateMonitorAsync();
        _driverPanel.Controls.AddRange(new Control[] { _driverText, _installDriver, _retry });
        root.Controls.Add(_driverPanel);

        // 1. Link
        root.Controls.Add(SectionTitle("1. No iPad, abra no Safari"));
        var linkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        _qr.Size = new Size(110, 110);
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.Margin = new Padding(0, 0, 14, 0);
        linkRow.Controls.Add(_qr);
        var linkCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        _url.Font = new Font("Consolas", 14f, FontStyle.Bold);
        _url.AutoSize = true;
        _url.Margin = new Padding(0, 8, 0, 8);
        linkCol.Controls.Add(_url);
        _copy.Text = "Copiar link";
        _copy.AutoSize = true;
        _copy.BackColor = Cyan;
        _copy.ForeColor = Color.White;
        _copy.FlatStyle = FlatStyle.Flat;
        _copy.FlatAppearance.BorderSize = 0;
        _copy.Padding = new Padding(8, 2, 8, 2);
        _copy.Click += (_, _) => CopyUrl();
        linkCol.Controls.Add(_copy);
        linkCol.Controls.Add(new Label { Text = "Ou aponte a câmera do iPad para o QR code.", ForeColor = Color.DimGray, AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        linkRow.Controls.Add(linkCol);
        root.Controls.Add(linkRow);
        _others.ForeColor = Color.DimGray;
        _others.AutoSize = true;
        _others.MaximumSize = new Size(width, 0);
        root.Controls.Add(_others);

        // 2. Posição
        root.Controls.Add(SectionTitle("2. Posição do iPad em relação ao PC"));
        var sideRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _left.Text = "Esquerda";
        _left.AutoSize = true;
        _right.Text = "Direita";
        _right.AutoSize = true;
        _left.CheckedChanged += (_, _) => { if (_left.Checked) _app.SetPosition(DisplaySide.Left); };
        _right.CheckedChanged += (_, _) => { if (_right.Checked) _app.SetPosition(DisplaySide.Right); };
        sideRow.Controls.Add(_left);
        sideRow.Controls.Add(_right);
        root.Controls.Add(sideRow);

        // Status
        root.Controls.Add(SectionTitle("Status"));
        foreach (var l in new[] { _serverStatus, _displayStatus, _viewers })
        {
            l.AutoSize = true;
            l.MaximumSize = new Size(width, 0);
            l.Margin = new Padding(0, 2, 0, 2);
            root.Controls.Add(l);
        }

        root.Controls.Add(new Label
        {
            Text = "ⓘ Ao fechar esta janela, o Ronic Mirror continua rodando na bandeja do sistema (perto do relógio). " +
                   "Para encerrar, clique com o botão direito no ícone e escolha Sair.",
            ForeColor = Color.DimGray,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 14, 0, 0),
        });
    }

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 10.5f),
        AutoSize = true,
        Margin = new Padding(0, 12, 0, 4),
    };

    // ---------------- Bandeja do sistema (segundo plano) ----------------

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_trayUrl);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir Ronic Mirror", null, (_, _) => ShowWindow());
        menu.Items.Add("Copiar link", null, (_, _) => CopyUrl());
        var position = new ToolStripMenuItem("Posição do iPad");
        _trayLeft.Click += (_, _) => _app.SetPosition(DisplaySide.Left);
        _trayRight.Click += (_, _) => _app.SetPosition(DisplaySide.Right);
        position.DropDownItems.AddRange(new ToolStripItem[] { _trayLeft, _trayRight });
        menu.Items.Add(position);
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
                    "O iPad segue funcionando. Use o ícone na bandeja para abrir ou sair.", ToolTipIcon.Info);
            }
            return;
        }
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

        _left.Checked = _app.Position == DisplaySide.Left;
        _right.Checked = _app.Position == DisplaySide.Right;
        _trayLeft.Checked = _left.Checked;
        _trayRight.Checked = _right.Checked;

        SetStatus(_serverStatus, "Servidor local", _app.ServerStatus, _app.ServerError);
        SetStatus(_displayStatus, $"Monitor virtual {_app.Resolution}", _app.DisplayStatus, _app.DisplayError);
        _viewers.Text = (_app.Viewers == 1 ? "📱 1 iPad conectado" : $"📱 {_app.Viewers} iPads conectados");
        _viewers.ForeColor = _app.Viewers > 0 ? Color.SeaGreen : Color.DimGray;

        _driverPanel.Visible = _app.DriverMissing;
        _installDriver.Enabled = !_app.InstallingDriver;
        _installDriver.Text = _app.InstallingDriver ? "Instalando…" : "Instalar driver";

        _tray.Text = _app.Viewers > 0 ? $"Ronic Mirror — {_app.Viewers} iPad(s)" : "Ronic Mirror";
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
