using FerramentasDBA.UI.Assets;

namespace FerramentasDBA.UI.Forms;

partial class Menu_Raiz
{
    private System.ComponentModel.IContainer components = null;

    private FerramentasDBA.UI.Controls.SidebarNav sidebarNav = null!;
    private Panel pnlConteudo = null!;
    private Panel pnlCabecalhoConteudo = null!;
    private Label lblTituloPagina = null!;
    private Panel pnlCorpoConteudo = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Monta o "shell" visual da janela principal: sidebar (navegação) à
    /// esquerda + painel de conteúdo à direita (cabeçalho com o título da
    /// página atual + área onde as páginas são inseridas dinamicamente).
    /// A árvore de itens da sidebar é montada em MontarMenuLateral() (ver
    /// Menu_Raiz.cs), não aqui.
    /// </summary>
    private void InitializeComponent()
    {
        this.sidebarNav = new FerramentasDBA.UI.Controls.SidebarNav();
        this.pnlConteudo = new Panel();
        this.pnlCabecalhoConteudo = new Panel();
        this.lblTituloPagina = new Label();
        this.pnlCorpoConteudo = new Panel();
        this.SuspendLayout();

        var corSidebar = Color.FromArgb(24, 40, 72);
        var corFundo = Color.FromArgb(244, 246, 250);

        // ---------------- Sidebar ----------------
        var pnlSidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 260,
            BackColor = corSidebar
        };

        var pnlTituloApp = new Panel
        {
            Dock = DockStyle.Top,
            Height = 84,
            BackColor = corSidebar
        };
        var lblApp = new Label
        {
            Text = "SQL Rocket",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 20)
        };
        var lblAppSub = new Label
        {
            Text = "Manutenção de SQL Server",
            ForeColor = Color.FromArgb(150, 165, 190),
            Font = new Font("Segoe UI", 8.5F),
            AutoSize = true,
            Location = new Point(20, 50)
        };
        pnlTituloApp.Controls.Add(lblApp);
        pnlTituloApp.Controls.Add(lblAppSub);

        this.sidebarNav.Dock = DockStyle.Fill;

        // IMPORTANTE: o controle com Dock=Fill precisa ser adicionado ANTES
        // dos controles Dock=Top/Left/Right do mesmo contêiner. Adicionar na
        // ordem errada faz o Fill ocupar toda a área (por baixo dos outros),
        // que ficam por cima cobrindo parte do conteúdo.
        pnlSidebar.Controls.Add(this.sidebarNav);
        pnlSidebar.Controls.Add(pnlTituloApp);

        // ---------------- Conteúdo (lado direito) ----------------
        this.pnlCabecalhoConteudo.Dock = DockStyle.Top;
        this.pnlCabecalhoConteudo.Height = 64;
        this.pnlCabecalhoConteudo.BackColor = Color.White;

        this.lblTituloPagina.Text = "Bem-vindo";
        this.lblTituloPagina.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
        this.lblTituloPagina.ForeColor = Color.FromArgb(30, 41, 59);
        this.lblTituloPagina.AutoSize = true;
        this.lblTituloPagina.Location = new Point(24, 18);
        this.pnlCabecalhoConteudo.Controls.Add(this.lblTituloPagina);

        this.pnlCorpoConteudo.Dock = DockStyle.Fill;
        this.pnlCorpoConteudo.BackColor = corFundo;
        this.pnlCorpoConteudo.Padding = new Padding(24);

        this.pnlConteudo.Dock = DockStyle.Fill;
        this.pnlConteudo.Controls.Add(this.pnlCorpoConteudo);
        this.pnlConteudo.Controls.Add(this.pnlCabecalhoConteudo);

        // ---------------- Form ----------------
        // Mesmo motivo do comentário acima: Fill (pnlConteudo) primeiro,
        // Left (pnlSidebar) depois.
        this.Controls.Add(this.pnlConteudo);
        this.Controls.Add(pnlSidebar);
        this.ClientSize = new Size(1200, 760);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.WindowState = FormWindowState.Maximized;
        this.Text = "SQL Rocket";
        this.BackColor = corFundo;
        // Marca "SQL Rocket" (pedido do usuário) — ícone da janela principal
        // (barra de título/Alt+Tab/barra de tarefas); ver Assets/RecursosVisuais.cs.
        this.Icon = RecursosVisuais.CarregarIconeApp();
        this.ResumeLayout(false);
    }
}
