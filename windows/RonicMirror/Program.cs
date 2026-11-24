namespace RonicMirror;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Uma instância por vez (o monitor virtual e a porta 3000 são únicos).
        using var mutex = new Mutex(true, @"Local\RonicMirror.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("O Ronic Mirror já está aberto. Procure o ícone na bandeja do sistema, perto do relógio.",
                "Ronic Mirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Coordenadas em pixels físicos: essencial para capturar o monitor certo com escala (DPI) diferente.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
