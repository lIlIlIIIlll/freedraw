using System.Drawing;
using System.Windows.Forms;

namespace Freedraw.App;

/// <summary>
/// Icone de bandeja. Como o overlay e click-through e nao tem janela visivel,
/// e por aqui que o app fica alcancavel -- sem ele nao haveria como sair a nao
/// ser pelo gerenciador de tarefas.
///
/// O icone vem embutido no proprio .exe (Assets/freedraw.ico, gerado por
/// tools/make_icon.py), para o app continuar sendo um unico arquivo.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly Icon _appIcon;

    public event Action? ClearRequested;
    public event Action? UndoRequested;
    public event Action? ToggleRequested;
    public event Action? QuitRequested;
    public event Action? RestartRequested;

    public TrayIcon()
    {
        _appIcon = LoadAppIcon();

        _toggleItem = new ToolStripMenuItem("Desenho ativado (Ctrl+Alt+D)")
        {
            Checked = true,
            CheckOnClick = false,
        };
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();

        // Escondido ate o updater trocar o exe: so entao reiniciar faz sentido.
        _restartItem = new ToolStripMenuItem { Visible = false };
        _restartItem.Click += (_, _) => RestartRequested?.Invoke();

        var menu = new ContextMenuStrip();
        // Rotulo de versao no topo: e a primeira coisa a perguntar quando
        // "nao atualizou no meu PC".
        menu.Items.Add(new ToolStripMenuItem(VersionLabel()) { Enabled = false });
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Desfazer (Ctrl+Alt+Z)", null, (_, _) => UndoRequested?.Invoke());
        menu.Items.Add("Limpar (Ctrl+Alt+C)", null, (_, _) => ClearRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair (Ctrl+Alt+Q)", null, (_, _) => QuitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Freedraw",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        _toggleItem.Text = enabled
            ? "Desenho ativado (Ctrl+Alt+D)"
            : "Desenho DESATIVADO (Ctrl+Alt+D)";
        _toggleItem.Checked = enabled;
    }

    /// <summary>Avisa uma vez que a versao nova ja esta no disco.</summary>
    public void ShowUpdateReady(Version version)
    {
        string v = version.ToString(3);
        _restartItem.Text = $"Reiniciar para atualizar (v{v})";
        _restartItem.Visible = true;
        _icon.ShowBalloonTip(5000, "Freedraw atualizado",
            $"A v{v} foi baixada e entra na proxima vez que o Freedraw abrir. "
            + "Para usar agora: menu da bandeja > Reiniciar para atualizar.",
            ToolTipIcon.Info);
    }

    private static string VersionLabel() =>
        $"Freedraw v{Updater.CurrentVersion.ToString(3)}"
        + (Updater.Enabled ? "" : " (build local, sem auto-update)");

    /// <summary>Tooltip da bandeja: limite de 63 chars, o Windows trunca alem disso.</summary>
    public void SetStatus(string status)
    {
        _icon.Text = status.Length <= 63 ? status : status[..63];
    }

    /// <summary>
    /// Pede ao .ico o quadro no tamanho de icone pequeno do sistema (16 a 100%,
    /// 24 a 150%...). Sem o tamanho, o Icon pega o quadro de 32 e o Windows o
    /// reduz -- e o traco fino do desenho some na reducao.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Freedraw.App.freedraw.ico")
            ?? throw new InvalidOperationException("recurso freedraw.ico ausente do assembly");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
