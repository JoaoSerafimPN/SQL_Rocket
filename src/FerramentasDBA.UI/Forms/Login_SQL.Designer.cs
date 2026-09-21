using FerramentasDBA.UI.Assets;

namespace FerramentasDBA.UI.Forms;

partial class Login_SQL
{
    private System.ComponentModel.IContainer components = null;

    // --- Controles da tela ---
    private Panel pnlCartao = null!;
    private TextBox txtServidor = null!;
    private TextBox txtBancoDados = null!;
    private TextBox txtUsuario = null!;
    private TextBox txtSenha = null!;
    private Button btnConectar = null!;
    private Button btnCancelar = null!;
    // Exposto ao Login_SQL.cs (montagem do ConnectionSettings) e ajustado
    // automaticamente conforme o servidor digitado — ver Login_SQL.
    internal CheckBox chkConfiarCertificado = null!;
    // Conexões salvas e modo de autenticação — internos porque Login_SQL.cs
    // (a lógica da tela) lê e escreve neles.
    internal ComboBox cmbConexoesSalvas = null!;
    internal Button btnSalvarConexao = null!;
    internal Button btnExcluirConexao = null!;
    internal ComboBox cmbAutenticacao = null!;
    internal CheckBox chkGuardarSenha = null!;
    internal Label lblUsuario = null!;
    internal Label lblSenha = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Visual "cartão" centralizado, na mesma paleta usada na Menu_Raiz
    /// (navy/azul de destaque sobre fundo neutro), no lugar do layout
    /// antigo com rótulos ao lado dos campos.
    /// </summary>
    private void InitializeComponent()
    {
        this.pnlCartao = new Panel();
        this.txtServidor = new TextBox();
        this.txtBancoDados = new TextBox();
        this.txtUsuario = new TextBox();
        this.txtSenha = new TextBox();
        this.btnConectar = new Button();
        this.btnCancelar = new Button();
        this.chkConfiarCertificado = new CheckBox();
        this.cmbConexoesSalvas = new ComboBox();
        this.btnSalvarConexao = new Button();
        this.btnExcluirConexao = new Button();
        this.cmbAutenticacao = new ComboBox();
        this.chkGuardarSenha = new CheckBox();
        this.SuspendLayout();

        var corAccent = Color.FromArgb(47, 111, 237);
        var corTitulo = Color.FromArgb(24, 40, 72);
        var corTextoSecundario = Color.FromArgb(110, 120, 140);
        var corFundo = Color.FromArgb(244, 246, 250);

        // ---------------- Cartão central ----------------
        // Altura aumentada de 400 para 460 (mais 60px) para caber o novo
        // campo "Banco de dados" — ver comentário em txtBancoDados abaixo.
        // Altura acompanha o conteúdo: título/logo, conexões salvas, servidor,
        // autenticação, banco, usuário, senha, as duas caixas de opção e os
        // dois botões (ver as coordenadas de cada bloco abaixo).
        this.pnlCartao.Size = new Size(360, 600);
        this.pnlCartao.BackColor = Color.White;
        this.pnlCartao.BorderStyle = BorderStyle.FixedSingle;

        var lblTitulo = new Label
        {
            Text = "SQL Rocket",
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = corTitulo,
            AutoSize = true,
            Location = new Point(28, 28)
        };
        var lblSubtitulo = new Label
        {
            Text = "Conectar ao SQL Server",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = corTextoSecundario,
            AutoSize = true,
            Location = new Point(28, 60)
        };

        // Marca "SQL Rocket" (pedido do usuário, com screenshot marcando o
        // espaço em branco ao lado do título): logo retangular (com
        // wordmark), alinhado com o bloco de título/subtítulo, sem invadir o
        // campo "SERVIDOR" logo abaixo (que começa em y=104). Zoom preserva
        // a proporção original da imagem (não distorce).
        var logoLogin = new PictureBox
        {
            Image = RecursosVisuais.CarregarLogoMarca(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(224, 20),
            Size = new Size(98, 84)
        };

        // ---------------- Conexões salvas ----------------
        // Guardadas no perfil do usuário (%APPDATA%\SQL Rocket), com a senha
        // cifrada pelo Windows — ver Infraestrutura/ConexoesSalvas. Existe
        // porque a tela deixou de trazer servidor/usuário/senha fixos no
        // código (eram segredo em repositório público), e sem isso a conexão
        // do dia a dia teria que ser redigitada a cada abertura.
        var lblConexoesSalvas = new Label
        {
            Text = "CONEXÃO SALVA",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = corTextoSecundario,
            AutoSize = true,
            Location = new Point(28, 104)
        };
        this.cmbConexoesSalvas.Location = new Point(28, 124);
        this.cmbConexoesSalvas.Size = new Size(152, 26);
        this.cmbConexoesSalvas.DropDownStyle = ComboBoxStyle.DropDownList;

        this.btnSalvarConexao.Text = "Salvar";
        this.btnSalvarConexao.Location = new Point(184, 124);
        this.btnSalvarConexao.Size = new Size(72, 26);
        this.btnSalvarConexao.FlatStyle = FlatStyle.Flat;
        this.btnSalvarConexao.FlatAppearance.BorderColor = corTextoSecundario;
        this.btnSalvarConexao.BackColor = Color.White;
        this.btnSalvarConexao.ForeColor = corTitulo;
        this.btnSalvarConexao.Font = new Font("Segoe UI", 8.5F);
        this.btnSalvarConexao.Cursor = Cursors.Hand;

        this.btnExcluirConexao.Text = "Excluir";
        this.btnExcluirConexao.Location = new Point(260, 124);
        this.btnExcluirConexao.Size = new Size(72, 26);
        this.btnExcluirConexao.FlatStyle = FlatStyle.Flat;
        this.btnExcluirConexao.FlatAppearance.BorderColor = corTextoSecundario;
        this.btnExcluirConexao.BackColor = Color.White;
        this.btnExcluirConexao.ForeColor = corTextoSecundario;
        this.btnExcluirConexao.Font = new Font("Segoe UI", 8.5F);
        this.btnExcluirConexao.Cursor = Cursors.Hand;

        // Campo: Servidor
        var lblServidor = new Label
        {
            Text = "SERVIDOR",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = corTextoSecundario,
            AutoSize = true,
            Location = new Point(28, 162)
        };
        this.txtServidor.Location = new Point(28, 182);
        this.txtServidor.Size = new Size(304, 26);
        this.txtServidor.BorderStyle = BorderStyle.FixedSingle;
        this.txtServidor.PlaceholderText = @"Ex: localhost\SQLEXPRESS";
        // Único campo que ainda mantém um valor padrão (não é segredo, só
        // conveniência para quem testa num SQL Server local) — Usuário e
        // Senha tiveram seus valores padrão removidos antes de publicar o
        // repositório no GitHub como público (ver comentários abaixo). Os 3
        // campos continuam editáveis normalmente antes de clicar em "Conectar".
        this.txtServidor.Text = "localhost";

        // Campo: Banco de dados (NOVO — pedido do usuário após erro de login
        // em Azure SQL Database: "The server principal ... is not able to
        // access the database 'master' under the current security context.
        // Cannot open user default database. Login failed."). Muitos logins
        // de Azure SQL Database são "contained database users", só válidos
        // dentro de UM banco específico — sem informar esse banco aqui, a
        // conexão tenta abrir no banco padrão do login (com frequência
        // "master"), o que falha para esse tipo de login. Campo opcional
        // (fica em branco = comportamento antigo, deixa o SQL Server decidir
        // o banco padrão do login — normal para logins de servidor local).
        // ---------------- Autenticação ----------------
        // O modelo (ConnectionSettings.TipoAutenticacao) sempre suportou
        // autenticação do Windows, mas a tela fixava autenticação do SQL
        // Server — então a ferramenta simplesmente não conectava em servidor
        // que só aceita conta de domínio. Escolhendo "Windows", os campos de
        // usuário/senha são desabilitados (a identidade vem da sessão do
        // Windows) — ver AplicarModoAutenticacao em Login_SQL.
        var lblAutenticacao = new Label
        {
            Text = "AUTENTICAÇÃO",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = corTextoSecundario,
            AutoSize = true,
            Location = new Point(28, 220)
        };
        this.cmbAutenticacao.Location = new Point(28, 240);
        this.cmbAutenticacao.Size = new Size(304, 26);
        this.cmbAutenticacao.DropDownStyle = ComboBoxStyle.DropDownList;

        var lblBancoDados = new Label
        {
            Text = "BANCO DE DADOS (opcional)",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = corTextoSecundario,
            AutoSize = true,
            Location = new Point(28, 278)
        };
        this.txtBancoDados.Location = new Point(28, 298);
        this.txtBancoDados.Size = new Size(304, 26);
        this.txtBancoDados.BorderStyle = BorderStyle.FixedSingle;
        this.txtBancoDados.PlaceholderText = "Ex: master — obrigatório p/ maioria dos logins do Azure SQL";
        // Tooltip com a explicação completa (não cabe como PlaceholderText
        // sem cortar) — aparece ao passar o mouse sobre o campo.
        var dicaBancoDados = new ToolTip();
        dicaBancoDados.SetToolTip(this.txtBancoDados,
            "Deixe em branco para usar o banco padrão do login (comportamento antigo, " +
            "normal em servidores locais). No Azure SQL Database, a maioria dos logins " +
            "é um \"contained database user\" válido só dentro de UM banco específico — " +
            "informe aqui o nome desse banco, senão a conexão tenta abrir no banco " +
            "padrão do login (geralmente \"master\") e falha com \"Login failed\".");

        // Campo: Usuário
        this.lblUsuario = new Label
        {
            Text = "USUÁRIO",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = corTextoSecundario,
            AutoSize = true,
            Location = new Point(28, 336)
        };
        this.txtUsuario.Location = new Point(28, 356);
        this.txtUsuario.Size = new Size(304, 26);
        this.txtUsuario.BorderStyle = BorderStyle.FixedSingle;
        this.txtUsuario.PlaceholderText = "Ex: supersa";
        // Removido o valor padrão fixo (era um usuário de teste real) antes de
        // publicar o repositório no GitHub como público — o campo fica em
        // branco, o usuário digita na hora de conectar.

        // Campo: Senha
        this.lblSenha = new Label
        {
            Text = "SENHA",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = corTextoSecundario,
            AutoSize = true,
            Location = new Point(28, 394)
        };
        this.txtSenha.Location = new Point(28, 414);
        this.txtSenha.Size = new Size(304, 26);
        this.txtSenha.BorderStyle = BorderStyle.FixedSingle;
        this.txtSenha.UseSystemPasswordChar = true;
        // Removido o valor padrão fixo (era uma senha real de teste) antes de
        // publicar o repositório no GitHub como público — nunca deixar
        // segredo/senha em texto no código-fonte de um repositório público.

        // Confiar no certificado do servidor (TrustServerCertificate).
        // Desmarcado = o certificado do servidor é validado, que é o correto
        // para servidores remotos e para Azure SQL. Marcado = aceita
        // certificado autoassinado, necessário na maioria das instâncias
        // locais. Login_SQL marca/desmarca sozinho conforme o servidor
        // digitado, até o usuário mexer na opção manualmente.
        this.chkConfiarCertificado.Text = "Confiar no certificado do servidor";
        this.chkConfiarCertificado.Location = new Point(28, 468);
        this.chkConfiarCertificado.Size = new Size(304, 20);
        this.chkConfiarCertificado.Font = new Font("Segoe UI", 8F);
        this.chkConfiarCertificado.ForeColor = corTextoSecundario;

        // Botão Conectar (ação primária, cheio)
        this.btnConectar.Text = "Conectar";
        this.btnConectar.Location = new Point(28, 496);
        this.btnConectar.Size = new Size(304, 40);
        this.btnConectar.FlatStyle = FlatStyle.Flat;
        this.btnConectar.FlatAppearance.BorderSize = 0;
        this.btnConectar.BackColor = corAccent;
        this.btnConectar.ForeColor = Color.White;
        this.btnConectar.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        this.btnConectar.Cursor = Cursors.Hand;
        this.btnConectar.Click += new EventHandler(this.btnConectar_Click);

        // Botão Cancelar (secundário, discreto)
        this.btnCancelar.Text = "Cancelar";
        this.btnCancelar.Location = new Point(28, 542);
        this.btnCancelar.Size = new Size(304, 34);
        this.btnCancelar.FlatStyle = FlatStyle.Flat;
        this.btnCancelar.FlatAppearance.BorderSize = 0;
        this.btnCancelar.BackColor = Color.White;
        this.btnCancelar.ForeColor = corTextoSecundario;
        this.btnCancelar.Font = new Font("Segoe UI", 9.5F);
        this.btnCancelar.Cursor = Cursors.Hand;
        this.btnCancelar.Click += new EventHandler(this.btnCancelar_Click);

        // Guardar a senha junto com a conexão salva — desmarcado por padrão:
        // gravar senha, mesmo cifrada, tem que ser escolha consciente. Sem
        // marcar, a conexão guarda só servidor/usuário e a senha é digitada
        // na hora.
        this.chkGuardarSenha.Text = "Guardar a senha desta conexão";
        this.chkGuardarSenha.Location = new Point(28, 444);
        this.chkGuardarSenha.Size = new Size(304, 20);
        this.chkGuardarSenha.Font = new Font("Segoe UI", 8F);
        this.chkGuardarSenha.ForeColor = corTextoSecundario;

        // O texto curto cabe em uma linha; a explicação completa (longa demais
        // para o rótulo) fica na dica de tela.
        var dicas = new ToolTip();
        dicas.SetToolTip(this.chkConfiarCertificado,
            "Desmarcado, o certificado do servidor é validado — é o correto para servidores " +
            "remotos e para Azure SQL. Marque apenas em instância local/com certificado " +
            "autoassinado, que é quando a validação falharia sem motivo. A opção é marcada " +
            "automaticamente quando o servidor digitado é local.");
        dicas.SetToolTip(this.chkGuardarSenha,
            "A senha é cifrada pelo Windows (DPAPI) e gravada no seu perfil de usuário — só a " +
            "sua conta, nesta máquina, consegue lê-la. Deixe desmarcado para digitar a senha a " +
            "cada conexão.");
        dicas.SetToolTip(this.cmbConexoesSalvas,
            "Conexões que você salvou. Escolher uma preenche os campos abaixo.");

        this.pnlCartao.Controls.Add(lblConexoesSalvas);
        this.pnlCartao.Controls.Add(this.cmbConexoesSalvas);
        this.pnlCartao.Controls.Add(this.btnSalvarConexao);
        this.pnlCartao.Controls.Add(this.btnExcluirConexao);
        this.pnlCartao.Controls.Add(lblAutenticacao);
        this.pnlCartao.Controls.Add(this.cmbAutenticacao);
        this.pnlCartao.Controls.Add(this.chkGuardarSenha);
        this.pnlCartao.Controls.Add(lblTitulo);
        this.pnlCartao.Controls.Add(lblSubtitulo);
        this.pnlCartao.Controls.Add(logoLogin);
        this.pnlCartao.Controls.Add(lblServidor);
        this.pnlCartao.Controls.Add(this.txtServidor);
        this.pnlCartao.Controls.Add(lblBancoDados);
        this.pnlCartao.Controls.Add(this.txtBancoDados);
        this.pnlCartao.Controls.Add(this.lblUsuario);
        this.pnlCartao.Controls.Add(this.txtUsuario);
        this.pnlCartao.Controls.Add(this.lblSenha);
        this.pnlCartao.Controls.Add(this.txtSenha);
        this.pnlCartao.Controls.Add(this.chkConfiarCertificado);
        this.pnlCartao.Controls.Add(this.btnConectar);
        this.pnlCartao.Controls.Add(this.btnCancelar);

        // ---------------- Form ----------------
        this.AcceptButton = this.btnConectar;
        this.CancelButton = this.btnCancelar;
        // Altura aumentada de 480 para 540 (mesmos +60px do pnlCartao) para
        // manter as mesmas margens ao redor do cartão centralizado.
        // Acompanha a altura do cartão, mantendo a mesma margem ao redor.
        this.ClientSize = new Size(480, 680);
        this.BackColor = corFundo;
        // Pedido do usuário: a tela de login NÃO tem mais imagem de fundo
        // atrás do cartão (removida — versão anterior mostrava o foguete
        // "atravessando" o cartão via Form.BackgroundImage). A marca
        // continua presente no pequeno logo dentro do próprio cartão
        // (ver "logoLogin" acima) e no ícone da janela logo abaixo.
        this.Icon = RecursosVisuais.CarregarIconeApp();
        this.Controls.Add(this.pnlCartao);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Text = "SQL Rocket - Login";
        this.ResumeLayout(false);

        // Centraliza o cartão no form (form tem tamanho fixo, não é redimensionável).
        this.pnlCartao.Location = new Point(
            (this.ClientSize.Width - this.pnlCartao.Width) / 2,
            (this.ClientSize.Height - this.pnlCartao.Height) / 2);
    }
}
