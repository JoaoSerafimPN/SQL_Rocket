namespace FerramentasDBA.UI.Controls;

/// <summary>
/// Navegação lateral estilo "sidebar moderna" com grupos expansíveis
/// (accordion), inspirada em ferramentas como o SQL Backup Manager.
/// Cada grupo representa uma categoria do menu (ex: Índice, Desempenho) e
/// cada item dentro do grupo é uma página navegável.
///
/// Controle puramente de UI/navegação — não referencia nenhuma classe de
/// negócio; ao ser clicado, apenas dispara <see cref="ItemSelecionado"/>
/// com a chave do item, deixando o formulário decidir o que fazer.
/// </summary>
public class SidebarNav : Panel
{
    /// <summary>Disparado quando um item folha (dentro de um grupo, ou item simples) é selecionado.</summary>
    public event EventHandler<string>? ItemSelecionado;

    private static readonly Color CorFundo = Color.FromArgb(24, 40, 72);        // navy
    private static readonly Color CorFundoHover = Color.FromArgb(34, 52, 88);
    private static readonly Color CorTextoGrupo = Color.White;
    private static readonly Color CorTextoItem = Color.FromArgb(201, 211, 224);
    private static readonly Color CorSelecionado = Color.FromArgb(47, 111, 237); // azul destaque

    private readonly FlowLayoutPanel _flow;
    private Button? _itemAtivo;

    public SidebarNav()
    {
        BackColor = CorFundo;
        AutoScroll = true;

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = CorFundo,
            Padding = new Padding(0, 8, 0, 8)
        };

        Controls.Add(_flow);
    }

    /// <summary>
    /// Adiciona um grupo expansível com uma lista de itens folha
    /// (chave interna, texto exibido). A chave é o que retorna no evento
    /// <see cref="ItemSelecionado"/> quando o item é clicado.
    /// </summary>
    public void AdicionarGrupo(string titulo, IEnumerable<(string Chave, string Texto)> itens)
    {
        var grupoContainer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = CorFundo,
            Margin = new Padding(0)
        };

        var itensPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = CorFundo,
            Visible = false,
            Margin = new Padding(0)
        };

        var cabecalho = new Button
        {
            Text = "▸  " + titulo,
            Width = 260,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            BackColor = CorFundo,
            ForeColor = CorTextoGrupo,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Margin = new Padding(0)
        };
        cabecalho.FlatAppearance.BorderSize = 0;
        cabecalho.FlatAppearance.MouseOverBackColor = CorFundoHover;

        cabecalho.Click += (_, _) =>
        {
            itensPanel.Visible = !itensPanel.Visible;
            cabecalho.Text = (itensPanel.Visible ? "▾  " : "▸  ") + titulo;
        };

        foreach (var (chave, texto) in itens)
        {
            var itemBtn = new Button
            {
                Text = texto,
                Width = 260,
                Height = 36,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(36, 0, 0, 0),
                BackColor = CorFundo,
                ForeColor = CorTextoItem,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                Cursor = Cursors.Hand,
                Margin = new Padding(0),
                Tag = chave
            };
            itemBtn.FlatAppearance.BorderSize = 0;
            itemBtn.FlatAppearance.MouseOverBackColor = CorFundoHover;

            itemBtn.Click += (_, _) =>
            {
                SelecionarItem(itemBtn);
                ItemSelecionado?.Invoke(this, chave);
            };

            itensPanel.Controls.Add(itemBtn);
        }

        grupoContainer.Controls.Add(cabecalho);
        grupoContainer.Controls.Add(itensPanel);
        _flow.Controls.Add(grupoContainer);
    }

    /// <summary>
    /// Adiciona um item de nível único, sem agrupamento (ex: "Sair").
    /// Se <paramref name="aoClicar"/> for informado, ele é chamado no lugar
    /// de disparar <see cref="ItemSelecionado"/> (útil para ações diretas,
    /// como encerrar a aplicação, que não navegam para uma página).
    /// </summary>
    public void AdicionarItemSimples(string chave, string texto, EventHandler? aoClicar = null)
    {
        var itemBtn = new Button
        {
            Text = texto,
            Width = 260,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            BackColor = CorFundo,
            ForeColor = CorTextoGrupo,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Margin = new Padding(0),
            Tag = chave
        };
        itemBtn.FlatAppearance.BorderSize = 0;
        itemBtn.FlatAppearance.MouseOverBackColor = CorFundoHover;

        itemBtn.Click += (_, _) =>
        {
            if (aoClicar is not null)
            {
                aoClicar(itemBtn, EventArgs.Empty);
                return;
            }

            SelecionarItem(itemBtn);
            ItemSelecionado?.Invoke(this, chave);
        };

        _flow.Controls.Add(itemBtn);
    }

    private void SelecionarItem(Button item)
    {
        if (_itemAtivo is not null)
        {
            _itemAtivo.BackColor = CorFundo;
        }

        item.BackColor = CorSelecionado;
        _itemAtivo = item;
    }
}
