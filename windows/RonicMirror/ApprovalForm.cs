using System.Drawing;

namespace RonicMirror;

internal enum ApprovalChoice { None, Accept, Reject, Block }

/// <summary>Janela "Aparelho X quer usar esta tela" com Aceitar / Recusar / Bloquear.</summary>
internal sealed class ApprovalForm : Form
{
    public ApprovalChoice Choice { get; private set; } = ApprovalChoice.None;
    public bool Trust => _trust.Checked;
    public string Conn { get; }

    private readonly CheckBox _trust = new()
    {
        Text = "Confiar neste aparelho (não perguntar de novo)",
        Checked = true,
        AutoSize = true,
    };

    public ApprovalForm(DeviceInfo device, Icon? icon)
    {
        Conn = device.Conn;
        Text = "Ronic Mirror — novo aparelho";
        Icon = icon;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        root.Controls.Add(new Label
        {
            Text = $"{device.Name} quer usar esta tela",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        root.Controls.Add(new Label
        {
            Text = $"Endereço na rede: {device.Ip}\nSó aceite se você reconhece este aparelho.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 12),
        });
        root.Controls.Add(_trust);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(MakeButton("Aceitar", ApprovalChoice.Accept, primary: true));
        buttons.Controls.Add(MakeButton("Recusar", ApprovalChoice.Reject));
        buttons.Controls.Add(MakeButton("Bloquear", ApprovalChoice.Block));
        root.Controls.Add(buttons);
        Controls.Add(root);
    }

    private Button MakeButton(string text, ApprovalChoice choice, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 0) };
        if (primary)
        {
            b.BackColor = Color.FromArgb(0, 180, 216);
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            AcceptButton = b;
        }
        b.Click += (_, _) => { Choice = choice; Close(); };
        return b;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }
}
