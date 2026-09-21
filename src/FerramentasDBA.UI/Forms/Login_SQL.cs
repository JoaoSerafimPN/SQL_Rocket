using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;

namespace FerramentasDBA.UI.Forms;

/// <summary>
/// Tela de login/conexão com o SQL Server.
/// Responsável apenas por coletar servidor/banco/usuário/senha e delegar a
/// validação para a camada de negócio (Conectar_SQL). Nenhuma query T-SQL
/// é escrita aqui — apenas orquestração de UI.
///
/// Suporta autenticação do SQL Server e do Windows (combo "AUTENTICAÇÃO"), e
/// guarda conexões no perfil do usuário — com a senha cifrada pelo Windows —
/// para não exigir que servidor/usuário/senha sejam redigitados a cada
/// abertura (ver Infraestrutura/ConexoesSalvas).
/// </summary>
public partial class Login_SQL : Form
{
    private readonly Conectar_SQL _conectarSql = new();

    /// <summary>
    /// Vira true assim que o usuário mexe na caixa "Confiar no certificado do
    /// servidor" — a partir daí a escolha dele manda, e o ajuste automático
    /// pelo nome do servidor para de sobrescrevê-la.
    /// </summary>
    private bool _usuarioEscolheuConfiancaCertificado;

    public Login_SQL()
    {
        InitializeComponent();

        AjustarConfiancaCertificado();
        txtServidor.TextChanged += (_, _) => AjustarConfiancaCertificado();
        chkConfiarCertificado.Click += (_, _) => _usuarioEscolheuConfiancaCertificado = true;

        MontarComboAutenticacao();
        cmbAutenticacao.SelectedIndexChanged += (_, _) => AplicarModoAutenticacao();
        AplicarModoAutenticacao();

        CarregarConexoesSalvas();
        cmbConexoesSalvas.SelectedIndexChanged += (_, _) => AplicarConexaoSelecionada();
        btnSalvarConexao.Click += (_, _) => SalvarConexaoAtual();
        btnExcluirConexao.Click += (_, _) => ExcluirConexaoSelecionada();
    }

    // ------------------------------------------------------------------
    // Autenticação (SQL Server x Windows)
    // ------------------------------------------------------------------

    /// <summary>
    /// Rótulo mostrado no combo para cada modo. O valor real que vai para o
    /// ConnectionSettings é o <see cref="TipoAutenticacao"/> guardado no Tag.
    /// </summary>
    private sealed record OpcaoAutenticacao(string Texto, TipoAutenticacao Tipo)
    {
        public override string ToString() => Texto;
    }

    private void MontarComboAutenticacao()
    {
        cmbAutenticacao.Items.Clear();
        cmbAutenticacao.Items.Add(new OpcaoAutenticacao("Autenticação do SQL Server", TipoAutenticacao.SqlServerAuthentication));
        cmbAutenticacao.Items.Add(new OpcaoAutenticacao("Autenticação do Windows", TipoAutenticacao.WindowsAuthentication));
        cmbAutenticacao.SelectedIndex = 0;
    }

    private TipoAutenticacao AutenticacaoSelecionada =>
        cmbAutenticacao.SelectedItem is OpcaoAutenticacao opcao
            ? opcao.Tipo
            : TipoAutenticacao.SqlServerAuthentication;

    /// <summary>
    /// Na autenticação do Windows quem identifica o usuário é a própria sessão
    /// do Windows: não há login/senha para digitar. Os dois campos são
    /// desabilitados (e não só ignorados) para a tela não sugerir que o que
    /// está escrito ali vai ser usado.
    /// </summary>
    private void AplicarModoAutenticacao()
    {
        var ehWindows = AutenticacaoSelecionada == TipoAutenticacao.WindowsAuthentication;

        txtUsuario.Enabled = !ehWindows;
        txtSenha.Enabled = !ehWindows;
        lblUsuario.Enabled = !ehWindows;
        lblSenha.Enabled = !ehWindows;
        chkGuardarSenha.Enabled = !ehWindows;

        if (ehWindows)
        {
            txtUsuario.Text = string.Empty;
            txtSenha.Text = string.Empty;
            chkGuardarSenha.Checked = false;
            txtUsuario.PlaceholderText = $"Conectando como {Environment.UserDomainName}\\{Environment.UserName}";
        }
        else
        {
            txtUsuario.PlaceholderText = "Ex: supersa";
        }
    }

    // ------------------------------------------------------------------
    // Conexões salvas
    // ------------------------------------------------------------------

    private const string TextoNenhumaConexao = "(nova conexão)";

    /// <summary>
    /// Evita que o preenchimento automático dos campos, ao escolher uma
    /// conexão salva, dispare os eventos que reagem a digitação do usuário.
    /// </summary>
    private bool _preenchendoCampos;

    private void CarregarConexoesSalvas(string? selecionar = null)
    {
        cmbConexoesSalvas.Items.Clear();
        cmbConexoesSalvas.Items.Add(TextoNenhumaConexao);

        foreach (var conexao in ConexoesSalvas.Listar().OrderBy(c => c.Nome, StringComparer.CurrentCultureIgnoreCase))
        {
            cmbConexoesSalvas.Items.Add(conexao.Nome);
        }

        var indice = selecionar is null ? 0 : cmbConexoesSalvas.Items.IndexOf(selecionar);
        cmbConexoesSalvas.SelectedIndex = indice >= 0 ? indice : 0;
    }

    private void AplicarConexaoSelecionada()
    {
        if (cmbConexoesSalvas.SelectedItem is not string nome || nome == TextoNenhumaConexao)
        {
            return;
        }

        var conexao = ConexoesSalvas.Listar()
            .FirstOrDefault(c => string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase));
        if (conexao is null)
        {
            return;
        }

        _preenchendoCampos = true;
        try
        {
            txtServidor.Text = conexao.ServerName;
            txtBancoDados.Text = conexao.InitialDatabase ?? string.Empty;

            for (var i = 0; i < cmbAutenticacao.Items.Count; i++)
            {
                if (cmbAutenticacao.Items[i] is OpcaoAutenticacao opcao && opcao.Tipo == conexao.TipoAutenticacao)
                {
                    cmbAutenticacao.SelectedIndex = i;
                    break;
                }
            }

            AplicarModoAutenticacao();

            if (conexao.TipoAutenticacao != TipoAutenticacao.WindowsAuthentication)
            {
                txtUsuario.Text = conexao.UsuarioLogin ?? string.Empty;

                // A senha pode não abrir: o arquivo é cifrado para ESTA conta
                // do Windows nesta máquina (DPAPI). Copiado de outro
                // computador/usuário, volta nulo — e aí a tela simplesmente
                // pede a senha, em vez de dar erro.
                var senha = ConexoesSalvas.RevelarSenha(conexao.SenhaProtegida);
                txtSenha.Text = senha ?? string.Empty;
                chkGuardarSenha.Checked = senha is not null;
            }

            // A escolha gravada na conexão vale mais que a deduzida pelo nome
            // do servidor (ver AjustarConfiancaCertificado).
            _usuarioEscolheuConfiancaCertificado = true;
            chkConfiarCertificado.Checked = conexao.ConfiarCertificadoServidor;
        }
        finally
        {
            _preenchendoCampos = false;
        }
    }

    private void SalvarConexaoAtual()
    {
        var servidor = txtServidor.Text.Trim();
        if (string.IsNullOrWhiteSpace(servidor))
        {
            MessageBox.Show(this, "Informe o servidor antes de salvar a conexão.", "SQL Rocket",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtServidor.Focus();
            return;
        }

        var sugestao = cmbConexoesSalvas.SelectedItem as string;
        if (sugestao == TextoNenhumaConexao)
        {
            sugestao = servidor;
        }

        var nome = PerguntarNomeDaConexao(sugestao ?? servidor);
        if (string.IsNullOrWhiteSpace(nome))
        {
            return;
        }

        var jaExiste = ConexoesSalvas.Listar()
            .Any(c => string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase));
        if (jaExiste)
        {
            var confirmar = MessageBox.Show(this,
                $"Já existe uma conexão salva chamada \"{nome}\". Deseja substituí-la?",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirmar != DialogResult.Yes)
            {
                return;
            }
        }

        var ehWindows = AutenticacaoSelecionada == TipoAutenticacao.WindowsAuthentication;

        try
        {
            ConexoesSalvas.Salvar(new ConexaoSalvaDto
            {
                Nome = nome,
                ServerName = servidor,
                InitialDatabase = string.IsNullOrWhiteSpace(txtBancoDados.Text) ? null : txtBancoDados.Text.Trim(),
                TipoAutenticacao = AutenticacaoSelecionada,
                UsuarioLogin = ehWindows ? null : txtUsuario.Text.Trim(),
                // Só grava a senha quando o usuário pediu — e mesmo assim
                // cifrada pelo Windows, nunca em texto.
                SenhaProtegida = !ehWindows && chkGuardarSenha.Checked
                    ? ConexoesSalvas.ProtegerSenha(txtSenha.Text)
                    : null,
                ConfiarCertificadoServidor = chkConfiarCertificado.Checked
            });

            CarregarConexoesSalvas(nome);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Não foi possível salvar a conexão.\n\n{ex.Message}\n\nArquivo: {ConexoesSalvas.CaminhoArquivo}",
                "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExcluirConexaoSelecionada()
    {
        if (cmbConexoesSalvas.SelectedItem is not string nome || nome == TextoNenhumaConexao)
        {
            MessageBox.Show(this, "Escolha uma conexão salva para excluir.", "SQL Rocket",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirmar = MessageBox.Show(this,
            $"Excluir a conexão salva \"{nome}\"? Os campos preenchidos na tela não são apagados.",
            "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmar != DialogResult.Yes)
        {
            return;
        }

        try
        {
            ConexoesSalvas.Excluir(nome);
            CarregarConexoesSalvas();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível excluir a conexão.\n\n{ex.Message}",
                "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Caixa de diálogo simples para o nome da conexão. Montada na mão porque
    /// o WinForms não traz um "InputBox" e trazer uma dependência só para isso
    /// não se justifica.
    /// </summary>
    private string? PerguntarNomeDaConexao(string sugestao)
    {
        using var janela = new Form
        {
            Text = "Salvar conexão",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(380, 130)
        };

        var rotulo = new Label
        {
            Text = "Nome desta conexão:",
            AutoSize = true,
            Location = new Point(16, 16)
        };
        var caixa = new TextBox
        {
            Text = sugestao,
            Location = new Point(16, 40),
            Size = new Size(348, 26),
            BorderStyle = BorderStyle.FixedSingle
        };
        var ok = new Button
        {
            Text = "Salvar",
            DialogResult = DialogResult.OK,
            Location = new Point(196, 82),
            Size = new Size(80, 30)
        };
        var cancelar = new Button
        {
            Text = "Cancelar",
            DialogResult = DialogResult.Cancel,
            Location = new Point(284, 82),
            Size = new Size(80, 30)
        };

        janela.Controls.AddRange(new Control[] { rotulo, caixa, ok, cancelar });
        janela.AcceptButton = ok;
        janela.CancelButton = cancelar;

        caixa.SelectAll();
        return janela.ShowDialog(this) == DialogResult.OK ? caixa.Text.Trim() : null;
    }

    /// <summary>
    /// Marca/desmarca sozinha a opção de confiar no certificado conforme o
    /// servidor digitado, enquanto o usuário não tiver mexido nela.
    ///
    /// O critério é simples de propósito: servidor LOCAL (localhost, ".",
    /// "(local)", 127.0.0.1, ou um nome de máquina sem ponto, com ou sem
    /// \INSTANCIA) quase sempre usa certificado autoassinado, e exigir
    /// validação ali só geraria um erro de SSL a cada login sem ganho nenhum
    /// de segurança. Qualquer outro endereço — FQDN, IP de outra máquina,
    /// Azure SQL — passa a ter o certificado VALIDADO, que é justamente onde
    /// a falta de validação era perigosa (tráfego saindo da máquina).
    /// </summary>
    private void AjustarConfiancaCertificado()
    {
        // Enquanto os campos estão sendo preenchidos a partir de uma conexão
        // salva, quem manda é o que foi gravado nela — não a dedução pelo nome
        // do servidor (o TextChanged de txtServidor dispararia aqui no meio).
        if (_usuarioEscolheuConfiancaCertificado || _preenchendoCampos)
        {
            return;
        }

        chkConfiarCertificado.Checked = ServidorParecePorLocal(txtServidor.Text);
    }

    private static bool ServidorParecePorLocal(string? servidor)
    {
        var nome = (servidor ?? string.Empty).Trim();
        if (nome.Length == 0)
        {
            return true;
        }

        // Separa a instância nomeada ("MAQUINA\SQLEXPRESS") e a porta
        // ("MAQUINA,1433") antes de analisar o host em si.
        var host = nome.Split('\\')[0].Split(',')[0].Trim();

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(".", StringComparison.Ordinal)
            || host.Equals("(local)", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Sem ponto = nome curto de máquina na rede local (ex.: "SRVBANCO").
        // Com ponto = FQDN, IP ou Azure — valida o certificado.
        return !host.Contains('.');
    }

    /// <summary>
    /// Monta a configuração de conexão a partir dos campos da tela, testa a
    /// conexão com o SQL Server e, em caso de sucesso, abre a Menu_Raiz.
    /// </summary>
    private async void btnConectar_Click(object? sender, EventArgs e)
    {
        var servidor = txtServidor.Text.Trim();
        var bancoDados = txtBancoDados.Text.Trim();
        var usuario = txtUsuario.Text.Trim();
        var senha = txtSenha.Text;

        if (string.IsNullOrWhiteSpace(servidor))
        {
            MessageBox.Show(this, "Informe o servidor.", "SQL Rocket",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtServidor.Focus();
            return;
        }

        // Usuário só é obrigatório na autenticação do SQL Server — na do
        // Windows a identidade vem da sessão, e os campos estão desabilitados.
        var ehAutenticacaoWindows = AutenticacaoSelecionada == TipoAutenticacao.WindowsAuthentication;
        if (!ehAutenticacaoWindows && string.IsNullOrWhiteSpace(usuario))
        {
            MessageBox.Show(this, "Informe o usuário.", "SQL Rocket",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtUsuario.Focus();
            return;
        }

        var configuracao = new ConnectionSettings
        {
            ServerName = servidor,
            // Opcional (ver comentário no campo, Login_SQL.Designer.cs) — em
            // branco, deixa o driver usar o banco padrão do login (SQL Server
            // local, comportamento antigo). Preenchido, evita o erro "Cannot
            // open user default database. Login failed." comum em logins do
            // Azure SQL Database que só têm acesso a UM banco específico
            // (nunca ao "master").
            InitialDatabase = string.IsNullOrWhiteSpace(bancoDados) ? null : bancoDados,
            // Modo escolhido no combo "AUTENTICAÇÃO" — era fixo em
            // SqlServerAuthentication, o que impedia conectar em servidor que
            // só aceita conta de domínio.
            TipoAutenticacao = AutenticacaoSelecionada,
            UsuarioLogin = ehAutenticacaoWindows ? null : usuario,
            Senha = ehAutenticacaoWindows ? null : senha,
            // Detecção simples pelo endereço do servidor — só usada hoje para
            // preencher esse campo de metadado (nenhuma query ainda ramifica
            // por Ambiente; a escolha entre Active Monitor Local/Azure
            // continua manual, pela sidebar).
            Ambiente = servidor.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase)
                ? AmbienteSqlType.AzureSql
                : AmbienteSqlType.Local,
            // Decisão do usuário, na caixa de seleção do próprio cartão de
            // login (marcada automaticamente para servidor local — ver
            // AjustarConfiancaCertificado). Antes era "true" fixo: a conexão
            // era criptografada mas o certificado do servidor NUNCA era
            // validado, inclusive contra Azure SQL pela internet.
            ConfiarCertificadoServidor = chkConfiarCertificado.Checked
        };

        SetTelaOcupada(true);
        try
        {
            _conectarSql.ConfigurarConexao(configuracao);

            // Lança exceção com mensagem amigável em caso de falha
            // (servidor não encontrado, credenciais inválidas, timeout, etc.).
            await _conectarSql.TestarConexaoAsync();

            AbrirMenuRaiz();
        }
        catch (Exception ex)
        {
            // Falha de certificado tem uma saída específica (a caixa de
            // seleção logo acima do botão) — sem essa dica, o erro de SSL do
            // driver não diz ao usuário o que fazer.
            var textoErro = ex.ToString();
            var pareceErroDeCertificado =
                textoErro.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                || textoErro.Contains("certificad", StringComparison.OrdinalIgnoreCase)
                || textoErro.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || textoErro.Contains("cadeia de certifica", StringComparison.OrdinalIgnoreCase);

            var dica = pareceErroDeCertificado && !chkConfiarCertificado.Checked
                ? "\n\nParece um problema com o certificado do servidor. Se esta é uma instância local " +
                  "(ou com certificado autoassinado), marque \"Confiar no certificado do servidor\" e tente de novo. " +
                  "Para servidores remotos, prefira corrigir o certificado a desativar a validação."
                : string.Empty;

            MessageBox.Show(this, $"Não foi possível conectar ao SQL Server.\n\n{ex.Message}{dica}",
                "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetTelaOcupada(false);
        }
    }

    /// <summary>
    /// Esconde a tela de login e abre a janela principal (MDI), passando a
    /// conexão já validada. Ao fechar a Menu_Raiz, a aplicação é encerrada.
    /// </summary>
    private void AbrirMenuRaiz()
    {
        this.Hide();

        var menuRaiz = new Menu_Raiz(_conectarSql);
        menuRaiz.FormClosed += (_, _) => this.Close();
        menuRaiz.Show();
    }

    private void SetTelaOcupada(bool ocupada)
    {
        Cursor = ocupada ? Cursors.WaitCursor : Cursors.Default;
        btnConectar.Enabled = !ocupada;
        btnCancelar.Enabled = !ocupada;
        txtServidor.Enabled = !ocupada;
        txtBancoDados.Enabled = !ocupada;
        txtUsuario.Enabled = !ocupada;
        txtSenha.Enabled = !ocupada;
    }

    private void btnCancelar_Click(object? sender, EventArgs e)
    {
        Application.Exit();
    }
}
