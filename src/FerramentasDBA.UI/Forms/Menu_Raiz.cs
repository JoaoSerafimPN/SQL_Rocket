using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using FerramentasDBA.Classes.Modulos;
using FerramentasDBA.UI.Assets;
using FerramentasDBA.UI.Controls;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.UI.Forms;

/// <summary>
/// Janela principal da aplicação: navegação lateral moderna (sidebar com
/// grupos expansíveis) no lugar do MenuStrip/MDI original, cobrindo a mesma
/// estrutura de funcionalidades definida no documento de arquitetura.
///
/// A navegação em si é real (troca de páginas funciona). O conteúdo de cada
/// página já chama o método de negócio correspondente. Modulo_Informacoes,
/// Modulo_Admin (backup/restore), Modulo_Indices (todo o submenu Índice) e
/// Modulo_Desempenho (Top 25 consultas, Active Monitor SQL Local e Azure)
/// já estão implementados, incluindo os dois submenus do Comparador
/// (Índices e Banco de dados — cada um com sua própria tela de duas
/// conexões origem/destino), e todo o submenu "Manutenção": "Limpeza de
/// Arquivos" (Modulo_Manutencao — shrink de log/banco, top 25 maiores
/// tabelas, primeiro/último registro e limpeza em lote — ou de 1 registro
/// só — por data+hora ou por chave primária real), "Headblock e DeadLock"
/// (Modulo_Deadlock — análise 100% offline de deadlock graphs e/ou
/// blocked process reports (head blocking) a partir de um arquivo .xel ou
/// .xml enviado pelo usuário) e "Análise do plano de execução"
/// (Modulo_PlanoExecucao — análise 100% offline de um arquivo .sqlplan
/// enviado pelo usuário: resumo do statement, operadores mais custosos,
/// scans em vez de seeks, e alertas/sugestões de índice do próprio SQL
/// Server). Nenhuma dessas três análises de arquivo (Limpeza de Arquivos à
/// parte, que trabalha direto no banco) depende de conexão com banco de
/// dados.
/// </summary>
public partial class Menu_Raiz : Form
{
    private static readonly Color CorAccent = Color.FromArgb(47, 111, 237);
    private static readonly Color CorTextoSecundario = Color.FromArgb(100, 110, 130);
    private static readonly Color CorTitulo = Color.FromArgb(24, 40, 72);

    // VAZAMENTO DE GDI corrigido: as colunas "clicáveis" das grades (Comando
    // exec / Query em Execução no Active Monitor, plano de execução no Top 25)
    // são pintadas em azul e sublinhadas para parecerem link. A fonte
    // sublinhada era criada com "new Font(grid.Font, FontStyle.Underline)"
    // DENTRO de AplicarFormatacaoColunas(), que roda a cada tick do timer de
    // atualização automática (1 em 1 segundo nas telas de Active Monitor) — a
    // Font anterior nunca era descartada, o que consumia ~3.600 handles GDI
    // por hora contra o limite de 10.000 por processo, até a tela começar a
    // falhar ao desenhar. Agora a fonte é criada UMA única vez e reutilizada
    // (mesmo padrão de fontes estáticas de PlanoExecucaoDiagrama).
    //
    // Base = Control.DefaultFont (e não "Segoe UI 9pt" escrito na mão) porque
    // é exatamente o que "grid.Font" devolvia nesses três pontos: nenhuma das
    // grades define Font própria, nenhum contêiner pai (cartão/painéis/Form)
    // define Font, e o projeto não usa ApplicationDefaultFont/SetDefaultFont —
    // então a aparência continua idêntica.
    private static readonly Font FonteColunaClicavel = new(Control.DefaultFont, FontStyle.Underline);

    // Imagem de fundo da tela de boas-vindas (Assets/WelcomeBackground.png —
    // 1920x1080 RGBA, ~8 MB já decodificada em memória). Antes era carregada
    // do assembly DENTRO de CriarPainelMensagem, ou seja, uma instância NOVA
    // de 8 MB a cada montagem da página — e "Início" é item fixo da sidebar,
    // usado no dia a dia como botão de "voltar", então isso acontecia a cada
    // clique. A instância antiga também nunca era descartada (ficava presa no
    // handler de Paint da página anterior), o que empilhava bitmaps de 8 MB
    // até o GC decidir coletá-los.
    //
    // Agora é UMA instância para o app inteiro (mesmo padrão de
    // FonteColunaClicavel acima), criada só no primeiro uso — de propósito
    // nunca descartada: vive enquanto o processo viver, e o SO libera tudo no
    // encerramento. Só é tocada na thread de UI (Paint/montagem de página),
    // por isso a inicialização preguiçosa não precisa de lock.
    private static Image? _fundoBoasVindas;

    private static Image FundoBoasVindas => _fundoBoasVindas ??= RecursosVisuais.CarregarFundoBoasVindas();

    // Resultado do desenho "cover" (ver DesenharImagemCover) JÁ redimensionado
    // para o último tamanho pedido. Existe porque o Paint da tela de
    // boas-vindas é disparado a cada Resize do cartão, e redimensionar uma
    // imagem de 2 megapixels com InterpolationMode.HighQualityBicubic na
    // thread de UI a cada evento de Resize travava visivelmente o
    // redimensionamento da janela. Com o cache, o custo do bicúbico é pago uma
    // vez por TAMANHO (só quando o tamanho realmente muda) e os demais Paints
    // viram um DrawImageUnscaled. A aparência final é idêntica: o cache é
    // pintado com a mesma cor de fundo do cartão e o mesmo DesenharImagemCover.
    private static Bitmap? _fundoBoasVindasEscalado;

    // Prefixos de "Command" (sys.dm_exec_requests.command) considerados
    // comandos de dados/DDL — usado pelo filtro "Somente comandos de
    // usuário" da tela Active Monitor SQL Local pra esconder tarefas
    // internas do SQL Server (TRACE QUEUE TASK, CHECKPOINT, BRKR TASK,
    // LAZY WRITER, etc.), deixando só o que pode ser executado por um
    // usuário ou por um processo/job (SELECT, INSERT, UPDATE, DELETE,
    // MERGE, CREATE/ALTER/DROP de qualquer objeto, TRUNCATE, BACKUP,
    // RESTORE, EXEC de procedure, DBCC, BULK INSERT, GRANT/REVOKE/DENY).
    // Comparação por PREFIXO (não valor exato) porque o SQL Server usa
    // vários sufixos pro mesmo verbo (ex.: "CREATE TABLE", "CREATE INDEX",
    // "CREATE PROCEDURE" — todos cobertos por um único prefixo "CREATE").
    private static readonly string[] PrefixosComandoUsuario =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP",
        "TRUNCATE", "BACKUP", "RESTORE", "EXEC", "DBCC", "BULK", "GRANT", "REVOKE", "DENY"
    };

    private readonly Conectar_SQL _conectarSql;
    private readonly Modulo_Indices _moduloIndices;
    private readonly Modulo_Desempenho _moduloDesempenho;
    private readonly Modulo_Admin _moduloAdmin;
    private readonly Modulo_Informacoes _moduloInformacoes;
    private readonly Modulo_Comparador _moduloComparador;
    private readonly Modulo_Manutencao _moduloManutencao;
    private readonly Modulo_Deadlock _moduloDeadlock;
    private readonly Modulo_PlanoExecucao _moduloPlanoExecucao;
    private readonly Modulo_Profiler _moduloProfiler;

    // Timer de atualização automática da página atualmente aberta (ex.:
    // Active Monitor SQL Local) — registrado por quem cria o Timer, e
    // parado/descartado automaticamente em DefinirConteudo ao trocar de
    // página (ver comentário lá). Só uma página por vez pode ter Timer
    // próprio, o que é suficiente hoje (Active Monitor e o "Executar" do
    // Profiler usam isso).
    private System.Windows.Forms.Timer? _timerPaginaAtiva;

    // Limpeza assíncrona de um recurso do SERVIDOR (não local, como o
    // Timer acima) deixado por trás pela página atualmente aberta — hoje
    // só usado pelo Profiler ("Executar"): uma sessão de Extended Events
    // criada no servidor continua existindo lá mesmo depois que o usuário
    // navega para outra tela ou fecha o app, então precisa ser
    // explicitamente parada/removida (ver Modulo_Profiler.PararSessaoAsync).
    // Registrado por quem inicia a captura, disparado (melhor esforço,
    // "fire and forget") em DefinirConteudo ao trocar de página, e também
    // aguardado sincronamente em Menu_Raiz_FormClosing ao fechar o app.
    private Func<Task>? _limpezaAssincronaPaginaAtiva;

    // Cancelamento das consultas da página atualmente aberta. Trocado em
    // NavegarPara, ANTES de montar a página nova: o CTS antigo é cancelado e
    // descartado, e um novo nasce para a página que está entrando.
    //
    // Existe porque nenhuma chamada da UI criava token nenhum — os módulos de
    // negócio já recebiam "CancellationToken ct = default" e repassavam
    // direitinho para o SqlCommand, mas a UI sempre passava default. Na
    // prática: sair de uma tela no meio de uma consulta pesada deixava a
    // consulta rodando no SERVIDOR até o fim, e a continuação dela ainda
    // escrevia em grades/labels de uma página que o usuário já tinha deixado.
    //
    // REGRA (vale para todo este arquivo): o token só é passado para consultas
    // de LEITURA/atualização — grades, combos, refresh do Active Monitor,
    // polling do Profiler, telas de informação. NUNCA para operações
    // DESTRUTIVAS (backup, restore, shrink, exclusão de registros em lote,
    // DROP/CREATE INDEX, rebuild/reorganize, alteração de collation): abortar
    // uma dessas no meio deixaria o trabalho pela metade no servidor, então
    // elas precisam terminar mesmo que o usuário navegue para outra tela — é o
    // mesmo motivo de _operacoesDestrutivasEmAndamento existir.
    private CancellationTokenSource? _ctsPaginaAtiva;

    /// <summary>
    /// Token da página que está sendo montada/exibida (ver
    /// <see cref="_ctsPaginaAtiva"/>). Cada construtor de página deve copiá-lo
    /// para uma variável local UMA vez ("var tokenPagina = TokenPaginaAtiva;")
    /// e usar essa variável nas continuações — assim elas continuam olhando
    /// para o token DA PRÓPRIA página mesmo depois que outra página trocar o
    /// campo.
    /// </summary>
    private CancellationToken TokenPaginaAtiva => _ctsPaginaAtiva?.Token ?? CancellationToken.None;

    // Quantidade de operações DESTRUTIVAS em andamento neste momento (shrink,
    // exclusão de registros em lote, DROP/CREATE INDEX, rebuild/reorganize,
    // backup, restore e alteração de collation).
    //
    // Existe porque "Sobre > Sair" chamava Application.Exit() direto, sem
    // pergunta nenhuma: um clique errado no meio de um RESTORE (ou de uma
    // limpeza em lote) derrubava o app com a operação ainda rodando. Agora
    // "Sair" e o FormClosing pedem confirmação enquanto isso for > 0.
    //
    // É um CONTADOR e não um bool porque nem toda operação destrutiva bloqueia
    // a tela inteira (backup/restore/collation só desabilitam o próprio
    // botão), então duas podem estar em andamento ao mesmo tempo em páginas
    // diferentes — com um bool, a primeira a terminar zeraria o aviso da
    // outra. Só é tocado na thread de UI (handlers de clique), por isso não
    // precisa de Interlocked.
    private int _operacoesDestrutivasEmAndamento;

    private bool HaOperacaoDestrutivaEmAndamento => _operacoesDestrutivasEmAndamento > 0;

    /// <summary>
    /// Pergunta se o usuário realmente quer sair/fechar com uma operação
    /// destrutiva em andamento. Retorna true quando pode encerrar.
    /// </summary>
    private bool PodeEncerrarComOperacaoEmAndamento()
    {
        if (!HaOperacaoDestrutivaEmAndamento)
        {
            return true;
        }

        var resposta = MessageBox.Show(this,
            "Existe uma operação em andamento (shrink, exclusão de registros, manutenção de índice, " +
            "backup/restore ou alteração de collation).\n\n" +
            "Fechar o SQL Rocket agora interrompe a conexão no meio da operação — o SQL Server pode " +
            "deixar o trabalho pela metade e o resultado não será exibido.\n\n" +
            "Deseja fechar mesmo assim?",
            "SQL Rocket — operação em andamento",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        return resposta == DialogResult.Yes;
    }

    public Menu_Raiz(Conectar_SQL conectarSql)
    {
        _conectarSql = conectarSql;
        _moduloIndices = new Modulo_Indices(_conectarSql);
        _moduloDesempenho = new Modulo_Desempenho(_conectarSql);
        _moduloAdmin = new Modulo_Admin(_conectarSql, _moduloIndices);
        _moduloInformacoes = new Modulo_Informacoes(_conectarSql);
        _moduloComparador = new Modulo_Comparador();
        _moduloManutencao = new Modulo_Manutencao(_conectarSql);
        // Sem conexão com banco — análise 100% offline a partir de um
        // arquivo .xel/.xml enviado pelo usuário (mesmo padrão do
        // Modulo_Comparador, que também não recebe Conectar_SQL).
        _moduloDeadlock = new Modulo_Deadlock();
        // A ANÁLISE em si é 100% offline (mesma ideia do Modulo_Deadlock,
        // a partir só do arquivo .sqlplan enviado) — mas recebe _conectarSql
        // porque criar automaticamente um dos índices sugeridos
        // (Modulo_PlanoExecucao.CriarIndiceAsync) precisa de uma conexão
        // real com o banco indicado no próprio plano.
        _moduloPlanoExecucao = new Modulo_PlanoExecucao(_conectarSql);
        // "Executar" cria/inicia/para uma sessão de Extended Events no
        // servidor; "Analisar arquivo" é 100% offline (só usa o mesmo
        // módulo por conveniência, sem abrir conexão nenhuma nesse caso).
        _moduloProfiler = new Modulo_Profiler(_conectarSql);

        InitializeComponent();
        MontarMenuLateral();
        MostrarBoasVindas();
        FormClosing += Menu_Raiz_FormClosing;
    }

    /// <summary>
    /// Melhor esforço para não deixar uma sessão de Extended Events do
    /// Profiler órfã no servidor quando o usuário fecha o app com uma
    /// captura ainda ativa (ver <see cref="_limpezaAssincronaPaginaAtiva"/>).
    /// Síncrono (".GetAwaiter().GetResult()") de propósito — FormClosing
    /// não tem uma variante assíncrona nativa, e é melhor esperar um
    /// instante para parar a sessão do que deixá-la rodando indefinidamente
    /// no servidor. Nunca impede o fechamento do app: qualquer falha aqui
    /// (ex.: rede já indisponível) é engolida.
    /// </summary>
    private void Menu_Raiz_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Antes de qualquer limpeza: com uma operação destrutiva em andamento,
        // pede confirmação e cancela o fechamento se o usuário desistir (ver
        // _operacoesDestrutivasEmAndamento). Vale tanto para o X da janela
        // quanto para o item "Sair" da sidebar — Application.Exit() também
        // passa por FormClosing.
        if (!PodeEncerrarComOperacaoEmAndamento())
        {
            e.Cancel = true;
            return;
        }

        if (_limpezaAssincronaPaginaAtiva is null)
        {
            return;
        }

        var limpeza = _limpezaAssincronaPaginaAtiva;
        _limpezaAssincronaPaginaAtiva = null;
        try
        {
            limpeza().GetAwaiter().GetResult();
        }
        catch
        {
            // Melhor esforço — ver XML doc do método.
        }
    }

    // ----------------------------------------------------------------
    // Montagem da árvore de navegação (espelha o documento de arquitetura)
    // ----------------------------------------------------------------

    private void MontarMenuLateral()
    {
        // Pedido do usuário: um botão para voltar à tela de boas-vindas a
        // partir de qualquer página — fica sozinho no topo (mesmo padrão de
        // item avulso já usado em "Sair", lá embaixo), sem precisar abrir
        // nenhum grupo.
        sidebarNav.AdicionarItemSimples("inicio.bem_vindo", "Início");

        sidebarNav.AdicionarGrupo("Informações", new[]
        {
            ("info.sql", "SQL"),
            ("info.servidor", "Servidor")
        });

        sidebarNav.AdicionarGrupo("Índice", new[]
        {
            ("indice.fragmentacao", "Fragmentação"),
            ("indice.nunca_utilizados", "Índices nunca Utilizados"),
            ("indice.sugeridos", "Índices sugeridos"),
            ("indice.mais_utilizados", "Índices mais utilizados")
        });

        sidebarNav.AdicionarGrupo("Desempenho", new[]
        {
            ("desempenho.top25", "Top 25 consultas")
        });

        sidebarNav.AdicionarGrupo("Monitoramento", new[]
        {
            ("monitoramento.active_local", "Active Monitor SQL Local"),
            ("monitoramento.active_azure", "Active Monitor SQL Azure")
        });

        sidebarNav.AdicionarGrupo("Collation", new[]
        {
            ("collation.database", "Collation database")
        });

        sidebarNav.AdicionarGrupo("Segurança", new[]
        {
            ("seguranca.logs", "Logs"),
            ("seguranca.usuarios", "Usuários")
        });

        sidebarNav.AdicionarGrupo("Backup", new[]
        {
            ("backup.full", "Full"),
            ("backup.restore", "Restore")
        });

        sidebarNav.AdicionarGrupo("Comparador", new[]
        {
            ("comparador.indices", "Índices"),
            ("comparador.banco", "Banco de dados")
        });

        sidebarNav.AdicionarGrupo("Manutenção", new[]
        {
            ("manutencao.limpeza_arquivos", "Limpeza de Arquivos"),
            ("manutencao.headblock_deadlock", "Headblock e DeadLock"),
            ("manutencao.plano_execucao", "Analise do plano de execução"),
            ("manutencao.profiler", "Profiler")
        });

        sidebarNav.AdicionarGrupo("Sobre", new[]
        {
            ("sobre.info", "Informações do programa")
        });

        // "Sair" não encerra mais o app direto: era um item de menu a um
        // clique de distância que chamava Application.Exit() sem pergunta
        // nenhuma, mesmo com um RESTORE ou uma limpeza em lote em andamento.
        sidebarNav.AdicionarItemSimples("sobre.sair", "Sair", (_, _) =>
        {
            // Com uma operação destrutiva em andamento, a confirmação
            // específica (e o cancelamento do fechamento) fica por conta de
            // Menu_Raiz_FormClosing — aqui não se pergunta nada para não
            // exibir dois diálogos seguidos pelo mesmo clique.
            if (!HaOperacaoDestrutivaEmAndamento)
            {
                var resposta = MessageBox.Show(this, "Deseja realmente sair do SQL Rocket?", "SQL Rocket",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (resposta != DialogResult.Yes)
                {
                    return;
                }
            }

            Application.Exit();
        });

        sidebarNav.ItemSelecionado += (_, chave) => NavegarPara(chave);
    }

    /// <summary>Roteia a chave selecionada na sidebar para a página correspondente.</summary>
    private void NavegarPara(string chave)
    {
        // A construção de uma página pode falhar no meio (consulta de
        // apoio que estoura timeout, conexão caída, etc.). Sem este
        // try/catch a exceção escapava para o message loop e caía no
        // diálogo bruto de erro do .NET, deixando a tela inconsistente:
        // o título já era o da página nova, mas o conteúdo continuava
        // sendo o da página anterior. Agora a falha vira a mensagem
        // amigável de sempre e a tela volta para um estado coerente
        // (título + conteúdo explicando a falha).

        // Primeira coisa: cancelar as consultas da página que está saindo e
        // abrir um token novo para a que está entrando (ver _ctsPaginaAtiva).
        // Feito AQUI, e não em DefinirConteudo, porque vários construtores de
        // página chamam DefinirConteudo no MEIO do corpo — se o token nascesse
        // lá, o trecho de montagem que vem antes ainda enxergaria o token da
        // página anterior (já cancelado).
        var ctsAnterior = _ctsPaginaAtiva;
        _ctsPaginaAtiva = new CancellationTokenSource();
        if (ctsAnterior is not null)
        {
            ctsAnterior.Cancel();
            ctsAnterior.Dispose();
        }

        try
        {
            switch (chave)
            {
                case "inicio.bem_vindo":
                    MostrarBoasVindas();
                    break;

                case "info.sql":
                    MostrarPaginaInfo("Informações > SQL",
                        "Versão e edição da instância conectada.",
                        ct => _moduloInformacoes.ObterInformacoesSqlAsync(ct));
                    break;

                case "info.servidor":
                    MostrarPaginaInfo("Informações > Servidor",
                        "Informações gerais do servidor/host.",
                        ct => _moduloInformacoes.ObterInformacoesServidorAsync(ct));
                    break;

                case "indice.fragmentacao":
                    MostrarPaginaIndiceFragmentacao();
                    break;

                case "indice.nunca_utilizados":
                    MostrarPaginaIndicesNuncaUtilizados();
                    break;

                case "indice.sugeridos":
                    MostrarPaginaIndicesSugeridos();
                    break;

                case "indice.mais_utilizados":
                    MostrarPaginaIndicesMaisUtilizados();
                    break;

                case "desempenho.top25":
                    MostrarPaginaTop25Consultas();
                    break;

                case "monitoramento.active_local":
                    MostrarPaginaActiveMonitorLocal();
                    break;

                case "monitoramento.active_azure":
                    MostrarPaginaActiveMonitorAzure();
                    break;

                case "seguranca.logs":
                    MostrarPaginaLogsErro();
                    break;

                case "seguranca.usuarios":
                    MostrarPaginaUsuarios();
                    break;

                case "collation.database":
                    MostrarPaginaCollation();
                    break;

                case "backup.full":
                    MostrarPaginaBackupFull();
                    break;

                case "backup.restore":
                    MostrarPaginaRestore();
                    break;

                case "comparador.indices":
                    MostrarPaginaComparadorIndices();
                    break;

                case "comparador.banco":
                    MostrarPaginaComparadorBanco();
                    break;

                case "manutencao.limpeza_arquivos":
                    MostrarPaginaLimpezaArquivos();
                    break;

                case "manutencao.headblock_deadlock":
                    MostrarPaginaHeadblockDeadlock();
                    break;

                case "manutencao.plano_execucao":
                    MostrarPaginaPlanoExecucao();
                    break;

                case "manutencao.profiler":
                    MostrarPaginaProfiler();
                    break;

                case "sobre.info":
                    MostrarPaginaSobre();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            lblTituloPagina.Text = "SQL Rocket";
            DefinirConteudo(CriarPainelMensagem(
                "Não foi possível abrir esta tela.\n\n" + ObterMensagemAmigavel(ex) +
                "\n\nEscolha outra opção no menu à esquerda ou tente novamente."));
        }
    }

    // ----------------------------------------------------------------
    // Construção de páginas
    // ----------------------------------------------------------------

    private void MostrarBoasVindas()
    {
        lblTituloPagina.Text = "Bem-vindo";
        var servidor = _conectarSql.ConfiguracaoAtual?.ServerName ?? "-";
        var usuario = _conectarSql.ConfiguracaoAtual?.UsuarioLogin ?? "-";

        DefinirConteudo(CriarPainelMensagem(
            "Selecione uma opção no menu à esquerda para começar.\n\n" +
            $"Conectado como \"{usuario}\" em \"{servidor}\"."));
    }

    /// <summary>
    /// Página "Sobre", no formato/tom de um README de repositório GitHub
    /// (título, badges de tecnologia, descrição curta, seções "Funcionalidades"/
    /// "Stack"/"Status"). Como o conteúdo aqui é fixo (não depende de dados do
    /// SQL Server), fica em um FlowLayoutPanel (Dock=Top, AutoSize) dentro do
    /// cartão — o cartão tem AutoScroll = true (CriarCartao), então o texto
    /// nunca é cortado, mesmo em janelas pequenas.
    /// </summary>
    private void MostrarPaginaSobre()
    {
        lblTituloPagina.Text = "Sobre > Informações do programa";

        var cartao = CriarCartao();

        var pilha = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        var lblTitulo = new Label
        {
            Text = "SQL Rocket",
            Font = new Font("Segoe UI", 20F, FontStyle.Bold),
            ForeColor = CorTitulo,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        var pnlBadges = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 18)
        };
        pnlBadges.Controls.Add(CriarBadge("v1.0", Color.FromArgb(47, 111, 237)));
        pnlBadges.Controls.Add(CriarBadge(".NET 8", Color.FromArgb(81, 43, 212)));
        pnlBadges.Controls.Add(CriarBadge("WinForms", Color.FromArgb(0, 137, 123)));
        pnlBadges.Controls.Add(CriarBadge("SQL Server", Color.FromArgb(196, 55, 55)));
        // Pedido do usuário: versão 1.0, status "produção" (era "v0.1"/"em
        // desenvolvimento") — cor verde (mesmo tom usado em badges de
        // status estável estilo shields.io), no lugar do cinza neutro
        // anterior.
        pnlBadges.Controls.Add(CriarBadge("produção", Color.FromArgb(46, 160, 67)));

        var lblDescricao = new Label
        {
            Text = "Ferramenta desktop de apoio à manutenção de instâncias SQL Server — monitoramento, " +
                   "desempenho, administração e segurança em um só lugar.",
            Font = new Font("Segoe UI", 10.5F),
            ForeColor = Color.FromArgb(60, 70, 90),
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 0, 0, 20)
        };

        var lblFuncionalidades = CriarTituloSecaoSobre("Funcionalidades");
        var lblTextoFuncionalidades = CriarTextoSecaoSobre(
            "• Informações — SQL (versão/edição/collation/instância) e Servidor (nome, IP, memória, " +
            "processador, HDs, versão do Windows, domínio)\n" +
            "• Índice — Fragmentação (rebuild/reorganize individual ou em lote por limiar de fragmentação, " +
            "atualização de estatísticas); nunca utilizados, sugeridos e mais utilizados\n" +
            "• Desempenho — Top N consultas mais custosas (15/25/50/100), com plano de execução por " +
            "consulta, visível em texto ou aberto graficamente no SSMS/Visual Studio\n" +
            "• Monitoramento — Active Monitor SQL Local e Azure SQL, com processos, bloqueios e métricas " +
            "atualizados automaticamente\n" +
            "• Collation — Collation Manager: consulta a collation do banco, do servidor e de cada coluna " +
            "(com aviso quando divergir do padrão), e altera a collation do banco de dados ou de todas as " +
            "colunas de texto, com recriação automática de índice quando necessário\n" +
            "• Segurança — log de erros do SQL Server (estilo Log File Viewer do SSMS); Usuários: criação de " +
            "login (SQL Server ou Windows Authentication), criação de usuário no banco, e permissões de " +
            "servidor e de banco de dados (papéis fixos e granulares), com tabelas/views geridas separadamente\n" +
            "• Backup — Full e Restore, com mensagens do SQL Server em tempo real\n" +
            "• Comparador — Índices: compara os índices de um banco da instância conectada com outro banco " +
            "(mesma instância ou outro servidor), destaca diferenças e ausências, e aplica em qualquer " +
            "sentido; Banco de dados: mesma ideia aplicada à estrutura de tabelas/colunas (tipo, tamanho/" +
            "precisão, nulidade, IDENTITY) — tabela inteira ausente é criada por completo ao aplicar, e uma " +
            "coluna NOT NULL de data/hora numa tabela com dados é preenchida automaticamente com a data/hora " +
            "atual; as duas telas mostram um resumo destacado (tudo igual, ou quantos itens são diferentes/" +
            "ausentes) assim que a comparação termina, além da grade linha a linha\n" +
            "• Manutenção — Limpeza de Arquivos: shrink do log (LDF, com troca temporária do Recovery Model " +
            "para SIMPLE e SHRINKFILE TRUNCATEONLY, restaurando o Recovery Model original ao final) e do banco " +
            "completo (SHRINKDATABASE), grade com as 25 maiores tabelas, seleção de tabela com o primeiro e o " +
            "último registro dela (ordenado pela chave primária real da tabela, lida dos metadados do SQL " +
            "Server), e limpeza por data+hora (coluna escolhida manualmente) ou por chave primária (quando " +
            "simples e numérica) — cada uma com um botão para excluir em lote (DELETE em lotes, com barra de " +
            "progresso e Cancelar) e outro para excluir só 1 registro como teste antes do lote; Headblock e " +
            "DeadLock: envio de um arquivo .xel (Extended Events) ou .xml com deadlock graph e/ou blocked " +
            "process report, analisado 100% offline (sem conectar a nenhum banco) — grade com os eventos " +
            "encontrados e, ao selecionar um, o resumo comparando os processos envolvidos (vítima/vencedor num " +
            "Deadlock, bloqueado/bloqueador num Head Blocking: SPID, operação, gatilho/código de origem, lock " +
            "retido e lock solicitado); Análise do plano de execução: envio de um arquivo .sqlplan, analisado " +
            "100% offline — grade com as instruções (statements) encontradas e, ao selecionar uma, o texto da " +
            "consulta, resumo geral (custo, linhas estimadas, plano real ou só estimado, tempo/memória de " +
            "compilação), Alertas (avisos que o próprio SQL Server registrou no plano, com sugestão de solução), " +
            "Índices Sugeridos (com botões para copiar o script CREATE INDEX ou criar o índice automaticamente), " +
            "Operadores mais custosos (ordenados pelo custo do próprio operador, com o Custo Relativo (%) que o " +
            "SSMS mostra em cada ícone do plano gráfico) e Scans em vez de Seeks — tudo numa aba \"Grades\"; uma " +
            "segunda aba \"Gráfico\" mostra a árvore de operadores como um diagrama visual (ícones genéricos, " +
            "clicáveis para ver os detalhes de cada operador)");

        var lblStack = CriarTituloSecaoSobre("Stack");
        var lblTextoStack = CriarTextoSecaoSobre(
            "• C# / .NET 8\n" +
            "• WinForms (camada de apresentação)\n" +
            "• Microsoft.Data.SqlClient (acesso a dados)\n" +
            "• System.Management/WMI (dados de servidor sem depender de xp_cmdshell)");

        var lblStatus = CriarTituloSecaoSobre("Status");
        var lblTextoStatus = CriarTextoSecaoSobre(
            "Versão 1.0 — em produção. Já implementados de ponta a ponta: Backup (Full e Restore), Informações " +
            "(SQL e Servidor), Desempenho (Top N consultas + plano de execução), Índice (Fragmentação, nunca " +
            "utilizados, sugeridos e mais utilizados), Monitoramento (Active Monitor SQL Local e Azure), " +
            "Collation (Collation Manager — banco de dados e colunas), Segurança (Logs — log de erros do SQL " +
            "Server; Usuários — criação de login/usuário e permissões de servidor, de banco de dados e de " +
            "tabelas/views), Comparador (Índices e Banco de dados), Manutenção > Limpeza de Arquivos (shrink, " +
            "top 25 maiores tabelas, primeiro/último registro e limpeza em lote por data+hora ou por chave " +
            "primária), Manutenção > Headblock e DeadLock (análise offline de deadlock graphs e blocked process " +
            "reports a partir de arquivo .xel ou .xml) e Manutenção > Análise do plano de execução (análise " +
            "offline de arquivo .sqlplan). Não resta nenhum submenu como stub nesta versão.");

        pilha.Controls.Add(lblTitulo);
        pilha.Controls.Add(pnlBadges);
        pilha.Controls.Add(lblDescricao);
        pilha.Controls.Add(lblFuncionalidades);
        pilha.Controls.Add(lblTextoFuncionalidades);
        pilha.Controls.Add(lblStack);
        pilha.Controls.Add(lblTextoStack);
        pilha.Controls.Add(lblStatus);
        pilha.Controls.Add(lblTextoStatus);

        cartao.Controls.Add(pilha);

        DefinirConteudo(cartao);
    }

    /// <summary>
    /// Página "Backup > Full", seguindo o mockup fornecido pelo usuário:
    /// combo de bancos de dados (populado de verdade via
    /// Modulo_Admin.ObterBancosDadosAsync), caminho do backup com seletor de
    /// arquivo, opções (Copy-only/Compressão/Verificar integridade), barra
    /// de progresso e execução — tudo embutido no painel de conteúdo, sem
    /// abrir uma janela separada.
    /// </summary>
    private void MostrarPaginaBackupFull()
    {
        lblTituloPagina.Text = "Backup > Full";

        var cartao = CriarCartao();

        var fonteRotulo = new Font("Segoe UI", 10F, FontStyle.Bold);
        var fonteCheckbox = new Font("Segoe UI", 9F);

        var lblBanco = new Label
        {
            Text = "Banco de Dados",
            Font = fonteRotulo,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var cmbBancoDados = new ComboBox
        {
            Location = new Point(0, 24),
            Size = new Size(536, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            Sorted = true
        };

        var lblCaminho = new Label
        {
            Text = "Caminho do Backup",
            Font = fonteRotulo,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 64)
        };
        var txtCaminho = new TextBox
        {
            Location = new Point(0, 88),
            Size = new Size(432, 26),
            PlaceholderText = @"Ex: C:\Backups\meubanco.bak"
        };
        var btnAdicionarCaminho = new Button
        {
            Text = "Add...",
            Location = new Point(438, 87),
            Size = new Size(98, 28),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnAdicionarCaminho.Click += async (_, _) =>
        {
            // Pedido do usuário (funcionário testou): em instância remota, o
            // SaveFileDialog abaixo mostraria o disco DESTE computador, não o
            // do servidor — que é onde "TO DISK = ..." de fato precisa
            // existir. Isso confundia mesmo depois do aviso (o funcionário
            // navegava/escolhia um arquivo pela janela e acabava montando um
            // caminho do disco local/rede mapeada, não do servidor). Ajuste
            // final: em instância remota, mostra SÓ o aviso (com a lista real
            // de discos do servidor) e NÃO abre o diálogo — o caminho tem que
            // ser digitado manualmente no campo de texto. A validação real
            // contra o servidor (xp_fileexist) roda depois, ao clicar em
            // "Executar". Em instância local o diálogo continua abrindo
            // normalmente, pois nesse caso ele já mostra o disco certo.
            var aviso = await ObterAvisoInstanciaRemotaAsync();
            if (aviso != null)
            {
                MessageBox.Show(this, aviso, "SQL Rocket — instância remota", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using var dialogo = new SaveFileDialog
                {
                    Title = "Selecionar destino do backup",
                    Filter = "Arquivo de backup SQL Server (*.bak)|*.bak|Todos os arquivos (*.*)|*.*",
                    DefaultExt = "bak",
                    AddExtension = true
                };

                if (dialogo.ShowDialog(this) == DialogResult.OK)
                {
                    txtCaminho.Text = dialogo.FileName;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MostrarAvisoSemPermissao(this, ex);
            }
        };

        var chkCopyOnly = new CheckBox
        {
            Text = "COPY-ONLY BACKUP",
            Font = fonteCheckbox,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 132)
        };
        var chkCompressao = new CheckBox
        {
            Text = "COMPRESSÃO DE BACKUP",
            Font = fonteCheckbox,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 164)
        };
        var chkVerificarIntegridade = new CheckBox
        {
            Text = "VERIFICAR INTEGRIDADE DO BACKUP",
            Font = fonteCheckbox,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 196)
        };

        var lblStatus = new Label
        {
            Text = "Carregando bancos de dados...",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = CorTextoSecundario,
            AutoSize = true,
            Location = new Point(0, 244)
        };
        var prgBackup = new ProgressBar
        {
            Location = new Point(0, 268),
            Size = new Size(536, 22),
            Style = ProgressBarStyle.Marquee
        };

        var btnExecutar = CriarBotaoAcao("Executar Backup");
        btnExecutar.Location = new Point(0, 312);
        btnExecutar.Size = new Size(180, 38);

        // Painel "Messages" (estilo console/SSMS) — mostra em tempo real as
        // mensagens que o SQL Server envia durante o backup (ex: "5 percent
        // processed."). Ancorado nos 4 lados: ocupa todo o espaço restante do
        // cartão (largura e altura), em vez de ficar com uma faixa de tela em
        // branco ao lado. Também tem rolagem própria (ScrollBars.Vertical)
        // para o texto das mensagens.
        var txtMensagens = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 9F),
            BackColor = Color.White,
            ForeColor = CorTitulo,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(0, 364),
            Size = new Size(536, 260),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        // Painel interno Dock=Fill: ocupa toda a área disponível do cartão
        // (assim o console acima esticado por Anchor usa a largura/altura
        // reais da janela, sem sobra de tela em branco). MinimumSize garante
        // que, se a janela for menor que o necessário para exibir todos os
        // campos, o painel não encolhe além disso — e como o cartão tem
        // AutoScroll = true (ver CriarCartao), uma barra de rolagem aparece
        // automaticamente nesse caso, garantindo que sempre dá para rolar até
        // o fim.
        var pnlFormulario = new Panel
        {
            Dock = DockStyle.Fill,
            MinimumSize = new Size(536, 624)
        };
        pnlFormulario.Controls.Add(lblBanco);
        pnlFormulario.Controls.Add(cmbBancoDados);
        pnlFormulario.Controls.Add(lblCaminho);
        pnlFormulario.Controls.Add(txtCaminho);
        pnlFormulario.Controls.Add(btnAdicionarCaminho);
        pnlFormulario.Controls.Add(chkCopyOnly);
        pnlFormulario.Controls.Add(chkCompressao);
        pnlFormulario.Controls.Add(chkVerificarIntegridade);
        pnlFormulario.Controls.Add(lblStatus);
        pnlFormulario.Controls.Add(prgBackup);
        pnlFormulario.Controls.Add(btnExecutar);
        pnlFormulario.Controls.Add(txtMensagens);

        cartao.Controls.Add(pnlFormulario);

        async Task CarregarBancosDadosAsync()
        {
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBancoDados.Items.Clear();
                cmbBancoDados.Items.AddRange(bancos.ToArray());
                lblStatus.Text = $"{bancos.Count} banco(s) de dados encontrado(s).";
            }
            catch (Exception ex)
            {
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                prgBackup.Style = ProgressBarStyle.Continuous;
                prgBackup.Value = 0;
            }
        }

        // Ao abrir a tela: SELECT SERVERPROPERTY('Edition') — em edições
        // Express/Web, BACKUP ... WITH COMPRESSION não é suportado, então a
        // opção fica desabilitada (Modulo_Admin também reforça essa regra ao
        // montar o script, então mesmo se algo desmarcar isso o backup sai correto).
        async Task VerificarSuporteACompressaoAsync()
        {
            try
            {
                var edicao = await _moduloAdmin.ObterEdicaoAsync();
                if (!Modulo_Admin.SuportaCompressaoDeBackup(edicao))
                {
                    chkCompressao.Checked = false;
                    chkCompressao.Enabled = false;
                }
            }
            catch
            {
                // Não bloqueia a tela se a edição não puder ser determinada agora;
                // Modulo_Admin volta a checar isso ao executar o backup.
            }
        }

        async void Executar()
        {
            var banco = cmbBancoDados.Text.Trim();
            var caminho = txtCaminho.Text.Trim();

            if (string.IsNullOrWhiteSpace(banco))
            {
                MessageBox.Show(this, "Selecione ou informe o banco de dados.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(caminho))
            {
                MessageBox.Show(this, "Informe o caminho de destino do backup.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Confirmação simples antes de executar (pedido do usuário).
            //
            // No lugar do aviso longo que existia aqui: a tela conferia no
            // próprio servidor (xp_fileexist) se a pasta de destino existia e,
            // quando não encontrava, exibia um alerta explicando que o caminho
            // precisa existir no disco do SERVIDOR. Na prática o aviso aparecia
            // em situações em que o backup rodava normalmente, então virava só
            // um diálogo a mais para dispensar. A verificação foi removida —
            // se o caminho realmente não existir, o próprio SQL Server devolve
            // o erro, que já é exibido no painel de mensagens da tela.
            var confirmarExecucao = MessageBox.Show(this,
                "Deseja executar o backup?",
                "SQL Rocket",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirmarExecucao != DialogResult.Yes)
            {
                return;
            }

            // O arquivo de destino já existe no servidor? Então o usuário
            // PRECISA escolher entre anexar e sobrescrever.
            //
            // Até a v1.0 o backup usava sempre "WITH INIT, SKIP", isto é,
            // apagava em silêncio tudo o que já estivesse no arquivo — e a tela
            // só avisava quando o caminho NÃO existia, ou seja, justamente o
            // caso perigoso passava batido. Apontar para o arquivo do job
            // noturno apagava o backup da noite anterior; se o backup novo
            // falhasse no meio (disco cheio), perdiam-se os dois.
            var sobrescreverArquivo = false;
            var verificacaoArquivo = await _moduloAdmin.VerificarCaminhoNoServidorAsync(caminho);
            if (verificacaoArquivo.HasValue && verificacaoArquivo.Value.Existe && !verificacaoArquivo.Value.EhDiretorio)
            {
                var escolha = MessageBox.Show(this,
                    $"O arquivo \"{caminho}\" JÁ EXISTE no servidor e provavelmente contém backups anteriores.\n\n" +
                    "• Sim = ANEXAR: o novo backup é acrescentado ao arquivo e os anteriores são preservados " +
                    "(recomendado; o Restore desta ferramenta usa sempre o backup mais recente do arquivo).\n\n" +
                    "• Não = SOBRESCREVER: apaga TODOS os backups já existentes nesse arquivo antes de gravar. " +
                    "Se o backup novo falhar no meio, os anteriores também serão perdidos.\n\n" +
                    "Deseja ANEXAR (Sim) ou SOBRESCREVER (Não)?",
                    "SQL Rocket — o arquivo de backup já existe",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

                if (escolha == DialogResult.Cancel)
                {
                    return;
                }

                sobrescreverArquivo = escolha == DialogResult.No;
            }

            var opcoes = new BackupOpcoes
            {
                CopyOnly = chkCopyOnly.Checked,
                Compressao = chkCompressao.Checked,
                VerificarIntegridade = chkVerificarIntegridade.Checked,
                SobrescreverArquivo = sobrescreverArquivo
            };

            btnExecutar.Enabled = false;
            // Marca a operação como destrutiva/longa em andamento para que
            // "Sair"/fechar a janela peçam confirmação no meio de um backup
            // (ver _operacoesDestrutivasEmAndamento). Zerado no finally abaixo.
            _operacoesDestrutivasEmAndamento++;
            Cursor = Cursors.WaitCursor;
            lblStatus.Text = "Executando backup...";
            prgBackup.Style = ProgressBarStyle.Marquee;
            txtMensagens.Clear();

            // IProgress<T> captura o SynchronizationContext da thread atual
            // (a UI thread, já que estamos dentro do handler de clique) e
            // garante que Report() sempre volte para essa mesma thread — as
            // mensagens do SQL Server chegam em segundo plano durante a
            // execução do comando, então tocar o TextBox direto ali não
            // seria thread-safe.
            //
            // Cada mensagem força rolagem até o final e um repaint imediato
            // (Update(), em vez de esperar o próximo ciclo do message loop),
            // para que a atualização apareça na tela assim que chega, mesmo
            // quando várias mensagens são reportadas em rápida sucessão.
            var progresso = new Progress<string>(mensagem =>
            {
                txtMensagens.AppendText(mensagem + Environment.NewLine);
                txtMensagens.SelectionStart = txtMensagens.TextLength;
                txtMensagens.ScrollToCaret();
                txtMensagens.Update();
            });

            try
            {
                var resultado = await _moduloAdmin.ExecutarBackupFullAsync(banco, caminho, opcoes, progresso);
                lblStatus.Text = resultado.Sucesso
                    ? $"Backup concluído em {resultado.Duracao}."
                    : $"Falha: {resultado.MensagemDetalhe}";
            }
            catch (Exception ex)
            {
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                _operacoesDestrutivasEmAndamento--;
                prgBackup.Style = ProgressBarStyle.Continuous;
                prgBackup.Value = 0;
                Cursor = Cursors.Default;
                btnExecutar.Enabled = true;
            }
        }

        btnExecutar.Click += (_, _) => Executar();

        DefinirConteudo(cartao);

        _ = CarregarBancosDadosAsync();
        _ = VerificarSuporteACompressaoAsync();
    }

    /// <summary>
    /// Página "Backup > Restore": banco de destino, caminho do arquivo de
    /// backup (.bak) e os caminhos onde os arquivos de dados (MDF) e de log
    /// (LDF) devem ser recriados — equivalente às cláusulas MOVE de um
    /// RESTORE DATABASE. Assim como "Backup > Full", fica embutida no painel
    /// de conteúdo (sem janela separada) e mostra as mensagens do SQL Server
    /// em tempo real durante a execução.
    ///
    /// Os nomes lógicos exigidos pelo MOVE (definidos no banco de origem, não
    /// digitáveis pelo usuário) são obtidos automaticamente a partir do
    /// próprio arquivo de backup (RESTORE FILELISTONLY) em
    /// Modulo_Admin.ExecutarRestoreAsync — a tela só pede os 4 campos que o
    /// usuário realmente precisa informar.
    /// </summary>
    private void MostrarPaginaRestore()
    {
        lblTituloPagina.Text = "Backup > Restore";

        var cartao = CriarCartao();

        var fonteRotulo = new Font("Segoe UI", 10F, FontStyle.Bold);

        var lblBanco = new Label
        {
            Text = "Nome do Banco de Dados",
            Font = fonteRotulo,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var txtBanco = new TextBox
        {
            Location = new Point(0, 24),
            Size = new Size(536, 26),
            PlaceholderText = "Ex: MeuBanco"
        };

        var lblCaminhoBackup = new Label
        {
            Text = "Caminho do Arquivo de Backup",
            Font = fonteRotulo,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 64)
        };
        var txtCaminhoBackup = new TextBox
        {
            Location = new Point(0, 88),
            Size = new Size(432, 26),
            PlaceholderText = @"Ex: C:\Backups\meubanco.bak"
        };
        var btnSelecionarBackup = new Button
        {
            Text = "Procurar...",
            Location = new Point(438, 87),
            Size = new Size(98, 28),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };

        var lblCaminhoMdf = new Label
        {
            Text = "Caminho do Arquivo MDF",
            Font = fonteRotulo,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 128)
        };
        var txtCaminhoMdf = new TextBox
        {
            Location = new Point(0, 152),
            Size = new Size(432, 26),
            PlaceholderText = @"Ex: C:\Dados\meubanco.mdf"
        };
        var btnSelecionarMdf = new Button
        {
            Text = "Procurar...",
            Location = new Point(438, 151),
            Size = new Size(98, 28),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };

        var lblCaminhoLdf = new Label
        {
            Text = "Caminho do Arquivo LDF",
            Font = fonteRotulo,
            ForeColor = CorTitulo,
            AutoSize = true,
            Location = new Point(0, 192)
        };
        var txtCaminhoLdf = new TextBox
        {
            Location = new Point(0, 216),
            Size = new Size(432, 26),
            PlaceholderText = @"Ex: C:\Dados\meubanco_log.ldf"
        };
        var btnSelecionarLdf = new Button
        {
            Text = "Procurar...",
            Location = new Point(438, 215),
            Size = new Size(98, 28),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };

        // Pedido do usuário (funcionário testou): os 3 caminhos abaixo
        // (arquivo de backup, MDF, LDF) são todos resolvidos pelo SERVIÇO do
        // SQL Server (RESTORE ... FROM DISK / MOVE ... TO), não pelo
        // computador onde a SQL Rocket está rodando — em instância
        // remota, os diálogos nativos do Windows abaixo mostrariam o disco
        // ERRADO. Mesmo aviso (com a lista real de discos do servidor,
        // quando disponível) usado em "Backup > Full" — e mesmo ajuste final:
        // em instância remota, mostra SÓ o aviso e NÃO abre o diálogo (o
        // caminho é digitado manualmente); em instância local o diálogo
        // continua abrindo normalmente.
        btnSelecionarBackup.Click += async (_, _) =>
        {
            var aviso = await ObterAvisoInstanciaRemotaAsync();
            if (aviso != null)
            {
                MessageBox.Show(this, aviso, "SQL Rocket — instância remota", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using var dialogo = new OpenFileDialog
                {
                    Title = "Selecionar arquivo de backup",
                    Filter = "Arquivo de backup SQL Server (*.bak)|*.bak|Todos os arquivos (*.*)|*.*",
                    CheckFileExists = true
                };

                if (dialogo.ShowDialog(this) == DialogResult.OK)
                {
                    txtCaminhoBackup.Text = dialogo.FileName;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MostrarAvisoSemPermissao(this, ex);
            }
        };
        btnSelecionarMdf.Click += async (_, _) =>
        {
            var aviso = await ObterAvisoInstanciaRemotaAsync();
            if (aviso != null)
            {
                MessageBox.Show(this, aviso, "SQL Rocket — instância remota", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using var dialogo = new SaveFileDialog
                {
                    Title = "Selecionar destino do arquivo de dados (MDF)",
                    Filter = "Arquivo de Dados (*.mdf)|*.mdf|Todos os arquivos (*.*)|*.*",
                    DefaultExt = "mdf",
                    AddExtension = true,
                    OverwritePrompt = false
                };

                if (dialogo.ShowDialog(this) == DialogResult.OK)
                {
                    txtCaminhoMdf.Text = dialogo.FileName;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MostrarAvisoSemPermissao(this, ex);
            }
        };
        btnSelecionarLdf.Click += async (_, _) =>
        {
            var aviso = await ObterAvisoInstanciaRemotaAsync();
            if (aviso != null)
            {
                MessageBox.Show(this, aviso, "SQL Rocket — instância remota", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using var dialogo = new SaveFileDialog
                {
                    Title = "Selecionar destino do arquivo de log (LDF)",
                    Filter = "Arquivo de Log (*.ldf)|*.ldf|Todos os arquivos (*.*)|*.*",
                    DefaultExt = "ldf",
                    AddExtension = true,
                    OverwritePrompt = false
                };

                if (dialogo.ShowDialog(this) == DialogResult.OK)
                {
                    txtCaminhoLdf.Text = dialogo.FileName;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MostrarAvisoSemPermissao(this, ex);
            }
        };

        var lblStatus = new Label
        {
            Text = "Informe os dados e clique em \"Executar Restore\".",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = CorTextoSecundario,
            AutoSize = true,
            Location = new Point(0, 260)
        };
        var prgRestore = new ProgressBar
        {
            Location = new Point(0, 284),
            Size = new Size(536, 22),
            Style = ProgressBarStyle.Continuous
        };

        var btnExecutar = CriarBotaoAcao("Executar Restore");
        btnExecutar.Location = new Point(0, 328);
        btnExecutar.Size = new Size(180, 38);

        // Mesmo painel "Messages" (estilo console/SSMS) usado em Backup > Full,
        // mostrando em tempo real as mensagens do SQL Server durante o
        // restore. Ancorado nos 4 lados: ocupa todo o espaço restante do
        // cartão (largura e altura), em vez de ficar com uma faixa de tela em
        // branco ao lado. Também tem rolagem própria (ScrollBars.Vertical)
        // para o texto das mensagens.
        var txtMensagens = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 9F),
            BackColor = Color.White,
            ForeColor = CorTitulo,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(0, 380),
            Size = new Size(536, 260),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        // Painel interno Dock=Fill: ocupa toda a área disponível do cartão
        // (assim o console acima esticado por Anchor usa a largura/altura
        // reais da janela, sem sobra de tela em branco). MinimumSize garante
        // que, se a janela for menor que o necessário para exibir todos os
        // campos, o painel não encolhe além disso — e como o cartão tem
        // AutoScroll = true (ver CriarCartao), uma barra de rolagem aparece
        // automaticamente nesse caso, garantindo que sempre dá para rolar até
        // o fim.
        var pnlFormulario = new Panel
        {
            Dock = DockStyle.Fill,
            MinimumSize = new Size(536, 640)
        };
        pnlFormulario.Controls.Add(lblBanco);
        pnlFormulario.Controls.Add(txtBanco);
        pnlFormulario.Controls.Add(lblCaminhoBackup);
        pnlFormulario.Controls.Add(txtCaminhoBackup);
        pnlFormulario.Controls.Add(btnSelecionarBackup);
        pnlFormulario.Controls.Add(lblCaminhoMdf);
        pnlFormulario.Controls.Add(txtCaminhoMdf);
        pnlFormulario.Controls.Add(btnSelecionarMdf);
        pnlFormulario.Controls.Add(lblCaminhoLdf);
        pnlFormulario.Controls.Add(txtCaminhoLdf);
        pnlFormulario.Controls.Add(btnSelecionarLdf);
        pnlFormulario.Controls.Add(lblStatus);
        pnlFormulario.Controls.Add(prgRestore);
        pnlFormulario.Controls.Add(btnExecutar);
        pnlFormulario.Controls.Add(txtMensagens);

        cartao.Controls.Add(pnlFormulario);

        async void Executar()
        {
            var banco = txtBanco.Text.Trim();
            var caminhoBackup = txtCaminhoBackup.Text.Trim();
            var caminhoMdf = txtCaminhoMdf.Text.Trim();
            var caminhoLdf = txtCaminhoLdf.Text.Trim();

            if (string.IsNullOrWhiteSpace(banco))
            {
                MessageBox.Show(this, "Informe o nome do banco de dados.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(caminhoBackup) || string.IsNullOrWhiteSpace(caminhoMdf) || string.IsNullOrWhiteSpace(caminhoLdf))
            {
                MessageBox.Show(this, "Informe o caminho do backup e os caminhos de destino do MDF e do LDF.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Pedido do usuário (funcionário testou, com screenshot): mesmo
            // problema de "Backup > Full" — os diálogos nativos mostram os
            // discos/redes mapeadas DESTE computador, sem relação garantida
            // com o disco do servidor. Pergunta pro PRÓPRIO SERVIDOR (via
            // xp_fileexist) se o arquivo de backup e as pastas de destino do
            // MDF/LDF realmente existem lá, antes da confirmação destrutiva
            // abaixo — os 3 problemas (se houver) são juntados numa única
            // mensagem em vez de 3 diálogos separados. Só avisa quando a
            // resposta é claramente "não existe"; se não der pra confirmar
            // (xp_fileexist indisponível), segue em frente sem bloquear.
            var problemasCaminho = new List<string>();

            var verificacaoBackup = await _moduloAdmin.VerificarCaminhoNoServidorAsync(caminhoBackup);
            if (verificacaoBackup.HasValue && !(verificacaoBackup.Value.Existe && !verificacaoBackup.Value.EhDiretorio))
            {
                problemasCaminho.Add($"Arquivo de backup \"{caminhoBackup}\" não encontrado no servidor.");
            }

            var pastaMdf = Path.GetDirectoryName(caminhoMdf);
            if (!string.IsNullOrWhiteSpace(pastaMdf))
            {
                var verificacaoMdf = await _moduloAdmin.VerificarCaminhoNoServidorAsync(pastaMdf);
                if (verificacaoMdf.HasValue && !(verificacaoMdf.Value.Existe && verificacaoMdf.Value.EhDiretorio))
                {
                    problemasCaminho.Add($"Pasta de destino do MDF \"{pastaMdf}\" não encontrada no servidor.");
                }
            }

            var pastaLdf = Path.GetDirectoryName(caminhoLdf);
            if (!string.IsNullOrWhiteSpace(pastaLdf))
            {
                var verificacaoLdf = await _moduloAdmin.VerificarCaminhoNoServidorAsync(pastaLdf);
                if (verificacaoLdf.HasValue && !(verificacaoLdf.Value.Existe && verificacaoLdf.Value.EhDiretorio))
                {
                    problemasCaminho.Add($"Pasta de destino do LDF \"{pastaLdf}\" não encontrada no servidor.");
                }
            }

            if (problemasCaminho.Count > 0)
            {
                var confirmarMesmoAssim = MessageBox.Show(this,
                    "Os seguintes caminhos NÃO foram encontrados no disco do SERVIDOR SQL Server conectado " +
                    "(verificado agora no próprio servidor) — o restore provavelmente vai falhar:\n\n" +
                    string.Join("\n", problemasCaminho.Select(p => $"• {p}")) +
                    "\n\nLembre-se: esses caminhos precisam existir no disco do SERVIDOR, não neste computador " +
                    "(mesmo que tenham vindo de uma pasta/unidade de rede escolhida no diálogo).\n\n" +
                    "Deseja tentar executar o restore mesmo assim?",
                    "SQL Rocket — caminho(s) não encontrado(s) no servidor",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (confirmarMesmoAssim != DialogResult.Yes)
                {
                    return;
                }
            }

            // RESTORE DATABASE sobrescreve o conteúdo do banco de destino — se
            // já existir um banco com esse nome, seus dados atuais são
            // perdidos. Confirmação explícita antes de uma operação destrutiva.
            var confirmacao = MessageBox.Show(this,
                $"Isso irá restaurar o backup sobre o banco \"{banco}\", substituindo os dados atuais " +
                "caso ele já exista. Deseja continuar?",
                "Confirmar Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            btnExecutar.Enabled = false;
            // RESTORE é a operação mais destrutiva do app (sobrescreve o banco
            // de destino): marca o andamento para que "Sair"/fechar a janela
            // peçam confirmação (ver _operacoesDestrutivasEmAndamento).
            _operacoesDestrutivasEmAndamento++;
            Cursor = Cursors.WaitCursor;
            lblStatus.Text = "Executando restore...";
            prgRestore.Style = ProgressBarStyle.Marquee;
            txtMensagens.Clear();

            var progresso = new Progress<string>(mensagem =>
            {
                txtMensagens.AppendText(mensagem + Environment.NewLine);
                txtMensagens.SelectionStart = txtMensagens.TextLength;
                txtMensagens.ScrollToCaret();
                txtMensagens.Update();
            });

            try
            {
                var resultado = await _moduloAdmin.ExecutarRestoreAsync(banco, caminhoBackup, caminhoMdf, caminhoLdf, progresso);
                lblStatus.Text = resultado.Sucesso
                    ? $"Restore concluído em {resultado.Duracao}."
                    : $"Falha: {resultado.MensagemDetalhe}";
            }
            catch (Exception ex)
            {
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                _operacoesDestrutivasEmAndamento--;
                prgRestore.Style = ProgressBarStyle.Continuous;
                prgRestore.Value = 0;
                Cursor = Cursors.Default;
                btnExecutar.Enabled = true;
            }
        }

        btnExecutar.Click += (_, _) => Executar();

        DefinirConteudo(cartao);
    }

    /// <summary>
    /// "Comparador &gt; Índices" — compara os índices de um banco da
    /// própria instância (Origem) contra outro banco, escolhido pelo
    /// usuário como outro banco da MESMA instância ou um banco de um
    /// servidor totalmente diferente (Destino). Pedido do usuário, sem
    /// script fornecido — T-SQL próprio (ver
    /// <see cref="Modulo_Comparador.CompararIndicesAsync"/>/
    /// <see cref="Modulo_Comparador.AplicarIndicesAsync"/>).
    /// </summary>
    /// <remarks>
    /// Decisões tomadas com o usuário antes de implementar: (1) "Aplicar"
    /// tem dois botões — um por sentido (Origem → Destino / Destino →
    /// Origem) — tanto para o índice selecionado quanto para todos os
    /// diferentes/ausentes de uma vez; (2) "igual"/"diferente" considera
    /// nome (usado para casar os índices), unicidade, tipo
    /// (CLUSTERED/NONCLUSTERED), colunas-chave (ordem e direção) e colunas
    /// incluídas — não considera fill factor nem filtro (WHERE).
    /// Linhas "Diferente" ficam em amarelo, "Ausente na Origem/no Destino"
    /// em vermelho (mesmas cores já usadas nos destaques de bloqueio do
    /// Active Monitor), "Igual" sem destaque. Um "Aplicar" nunca exclui um
    /// índice sem ter uma definição para recriar no lugar (ver comentário em
    /// <see cref="Modulo_Comparador.AplicarIndicesAsync"/>) — evita apagar
    /// por engano um índice que só existe de um lado.
    /// </remarks>
    private void MostrarPaginaComparadorIndices()
    {
        lblTituloPagina.Text = "Comparador > Índices";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tabela", HeaderText = "Tabela", DataPropertyName = "Tabela", Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NomeIndice", HeaderText = "Índice", DataPropertyName = "NomeIndice", Width = 200 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", DataPropertyName = "Status", Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DescricaoOrigem", HeaderText = "Definição na Origem", DataPropertyName = "DescricaoOrigem", Width = 260 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DescricaoDestino", HeaderText = "Definição no Destino", DataPropertyName = "DescricaoDestino", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        // ---- Topo: seleção dos dois bancos a comparar ----
        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 218 };

        var lblBancoOrigem = new Label { Text = "Banco de Dados (minha instância)", AutoSize = true, Location = new Point(0, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbBancoOrigem = new ComboBox { Location = new Point(0, 26), Size = new Size(260, 26), DropDownStyle = ComboBoxStyle.DropDownList };

        var lblCompararCom = new Label { Text = "Comparar com:", AutoSize = true, Location = new Point(300, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var radioMesmaInstancia = new RadioButton { Text = "Outro banco nesta mesma instância", AutoSize = true, Location = new Point(300, 24), Checked = true };
        var radioOutroLocal = new RadioButton { Text = "Banco em outro local (outro servidor)", AutoSize = true, Location = new Point(300, 46) };

        var pnlMesmaInstancia = new Panel { Location = new Point(0, 66), Size = new Size(620, 46) };
        var lblBancoDestinoMesma = new Label { Text = "Banco de Dados (destino)", AutoSize = true, Location = new Point(0, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbBancoDestinoMesma = new ComboBox { Location = new Point(0, 22), Size = new Size(260, 26), DropDownStyle = ComboBoxStyle.DropDownList };
        pnlMesmaInstancia.Controls.Add(lblBancoDestinoMesma);
        pnlMesmaInstancia.Controls.Add(cmbBancoDestinoMesma);

        var pnlOutroLocal = new Panel { Location = new Point(0, 66), Size = new Size(620, 100), Visible = false };
        var lblServidorDestino = new Label { Text = "Servidor", AutoSize = true, Location = new Point(0, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtServidorDestino = new TextBox { Location = new Point(0, 22), Size = new Size(200, 26), PlaceholderText = @"Ex.: SERVIDOR\INSTANCIA" };
        var lblBancoDestinoOutro = new Label { Text = "Banco de Dados", AutoSize = true, Location = new Point(220, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtBancoDestino = new TextBox { Location = new Point(220, 22), Size = new Size(180, 26) };
        var lblUsuarioDestino = new Label { Text = "Usuário", AutoSize = true, Location = new Point(0, 56), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtUsuarioDestino = new TextBox { Location = new Point(0, 78), Size = new Size(180, 26) };
        var lblSenhaDestino = new Label { Text = "Senha", AutoSize = true, Location = new Point(200, 56), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtSenhaDestino = new TextBox { Location = new Point(200, 78), Size = new Size(180, 26), UseSystemPasswordChar = true };
        pnlOutroLocal.Controls.Add(lblServidorDestino);
        pnlOutroLocal.Controls.Add(txtServidorDestino);
        pnlOutroLocal.Controls.Add(lblBancoDestinoOutro);
        pnlOutroLocal.Controls.Add(txtBancoDestino);
        pnlOutroLocal.Controls.Add(lblUsuarioDestino);
        pnlOutroLocal.Controls.Add(txtUsuarioDestino);
        pnlOutroLocal.Controls.Add(lblSenhaDestino);
        pnlOutroLocal.Controls.Add(txtSenhaDestino);

        var btnComparar = CriarBotaoAcao("Comparar");
        btnComparar.Location = new Point(0, 176);

        pnlTopo.Controls.Add(lblBancoOrigem);
        pnlTopo.Controls.Add(cmbBancoOrigem);
        pnlTopo.Controls.Add(lblCompararCom);
        pnlTopo.Controls.Add(radioMesmaInstancia);
        pnlTopo.Controls.Add(radioOutroLocal);
        pnlTopo.Controls.Add(pnlMesmaInstancia);
        pnlTopo.Controls.Add(pnlOutroLocal);
        pnlTopo.Controls.Add(btnComparar);

        var (pnlResumo, lblResumoTotal, lblResumoIguais, lblResumoDiferentes, lblResumoAusentes) = CriarPainelResumoComparacao();
        pnlTopo.Controls.Add(pnlResumo);

        void AtualizarVisibilidadeDestino()
        {
            pnlMesmaInstancia.Visible = radioMesmaInstancia.Checked;
            pnlOutroLocal.Visible = radioOutroLocal.Checked;
        }
        radioMesmaInstancia.CheckedChanged += (_, _) => AtualizarVisibilidadeDestino();
        radioOutroLocal.CheckedChanged += (_, _) => AtualizarVisibilidadeDestino();
        AtualizarVisibilidadeDestino();

        // ---- Rodapé: aplicar (selecionado / tudo, nos 2 sentidos) + status ----
        var pnlAcoes = new Panel { Dock = DockStyle.Bottom, Height = 106, Padding = new Padding(0, 10, 0, 0) };

        var lblAcaoSelecionado = new Label { Text = "Índice selecionado:", AutoSize = true, Location = new Point(0, 16), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var btnAplicarSelOD = CriarBotaoAcao("Aplicar Origem → Destino");
        btnAplicarSelOD.Location = new Point(200, 10);
        btnAplicarSelOD.Size = new Size(200, 30);
        var btnAplicarSelDO = CriarBotaoAcao("Aplicar Destino → Origem");
        btnAplicarSelDO.Location = new Point(410, 10);
        btnAplicarSelDO.Size = new Size(200, 30);

        var lblAcaoTudo = new Label { Text = "Todos os diferentes/ausentes:", AutoSize = true, Location = new Point(0, 58), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var btnAplicarTudoOD = CriarBotaoAcao("Aplicar Tudo Origem → Destino");
        btnAplicarTudoOD.Location = new Point(200, 52);
        btnAplicarTudoOD.Size = new Size(200, 30);
        var btnAplicarTudoDO = CriarBotaoAcao("Aplicar Tudo Destino → Origem");
        btnAplicarTudoDO.Location = new Point(410, 52);
        btnAplicarTudoDO.Size = new Size(200, 30);

        var lblStatus = new Label
        {
            Location = new Point(0, 88),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = CorTextoSecundario
        };

        pnlAcoes.Controls.Add(lblAcaoSelecionado);
        pnlAcoes.Controls.Add(btnAplicarSelOD);
        pnlAcoes.Controls.Add(btnAplicarSelDO);
        pnlAcoes.Controls.Add(lblAcaoTudo);
        pnlAcoes.Controls.Add(btnAplicarTudoOD);
        pnlAcoes.Controls.Add(btnAplicarTudoDO);
        pnlAcoes.Controls.Add(lblStatus);

        void SetBotoesAplicar(bool habilitado)
        {
            btnAplicarSelOD.Enabled = habilitado;
            btnAplicarSelDO.Enabled = habilitado;
            btnAplicarTudoOD.Enabled = habilitado;
            btnAplicarTudoDO.Enabled = habilitado;
        }
        SetBotoesAplicar(false);

        List<ComparacaoIndiceDto>? resultadoAtual = null;
        Conectar_SQL? conexaoDestinoAtual = null;
        string? bancoOrigemAtual = null;
        string? bancoDestinoAtual = null;

        void AplicarCoresLinhas()
        {
            foreach (DataGridViewRow linha in grid.Rows)
            {
                if (linha.DataBoundItem is not ComparacaoIndiceDto comparacao)
                {
                    continue;
                }

                if (comparacao.Status == "Diferente")
                {
                    linha.DefaultCellStyle.BackColor = Color.FromArgb(255, 236, 204);
                    linha.DefaultCellStyle.ForeColor = Color.FromArgb(140, 80, 0);
                }
                else if (comparacao.Status.StartsWith("Ausente", StringComparison.Ordinal))
                {
                    linha.DefaultCellStyle.BackColor = Color.FromArgb(255, 224, 224);
                    linha.DefaultCellStyle.ForeColor = Color.FromArgb(140, 20, 20);
                }
            }
        }

        async Task ExecutarComparacaoAsync(string bancoOrigem, Conectar_SQL conexaoDestino, string bancoDestino)
        {
            btnComparar.Enabled = false;
            SetBotoesAplicar(false);
            Cursor = Cursors.WaitCursor;
            lblStatus.Text = "Comparando...";
            try
            {
                resultadoAtual = await _moduloComparador.CompararIndicesAsync(_conectarSql, bancoOrigem, conexaoDestino, bancoDestino);
                bancoOrigemAtual = bancoOrigem;
                conexaoDestinoAtual = conexaoDestino;
                bancoDestinoAtual = bancoDestino;

                grid.DataSource = resultadoAtual;
                AplicarCoresLinhas();

                var diferentes = resultadoAtual.Count(r => r.Status == "Diferente");
                var ausentes = resultadoAtual.Count(r => r.Status.StartsWith("Ausente", StringComparison.Ordinal));
                var iguais = resultadoAtual.Count - diferentes - ausentes;
                lblStatus.Text = $"{resultadoAtual.Count} índice(s) comparado(s): {iguais} igual(is), {diferentes} diferente(s), {ausentes} ausente(s) — {DateTime.Now:HH:mm:ss}.";
                AtualizarResumoComparacao(lblResumoTotal, lblResumoIguais, lblResumoDiferentes, lblResumoAusentes, resultadoAtual.Count, iguais, diferentes, ausentes, "índices");
                SetBotoesAplicar(resultadoAtual.Count > 0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Falha ao comparar.";
                LimparResumoComparacaoAposFalha(lblResumoTotal, lblResumoIguais, lblResumoDiferentes, lblResumoAusentes);
                SetBotoesAplicar(false);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnComparar.Enabled = true;
            }
        }

        async Task CompararCliqueAsync()
        {
            if (cmbBancoOrigem.SelectedItem is not string bancoOrigem)
            {
                MessageBox.Show(this, "Selecione o banco de dados da sua instância.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Conectar_SQL conexaoDestino;
            string bancoDestino;

            if (radioMesmaInstancia.Checked)
            {
                if (cmbBancoDestinoMesma.SelectedItem is not string bancoDestinoSelecionado)
                {
                    MessageBox.Show(this, "Selecione o banco de dados de destino.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (string.Equals(bancoOrigem, bancoDestinoSelecionado, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "Escolha dois bancos diferentes para comparar.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                conexaoDestino = _conectarSql;
                bancoDestino = bancoDestinoSelecionado;
            }
            else
            {
                var servidor = txtServidorDestino.Text.Trim();
                var banco = txtBancoDestino.Text.Trim();
                var usuario = txtUsuarioDestino.Text.Trim();
                if (string.IsNullOrWhiteSpace(servidor) || string.IsNullOrWhiteSpace(banco) || string.IsNullOrWhiteSpace(usuario))
                {
                    MessageBox.Show(this, "Preencha servidor, banco de dados e usuário do outro local.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var conexaoExterna = new Conectar_SQL();
                try
                {
                    conexaoExterna.ConfigurarConexao(new ConnectionSettings
                    {
                        ServerName = servidor,
                        InitialDatabase = banco,
                        TipoAutenticacao = TipoAutenticacao.SqlServerAuthentication,
                        UsuarioLogin = usuario,
                        Senha = txtSenhaDestino.Text,
                        // Certificado deve ser validado por padrão: aceitar
                        // qualquer certificado do servidor de destino abre a
                        // conexão para interceptação (man-in-the-middle).
                        // Instâncias com certificado autoassinado vão precisar
                        // de uma opção explícita na tela (item separado).
                        ConfiarCertificadoServidor = false
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                conexaoDestino = conexaoExterna;
                bancoDestino = banco;
            }

            await ExecutarComparacaoAsync(bancoOrigem, conexaoDestino, bancoDestino);
        }

        async Task AplicarAsync(bool origemParaDestino, bool apenasSelecionado)
        {
            if (resultadoAtual is null || conexaoDestinoAtual is null || bancoOrigemAtual is null || bancoDestinoAtual is null)
            {
                return;
            }

            List<ComparacaoIndiceDto> linhas;
            if (apenasSelecionado)
            {
                if (grid.CurrentRow?.DataBoundItem is not ComparacaoIndiceDto linhaSelecionada)
                {
                    MessageBox.Show(this, "Selecione uma linha na grade.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (linhaSelecionada.Status == "Igual")
                {
                    MessageBox.Show(this, "Este índice já está igual nos dois bancos — nada para aplicar.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                linhas = new List<ComparacaoIndiceDto> { linhaSelecionada };
            }
            else
            {
                linhas = resultadoAtual.Where(r => r.Status != "Igual").ToList();
                if (linhas.Count == 0)
                {
                    MessageBox.Show(this, "Não há índices diferentes ou ausentes para aplicar.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var direcaoTexto = origemParaDestino ? "Origem → Destino" : "Destino → Origem";
            var confirmacao = MessageBox.Show(this,
                $"Confirma aplicar {linhas.Count} índice(s) no sentido \"{direcaoTexto}\"?\n\n" +
                "Um índice diferente é recriado (DROP + CREATE) no lado que recebe a aplicação — em " +
                "tabelas grandes isso pode levar tempo e bloquear acessos durante a recriação.",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            btnComparar.Enabled = false;
            SetBotoesAplicar(false);
            Cursor = Cursors.WaitCursor;
            try
            {
                await _moduloComparador.AplicarIndicesAsync(_conectarSql, bancoOrigemAtual, conexaoDestinoAtual, bancoDestinoAtual, linhas, origemParaDestino);
                lblStatus.Text = $"{linhas.Count} índice(s) aplicado(s) ({direcaoTexto}) em {DateTime.Now:HH:mm:ss}. Atualizando comparação...";
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                btnComparar.Enabled = true;
                SetBotoesAplicar(true);
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            await ExecutarComparacaoAsync(bancoOrigemAtual, conexaoDestinoAtual, bancoDestinoAtual);
        }

        async Task CarregarBancosAsync()
        {
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBancoOrigem.Items.Clear();
                cmbBancoOrigem.Items.AddRange(bancos.ToArray());
                cmbBancoDestinoMesma.Items.Clear();
                cmbBancoDestinoMesma.Items.AddRange(bancos.ToArray());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbBancoOrigem.Items.Count > 0)
            {
                cmbBancoOrigem.SelectedIndex = 0;
            }
            cmbBancoDestinoMesma.SelectedIndex = cmbBancoDestinoMesma.Items.Count > 1 ? 1 : (cmbBancoDestinoMesma.Items.Count > 0 ? 0 : -1);
        }

        btnComparar.Click += (_, _) => _ = CompararCliqueAsync();
        btnAplicarSelOD.Click += (_, _) => _ = AplicarAsync(origemParaDestino: true, apenasSelecionado: true);
        btnAplicarSelDO.Click += (_, _) => _ = AplicarAsync(origemParaDestino: false, apenasSelecionado: true);
        btnAplicarTudoOD.Click += (_, _) => _ = AplicarAsync(origemParaDestino: true, apenasSelecionado: false);
        btnAplicarTudoDO.Click += (_, _) => _ = AplicarAsync(origemParaDestino: false, apenasSelecionado: false);

        cartao.Controls.Add(grid);
        cartao.Controls.Add(pnlAcoes);
        cartao.Controls.Add(pnlTopo);

        DefinirConteudo(cartao);

        _ = CarregarBancosAsync();
    }

    /// <summary>
    /// "Comparador &gt; Banco de dados" — compara a estrutura de
    /// tabelas/colunas de um banco da própria instância (Origem) contra
    /// outro banco, escolhido pelo usuário como outro banco da MESMA
    /// instância ou um banco de um servidor totalmente diferente
    /// (Destino). Mesmo padrão de tela de "Comparador &gt; Índices" — pedido
    /// do usuário, sem script fornecido — T-SQL próprio (ver
    /// <see cref="Modulo_Comparador.CompararBancosAsync"/>/
    /// <see cref="Modulo_Comparador.AplicarBancosAsync"/>).
    /// </summary>
    /// <remarks>
    /// Decisões tomadas com o usuário antes de implementar: (1) "igual"/
    /// "diferente" considera tipo, tamanho/precisão, nulidade e IDENTITY
    /// (não considera DEFAULT/collation/constraints — ver comentário em
    /// <see cref="Modulo_Comparador.CompararBancosAsync"/>); (2) quando uma
    /// tabela inteira está ausente do lado que recebe a aplicação, o
    /// "Aplicar" cria a tabela inteira com TODAS as colunas de origem —
    /// nunca uma tabela parcial, mesmo que só uma coluna dela tenha sido
    /// selecionada/esteja entre as diferentes. Mesmas cores já usadas em
    /// "Comparador &gt; Índices": "Diferente" em amarelo, "Ausente na
    /// Origem/no Destino" em vermelho, "Igual" sem destaque.
    /// </remarks>
    private void MostrarPaginaComparadorBanco()
    {
        lblTituloPagina.Text = "Comparador > Banco de dados";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tabela", HeaderText = "Tabela", DataPropertyName = "Tabela", Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NomeColuna", HeaderText = "Coluna", DataPropertyName = "NomeColuna", Width = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", DataPropertyName = "Status", Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DescricaoOrigem", HeaderText = "Definição na Origem", DataPropertyName = "DescricaoOrigem", Width = 240 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DescricaoDestino", HeaderText = "Definição no Destino", DataPropertyName = "DescricaoDestino", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        // ---- Topo: seleção dos dois bancos a comparar ----
        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 218 };

        var lblBancoOrigem = new Label { Text = "Banco de Dados (minha instância)", AutoSize = true, Location = new Point(0, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbBancoOrigem = new ComboBox { Location = new Point(0, 26), Size = new Size(260, 26), DropDownStyle = ComboBoxStyle.DropDownList };

        var lblCompararCom = new Label { Text = "Comparar com:", AutoSize = true, Location = new Point(300, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var radioMesmaInstancia = new RadioButton { Text = "Outro banco nesta mesma instância", AutoSize = true, Location = new Point(300, 24), Checked = true };
        var radioOutroLocal = new RadioButton { Text = "Banco em outro local (outro servidor)", AutoSize = true, Location = new Point(300, 46) };

        var pnlMesmaInstancia = new Panel { Location = new Point(0, 66), Size = new Size(620, 46) };
        var lblBancoDestinoMesma = new Label { Text = "Banco de Dados (destino)", AutoSize = true, Location = new Point(0, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbBancoDestinoMesma = new ComboBox { Location = new Point(0, 22), Size = new Size(260, 26), DropDownStyle = ComboBoxStyle.DropDownList };
        pnlMesmaInstancia.Controls.Add(lblBancoDestinoMesma);
        pnlMesmaInstancia.Controls.Add(cmbBancoDestinoMesma);

        var pnlOutroLocal = new Panel { Location = new Point(0, 66), Size = new Size(620, 100), Visible = false };
        var lblServidorDestino = new Label { Text = "Servidor", AutoSize = true, Location = new Point(0, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtServidorDestino = new TextBox { Location = new Point(0, 22), Size = new Size(200, 26), PlaceholderText = @"Ex.: SERVIDOR\INSTANCIA" };
        var lblBancoDestinoOutro = new Label { Text = "Banco de Dados", AutoSize = true, Location = new Point(220, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtBancoDestino = new TextBox { Location = new Point(220, 22), Size = new Size(180, 26) };
        var lblUsuarioDestino = new Label { Text = "Usuário", AutoSize = true, Location = new Point(0, 56), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtUsuarioDestino = new TextBox { Location = new Point(0, 78), Size = new Size(180, 26) };
        var lblSenhaDestino = new Label { Text = "Senha", AutoSize = true, Location = new Point(200, 56), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtSenhaDestino = new TextBox { Location = new Point(200, 78), Size = new Size(180, 26), UseSystemPasswordChar = true };
        pnlOutroLocal.Controls.Add(lblServidorDestino);
        pnlOutroLocal.Controls.Add(txtServidorDestino);
        pnlOutroLocal.Controls.Add(lblBancoDestinoOutro);
        pnlOutroLocal.Controls.Add(txtBancoDestino);
        pnlOutroLocal.Controls.Add(lblUsuarioDestino);
        pnlOutroLocal.Controls.Add(txtUsuarioDestino);
        pnlOutroLocal.Controls.Add(lblSenhaDestino);
        pnlOutroLocal.Controls.Add(txtSenhaDestino);

        var btnComparar = CriarBotaoAcao("Comparar");
        btnComparar.Location = new Point(0, 176);

        pnlTopo.Controls.Add(lblBancoOrigem);
        pnlTopo.Controls.Add(cmbBancoOrigem);
        pnlTopo.Controls.Add(lblCompararCom);
        pnlTopo.Controls.Add(radioMesmaInstancia);
        pnlTopo.Controls.Add(radioOutroLocal);
        pnlTopo.Controls.Add(pnlMesmaInstancia);
        pnlTopo.Controls.Add(pnlOutroLocal);
        pnlTopo.Controls.Add(btnComparar);

        var (pnlResumo, lblResumoTotal, lblResumoIguais, lblResumoDiferentes, lblResumoAusentes) = CriarPainelResumoComparacao();
        pnlTopo.Controls.Add(pnlResumo);

        void AtualizarVisibilidadeDestino()
        {
            pnlMesmaInstancia.Visible = radioMesmaInstancia.Checked;
            pnlOutroLocal.Visible = radioOutroLocal.Checked;
        }
        radioMesmaInstancia.CheckedChanged += (_, _) => AtualizarVisibilidadeDestino();
        radioOutroLocal.CheckedChanged += (_, _) => AtualizarVisibilidadeDestino();
        AtualizarVisibilidadeDestino();

        // ---- Rodapé: aplicar (selecionado / tudo, nos 2 sentidos) + status ----
        var pnlAcoes = new Panel { Dock = DockStyle.Bottom, Height = 106, Padding = new Padding(0, 10, 0, 0) };

        var lblAcaoSelecionado = new Label { Text = "Coluna selecionada:", AutoSize = true, Location = new Point(0, 16), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var btnAplicarSelOD = CriarBotaoAcao("Aplicar Origem → Destino");
        btnAplicarSelOD.Location = new Point(200, 10);
        btnAplicarSelOD.Size = new Size(200, 30);
        var btnAplicarSelDO = CriarBotaoAcao("Aplicar Destino → Origem");
        btnAplicarSelDO.Location = new Point(410, 10);
        btnAplicarSelDO.Size = new Size(200, 30);

        var lblAcaoTudo = new Label { Text = "Todas as diferentes/ausentes:", AutoSize = true, Location = new Point(0, 58), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var btnAplicarTudoOD = CriarBotaoAcao("Aplicar Tudo Origem → Destino");
        btnAplicarTudoOD.Location = new Point(200, 52);
        btnAplicarTudoOD.Size = new Size(200, 30);
        var btnAplicarTudoDO = CriarBotaoAcao("Aplicar Tudo Destino → Origem");
        btnAplicarTudoDO.Location = new Point(410, 52);
        btnAplicarTudoDO.Size = new Size(200, 30);

        var lblStatus = new Label
        {
            Location = new Point(0, 88),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = CorTextoSecundario
        };

        pnlAcoes.Controls.Add(lblAcaoSelecionado);
        pnlAcoes.Controls.Add(btnAplicarSelOD);
        pnlAcoes.Controls.Add(btnAplicarSelDO);
        pnlAcoes.Controls.Add(lblAcaoTudo);
        pnlAcoes.Controls.Add(btnAplicarTudoOD);
        pnlAcoes.Controls.Add(btnAplicarTudoDO);
        pnlAcoes.Controls.Add(lblStatus);

        void SetBotoesAplicar(bool habilitado)
        {
            btnAplicarSelOD.Enabled = habilitado;
            btnAplicarSelDO.Enabled = habilitado;
            btnAplicarTudoOD.Enabled = habilitado;
            btnAplicarTudoDO.Enabled = habilitado;
        }
        SetBotoesAplicar(false);

        List<ComparacaoColunaDto>? resultadoAtual = null;
        Conectar_SQL? conexaoDestinoAtual = null;
        string? bancoOrigemAtual = null;
        string? bancoDestinoAtual = null;

        void AplicarCoresLinhas()
        {
            foreach (DataGridViewRow linha in grid.Rows)
            {
                if (linha.DataBoundItem is not ComparacaoColunaDto comparacao)
                {
                    continue;
                }

                if (comparacao.Status == "Diferente")
                {
                    linha.DefaultCellStyle.BackColor = Color.FromArgb(255, 236, 204);
                    linha.DefaultCellStyle.ForeColor = Color.FromArgb(140, 80, 0);
                }
                else if (comparacao.Status.StartsWith("Ausente", StringComparison.Ordinal))
                {
                    linha.DefaultCellStyle.BackColor = Color.FromArgb(255, 224, 224);
                    linha.DefaultCellStyle.ForeColor = Color.FromArgb(140, 20, 20);
                }
            }
        }

        async Task ExecutarComparacaoAsync(string bancoOrigem, Conectar_SQL conexaoDestino, string bancoDestino)
        {
            btnComparar.Enabled = false;
            SetBotoesAplicar(false);
            Cursor = Cursors.WaitCursor;
            lblStatus.Text = "Comparando...";
            try
            {
                resultadoAtual = await _moduloComparador.CompararBancosAsync(_conectarSql, bancoOrigem, conexaoDestino, bancoDestino);
                bancoOrigemAtual = bancoOrigem;
                conexaoDestinoAtual = conexaoDestino;
                bancoDestinoAtual = bancoDestino;

                grid.DataSource = resultadoAtual;
                AplicarCoresLinhas();

                var diferentes = resultadoAtual.Count(r => r.Status == "Diferente");
                var ausentes = resultadoAtual.Count(r => r.Status.StartsWith("Ausente", StringComparison.Ordinal));
                var iguais = resultadoAtual.Count - diferentes - ausentes;
                lblStatus.Text = $"{resultadoAtual.Count} coluna(s) comparada(s): {iguais} igual(is), {diferentes} diferente(s), {ausentes} ausente(s) — {DateTime.Now:HH:mm:ss}.";
                AtualizarResumoComparacao(lblResumoTotal, lblResumoIguais, lblResumoDiferentes, lblResumoAusentes, resultadoAtual.Count, iguais, diferentes, ausentes, "colunas");
                SetBotoesAplicar(resultadoAtual.Count > 0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Falha ao comparar.";
                LimparResumoComparacaoAposFalha(lblResumoTotal, lblResumoIguais, lblResumoDiferentes, lblResumoAusentes);
                SetBotoesAplicar(false);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnComparar.Enabled = true;
            }
        }

        async Task CompararCliqueAsync()
        {
            if (cmbBancoOrigem.SelectedItem is not string bancoOrigem)
            {
                MessageBox.Show(this, "Selecione o banco de dados da sua instância.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Conectar_SQL conexaoDestino;
            string bancoDestino;

            if (radioMesmaInstancia.Checked)
            {
                if (cmbBancoDestinoMesma.SelectedItem is not string bancoDestinoSelecionado)
                {
                    MessageBox.Show(this, "Selecione o banco de dados de destino.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (string.Equals(bancoOrigem, bancoDestinoSelecionado, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "Escolha dois bancos diferentes para comparar.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                conexaoDestino = _conectarSql;
                bancoDestino = bancoDestinoSelecionado;
            }
            else
            {
                var servidor = txtServidorDestino.Text.Trim();
                var banco = txtBancoDestino.Text.Trim();
                var usuario = txtUsuarioDestino.Text.Trim();
                if (string.IsNullOrWhiteSpace(servidor) || string.IsNullOrWhiteSpace(banco) || string.IsNullOrWhiteSpace(usuario))
                {
                    MessageBox.Show(this, "Preencha servidor, banco de dados e usuário do outro local.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var conexaoExterna = new Conectar_SQL();
                try
                {
                    conexaoExterna.ConfigurarConexao(new ConnectionSettings
                    {
                        ServerName = servidor,
                        InitialDatabase = banco,
                        TipoAutenticacao = TipoAutenticacao.SqlServerAuthentication,
                        UsuarioLogin = usuario,
                        Senha = txtSenhaDestino.Text,
                        // Certificado deve ser validado por padrão: aceitar
                        // qualquer certificado do servidor de destino abre a
                        // conexão para interceptação (man-in-the-middle).
                        // Instâncias com certificado autoassinado vão precisar
                        // de uma opção explícita na tela (item separado).
                        ConfiarCertificadoServidor = false
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                conexaoDestino = conexaoExterna;
                bancoDestino = banco;
            }

            await ExecutarComparacaoAsync(bancoOrigem, conexaoDestino, bancoDestino);
        }

        async Task AplicarAsync(bool origemParaDestino, bool apenasSelecionado)
        {
            if (resultadoAtual is null || conexaoDestinoAtual is null || bancoOrigemAtual is null || bancoDestinoAtual is null)
            {
                return;
            }

            List<ComparacaoColunaDto> linhas;
            if (apenasSelecionado)
            {
                if (grid.CurrentRow?.DataBoundItem is not ComparacaoColunaDto linhaSelecionada)
                {
                    MessageBox.Show(this, "Selecione uma linha na grade.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (linhaSelecionada.Status == "Igual")
                {
                    MessageBox.Show(this, "Esta coluna já está igual nos dois bancos — nada para aplicar.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                linhas = new List<ComparacaoColunaDto> { linhaSelecionada };
            }
            else
            {
                linhas = resultadoAtual.Where(r => r.Status != "Igual").ToList();
                if (linhas.Count == 0)
                {
                    MessageBox.Show(this, "Não há colunas diferentes ou ausentes para aplicar.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var direcaoTexto = origemParaDestino ? "Origem → Destino" : "Destino → Origem";
            var confirmacao = MessageBox.Show(this,
                $"Confirma aplicar {linhas.Count} coluna(s) no sentido \"{direcaoTexto}\"?\n\n" +
                "Se a tabela inteira não existir no lado que recebe a aplicação, ela é criada por " +
                "completo (com todas as colunas de origem). Se já existir, cada coluna recebe um " +
                "ADD COLUMN (se estava ausente) ou ALTER COLUMN (se a definição divergia) — em " +
                "tabelas grandes/com dados isso pode falhar ou levar tempo. Uma coluna NOT NULL de " +
                "data/hora adicionada numa tabela que já tem linhas recebe automaticamente a data/hora " +
                "atual (GETDATE()) como valor de preenchimento; para os demais tipos NOT NULL sem " +
                "DEFAULT, o ADD falha nesse caso (ver aviso de limitações na tela de Sobre).",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            btnComparar.Enabled = false;
            SetBotoesAplicar(false);
            Cursor = Cursors.WaitCursor;
            try
            {
                await _moduloComparador.AplicarBancosAsync(_conectarSql, bancoOrigemAtual, conexaoDestinoAtual, bancoDestinoAtual, linhas, origemParaDestino);
                lblStatus.Text = $"{linhas.Count} coluna(s) aplicada(s) ({direcaoTexto}) em {DateTime.Now:HH:mm:ss}. Atualizando comparação...";
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                btnComparar.Enabled = true;
                SetBotoesAplicar(true);
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            await ExecutarComparacaoAsync(bancoOrigemAtual, conexaoDestinoAtual, bancoDestinoAtual);
        }

        async Task CarregarBancosAsync()
        {
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBancoOrigem.Items.Clear();
                cmbBancoOrigem.Items.AddRange(bancos.ToArray());
                cmbBancoDestinoMesma.Items.Clear();
                cmbBancoDestinoMesma.Items.AddRange(bancos.ToArray());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbBancoOrigem.Items.Count > 0)
            {
                cmbBancoOrigem.SelectedIndex = 0;
            }
            cmbBancoDestinoMesma.SelectedIndex = cmbBancoDestinoMesma.Items.Count > 1 ? 1 : (cmbBancoDestinoMesma.Items.Count > 0 ? 0 : -1);
        }

        btnComparar.Click += (_, _) => _ = CompararCliqueAsync();
        btnAplicarSelOD.Click += (_, _) => _ = AplicarAsync(origemParaDestino: true, apenasSelecionado: true);
        btnAplicarSelDO.Click += (_, _) => _ = AplicarAsync(origemParaDestino: false, apenasSelecionado: true);
        btnAplicarTudoOD.Click += (_, _) => _ = AplicarAsync(origemParaDestino: true, apenasSelecionado: false);
        btnAplicarTudoDO.Click += (_, _) => _ = AplicarAsync(origemParaDestino: false, apenasSelecionado: false);

        cartao.Controls.Add(grid);
        cartao.Controls.Add(pnlAcoes);
        cartao.Controls.Add(pnlTopo);

        DefinirConteudo(cartao);

        _ = CarregarBancosAsync();
    }

    /// <summary>
    /// Página "Índice > Fragmentação": painel de filtros e ações de
    /// manutenção do lado ESQUERDO — Banco de Dados / PK Indexes / Nome da
    /// Tabela / Nome do Índice / contadores "Stats Fragmentation" por faixa,
    /// layout baseado no print de referência fornecido — e, do lado
    /// DIREITO, a grade com todos os índices da tabela carregada e sua
    /// fragmentação (script exato do usuário via
    /// <see cref="Modulo_Indices.ObterFragmentacaoIndicesAsync"/>), populada
    /// ao clicar "Carregar Índices".
    ///
    /// "Banco de Dados" é um combo (igual ao de Backup/Top N consultas,
    /// populado com <see cref="Modulo_Admin.ObterBancosDadosAsync"/>) porque
    /// a conexão abre por padrão no banco definido no login — sem esse
    /// combo não haveria como consultar/alterar índices de uma tabela que
    /// mora em outro banco da mesma instância. Troca de banco é feita com
    /// <see cref="SqlConnection.ChangeDatabase"/> (equivalente a "USE
    /// [banco]") logo depois de abrir cada conexão.
    ///
    /// Diferença deliberada em relação ao script/print: como a consulta do
    /// usuário exige um nome de tabela (OBJECT_ID(@tabela)), o carregamento é
    /// por tabela — o usuário digita o nome e clica "Carregar Índices"; os
    /// filtros de PK e de nome do índice, depois, são aplicados em memória
    /// sobre o que já foi carregado (não disparam uma nova consulta).
    ///
    /// Ações de manutenção (usam <see cref="Modulo_Indices"/>, que por sua
    /// vez usa exatamente os comandos ALTER INDEX da stored procedure de
    /// referência fornecida pelo usuário):
    /// - "Rebuild Índice" / "Reorganize Índice": atuam só no índice
    ///   selecionado na grade.
    /// - "Rebuild Todos (> 30%)" / "Reorganize Todos (5% – 30%)": atuam em
    ///   lote sobre os índices atualmente exibidos na grade (respeitando os
    ///   filtros de PK/nome aplicados), usando os mesmos limiares da
    ///   procedure de referência.
    /// - "Atualizar Estatísticas": UPDATE STATISTICS da tabela inteira.
    /// Todas passam por <see cref="ExecutarManutencaoIndiceAsync"/>, que abre
    /// uma janela de progresso com um botão Cancelar (ver esse método para o
    /// aviso de risco ao cancelar no meio da operação).
    /// </summary>
    private void MostrarPaginaIndiceFragmentacao()
    {
        lblTituloPagina.Text = "Índice > Fragmentação";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        var lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Clique em \"Carregar Índices\". Deixe \"Nome da Tabela\" em branco para carregar todas as tabelas do banco."
        };

        var pnlFiltros = new Panel
        {
            Dock = DockStyle.Left,
            Width = 300,
            Padding = new Padding(0, 0, 16, 0),
            AutoScroll = true
        };

        var fonteRotulo = new Font("Segoe UI", 9F, FontStyle.Bold);
        var fonteSecao = new Font("Segoe UI", 11F, FontStyle.Bold);

        // Banco de dados: a conexão abre por padrão no banco definido no
        // login, que não é necessariamente onde a tabela informada mora — por
        // isso esse combo (igual ao de Backup/Top N consultas) para escolher
        // explicitamente o banco antes de carregar/agir sobre os índices.
        var lblBanco = new Label { Text = "Banco de Dados", AutoSize = true, Location = new Point(0, 0), Font = fonteRotulo, ForeColor = CorTitulo };
        var cmbBanco = new ComboBox
        {
            Location = new Point(0, 22),
            Size = new Size(280, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            Sorted = true,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var lblFiltrosTitulo = new Label { Text = "Filtros", AutoSize = true, Location = new Point(0, 58), Font = new Font("Segoe UI", 12F, FontStyle.Bold), ForeColor = CorTitulo };

        var lblPk = new Label { Text = "PK Indexes:", AutoSize = true, Location = new Point(0, 92), Font = fonteRotulo, ForeColor = CorTitulo };
        var radPkTodos = new RadioButton { Text = "Todos", AutoSize = true, Location = new Point(0, 114), Checked = true };
        var radPkSomente = new RadioButton { Text = "Somente PK", AutoSize = true, Location = new Point(0, 138) };
        var radPkNao = new RadioButton { Text = "Sem PK", AutoSize = true, Location = new Point(0, 162) };

        var lblTabela = new Label { Text = "Nome da Tabela (opcional)", AutoSize = true, Location = new Point(0, 196), Font = fonteRotulo, ForeColor = CorTitulo };
        // ComboBox (editável) em vez de TextBox: ao abrir a lista, é
        // populada com todas as tabelas do banco selecionado (evento
        // DropDown, consultado sob demanda em Modulo_Indices.ObterNomesTabelasAsync)
        // — mas continua aceitando digitação livre e continua opcional
        // (em branco = todas as tabelas), então nada do comportamento
        // anterior (Carregar/filtro em memória/Atualizar Estatísticas) muda.
        var cmbTabela = new ComboBox
        {
            Location = new Point(0, 218),
            Size = new Size(280, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var lblIndiceFiltro = new Label { Text = "Nome do Índice", AutoSize = true, Location = new Point(0, 254), Font = fonteRotulo, ForeColor = CorTitulo };
        // Mesma ideia: ao abrir a lista, é populada com os índices da
        // tabela selecionada em cmbTabela (Modulo_Indices.ObterNomesIndicesAsync).
        var cmbIndiceFiltro = new ComboBox
        {
            Location = new Point(0, 276),
            Size = new Size(280, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var btnCarregar = CriarBotaoAcao("Carregar Índices");
        btnCarregar.Location = new Point(0, 314);
        btnCarregar.Size = new Size(280, 34);

        var lblFragTitulo = new Label { Text = "Stats Fragmentation", AutoSize = true, Location = new Point(0, 364), Font = fonteSecao, ForeColor = CorTitulo };

        var (pnlVerde, lblContagemVerde) = CriarCaixaFragmentacao("0% - 20%", Color.FromArgb(46, 160, 67), new Point(0, 390));
        var (pnlAmarelo, lblContagemAmarelo) = CriarCaixaFragmentacao("21% - 40%", Color.FromArgb(191, 143, 0), new Point(96, 390));
        var (pnlVermelho, lblContagemVermelho) = CriarCaixaFragmentacao("41% - 100%", Color.FromArgb(196, 55, 55), new Point(192, 390));

        var lblAcoesIndividuais = new Label { Text = "Índice selecionado", AutoSize = true, Location = new Point(0, 472), Font = fonteSecao, ForeColor = CorTitulo };
        var btnRebuildIndividual = CriarBotaoAcao("Rebuild Índice");
        btnRebuildIndividual.Location = new Point(0, 498);
        btnRebuildIndividual.Size = new Size(280, 34);
        var btnReorganizeIndividual = CriarBotaoAcao("Reorganize Índice");
        btnReorganizeIndividual.Location = new Point(0, 538);
        btnReorganizeIndividual.Size = new Size(280, 34);

        var lblAcoesTodos = new Label { Text = "Todos os índices da tabela", AutoSize = true, Location = new Point(0, 596), Font = fonteSecao, ForeColor = CorTitulo };
        var btnRebuildTodos = CriarBotaoAcao("Rebuild Todos (> 30%)");
        btnRebuildTodos.Location = new Point(0, 622);
        btnRebuildTodos.Size = new Size(280, 34);
        var btnReorganizeTodos = CriarBotaoAcao("Reorganize Todos (5% – 30%)");
        btnReorganizeTodos.Location = new Point(0, 662);
        btnReorganizeTodos.Size = new Size(280, 34);
        var btnAtualizarEstatisticas = CriarBotaoAcao("Atualizar Estatísticas");
        btnAtualizarEstatisticas.Location = new Point(0, 702);
        btnAtualizarEstatisticas.Size = new Size(280, 34);
        btnAtualizarEstatisticas.BackColor = Color.FromArgb(0, 137, 123);

        pnlFiltros.Controls.AddRange(new Control[]
        {
            lblBanco, cmbBanco,
            lblFiltrosTitulo, lblPk, radPkTodos, radPkSomente, radPkNao,
            lblTabela, cmbTabela, lblIndiceFiltro, cmbIndiceFiltro, btnCarregar,
            lblFragTitulo, pnlVerde, pnlAmarelo, pnlVermelho,
            lblAcoesIndividuais, btnRebuildIndividual, btnReorganizeIndividual,
            lblAcoesTodos, btnRebuildTodos, btnReorganizeTodos, btnAtualizarEstatisticas
        });

        // Fill (grid) primeiro, depois Left (pnlFiltros) e por último Top
        // (lblStatus) — mesma regra de ordenação já documentada nas outras
        // páginas: quem é adicionado por último reivindica seu espaço
        // primeiro, então lblStatus vira uma faixa no topo com a largura
        // inteira do cartão, pnlFiltros ocupa a coluna esquerda do que sobrou
        // e o grid (com todos os índices e sua fragmentação, carregado ao
        // clicar "Carregar Índices") preenche o restante à direita.
        cartao.Controls.Add(grid);
        cartao.Controls.Add(pnlFiltros);
        cartao.Controls.Add(lblStatus);

        DefinirConteudo(cartao);

        var dadosCarregados = new List<IndiceFragmentacaoDto>();
        var listaVisivel = new List<IndiceFragmentacaoDto>();

        string? BancoAtual() => string.IsNullOrWhiteSpace(cmbBanco.Text) ? null : cmbBanco.Text.Trim();

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR para as
        // continuações abaixo continuarem olhando para o token DESTA página
        // mesmo depois que outra tela substituir o campo. Só entra em consultas
        // de LEITURA; as manutenções de índice (rebuild/reorganize/estatísticas)
        // continuam com o CTS próprio delas, em ExecutarManutencaoIndiceAsync.
        var tokenPagina = TokenPaginaAtiva;

        // Canal de erro desta tela: a faixa de status no topo (mesmo formato
        // usado por CarregarAsync mais abaixo). Antes, as três funções que
        // preenchem combos tinham "catch" vazio — uma falha qualquer (ex.:
        // login sem VIEW ANY DATABASE) deixava o combo vazio sem dizer por quê.
        void ReportarFalhaCombo(string mensagem)
        {
            if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
            {
                return;
            }

            lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
            lblStatus.Text = mensagem;
        }

        // Popula cmbTabela com todas as tabelas do banco atualmente
        // selecionado em cmbBanco. Chamada ao sair de cmbBanco (Leave) ou
        // escolher um item nele (SelectedIndexChanged), e uma vez ao abrir
        // a página — NÃO no evento DropDown de cmbTabela: alterar os Items
        // enquanto a lista já está aberta faz o combo "abrir e fechar"
        // sozinho (comportamento reportado pelo usuário). Assim, quando o
        // usuário realmente clica na seta, a lista já está pronta.
        async Task CarregarTabelasAsync()
        {
            try
            {
                var tabelas = await _moduloIndices.ObterNomesTabelasAsync(BancoAtual(), tokenPagina);
                if (tokenPagina.IsCancellationRequested || cmbTabela.IsDisposed)
                {
                    return;
                }

                cmbTabela.Items.Clear();
                cmbTabela.Items.AddRange(tabelas.ToArray());
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                // O combo continua editável (dá pra digitar o nome da tabela na
                // mão); aqui só explicamos por que a lista veio vazia.
                ReportarFalhaCombo("Não foi possível listar as tabelas (" + ObterMensagemAmigavel(ex) +
                    ") — digite o nome da tabela diretamente no campo \"Nome da Tabela\".");
            }
        }

        // Popula cmbIndiceFiltro com os índices da tabela atualmente em
        // cmbTabela. Mesma lógica de disparo de CarregarTabelasAsync (Leave/
        // SelectedIndexChanged de cmbTabela, nunca no DropDown de
        // cmbIndiceFiltro). Sem tabela selecionada, não tem como saber de
        // qual tabela listar os índices — a lista fica vazia nesse caso.
        async Task CarregarIndicesFiltroAsync()
        {
            var tabela = cmbTabela.Text.Trim();
            if (string.IsNullOrWhiteSpace(tabela))
            {
                cmbIndiceFiltro.Items.Clear();
                return;
            }

            try
            {
                var indices = await _moduloIndices.ObterNomesIndicesAsync(tabela, BancoAtual(), tokenPagina);
                if (tokenPagina.IsCancellationRequested || cmbIndiceFiltro.IsDisposed)
                {
                    return;
                }

                cmbIndiceFiltro.Items.Clear();
                cmbIndiceFiltro.Items.AddRange(indices.ToArray());
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                // O combo continua editável; aqui só explicamos por que a
                // lista de índices veio vazia.
                ReportarFalhaCombo("Não foi possível listar os índices da tabela \"" + tabela + "\" (" +
                    ObterMensagemAmigavel(ex) + ") — o filtro por índice fica indisponível, o resto da tela continua funcionando.");
            }
        }

        void AtualizarContadores(IReadOnlyList<IndiceFragmentacaoDto> lista)
        {
            lblContagemVerde.Text = lista.Count(i => i.PercentualFragmentacao <= 20).ToString();
            lblContagemAmarelo.Text = lista.Count(i => i.PercentualFragmentacao > 20 && i.PercentualFragmentacao <= 40).ToString();
            lblContagemVermelho.Text = lista.Count(i => i.PercentualFragmentacao > 40).ToString();
        }

        void AplicarFiltros()
        {
            IEnumerable<IndiceFragmentacaoDto> filtrado = dadosCarregados;

            if (radPkSomente.Checked)
            {
                filtrado = filtrado.Where(i => i.EhChavePrimaria);
            }
            else if (radPkNao.Checked)
            {
                filtrado = filtrado.Where(i => !i.EhChavePrimaria);
            }

            // "Nome da Tabela" funciona em duas camadas: decide o escopo da
            // consulta ao clicar "Carregar Índices" (uma tabela específica ou
            // todas, se vazio) e, além disso — igual ao "Nome do Índice" —
            // também filtra em memória o que já foi carregado, sem precisar
            // recarregar. Útil principalmente depois de um carregamento sem
            // tabela (todas), pra encontrar uma tabela específica na lista.
            var nomeTabelaFiltro = cmbTabela.Text.Trim();
            if (!string.IsNullOrEmpty(nomeTabelaFiltro))
            {
                filtrado = filtrado.Where(i => i.NomeTabela.Contains(nomeTabelaFiltro, StringComparison.OrdinalIgnoreCase));
            }

            var nomeFiltro = cmbIndiceFiltro.Text.Trim();
            if (!string.IsNullOrEmpty(nomeFiltro))
            {
                filtrado = filtrado.Where(i => i.NomeIndice.Contains(nomeFiltro, StringComparison.OrdinalIgnoreCase));
            }

            var listaFiltrada = filtrado.ToList();
            listaVisivel = listaFiltrada;
            grid.DataSource = listaFiltrada;
            AtualizarContadores(listaFiltrada);
        }

        async Task CarregarAsync()
        {
            // Vazio = carrega os índices de todas as tabelas do banco (ver
            // Modulo_Indices.ObterFragmentacaoIndicesAsync) — sem validação
            // de obrigatoriedade aqui, é opcional por design.
            var tabela = cmbTabela.Text.Trim();
            var tabelaInformada = !string.IsNullOrWhiteSpace(tabela);

            btnCarregar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            lblStatus.ForeColor = CorTextoSecundario;
            lblStatus.Text = tabelaInformada ? "Carregando..." : "Carregando todas as tabelas do banco (pode demorar mais)...";
            try
            {
                dadosCarregados = await _moduloIndices.ObterFragmentacaoIndicesAsync(
                    tabelaInformada ? tabela : null, BancoAtual(), tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                AplicarFiltros();
                lblStatus.ForeColor = CorTextoSecundario;
                lblStatus.Text = tabelaInformada
                    ? $"{dadosCarregados.Count} índice(s) encontrado(s) em \"{tabela}\"."
                    : $"{dadosCarregados.Count} índice(s) encontrado(s) em todas as tabelas do banco.";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                dadosCarregados = new List<IndiceFragmentacaoDto>();
                grid.DataSource = null;
                AtualizarContadores(dadosCarregados);
                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCarregar.Enabled = true;
            }
        }

        btnCarregar.Click += (_, _) => _ = CarregarAsync();
        radPkTodos.CheckedChanged += (_, _) => AplicarFiltros();
        radPkSomente.CheckedChanged += (_, _) => AplicarFiltros();
        radPkNao.CheckedChanged += (_, _) => AplicarFiltros();
        cmbTabela.TextChanged += (_, _) => AplicarFiltros();
        cmbIndiceFiltro.TextChanged += (_, _) => AplicarFiltros();

        // A lista de "Nome da Tabela" é atualizada quando o banco muda (ao
        // sair do campo ou ao escolher um item da lista) — e a de "Nome do
        // Índice", quando a tabela muda — em vez de no evento DropDown.
        // Popular os Items durante o DropDown (lista se abrindo) fazia o
        // combo "abrir e fechar" sozinho, porque o WinForms redesenha/fecha
        // o dropdown quando os Items mudam enquanto ele já está sendo
        // exibido. Assim, quando o usuário realmente clica na seta, a lista
        // já está pronta de antemão e não é mais mexida durante a abertura.
        cmbBanco.SelectedIndexChanged += (_, _) => _ = CarregarTabelasAsync();
        cmbBanco.Leave += (_, _) => _ = CarregarTabelasAsync();
        cmbTabela.SelectedIndexChanged += (_, _) => _ = CarregarIndicesFiltroAsync();
        cmbTabela.Leave += (_, _) => _ = CarregarIndicesFiltroAsync();

        IndiceFragmentacaoDto? ObterIndiceSelecionado()
        {
            if (grid.CurrentRow?.DataBoundItem is IndiceFragmentacaoDto dto)
            {
                return dto;
            }

            MessageBox.Show(this, "Selecione um índice na grade primeiro.", "SQL Rocket",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        // Ações individuais usam a tabela do PRÓPRIO índice selecionado
        // (indice.NomeTabela), não o texto do filtro — importante porque,
        // com um carregamento sem tabela específica, a grade pode ter
        // índices de várias tabelas ao mesmo tempo.
        btnRebuildIndividual.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnRebuildIndividual.Enabled = false;
            try
            {
                var indice = ObterIndiceSelecionado();
                if (indice is null)
                {
                    return;
                }

                var confirmacao = MessageBox.Show(this,
                    $"Confirma o REBUILD do índice \"{indice.NomeIndice}\" na tabela \"{indice.NomeTabela}\"?\n\n" +
                    "Atenção: esse procedimento pode travar o banco de dados (bloqueios/locks na tabela) " +
                    "durante a execução.",
                    "Confirmar Rebuild", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync($"Rebuild — {indice.NomeTabela}.{indice.NomeIndice}",
                    (progresso, ct) => _moduloIndices.ReconstruirIndiceAsync(indice.NomeTabela, indice.NomeIndice, BancoAtual(), progresso, ct));
                await CarregarAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnRebuildIndividual.Enabled = true;
            }
        };

        btnReorganizeIndividual.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnReorganizeIndividual.Enabled = false;
            try
            {
                var indice = ObterIndiceSelecionado();
                if (indice is null)
                {
                    return;
                }

                var confirmacao = MessageBox.Show(this,
                    $"Confirma o REORGANIZE do índice \"{indice.NomeIndice}\" na tabela \"{indice.NomeTabela}\"?\n\n" +
                    "Atenção: esse procedimento pode travar o banco de dados (bloqueios/locks na tabela) " +
                    "durante a execução.",
                    "Confirmar Reorganize", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync($"Reorganize — {indice.NomeTabela}.{indice.NomeIndice}",
                    (progresso, ct) => _moduloIndices.ReorganizarIndiceAsync(indice.NomeTabela, indice.NomeIndice, BancoAtual(), progresso, ct));
                await CarregarAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnReorganizeIndividual.Enabled = true;
            }
        };

        // Ações em lote também usam a tabela de CADA linha (Modulo_Indices já
        // faz isso internamente, lendo IndiceFragmentacaoDto.NomeTabela) —
        // aqui só precisamos garantir que existe algo carregado.
        btnRebuildTodos.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnRebuildTodos.Enabled = false;
            try
            {
                if (listaVisivel.Count == 0)
                {
                    MessageBox.Show(this, "Carregue os índices primeiro.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var confirmacao = MessageBox.Show(this,
                    $"Confirma o REBUILD de TODOS os índices com fragmentação acima de 30% ({listaVisivel.Count} " +
                    "índice(s) carregado(s) na lista)?\n\n" +
                    "Atenção: esse procedimento pode travar o banco de dados (bloqueios/locks nas tabelas) " +
                    "durante a execução e pode levar bastante tempo.",
                    "Confirmar Rebuild em Lote", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync("Rebuild de todos os índices > 30%",
                    (progresso, ct) => _moduloIndices.ReconstruirTodosAcimaDoLimiarAsync(listaVisivel, BancoAtual(), progresso, ct));
                await CarregarAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnRebuildTodos.Enabled = true;
            }
        };

        btnReorganizeTodos.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnReorganizeTodos.Enabled = false;
            try
            {
                if (listaVisivel.Count == 0)
                {
                    MessageBox.Show(this, "Carregue os índices primeiro.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var confirmacao = MessageBox.Show(this,
                    $"Confirma o REORGANIZE de TODOS os índices com fragmentação entre 5% e 30% ({listaVisivel.Count} " +
                    "índice(s) carregado(s) na lista)?\n\n" +
                    "Atenção: esse procedimento pode travar o banco de dados (bloqueios/locks nas tabelas) " +
                    "durante a execução e pode levar bastante tempo.",
                    "Confirmar Reorganize em Lote", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync("Reorganize de todos os índices 5% – 30%",
                    (progresso, ct) => _moduloIndices.ReorganizarTodosNoLimiarAsync(listaVisivel, BancoAtual(), progresso, ct));
                await CarregarAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnReorganizeTodos.Enabled = true;
            }
        };

        // "Atualizar Estatísticas" agora é opcional igual ao Carregar Índices:
        // com uma tabela informada, atualiza só ela; em branco, atualiza (com
        // WITH FULLSCAN) todas as tabelas do banco atual — ver
        // Modulo_Indices.AtualizarEstatisticasAsync.
        btnAtualizarEstatisticas.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnAtualizarEstatisticas.Enabled = false;
            try
            {
                var tabela = cmbTabela.Text.Trim();
                var titulo = string.IsNullOrWhiteSpace(tabela)
                    ? "Atualizar estatísticas — todas as tabelas do banco"
                    : $"Atualizar estatísticas — {tabela}";

                var confirmacao = MessageBox.Show(this,
                    (string.IsNullOrWhiteSpace(tabela)
                        ? "Confirma a atualização de estatísticas (WITH FULLSCAN) de TODAS as tabelas do banco?"
                        : $"Confirma a atualização de estatísticas (WITH FULLSCAN) da tabela \"{tabela}\"?") +
                    "\n\nAtenção: esse procedimento pode travar o banco de dados (bloqueios/locks nas tabelas) " +
                    "durante a execução e pode levar bastante tempo.",
                    "Confirmar Atualização de Estatísticas", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                // Aviso específico de UPDATE STATISTICS — nada de REBUILD/
                // REORGANIZE aqui, então não faz sentido usar o aviso padrão
                // (que fala em "índice reconstruído/reorganizado").
                var avisoEstatisticas = string.IsNullOrWhiteSpace(tabela)
                    ? "Atenção: cancelar não corrompe dados (UPDATE STATISTICS é uma operação segura), mas a " +
                      "tabela em atualização no momento do cancelamento pode ficar com estatísticas parcialmente " +
                      "atualizadas, e as tabelas seguintes da lista não serão atualizadas."
                    : "Atenção: cancelar não corrompe dados (UPDATE STATISTICS é uma operação segura), mas a " +
                      "tabela pode ficar com estatísticas parcialmente atualizadas até uma nova execução.";

                await ExecutarManutencaoIndiceAsync(titulo,
                    (progresso, ct) => _moduloIndices.AtualizarEstatisticasAsync(
                        string.IsNullOrWhiteSpace(tabela) ? null : tabela, BancoAtual(), progresso, ct),
                    avisoEstatisticas);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnAtualizarEstatisticas.Enabled = true;
            }
        };

        AtualizarContadores(dadosCarregados);
        // Combo "Banco de Dados": helper único compartilhado pelas seis telas
        // que têm esse combo (ver PreencherComboBancosAsync) — a falha agora
        // aparece na faixa de status em vez de sumir num catch vazio.
        _ = PreencherComboBancosAsync(cmbBanco, ReportarFalhaCombo, tokenPagina);
        _ = CarregarTabelasAsync(); // pré-carrega a lista de tabelas do banco padrão (cmbBanco ainda vazio) assim que a página abre
    }

    /// <summary>
    /// Caixa "Stats Fragmentation" (rótulo colorido da faixa + número dentro
    /// de uma moldura colorida) — usada 3x em <see cref="MostrarPaginaIndiceFragmentacao"/>,
    /// uma por faixa de fragmentação (verde/amarelo/vermelho). A "moldura
    /// colorida" é o truque clássico do WinForms para simular borda colorida:
    /// um Panel com BackColor da cor da faixa e Padding pequeno, com um Label
    /// branco por cima Dock=Fill — sobra uma faixa da cor de fundo ao redor,
    /// parecendo uma borda.
    /// </summary>
    private static (Panel caixa, Label lblContagem) CriarCaixaFragmentacao(string faixaTexto, Color cor, Point localizacao)
    {
        var lblFaixa = new Label
        {
            Text = faixaTexto,
            AutoSize = true,
            Location = new Point(0, 0),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = cor
        };

        var moldura = new Panel
        {
            Location = new Point(0, 18),
            Size = new Size(88, 40),
            BackColor = cor,
            Padding = new Padding(2)
        };
        var lblContagem = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            ForeColor = cor,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            Text = "0"
        };
        moldura.Controls.Add(lblContagem);

        var container = new Panel { Location = localizacao, Size = new Size(88, 58) };
        container.Controls.Add(lblFaixa);
        container.Controls.Add(moldura);

        return (container, lblContagem);
    }

    /// <summary>
    /// Executa uma operação de manutenção de índice (rebuild/reorganize/
    /// atualizar estatísticas, individual ou em lote) numa janela de
    /// progresso dedicada: mostra as mensagens em tempo real (mesmo estilo
    /// "console" branco usado em Backup/Restore) e oferece um botão
    /// "Cancelar operação".
    ///
    /// Aviso mostrado nessa janela (pedido explicitamente pelo usuário):
    /// cancelar no meio de um REBUILD/REORGANIZE pode deixar o índice em
    /// estado inconsistente ou incompleto. O texto aqui foi levemente
    /// ajustado em relação ao pedido original ("pode ter deletado todos os
    /// índices do banco de dados") para não afirmar algo tecnicamente
    /// impreciso — cancelar uma operação de índice não apaga índices de
    /// outras tabelas do banco — mas mantém a mesma ideia central: cancelar
    /// no meio do processo é arriscado para o(s) índice(s) em andamento e
    /// deve ser evitado.
    ///
    /// Esse texto padrão só faz sentido para REBUILD/REORGANIZE — por isso
    /// <paramref name="mensagemAviso"/> deixa o chamador informar um aviso
    /// diferente (ex.: Atualizar Estatísticas, que não mexe na estrutura do
    /// índice e tem um risco bem menor ao cancelar).
    ///
    /// Enquanto a operação roda, a janela não pode ser fechada pelo X
    /// (ControlBox = false) — só pelo botão, que vira "Cancelar operação" e,
    /// ao terminar (com sucesso, erro ou cancelamento), vira "Fechar".
    /// </summary>
    private async Task ExecutarManutencaoIndiceAsync(
        string titulo, Func<IProgress<string>, CancellationToken, Task> operacao, string? mensagemAviso = null)
    {
        using var cts = new CancellationTokenSource();
        var concluido = false;

        var janela = new Form
        {
            Text = titulo,
            Width = 640,
            Height = 420,
            MinimumSize = new Size(480, 320),
            StartPosition = FormStartPosition.CenterParent,
            ControlBox = false
        };

        var txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Font = new Font("Consolas", 9F),
            BackColor = Color.White,
            ForeColor = CorTitulo,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblAviso = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(0, 6, 0, 0),
            ForeColor = Color.FromArgb(196, 55, 55),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = mensagemAviso ??
                   "Atenção: evite cancelar com a operação em andamento — o índice sendo reconstruído/" +
                   "reorganizado no momento do cancelamento pode ficar em estado inconsistente ou incompleto."
        };

        var btnCancelar = CriarBotaoAcao("Cancelar operação");
        btnCancelar.BackColor = Color.FromArgb(196, 55, 55);
        btnCancelar.Width = 200;
        btnCancelar.Location = new Point(0, 8);
        var pnlBotao = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        pnlBotao.Controls.Add(btnCancelar);

        btnCancelar.Click += (_, _) =>
        {
            if (concluido)
            {
                janela.Close();
                return;
            }

            btnCancelar.Enabled = false;
            btnCancelar.Text = "Cancelando...";
            cts.Cancel();
        };

        // Fill (txtLog) primeiro, depois Bottom (pnlBotao) e por último Top
        // (lblAviso) — mesma regra de ordenação de sempre.
        janela.Controls.Add(txtLog);
        janela.Controls.Add(pnlBotao);
        janela.Controls.Add(lblAviso);

        IProgress<string> progresso = new Progress<string>(mensagem =>
        {
            txtLog.AppendText(mensagem + Environment.NewLine);
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        });

        janela.Show(this);

        // A janela de progresso continua MODELESS (Show, não ShowDialog) de
        // propósito: ShowDialog só retorna quando a janela fecha, e este
        // método precisa continuar executando logo abaixo para rodar a
        // operação e reportar o progresso DENTRO dessa mesma janela — trocar
        // por ShowDialog inverteria todo o fluxo de controle deste método.
        //
        // Para obter o efeito que interessa (a janela principal não poder ser
        // usada enquanto a manutenção roda — antes dava para navegar para
        // outra tela e disparar uma segunda operação destrutiva por baixo),
        // a própria Menu_Raiz é desabilitada durante a operação e reabilitada
        // no finally. A janela de progresso é um top-level próprio (só tem
        // esta como owner), então continua clicável — inclusive o "Cancelar
        // operação".
        Enabled = false;
        _operacoesDestrutivasEmAndamento++;

        try
        {
            await operacao(progresso, cts.Token);
            progresso.Report("Operação concluída.");
        }
        catch (OperationCanceledException)
        {
            progresso.Report("Operação cancelada pelo usuário.");
        }
        catch (Exception ex)
        {
            progresso.Report($"Erro: {ObterMensagemAmigavel(ex)}");
        }
        finally
        {
            _operacoesDestrutivasEmAndamento--;
            Enabled = true;
            concluido = true;
            janela.ControlBox = true;
            btnCancelar.Enabled = true;
            btnCancelar.Text = "Fechar";
            lblAviso.Visible = false;
        }
    }

    /// <summary>
    /// Página "Índice > Índices nunca Utilizados": consulta baseada no script
    /// fornecido pelo usuário (sys.dm_db_index_usage_stats sem nenhum uso —
    /// seek/scan/lookup de usuário nem de sistema — JOIN sys.dm_db_index_operational_stats
    /// para os contadores leaf/non-leaf de insert/delete/update), com um
    /// botão para excluir o índice selecionado na grade.
    ///
    /// Antes de excluir, pedido explícito do usuário como garantia de
    /// segurança: SEMPRE gera um script de recriação
    /// (Modulo_Indices.GerarScriptRecriacaoIndiceAsync) e pede pra salvar
    /// num arquivo local (SaveFileDialog) — "salvar um script na máquina
    /// local caso queira criar novamente o índice". Se a geração do script
    /// falhar (tipo de índice não suportado) OU o usuário cancelar o "Salvar
    /// como", a exclusão é cancelada também — nunca exclui sem o script
    /// salvo. A exclusão em si reaproveita <see cref="ExecutarManutencaoIndiceAsync"/>
    /// (mesma janela de progresso com Cancelar usada em Rebuild/Reorganize/
    /// Atualizar Estatísticas), com um aviso próprio para esta operação.
    /// </summary>
    private void MostrarPaginaIndicesNuncaUtilizados()
    {
        lblTituloPagina.Text = "Índice > Índices nunca Utilizados";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        var lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Carregando..."
        };

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 74 };

        var lblAvisoDmv = new Label
        {
            Text = "Sem uso registrado (seeks/scans/lookups) desde o último restart do SQL Server — " +
                   "confira o tempo de atividade da instância antes de excluir.",
            AutoSize = true,
            Location = new Point(0, 4),
            Font = new Font("Segoe UI", 8F, FontStyle.Italic),
            ForeColor = CorTextoSecundario
        };

        var lblBanco = new Label
        {
            Text = "Banco de dados:",
            AutoSize = true,
            Location = new Point(0, 32),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbBanco = new ComboBox
        {
            Location = new Point(112, 28),
            Size = new Size(220, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            Sorted = true,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var btnCarregar = CriarBotaoAcao("Carregar");
        btnCarregar.Location = new Point(344, 27);
        btnCarregar.Size = new Size(110, 30);

        var btnExcluir = CriarBotaoAcao("Excluir Índice Selecionado");
        btnExcluir.BackColor = Color.FromArgb(196, 55, 55);
        btnExcluir.Location = new Point(464, 27);
        btnExcluir.Size = new Size(220, 30);

        pnlTopo.Controls.Add(lblAvisoDmv);
        pnlTopo.Controls.Add(lblBanco);
        pnlTopo.Controls.Add(cmbBanco);
        pnlTopo.Controls.Add(btnCarregar);
        pnlTopo.Controls.Add(btnExcluir);

        // Fill (grid) primeiro, depois Top (lblStatus, depois pnlTopo por
        // último para ficar por cima de tudo) — mesma regra de sempre.
        cartao.Controls.Add(grid);
        cartao.Controls.Add(lblStatus);
        cartao.Controls.Add(pnlTopo);

        DefinirConteudo(cartao);

        string? BancoAtual() => string.IsNullOrWhiteSpace(cmbBanco.Text) ? null : cmbBanco.Text.Trim();

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Só
        // entra em consultas de LEITURA; a exclusão do índice continua com o
        // CTS próprio de ExecutarManutencaoIndiceAsync.
        var tokenPagina = TokenPaginaAtiva;

        // Canal de erro desta tela (mesmo formato de CarregarDadosAsync).
        void ReportarFalhaCombo(string mensagem)
        {
            if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
            {
                return;
            }

            lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
            lblStatus.Text = mensagem;
        }

        async Task CarregarDadosAsync()
        {
            btnCarregar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            lblStatus.ForeColor = CorTextoSecundario;
            lblStatus.Text = "Carregando...";
            try
            {
                var dados = await _moduloIndices.ObterIndicesNuncaUtilizadosAsync(BancoAtual(), tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = dados;
                lblStatus.ForeColor = CorTextoSecundario;
                lblStatus.Text = $"{dados.Count} índice(s) sem uso registrado.";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = null;
                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCarregar.Enabled = true;
            }
        }

        btnCarregar.Click += (_, _) => _ = CarregarDadosAsync();

        btnExcluir.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnExcluir.Enabled = false;
            try
            {
                if (grid.CurrentRow?.DataBoundItem is not IndiceNaoUtilizadoDto indice)
                {
                    MessageBox.Show(this, "Selecione um índice na grade primeiro.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var ehRestricao = indice.EhChavePrimaria || indice.EhRestricaoUnica;
                var tipoRegistro = indice.EhChavePrimaria ? "a chave primária" : ehRestricao ? "a restrição única" : "o índice";

                var confirmar = MessageBox.Show(this,
                    $"Tem certeza que deseja excluir {tipoRegistro} \"{indice.NomeIndice}\" da tabela \"{indice.NomeTabela}\"?" +
                    "\n\nEm seguida será pedido para salvar um script de recriação em um arquivo local — guarde-o para " +
                    "poder recriar o índice se precisar reverter.",
                    "Confirmar exclusão", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirmar != DialogResult.Yes)
                {
                    return;
                }

                string script;
                try
                {
                    script = await _moduloIndices.GerarScriptRecriacaoIndiceAsync(indice.NomeTabela, indice.NomeIndice, BancoAtual());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Não foi possível gerar o script de recriação — a exclusão foi cancelada por segurança." +
                        $"\n\n{ObterMensagemAmigavel(ex)}",
                        "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                using var dialogoSalvar = new SaveFileDialog
                {
                    Title = "Salvar script de recriação do índice",
                    Filter = "Script SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*",
                    FileName = $"Recriar_{indice.NomeTabela}_{indice.NomeIndice}_{DateTime.Now:yyyyMMdd_HHmmss}.sql"
                };

                if (dialogoSalvar.ShowDialog(this) != DialogResult.OK)
                {
                    MessageBox.Show(this, "Exclusão cancelada — o script de recriação não foi salvo.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    File.WriteAllText(dialogoSalvar.FileName, script);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        $"Não foi possível salvar o script — a exclusão foi cancelada por segurança.\n\n{ex.Message}",
                        "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var aviso =
                    $"Atenção: o script de recriação já foi salvo em \"{dialogoSalvar.FileName}\". A exclusão é uma " +
                    "operação DDL, geralmente rápida — mesmo assim, evite cancelar com ela em andamento.";

                await ExecutarManutencaoIndiceAsync($"Excluir — {indice.NomeTabela}.{indice.NomeIndice}",
                    (progresso, ct) => _moduloIndices.ExcluirIndiceAsync(indice.NomeTabela, indice.NomeIndice, BancoAtual(), progresso, ct),
                    aviso);

                await CarregarDadosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnExcluir.Enabled = true;
            }
        };

        // Combo "Banco de Dados": helper único compartilhado pelas seis telas
        // que têm esse combo (ver PreencherComboBancosAsync).
        _ = PreencherComboBancosAsync(cmbBanco, ReportarFalhaCombo, tokenPagina);
        _ = CarregarDadosAsync();
    }

    /// <summary>
    /// Página "Índice > Índices sugeridos": consulta baseada no script
    /// fornecido pelo usuário ("Listagem 2" — TOP 15
    /// sys.dm_db_missing_index_group_stats/_groups/_details, ORDER BY
    /// impacto estimado DESC), com um combo de banco de dados, um combo
    /// opcional de tabela (reativa o filtro que estava comentado no script
    /// original) e um botão para criar o índice selecionado.
    ///
    /// "Criar Índice Selecionado" mostra o script CREATE NONCLUSTERED INDEX
    /// completo (montado em Modulo_Indices.ObterIndicesSugeridosAsync a
    /// partir das colunas de igualdade/desigualdade/incluídas — mesma
    /// fórmula do SSMS) numa confirmação antes de executar, e reaproveita
    /// <see cref="ExecutarManutencaoIndiceAsync"/> (mesma janela de
    /// progresso com Cancelar das outras ações de índice).
    /// </summary>
    private void MostrarPaginaIndicesSugeridos()
    {
        lblTituloPagina.Text = "Índice > Índices sugeridos";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        var lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Carregando..."
        };

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 56 };

        var lblBanco = new Label
        {
            Text = "Banco de dados:",
            AutoSize = true,
            Location = new Point(0, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbBanco = new ComboBox
        {
            Location = new Point(112, 11),
            Size = new Size(200, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            Sorted = true,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var lblTabela = new Label
        {
            Text = "Tabela (opcional):",
            AutoSize = true,
            Location = new Point(324, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        // Em branco = mantém o comportamento original do script (todas as
        // tabelas do banco). Populado sob demanda ao sair de cmbBanco (não
        // no DropDown — mesma correção já aplicada em Fragmentação para o
        // combo não "abrir e fechar" sozinho).
        var cmbTabela = new ComboBox
        {
            Location = new Point(456, 11),
            Size = new Size(200, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var btnCarregar = CriarBotaoAcao("Carregar");
        btnCarregar.Location = new Point(668, 10);
        btnCarregar.Size = new Size(110, 30);

        var btnCriar = CriarBotaoAcao("Criar Índice Selecionado");
        btnCriar.Location = new Point(788, 10);
        btnCriar.Size = new Size(210, 30);

        pnlTopo.Controls.Add(lblBanco);
        pnlTopo.Controls.Add(cmbBanco);
        pnlTopo.Controls.Add(lblTabela);
        pnlTopo.Controls.Add(cmbTabela);
        pnlTopo.Controls.Add(btnCarregar);
        pnlTopo.Controls.Add(btnCriar);

        // Fill (grid) primeiro, depois Top (lblStatus, depois pnlTopo por
        // último para ficar por cima de tudo) — mesma regra de sempre.
        cartao.Controls.Add(grid);
        cartao.Controls.Add(lblStatus);
        cartao.Controls.Add(pnlTopo);

        DefinirConteudo(cartao);

        string? BancoAtual() => string.IsNullOrWhiteSpace(cmbBanco.Text) ? null : cmbBanco.Text.Trim();

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Só
        // entra em consultas de LEITURA; a criação do índice continua com o
        // CTS próprio de ExecutarManutencaoIndiceAsync.
        var tokenPagina = TokenPaginaAtiva;

        // Canal de erro desta tela (mesmo formato de CarregarDadosAsync).
        void ReportarFalhaCombo(string mensagem)
        {
            if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
            {
                return;
            }

            lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
            lblStatus.Text = mensagem;
        }

        async Task CarregarTabelasAsync()
        {
            try
            {
                var tabelas = await _moduloIndices.ObterNomesTabelasAsync(BancoAtual(), tokenPagina);
                if (tokenPagina.IsCancellationRequested || cmbTabela.IsDisposed)
                {
                    return;
                }

                cmbTabela.Items.Clear();
                cmbTabela.Items.AddRange(tabelas.ToArray());
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                // O combo continua editável (dá pra digitar o nome da tabela na
                // mão); aqui só explicamos por que a lista veio vazia.
                ReportarFalhaCombo("Não foi possível listar as tabelas (" + ObterMensagemAmigavel(ex) +
                    ") — digite o nome da tabela diretamente no campo \"Tabela\".");
            }
        }

        async Task CarregarDadosAsync()
        {
            btnCarregar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            lblStatus.ForeColor = CorTextoSecundario;
            lblStatus.Text = "Carregando...";
            try
            {
                var tabela = cmbTabela.Text.Trim();
                var dados = await _moduloIndices.ObterIndicesSugeridosAsync(
                    BancoAtual(), string.IsNullOrWhiteSpace(tabela) ? null : tabela, tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = dados;
                lblStatus.ForeColor = CorTextoSecundario;
                lblStatus.Text = string.IsNullOrWhiteSpace(tabela)
                    ? $"{dados.Count} sugestão(ões) de índice encontrada(s) no banco."
                    : $"{dados.Count} sugestão(ões) de índice encontrada(s) para \"{tabela}\".";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = null;
                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCarregar.Enabled = true;
            }
        }

        btnCarregar.Click += (_, _) => _ = CarregarDadosAsync();

        cmbBanco.SelectedIndexChanged += (_, _) => _ = CarregarTabelasAsync();
        cmbBanco.Leave += (_, _) => _ = CarregarTabelasAsync();

        btnCriar.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnCriar.Enabled = false;
            try
            {
                if (grid.CurrentRow?.DataBoundItem is not IndiceSugeridoDto sugestao)
                {
                    MessageBox.Show(this, "Selecione uma sugestão de índice na grade primeiro.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var confirmar = MessageBox.Show(this,
                    $"Criar o seguinte índice na tabela \"{sugestao.NomeTabela}\"?\n\n{sugestao.ScriptCriacaoSugerido}" +
                    "\n\nAtenção: a criação de um índice pode deixar o banco de dados lento enquanto estiver em " +
                    "andamento (bloqueios e uso intenso de CPU/disco), especialmente em tabelas grandes.",
                    "Confirmar criação de índice", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirmar != DialogResult.Yes)
                {
                    return;
                }

                // Diferente do Rebuild/Reorganize em lote (onde cancelar no meio pode
                // deixar o índice em andamento incompleto), CREATE INDEX é uma única
                // instrução — cancelar simplesmente desfaz a transação, sem deixar
                // índice pela metade, então aqui o botão Cancelar pode ser oferecido
                // sem ressalva.
                await ExecutarManutencaoIndiceAsync($"Criar índice — {sugestao.NomeTabela}",
                    (progresso, ct) => _moduloIndices.CriarIndiceSugeridoAsync(sugestao.ScriptCriacaoSugerido, BancoAtual(), progresso, ct),
                    "Atenção: a criação do índice pode deixar o banco de dados lento (bloqueios e uso intenso de " +
                    "CPU/disco) até terminar, especialmente em tabelas grandes. Se preferir, clique em \"Cancelar " +
                    "operação\" abaixo para cancelar a criação — é seguro, o SQL Server desfaz a criação sem deixar " +
                    "o índice pela metade.");

                await CarregarDadosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnCriar.Enabled = true;
            }
        };

        // Combo "Banco de Dados": helper único compartilhado pelas seis telas
        // que têm esse combo (ver PreencherComboBancosAsync).
        _ = PreencherComboBancosAsync(cmbBanco, ReportarFalhaCombo, tokenPagina);
        _ = CarregarTabelasAsync();
        _ = CarregarDadosAsync();
    }

    /// <summary>
    /// Página "Índice > Índices mais utilizados": consulta baseada no
    /// script fornecido pelo usuário (sys.dm_db_index_usage_stats JOIN
    /// sys.objects/sys.indexes + JOIN sys.dm_db_index_operational_stats,
    /// ver <see cref="Modulo_Indices.ObterIndicesMaisUtilizadosAsync"/>),
    /// com um combo de banco de dados e dois botões — Rebuild e Reorganize
    /// do índice selecionado na grade — que reaproveitam os mesmos métodos
    /// já usados na tela de Fragmentação (<see cref="Modulo_Indices.ReconstruirIndiceAsync"/>/
    /// <see cref="Modulo_Indices.ReorganizarIndiceAsync"/>).
    ///
    /// Antes de executar, pedido explícito do usuário: uma mensagem
    /// avisando que o procedimento pode demorar e deixar o banco de dados
    /// com lentidão — mostrada tanto na confirmação inicial quanto na
    /// janela de progresso (que reaproveita <see cref="ExecutarManutencaoIndiceAsync"/>,
    /// com o mesmo botão Cancelar das outras ações de índice).
    /// </summary>
    private void MostrarPaginaIndicesMaisUtilizados()
    {
        lblTituloPagina.Text = "Índice > Índices mais utilizados";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        var lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Carregando..."
        };

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 56 };

        var lblBanco = new Label
        {
            Text = "Banco de dados:",
            AutoSize = true,
            Location = new Point(0, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbBanco = new ComboBox
        {
            Location = new Point(112, 11),
            Size = new Size(220, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            Sorted = true,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var btnCarregar = CriarBotaoAcao("Carregar");
        btnCarregar.Location = new Point(344, 10);
        btnCarregar.Size = new Size(110, 30);

        var btnRebuild = CriarBotaoAcao("Rebuild Índice Selecionado");
        btnRebuild.Location = new Point(464, 10);
        btnRebuild.Size = new Size(220, 30);

        var btnReorganize = CriarBotaoAcao("Reorganize Índice Selecionado");
        btnReorganize.Location = new Point(694, 10);
        btnReorganize.Size = new Size(230, 30);

        pnlTopo.Controls.Add(lblBanco);
        pnlTopo.Controls.Add(cmbBanco);
        pnlTopo.Controls.Add(btnCarregar);
        pnlTopo.Controls.Add(btnRebuild);
        pnlTopo.Controls.Add(btnReorganize);

        // Fill (grid) primeiro, depois Top (lblStatus, depois pnlTopo por
        // último para ficar por cima de tudo) — mesma regra de sempre.
        cartao.Controls.Add(grid);
        cartao.Controls.Add(lblStatus);
        cartao.Controls.Add(pnlTopo);

        DefinirConteudo(cartao);

        string? BancoAtual() => string.IsNullOrWhiteSpace(cmbBanco.Text) ? null : cmbBanco.Text.Trim();

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Só
        // entra em consultas de LEITURA; rebuild/reorganize continuam com o
        // CTS próprio de ExecutarManutencaoIndiceAsync.
        var tokenPagina = TokenPaginaAtiva;

        // Canal de erro desta tela (mesmo formato de CarregarDadosAsync).
        void ReportarFalhaCombo(string mensagem)
        {
            if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
            {
                return;
            }

            lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
            lblStatus.Text = mensagem;
        }

        async Task CarregarDadosAsync()
        {
            btnCarregar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            lblStatus.ForeColor = CorTextoSecundario;
            lblStatus.Text = "Carregando...";
            try
            {
                var dados = await _moduloIndices.ObterIndicesMaisUtilizadosAsync(BancoAtual(), tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = dados;
                lblStatus.ForeColor = CorTextoSecundario;
                lblStatus.Text = $"{dados.Count} índice(s) encontrado(s).";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = null;
                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCarregar.Enabled = true;
            }
        }

        btnCarregar.Click += (_, _) => _ = CarregarDadosAsync();

        IndiceUtilizadoDto? ObterIndiceSelecionado()
        {
            if (grid.CurrentRow?.DataBoundItem is IndiceUtilizadoDto dto)
            {
                return dto;
            }

            MessageBox.Show(this, "Selecione um índice na grade primeiro.", "SQL Rocket",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        btnRebuild.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnRebuild.Enabled = false;
            try
            {
                var indice = ObterIndiceSelecionado();
                if (indice is null)
                {
                    return;
                }

                var confirmar = MessageBox.Show(this,
                    $"Tem certeza que deseja reconstruir (Rebuild) o índice \"{indice.NomeIndice}\" da tabela \"{indice.NomeTabela}\"?" +
                    "\n\nAtenção: esse procedimento pode demorar e deixar o banco de dados com lentidão enquanto " +
                    "estiver em andamento.",
                    "Confirmar Rebuild", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirmar != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync($"Rebuild — {indice.NomeTabela}.{indice.NomeIndice}",
                    (progresso, ct) => _moduloIndices.ReconstruirIndiceAsync(indice.NomeTabela, indice.NomeIndice, BancoAtual(), progresso, ct),
                    "Atenção: reconstruir um índice pode demorar e deixar o banco de dados com lentidão enquanto " +
                    "estiver em andamento, especialmente em tabelas grandes. Evite cancelar com a operação em " +
                    "andamento — o índice pode ficar em estado inconsistente ou incompleto.");

                await CarregarDadosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnRebuild.Enabled = true;
            }
        };

        btnReorganize.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnReorganize.Enabled = false;
            try
            {
                var indice = ObterIndiceSelecionado();
                if (indice is null)
                {
                    return;
                }

                var confirmar = MessageBox.Show(this,
                    $"Tem certeza que deseja reorganizar (Reorganize) o índice \"{indice.NomeIndice}\" da tabela \"{indice.NomeTabela}\"?" +
                    "\n\nAtenção: esse procedimento pode demorar e deixar o banco de dados com lentidão enquanto " +
                    "estiver em andamento.",
                    "Confirmar Reorganize", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirmar != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync($"Reorganize — {indice.NomeTabela}.{indice.NomeIndice}",
                    (progresso, ct) => _moduloIndices.ReorganizarIndiceAsync(indice.NomeTabela, indice.NomeIndice, BancoAtual(), progresso, ct),
                    "Atenção: reorganizar um índice pode demorar e deixar o banco de dados com lentidão enquanto " +
                    "estiver em andamento, especialmente em tabelas grandes. Evite cancelar com a operação em " +
                    "andamento — o índice pode ficar em estado inconsistente ou incompleto.");

                await CarregarDadosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnReorganize.Enabled = true;
            }
        };

        // Combo "Banco de Dados": helper único compartilhado pelas seis telas
        // que têm esse combo (ver PreencherComboBancosAsync).
        _ = PreencherComboBancosAsync(cmbBanco, ReportarFalhaCombo, tokenPagina);
        _ = CarregarDadosAsync();
    }

    /// <summary>
    /// "Monitoramento &gt; Active Monitor SQL Local" — sessões/processos
    /// ativos na instância, a partir do script exato fornecido pelo usuário
    /// (ver <see cref="Modulo_Desempenho.ObterActiveMonitorLocalAsync"/>),
    /// com atualização automática (pedido do usuário: opções de 1s/5s/10s/
    /// 30s/1m/5m, mais "Pausado") e filtros de Banco/Usuário. Fina wrapper
    /// sobre <see cref="MostrarPaginaActiveMonitor"/>.
    /// </summary>
    /// <remarks>
    /// Único chamador de <see cref="MostrarPaginaActiveMonitor"/> — a tela
    /// Azure deixou de reaproveitar esse corpo compartilhado (ver
    /// <see cref="MostrarPaginaActiveMonitorAzure"/>) porque o ambiente
    /// Azure SQL Database do usuário nega a permissão "VIEW SERVER
    /// PERFORMANCE STATE" usada pelas DMVs do script do Local
    /// (sys.dm_os_tasks, sys.dm_os_waiting_tasks, sys.sysprocesses,
    /// sys.dm_os_performance_counters), mesmo com o script idêntico ao do
    /// Local — só um subconjunto mais estreito de DMVs (sys.dm_exec_requests/
    /// sys.dm_exec_sessions diretamente, sys.dm_db_resource_stats) é permitido
    /// nesse login.
    /// </remarks>
    private void MostrarPaginaActiveMonitorLocal()
    {
        MostrarPaginaActiveMonitor(
            "Monitoramento > Active Monitor SQL Local",
            ct => _moduloDesempenho.ObterActiveMonitorLocalAsync(ct),
            ct => _moduloDesempenho.ObterMetricasOverviewAsync(ct));
    }

    /// <summary>
    /// "Monitoramento &gt; Active Monitor SQL Azure" — tela própria (NÃO
    /// reaproveita <see cref="MostrarPaginaActiveMonitor"/>), montada com os
    /// 3 scripts fornecidos diretamente pelo usuário depois que o script do
    /// Local (idêntico, inclusive) continuou batendo em "VIEW SERVER
    /// PERFORMANCE STATE permission was denied..." nesse ambiente Azure:
    /// processos via <see cref="Modulo_Desempenho.ObterProcessosAzureAsync"/>
    /// (sys.dm_exec_requests/sys.dm_exec_sessions), Overview via
    /// <see cref="Modulo_Desempenho.ObterMetricasRecursoAzureAsync"/>
    /// (sys.dm_db_resource_stats) e o novo painel "Conexões por Aplicação/
    /// Usuário" via <see cref="Modulo_Desempenho.ObterConexoesAgrupadasAsync"/>
    /// (sys.dm_exec_sessions agrupado). Cada consulta abre sua própria
    /// conexão e executa "SET NOCOUNT ON;" antes do script (pedido explícito
    /// do usuário) — ver <see cref="Modulo_Desempenho.DefinirNoCountOnAsync"/>.
    /// </summary>
    /// <remarks>
    /// Diferenças de layout em relação ao Local (consequência direta da
    /// forma dos novos scripts, não escolha estética):
    /// <list type="bullet">
    /// <item>Overview tem 6 gráficos (CPU/Memória/Data IO/Log Write/Workers/
    /// Sessões, todos em %, 0-100) em vez dos 4 do Local — o script de
    /// sys.dm_db_resource_stats já traz as 6 métricas prontas em percentual,
    /// sem precisar de cálculo de taxa/delta entre amostras.</item>
    /// <item>Sem filtro de Banco nem checkbox "Somente comandos de usuário":
    /// o script de processos do Azure não tem coluna de banco por linha
    /// (INNER JOIN direto em sys.dm_exec_requests, sem sys.databases) e o
    /// filtro "s.is_user_process = 1" já vem embutido no próprio script.
    /// Só restam os filtros de Usuário e Aplicação.</item>
    /// <item>Novo painel "Conexões por Aplicação/Usuário" no rodapé,
    /// abaixo de "Recent Expensive Queries" (pedido explícito do usuário:
    /// "vai colocar essa opção também abaixo de tudo").</item>
    /// </list>
    /// "Recent Expensive Queries" continua reaproveitando
    /// <see cref="Modulo_Desempenho.ObterTop25ConsultasCustosasAsync"/> (já é
    /// database-scoped, não depende da permissão negada), sempre com
    /// <c>nomeBanco: null</c> já que não existe mais combo de Banco nesta
    /// tela.
    /// </remarks>
    private void MostrarPaginaActiveMonitorAzure()
    {
        lblTituloPagina.Text = "Monitoramento > Active Monitor SQL Azure";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        var lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Carregando..."
        };

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 48 };

        var lblAtualizacao = new Label
        {
            Text = "Atualização automática:",
            AutoSize = true,
            Location = new Point(0, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbIntervalo = new ComboBox
        {
            Location = new Point(168, 11),
            Size = new Size(110, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbIntervalo.Items.AddRange(new object[] { "Pausado", "1 segundo", "5 segundos", "10 segundos", "30 segundos", "1 minuto", "5 minutos" });
        cmbIntervalo.SelectedItem = "1 segundo";

        var lblUsuario = new Label
        {
            Text = "Usuário:",
            AutoSize = true,
            Location = new Point(296, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbUsuarioFiltro = new ComboBox
        {
            Location = new Point(352, 11),
            Size = new Size(170, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var lblAplicacao = new Label
        {
            Text = "Aplicação:",
            AutoSize = true,
            Location = new Point(534, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbAplicacaoFiltro = new ComboBox
        {
            Location = new Point(600, 11),
            Size = new Size(170, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var btnAtualizarAgora = CriarBotaoAcao("Atualizar Agora");
        btnAtualizarAgora.Location = new Point(782, 10);
        btnAtualizarAgora.Size = new Size(140, 30);

        pnlTopo.Controls.Add(lblAtualizacao);
        pnlTopo.Controls.Add(cmbIntervalo);
        pnlTopo.Controls.Add(lblUsuario);
        pnlTopo.Controls.Add(cmbUsuarioFiltro);
        pnlTopo.Controls.Add(lblAplicacao);
        pnlTopo.Controls.Add(cmbAplicacaoFiltro);
        pnlTopo.Controls.Add(btnAtualizarAgora);

        // ------------------------------------------------------------
        // "Overview": 6 strip charts em tempo real (CPU/Memória/Data IO/
        // Log Write/Workers/Sessões, todos em %), a partir do script de
        // sys.dm_db_resource_stats fornecido pelo usuário (ver
        // ObterMetricasRecursoAzureAsync). Layout em 2 linhas x 3 colunas
        // (6 gráficos não cabem legíveis numa linha só, diferente das 4
        // colunas do Overview do Local).
        // ------------------------------------------------------------
        (Panel caixa, Label lblTitulo, GraficoFaixa grafico) CriarCaixaGrafico(string tituloInicial)
        {
            var caixaGrafico = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 4, 4, 4),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle
            };
            var tituloGrafico = new Label
            {
                Text = tituloInicial,
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.FromArgb(140, 210, 255),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Black
            };
            var grafico = new GraficoFaixa(60)
            {
                Dock = DockStyle.Fill,
                EixoMinimo = 0,
                EixoFixo = 100
            };
            caixaGrafico.Controls.Add(grafico);
            caixaGrafico.Controls.Add(tituloGrafico);
            return (caixaGrafico, tituloGrafico, grafico);
        }

        var pnlOverview = new Panel { Dock = DockStyle.Top, Height = 280, Padding = new Padding(0, 0, 0, 8) };
        var lblOverviewTitulo = new Label
        {
            Text = "Overview",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var tabelaOverview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2
        };
        tabelaOverview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        tabelaOverview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        tabelaOverview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        tabelaOverview.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        tabelaOverview.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var (caixaCpu, lblCpuTitulo, graficoCpu) = CriarCaixaGrafico("CPU (—)");
        var (caixaMemoria, lblMemoriaTitulo, graficoMemoria) = CriarCaixaGrafico("Memória (—)");
        var (caixaDataIo, lblDataIoTitulo, graficoDataIo) = CriarCaixaGrafico("Data IO (—)");
        var (caixaLogWrite, lblLogWriteTitulo, graficoLogWrite) = CriarCaixaGrafico("Log Write (—)");
        var (caixaWorkers, lblWorkersTitulo, graficoWorkers) = CriarCaixaGrafico("Workers (—)");
        var (caixaSessoes, lblSessoesTitulo, graficoSessoes) = CriarCaixaGrafico("Sessões (—)");

        tabelaOverview.Controls.Add(caixaCpu, 0, 0);
        tabelaOverview.Controls.Add(caixaMemoria, 1, 0);
        tabelaOverview.Controls.Add(caixaDataIo, 2, 0);
        tabelaOverview.Controls.Add(caixaLogWrite, 0, 1);
        tabelaOverview.Controls.Add(caixaWorkers, 1, 1);
        tabelaOverview.Controls.Add(caixaSessoes, 2, 1);

        pnlOverview.Controls.Add(tabelaOverview);
        pnlOverview.Controls.Add(lblOverviewTitulo);

        // ------------------------------------------------------------
        // "Recent Expensive Queries": mesma consulta de Desempenho > Top 25
        // consultas (database-scoped, não depende da permissão "VIEW SERVER
        // PERFORMANCE STATE") — sempre nomeBanco: null, já que esta tela não
        // tem mais combo de Banco.
        // ------------------------------------------------------------
        var pnlRecentQueries = new Panel { Dock = DockStyle.Bottom, Height = 215, Padding = new Padding(0, 8, 0, 0) };
        var lblRecentTitulo = new Label
        {
            Text = "Recent Expensive Queries",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var gridConsultas = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "Query", HeaderText = "Query", Width = 260 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "Execucoes", HeaderText = "Execuções", Width = 80 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "CpuMs", HeaderText = "CPU (ms)", Width = 80 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "LeiturasFisicas", HeaderText = "Leituras Físicas", Width = 100 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "EscritasLogicas", HeaderText = "Escritas Lógicas", Width = 100 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "LeiturasLogicas", HeaderText = "Leituras Lógicas", Width = 100 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "CpuMedioMs", HeaderText = "CPU Médio (ms)", Width = 110 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "PlanCount", HeaderText = "Plan Count", Width = 80 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "Banco", HeaderText = "Banco", Width = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        pnlRecentQueries.Controls.Add(gridConsultas);
        pnlRecentQueries.Controls.Add(lblRecentTitulo);

        // ------------------------------------------------------------
        // NEW: "Conexões por Aplicação/Usuário" — pedido explícito do
        // usuário ("e vai colocar essa opção também abaixo de tudo"),
        // a partir do 4º script fornecido (sys.dm_exec_sessions agrupado
        // por program_name/login_name). Fica abaixo de "Recent Expensive
        // Queries" (Controls.Add por último entre os Dock.Bottom).
        // ------------------------------------------------------------
        var pnlConexoes = new Panel { Dock = DockStyle.Bottom, Height = 200, Padding = new Padding(0, 8, 0, 0) };
        var lblConexoesTitulo = new Label
        {
            Text = "Conexões por Aplicação/Usuário",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var gridConexoes = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        pnlConexoes.Controls.Add(gridConexoes);
        pnlConexoes.Controls.Add(lblConexoesTitulo);

        // Fill (grid) primeiro, depois Dock.Bottom na ordem em que devem
        // ficar de cima pra baixo a partir da borda (pnlRecentQueries,
        // depois pnlConexoes por último = mais próximo da borda inferior =
        // "abaixo de tudo", como pedido), e por fim Dock.Top na ordem em
        // que devem ficar de cima pra baixo a partir do topo (lblStatus,
        // pnlTopo, pnlOverview por último = mais próximo da borda superior).
        cartao.Controls.Add(grid);
        cartao.Controls.Add(pnlRecentQueries);
        cartao.Controls.Add(pnlConexoes);
        cartao.Controls.Add(lblStatus);
        cartao.Controls.Add(pnlTopo);
        cartao.Controls.Add(pnlOverview);

        // Coluna da query em execução: mesmo tratamento visual/clique da
        // coluna "Comando exec" do Local (ver AbrirJanelaComandoAtivo).
        grid.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0
                && grid.Columns[e.ColumnIndex].Name == nameof(ProcessoAtivoAzureDto.QueryEmExecucao))
            {
                grid.Cursor = Cursors.Hand;
            }
        };
        grid.CellMouseLeave += (_, _) => grid.Cursor = Cursors.Default;
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != nameof(ProcessoAtivoAzureDto.QueryEmExecucao)) return;
            if (grid.Rows[e.RowIndex].DataBoundItem is ProcessoAtivoAzureDto processo)
            {
                AbrirJanelaComandoAtivo(processo.QueryEmExecucao);
            }
        };

        DefinirConteudo(cartao);

        var timerAtualizacao = new System.Windows.Forms.Timer();
        _timerPaginaAtiva = timerAtualizacao;

        var todosProcessos = new List<ProcessoAtivoAzureDto>();
        var carregando = false;

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Aqui
        // ele importa mais do que em qualquer outra tela: a atualização
        // automática pode ser de 1 em 1 segundo e dispara QUATRO consultas por
        // tick, então sair da página sem cancelar deixava até quatro consultas
        // ainda rodando no servidor. Tudo aqui é LEITURA.
        var tokenPagina = TokenPaginaAtiva;

        void AplicarFormatacaoColunas()
        {
            if (grid.Columns[nameof(ProcessoAtivoAzureDto.QueryEmExecucao)] is DataGridViewColumn colunaComando)
            {
                colunaComando.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                colunaComando.Width = 220;
                colunaComando.DefaultCellStyle.ForeColor = Color.FromArgb(31, 111, 191);
                colunaComando.DefaultCellStyle.Font = FonteColunaClicavel;
                colunaComando.ToolTipText = "Clique para abrir a query completa em uma nova janela.";
            }
        }

        void AplicarDestaqueBloqueios()
        {
            foreach (DataGridViewRow linha in grid.Rows)
            {
                if (linha.DataBoundItem is not ProcessoAtivoAzureDto processo) continue;

                if (processo.BloqueadoPor != 0)
                {
                    linha.DefaultCellStyle.BackColor = Color.FromArgb(255, 224, 224);
                    linha.DefaultCellStyle.ForeColor = Color.FromArgb(140, 20, 20);
                }
            }
        }

        static void AtualizarComboFiltro(ComboBox combo, IEnumerable<string> valores)
        {
            var novos = valores.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v).ToArray();
            var atuais = combo.Items.Cast<string>().Skip(1).ToArray(); // ignora o "Todos" fixo na posição 0

            if (combo.Items.Count > 0 && novos.SequenceEqual(atuais))
            {
                return;
            }

            var selecionadoAntes = combo.SelectedItem as string ?? "Todos";
            combo.Items.Clear();
            combo.Items.Add("Todos");
            combo.Items.AddRange(novos);
            combo.SelectedItem = combo.Items.Contains(selecionadoAntes) ? selecionadoAntes : "Todos";
        }

        void AtualizarCombosFiltro()
        {
            AtualizarComboFiltro(cmbUsuarioFiltro, todosProcessos.Select(p => p.Usuario));
            AtualizarComboFiltro(cmbAplicacaoFiltro, todosProcessos.Select(p => p.Aplicacao));
        }

        void AplicarFiltroEExibir()
        {
            IEnumerable<ProcessoAtivoAzureDto> filtrado = todosProcessos;

            if (cmbUsuarioFiltro.SelectedItem is string usuario && usuario != "Todos")
            {
                filtrado = filtrado.Where(p => p.Usuario == usuario);
            }
            if (cmbAplicacaoFiltro.SelectedItem is string aplicacao && aplicacao != "Todos")
            {
                filtrado = filtrado.Where(p => p.Aplicacao == aplicacao);
            }

            // Guarda rolagem + linha selecionada (por SPID, chave estável do
            // processo) e devolve depois do rebind — sem isso a grade voltava
            // ao topo e perdia a seleção a cada tick de 1 segundo. Ver
            // CapturarEstadoGrade/RestaurarEstadoGrade.
            static int? ObterChaveProcesso(object item) =>
                item is ProcessoAtivoAzureDto processo ? processo.Spid : (int?)null;

            var (chaveSelecionada, primeiraLinhaVisivel) = CapturarEstadoGrade(grid, ObterChaveProcesso);

            grid.DataSource = filtrado.ToList();
            AplicarFormatacaoColunas();
            AplicarDestaqueBloqueios();

            RestaurarEstadoGrade(grid, ObterChaveProcesso, chaveSelecionada, primeiraLinhaVisivel);
        }

        void AtualizarOverview(MetricasRecursoAzureDto overview)
        {
            graficoCpu.AdicionarAmostra(overview.CpuPercent);
            lblCpuTitulo.Text = overview.CpuPercent.HasValue ? $"CPU ({overview.CpuPercent.Value:0.0}%)" : "CPU (N/D)";

            graficoMemoria.AdicionarAmostra(overview.MemoriaPercent);
            lblMemoriaTitulo.Text = overview.MemoriaPercent.HasValue ? $"Memória ({overview.MemoriaPercent.Value:0.0}%)" : "Memória (N/D)";

            graficoDataIo.AdicionarAmostra(overview.DataIoPercent);
            lblDataIoTitulo.Text = overview.DataIoPercent.HasValue ? $"Data IO ({overview.DataIoPercent.Value:0.0}%)" : "Data IO (N/D)";

            graficoLogWrite.AdicionarAmostra(overview.LogWritePercent);
            lblLogWriteTitulo.Text = overview.LogWritePercent.HasValue ? $"Log Write ({overview.LogWritePercent.Value:0.0}%)" : "Log Write (N/D)";

            graficoWorkers.AdicionarAmostra(overview.WorkersPercent);
            lblWorkersTitulo.Text = overview.WorkersPercent.HasValue ? $"Workers ({overview.WorkersPercent.Value:0.0}%)" : "Workers (N/D)";

            graficoSessoes.AdicionarAmostra(overview.SessoesPercent);
            lblSessoesTitulo.Text = overview.SessoesPercent.HasValue ? $"Sessões ({overview.SessoesPercent.Value:0.0}%)" : "Sessões (N/D)";
        }

        void AtualizarConsultasRecentes(List<ConsultaCustosaDto> consultas)
        {
            gridConsultas.Rows.Clear();
            foreach (var consulta in consultas.Take(5))
            {
                var cpuMs = Math.Round(consulta.TempoCpuTotal / 1000.0, 1);
                var cpuMedioMs = consulta.ContagemExecucoes > 0
                    ? Math.Round(consulta.TempoCpuTotal / (double)consulta.ContagemExecucoes / 1000.0, 2)
                    : 0;

                var indiceLinha = gridConsultas.Rows.Add(
                    consulta.InstrucaoSql, consulta.ContagemExecucoes, cpuMs,
                    consulta.LeiturasFisicasTotal, consulta.EscritasLogicasTotal, consulta.LeiturasLogicasTotal,
                    cpuMedioMs, consulta.NumeroGeracoesPlano, consulta.Banco ?? string.Empty);

                gridConsultas.Rows[indiceLinha].Cells["Query"].ToolTipText = consulta.InstrucaoSql;
            }
        }

        void AtualizarConexoes(List<ConexaoAgrupadaDto> conexoes)
        {
            gridConexoes.DataSource = conexoes;
        }

        async Task AtualizarTudoAsync()
        {
            // Evita sobreposição: se uma atualização demorar mais do que o
            // intervalo escolhido, o próximo Tick do Timer é ignorado em vez
            // de empilhar consultas (mesmo padrão do Local).
            if (carregando) return;
            carregando = true;
            btnAtualizarAgora.Enabled = false;
            try
            {
                // As 4 consultas (processos, overview, top consultas,
                // conexões agrupadas) rodam em paralelo — conexões
                // independentes, cada uma já com SET NOCOUNT ON via
                // DefinirNoCountOnAsync.
                var tarefaProcessos = _moduloDesempenho.ObterProcessosAzureAsync(tokenPagina);
                var tarefaOverview = _moduloDesempenho.ObterMetricasRecursoAzureAsync(tokenPagina);
                var tarefaConsultas = _moduloDesempenho.ObterTop25ConsultasCustosasAsync(15, null, tokenPagina);
                var tarefaConexoes = _moduloDesempenho.ObterConexoesAgrupadasAsync(tokenPagina);

                await Task.WhenAll(tarefaProcessos, tarefaOverview, tarefaConsultas, tarefaConexoes);

                // Página trocada durante o tick: nada a atualizar na tela.
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                todosProcessos = tarefaProcessos.Result;
                AtualizarCombosFiltro();
                AplicarFiltroEExibir();

                AtualizarOverview(tarefaOverview.Result);
                AtualizarConsultasRecentes(tarefaConsultas.Result);
                AtualizarConexoes(tarefaConexoes.Result);

                lblStatus.ForeColor = CorTextoSecundario;
                lblStatus.Text = $"{todosProcessos.Count} sessão(ões) — última atualização às {DateTime.Now:HH:mm:ss}.";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio do tick — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                carregando = false;
                btnAtualizarAgora.Enabled = true;
            }
        }

        int ObterIntervaloMs(string? opcao) => opcao switch
        {
            "1 segundo" => 1000,
            "5 segundos" => 5000,
            "10 segundos" => 10000,
            "30 segundos" => 30000,
            "1 minuto" => 60000,
            "5 minutos" => 300000,
            _ => 0 // "Pausado" (ou qualquer valor inesperado) = Timer parado
        };

        void AplicarIntervalo()
        {
            timerAtualizacao.Stop();
            var intervaloMs = ObterIntervaloMs(cmbIntervalo.SelectedItem as string);
            if (intervaloMs > 0)
            {
                timerAtualizacao.Interval = intervaloMs;
                timerAtualizacao.Start();
            }
        }

        timerAtualizacao.Tick += (_, _) => _ = AtualizarTudoAsync();
        cmbIntervalo.SelectedIndexChanged += (_, _) => AplicarIntervalo();
        cmbUsuarioFiltro.SelectedIndexChanged += (_, _) => AplicarFiltroEExibir();
        cmbAplicacaoFiltro.SelectedIndexChanged += (_, _) => AplicarFiltroEExibir();
        btnAtualizarAgora.Click += (_, _) => _ = AtualizarTudoAsync();

        AplicarIntervalo();
        _ = AtualizarTudoAsync();
    }

    /// <summary>
    /// Corpo compartilhado do Active Monitor (hoje só o Local — ver
    /// <see cref="MostrarPaginaActiveMonitorLocal"/>): sessões/processos
    /// ativos, com atualização automática (pedido do usuário: opções de
    /// 1s/5s/10s/30s/1m/5m, mais "Pausado") e filtros de Banco/Usuário. A
    /// tela Azure tem seu próprio corpo (<see cref="MostrarPaginaActiveMonitorAzure"/>)
    /// desde que passou a usar 3 scripts próprios, incompatíveis com a forma
    /// esperada aqui (<see cref="ProcessoAtivoDto"/>/<see cref="MetricasOverviewDto"/>).
    /// </summary>
    /// <param name="titulo">Texto exibido em lblTituloPagina (ex.: "Monitoramento &gt; Active Monitor SQL Local").</param>
    /// <param name="obterProcessos">Consulta de sessões/processos ativos (Local ou Azure). Recebe o token da página (ver <see cref="_ctsPaginaAtiva"/>) — é LEITURA, cancelável ao navegar.</param>
    /// <param name="obterOverview">Consulta das 4 métricas do painel Overview (Local ou Azure). Recebe o token da página, pelo mesmo motivo.</param>
    /// <remarks>
    /// O filtro por Banco/Usuário é aplicado LOCALMENTE (em memória, sobre a
    /// última lista carregada) em vez de refazer a consulta no SQL Server —
    /// o script não tem parâmetro de banco/usuário (só um WHERE comentado
    /// pra um login fixo), e como essa tela pode atualizar sozinha a cada 1
    /// segundo, filtrar em memória evita ida e volta desnecessária ao banco
    /// só pra trocar o filtro, e evita perder a seleção do combo a cada
    /// atualização automática. Os combos de filtro são repopulados a cada
    /// atualização (a lista de bancos/usuários com sessão ativa muda com o
    /// tempo), preservando a seleção atual quando ela ainda existe na lista
    /// nova; senão volta para "Todos".
    ///
    /// Linhas bloqueadas (coluna "Blocked By" preenchida com um session_id
    /// diferente de "0"/vazio) ficam destacadas em vermelho claro; quem está
    /// bloqueando outras sessões sem estar bloqueado ("Head Blocker" = "1")
    /// fica destacado em laranja claro — mesma ideia visual do Activity
    /// Monitor nativo do SSMS, pra achar bloqueios de relance numa lista
    /// grande. Isso é só apresentação (cor de linha), não altera nenhum dado
    /// do script.
    ///
    /// O Timer de atualização automática é registrado em <c>_timerPaginaAtiva</c>
    /// (ver DefinirConteudo) para ser parado/descartado sozinho ao navegar
    /// para outra página do menu.
    ///
    /// Além da grade "Processes", a página também reproduz (a pedido do
    /// usuário, print de referência do Activity Monitor do SSMS) o painel
    /// "Overview" (4 gráficos em tempo real — % Processor Time, Waiting
    /// Tasks, Database I/O, Batch Requests/sec — via
    /// <paramref name="obterOverview"/> e o controle
    /// <see cref="FerramentasDBA.UI.Controls.GraficoFaixa"/>), o painel
    /// "Recent Expensive Queries" (reaproveitando a MESMA consulta de
    /// Desempenho &gt; Top 25 consultas — igual nas duas telas, já é
    /// database-scoped — só num layout compacto com as 5 primeiras linhas)
    /// e, no rodapé, "Conexões por Aplicação/Usuário"
    /// (<see cref="Modulo_Desempenho.ObterConexoesAgrupadasAsync"/> —
    /// mesmo painel/consulta do Active Monitor SQL Azure, pedido pelo
    /// usuário também aqui depois de ver funcionando lá). Os painéis
    /// "Resource Waits" e "Data File I/O" do print de referência não foram
    /// implementados — o usuário não forneceu script/fonte de dados pra
    /// eles.
    /// </remarks>
    private void MostrarPaginaActiveMonitor(
        string titulo,
        Func<CancellationToken, Task<List<ProcessoAtivoDto>>> obterProcessos,
        Func<CancellationToken, Task<MetricasOverviewDto>> obterOverview)
    {
        lblTituloPagina.Text = titulo;

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        var lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Carregando..."
        };

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 84 };

        var lblAtualizacao = new Label
        {
            Text = "Atualização automática:",
            AutoSize = true,
            Location = new Point(0, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbIntervalo = new ComboBox
        {
            Location = new Point(168, 11),
            Size = new Size(110, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbIntervalo.Items.AddRange(new object[] { "Pausado", "1 segundo", "5 segundos", "10 segundos", "30 segundos", "1 minuto", "5 minutos" });
        cmbIntervalo.SelectedItem = "1 segundo";

        var lblBanco = new Label
        {
            Text = "Banco:",
            AutoSize = true,
            Location = new Point(296, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbBancoFiltro = new ComboBox
        {
            Location = new Point(348, 11),
            Size = new Size(170, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var lblUsuario = new Label
        {
            Text = "Usuário:",
            AutoSize = true,
            Location = new Point(530, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbUsuarioFiltro = new ComboBox
        {
            Location = new Point(590, 11),
            Size = new Size(170, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var btnAtualizarAgora = CriarBotaoAcao("Atualizar Agora");
        btnAtualizarAgora.Location = new Point(772, 10);
        btnAtualizarAgora.Size = new Size(140, 30);

        // Pedido do usuário: esconder tarefas internas do SQL Server (TRACE
        // QUEUE TASK, CHECKPOINT, BRKR TASK, etc.) e deixar só o que é um
        // comando de dados/DDL de verdade (SELECT, INSERT, UPDATE, DELETE,
        // CREATE, ALTER, DROP entre outros — ver PrefixosComandoUsuario).
        // Fica marcado por padrão (o pedido foi "remover", não "ter a
        // opção de remover"), mas dá pra desmarcar se precisar ver tudo —
        // é um filtro só de apresentação (client-side, sem nova consulta).
        var chkSomenteComandosUsuario = new CheckBox
        {
            Text = "Somente comandos de usuário (SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP...) — oculta tarefas internas do SQL Server",
            AutoSize = true,
            Location = new Point(0, 52),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTitulo,
            Checked = true
        };

        pnlTopo.Controls.Add(lblAtualizacao);
        pnlTopo.Controls.Add(cmbIntervalo);
        pnlTopo.Controls.Add(lblBanco);
        pnlTopo.Controls.Add(cmbBancoFiltro);
        pnlTopo.Controls.Add(lblUsuario);
        pnlTopo.Controls.Add(cmbUsuarioFiltro);
        pnlTopo.Controls.Add(btnAtualizarAgora);
        pnlTopo.Controls.Add(chkSomenteComandosUsuario);

        // ------------------------------------------------------------
        // "Overview": 4 strip charts em tempo real (% Processor Time,
        // Waiting Tasks, Database I/O, Batch Requests/sec), mesmo estilo
        // visual do Activity Monitor nativo do SSMS. GraficoFaixa
        // (FerramentasDBA.UI.Controls) só desenha; os dados vêm de
        // obterOverview (ObterMetricasOverviewAsync, único chamador hoje —
        // ver remarks do método) a cada atualização.
        // ------------------------------------------------------------
        (Panel caixa, Label lblTitulo, GraficoFaixa grafico) CriarCaixaGrafico(string tituloInicial, double eixoMinimo, double? eixoFixo)
        {
            var caixaGrafico = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 4, 0),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle
            };
            var tituloGrafico = new Label
            {
                Text = tituloInicial,
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.FromArgb(140, 210, 255),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Black
            };
            var grafico = new GraficoFaixa(60)
            {
                Dock = DockStyle.Fill,
                EixoMinimo = eixoMinimo,
                EixoFixo = eixoFixo
            };
            caixaGrafico.Controls.Add(grafico);
            caixaGrafico.Controls.Add(tituloGrafico);
            return (caixaGrafico, tituloGrafico, grafico);
        }

        var pnlOverview = new Panel { Dock = DockStyle.Top, Height = 180, Padding = new Padding(0, 0, 0, 8) };
        var lblOverviewTitulo = new Label
        {
            Text = "Overview",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var tabelaOverview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1
        };
        tabelaOverview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tabelaOverview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tabelaOverview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tabelaOverview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tabelaOverview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var (caixaCpu, lblCpuTitulo, graficoCpu) = CriarCaixaGrafico("% Processor Time (—)", 100, 100);
        var (caixaEspera, lblEsperaTitulo, graficoEspera) = CriarCaixaGrafico("Waiting Tasks (—)", 10, null);
        var (caixaIo, lblIoTitulo, graficoIo) = CriarCaixaGrafico("Database I/O (—)", 10, null);
        var (caixaBatch, lblBatchTitulo, graficoBatch) = CriarCaixaGrafico("Batch Requests/sec (—)", 10, null);

        tabelaOverview.Controls.Add(caixaCpu, 0, 0);
        tabelaOverview.Controls.Add(caixaEspera, 1, 0);
        tabelaOverview.Controls.Add(caixaIo, 2, 0);
        tabelaOverview.Controls.Add(caixaBatch, 3, 0);

        pnlOverview.Controls.Add(tabelaOverview);
        pnlOverview.Controls.Add(lblOverviewTitulo);

        // ------------------------------------------------------------
        // "Recent Expensive Queries": reaproveita a mesma consulta de
        // Desempenho > Top 25 consultas (Modulo_Desempenho.ObterTop25ConsultasCustosasAsync
        // — sys.dm_exec_query_stats + sys.dm_exec_sql_text), mostrando só as
        // 5 primeiras num layout compacto (colunas manuais, sem plano de
        // execução aqui — para o plano completo, a tela dedicada de Top 25
        // consultas já resolve).
        // ------------------------------------------------------------
        var pnlRecentQueries = new Panel { Dock = DockStyle.Bottom, Height = 215, Padding = new Padding(0, 8, 0, 0) };
        var lblRecentTitulo = new Label
        {
            Text = "Recent Expensive Queries",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var gridConsultas = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "Query", HeaderText = "Query", Width = 260 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "Execucoes", HeaderText = "Execuções", Width = 80 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "CpuMs", HeaderText = "CPU (ms)", Width = 80 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "LeiturasFisicas", HeaderText = "Leituras Físicas", Width = 100 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "EscritasLogicas", HeaderText = "Escritas Lógicas", Width = 100 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "LeiturasLogicas", HeaderText = "Leituras Lógicas", Width = 100 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "CpuMedioMs", HeaderText = "CPU Médio (ms)", Width = 110 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "PlanCount", HeaderText = "Plan Count", Width = 80 });
        gridConsultas.Columns.Add(new DataGridViewTextBoxColumn { Name = "Banco", HeaderText = "Banco", Width = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        pnlRecentQueries.Controls.Add(gridConsultas);
        pnlRecentQueries.Controls.Add(lblRecentTitulo);

        // ------------------------------------------------------------
        // "Conexões por Aplicação/Usuário": mesmo painel criado primeiro
        // para o Active Monitor SQL Azure (script do usuário, só
        // sys.dm_exec_sessions agrupado por program_name/login_name — sem
        // nenhuma DMV restrita a permissão de Azure) e depois pedido pelo
        // usuário também aqui no Local. Fica abaixo de "Recent Expensive
        // Queries" (Controls.Add por último entre os Dock.Bottom).
        // ------------------------------------------------------------
        var pnlConexoes = new Panel { Dock = DockStyle.Bottom, Height = 200, Padding = new Padding(0, 8, 0, 0) };
        var lblConexoesTitulo = new Label
        {
            Text = "Conexões por Aplicação/Usuário",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var gridConexoes = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        pnlConexoes.Controls.Add(gridConexoes);
        pnlConexoes.Controls.Add(lblConexoesTitulo);

        // Fill (grid) primeiro, depois Dock.Bottom na ordem em que devem
        // ficar de cima pra baixo a partir da borda (pnlRecentQueries,
        // depois pnlConexoes por último = mais próximo da borda inferior),
        // e por fim Dock.Top na ordem em que devem ficar de cima pra baixo
        // a partir do topo (lblStatus, pnlTopo, e pnlOverview por último
        // para ficar no topo de tudo) — mesma regra de sempre (o último
        // Dock.Top/Bottom adicionado fica mais próximo da borda
        // correspondente).
        cartao.Controls.Add(grid);
        cartao.Controls.Add(pnlRecentQueries);
        cartao.Controls.Add(pnlConexoes);
        cartao.Controls.Add(lblStatus);
        cartao.Controls.Add(pnlTopo);
        cartao.Controls.Add(pnlOverview);

        // Coluna do comando: igual ao tratamento da coluna do plano de
        // execução em Desempenho > Top 25 consultas — largura fixa, cor/
        // sublinhado de link, clique abre o texto completo numa janela
        // separada, em vez de deixar o AutoSizeColumnsMode esticar a grade
        // pro tamanho do batch inteiro.
        grid.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0
                && grid.Columns[e.ColumnIndex].Name == nameof(ProcessoAtivoDto.ComandoExec))
            {
                grid.Cursor = Cursors.Hand;
            }
        };
        grid.CellMouseLeave += (_, _) => grid.Cursor = Cursors.Default;
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != nameof(ProcessoAtivoDto.ComandoExec)) return;
            if (grid.Rows[e.RowIndex].DataBoundItem is ProcessoAtivoDto processo)
            {
                AbrirJanelaComandoAtivo(processo.ComandoExec);
            }
        };

        DefinirConteudo(cartao);

        var timerAtualizacao = new System.Windows.Forms.Timer();
        _timerPaginaAtiva = timerAtualizacao;

        var todosProcessos = new List<ProcessoAtivoDto>();
        var carregando = false;

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Aqui
        // ele importa mais do que em qualquer outra tela: a atualização
        // automática pode ser de 1 em 1 segundo e dispara QUATRO consultas por
        // tick, então sair da página sem cancelar deixava até quatro consultas
        // ainda rodando no servidor. Tudo aqui é LEITURA.
        var tokenPagina = TokenPaginaAtiva;

        void AplicarFormatacaoColunas()
        {
            if (grid.Columns[nameof(ProcessoAtivoDto.ComandoExec)] is DataGridViewColumn colunaComando)
            {
                colunaComando.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                colunaComando.Width = 220;
                colunaComando.DefaultCellStyle.ForeColor = Color.FromArgb(31, 111, 191);
                colunaComando.DefaultCellStyle.Font = FonteColunaClicavel;
                colunaComando.ToolTipText = "Clique para abrir o comando completo em uma nova janela.";
            }
        }

        void AplicarDestaqueBloqueios()
        {
            foreach (DataGridViewRow linha in grid.Rows)
            {
                if (linha.DataBoundItem is not ProcessoAtivoDto processo) continue;

                var bloqueado = !string.IsNullOrWhiteSpace(processo.BlockedBy) && processo.BlockedBy != "0";
                var headBlocker = processo.HeadBlocker == "1";

                if (bloqueado)
                {
                    linha.DefaultCellStyle.BackColor = Color.FromArgb(255, 224, 224);
                    linha.DefaultCellStyle.ForeColor = Color.FromArgb(140, 20, 20);
                }
                else if (headBlocker)
                {
                    linha.DefaultCellStyle.BackColor = Color.FromArgb(255, 236, 204);
                    linha.DefaultCellStyle.ForeColor = Color.FromArgb(140, 80, 0);
                }
            }
        }

        // Só mexe nos Items do combo (Clear + AddRange) quando o conjunto de
        // valores realmente mudou desde a última atualização — com
        // atualização automática a cada 1 segundo, mexer no combo a cada
        // tick mesmo sem mudança nenhuma arriscaria fechar/perturbar o
        // dropdown bem na hora em que o usuário está escolhendo um filtro
        // (mesma categoria do bug de combo "abrindo e fechando" já corrigido
        // nas telas de Índice — aqui evitado preventivamente).
        static void AtualizarComboFiltro(ComboBox combo, IEnumerable<string> valores)
        {
            var novos = valores.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v).ToArray();
            var atuais = combo.Items.Cast<string>().Skip(1).ToArray(); // ignora o "Todos" fixo na posição 0

            // combo.Items.Count > 0 garante que a primeira população (combo
            // ainda vazio, sem nem o "Todos") sempre acontece, mesmo no caso
            // extremo de novos também vir vazio.
            if (combo.Items.Count > 0 && novos.SequenceEqual(atuais))
            {
                return;
            }

            var selecionadoAntes = combo.SelectedItem as string ?? "Todos";
            combo.Items.Clear();
            combo.Items.Add("Todos");
            combo.Items.AddRange(novos);
            combo.SelectedItem = combo.Items.Contains(selecionadoAntes) ? selecionadoAntes : "Todos";
        }

        void AtualizarCombosFiltro()
        {
            AtualizarComboFiltro(cmbBancoFiltro, todosProcessos.Select(p => p.Database));
            AtualizarComboFiltro(cmbUsuarioFiltro, todosProcessos.Select(p => p.Login));
        }

        void AplicarFiltroEExibir()
        {
            IEnumerable<ProcessoAtivoDto> filtrado = todosProcessos;

            if (cmbBancoFiltro.SelectedItem is string banco && banco != "Todos")
            {
                filtrado = filtrado.Where(p => p.Database == banco);
            }
            if (cmbUsuarioFiltro.SelectedItem is string usuario && usuario != "Todos")
            {
                filtrado = filtrado.Where(p => p.Login == usuario);
            }
            if (chkSomenteComandosUsuario.Checked)
            {
                filtrado = filtrado.Where(p => PrefixosComandoUsuario.Any(
                    prefixo => p.Command.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)));
            }

            // Guarda rolagem + linha selecionada (por SESSION ID, chave
            // estável da sessão) e devolve depois do rebind — sem isso a
            // grade voltava ao topo e perdia a seleção a cada tick de 1
            // segundo. Ver CapturarEstadoGrade/RestaurarEstadoGrade.
            static int? ObterChaveProcesso(object item) =>
                item is ProcessoAtivoDto processo ? processo.SessionId : (int?)null;

            var (chaveSelecionada, primeiraLinhaVisivel) = CapturarEstadoGrade(grid, ObterChaveProcesso);

            grid.DataSource = filtrado.ToList();
            AplicarFormatacaoColunas();
            AplicarDestaqueBloqueios();

            RestaurarEstadoGrade(grid, ObterChaveProcesso, chaveSelecionada, primeiraLinhaVisivel);
        }

        void AtualizarOverview(MetricasOverviewDto overview)
        {
            // As 4 métricas já chegam prontas pra exibir (taxa por segundo
            // já calculada server-side, dentro do próprio script — ver
            // remarks de Modulo_Desempenho.TextoMetricasOverviewLocal) —
            // nenhum cálculo de delta entre amostras é feito aqui. Todas
            // nullable como um grupo só: uma falha em qualquer instrução do
            // lote deixa as 4 nulas de uma vez (não é mais possível uma
            // métrica falhar isoladamente enquanto as outras aparecem — ver
            // remarks de MetricasOverviewDto).
            graficoCpu.AdicionarAmostra(overview.PercentualProcessadorSql);
            lblCpuTitulo.Text = overview.PercentualProcessadorSql.HasValue
                ? $"% Processor Time ({overview.PercentualProcessadorSql.Value:0}%)"
                : "% Processor Time (N/D)";

            graficoEspera.AdicionarAmostra(overview.TarefasEsperando);
            lblEsperaTitulo.Text = overview.TarefasEsperando.HasValue
                ? $"Waiting Tasks ({overview.TarefasEsperando.Value})"
                : "Waiting Tasks (N/D)";

            graficoIo.AdicionarAmostra(overview.DatabaseIoMBps);
            lblIoTitulo.Text = overview.DatabaseIoMBps.HasValue
                ? $"Database I/O ({overview.DatabaseIoMBps.Value:0.0} MB/sec)"
                : "Database I/O (N/D)";

            graficoBatch.AdicionarAmostra(overview.BatchRequestsPorSegundo);
            lblBatchTitulo.Text = overview.BatchRequestsPorSegundo.HasValue
                ? $"Batch Requests/sec ({overview.BatchRequestsPorSegundo.Value:0})"
                : "Batch Requests/sec (N/D)";
        }

        // "Recent Expensive Queries" segue o mesmo filtro de Banco da grade
        // de processos (pedido do usuário) — null quando o combo está em
        // "Todos", que é o que Modulo_Desempenho.ObterTop25ConsultasCustosasAsync
        // já espera pra trazer de todos os bancos (mesmo comportamento do
        // combo "Banco de dados" da tela Desempenho > Top 25 consultas).
        string? BancoFiltroSelecionado()
        {
            var banco = cmbBancoFiltro.SelectedItem as string;
            return string.IsNullOrWhiteSpace(banco) || banco == "Todos" ? null : banco;
        }

        void AtualizarConsultasRecentes(List<ConsultaCustosaDto> consultas)
        {
            var bancoFiltro = BancoFiltroSelecionado();
            lblRecentTitulo.Text = bancoFiltro is null
                ? "Recent Expensive Queries"
                : $"Recent Expensive Queries — {bancoFiltro}";

            gridConsultas.Rows.Clear();
            foreach (var consulta in consultas.Take(5))
            {
                // TempoCpuTotal vem em MICROSSEGUNDOS (ver remarks de
                // ConsultaCustosaDto) — convertido pra milissegundos aqui só
                // pra exibição, igual à coluna "CPU (ms)" do print de
                // referência.
                var cpuMs = Math.Round(consulta.TempoCpuTotal / 1000.0, 1);
                var cpuMedioMs = consulta.ContagemExecucoes > 0
                    ? Math.Round(consulta.TempoCpuTotal / (double)consulta.ContagemExecucoes / 1000.0, 2)
                    : 0;

                var indiceLinha = gridConsultas.Rows.Add(
                    consulta.InstrucaoSql, consulta.ContagemExecucoes, cpuMs,
                    consulta.LeiturasFisicasTotal, consulta.EscritasLogicasTotal, consulta.LeiturasLogicasTotal,
                    cpuMedioMs, consulta.NumeroGeracoesPlano, consulta.Banco ?? string.Empty);

                gridConsultas.Rows[indiceLinha].Cells["Query"].ToolTipText = consulta.InstrucaoSql;
            }
        }

        void AtualizarConexoes(List<ConexaoAgrupadaDto> conexoes)
        {
            gridConexoes.DataSource = conexoes;
        }

        // BUG DE PRODUÇÃO reportado pelo usuário nesta tela: os combos
        // "Banco"/"Usuário" apareciam vazios e a grade de processos não
        // carregava, mesmo com a instância respondendo normalmente. Causa:
        // as 4 consultas (sessões, overview, top consultas, conexões
        // agrupadas) rodavam dentro de um ÚNICO Task.WhenAll com um único
        // try/catch — se QUALQUER uma falhasse ou estourasse o timeout (o
        // caso mais comum era "Recent Expensive Queries", ver o comentário
        // na SQL de Modulo_Desempenho.ObterTop25ConsultasCustosasAsync sobre
        // o plan cache grande de produção), a exceção derrubava a
        // atualização INTEIRA: `todosProcessos`/os combos nunca chegavam a
        // ser preenchidos, mesmo quando a consulta de sessões (obterProcessos)
        // tinha terminado com sucesso — o `await Task.WhenAll` relança a
        // exceção antes do código chegar em `todosProcessos = tarefaProcessos.Result`.
        // Corrigido: cada uma das 4 consultas agora trata sua própria falha
        // (mesmo padrão que ObterMetricasOverviewAsync/ObterMetricasRecursoAzureAsync
        // já usavam internamente) e atualiza seu próprio painel de forma
        // independente — uma consulta lenta/com erro não impede mais as
        // outras 3 de atualizar a tela.
        async Task AtualizarTudoAsync()
        {
            // Evita sobreposição: se uma atualização demorar mais do que o
            // intervalo escolhido (ex.: 1 segundo com uma instância lenta),
            // o próximo Tick do Timer é ignorado em vez de empilhar consultas.
            if (carregando) return;
            carregando = true;
            btnAtualizarAgora.Enabled = false;

            string? erroProcessos = null;
            string? erroOverview = null;
            string? erroConsultas = null;
            string? erroConexoes = null;

            // Guarda comum dos 4 painéis: a página pode ter sido trocada
            // enquanto a consulta rodava, e aí não há mais grade/gráfico para
            // atualizar (ver _ctsPaginaAtiva).
            bool PaginaSaiu() => tokenPagina.IsCancellationRequested || lblStatus.IsDisposed;

            async Task CarregarProcessosAsync()
            {
                try
                {
                    var processos = await obterProcessos(tokenPagina);
                    if (PaginaSaiu())
                    {
                        return;
                    }

                    todosProcessos = processos;
                    AtualizarCombosFiltro();
                    AplicarFiltroEExibir();
                }
                catch (OperationCanceledException)
                {
                    // Usuário saiu da página no meio do tick — esperado, não é erro.
                }
                catch (Exception ex)
                {
                    erroProcessos = ObterMensagemAmigavel(ex);
                }
            }

            async Task CarregarOverviewAsync()
            {
                try
                {
                    var overview = await obterOverview(tokenPagina);
                    if (PaginaSaiu())
                    {
                        return;
                    }

                    AtualizarOverview(overview);
                }
                catch (OperationCanceledException)
                {
                    // Usuário saiu da página no meio do tick — esperado, não é erro.
                }
                catch (Exception ex)
                {
                    erroOverview = ObterMensagemAmigavel(ex);
                }
            }

            async Task CarregarConsultasAsync()
            {
                try
                {
                    var consultas = await _moduloDesempenho.ObterTop25ConsultasCustosasAsync(15, BancoFiltroSelecionado(), tokenPagina);
                    if (PaginaSaiu())
                    {
                        return;
                    }

                    AtualizarConsultasRecentes(consultas);
                }
                catch (OperationCanceledException)
                {
                    // Usuário saiu da página no meio do tick — esperado, não é erro.
                }
                catch (Exception ex)
                {
                    erroConsultas = ObterMensagemAmigavel(ex);
                }
            }

            async Task CarregarConexoesAsync()
            {
                try
                {
                    var conexoes = await _moduloDesempenho.ObterConexoesAgrupadasAsync(tokenPagina);
                    if (PaginaSaiu())
                    {
                        return;
                    }

                    AtualizarConexoes(conexoes);
                }
                catch (OperationCanceledException)
                {
                    // Usuário saiu da página no meio do tick — esperado, não é erro.
                }
                catch (Exception ex)
                {
                    erroConexoes = ObterMensagemAmigavel(ex);
                }
            }

            try
            {
                // As 4 consultas continuam rodando em paralelo (conexões
                // independentes) — a diferença é que cada uma agora captura
                // sua própria exceção, então este Task.WhenAll só falharia
                // por algo verdadeiramente inesperado (fora dos 4 métodos
                // acima); mantido como rede de segurança.
                await Task.WhenAll(CarregarProcessosAsync(), CarregarOverviewAsync(), CarregarConsultasAsync(), CarregarConexoesAsync());

                if (PaginaSaiu())
                {
                    return;
                }

                var erros = new[] { erroProcessos, erroOverview, erroConsultas, erroConexoes }
                    .Where(e => e is not null)
                    .ToList();

                if (erros.Count == 0)
                {
                    lblStatus.ForeColor = CorTextoSecundario;
                    lblStatus.Text = $"{todosProcessos.Count} sessão(ões) — última atualização às {DateTime.Now:HH:mm:ss}.";
                }
                else
                {
                    // Painéis que funcionaram continuam mostrando o dado mais
                    // recente que conseguiram buscar — só o texto de status
                    // avisa quais falharam, sem apagar o resto da tela.
                    lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                    lblStatus.Text = $"{todosProcessos.Count} sessão(ões) — última atualização às {DateTime.Now:HH:mm:ss}. " +
                        $"{erros.Count} painel(éis) não atualizou/atualizaram: {string.Join(" | ", erros)}";
                }
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio do tick — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (PaginaSaiu())
                {
                    return;
                }

                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                carregando = false;
                btnAtualizarAgora.Enabled = true;
            }
        }

        // Quando o usuário troca o combo "Banco" na hora (fora do ciclo do
        // Timer), só o painel "Recent Expensive Queries" precisa de uma
        // consulta nova ao SQL Server — a grade de processos já está toda
        // carregada em memória e só precisa ser refiltrada (AplicarFiltroEExibir,
        // chamado separadamente no evento). Roda fora do guard "carregando"
        // de AtualizarTudoAsync porque é uma consulta pequena e independente
        // (não mexe na grade principal nem no Overview).
        async Task AtualizarApenasConsultasRecentesAsync()
        {
            try
            {
                var consultas = await _moduloDesempenho.ObterTop25ConsultasCustosasAsync(15, BancoFiltroSelecionado(), tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                AtualizarConsultasRecentes(consultas);
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
        }

        int ObterIntervaloMs(string? opcao) => opcao switch
        {
            "1 segundo" => 1000,
            "5 segundos" => 5000,
            "10 segundos" => 10000,
            "30 segundos" => 30000,
            "1 minuto" => 60000,
            "5 minutos" => 300000,
            _ => 0 // "Pausado" (ou qualquer valor inesperado) = Timer parado
        };

        void AplicarIntervalo()
        {
            timerAtualizacao.Stop();
            var intervaloMs = ObterIntervaloMs(cmbIntervalo.SelectedItem as string);
            if (intervaloMs > 0)
            {
                timerAtualizacao.Interval = intervaloMs;
                timerAtualizacao.Start();
            }
        }

        timerAtualizacao.Tick += (_, _) => _ = AtualizarTudoAsync();
        cmbIntervalo.SelectedIndexChanged += (_, _) => AplicarIntervalo();
        cmbBancoFiltro.SelectedIndexChanged += (_, _) =>
        {
            AplicarFiltroEExibir();
            _ = AtualizarApenasConsultasRecentesAsync();
        };
        cmbUsuarioFiltro.SelectedIndexChanged += (_, _) => AplicarFiltroEExibir();
        chkSomenteComandosUsuario.CheckedChanged += (_, _) => AplicarFiltroEExibir();
        btnAtualizarAgora.Click += (_, _) => _ = AtualizarTudoAsync();

        AplicarIntervalo();
        _ = AtualizarTudoAsync();
    }

    /// <summary>
    /// Abre em uma janela separada o texto completo do comando em execução
    /// de uma sessão (coluna "Comando exec" do Active Monitor SQL Local) —
    /// mesmo padrão simplificado de <see cref="AbrirJanelaPlanoExecucao"/>,
    /// sem o botão de plano gráfico (não se aplica aqui, é só texto).
    /// </summary>
    private void AbrirJanelaComandoAtivo(string? comando)
    {
        if (string.IsNullOrWhiteSpace(comando))
        {
            MessageBox.Show(this, "Esta sessão não possui um comando em execução no momento.", "Comando em Execução",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var janela = new Form
        {
            Text = "Comando em Execução",
            Width = 900,
            Height = 600,
            MinimumSize = new Size(500, 300),
            StartPosition = FormStartPosition.CenterParent
        };

        var txtComando = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.White,
            Text = comando
        };

        janela.Controls.Add(txtComando);
        janela.Show(this);
    }

    /// <summary>
    /// "Segurança &gt; Logs" — log de erros do SQL Server, no mesmo formato
    /// do "Log File Viewer" do SSMS (colunas Date/Source/Message/Log
    /// Type/Log Source — aqui Data/Origem/Mensagem/Tipo de Log/Fonte do
    /// Log, traduzidas para consistência com o resto da tela). Pedido do
    /// usuário, com o print do Log File Viewer do SSMS como referência:
    /// "já pode implementar todos os scripts que precisa" — T-SQL próprio
    /// desta vez (não fornecido pelo usuário), via os procedimentos de
    /// sistema xp_readerrorlog/xp_enumerrorlogs (ver
    /// <see cref="Modulo_Admin.ObterLogsErroAsync"/>/
    /// <see cref="Modulo_Admin.ObterArquivosLogErroAsync"/>).
    /// </summary>
    /// <remarks>
    /// Só implementa o log de erros do SQL Server (Tipo de Log fixo em
    /// "SQL Server") — o Log File Viewer real do SSMS também lista o log
    /// do SQL Server Agent e logs do Windows, não implementados aqui. O
    /// combo "Arquivo" seleciona QUAL arquivo ler (log atual ou um
    /// arquivado, mais novo primeiro) — o SSMS permite marcar vários de
    /// uma vez (checkboxes) e mesclar o resultado; aqui é um arquivo por
    /// vez, por simplicidade. A barra cinza acima da grade equivale ao
    /// "Log file summary: No filter applied" do SSMS.
    /// </remarks>
    private void MostrarPaginaLogsErro()
    {
        lblTituloPagina.Text = "Segurança > Logs";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "DataHora",
            HeaderText = "Data",
            DataPropertyName = "DataHora",
            Width = 150,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy HH:mm:ss" }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ProcessInfo", HeaderText = "Origem", DataPropertyName = "ProcessInfo", Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Texto", HeaderText = "Mensagem", DataPropertyName = "Texto",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TipoLog", HeaderText = "Tipo de Log", DataPropertyName = "TipoLog", Width = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FonteLog", HeaderText = "Fonte do Log", DataPropertyName = "FonteLog", Width = 220 });

        // ---- Barra de topo: arquivo de log + filtro por texto ----
        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 84 };

        var lblArquivo = new Label
        {
            Text = "Arquivo",
            AutoSize = true,
            Location = new Point(0, 8),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTextoSecundario
        };
        var cmbArquivo = new ComboBox
        {
            Location = new Point(64, 4),
            Size = new Size(260, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        var btnAtualizarLog = CriarBotaoAcao("Atualizar");
        btnAtualizarLog.Location = new Point(334, 3);
        btnAtualizarLog.Size = new Size(100, 28);

        var lblFiltro = new Label
        {
            Text = "Contém o texto",
            AutoSize = true,
            Location = new Point(0, 46),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTextoSecundario
        };
        var txtFiltro = new TextBox
        {
            Location = new Point(100, 42),
            Size = new Size(260, 26),
            PlaceholderText = "Ex: login failed"
        };
        var btnFiltrar = CriarBotaoAcao("Filtrar");
        btnFiltrar.Location = new Point(370, 41);
        btnFiltrar.Size = new Size(90, 28);
        var btnLimparFiltro = CriarBotaoAcao("Limpar filtro");
        btnLimparFiltro.Location = new Point(468, 41);
        btnLimparFiltro.Size = new Size(112, 28);

        pnlTopo.Controls.Add(lblArquivo);
        pnlTopo.Controls.Add(cmbArquivo);
        pnlTopo.Controls.Add(btnAtualizarLog);
        pnlTopo.Controls.Add(lblFiltro);
        pnlTopo.Controls.Add(txtFiltro);
        pnlTopo.Controls.Add(btnFiltrar);
        pnlTopo.Controls.Add(btnLimparFiltro);

        // ---- Barra de resumo — equivalente ao "Log file summary" do SSMS ----
        var pnlResumo = new Panel
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = Color.FromArgb(237, 241, 245),
            BorderStyle = BorderStyle.FixedSingle
        };
        var lblResumo = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = CorTextoSecundario,
            Text = "Resumo do arquivo de log: nenhum filtro aplicado."
        };
        pnlResumo.Controls.Add(lblResumo);

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Tela
        // 100% de leitura (xp_readerrorlog/xp_enumerrorlogs), e ler o log de
        // erro inteiro de uma instância antiga pode demorar bastante: não faz
        // sentido continuar ocupando o servidor depois que o usuário saiu.
        var tokenPagina = TokenPaginaAtiva;

        async Task CarregarAsync()
        {
            var arquivoSelecionado = cmbArquivo.SelectedItem as ArquivoLogErroDto;
            var numeroArquivo = arquivoSelecionado?.Numero ?? 0;
            var filtro = string.IsNullOrWhiteSpace(txtFiltro.Text) ? null : txtFiltro.Text.Trim();

            btnAtualizarLog.Enabled = false;
            btnFiltrar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                var linhas = await _moduloAdmin.ObterLogsErroAsync(numeroArquivo, filtro, null, null, tokenPagina);
                if (tokenPagina.IsCancellationRequested || grid.IsDisposed)
                {
                    return;
                }

                grid.DataSource = linhas;
                lblResumo.Text = filtro is null
                    ? $"Resumo do arquivo de log: nenhum filtro aplicado. {linhas.Count} linha(s)."
                    : $"Resumo do arquivo de log: contém \"{filtro}\". {linhas.Count} linha(s).";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || grid.IsDisposed)
                {
                    return;
                }

                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnAtualizarLog.Enabled = true;
                btnFiltrar.Enabled = true;
            }
        }

        // Carrega a lista de arquivos (xp_enumerrorlogs) e seleciona o
        // primeiro ("Current", o log atual) — isso já dispara CarregarAsync
        // via SelectedIndexChanged. Se a lista vier vazia (ex.: sem
        // permissão em xp_enumerrorlogs), carrega o log atual mesmo assim
        // (ObterLogsErroAsync já usa numeroArquivo = 0 por padrão).
        async Task CarregarArquivosAsync()
        {
            try
            {
                var arquivos = await _moduloAdmin.ObterArquivosLogErroAsync(tokenPagina);
                if (tokenPagina.IsCancellationRequested || cmbArquivo.IsDisposed)
                {
                    return;
                }

                cmbArquivo.Items.Clear();
                cmbArquivo.Items.AddRange(arquivos.Cast<object>().ToArray());
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
                return;
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || cmbArquivo.IsDisposed)
                {
                    return;
                }

                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (cmbArquivo.Items.Count > 0)
            {
                cmbArquivo.SelectedIndex = 0;
            }
            else
            {
                await CarregarAsync();
            }
        }

        btnAtualizarLog.Click += (_, _) => _ = CarregarAsync();
        btnFiltrar.Click += (_, _) => _ = CarregarAsync();
        btnLimparFiltro.Click += (_, _) =>
        {
            txtFiltro.Text = string.Empty;
            _ = CarregarAsync();
        };
        cmbArquivo.SelectedIndexChanged += (_, _) => _ = CarregarAsync();
        txtFiltro.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = CarregarAsync();
            }
        };

        // Fill (grid) precisa ser adicionado antes dos Top (pnlResumo/pnlTopo),
        // senão o grid ocupa o cartão inteiro por baixo e as barras de topo
        // cobrem parte do conteúdo.
        cartao.Controls.Add(grid);
        cartao.Controls.Add(pnlResumo);
        cartao.Controls.Add(pnlTopo);

        DefinirConteudo(cartao);

        _ = CarregarArquivosAsync();
    }

    /// <summary>
    /// "Segurança &gt; Usuários" — criação de login/usuário e gestão de
    /// permissões (servidor e banco de dados, incluindo tabelas/views
    /// separadamente). Pedido do usuário, sem script pronto fornecido —
    /// T-SQL próprio (mesma autorização geral já dada para "Segurança &gt;
    /// Logs"): "criar o novo usuário no SQL, inserir o usuário no banco
    /// de dados, alterar as permissões do usuário do SQL Server, alterar
    /// as permissões do usuário do banco de dados, dentro do banco de
    /// dados permissão em tabelas e views separados".
    /// </summary>
    /// <remarks>
    /// Uma única tela com 4 abas (decisão tomada com o usuário antes de
    /// implementar): "Criar Login" (SQL Server OU Windows Authentication),
    /// "Criar Usuário no Banco" (mapeia um login existente a um usuário de
    /// um banco específico), "Permissões de Servidor" (papéis fixos +
    /// seção avançada com permissões granulares, por login) e "Permissões
    /// de Banco/Tabelas e Views" (papéis fixos + avançado do banco + grade
    /// de SELECT/INSERT/UPDATE/DELETE por tabela/view, por usuário de um
    /// banco específico). Os checkboxes de permissão granular/tabela-view
    /// são de 3 estados (ver <see cref="CriarGradePermissoes"/>/
    /// <see cref="CriarGradeObjetos"/>): marcado = concedida (GRANT),
    /// desmarcado = negada (DENY), indeterminado/cinza = não definida
    /// (REVOKE) — clique para alternar entre os 3.
    /// </remarks>
    private void MostrarPaginaUsuarios()
    {
        lblTituloPagina.Text = "Segurança > Usuários";

        var abas = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5F) };
        abas.TabPages.Add(CriarAbaCriarLogin());
        abas.TabPages.Add(CriarAbaCriarUsuarioBanco());
        abas.TabPages.Add(CriarAbaPermissoesServidor());
        abas.TabPages.Add(CriarAbaPermissoesBanco());

        DefinirConteudo(abas);
    }

    /// <summary>
    /// Aba "Criar Login" de <see cref="MostrarPaginaUsuarios"/> — SQL
    /// Server Authentication (usuário/senha) ou Windows Authentication
    /// (conta de domínio/local já existente), com uma grade dos logins já
    /// existentes logo abaixo, para conferência imediata do resultado.
    /// </summary>
    private TabPage CriarAbaCriarLogin()
    {
        var aba = new TabPage("Criar Login");
        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Nome", HeaderText = "Login", DataPropertyName = "Nome", Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TipoAutenticacao", HeaderText = "Autenticação", DataPropertyName = "TipoAutenticacao", Width = 110 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Desabilitado", HeaderText = "Desabilitado", DataPropertyName = "Desabilitado", Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "DataCriacao", HeaderText = "Criado em", DataPropertyName = "DataCriacao",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy HH:mm:ss" }
        });

        var pnlForm = new Panel { Dock = DockStyle.Top, Height = 264 };

        var lblTipo = new Label { Text = "Tipo de Autenticação", AutoSize = true, Location = new Point(0, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var radioSql = new RadioButton { Text = "SQL Server Authentication", AutoSize = true, Location = new Point(0, 26), Checked = true };
        var radioWindows = new RadioButton { Text = "Windows Authentication", AutoSize = true, Location = new Point(230, 26) };

        var lblNomeLogin = new Label { Text = "Nome do Login", AutoSize = true, Location = new Point(0, 60), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtNomeLogin = new TextBox { Location = new Point(0, 82), Size = new Size(300, 26), PlaceholderText = @"Ex.: usuario_app  ou  DOMINIO\usuario" };

        var lblSenha = new Label { Text = "Senha", AutoSize = true, Location = new Point(0, 116), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtSenha = new TextBox { Location = new Point(0, 138), Size = new Size(300, 26), UseSystemPasswordChar = true };

        var lblConfirmarSenha = new Label { Text = "Confirmar Senha", AutoSize = true, Location = new Point(320, 116), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtConfirmarSenha = new TextBox { Location = new Point(320, 138), Size = new Size(300, 26), UseSystemPasswordChar = true };

        var chkExigirTroca = new CheckBox { Text = "Exigir troca de senha no próximo login (MUST_CHANGE)", AutoSize = true, Location = new Point(0, 174) };
        var chkPolitica = new CheckBox { Text = "Aplicar política de senha do Windows (CHECK_POLICY)", AutoSize = true, Location = new Point(0, 198), Checked = true };
        var chkExpiracao = new CheckBox { Text = "Aplicar expiração de senha (CHECK_EXPIRATION)", AutoSize = true, Location = new Point(0, 222) };

        var btnCriar = CriarBotaoAcao("Criar Login");
        btnCriar.Location = new Point(560, 82);

        var lblStatus = new Label
        {
            Location = new Point(0, 252),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = CorTextoSecundario
        };

        void AtualizarCamposPorTipo()
        {
            var ehSql = radioSql.Checked;
            lblSenha.Visible = txtSenha.Visible = ehSql;
            lblConfirmarSenha.Visible = txtConfirmarSenha.Visible = ehSql;
            chkExigirTroca.Visible = chkPolitica.Visible = chkExpiracao.Visible = ehSql;
            lblNomeLogin.Text = ehSql ? "Nome do Login" : @"Nome do Login (ex.: DOMINIO\usuario)";
        }
        radioSql.CheckedChanged += (_, _) => AtualizarCamposPorTipo();
        radioWindows.CheckedChanged += (_, _) => AtualizarCamposPorTipo();
        AtualizarCamposPorTipo();

        pnlForm.Controls.Add(lblTipo);
        pnlForm.Controls.Add(radioSql);
        pnlForm.Controls.Add(radioWindows);
        pnlForm.Controls.Add(lblNomeLogin);
        pnlForm.Controls.Add(txtNomeLogin);
        pnlForm.Controls.Add(lblSenha);
        pnlForm.Controls.Add(txtSenha);
        pnlForm.Controls.Add(lblConfirmarSenha);
        pnlForm.Controls.Add(txtConfirmarSenha);
        pnlForm.Controls.Add(chkExigirTroca);
        pnlForm.Controls.Add(chkPolitica);
        pnlForm.Controls.Add(chkExpiracao);
        pnlForm.Controls.Add(btnCriar);
        pnlForm.Controls.Add(lblStatus);

        async Task CarregarLoginsAsync()
        {
            try
            {
                grid.DataSource = await _moduloAdmin.ObterLoginsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        btnCriar.Click += async (_, _) =>
        {
            var nomeLogin = txtNomeLogin.Text.Trim();
            if (string.IsNullOrWhiteSpace(nomeLogin))
            {
                MessageBox.Show(this, "Informe o nome do login.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (radioSql.Checked && txtSenha.Text != txtConfirmarSenha.Text)
            {
                MessageBox.Show(this, "As senhas informadas não conferem.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirmacao = MessageBox.Show(this,
                $"Confirma a criação do login \"{nomeLogin}\" ({(radioSql.Checked ? "SQL Server Authentication" : "Windows Authentication")})?",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            btnCriar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (radioSql.Checked)
                {
                    await _moduloAdmin.CriarLoginSqlAsync(nomeLogin, txtSenha.Text, chkExigirTroca.Checked, chkPolitica.Checked, chkExpiracao.Checked);
                }
                else
                {
                    await _moduloAdmin.CriarLoginWindowsAsync(nomeLogin);
                }

                lblStatus.Text = $"Login \"{nomeLogin}\" criado com sucesso em {DateTime.Now:HH:mm:ss}.";
                txtNomeLogin.Text = string.Empty;
                txtSenha.Text = string.Empty;
                txtConfirmarSenha.Text = string.Empty;
                await CarregarLoginsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCriar.Enabled = true;
            }
        };

        var pnlGridTopo = new Panel { Dock = DockStyle.Top, Height = 36 };
        var lblLoginsExistentes = new Label { Text = "Logins existentes", Dock = DockStyle.Left, Width = 200, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = CorTitulo, TextAlign = ContentAlignment.MiddleLeft };
        var btnAtualizarLogins = CriarBotaoAcao("Atualizar");
        btnAtualizarLogins.Dock = DockStyle.Right;
        btnAtualizarLogins.Click += (_, _) => _ = CarregarLoginsAsync();
        pnlGridTopo.Controls.Add(lblLoginsExistentes);
        pnlGridTopo.Controls.Add(btnAtualizarLogins);

        var pnlGrid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        pnlGrid.Controls.Add(grid);
        pnlGrid.Controls.Add(pnlGridTopo);

        cartao.Controls.Add(pnlGrid);
        cartao.Controls.Add(pnlForm);

        aba.Controls.Add(cartao);

        _ = CarregarLoginsAsync();

        return aba;
    }

    /// <summary>
    /// Aba "Criar Usuário no Banco" de <see cref="MostrarPaginaUsuarios"/>
    /// — mapeia um login já existente a um usuário de um banco específico
    /// (CREATE USER ... FOR LOGIN ...), com uma grade dos usuários já
    /// existentes no banco selecionado logo abaixo.
    /// </summary>
    private TabPage CriarAbaCriarUsuarioBanco()
    {
        var aba = new TabPage("Criar Usuário no Banco");
        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Nome", HeaderText = "Usuário", DataPropertyName = "Nome", Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LoginAssociado", HeaderText = "Login associado", DataPropertyName = "LoginAssociado", Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TipoUsuario", HeaderText = "Tipo", DataPropertyName = "TipoUsuario", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        var pnlForm = new Panel { Dock = DockStyle.Top, Height = 120 };

        var lblBanco = new Label { Text = "Banco de Dados", AutoSize = true, Location = new Point(0, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbBanco = new ComboBox { Location = new Point(0, 26), Size = new Size(220, 26), DropDownStyle = ComboBoxStyle.DropDownList };

        var lblLogin = new Label { Text = "Login", AutoSize = true, Location = new Point(240, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbLogin = new ComboBox { Location = new Point(240, 26), Size = new Size(220, 26), DropDownStyle = ComboBoxStyle.DropDownList };

        var lblNomeUsuario = new Label { Text = "Nome do Usuário (opcional — padrão: nome do login)", AutoSize = true, Location = new Point(480, 4), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var txtNomeUsuario = new TextBox { Location = new Point(480, 26), Size = new Size(240, 26) };

        var btnCriar = CriarBotaoAcao("Criar Usuário");
        btnCriar.Location = new Point(0, 66);

        var lblStatus = new Label
        {
            Location = new Point(180, 74),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = CorTextoSecundario
        };

        pnlForm.Controls.Add(lblBanco);
        pnlForm.Controls.Add(cmbBanco);
        pnlForm.Controls.Add(lblLogin);
        pnlForm.Controls.Add(cmbLogin);
        pnlForm.Controls.Add(lblNomeUsuario);
        pnlForm.Controls.Add(txtNomeUsuario);
        pnlForm.Controls.Add(btnCriar);
        pnlForm.Controls.Add(lblStatus);

        async Task CarregarUsuariosAsync()
        {
            if (cmbBanco.SelectedItem is not string banco)
            {
                return;
            }
            try
            {
                grid.DataSource = await _moduloAdmin.ObterUsuariosBancoAsync(banco);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        async Task CarregarBancosAsync()
        {
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBanco.Items.Clear();
                cmbBanco.Items.AddRange(bancos.ToArray());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbBanco.Items.Count > 0)
            {
                cmbBanco.SelectedIndex = 0;
            }

            cmbBanco.SelectedIndexChanged += (_, _) => _ = CarregarUsuariosAsync();

            if (cmbBanco.Items.Count > 0)
            {
                await CarregarUsuariosAsync();
            }
        }

        async Task CarregarLoginsAsync()
        {
            try
            {
                var logins = await _moduloAdmin.ObterLoginsAsync();
                cmbLogin.Items.Clear();
                cmbLogin.Items.AddRange(logins.Cast<object>().ToArray());
                if (cmbLogin.Items.Count > 0)
                {
                    cmbLogin.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        btnCriar.Click += async (_, _) =>
        {
            if (cmbBanco.SelectedItem is not string banco)
            {
                MessageBox.Show(this, "Selecione o banco de dados.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (cmbLogin.SelectedItem is not LoginDto login)
            {
                MessageBox.Show(this, "Selecione o login.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnCriar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                await _moduloAdmin.CriarUsuarioBancoAsync(banco, login.Nome, txtNomeUsuario.Text);
                lblStatus.Text = $"Usuário criado no banco \"{banco}\" com sucesso em {DateTime.Now:HH:mm:ss}.";
                txtNomeUsuario.Text = string.Empty;
                await CarregarUsuariosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCriar.Enabled = true;
            }
        };

        // Pedido do usuário (funcionário testou): "as telas precisam de um
        // botão de refresh em todas as abas" — antes só a aba "Criar Login"
        // tinha "Atualizar". Aqui reconsulta as listas de bancos e logins
        // (preservando a seleção atual quando ela continuar existindo) e a
        // grade de usuários do banco selecionado — útil, por exemplo, depois
        // de criar um login novo na aba "Criar Login" sem sair desta tela.
        var pnlGridTopo = new Panel { Dock = DockStyle.Top, Height = 36 };
        var lblUsuariosExistentes = new Label { Text = "Usuários existentes", Dock = DockStyle.Left, Width = 200, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = CorTitulo, TextAlign = ContentAlignment.MiddleLeft };
        var btnAtualizarUsuarios = CriarBotaoAcao("Atualizar");
        btnAtualizarUsuarios.Dock = DockStyle.Right;
        btnAtualizarUsuarios.Click += async (_, _) =>
        {
            var bancoAnterior = cmbBanco.SelectedItem as string;
            var loginAnterior = cmbLogin.SelectedItem as LoginDto;

            btnAtualizarUsuarios.Enabled = false;
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBanco.Items.Clear();
                cmbBanco.Items.AddRange(bancos.ToArray());
                if (bancoAnterior != null && cmbBanco.Items.Contains(bancoAnterior))
                {
                    cmbBanco.SelectedItem = bancoAnterior;
                }
                else if (cmbBanco.Items.Count > 0)
                {
                    cmbBanco.SelectedIndex = 0;
                }

                var logins = await _moduloAdmin.ObterLoginsAsync();
                cmbLogin.Items.Clear();
                cmbLogin.Items.AddRange(logins.Cast<object>().ToArray());
                var loginCorrespondente = loginAnterior != null ? logins.FirstOrDefault(l => l.Nome == loginAnterior.Nome) : null;
                if (loginCorrespondente != null)
                {
                    cmbLogin.SelectedItem = loginCorrespondente;
                }
                else if (cmbLogin.Items.Count > 0)
                {
                    cmbLogin.SelectedIndex = 0;
                }

                await CarregarUsuariosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnAtualizarUsuarios.Enabled = true;
            }
        };
        pnlGridTopo.Controls.Add(lblUsuariosExistentes);
        pnlGridTopo.Controls.Add(btnAtualizarUsuarios);

        var pnlGrid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        pnlGrid.Controls.Add(grid);
        pnlGrid.Controls.Add(pnlGridTopo);

        cartao.Controls.Add(pnlGrid);
        cartao.Controls.Add(pnlForm);

        aba.Controls.Add(cartao);

        _ = CarregarBancosAsync();
        _ = CarregarLoginsAsync();

        return aba;
    }

    /// <summary>
    /// Aba "Permissões de Servidor" de <see cref="MostrarPaginaUsuarios"/>
    /// — papéis fixos de servidor (<see cref="CriarGradePapeis"/>) e, na
    /// seção avançada, permissões granulares (<see cref="CriarGradePermissoes"/>)
    /// para o login selecionado.
    /// </summary>
    private TabPage CriarAbaPermissoesServidor()
    {
        var aba = new TabPage("Permissões de Servidor");
        var cartao = CriarCartao();

        var gridPapeis = CriarGradePapeis();
        var gridPermissoes = CriarGradePermissoes();

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 44 };
        var lblLogin = new Label { Text = "Login", AutoSize = true, Location = new Point(0, 10), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbLogin = new ComboBox { Location = new Point(50, 6), Size = new Size(280, 26), DropDownStyle = ComboBoxStyle.DropDownList };
        var btnCarregar = CriarBotaoAcao("Carregar");
        btnCarregar.Location = new Point(340, 4);
        btnCarregar.Size = new Size(110, 30);
        // Pedido do usuário (funcionário testou): botão de refresh nesta
        // aba — a lista de logins do combo só era carregada uma vez, ao
        // abrir a tela; se um login novo fosse criado na aba "Criar Login"
        // sem sair desta, não aparecia aqui até reabrir a página inteira.
        var btnAtualizarLogins = CriarBotaoAcao("Atualizar Lista");
        btnAtualizarLogins.Location = new Point(460, 4);
        btnAtualizarLogins.Size = new Size(130, 30);
        pnlTopo.Controls.Add(lblLogin);
        pnlTopo.Controls.Add(cmbLogin);
        pnlTopo.Controls.Add(btnCarregar);
        pnlTopo.Controls.Add(btnAtualizarLogins);

        var pnlPapeis = new Panel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(0, 10, 0, 0) };
        var lblPapeis = new Label { Text = "Papéis do Servidor (server roles)", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = CorTitulo };
        pnlPapeis.Controls.Add(gridPapeis);
        pnlPapeis.Controls.Add(lblPapeis);

        var pnlGranular = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        var lblGranular = new Label { Text = "Avançado — Permissões Granulares (GRANT/DENY individuais)", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = CorTitulo };
        pnlGranular.Controls.Add(gridPermissoes);
        pnlGranular.Controls.Add(CriarLegendaTriState());
        pnlGranular.Controls.Add(lblGranular);

        var pnlRodape = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, 12, 0, 0) };
        var btnAplicar = CriarBotaoAcao("Aplicar Alterações");
        btnAplicar.Enabled = false;
        var lblStatus = new Label
        {
            Location = new Point(180, 12),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = CorTextoSecundario
        };
        pnlRodape.Controls.Add(btnAplicar);
        pnlRodape.Controls.Add(lblStatus);

        async Task CarregarLoginsComboAsync()
        {
            try
            {
                var logins = await _moduloAdmin.ObterLoginsAsync();
                cmbLogin.Items.Clear();
                cmbLogin.Items.AddRange(logins.Cast<object>().ToArray());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        async Task CarregarPermissoesAsync()
        {
            if (cmbLogin.SelectedItem is not LoginDto login)
            {
                MessageBox.Show(this, "Selecione um login.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnCarregar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                gridPapeis.DataSource = await _moduloAdmin.ObterPapeisServidorAsync(login.Nome);
                gridPermissoes.DataSource = await _moduloAdmin.ObterPermissoesServidorGranularesAsync(login.Nome);
                btnAplicar.Enabled = true;
                lblStatus.Text = $"Permissões de \"{login.Nome}\" carregadas em {DateTime.Now:HH:mm:ss}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCarregar.Enabled = true;
            }
        }

        btnCarregar.Click += (_, _) => _ = CarregarPermissoesAsync();

        btnAtualizarLogins.Click += async (_, _) =>
        {
            var loginAnterior = cmbLogin.SelectedItem as LoginDto;
            btnAtualizarLogins.Enabled = false;
            try
            {
                var logins = await _moduloAdmin.ObterLoginsAsync();
                cmbLogin.Items.Clear();
                cmbLogin.Items.AddRange(logins.Cast<object>().ToArray());
                var correspondente = loginAnterior != null ? logins.FirstOrDefault(l => l.Nome == loginAnterior.Nome) : null;
                if (correspondente != null)
                {
                    cmbLogin.SelectedItem = correspondente;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnAtualizarLogins.Enabled = true;
            }
        };

        btnAplicar.Click += async (_, _) =>
        {
            if (cmbLogin.SelectedItem is not LoginDto login
                || gridPapeis.DataSource is not List<PapelDto> papeis
                || gridPermissoes.DataSource is not List<PermissaoDto> permissoes)
            {
                return;
            }

            gridPapeis.EndEdit();
            gridPermissoes.EndEdit();

            var confirmacao = MessageBox.Show(this,
                $"Confirma a aplicação das alterações de permissão para o login \"{login.Nome}\"?",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            btnAplicar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                await _moduloAdmin.AtualizarPapeisServidorAsync(login.Nome, papeis);
                await _moduloAdmin.AtualizarPermissoesServidorGranularesAsync(login.Nome, permissoes);
                lblStatus.Text = $"Permissões de \"{login.Nome}\" atualizadas com sucesso em {DateTime.Now:HH:mm:ss}.";
                await CarregarPermissoesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnAplicar.Enabled = true;
            }
        };

        cartao.Controls.Add(pnlGranular);
        cartao.Controls.Add(pnlPapeis);
        cartao.Controls.Add(pnlRodape);
        cartao.Controls.Add(pnlTopo);

        aba.Controls.Add(cartao);

        _ = CarregarLoginsComboAsync();

        return aba;
    }

    /// <summary>
    /// Aba "Permissões de Banco/Tabelas e Views" de
    /// <see cref="MostrarPaginaUsuarios"/> — papéis fixos de banco
    /// (<see cref="CriarGradePapeis"/>), permissões granulares de banco na
    /// seção avançada (<see cref="CriarGradePermissoes"/>) e, separadamente
    /// (pedido explícito do usuário), a grade de SELECT/INSERT/UPDATE/
    /// DELETE por tabela/view (<see cref="CriarGradeObjetos"/>) — tudo
    /// para o usuário de banco selecionado.
    /// </summary>
    private TabPage CriarAbaPermissoesBanco()
    {
        var aba = new TabPage("Permissões de Banco/Tabelas e Views");
        var cartao = CriarCartao();

        var gridPapeis = CriarGradePapeis();
        var gridPermissoes = CriarGradePermissoes();
        var gridObjetos = CriarGradeObjetos();

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 44 };
        var lblBanco = new Label { Text = "Banco", AutoSize = true, Location = new Point(0, 10), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbBanco = new ComboBox { Location = new Point(52, 6), Size = new Size(180, 26), DropDownStyle = ComboBoxStyle.DropDownList };
        var lblUsuario = new Label { Text = "Usuário", AutoSize = true, Location = new Point(244, 10), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbUsuario = new ComboBox { Location = new Point(300, 6), Size = new Size(200, 26), DropDownStyle = ComboBoxStyle.DropDownList };
        var btnCarregar = CriarBotaoAcao("Carregar");
        btnCarregar.Location = new Point(510, 4);
        btnCarregar.Size = new Size(110, 30);
        // Pedido do usuário (funcionário testou): botão de refresh nesta
        // aba — os combos "Banco"/"Usuário" só eram carregados uma vez, ao
        // abrir a tela; se um banco/usuário novo fosse criado em outra aba
        // sem sair desta, não apareciam aqui até reabrir a página inteira.
        var btnAtualizarListas = CriarBotaoAcao("Atualizar Listas");
        btnAtualizarListas.Location = new Point(630, 4);
        btnAtualizarListas.Size = new Size(140, 30);
        pnlTopo.Controls.Add(lblBanco);
        pnlTopo.Controls.Add(cmbBanco);
        pnlTopo.Controls.Add(lblUsuario);
        pnlTopo.Controls.Add(cmbUsuario);
        pnlTopo.Controls.Add(btnCarregar);
        pnlTopo.Controls.Add(btnAtualizarListas);

        var pnlPapeis = new Panel { Dock = DockStyle.Top, Height = 160, Padding = new Padding(0, 10, 0, 0) };
        var lblPapeis = new Label { Text = "Papéis do Banco de Dados (database roles)", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = CorTitulo };
        pnlPapeis.Controls.Add(gridPapeis);
        pnlPapeis.Controls.Add(lblPapeis);

        var pnlGranular = new Panel { Dock = DockStyle.Top, Height = 170, Padding = new Padding(0, 10, 0, 0) };
        var lblGranular = new Label { Text = "Avançado — Permissões Granulares do Banco (GRANT/DENY individuais)", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = CorTitulo };
        pnlGranular.Controls.Add(gridPermissoes);
        pnlGranular.Controls.Add(CriarLegendaTriState());
        pnlGranular.Controls.Add(lblGranular);

        // Pedido do usuário (funcionário testou): "botão para filtrar as
        // tabelas e views na hora de dar permissões" — a grade de baixo
        // pode ter centenas de tabelas/views num banco grande. O filtro só
        // esconde linhas (DataGridViewRow.Visible) sem trocar o DataSource
        // da grade — importante porque gridObjetos.DataSource continua
        // sendo a MESMA List<PermissaoObjetoDto> usada por btnAplicar, e
        // ConfigurarCommitImediato grava cada clique de checkbox direto nos
        // objetos dessa lista; se o filtro reatribuísse um DataSource novo
        // (uma sublista), "Aplicar Alterações" enviaria só as linhas
        // visíveis e perderia qualquer alteração feita antes em linhas que
        // ficaram fora do filtro.
        var pnlFiltroObjetos = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(0, 4, 0, 4) };
        var lblFiltroObjetos = new Label
        {
            Text = "Filtrar (esquema/tabela/view)",
            AutoSize = true,
            Location = new Point(0, 8),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTextoSecundario
        };
        var txtFiltroObjetos = new TextBox { Location = new Point(190, 4), Size = new Size(240, 24) };
        var btnFiltrarObjetos = CriarBotaoAcao("Filtrar");
        btnFiltrarObjetos.Location = new Point(438, 2);
        btnFiltrarObjetos.Size = new Size(90, 26);
        var btnLimparFiltroObjetos = CriarBotaoAcao("Limpar");
        btnLimparFiltroObjetos.Location = new Point(534, 2);
        btnLimparFiltroObjetos.Size = new Size(90, 26);
        pnlFiltroObjetos.Controls.Add(lblFiltroObjetos);
        pnlFiltroObjetos.Controls.Add(txtFiltroObjetos);
        pnlFiltroObjetos.Controls.Add(btnFiltrarObjetos);
        pnlFiltroObjetos.Controls.Add(btnLimparFiltroObjetos);

        var pnlObjetos = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        var lblObjetos = new Label { Text = "Permissões em Tabelas e Views", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = CorTitulo };
        pnlObjetos.Controls.Add(gridObjetos);
        pnlObjetos.Controls.Add(CriarLegendaTriState());
        pnlObjetos.Controls.Add(pnlFiltroObjetos);
        pnlObjetos.Controls.Add(lblObjetos);

        var pnlRodape = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, 12, 0, 0) };
        var btnAplicar = CriarBotaoAcao("Aplicar Alterações");
        btnAplicar.Enabled = false;
        var lblStatus = new Label
        {
            Location = new Point(180, 12),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = CorTextoSecundario
        };
        pnlRodape.Controls.Add(btnAplicar);
        pnlRodape.Controls.Add(lblStatus);

        async Task CarregarUsuariosAsync()
        {
            if (cmbBanco.SelectedItem is not string banco)
            {
                return;
            }
            try
            {
                var usuarios = await _moduloAdmin.ObterUsuariosBancoAsync(banco);
                cmbUsuario.Items.Clear();
                cmbUsuario.Items.AddRange(usuarios.Cast<object>().ToArray());
                if (cmbUsuario.Items.Count > 0)
                {
                    cmbUsuario.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        async Task CarregarBancosAsync()
        {
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBanco.Items.Clear();
                cmbBanco.Items.AddRange(bancos.ToArray());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbBanco.Items.Count > 0)
            {
                cmbBanco.SelectedIndex = 0;
            }

            cmbBanco.SelectedIndexChanged += (_, _) => _ = CarregarUsuariosAsync();

            if (cmbBanco.Items.Count > 0)
            {
                await CarregarUsuariosAsync();
            }
        }

        // Esconde/mostra linhas de gridObjetos por esquema/nome (sem trocar
        // o DataSource — ver comentário acima de pnlFiltroObjetos). Chamada
        // pelo botão "Filtrar", pelo Enter na caixa de texto, e de novo ao
        // final de CarregarPermissoesAsync (pra manter o filtro aplicado
        // depois de um "Carregar"/"Atualizar Listas", em vez de voltar a
        // mostrar tudo silenciosamente).
        void AplicarFiltroObjetos()
        {
            var filtro = txtFiltroObjetos.Text.Trim();
            foreach (DataGridViewRow linha in gridObjetos.Rows)
            {
                if (string.IsNullOrEmpty(filtro))
                {
                    linha.Visible = true;
                    continue;
                }

                var esquema = Convert.ToString(linha.Cells["Esquema"].Value) ?? string.Empty;
                var objeto = Convert.ToString(linha.Cells["NomeObjeto"].Value) ?? string.Empty;
                linha.Visible = esquema.Contains(filtro, StringComparison.OrdinalIgnoreCase)
                    || objeto.Contains(filtro, StringComparison.OrdinalIgnoreCase);
            }
        }

        btnFiltrarObjetos.Click += (_, _) => AplicarFiltroObjetos();
        btnLimparFiltroObjetos.Click += (_, _) =>
        {
            txtFiltroObjetos.Text = string.Empty;
            AplicarFiltroObjetos();
        };
        txtFiltroObjetos.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AplicarFiltroObjetos();
            }
        };

        async Task CarregarPermissoesAsync()
        {
            if (cmbBanco.SelectedItem is not string banco || cmbUsuario.SelectedItem is not UsuarioBancoDto usuario)
            {
                MessageBox.Show(this, "Selecione o banco de dados e o usuário.", "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnCarregar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                gridPapeis.DataSource = await _moduloAdmin.ObterPapeisBancoAsync(banco, usuario.Nome);
                gridPermissoes.DataSource = await _moduloAdmin.ObterPermissoesBancoGranularesAsync(banco, usuario.Nome);
                gridObjetos.DataSource = await _moduloAdmin.ObterPermissoesObjetosAsync(banco, usuario.Nome);
                AplicarFiltroObjetos();
                btnAplicar.Enabled = true;
                lblStatus.Text = $"Permissões de \"{usuario.Nome}\" no banco \"{banco}\" carregadas em {DateTime.Now:HH:mm:ss}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCarregar.Enabled = true;
            }
        }

        btnCarregar.Click += (_, _) => _ = CarregarPermissoesAsync();

        btnAtualizarListas.Click += async (_, _) =>
        {
            var bancoAnterior = cmbBanco.SelectedItem as string;
            var usuarioAnterior = cmbUsuario.SelectedItem as UsuarioBancoDto;

            btnAtualizarListas.Enabled = false;
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBanco.Items.Clear();
                cmbBanco.Items.AddRange(bancos.ToArray());
                if (bancoAnterior != null && cmbBanco.Items.Contains(bancoAnterior))
                {
                    cmbBanco.SelectedItem = bancoAnterior;
                }
                else if (cmbBanco.Items.Count > 0)
                {
                    cmbBanco.SelectedIndex = 0;
                }

                // CarregarUsuariosAsync já roda sozinha via
                // SelectedIndexChanged quando o banco muda de fato acima;
                // chamando de novo aqui garante que o combo "Usuário"
                // também seja reconsultado quando o banco selecionado
                // continuar sendo o mesmo de antes (senão ficaria com a
                // lista antiga).
                await CarregarUsuariosAsync();
                if (usuarioAnterior != null)
                {
                    var usuarioCorrespondente = cmbUsuario.Items.Cast<UsuarioBancoDto>()
                        .FirstOrDefault(u => u.Nome == usuarioAnterior.Nome);
                    if (usuarioCorrespondente != null)
                    {
                        cmbUsuario.SelectedItem = usuarioCorrespondente;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnAtualizarListas.Enabled = true;
            }
        };

        btnAplicar.Click += async (_, _) =>
        {
            if (cmbBanco.SelectedItem is not string banco || cmbUsuario.SelectedItem is not UsuarioBancoDto usuario
                || gridPapeis.DataSource is not List<PapelDto> papeis
                || gridPermissoes.DataSource is not List<PermissaoDto> permissoes
                || gridObjetos.DataSource is not List<PermissaoObjetoDto> objetos)
            {
                return;
            }

            gridPapeis.EndEdit();
            gridPermissoes.EndEdit();
            gridObjetos.EndEdit();

            var confirmacao = MessageBox.Show(this,
                $"Confirma a aplicação das alterações de permissão para o usuário \"{usuario.Nome}\" no banco \"{banco}\"?",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            btnAplicar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                await _moduloAdmin.AtualizarPapeisBancoAsync(banco, usuario.Nome, papeis);
                await _moduloAdmin.AtualizarPermissoesBancoGranularesAsync(banco, usuario.Nome, permissoes);
                await _moduloAdmin.AtualizarPermissoesObjetosAsync(banco, usuario.Nome, objetos);
                lblStatus.Text = $"Permissões de \"{usuario.Nome}\" no banco \"{banco}\" atualizadas com sucesso em {DateTime.Now:HH:mm:ss}.";
                await CarregarPermissoesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnAplicar.Enabled = true;
            }
        };

        cartao.Controls.Add(pnlObjetos);
        cartao.Controls.Add(pnlGranular);
        cartao.Controls.Add(pnlPapeis);
        cartao.Controls.Add(pnlRodape);
        cartao.Controls.Add(pnlTopo);

        aba.Controls.Add(cartao);

        _ = CarregarBancosAsync();

        return aba;
    }

    /// <summary>
    /// Grade reutilizada nas abas "Permissões de Servidor"/"Permissões de
    /// Banco/Tabelas e Views" para os PAPÉIS FIXOS (server roles/database
    /// roles) — checkbox "Membro" editável, colunas "Papel"/"Descrição"
    /// somente leitura. Ver <see cref="ConfigurarCommitImediato"/>.
    /// </summary>
    private static DataGridView CriarGradePapeis()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Membro", HeaderText = "Membro", DataPropertyName = "Membro", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Nome", HeaderText = "Papel", DataPropertyName = "Nome", Width = 150, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Descricao", HeaderText = "Descrição", DataPropertyName = "Descricao", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        ConfigurarCommitImediato(grid);
        return grid;
    }

    /// <summary>
    /// Grade reutilizada nas seções "avançado" de "Permissões de
    /// Servidor"/"Permissões de Banco/Tabelas e Views" para PERMISSÕES
    /// GRANULARES — checkbox de 3 estados "Concedida" (marcado = GRANT,
    /// desmarcado = DENY, indeterminado = não definida/REVOKE).
    /// </summary>
    private static DataGridView CriarGradePermissoes()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Estado", HeaderText = "Concedida", DataPropertyName = "Estado", ThreeState = true, Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Nome", HeaderText = "Permissão", DataPropertyName = "Nome", Width = 200, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Descricao", HeaderText = "Descrição", DataPropertyName = "Descricao", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        ConfigurarCommitImediato(grid);
        return grid;
    }

    /// <summary>
    /// Grade de permissões em TABELAS e VIEWS (aba "Permissões de
    /// Banco/Tabelas e Views") — mesmo checkbox de 3 estados de
    /// <see cref="CriarGradePermissoes"/>, um conjunto (Select/Insert/
    /// Update/Delete) por linha (tabela/view).
    /// </summary>
    private static DataGridView CriarGradeObjetos()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Esquema", HeaderText = "Esquema", DataPropertyName = "Esquema", Width = 90, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NomeObjeto", HeaderText = "Objeto", DataPropertyName = "NomeObjeto", Width = 180, ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TipoObjeto", HeaderText = "Tipo", DataPropertyName = "TipoObjeto", Width = 70, ReadOnly = true });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", HeaderText = "Select", DataPropertyName = "Select", ThreeState = true, Width = 60 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Insert", HeaderText = "Insert", DataPropertyName = "Insert", ThreeState = true, Width = 60 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Update", HeaderText = "Update", DataPropertyName = "Update", ThreeState = true, Width = 60 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Delete", HeaderText = "Delete", DataPropertyName = "Delete", ThreeState = true, Width = 60 });
        ConfigurarCommitImediato(grid);
        return grid;
    }

    /// <summary>Legenda curta explicando a semântica dos checkboxes de 3 estados usados nas grades de permissão granular/tabelas-views.</summary>
    private static Label CriarLegendaTriState() => new()
    {
        Text = "Marcado = concedida (GRANT)   |   Desmarcado = negada (DENY)   |   Cinza = não definida/herda (REVOKE) — clique para alternar entre os 3 estados.",
        Dock = DockStyle.Top,
        Height = 18,
        Font = new Font("Segoe UI", 8F, FontStyle.Italic),
        ForeColor = CorTextoSecundario
    };

    /// <summary>
    /// Faz o valor de uma célula de checkbox (inclusive as de 3 estados)
    /// ser gravado no objeto de origem (List&lt;T&gt; usada como
    /// DataSource) assim que o usuário clica — sem isso, o valor só é
    /// propagado quando a célula perde o foco, e um clique seguido direto
    /// do botão "Aplicar" (sem trocar de célula) ignoraria a alteração.
    /// </summary>
    private static void ConfigurarCommitImediato(DataGridView grid)
    {
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
    }

    /// <summary>
    /// Página "Desempenho > Top N consultas": mesmo script clássico de
    /// tuning ("Listagem 5" — sys.dm_exec_query_stats + sys.dm_exec_sql_text
    /// + sys.dm_exec_query_plan) fornecido pelo usuário, com dois filtros que
    /// não existiam no script original: quantidade (Top 15/25/50/100) e
    /// banco de dados opcional (em branco = todos os bancos da instância,
    /// equivalente a manter o "WHERE DB.name = ..." do script comentado).
    /// A grade tem muitas colunas (uma para cada métrica do
    /// sys.dm_exec_query_stats), então usa AutoSizeColumnsMode = AllCells
    /// (cada coluna do tamanho do próprio conteúdo, com rolagem horizontal)
    /// em vez de Fill — com Fill, 20+ colunas ficariam espremidas e
    /// ilegíveis.
    /// </summary>
    private void MostrarPaginaTop25Consultas()
    {
        lblTituloPagina.Text = "Desempenho > Top 25 consultas";

        var cartao = CriarCartao();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        var lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Carregando..."
        };

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 56 };

        var lblTop = new Label
        {
            Text = "Top:",
            AutoSize = true,
            Location = new Point(0, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbTop = new ComboBox
        {
            Location = new Point(38, 11),
            Size = new Size(70, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbTop.Items.AddRange(new object[] { 15, 25, 50, 100 });
        cmbTop.SelectedItem = 25;

        var lblBanco = new Label
        {
            Text = "Banco de dados:",
            AutoSize = true,
            Location = new Point(128, 15),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var cmbBanco = new ComboBox
        {
            Location = new Point(240, 11),
            Size = new Size(230, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            Sorted = true,
            // Em branco = traz de todos os bancos (não é obrigatório escolher um).
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

        var btnBuscar = CriarBotaoAcao("Buscar");
        btnBuscar.Location = new Point(484, 10);
        btnBuscar.Size = new Size(120, 30);

        pnlTopo.Controls.Add(lblTop);
        pnlTopo.Controls.Add(cmbTop);
        pnlTopo.Controls.Add(lblBanco);
        pnlTopo.Controls.Add(cmbBanco);
        pnlTopo.Controls.Add(btnBuscar);

        // Coluna do plano de execução: em vez de deixar o AutoSizeColumnsMode
        // (AllCells) esticar a coluna para caber o XML inteiro numa linha só
        // (o que deixaria a grade absurdamente larga), fixamos a largura e
        // deixamos o texto ser cortado — a linha inteira só é lida na janela
        // aberta pelo clique, tratado logo abaixo.
        grid.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0
                && grid.Columns[e.ColumnIndex].Name == nameof(ConsultaCustosaDto.PlanoExecucaoXml))
            {
                grid.Cursor = Cursors.Hand;
            }
        };
        grid.CellMouseLeave += (_, _) => grid.Cursor = Cursors.Default;

        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != nameof(ConsultaCustosaDto.PlanoExecucaoXml)) return;
            if (grid.Rows[e.RowIndex].DataBoundItem is ConsultaCustosaDto consulta)
            {
                AbrirJanelaPlanoExecucao(consulta.PlanoExecucaoXml);
            }
        };

        // Fill (grid) precisa ser adicionado antes dos Top (lblStatus, pnlTopo,
        // este por último para ficar por cima de tudo), senão o grid ocupa o
        // cartão inteiro por baixo e os painéis de topo cobrem parte do conteúdo.
        cartao.Controls.Add(grid);
        cartao.Controls.Add(lblStatus);
        cartao.Controls.Add(pnlTopo);

        DefinirConteudo(cartao);

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Esta
        // tela é só leitura, então tudo aqui pode ser abortado ao navegar.
        var tokenPagina = TokenPaginaAtiva;

        // Canal de erro desta tela (mesmo formato de CarregarDadosAsync).
        void ReportarFalhaCombo(string mensagem)
        {
            if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
            {
                return;
            }

            lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
            lblStatus.Text = mensagem;
        }

        async Task CarregarDadosAsync()
        {
            btnBuscar.Enabled = false;
            Cursor = Cursors.WaitCursor;
            lblStatus.ForeColor = CorTextoSecundario;
            lblStatus.Text = "Carregando...";
            try
            {
                var top = cmbTop.SelectedItem is int valor ? valor : 25;
                var banco = string.IsNullOrWhiteSpace(cmbBanco.Text) ? null : cmbBanco.Text.Trim();

                var dados = await _moduloDesempenho.ObterTop25ConsultasCustosasAsync(top, banco, tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = dados;

                // As colunas são recriadas a cada troca de DataSource (auto-geradas),
                // então a formatação especial da coluna de XML precisa ser reaplicada
                // toda vez que os dados são recarregados.
                if (grid.Columns[nameof(ConsultaCustosaDto.PlanoExecucaoXml)] is DataGridViewColumn colunaXml)
                {
                    colunaXml.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    colunaXml.Width = 240;
                    colunaXml.DefaultCellStyle.ForeColor = Color.FromArgb(31, 111, 191);
                    colunaXml.DefaultCellStyle.Font = FonteColunaClicavel;
                    colunaXml.ToolTipText = "Clique para abrir o plano de execução completo em uma nova janela.";
                }

                lblStatus.Text = banco is null
                    ? $"{dados.Count} consulta(s) encontrada(s) em todos os bancos."
                    : $"{dados.Count} consulta(s) encontrada(s) no banco \"{banco}\".";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                grid.DataSource = null;
                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnBuscar.Enabled = true;
            }
        }

        btnBuscar.Click += (_, _) => _ = CarregarDadosAsync();

        // Combo "Banco de Dados": helper único compartilhado pelas seis telas
        // que têm esse combo (ver PreencherComboBancosAsync).
        _ = PreencherComboBancosAsync(cmbBanco, ReportarFalhaCombo, tokenPagina);
        _ = CarregarDadosAsync();
    }

    /// <summary>
    /// Abre em uma janela separada (fora do padrão de páginas embutidas do
    /// menu) o plano de execução XML de uma linha da grade de Top N
    /// consultas. Janela separada aqui é proposital: é conteúdo grande e
    /// específico de uma linha, não uma seção de navegação do app.
    ///
    /// Oferece duas formas de ver o plano:
    /// 1) "Abrir plano gráfico": salva o XML EXATAMENTE como veio do SQL
    ///    Server (sem reformatar) em um arquivo temporário com extensão
    ///    .sqlplan e pede pro Windows abrir com o programa associado a essa
    ///    extensão (Process.Start com UseShellExecute=true) — é a mesma
    ///    técnica que o SSMS/Visual Studio usam para renderizar o plano
    ///    gráfico, então se o usuário tem SSMS ou Visual Studio com SQL
    ///    Server Data Tools instalado (associação padrão de .sqlplan), abre
    ///    direto no visualizador gráfico deles, sem precisar copiar/colar
    ///    manualmente como antes.
    /// 2) O texto XML (reformatado com indentação via XDocument para ficar
    ///    legível, com fallback pro texto cru se o parse falhar) continua
    ///    disponível abaixo, pra quem quiser inspecionar ou copiar o XML.
    /// </summary>
    private void AbrirJanelaPlanoExecucao(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            MessageBox.Show(this, "Esta linha não possui plano de execução disponível.", "Plano de Execução",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string textoExibido = xml;
        try
        {
            textoExibido = XDocument.Parse(xml).ToString();
        }
        catch
        {
            // Mostra o XML como veio do SQL Server, sem indentação, em vez de falhar.
        }

        var janela = new Form
        {
            Text = "Plano de Execução",
            Width = 1000,
            Height = 700,
            MinimumSize = new Size(500, 300),
            StartPosition = FormStartPosition.CenterParent
        };

        var txtPlano = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.White,
            Text = textoExibido
        };

        var pnlTopo = new Panel { Dock = DockStyle.Top, Height = 44 };

        var btnAbrirGrafico = CriarBotaoAcao("Abrir plano gráfico (.sqlplan)");
        btnAbrirGrafico.Location = new Point(8, 7);
        btnAbrirGrafico.Size = new Size(220, 30);
        btnAbrirGrafico.Click += (_, _) => AbrirPlanoGraficoExterno(xml);

        var lblDica = new Label
        {
            AutoSize = true,
            Location = new Point(240, 15),
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            Text = "Requer SSMS ou Visual Studio com SQL Server Data Tools associado a .sqlplan."
        };

        pnlTopo.Controls.Add(btnAbrirGrafico);
        pnlTopo.Controls.Add(lblDica);

        // Fill (txtPlano) precisa ser adicionado antes do Top (pnlTopo), senão
        // o TextBox ocupa a janela inteira por baixo e o painel de topo fica
        // escondido atrás dele.
        janela.Controls.Add(txtPlano);
        janela.Controls.Add(pnlTopo);

        janela.Show(this);
    }

    /// <summary>
    /// Salva o XML do plano de execução (sem reformatar — exatamente como o
    /// SQL Server retornou) em um arquivo temporário .sqlplan e pede pro
    /// Windows abrir com o programa associado a essa extensão. É a mesma
    /// abordagem do SSMS: um .sqlplan nada mais é do que esse XML com essa
    /// extensão; quem faz o desenho gráfico é o programa associado, não este
    /// app. Se não houver nenhum programa associado a .sqlplan na máquina
    /// (SSMS/Visual Studio com SSDT não instalado, ou associação removida),
    /// o Windows recusa a abertura e avisamos onde o arquivo ficou salvo.
    /// </summary>
    private void AbrirPlanoGraficoExterno(string xml)
    {
        string caminho;
        try
        {
            caminho = Path.Combine(Path.GetTempPath(), $"PlanoExecucao_{Guid.NewGuid():N}.sqlplan");
            File.WriteAllText(caminho, xml);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível salvar o arquivo temporário do plano.\n\n{ObterMensagemAmigavel(ex)}",
                "Plano de Execução", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(caminho) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Não há nenhum programa associado a arquivos .sqlplan nesta máquina " +
                "(instale o SSMS ou o Visual Studio com SQL Server Data Tools). " +
                $"O plano foi salvo em:\n{caminho}\n\nDetalhe: {ex.Message}",
                "Plano de Execução", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Página para métodos que retornam um único objeto "chave/valor" (ex:
    /// Informações > SQL/Servidor). Carrega automaticamente ao abrir a
    /// página — sem precisar clicar em nada — e apresenta cada propriedade
    /// como uma linha "rótulo : valor" estilizada (com listras alternadas,
    /// estilo lista de propriedades moderna), em vez do PropertyGrid padrão
    /// do Windows usado antes. O rótulo de cada linha vem do atributo
    /// [DisplayName] da propriedade (ver InformacoesSqlDto/InformacoesServidorDto),
    /// caindo para o nome da propriedade em C# quando ele não existir.
    /// </summary>
    /// <remarks>
    /// <paramref name="obterDados"/> recebe o token da página (ver
    /// <see cref="_ctsPaginaAtiva"/>) em vez de ser um <c>Func&lt;Task&lt;T&gt;&gt;</c>
    /// sem parâmetro: são consultas de LEITURA, então não faz sentido
    /// continuar ocupando o servidor depois que o usuário já saiu da tela.
    /// </remarks>
    private void MostrarPaginaInfo<T>(string titulo, string descricao, Func<CancellationToken, Task<T>> obterDados)
    {
        lblTituloPagina.Text = titulo;

        var cartao = CriarCartao();

        var lblDescricao = new Label
        {
            Text = descricao,
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 9F)
        };

        var lblStatus = new Label
        {
            Text = "Carregando...",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CorTextoSecundario,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Italic)
        };

        var pnlLinhas = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 8, 0, 0)
        };

        // Children Dock=Top empilham na ordem inversa em que são adicionados
        // (o último Controls.Add fica mais perto da borda de cima) — por
        // isso pnlLinhas entra primeiro e lblDescricao por último, para o
        // resultado visual ficar Descrição (topo) → Status → Linhas.
        cartao.Controls.Add(pnlLinhas);
        cartao.Controls.Add(lblStatus);
        cartao.Controls.Add(lblDescricao);

        DefinirConteudo(cartao);

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Tela
        // 100% de leitura, então a consulta pode ser abortada ao navegar.
        var tokenPagina = TokenPaginaAtiva;

        async Task CarregarDadosAsync()
        {
            try
            {
                var dados = await obterDados(tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                lblStatus.Visible = false;
                PreencherLinhasInfo(pnlLinhas, dados!);
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatus.IsDisposed)
                {
                    return;
                }

                lblStatus.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatus.Font = new Font("Segoe UI", 9.5F);
                lblStatus.Text = ObterMensagemAmigavel(ex);
            }
        }

        _ = CarregarDadosAsync();
    }

    /// <summary>
    /// Monta, via reflexão, uma linha "rótulo : valor" por propriedade
    /// pública de <paramref name="dados"/> (na ordem em que foram
    /// declaradas), dentro de <paramref name="pnlLinhas"/>.
    /// </summary>
    private static void PreencherLinhasInfo(Panel pnlLinhas, object dados)
    {
        pnlLinhas.Controls.Clear();

        var propriedades = dados.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Mesma regra de ordem de Dock=Top: para a propriedade[0] terminar
        // visualmente no topo, ela precisa ser a ÚLTIMA adicionada — por
        // isso o laço percorre de trás para frente.
        for (var i = propriedades.Length - 1; i >= 0; i--)
        {
            var propriedade = propriedades[i];
            var rotulo = propriedade.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? propriedade.Name;
            var valor = FormatarValorInfo(propriedade.GetValue(dados));
            pnlLinhas.Controls.Add(CriarLinhaInfo(rotulo, valor, linhaClara: i % 2 == 0));
        }
    }

    private static string FormatarValorInfo(object? valor) => valor switch
    {
        null => "-",
        DateTime data => data.ToString("dd/MM/yyyy HH:mm:ss"),
        _ => valor.ToString() ?? "-"
    };

    /// <summary>Uma linha "rótulo : valor" da página de Informações — largura do rótulo fixa, valor com quebra de linha automática.</summary>
    private static Panel CriarLinhaInfo(string rotulo, string valor, bool linhaClara)
    {
        const int paddingHorizontal = 16;
        const int paddingVertical = 10;
        const int larguraRotulo = 200;
        const int larguraValor = 460;

        var lblValor = new Label
        {
            Text = valor,
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = Color.FromArgb(40, 46, 60),
            Location = new Point(paddingHorizontal + larguraRotulo + 12, paddingVertical)
        };
        var tamanhoValor = lblValor.GetPreferredSize(new Size(larguraValor, 0));
        lblValor.Size = new Size(larguraValor, tamanhoValor.Height);

        var lblRotulo = new Label
        {
            Text = rotulo,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = CorTitulo,
            Location = new Point(paddingHorizontal, paddingVertical),
            Size = new Size(larguraRotulo, Math.Max(20, tamanhoValor.Height))
        };

        var linha = new Panel
        {
            Dock = DockStyle.Top,
            Height = Math.Max(lblRotulo.Bottom, lblValor.Bottom) + paddingVertical,
            BackColor = linhaClara ? Color.FromArgb(247, 249, 252) : Color.White
        };
        linha.Controls.Add(lblRotulo);
        linha.Controls.Add(lblValor);
        return linha;
    }

    /// <summary>
    /// "Collation &gt; Collation Manager". Duas colunas: à esquerda, combo
    /// "Database" real + botão "Atualizar", a collation do banco escolhido,
    /// a grade "Collations Encontradas" (agrupada por collation, com aviso
    /// "ATENÇÃO!" quando alguma diverge do padrão) e, logo abaixo, a grade
    /// "Detalhe de colunas" (todas as colunas do banco, com tipo e
    /// collation). À direita, "Ação" com o aviso fixo de OFFLINE, o combo
    /// de collation de destino, e 2 botões (o antigo 3º botão, "Mudar a
    /// collation das tabelas", foi removido a pedido do usuário — ver
    /// <remarks>).
    /// </summary>
    /// <remarks>
    /// Os 2 botões atuais:
    /// - "Mudar a collation do banco de dados" — script fornecido pelo
    ///   usuário (SET SINGLE_USER WITH ROLLBACK IMMEDIATE / ALTER DATABASE
    ///   ... COLLATE / SET MULTI_USER). <see cref="Modulo_Admin.AlterarCollationBancoAsync"/>
    ///   executa os 3 comandos na MESMA conexão (obrigatório depois do
    ///   SINGLE_USER) e devolve o banco para MULTI_USER num finally, mesmo
    ///   se o ALTER DATABASE COLLATE falhar — só afeta objetos novos, não
    ///   as colunas já existentes (para isso, o outro botão).
    /// - "Mudar a collation das colunas" — <see cref="Modulo_Admin.AlterarCollationColunasAsync"/>,
    ///   sem filtro de tabela (todas as colunas de texto do banco). Pedido
    ///   do usuário: "para as colunas se for preciso pode recriar o
    ///   índice" — o método detecta, por coluna, se ela participa de algum
    ///   índice (comum, chave primária ou restrição única) e, se sim, gera
    ///   o script de recriação, remove o índice, roda o ALTER COLUMN, e
    ///   recria o índice — reaproveitando
    ///   <see cref="Modulo_Indices.GerarScriptRecriacaoIndiceAsync"/>/
    ///   <see cref="Modulo_Indices.ExcluirIndiceAsync"/> (mesmo código já
    ///   usado antes de excluir um índice sem uso na tela de Fragmentação),
    ///   em vez de duplicar essa lógica.
    ///
    /// Existiu um 3º botão, "Mudar a collation das tabelas" (script de lote
    /// único via sp_executesql, sem recriação automática de índice — ver
    /// <see cref="Modulo_Admin.AlterarCollationTabelaAsync"/>, que continua
    /// implementado em Modulo_Admin mas sem nenhum botão chamando-o hoje).
    /// Removido a pedido explícito do usuário; o combo "Tabela" que só
    /// servia a esse botão foi removido junto.
    ///
    /// As 2 ações continuam SEMPRE oferecendo salvar um .txt de log
    /// (SaveFileDialog — <c>SalvarLogTxt</c>, helper local compartilhado).
    /// O painel "Situação" (<c>lstStatus</c>) preserva o resultado da
    /// última ação mesmo depois da atualização automática das grades que
    /// roda ao final de cada uma (<c>AtualizarCollationAsync(limparStatus: false)</c>).
    /// </remarks>
    private void MostrarPaginaCollation()
    {
        lblTituloPagina.Text = "Collation > Collation Manager";

        var cartao = CriarCartao();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Collation padrão da empresa — referência usada para detectar
        // divergência na grade "Collations found" (pedido de uma rodada
        // anterior: "o aviso ficará em vermelho caso a collation esteja
        // diferente de SQL_Latin1_General_CP1_CI_AI").
        const string CollationPadrao = "SQL_Latin1_General_CP1_CI_AI";

        // Collation "default" da PWI — pré-selecionada no combo de
        // collation de destino (à direita) quando presente na lista
        // retornada por ObterCollationsServidorAsync.
        const string CollationDefaultPwi = "Latin1_General_CI_AI";

        // Detalhe por coluna da última consulta (script "VISÃO
        // COMPARATIVA") — mantido aqui para reaproveitar sem consultar de
        // novo: agrupado na grade "Collations found" e usado para o resumo
        // no painel "Status".
        var colunasCollationAtual = new List<CollationColunaDto>();

        // ================= Coluna esquerda =================
        var pnlEsquerda = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 8, 12) };

        // Pedido do usuário: diminuir a grade "Collations found" (que só
        // mostra o resumo agrupado por collation) para abrir espaço, logo
        // abaixo dela, para a grade "Detalhe de colunas" — listagem crua de
        // TODAS as colunas (inclusive não-texto) com tabela/tipo/collation.
        var gridCollations = new DataGridView
        {
            Dock = DockStyle.Top,
            Height = 130,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        // "Name"/"Occurs" continuam como chave interna das colunas (usadas
        // em código, ex.: linha.Cells["Name"]) — só o texto do cabeçalho
        // (HeaderText) foi traduzido, pedido do usuário.
        gridCollations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Nome", Width = 220 });
        gridCollations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Occurs", HeaderText = "Ocorrências", Width = 80, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        // Sem linhas de exemplo — a grade só é preenchida por dados reais
        // (ver AtualizarCollationAsync), ao abrir a tela e a cada "Atualizar".

        // "Detalhe de colunas" — logo abaixo de "Collations found" (pedido
        // do usuário), a partir do script fornecido: TODAS as colunas de
        // TODAS as tabelas do banco, com tipo de dado e collation (ou
        // "N/A (Não-Texto)" quando a coluna não é de tipo texto). Preenchida
        // em AtualizarCollationAsync via Modulo_Admin.ObterDetalheColunasAsync.
        var pnlDetalheColunas = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        var lblDetalheColunasTitulo = new Label
        {
            Text = "Detalhe de colunas",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var gridDetalheColunas = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        gridDetalheColunas.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tabela", HeaderText = "Tabela", Width = 140 });
        gridDetalheColunas.Columns.Add(new DataGridViewTextBoxColumn { Name = "NomeDaColuna", HeaderText = "Coluna", Width = 130 });
        gridDetalheColunas.Columns.Add(new DataGridViewTextBoxColumn { Name = "TipoDeDado", HeaderText = "Tipo", Width = 90 });
        gridDetalheColunas.Columns.Add(new DataGridViewTextBoxColumn { Name = "CollationDaColuna", HeaderText = "Collation", Width = 160, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        // Sem linhas de exemplo — só dados reais (ver AtualizarCollationAsync).
        pnlDetalheColunas.Controls.Add(gridDetalheColunas);
        pnlDetalheColunas.Controls.Add(lblDetalheColunasTitulo);

        var flowEsquerda = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        // "Database" — combo com os bancos reais da instância (via
        // Modulo_Admin.ObterBancosDadosAsync, carregado em
        // CarregarBancosAsync) + botão "Atualizar" ao lado, que reconsulta
        // a collation do banco e das colunas sem precisar trocar de banco
        // (pedido do usuário: "colocar um botão de atualizar que quando for
        // clicado mostra novamente a informação da collation do banco de
        // dados das tabelas e colunas").
        var linhaBanco = new Panel { Width = 400, Height = 30, Margin = new Padding(0, 0, 0, 8) };
        var lblBancoRotulo = new Label
        {
            Text = "Database",
            AutoSize = true,
            Location = new Point(0, 7),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTextoSecundario
        };
        var cmbBancoAlvo = new ComboBox
        {
            Location = new Point(90, 3),
            Size = new Size(190, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        var btnAtualizar = CriarBotaoAcao("Atualizar");
        btnAtualizar.Location = new Point(286, 2);
        btnAtualizar.Size = new Size(108, 28);
        btnAtualizar.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        linhaBanco.Controls.Add(lblBancoRotulo);
        linhaBanco.Controls.Add(cmbBancoAlvo);
        linhaBanco.Controls.Add(btnAtualizar);
        flowEsquerda.Controls.Add(linhaBanco);

        var linhaCollationDb = new Panel { Width = 400, Height = 24, Margin = new Padding(0, 0, 0, 12) };
        var lblCollationDbRotulo = new Label
        {
            Text = "Collation (database)",
            AutoSize = true,
            Location = new Point(0, 4),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTextoSecundario
        };
        var lblCollationDbValor = new Label
        {
            Text = "—",
            AutoSize = true,
            Location = new Point(160, 4),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        linhaCollationDb.Controls.Add(lblCollationDbRotulo);
        linhaCollationDb.Controls.Add(lblCollationDbValor);
        flowEsquerda.Controls.Add(linhaCollationDb);

        var linhaCollationsFound = new Panel { Width = 400, Height = 24, Margin = new Padding(0, 0, 0, 6) };
        var lblCollationsFound = new Label
        {
            Text = "Collations Encontradas",
            AutoSize = true,
            Location = new Point(0, 4),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = CorTitulo
        };
        var lblWarning = new Label
        {
            Text = "ATENÇÃO!",
            AutoSize = true,
            Location = new Point(230, 3),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(196, 55, 55),
            Visible = false
        };
        linhaCollationsFound.Controls.Add(lblCollationsFound);
        linhaCollationsFound.Controls.Add(lblWarning);
        flowEsquerda.Controls.Add(linhaCollationsFound);

        pnlEsquerda.Controls.Add(pnlDetalheColunas);
        pnlEsquerda.Controls.Add(gridCollations);
        pnlEsquerda.Controls.Add(flowEsquerda);

        // Pedido do usuário (rodada anterior): "o aviso ficará em vermelho
        // caso a collation esteja diferente de SQL_Latin1_General_CP1_CI_AI"
        // — comparado linha a linha contra a grade "Collations found" (não
        // contra "Collation (database)", que é só a collation padrão do
        // banco em si). Antes rodava sobre 2 linhas de exemplo; agora roda
        // sobre o resultado real de AtualizarCollationAsync.
        void AtualizarAvisoCollationDivergente()
        {
            var divergente = gridCollations.Rows.Cast<DataGridViewRow>()
                .Any(linha => !string.Equals(Convert.ToString(linha.Cells["Name"].Value), CollationPadrao, StringComparison.Ordinal));
            lblWarning.Visible = divergente;
        }

        // ================= Coluna direita: "Ação" =================
        var pnlDireita = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 12, 16, 12) };

        var lstStatus = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9F, FontStyle.Italic)
        };
        lstStatus.Items.Add("Summary");
        lstStatus.SelectedIndex = 0;

        var flowDireita = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        // Pedido do usuário: renomear "Action" para "Ação".
        var lblAcaoTitulo = new Label
        {
            Text = "Ação",
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = CorTitulo,
            Margin = new Padding(0, 0, 0, 8)
        };
        flowDireita.Controls.Add(lblAcaoTitulo);

        // Pedido do usuário: "colocar na interface dessa tela em vermelho
        // um alerta que as ações executar deixa o banco Offline" — aviso
        // fixo (sempre visível, não só dentro da confirmação de cada
        // botão) em destaque acima dos botões de ação.
        var pnlAvisoOffline = new Panel
        {
            Width = 400,
            Height = 40,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = Color.FromArgb(252, 231, 231),
            BorderStyle = BorderStyle.FixedSingle
        };
        var lblAvisoOffline = new Label
        {
            Text = "Atenção: executar qualquer ação abaixo deixa o banco de dados OFFLINE para os usuários durante o processo.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(178, 34, 34)
        };
        pnlAvisoOffline.Controls.Add(lblAvisoOffline);
        flowDireita.Controls.Add(pnlAvisoOffline);

        // Pedido do usuário: "colocar uma Box para selecionar as collation
        // do SQL Server ... a que está marcada [como] Default PWI" — antes
        // era um texto fixo ("Collation (default-PWI): LATIN1_GENERAL_CI_AI"),
        // agora um combo com a lista real do servidor (ObterCollationsServidorAsync),
        // pré-selecionado na collation padrão da PWI. É a collation usada
        // pelos 3 botões de ação abaixo.
        var linhaCollationAlvo = new Panel { Width = 400, Height = 30, Margin = new Padding(0, 0, 0, 12) };
        var lblCollationAlvoRotulo = new Label
        {
            Text = "Collation (default-PWI)",
            AutoSize = true,
            Location = new Point(0, 7),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTextoSecundario
        };
        var cmbCollationAlvo = new ComboBox
        {
            Location = new Point(210, 3),
            Size = new Size(185, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        linhaCollationAlvo.Controls.Add(lblCollationAlvoRotulo);
        linhaCollationAlvo.Controls.Add(cmbCollationAlvo);
        flowDireita.Controls.Add(linhaCollationAlvo);

        // Pedido do usuário (rodada anterior): trocar o combo + botão único
        // "Change collations" por 3 botões de ação separados (banco/
        // tabelas/colunas — granularidades diferentes de uma troca de
        // collation).
        Button CriarBotaoAcaoCollation(string texto)
        {
            var botao = CriarBotaoAcao(texto);
            botao.Width = 340;
            botao.Height = 36;
            botao.TextAlign = ContentAlignment.MiddleCenter;
            botao.Margin = new Padding(0, 0, 0, 8);
            return botao;
        }

        // Pedido do usuário: remover o botão "Mudar a collation das
        // tabelas" (a ação de tabela por tabela ficou só com "Mudar a
        // collation das colunas", que já cobre todas as colunas do banco
        // com recriação automática de índice).
        var btnMudarBanco = CriarBotaoAcaoCollation("Mudar a collation do banco de dados");
        var btnMudarColunas = CriarBotaoAcaoCollation("Mudar a collation das colunas");
        // Pedido do usuário (funcionário testou e reportou): "Mudar a
        // collation das colunas" altera TODAS as colunas de texto do banco
        // de uma vez — botão à parte que altera só a coluna selecionada na
        // grade "Detalhe de colunas" (à esquerda).
        var btnMudarColunaSelecionada = CriarBotaoAcaoCollation("Mudar a collation da coluna selecionada");
        flowDireita.Controls.Add(btnMudarBanco);
        flowDireita.Controls.Add(btnMudarColunas);
        flowDireita.Controls.Add(btnMudarColunaSelecionada);

        var lblStatusTitulo = new Label
        {
            Text = "Situação",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = CorTitulo,
            Margin = new Padding(0, 0, 0, 4)
        };
        flowDireita.Controls.Add(lblStatusTitulo);

        pnlDireita.Controls.Add(lstStatus);
        pnlDireita.Controls.Add(flowDireita);

        // Salva o conteúdo informado num .txt escolhido pelo usuário — helper
        // compartilhado pelos 3 botões de ação (pedido original do usuário:
        // "gerar um txt de log o resultado"), mesmo padrão SaveFileDialog +
        // File.WriteAllText já usado em outras telas do app (ex.: script de
        // recriação de índice antes de excluir).
        void SalvarLogTxt(string sugestaoNomeArquivo, string conteudo)
        {
            using var dialogoSalvar = new SaveFileDialog
            {
                Title = "Salvar log da alteração de collation",
                Filter = "Arquivo de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*",
                FileName = sugestaoNomeArquivo
            };
            if (dialogoSalvar.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                File.WriteAllText(dialogoSalvar.FileName, conteudo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Não foi possível salvar o log.\n\n{ex.Message}", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Monta o texto do .txt de log para "Mudar a collation das
        // colunas" — inclui, para cada coluna alterada, quais índices
        // precisaram ser removidos/recriados automaticamente (ver
        // Modulo_Admin.AlterarCollationColunasAsync).
        string MontarLogColunas(string titulo, string banco, string novaCollation, List<ResultadoAlteracaoColunaDto> resultados)
        {
            var sucesso = resultados.Count(r => r.Sucesso);
            var falha = resultados.Count(r => !r.Sucesso);

            var log = new System.Text.StringBuilder();
            log.AppendLine($"SQL Rocket — {titulo}");
            log.AppendLine($"Banco de dados: {banco}");
            log.AppendLine($"Collation de destino: {novaCollation}");
            log.AppendLine($"Executado em: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine($"Total de colunas: {resultados.Count} | Sucesso: {sucesso} | Falha: {falha}");
            log.AppendLine();
            log.AppendLine("Colunas alteradas com sucesso:");
            foreach (var item in resultados.Where(r => r.Sucesso))
            {
                var indices = string.IsNullOrEmpty(item.IndicesRecriados)
                    ? string.Empty
                    : $" (índice(s) removido(s) e recriado(s): {item.IndicesRecriados})";
                log.AppendLine($"  [{item.Esquema}].[{item.Tabela}].[{item.Coluna}]{indices}");
            }
            log.AppendLine();
            log.AppendLine("Colunas que NÃO puderam ser alteradas:");
            foreach (var item in resultados.Where(r => !r.Sucesso))
            {
                log.AppendLine($"  [{item.Esquema}].[{item.Tabela}].[{item.Coluna}] — {item.MensagemErro}");
            }

            return log.ToString();
        }

        // "Atualizar" (e a troca do combo "Database"): consulta a
        // collation do banco escolhido (script 1 do usuário) e o
        // comparativo por coluna (script 2/"VISÃO COMPARATIVA"), agrupando
        // o resultado por collation para a grade "Collations found".
        // "limparStatus": true (padrão) reinicia o painel "Status" — usado
        // pelo botão "Atualizar" e pela troca de banco. Depois de uma das 3
        // ações de "Ação" (Mudar collation do banco/tabelas/colunas), a
        // atualização automática que roda ao final passa "false" para NÃO
        // apagar o resultado que acabou de ser mostrado (pedido do usuário:
        // "deixar o status com a última informação realizada, não apagar
        // essa informação") — as novas linhas só são adicionadas depois.
        async Task AtualizarCollationAsync(bool limparStatus = true)
        {
            var banco = cmbBancoAlvo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(banco))
            {
                MessageBox.Show(this, "Selecione um banco de dados antes de atualizar.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnAtualizar.Enabled = false;
            if (limparStatus)
            {
                lstStatus.Items.Clear();
                lstStatus.Items.Add("Summary");
            }
            lstStatus.Items.Add($"Consultando collation de \"{banco}\"...");

            try
            {
                var infoBanco = await _moduloAdmin.ObterCollationDatabaseAsync(banco);
                lblCollationDbValor.Text = string.IsNullOrEmpty(infoBanco.CollationBanco) ? "N/D" : infoBanco.CollationBanco;

                colunasCollationAtual.Clear();
                colunasCollationAtual.AddRange(await _moduloAdmin.ObterComparativoCollationColunasAsync(banco));

                gridCollations.Rows.Clear();
                var agrupado = colunasCollationAtual
                    .GroupBy(c => string.IsNullOrEmpty(c.CollationDaColuna) ? "(sem collation)" : c.CollationDaColuna)
                    .Select(g => new { Nome = g.Key, Ocorrencias = g.Count() })
                    .OrderByDescending(g => g.Ocorrencias);
                foreach (var grupo in agrupado)
                {
                    gridCollations.Rows.Add(grupo.Nome, grupo.Ocorrencias);
                }

                AtualizarAvisoCollationDivergente();

                var divergentes = colunasCollationAtual.Count(c => c.StatusCollation == "DIVERGENTE");
                lstStatus.Items.Add($"{colunasCollationAtual.Count} coluna(s) de texto verificada(s), {divergentes} divergente(s) da collation do banco \"{banco}\".");

                // "Detalhe de colunas" — listagem crua de TODAS as colunas
                // (inclusive não-texto) do banco, logo abaixo de "Collations
                // found" (pedido do usuário).
                gridDetalheColunas.Rows.Clear();
                var detalheColunas = await _moduloAdmin.ObterDetalheColunasAsync(banco);
                foreach (var linha in detalheColunas)
                {
                    gridDetalheColunas.Rows.Add(linha.Tabela, linha.NomeDaColuna, linha.TipoDeDado, linha.CollationDaColuna);
                }

            }
            catch (Exception ex)
            {
                lstStatus.Items.Add(ObterMensagemAmigavel(ex));
            }
            finally
            {
                btnAtualizar.Enabled = true;
            }
        }

        // "Mudar a collation das colunas" — único dos 3 botões com script
        // fornecido pelo usuário (gerador de "ALTER TABLE ... ALTER COLUMN
        // ... COLLATE"). Gera e executa um ALTER por coluna de texto do
        // banco, continuando mesmo se uma coluna falhar, e sempre oferece
        // salvar um .txt com o resultado (pedido do usuário: "gerar um txt
        // de log o resultado e quais as colunas não pode ser alterado").
        async Task MudarCollationColunasAsync()
        {
            var banco = cmbBancoAlvo.SelectedItem as string;
            var novaCollation = cmbCollationAlvo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(banco) || string.IsNullOrWhiteSpace(novaCollation))
            {
                MessageBox.Show(this, "Selecione um banco de dados e uma collation de destino antes de continuar.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var resposta = MessageBox.Show(this,
                "Mudar a collation das colunas\n\n" +
                $"Esta operação deixará o banco de dados \"{banco}\" OFFLINE para os usuários enquanto for executada " +
                $"e vai tentar alterar a collation de TODAS as colunas de texto do banco para \"{novaCollation}\" " +
                "(removendo e recriando automaticamente os índices que dependerem de alguma delas).\n\n" +
                "Ao final, será gerado um arquivo de log (.txt) com o resultado da operação e a lista de colunas que não puderam ser alteradas.\n\n" +
                "Deseja continuar?",
                "Confirmar alteração de collation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (resposta != DialogResult.Yes) return;

            btnMudarColunas.Enabled = false;
            // Alteração de collation é destrutiva (deixa o banco indisponível
            // e recria índices): sinaliza o andamento para que "Sair"/fechar
            // a janela peçam confirmação (ver _operacoesDestrutivasEmAndamento).
            _operacoesDestrutivasEmAndamento++;
            lstStatus.Items.Clear();
            lstStatus.Items.Add("Summary");
            lstStatus.Items.Add($"Alterando collation das colunas de \"{banco}\" para \"{novaCollation}\"...");

            List<ResultadoAlteracaoColunaDto> resultados;
            try
            {
                resultados = await _moduloAdmin.AlterarCollationColunasAsync(banco, novaCollation,
                    mensagens: new Progress<string>(msg => lstStatus.Items.Add(msg)));
            }
            catch (Exception ex)
            {
                lstStatus.Items.Add(ObterMensagemAmigavel(ex));
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnMudarColunas.Enabled = true;
                _operacoesDestrutivasEmAndamento--;
                return;
            }

            btnMudarColunas.Enabled = true;
            _operacoesDestrutivasEmAndamento--;

            var sucesso = resultados.Count(r => r.Sucesso);
            var falha = resultados.Count(r => !r.Sucesso);
            lstStatus.Items.Add($"Concluído: {sucesso} coluna(s) alterada(s), {falha} não puderam ser alterada(s).");

            SalvarLogTxt(
                $"CollationColunas_{banco}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                MontarLogColunas("Alteração de collation das colunas", banco, novaCollation, resultados));

            MessageBox.Show(this,
                $"{sucesso} coluna(s) alterada(s) com sucesso.\n{falha} coluna(s) não puderam ser alteradas (detalhes no log/Status).",
                "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);

            _ = AtualizarCollationAsync(limparStatus: false);
        }

        // "Mudar a collation da coluna selecionada" — pedido do usuário
        // (funcionário testou e reportou que "Mudar a collation das
        // colunas" muda TODAS as colunas do banco de uma vez, e ele
        // precisava de uma opção que mudasse só uma). Reaproveita
        // Modulo_Admin.AlterarCollationColunasAsync (mesma recriação
        // automática de índice quando necessário), passando tabela+coluna
        // da linha selecionada na grade "Detalhe de colunas" — só essa
        // coluna é alterada, as demais do banco ficam intactas.
        async Task MudarCollationColunaSelecionadaAsync()
        {
            var banco = cmbBancoAlvo.SelectedItem as string;
            var novaCollation = cmbCollationAlvo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(banco) || string.IsNullOrWhiteSpace(novaCollation))
            {
                MessageBox.Show(this, "Selecione um banco de dados e uma collation de destino antes de continuar.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (gridDetalheColunas.CurrentRow is null)
            {
                MessageBox.Show(this,
                    "Selecione uma coluna na grade \"Detalhe de colunas\" (à esquerda) antes de continuar.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var tabela = Convert.ToString(gridDetalheColunas.CurrentRow.Cells["Tabela"].Value);
            var coluna = Convert.ToString(gridDetalheColunas.CurrentRow.Cells["NomeDaColuna"].Value);
            var tipo = Convert.ToString(gridDetalheColunas.CurrentRow.Cells["TipoDeDado"].Value);
            if (string.IsNullOrWhiteSpace(tabela) || string.IsNullOrWhiteSpace(coluna))
            {
                MessageBox.Show(this, "Não foi possível identificar a tabela/coluna selecionada.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.Equals(Convert.ToString(gridDetalheColunas.CurrentRow.Cells["CollationDaColuna"].Value), "N/A (Não-Texto)", StringComparison.Ordinal))
            {
                MessageBox.Show(this,
                    $"A coluna [{tabela}].[{coluna}] não é de tipo texto (tipo \"{tipo}\") e não tem collation para alterar.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var resposta = MessageBox.Show(this,
                "Mudar a collation da coluna selecionada\n\n" +
                $"Esta operação altera só a coluna [{tabela}].[{coluna}] do banco \"{banco}\" para a collation \"{novaCollation}\" " +
                "(removendo e recriando automaticamente o(s) índice(s) que dependerem dela, se houver) — as demais " +
                "colunas do banco NÃO são afetadas. Só a tabela dessa coluna fica bloqueada, e só durante a execução " +
                "do ALTER COLUMN.\n\n" +
                "Ao final, será gerado um arquivo de log (.txt) com o resultado.\n\n" +
                "Deseja continuar?",
                "Confirmar alteração de collation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (resposta != DialogResult.Yes) return;

            btnMudarColunaSelecionada.Enabled = false;
            // Alteração de collation é destrutiva (deixa o banco indisponível
            // e recria índices): sinaliza o andamento para que "Sair"/fechar
            // a janela peçam confirmação (ver _operacoesDestrutivasEmAndamento).
            _operacoesDestrutivasEmAndamento++;
            lstStatus.Items.Clear();
            lstStatus.Items.Add("Summary");
            lstStatus.Items.Add($"Alterando collation de [{tabela}].[{coluna}] para \"{novaCollation}\"...");

            List<ResultadoAlteracaoColunaDto> resultados;
            try
            {
                resultados = await _moduloAdmin.AlterarCollationColunasAsync(banco, novaCollation, nomeTabela: tabela, nomeColuna: coluna,
                    mensagens: new Progress<string>(msg => lstStatus.Items.Add(msg)));
            }
            catch (Exception ex)
            {
                lstStatus.Items.Add(ObterMensagemAmigavel(ex));
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnMudarColunaSelecionada.Enabled = true;
                _operacoesDestrutivasEmAndamento--;
                return;
            }

            btnMudarColunaSelecionada.Enabled = true;
            _operacoesDestrutivasEmAndamento--;

            if (resultados.Count == 0)
            {
                lstStatus.Items.Add($"[{tabela}].[{coluna}] não foi encontrada como coluna de texto — nada foi alterado.");
                MessageBox.Show(this,
                    $"Não foi encontrada a coluna [{tabela}].[{coluna}] como coluna de texto — nada foi alterado.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _ = AtualizarCollationAsync(limparStatus: false);
                return;
            }

            var sucesso = resultados.Count(r => r.Sucesso);
            var falha = resultados.Count(r => !r.Sucesso);
            lstStatus.Items.Add(sucesso > 0
                ? $"Concluído: coluna [{tabela}].[{coluna}] alterada com sucesso."
                : $"Concluído: coluna [{tabela}].[{coluna}] NÃO pôde ser alterada.");

            SalvarLogTxt(
                $"CollationColuna_{banco}_{tabela}_{coluna}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                MontarLogColunas("Alteração de collation da coluna selecionada", banco, novaCollation, resultados));

            MessageBox.Show(this,
                falha == 0
                    ? $"Coluna [{tabela}].[{coluna}] alterada com sucesso para \"{novaCollation}\"."
                    : $"Não foi possível alterar a coluna [{tabela}].[{coluna}] (detalhes no log/Status).",
                "SQL Rocket", MessageBoxButtons.OK, falha == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);

            _ = AtualizarCollationAsync(limparStatus: false);
        }

        // "Mudar a collation do banco de dados" — script fornecido pelo
        // usuário (SET SINGLE_USER WITH ROLLBACK IMMEDIATE / ALTER DATABASE
        // ... COLLATE / SET MULTI_USER). Modulo_Admin.AlterarCollationBancoAsync
        // devolve o banco para MULTI_USER mesmo se a alteração falhar — ver
        // <remarks> do método.
        async Task MudarCollationBancoAsync()
        {
            var banco = cmbBancoAlvo.SelectedItem as string;
            var novaCollation = cmbCollationAlvo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(banco) || string.IsNullOrWhiteSpace(novaCollation))
            {
                MessageBox.Show(this, "Selecione um banco de dados e uma collation de destino antes de continuar.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var resposta = MessageBox.Show(this,
                "Mudar a collation do banco de dados\n\n" +
                $"Esta operação coloca o banco \"{banco}\" em modo SINGLE_USER (desconectando os demais usuários — " +
                "o \"OFFLINE\" do aviso acima), altera a collation do banco e devolve para MULTI_USER ao final, " +
                $"mesmo se a alteração falhar, para \"{novaCollation}\".\n\n" +
                "Isso NÃO altera a collation das colunas já existentes (só afeta objetos novos criados sem collation " +
                "explícita) — use \"Mudar a collation das colunas\" para isso.\n\n" +
                "Deseja continuar?",
                "Confirmar alteração de collation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (resposta != DialogResult.Yes) return;

            btnMudarBanco.Enabled = false;
            // Alteração de collation é destrutiva (coloca o banco em
            // SINGLE_USER, desconectando os demais usuários): sinaliza o
            // andamento para que "Sair"/fechar a janela peçam confirmação
            // (ver _operacoesDestrutivasEmAndamento).
            _operacoesDestrutivasEmAndamento++;
            lstStatus.Items.Clear();
            lstStatus.Items.Add("Summary");

            var log = new System.Text.StringBuilder();
            log.AppendLine("SQL Rocket — Alteração de collation do banco de dados");
            log.AppendLine($"Banco de dados: {banco}");
            log.AppendLine($"Collation de destino: {novaCollation}");
            log.AppendLine($"Executado em: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine();

            try
            {
                await _moduloAdmin.AlterarCollationBancoAsync(banco, novaCollation, new Progress<string>(msg =>
                {
                    lstStatus.Items.Add(msg);
                    log.AppendLine(msg);
                }));
                log.AppendLine();
                log.AppendLine("Resultado: sucesso.");
                MessageBox.Show(this, $"Collation do banco \"{banco}\" alterada para \"{novaCollation}\" com sucesso.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lstStatus.Items.Add(ObterMensagemAmigavel(ex));
                log.AppendLine();
                log.AppendLine($"Resultado: falha — {ex.Message}");
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _operacoesDestrutivasEmAndamento--;
                btnMudarBanco.Enabled = true;
            }

            SalvarLogTxt($"CollationBanco_{banco}_{DateTime.Now:yyyyMMdd_HHmmss}.txt", log.ToString());

            _ = AtualizarCollationAsync(limparStatus: false);
        }

        btnAtualizar.Click += (_, _) => _ = AtualizarCollationAsync();
        btnMudarBanco.Click += (_, _) => _ = MudarCollationBancoAsync();
        btnMudarColunas.Click += (_, _) => _ = MudarCollationColunasAsync();
        btnMudarColunaSelecionada.Click += (_, _) => _ = MudarCollationColunaSelecionadaAsync();

        // Carrega o combo "Database" com os bancos reais da instância
        // (pedido do usuário) e, assim que carregado, já consulta a
        // collation do primeiro banco — troca de banco (SelectedIndexChanged)
        // também reconsulta automaticamente, além do botão "Atualizar".
        async Task CarregarBancosAsync()
        {
            try
            {
                var bancos = await _moduloAdmin.ObterBancosDadosAsync();
                cmbBancoAlvo.Items.Clear();
                cmbBancoAlvo.Items.AddRange(bancos.ToArray());
            }
            catch (Exception ex)
            {
                lstStatus.Items.Clear();
                lstStatus.Items.Add("Summary");
                lstStatus.Items.Add(ObterMensagemAmigavel(ex));
                return;
            }

            if (cmbBancoAlvo.Items.Count > 0)
            {
                cmbBancoAlvo.SelectedIndex = 0;
            }

            cmbBancoAlvo.SelectedIndexChanged += (_, _) => _ = AtualizarCollationAsync();

            if (cmbBancoAlvo.Items.Count > 0)
            {
                await AtualizarCollationAsync();
            }
        }

        // Carrega o combo de collation de destino com a lista real do
        // servidor, pré-selecionando a collation padrão da PWI.
        async Task CarregarCollationsServidorAsync()
        {
            try
            {
                var collations = await _moduloAdmin.ObterCollationsServidorAsync();
                cmbCollationAlvo.Items.Clear();
                cmbCollationAlvo.Items.AddRange(collations.ToArray());

                var indicePadrao = -1;
                for (var i = 0; i < cmbCollationAlvo.Items.Count; i++)
                {
                    if (string.Equals(cmbCollationAlvo.Items[i] as string, CollationDefaultPwi, StringComparison.OrdinalIgnoreCase))
                    {
                        indicePadrao = i;
                        break;
                    }
                }
                cmbCollationAlvo.SelectedIndex = indicePadrao >= 0 ? indicePadrao : (cmbCollationAlvo.Items.Count > 0 ? 0 : -1);
            }
            catch (Exception ex)
            {
                lstStatus.Items.Add(ObterMensagemAmigavel(ex));
            }
        }

        layout.Controls.Add(pnlEsquerda, 0, 0);
        layout.Controls.Add(pnlDireita, 1, 0);

        cartao.Controls.Add(layout);

        DefinirConteudo(cartao);

        _ = CarregarBancosAsync();
        _ = CarregarCollationsServidorAsync();
    }

    /// <summary>
    /// Página "Manutenção &gt; Limpeza de Arquivos" ("limpeza de banco",
    /// pedido do usuário): shrink do log (LDF) e do banco completo, grade
    /// com as 25 maiores tabelas, seletor de tabela com o primeiro/último
    /// registro dela, e duas limpezas (DELETE em lote) — por data+hora
    /// (coluna de data escolhida manualmente, dia/mês/ano/hora escolhidos
    /// pelo usuário) e por chave primária (a chave REAL da tabela, lida dos
    /// metadados do SQL Server, nunca um nome de coluna adivinhado). Ver
    /// <see cref="Modulo_Manutencao"/> para as 3 decisões de design
    /// confirmadas com o usuário antes desta implementação.
    ///
    /// Diferença de layout em relação ao pedido original: o combo de
    /// seleção de tabela fica ABAIXO da grade "Top 25" (não ao lado) — só
    /// para reaproveitar o mesmo padrão de empilhamento vertical usado em
    /// todas as outras páginas desta tela (ex.: Backup Full, Sobre), em vez
    /// de introduzir um layout de 2 colunas só para esta tela.
    /// </summary>
    private void MostrarPaginaLimpezaArquivos()
    {
        lblTituloPagina.Text = "Manutenção > Limpeza de Arquivos";

        var cartao = CriarCartao();

        var fonteRotulo = new Font("Segoe UI", 9F, FontStyle.Bold);
        var fonteSecao = new Font("Segoe UI", 12F, FontStyle.Bold);
        var fonteAviso = new Font("Segoe UI", 8.5F, FontStyle.Italic);

        // ---- Banco de dados ----
        var lblBanco = new Label { Text = "Banco de Dados", AutoSize = true, Location = new Point(0, 0), Font = fonteRotulo, ForeColor = CorTitulo };
        var cmbBanco = new ComboBox
        {
            Location = new Point(0, 22),
            Size = new Size(280, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            Sorted = true,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };
        var btnCarregarTudo = CriarBotaoAcao("Carregar");
        btnCarregarTudo.Location = new Point(300, 21);
        btnCarregarTudo.Size = new Size(140, 28);

        // ---- Shrink ----
        var lblShrinkTitulo = new Label { Text = "Shrink", AutoSize = true, Location = new Point(0, 66), Font = fonteSecao, ForeColor = CorTitulo };
        var btnShrinkLog = CriarBotaoAcao("Shrink do Log (LDF)");
        btnShrinkLog.Location = new Point(0, 96);
        btnShrinkLog.Size = new Size(220, 34);
        var btnShrinkBanco = CriarBotaoAcao("Shrink do Banco Completo");
        btnShrinkBanco.Location = new Point(236, 96);
        btnShrinkBanco.Size = new Size(240, 34);
        var lblStatusShrink = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 138),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Escolha o banco de dados e clique em \"Carregar\" antes de executar o shrink."
        };

        // ---- Top 25 maiores tabelas ----
        var lblTop25Titulo = new Label { Text = "Top 25 maiores tabelas", AutoSize = true, Location = new Point(0, 178), Font = fonteSecao, ForeColor = CorTitulo };
        var gridTop25 = new DataGridView
        {
            Location = new Point(0, 206),
            Size = new Size(1000, 220),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        // ---- Tabela selecionada ----
        var lblTabelaSelecionadaTitulo = new Label { Text = "Tabela selecionada", AutoSize = true, Location = new Point(0, 446), Font = fonteSecao, ForeColor = CorTitulo };
        var lblTabela = new Label { Text = "Tabela", AutoSize = true, Location = new Point(0, 480), Font = fonteRotulo, ForeColor = CorTitulo };
        var cmbTabelaSelecionada = new ComboBox
        {
            Location = new Point(0, 502),
            Size = new Size(320, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        var lblChavePrimaria = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            Location = new Point(340, 506),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Selecione uma tabela para ver a chave primária."
        };

        // ---- Primeiro e último registro ----
        var lblPrimeiroUltimoTitulo = new Label { Text = "Primeiro e último registro", AutoSize = true, Location = new Point(0, 546), Font = fonteSecao, ForeColor = CorTitulo };
        var gridPrimeiroUltimo = new DataGridView
        {
            Location = new Point(0, 574),
            Size = new Size(1000, 110),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        // ---- Limpeza por Data ----
        var lblLimpezaDataTitulo = new Label { Text = "Limpeza por Data", AutoSize = true, Location = new Point(0, 704), Font = fonteSecao, ForeColor = CorTitulo };
        var lblColunaData = new Label { Text = "Coluna de data", AutoSize = true, Location = new Point(0, 738), Font = fonteRotulo, ForeColor = CorTitulo };
        var cmbColunaData = new ComboBox
        {
            Location = new Point(0, 760),
            Size = new Size(240, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        var lblDia = new Label { Text = "Dia", AutoSize = true, Location = new Point(256, 738), Font = fonteRotulo, ForeColor = CorTitulo };
        var numDia = new NumericUpDown { Location = new Point(256, 760), Size = new Size(56, 26), Minimum = 1, Maximum = 31, Value = DateTime.Today.Day };
        var lblMes = new Label { Text = "Mês", AutoSize = true, Location = new Point(320, 738), Font = fonteRotulo, ForeColor = CorTitulo };
        var numMes = new NumericUpDown { Location = new Point(320, 760), Size = new Size(56, 26), Minimum = 1, Maximum = 12, Value = DateTime.Today.Month };
        var lblAno = new Label { Text = "Ano", AutoSize = true, Location = new Point(384, 738), Font = fonteRotulo, ForeColor = CorTitulo };
        var numAno = new NumericUpDown { Location = new Point(384, 760), Size = new Size(76, 26), Minimum = 1900, Maximum = 2100, Value = DateTime.Today.Year };
        // Pedido do usuário: além de dia/mês/ano, escolher a HORA (e, depois,
        // o MINUTO) do corte — padrão 23:59 para reproduzir o comportamento
        // antigo (cobrir o dia inteiro) quando o usuário não mexer nos campos.
        var lblHora = new Label { Text = "Hora", AutoSize = true, Location = new Point(470, 738), Font = fonteRotulo, ForeColor = CorTitulo };
        var numHora = new NumericUpDown { Location = new Point(470, 760), Size = new Size(56, 26), Minimum = 0, Maximum = 23, Value = 23 };
        var lblMinuto = new Label { Text = "Minuto", AutoSize = true, Location = new Point(534, 738), Font = fonteRotulo, ForeColor = CorTitulo };
        var numMinuto = new NumericUpDown { Location = new Point(534, 760), Size = new Size(56, 26), Minimum = 0, Maximum = 59, Value = 59 };
        var btnExcluirPorData = CriarBotaoAcao("Excluir registros ≤ data");
        btnExcluirPorData.BackColor = Color.FromArgb(196, 55, 55);
        btnExcluirPorData.Location = new Point(0, 796);
        btnExcluirPorData.Size = new Size(260, 30);
        // Pedido do usuário: um jeito de testar a condição de "≤ data" antes
        // de rodar a limpeza em lote — apaga só 1 registro, cor diferente
        // (laranja) do botão em lote (vermelho) para deixar claro que é uma
        // ação de escopo menor, mesmo sendo igualmente irreversível.
        var btnExcluirUmPorData = CriarBotaoAcao("Excluir 1 registro");
        btnExcluirUmPorData.BackColor = Color.FromArgb(191, 143, 0);
        btnExcluirUmPorData.Location = new Point(280, 796);
        btnExcluirUmPorData.Size = new Size(220, 30);
        var lblStatusData = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 834),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Selecione uma tabela com colunas de data/hora para habilitar esta limpeza."
        };

        // ---- Limpeza por ID (chave primária) ----
        var lblLimpezaIdTitulo = new Label { Text = "Limpeza por ID (chave primária)", AutoSize = true, Location = new Point(0, 874), Font = fonteSecao, ForeColor = CorTitulo };
        var lblChaveDetectadaLimpeza = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 908),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Selecione uma tabela com chave primária simples e numérica para habilitar esta limpeza."
        };
        var lblIdLimite = new Label { Text = "ID limite", AutoSize = true, Location = new Point(0, 934), Font = fonteRotulo, ForeColor = CorTitulo };
        var numIdLimite = new NumericUpDown { Location = new Point(0, 956), Size = new Size(200, 26), Minimum = 0, Maximum = 999999999999, DecimalPlaces = 0 };
        var btnExcluirPorId = CriarBotaoAcao("Excluir registros ≤ ID");
        btnExcluirPorId.BackColor = Color.FromArgb(196, 55, 55);
        btnExcluirPorId.Location = new Point(220, 955);
        btnExcluirPorId.Size = new Size(260, 30);
        // Mesma ideia de btnExcluirUmPorData, para a limpeza por ID.
        var btnExcluirUmPorId = CriarBotaoAcao("Excluir 1 registro");
        btnExcluirUmPorId.BackColor = Color.FromArgb(191, 143, 0);
        btnExcluirUmPorId.Location = new Point(500, 955);
        btnExcluirUmPorId.Size = new Size(220, 30);

        cartao.Controls.AddRange(new Control[]
        {
            lblBanco, cmbBanco, btnCarregarTudo,
            lblShrinkTitulo, btnShrinkLog, btnShrinkBanco, lblStatusShrink,
            lblTop25Titulo, gridTop25,
            lblTabelaSelecionadaTitulo, lblTabela, cmbTabelaSelecionada, lblChavePrimaria,
            lblPrimeiroUltimoTitulo, gridPrimeiroUltimo,
            lblLimpezaDataTitulo, lblColunaData, cmbColunaData, lblDia, numDia, lblMes, numMes, lblAno, numAno, lblHora, numHora, lblMinuto, numMinuto, btnExcluirPorData, btnExcluirUmPorData, lblStatusData,
            lblLimpezaIdTitulo, lblChaveDetectadaLimpeza, lblIdLimite, numIdLimite, btnExcluirPorId, btnExcluirUmPorId
        });

        DefinirConteudo(cartao);

        string? BancoAtual() => string.IsNullOrWhiteSpace(cmbBanco.Text) ? null : cmbBanco.Text.Trim();

        // Guarda a única coluna da chave primária da tabela atualmente
        // selecionada (null = sem chave primária simples utilizável) —
        // preenchida em AtualizarTabelaSelecionadaAsync, consultada pelos
        // handlers de "Excluir registros ≤ ID" e de recarregar
        // primeiro/último depois de uma exclusão.
        ChavePrimariaColunaDto? chavePrimariaAtual = null;

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR. Só
        // entra em consultas de LEITURA (top 25 maiores tabelas, chave
        // primária, colunas de data, primeiro/último registro). As operações
        // DESTRUTIVAS desta tela (shrink de log/banco e as exclusões de
        // registros, em lote ou de 1 registro) continuam SEM token de página,
        // de propósito: abortá-las no meio deixaria o trabalho pela metade no
        // servidor — elas já têm o Cancelar próprio de
        // ExecutarManutencaoIndiceAsync quando é o caso.
        var tokenPagina = TokenPaginaAtiva;

        // Canal de erro do bloco "Banco de dados/Shrink" desta tela — é o
        // label que fica logo abaixo do combo "Banco de Dados".
        void ReportarFalhaCombo(string mensagem)
        {
            if (tokenPagina.IsCancellationRequested || lblStatusShrink.IsDisposed)
            {
                return;
            }

            lblStatusShrink.ForeColor = Color.FromArgb(196, 55, 55);
            lblStatusShrink.Text = mensagem;
        }

        async Task AtualizarTabelaSelecionadaAsync()
        {
            var tabela = cmbTabelaSelecionada.SelectedItem as string;

            gridPrimeiroUltimo.DataSource = null;
            chavePrimariaAtual = null;
            cmbColunaData.Items.Clear();
            btnExcluirPorData.Enabled = false;
            btnExcluirPorId.Enabled = false;

            if (string.IsNullOrWhiteSpace(tabela))
            {
                lblChavePrimaria.Text = "Selecione uma tabela para ver a chave primária.";
                lblStatusData.Text = "Selecione uma tabela com colunas de data/hora para habilitar esta limpeza.";
                lblChaveDetectadaLimpeza.Text = "Selecione uma tabela com chave primária simples e numérica para habilitar esta limpeza.";
                return;
            }

            var banco = BancoAtual();

            try
            {
                var chavePrimaria = await _moduloManutencao.ObterChavePrimariaAsync(tabela, banco, tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblChavePrimaria.IsDisposed)
                {
                    return;
                }

                if (chavePrimaria.Count == 1)
                {
                    chavePrimariaAtual = chavePrimaria[0];
                    lblChavePrimaria.Text = $"Chave primária: {chavePrimariaAtual.NomeColuna} ({chavePrimariaAtual.TipoDado}).";

                    var primeiroUltimo = await _moduloManutencao.ObterPrimeiroUltimoRegistroAsync(tabela, chavePrimariaAtual.NomeColuna, banco, tokenPagina);
                    if (tokenPagina.IsCancellationRequested || gridPrimeiroUltimo.IsDisposed)
                    {
                        return;
                    }

                    gridPrimeiroUltimo.DataSource = primeiroUltimo;

                    if (Modulo_Manutencao.EhTipoNumericoInteiro(chavePrimariaAtual.TipoDado))
                    {
                        lblChaveDetectadaLimpeza.Text = $"Chave primária \"{chavePrimariaAtual.NomeColuna}\" ({chavePrimariaAtual.TipoDado}) pronta para uso.";
                        lblChaveDetectadaLimpeza.ForeColor = CorTextoSecundario;
                        btnExcluirPorId.Enabled = true;
                    }
                    else
                    {
                        lblChaveDetectadaLimpeza.Text = $"Chave primária \"{chavePrimariaAtual.NomeColuna}\" não é numérica ({chavePrimariaAtual.TipoDado}) — limpeza por ID indisponível.";
                        lblChaveDetectadaLimpeza.ForeColor = Color.FromArgb(140, 80, 0);
                    }
                }
                else
                {
                    lblChavePrimaria.Text = chavePrimaria.Count == 0
                        ? "Esta tabela não tem chave primária — \"primeiro/último registro\" indisponível."
                        : "Esta tabela tem chave primária composta (mais de uma coluna) — \"primeiro/último registro\" indisponível.";
                    lblChaveDetectadaLimpeza.Text = chavePrimaria.Count == 0
                        ? "Esta tabela não tem chave primária — limpeza por ID indisponível."
                        : "Esta tabela tem chave primária composta — limpeza por ID indisponível.";
                    lblChaveDetectadaLimpeza.ForeColor = Color.FromArgb(140, 80, 0);
                }

                var colunasData = await _moduloManutencao.ObterColunasDataAsync(tabela, banco, tokenPagina);
                if (tokenPagina.IsCancellationRequested || cmbColunaData.IsDisposed)
                {
                    return;
                }

                cmbColunaData.Items.AddRange(colunasData.ToArray());
                if (colunasData.Count > 0)
                {
                    cmbColunaData.SelectedIndex = 0;
                    btnExcluirPorData.Enabled = true;
                    lblStatusData.Text = $"{colunasData.Count} coluna(s) de data/hora encontrada(s) nesta tabela.";
                    lblStatusData.ForeColor = CorTextoSecundario;
                }
                else
                {
                    lblStatusData.Text = "Esta tabela não tem colunas de data/hora — limpeza por data indisponível.";
                    lblStatusData.ForeColor = Color.FromArgb(140, 80, 0);
                }
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblChavePrimaria.IsDisposed)
                {
                    return;
                }

                lblChavePrimaria.Text = ObterMensagemAmigavel(ex);
                lblChavePrimaria.ForeColor = Color.FromArgb(196, 55, 55);
            }
        }

        async Task CarregarTudoAsync()
        {
            var banco = BancoAtual();

            btnCarregarTudo.Enabled = false;
            Cursor = Cursors.WaitCursor;
            lblStatusShrink.ForeColor = CorTextoSecundario;
            lblStatusShrink.Text = "Carregando...";
            try
            {
                var top25 = await _moduloManutencao.ObterTop25MaioresTabelasAsync(banco, tokenPagina);
                if (tokenPagina.IsCancellationRequested || gridTop25.IsDisposed)
                {
                    return;
                }

                gridTop25.DataSource = top25;

                var tabelas = await _moduloIndices.ObterNomesTabelasAsync(banco, tokenPagina);
                if (tokenPagina.IsCancellationRequested || cmbTabelaSelecionada.IsDisposed)
                {
                    return;
                }

                cmbTabelaSelecionada.Items.Clear();
                cmbTabelaSelecionada.Items.AddRange(tabelas.ToArray());

                lblStatusShrink.Text = $"{top25.Count} tabela(s) na grade acima — {tabelas.Count} tabela(s) disponível(is) no seletor.";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da consulta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatusShrink.IsDisposed)
                {
                    return;
                }

                gridTop25.DataSource = null;
                lblStatusShrink.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatusShrink.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnCarregarTudo.Enabled = true;
            }
        }

        btnCarregarTudo.Click += (_, _) => _ = CarregarTudoAsync();
        cmbTabelaSelecionada.SelectedIndexChanged += (_, _) => _ = AtualizarTabelaSelecionadaAsync();

        btnShrinkLog.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnShrinkLog.Enabled = false;
            try
            {
                var banco = BancoAtual() ?? "banco padrão da conexão";
                var confirmacao = MessageBox.Show(this,
                    $"Confirma o SHRINK do arquivo de log (LDF) do banco \"{banco}\"? O Recovery Model será alterado " +
                    "temporariamente para SIMPLE (e restaurado ao original ao final, mesmo se algo der errado) para " +
                    "permitir o TRUNCATEONLY — reduz o tamanho do arquivo de log, mas pode gerar fragmentação; evite " +
                    "fazer isso com frequência.",
                    "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync("Shrink do Log (LDF)",
                    (progresso, ct) => _moduloManutencao.ExecutarShrinkLogAsync(BancoAtual(), progresso, ct),
                    "Atenção: shrink de log pode demorar em bancos grandes e pode gerar fragmentação. Evite cancelar " +
                    "no meio da operação — mesmo se cancelar, o Recovery Model original é restaurado antes de encerrar.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnShrinkLog.Enabled = true;
            }
        };

        btnShrinkBanco.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnShrinkBanco.Enabled = false;
            try
            {
                var banco = BancoAtual() ?? "banco padrão da conexão";
                var confirmacao = MessageBox.Show(this,
                    $"Confirma o SHRINK completo do banco de dados \"{banco}\" (dados e log)? Reduz o tamanho dos " +
                    "arquivos, mas pode gerar fragmentação e impactar o desempenho durante a execução.",
                    "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync("Shrink do Banco Completo",
                    (progresso, ct) => _moduloManutencao.ExecutarShrinkBancoAsync(BancoAtual(), progresso, ct),
                    "Atenção: shrink de banco pode demorar bastante em bancos grandes e pode gerar fragmentação e " +
                    "impactar o desempenho durante a execução. Evite cancelar no meio da operação.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnShrinkBanco.Enabled = true;
            }
        };

        btnExcluirPorData.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnExcluirPorData.Enabled = false;
            try
            {
                var tabela = cmbTabelaSelecionada.SelectedItem as string;
                var coluna = cmbColunaData.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(tabela) || string.IsNullOrWhiteSpace(coluna))
                {
                    MessageBox.Show(this, "Selecione a tabela e a coluna de data primeiro.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                DateTime dataLimite;
                try
                {
                    dataLimite = new DateTime((int)numAno.Value, (int)numMes.Value, (int)numDia.Value, (int)numHora.Value, (int)numMinuto.Value, 0);
                }
                catch (ArgumentOutOfRangeException)
                {
                    MessageBox.Show(this, "Data inválida — confira o dia, mês, ano, hora e minuto informados.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var confirmacao = MessageBox.Show(this,
                    $"Confirma a exclusão de TODOS os registros de \"{tabela}\" onde \"{coluna}\" seja menor ou igual " +
                    $"a {dataLimite:dd/MM/yyyy HH:mm}? Esta ação NÃO pode ser desfeita.",
                    "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                var banco = BancoAtual();
                await ExecutarManutencaoIndiceAsync($"Limpeza por data — {tabela}",
                    (progresso, ct) => _moduloManutencao.ExcluirRegistrosPorDataAsync(tabela, coluna, dataLimite, banco, progresso, ct),
                    "Atenção: exclusão de registros é uma operação IRREVERSÍVEL. Cancelar no meio da operação " +
                    "preserva os lotes já apagados até aquele momento (eles não são desfeitos), mas interrompe os " +
                    "lotes restantes.");

                await AtualizarTabelaSelecionadaAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnExcluirPorData.Enabled = true;
            }
        };

        // Pedido do usuário: apaga só 1 registro com a mesma condição de
        // "≤ data" acima — útil para testar antes de rodar em lote. Não usa
        // a janela de progresso (é uma única instrução, praticamente
        // instantânea) — só cursor de espera e um MessageBox com o
        // resultado.
        btnExcluirUmPorData.Click += async (_, _) =>
        {
            var tabela = cmbTabelaSelecionada.SelectedItem as string;
            var coluna = cmbColunaData.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(tabela) || string.IsNullOrWhiteSpace(coluna))
            {
                MessageBox.Show(this, "Selecione a tabela e a coluna de data primeiro.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DateTime dataLimite;
            try
            {
                dataLimite = new DateTime((int)numAno.Value, (int)numMes.Value, (int)numDia.Value, (int)numHora.Value, (int)numMinuto.Value, 0);
            }
            catch (ArgumentOutOfRangeException)
            {
                MessageBox.Show(this, "Data inválida — confira o dia, mês, ano, hora e minuto informados.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirmacao = MessageBox.Show(this,
                $"Confirma a exclusão de APENAS 1 registro de \"{tabela}\" onde \"{coluna}\" seja menor ou igual " +
                $"a {dataLimite:dd/MM/yyyy HH:mm}? Esta ação NÃO pode ser desfeita.",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            var banco = BancoAtual();
            try
            {
                Cursor = Cursors.WaitCursor;
                var excluidos = await _moduloManutencao.ExcluirUmRegistroPorDataAsync(tabela, coluna, dataLimite, banco);
                MessageBox.Show(this,
                    excluidos > 0 ? "1 registro excluído." : "Nenhum registro encontrado com essa condição.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            await AtualizarTabelaSelecionadaAsync();
        };

        btnExcluirPorId.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnExcluirPorId.Enabled = false;
            try
            {
                var tabela = cmbTabelaSelecionada.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(tabela) || chavePrimariaAtual is null || !Modulo_Manutencao.EhTipoNumericoInteiro(chavePrimariaAtual.TipoDado))
                {
                    MessageBox.Show(this, "Selecione uma tabela com chave primária simples e numérica primeiro.", "SQL Rocket",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var coluna = chavePrimariaAtual.NomeColuna;
                var idLimite = (long)numIdLimite.Value;

                var confirmacao = MessageBox.Show(this,
                    $"Confirma a exclusão de TODOS os registros de \"{tabela}\" onde \"{coluna}\" seja menor ou igual " +
                    $"a {idLimite}? Esta ação NÃO pode ser desfeita.",
                    "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                var banco = BancoAtual();
                await ExecutarManutencaoIndiceAsync($"Limpeza por ID — {tabela}",
                    (progresso, ct) => _moduloManutencao.ExcluirRegistrosPorChaveAsync(tabela, coluna, idLimite, banco, progresso, ct),
                    "Atenção: exclusão de registros é uma operação IRREVERSÍVEL. Cancelar no meio da operação " +
                    "preserva os lotes já apagados até aquele momento (eles não são desfeitos), mas interrompe os " +
                    "lotes restantes.");

                await AtualizarTabelaSelecionadaAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnExcluirPorId.Enabled = true;
            }
        };

        // Pedido do usuário: apaga só 1 registro com a mesma condição de
        // "≤ ID" acima — útil para testar antes de rodar em lote. Mesma
        // ideia de btnExcluirUmPorData (sem janela de progresso).
        btnExcluirUmPorId.Click += async (_, _) =>
        {
            var tabela = cmbTabelaSelecionada.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(tabela) || chavePrimariaAtual is null || !Modulo_Manutencao.EhTipoNumericoInteiro(chavePrimariaAtual.TipoDado))
            {
                MessageBox.Show(this, "Selecione uma tabela com chave primária simples e numérica primeiro.", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var coluna = chavePrimariaAtual.NomeColuna;
            var idLimite = (long)numIdLimite.Value;

            var confirmacao = MessageBox.Show(this,
                $"Confirma a exclusão de APENAS 1 registro de \"{tabela}\" onde \"{coluna}\" seja menor ou igual " +
                $"a {idLimite}? Esta ação NÃO pode ser desfeita.",
                "SQL Rocket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmacao != DialogResult.Yes)
            {
                return;
            }

            var banco = BancoAtual();
            try
            {
                Cursor = Cursors.WaitCursor;
                var excluidos = await _moduloManutencao.ExcluirUmRegistroPorChaveAsync(tabela, coluna, idLimite, banco);
                MessageBox.Show(this,
                    excluidos > 0 ? "1 registro excluído." : "Nenhum registro encontrado com essa condição.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            await AtualizarTabelaSelecionadaAsync();
        };

        // Combo "Banco de Dados": helper único compartilhado pelas seis telas
        // que têm esse combo (ver PreencherComboBancosAsync).
        _ = PreencherComboBancosAsync(cmbBanco, ReportarFalhaCombo, tokenPagina);
    }

    /// <summary>
    /// Manutenção &gt; Headblock e DeadLock — análise 100% offline de
    /// deadlocks E de head blocking a partir de um arquivo enviado pelo
    /// usuário (nenhuma conexão com banco é usada aqui, ao contrário das
    /// outras páginas de Manutenção): um arquivo .xel (Extended Events,
    /// ex.: "BlockedProcesses_0_134331021609830000.xel") ou um arquivo .xml
    /// já contendo o deadlock graph e/ou o blocked process report (ex.:
    /// resultado de sys.fn_xe_file_target_read_file, ou salvo pelo SSMS).
    /// Ambos os botões terminam no mesmo lugar — a grade "Eventos
    /// encontrados" — e selecionar uma linha ali mostra o detalhe "Resumo
    /// dos Processos Envolvidos" (tabela pivotada: 1 coluna por processo,
    /// vítima primeiro).
    /// </summary>
    private void MostrarPaginaHeadblockDeadlock()
    {
        lblTituloPagina.Text = "Manutenção > Headblock e DeadLock";

        var cartao = CriarCartao();

        var fonteSecao = new Font("Segoe UI", 12F, FontStyle.Bold);
        var fonteAviso = new Font("Segoe UI", 8.5F, FontStyle.Italic);

        var lblExplicacao = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 0),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Envie um arquivo .xel (Extended Events, ex.: sessão \"system_health\" ou uma sessão de captura " +
                   "dedicada) ou um arquivo .xml já com o evento (deadlock graph ou blocked process report — ex.: " +
                   "o resultado de sys.fn_xe_file_target_read_file, ou salvo pelo SSMS). Cobre tanto Deadlock " +
                   "(alguém é cancelado) quanto Head Blocking (bloqueio sem cancelar ninguém). A análise é feita " +
                   "inteiramente a partir do arquivo enviado, sem conectar a nenhum banco de dados."
        };

        var lblEnvioTitulo = new Label { Text = "Enviar arquivo para análise", AutoSize = true, Location = new Point(0, 54), Font = fonteSecao, ForeColor = CorTitulo };
        var btnEnviarXel = CriarBotaoAcao("Enviar arquivo .xel");
        btnEnviarXel.Location = new Point(0, 84);
        btnEnviarXel.Size = new Size(260, 34);
        var btnEnviarXml = CriarBotaoAcao("Enviar arquivo XML");
        btnEnviarXml.Location = new Point(276, 84);
        btnEnviarXml.Size = new Size(260, 34);
        var lblStatusDeadlock = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 128),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Nenhum arquivo analisado ainda."
        };

        var lblGridDeadlocksTitulo = new Label { Text = "Eventos encontrados", AutoSize = true, Location = new Point(0, 164), Font = fonteSecao, ForeColor = CorTitulo };
        var gridDeadlocks = new DataGridView
        {
            Location = new Point(0, 194),
            Size = new Size(1000, 160),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        gridDeadlocks.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colTipo",
            HeaderText = "Tipo",
            DataPropertyName = nameof(DeadlockEventoDto.DescricaoTipoEvento)
        });
        gridDeadlocks.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colHorario",
            HeaderText = "Horário",
            DataPropertyName = nameof(DeadlockEventoDto.Timestamp),
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy HH:mm:ss", NullValue = "(sem horário — veio de um XML solto)" }
        });
        gridDeadlocks.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colQuantidadeProcessos",
            HeaderText = "Processos envolvidos",
            DataPropertyName = nameof(DeadlockEventoDto.QuantidadeProcessos)
        });
        gridDeadlocks.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colSpids",
            HeaderText = "SPIDs",
            DataPropertyName = nameof(DeadlockEventoDto.ResumoSpids)
        });

        var lblResumoTitulo = new Label { Text = "Resumo dos Processos Envolvidos", AutoSize = true, Location = new Point(0, 366), Font = fonteSecao, ForeColor = CorTitulo };
        var gridResumoProcessos = new DataGridView
        {
            Location = new Point(0, 396),
            Size = new Size(1000, 220),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        cartao.Controls.AddRange(new Control[]
        {
            lblExplicacao,
            lblEnvioTitulo, btnEnviarXel, btnEnviarXml, lblStatusDeadlock,
            lblGridDeadlocksTitulo, gridDeadlocks,
            lblResumoTitulo, gridResumoProcessos
        });

        DefinirConteudo(cartao);

        // Monta a tabela pivotada "Resumo dos Processos Envolvidos": 1 linha
        // por campo (SPID/Process ID, Operação Primária, ...), 1 coluna por
        // processo do deadlock — vítima(s) sempre primeiro, igual à imagem
        // de referência do usuário. Com mais de 2 processos (deadlock
        // encadeado, raro mas possível), os sobreviventes além do primeiro
        // ganham o SPID no cabeçalho para diferenciar um do outro.
        void ExibirDeadlock(DeadlockEventoDto evento)
        {
            gridResumoProcessos.Rows.Clear();
            gridResumoProcessos.Columns.Clear();

            var colInformacao = new DataGridViewTextBoxColumn
            {
                Name = "colInformacao",
                HeaderText = "Informação",
                ReadOnly = true,
                DefaultCellStyle = new DataGridViewCellStyle { Font = new Font(gridResumoProcessos.Font, FontStyle.Bold) }
            };
            gridResumoProcessos.Columns.Add(colInformacao);

            // Cabeçalhos das colunas dependem do tipo de evento: um Deadlock
            // tem vítima (cancelada) e vencedor(es); um BlockedProcess
            // (head blocking) tem só o bloqueado (esperando) e o bloqueador
            // — ninguém é cancelado. EhVitima carrega o mesmo sentido de
            // "processo prejudicado" nos dois casos (ver DeadlockProcessoDto).
            var ehBlockedProcess = string.Equals(evento.TipoEvento, "BlockedProcess", StringComparison.OrdinalIgnoreCase);
            var processosOrdenados = evento.Processos.OrderByDescending(p => p.EhVitima).ToList();
            var totalVencedores = processosOrdenados.Count(p => !p.EhVitima);

            foreach (var processo in processosOrdenados)
            {
                string cabecalho;
                if (ehBlockedProcess)
                {
                    cabecalho = processo.EhVitima ? "Processo Bloqueado (Aguardando)" : "Processo Bloqueador";
                }
                else if (processo.EhVitima)
                {
                    cabecalho = "Processo Vitimado (Cancelado)";
                }
                else
                {
                    cabecalho = totalVencedores <= 1
                        ? "Processo Vencedor"
                        : $"Processo Sobrevivente (SPID {processo.Spid})";
                }

                gridResumoProcessos.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = $"col_{processo.ProcessId}",
                    HeaderText = cabecalho
                });
            }

            // Num BlockedProcess, "Bloqueio Retido"/"Bloqueio Solicitado" só
            // costumam vir preenchidos para o processo bloqueado (o que
            // informa, nos próprios atributos, o recurso que está
            // esperando) — o relatório desse tipo de evento não indica qual
            // lock o bloqueador está segurando (ver comentário em
            // Modulo_Deadlock.MontarProcessoBloqueio). O "-" cobre esse caso
            // sem inventar informação que o evento não tem.
            //
            // "Horário do Evento" é do evento como um todo (não varia por
            // processo) — por isso a mesma string aparece repetida em cada
            // coluna, em vez de recalculada a partir de "p".
            //
            // IMPORTANTE sobre "Host de Origem": o deadlock graph/blocked
            // process report do SQL Server só traz o atributo "hostname" da
            // conexão (nome da máquina CLIENTE que abriu a sessão) — não
            // existe, nesse tipo de evento, um IP (nem do servidor nem do
            // cliente). Por isso o rótulo é "Host de Origem (máquina
            // cliente)", não "IP do servidor": mostrar um IP aqui seria
            // inventar uma informação que o evento não contém.
            var horarioEvento = evento.Timestamp.HasValue
                ? evento.Timestamp.Value.ToString("dd/MM/yyyy HH:mm:ss")
                : "(sem horário — veio de um XML solto, sem essa informação)";

            (string Rotulo, Func<DeadlockProcessoDto, string> Valor)[] linhas =
            {
                ("Horário do Evento", _ => horarioEvento),
                ("SPID / Process ID", p => $"spid=\"{p.Spid}\" / {p.ProcessId}"),
                ("Aplicação (Client App)", p => string.IsNullOrEmpty(p.ClientApp) ? "-" : p.ClientApp),
                ("Host de Origem (máquina cliente)", p => string.IsNullOrEmpty(p.HostName) ? "-" : p.HostName),
                ("Operação Primária", p => p.OperacaoPrimaria),
                ("Gatilho / Código", p => p.GatilhoOuCodigo),
                ("Bloqueio Retido", p => string.IsNullOrEmpty(p.BloqueioRetidoDescricao) ? "-" : p.BloqueioRetidoDescricao),
                ("Bloqueio Solicitado", p => string.IsNullOrEmpty(p.BloqueioSolicitadoDescricao) ? "-" : p.BloqueioSolicitadoDescricao),
            };

            foreach (var (rotulo, valor) in linhas)
            {
                var valoresLinha = new List<object> { rotulo };
                valoresLinha.AddRange(processosOrdenados.Select(p => (object)valor(p)));
                gridResumoProcessos.Rows.Add(valoresLinha.ToArray());
            }
        }

        gridDeadlocks.SelectionChanged += (_, _) =>
        {
            if (gridDeadlocks.CurrentRow?.DataBoundItem is DeadlockEventoDto evento)
            {
                ExibirDeadlock(evento);
            }
        };

        // Compartilhado pelos dois botões de envio — só muda o filtro do
        // diálogo e qual método do Modulo_Deadlock é chamado para analisar
        // o arquivo escolhido.
        async Task CarregarArquivoAsync(string filtro, string tituloDialogo, Func<string, CancellationToken, Task<List<DeadlockEventoDto>>> analisar)
        {
            using var dialogo = new OpenFileDialog { Title = tituloDialogo, Filter = filtro, CheckFileExists = true };
            if (dialogo.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var nomeArquivo = Path.GetFileName(dialogo.FileName);
            try
            {
                Cursor = Cursors.WaitCursor;
                lblStatusDeadlock.ForeColor = CorTextoSecundario;
                lblStatusDeadlock.Text = $"Analisando \"{nomeArquivo}\"...";

                var eventos = await analisar(dialogo.FileName, CancellationToken.None);

                gridDeadlocks.DataSource = null;
                gridDeadlocks.DataSource = eventos;

                lblStatusDeadlock.ForeColor = CorTextoSecundario;
                lblStatusDeadlock.Text = eventos.Count == 1
                    ? $"1 evento encontrado em \"{nomeArquivo}\"."
                    : $"{eventos.Count} eventos encontrados em \"{nomeArquivo}\".";

                if (eventos.Count > 0)
                {
                    gridDeadlocks.ClearSelection();
                    gridDeadlocks.Rows[0].Selected = true;
                    if (gridDeadlocks.Rows[0].Cells.Count > 0)
                    {
                        gridDeadlocks.CurrentCell = gridDeadlocks.Rows[0].Cells[0];
                    }
                    ExibirDeadlock(eventos[0]);
                }
                else
                {
                    gridResumoProcessos.Rows.Clear();
                    gridResumoProcessos.Columns.Clear();
                }
            }
            catch (Exception ex)
            {
                lblStatusDeadlock.ForeColor = Color.FromArgb(178, 34, 34);
                lblStatusDeadlock.Text = $"Falha ao analisar \"{nomeArquivo}\".";
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        btnEnviarXel.Click += async (_, _) =>
        {
            await CarregarArquivoAsync(
                "Arquivos Extended Events (*.xel)|*.xel|Todos os arquivos (*.*)|*.*",
                "Selecionar arquivo .xel",
                (caminho, ct) => _moduloDeadlock.AnalisarArquivoXelAsync(caminho, ct));
        };

        btnEnviarXml.Click += async (_, _) =>
        {
            await CarregarArquivoAsync(
                "Arquivos XML (*.xml)|*.xml|Todos os arquivos (*.*)|*.*",
                "Selecionar arquivo XML",
                (caminho, ct) => _moduloDeadlock.AnalisarArquivoXmlAsync(caminho, ct));
        };
    }

    /// <summary>
    /// Manutenção &gt; Análise do plano de execução — mesmo padrão de
    /// "enviar arquivo &gt; analisar offline &gt; grade mestre + detalhe"
    /// do Headblock/DeadLock (<see cref="MostrarPaginaHeadblockDeadlock"/>):
    /// o usuário envia um .sqlplan, cada statement encontrado vira uma
    /// linha da grade "Instruções encontradas" e, ao selecionar uma linha,
    /// as 4 seções de detalhe pedidas pelo usuário são preenchidas (texto
    /// da consulta + resumo geral, Alertas — Warnings do XML mais Índices
    /// Sugeridos — MissingIndexes do XML, Operadores mais custosos por
    /// "custo exclusivo" — com o "Custo Relativo (%)" que o SSMS mostra em
    /// cada ícone do plano gráfico — e Scans candidatos a índice).
    ///
    /// A grade "Índices Sugeridos" tem dois botões: "Copiar script" (só
    /// copia o CREATE INDEX para a área de transferência) e "Criar índice
    /// automaticamente" (executa o script de verdade — única ação desta
    /// tela que precisa de uma conexão real, ver
    /// <see cref="Modulo_PlanoExecucao.CriarIndiceAsync"/> — com confirmação
    /// mostrando o servidor conectado, já que o .sqlplan pode ter sido
    /// gerado em outro ambiente).
    /// </summary>
    private void MostrarPaginaPlanoExecucao()
    {
        lblTituloPagina.Text = "Manutenção > Análise do plano de execução";

        var cartao = CriarCartao();

        var fonteSecao = new Font("Segoe UI", 12F, FontStyle.Bold);
        var fonteAviso = new Font("Segoe UI", 8.5F, FontStyle.Italic);

        var lblExplicacao = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 0),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Envie um arquivo .sqlplan (Ctrl+M/\"Include Actual Execution Plan\" no SSMS antes de rodar a " +
                   "consulta, ou \"Save Execution Plan As...\" num plano já aberto — funciona tanto com plano " +
                   "estimado quanto real). A análise é feita inteiramente a partir do arquivo enviado, sem " +
                   "conectar a nenhum banco de dados."
        };

        var lblEnvioTitulo = new Label { Text = "Enviar arquivo para análise", AutoSize = true, Location = new Point(0, 60), Font = fonteSecao, ForeColor = CorTitulo };
        var btnEnviarPlano = CriarBotaoAcao("Enviar arquivo .sqlplan");
        btnEnviarPlano.Location = new Point(0, 90);
        btnEnviarPlano.Size = new Size(220, 34);
        var lblStatusPlano = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 134),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Nenhum arquivo analisado ainda."
        };

        var lblGridStatementsTitulo = new Label { Text = "Instruções encontradas", AutoSize = true, Location = new Point(0, 170), Font = fonteSecao, ForeColor = CorTitulo };
        var gridStatements = new DataGridView
        {
            Location = new Point(0, 200),
            Size = new Size(1000, 150),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTipo", HeaderText = "Tipo", DataPropertyName = nameof(PlanoStatementDto.StatementType), FillWeight = 60 });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResumo", HeaderText = "Instrução", DataPropertyName = nameof(PlanoStatementDto.ResumoTexto), FillWeight = 220 });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colCusto", HeaderText = "Custo (subtree)", DataPropertyName = nameof(PlanoStatementDto.CustoSubTree), FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { Format = "N4" } });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLinhas", HeaderText = "Linhas Estimadas", DataPropertyName = nameof(PlanoStatementDto.LinhasEstimadas), FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" } });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colOperadores", HeaderText = "Operadores", DataPropertyName = nameof(PlanoStatementDto.QuantidadeOperadores), FillWeight = 60 });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colScans", HeaderText = "Scans", DataPropertyName = nameof(PlanoStatementDto.QuantidadeScans), FillWeight = 50 });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colAvisos", HeaderText = "Avisos", DataPropertyName = nameof(PlanoStatementDto.QuantidadeAvisos), FillWeight = 50 });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colIndices", HeaderText = "Índices Sugeridos", DataPropertyName = nameof(PlanoStatementDto.QuantidadeIndicesFaltantes), FillWeight = 70 });
        gridStatements.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTipoPlano", HeaderText = "Tipo de Plano", DataPropertyName = nameof(PlanoStatementDto.DescricaoTipoPlano), FillWeight = 90 });

        var lblConsultaTitulo = new Label { Text = "Texto da Instrução Selecionada", AutoSize = true, Location = new Point(0, 364), Font = fonteSecao, ForeColor = CorTitulo };
        var txtConsulta = new TextBox
        {
            Location = new Point(0, 394),
            Size = new Size(1000, 80),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(248, 249, 251)
        };

        var lblInfoResumo = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 484),
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = CorTextoSecundario,
            Text = "Selecione uma instrução na grade acima."
        };

        // A partir daqui, tudo é DETALHE do statement selecionado acima —
        // as duas abas ("Grades" e "Gráfico") mostram a MESMA seleção, só
        // que de jeitos diferentes (grades tabulares vs. diagrama visual).
        // As coordenadas Y dos controles abaixo são relativas ao topo de
        // CADA aba (0,0), não ao cartão inteiro.
        var lblAlertasTitulo = new Label { Text = "Alertas", AutoSize = true, Location = new Point(0, 0), Font = fonteSecao, ForeColor = CorTitulo };
        var gridAlertas = new DataGridView
        {
            Location = new Point(0, 30),
            Size = new Size(1000, 140),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        gridAlertas.Columns.Add(new DataGridViewTextBoxColumn { Name = "colSeveridade", HeaderText = "Severidade", DataPropertyName = nameof(PlanoAvisoDto.Severidade), FillWeight = 60 });
        gridAlertas.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTipoAviso", HeaderText = "Tipo", DataPropertyName = nameof(PlanoAvisoDto.Tipo), FillWeight = 90 });
        gridAlertas.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDescricaoAviso", HeaderText = "Descrição", DataPropertyName = nameof(PlanoAvisoDto.Descricao), FillWeight = 320 });
        gridAlertas.Columns.Add(new DataGridViewTextBoxColumn { Name = "colOperadorAviso", HeaderText = "Operador (NodeId)", DataPropertyName = nameof(PlanoAvisoDto.NodeIdOperador), FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { NullValue = "(geral do statement)" } });

        var lblIndicesTitulo = new Label { Text = "Índices Sugeridos", AutoSize = true, Location = new Point(0, 186), Font = fonteSecao, ForeColor = CorTitulo };
        var gridIndices = new DataGridView
        {
            Location = new Point(0, 216),
            Size = new Size(1000, 120),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        gridIndices.Columns.Add(new DataGridViewTextBoxColumn { Name = "colImpacto", HeaderText = "Impacto Estimado (%)", DataPropertyName = nameof(PlanoIndiceFaltanteDto.Impacto), FillWeight = 90, DefaultCellStyle = new DataGridViewCellStyle { Format = "N2" } });
        gridIndices.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTabelaIndice", HeaderText = "Tabela", DataPropertyName = nameof(PlanoIndiceFaltanteDto.TabelaCompleta), FillWeight = 130 });
        gridIndices.Columns.Add(new DataGridViewTextBoxColumn { Name = "colColunasChave", HeaderText = "Colunas (chave)", DataPropertyName = nameof(PlanoIndiceFaltanteDto.ColunasChaveDescricao), FillWeight = 180 });
        gridIndices.Columns.Add(new DataGridViewTextBoxColumn { Name = "colColunasInclude", HeaderText = "Colunas (INCLUDE)", DataPropertyName = nameof(PlanoIndiceFaltanteDto.ColunasIncludeDescricao), FillWeight = 180 });

        var btnCopiarScript = CriarBotaoAcao("Copiar script CREATE INDEX");
        btnCopiarScript.Location = new Point(0, 342);
        btnCopiarScript.Size = new Size(260, 32);
        var btnCriarIndiceAutomatico = CriarBotaoAcao("Criar índice automaticamente");
        btnCriarIndiceAutomatico.Location = new Point(276, 342);
        btnCriarIndiceAutomatico.Size = new Size(260, 32);
        btnCriarIndiceAutomatico.BackColor = Color.FromArgb(178, 34, 34);
        var lblAcoesIndiceStatus = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 382),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Selecione uma linha na grade acima. \"Copiar script\" só copia o CREATE INDEX para a área de " +
                   "transferência (nada é executado); \"Criar índice automaticamente\" roda o script agora, na " +
                   "conexão atual, contra o banco indicado pelo plano — confirme o servidor certo antes."
        };

        var lblOperadoresTitulo = new Label { Text = "Operadores Mais Custosos", AutoSize = true, Location = new Point(0, 430), Font = fonteSecao, ForeColor = CorTitulo };
        var lblOperadoresExplicacao = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 460),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Ordenado por \"Custo Exclusivo\" (o custo do próprio operador, sem contar os filhos) — geralmente " +
                   "um indicador melhor de onde está o gargalo do que o custo acumulado, que é sempre maior no " +
                   "topo da árvore. \"Custo Relativo\" é a mesma informação que o SSMS mostra embaixo de cada " +
                   "ícone no plano gráfico (ex.: \"Cost: 13%\")."
        };
        var gridOperadores = new DataGridView
        {
            Location = new Point(0, 504),
            Size = new Size(1000, 180),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colOperador", HeaderText = "Operador", DataPropertyName = nameof(PlanoOperadorDto.Descricao), FillWeight = 220 });
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colNivel", HeaderText = "Nível na Árvore", DataPropertyName = nameof(PlanoOperadorDto.Profundidade), FillWeight = 60 });
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colPercentualCusto", HeaderText = "Custo Relativo (%)", DataPropertyName = nameof(PlanoOperadorDto.DescricaoPercentualCusto), FillWeight = 80 });
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colCustoExclusivo", HeaderText = "Custo Exclusivo", DataPropertyName = nameof(PlanoOperadorDto.CustoExclusivo), FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { Format = "N4" } });
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colCustoAcumulado", HeaderText = "Custo Acumulado", DataPropertyName = nameof(PlanoOperadorDto.CustoSubTreeTotal), FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { Format = "N4" } });
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLinhasEstOper", HeaderText = "Linhas Estimadas", DataPropertyName = nameof(PlanoOperadorDto.LinhasEstimadas), FillWeight = 70, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" } });
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLinhasReaisOper", HeaderText = "Linhas Reais", DataPropertyName = nameof(PlanoOperadorDto.LinhasReais), FillWeight = 70, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", NullValue = "-" } });
        gridOperadores.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDivergencia", HeaderText = "Estatística Divergente?", DataPropertyName = nameof(PlanoOperadorDto.DescricaoDivergencia), FillWeight = 130 });

        var lblScansTitulo = new Label { Text = "Scans em vez de Seeks", AutoSize = true, Location = new Point(0, 700), Font = fonteSecao, ForeColor = CorTitulo };
        var lblScansExplicacao = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 730),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Um Scan lê a tabela/índice inteiro, enquanto um Seek busca direto pelas linhas necessárias — nem " +
                   "todo Scan é um problema (tabelas pequenas, ou quando a consulta realmente precisa de quase " +
                   "todas as linhas), mas é sempre o primeiro lugar para conferir se falta um índice adequado."
        };
        var gridScans = new DataGridView
        {
            Location = new Point(0, 774),
            Size = new Size(1000, 140),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        gridScans.Columns.Add(new DataGridViewTextBoxColumn { Name = "colOperadorScan", HeaderText = "Operador", DataPropertyName = nameof(PlanoOperadorDto.PhysicalOp), FillWeight = 100 });
        gridScans.Columns.Add(new DataGridViewTextBoxColumn { Name = "colObjetoScan", HeaderText = "Tabela/Índice", DataPropertyName = nameof(PlanoOperadorDto.ObjetoAlvo), FillWeight = 220, DefaultCellStyle = new DataGridViewCellStyle { NullValue = "-" } });
        gridScans.Columns.Add(new DataGridViewTextBoxColumn { Name = "colPercentualCustoScan", HeaderText = "Custo Relativo (%)", DataPropertyName = nameof(PlanoOperadorDto.DescricaoPercentualCusto), FillWeight = 80 });
        gridScans.Columns.Add(new DataGridViewTextBoxColumn { Name = "colCustoExclusivoScan", HeaderText = "Custo Exclusivo", DataPropertyName = nameof(PlanoOperadorDto.CustoExclusivo), FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { Format = "N4" } });
        gridScans.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLinhasEstScan", HeaderText = "Linhas Estimadas", DataPropertyName = nameof(PlanoOperadorDto.LinhasEstimadas), FillWeight = 70, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" } });
        gridScans.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLinhasReaisScan", HeaderText = "Linhas Reais", DataPropertyName = nameof(PlanoOperadorDto.LinhasReais), FillWeight = 70, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", NullValue = "-" } });

        // Aba "Grades": as 4 grades de detalhe de sempre, só que agora
        // dentro de uma TabPage em vez de soltas direto no cartão.
        var tabGrades = new TabPage("Grades");
        tabGrades.Controls.AddRange(new Control[]
        {
            lblAlertasTitulo, gridAlertas,
            lblIndicesTitulo, gridIndices, btnCopiarScript, btnCriarIndiceAutomatico, lblAcoesIndiceStatus,
            lblOperadoresTitulo, lblOperadoresExplicacao, gridOperadores,
            lblScansTitulo, lblScansExplicacao, gridScans
        });

        // Aba "Gráfico": diagrama visual da árvore de operadores do
        // statement selecionado (aproximação do plano gráfico do SSMS —
        // ver comentário de PlanoExecucaoDiagrama sobre o que é fiel ao
        // SSMS e o que é simplificado/genérico). Ocupa a aba INTEIRA
        // (Dock = Fill) — o painel de detalhes do operador NÃO fica mais
        // encaixado aqui dentro (ver comentário mais abaixo, em
        // pnlDetalheOperador), então o diagrama não perde mais espaço
        // horizontal para ele.
        var diagramaPlano = new PlanoExecucaoDiagrama { Dock = DockStyle.Fill };

        var tabGrafico = new TabPage("Gráfico");
        tabGrafico.Controls.Add(diagramaPlano);

        var tabDetalhe = new TabControl
        {
            Location = new Point(0, 550),
            Size = new Size(1000, 980)
        };
        tabDetalhe.TabPages.Add(tabGrades);
        tabDetalhe.TabPages.Add(tabGrafico);

        // Painel "Detalhes do Operador" — a pedido do usuário, virou um
        // QUADRO SEPARADO ao lado do TabControl inteiro, em vez de ficar
        // encaixado dentro da própria aba "Gráfico" (onde disputava
        // espaço horizontal com o diagrama e apertava ele, sobretudo
        // depois que o painel ficou mais largo — ver rodada anterior).
        // Mesma altura do TabControl, começando logo depois da borda
        // direita dele; fica visível nas duas abas (Grades e Gráfico),
        // não só na do gráfico, mas só é atualizado ao clicar num
        // operador do diagrama (ver ExibirDetalheOperador).
        var pnlDetalheOperador = new Panel
        {
            Location = new Point(tabDetalhe.Right + 20, tabDetalhe.Top),
            Size = new Size(420, tabDetalhe.Height),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            AutoScroll = true
        };
        var lblDetalheOperadorTitulo = new Label
        {
            Text = "Detalhes do Operador", AutoSize = true, Location = new Point(12, 12), Font = fonteSecao, ForeColor = CorTitulo
        };
        var lblDetalheOperadorTexto = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(388, 0),
            Location = new Point(12, 46),
            Font = new Font("Segoe UI", 9F),
            ForeColor = CorTextoSecundario,
            Text = "Clique numa caixa do diagrama à esquerda para ver os detalhes do operador aqui."
        };
        pnlDetalheOperador.Controls.Add(lblDetalheOperadorTitulo);
        pnlDetalheOperador.Controls.Add(lblDetalheOperadorTexto);

        cartao.Controls.AddRange(new Control[]
        {
            lblExplicacao,
            lblEnvioTitulo, btnEnviarPlano, lblStatusPlano,
            lblGridStatementsTitulo, gridStatements,
            lblConsultaTitulo, txtConsulta, lblInfoResumo,
            tabDetalhe, pnlDetalheOperador
        });

        DefinirConteudo(cartao);

        // Preenche o painel de detalhes do operador clicado no diagrama —
        // mesmas informações da grade "Operadores mais custosos", só que
        // em texto corrido (o diagrama não tem colunas de grade).
        void ExibirDetalheOperador(PlanoOperadorDto op)
        {
            var linhas = new List<string> { $"Operador: {op.PhysicalOp}" };
            if (!string.IsNullOrEmpty(op.LogicalOp) && !string.Equals(op.LogicalOp, op.PhysicalOp, StringComparison.OrdinalIgnoreCase))
            {
                linhas.Add($"Operação lógica: {op.LogicalOp}");
            }
            if (!string.IsNullOrEmpty(op.ObjetoAlvo))
            {
                linhas.Add($"Tabela/Índice: {op.ObjetoAlvo}");
            }
            linhas.Add($"Custo relativo: {op.DescricaoPercentualCusto}");
            linhas.Add($"Custo exclusivo: {op.CustoExclusivo:N4}");
            linhas.Add($"Custo acumulado: {op.CustoSubTreeTotal:N4}");
            linhas.Add($"Linhas estimadas: {op.LinhasEstimadas:N0}");
            if (op.LinhasReais.HasValue)
            {
                linhas.Add($"Linhas reais: {op.LinhasReais.Value:N0}");
            }

            // Mesmos campos extras que a dica (hover) já mostra em cima do
            // diagrama (ver PlanoExecucaoDiagrama.MontarDica) — repetidos
            // aqui no painel porque ficam visíveis o tempo todo, mesmo
            // depois de tirar o mouse de cima da caixa (a dica some).
            if (!string.IsNullOrEmpty(op.ModoExecucaoEstimado))
            {
                linhas.Add($"Modo de execução estimado: {op.ModoExecucaoEstimado}");
            }
            if (!string.IsNullOrEmpty(op.Armazenamento))
            {
                linhas.Add($"Armazenamento: {op.Armazenamento}");
            }
            if (op.CustoIO.HasValue)
            {
                linhas.Add($"Custo de E/S estimado: {op.CustoIO.Value:N7}");
            }
            if (op.CustoCPU.HasValue)
            {
                linhas.Add($"Custo de CPU estimado: {op.CustoCPU.Value:N7}");
            }
            linhas.Add($"Número de execuções estimado: {op.NumeroExecucoesEstimado:N0}");
            linhas.Add($"Linhas estimadas (todas as execuções): {op.LinhasEstimadasTodasExecucoes:N0}");
            if (op.LinhasParaLer.HasValue)
            {
                linhas.Add($"Linhas estimadas para ler: {op.LinhasParaLer.Value:N0}");
            }
            if (op.TamanhoLinhaBytes.HasValue)
            {
                linhas.Add($"Tamanho de linha estimado: {op.TamanhoLinhaBytes.Value:N0} B");
            }
            if (op.Ordenado.HasValue)
            {
                linhas.Add($"Ordenado: {(op.Ordenado.Value ? "Sim" : "Não")}");
            }
            linhas.Add($"Node ID: {op.NodeId}");
            if (op.ListaSaida.Count > 0)
            {
                // Uma coluna por linha (em vez de um parágrafo só separado
                // por "; ") — mesmo padrão já usado em "Avisos" logo
                // abaixo; evita que um nome de coluna qualquer force uma
                // quebra de linha no meio da palavra.
                linhas.Add("Lista de saída:");
                linhas.AddRange(op.ListaSaida.Select(c => $"• {c}"));
            }

            if (op.EstimativaMuitoDivergente)
            {
                linhas.Add("Estimativa muito divergente do real — considere atualizar estatísticas.");
            }
            if (op.Avisos.Count > 0)
            {
                linhas.Add("Avisos:");
                linhas.AddRange(op.Avisos.Select(a => $"• {a.Descricao}"));
            }
            lblDetalheOperadorTexto.Text = string.Join("\n", linhas);
        }

        diagramaPlano.OperadorSelecionado += (_, op) => ExibirDetalheOperador(op);

        // Preenche todas as seções de detalhe a partir do statement
        // selecionado na grade mestre — mesmo formato "N0"/"N4" das
        // colunas é repetido aqui no resumo em texto livre porque
        // lblInfoResumo não é uma grade (não tem DefaultCellStyle.Format).
        void ExibirStatement(PlanoStatementDto statement)
        {
            txtConsulta.Text = statement.StatementText;

            var partesResumo = new List<string>
            {
                $"Tipo: {statement.StatementType}",
                $"Custo estimado (subtree): {statement.CustoSubTree:N4}",
                $"Linhas estimadas: {statement.LinhasEstimadas:N0}",
                $"Plano: {statement.DescricaoTipoPlano}",
            };
            if (statement.TempoCompilacaoMs.HasValue)
            {
                partesResumo.Add($"Tempo de compilação: {statement.TempoCompilacaoMs.Value:N2} ms");
            }
            if (statement.CpuCompilacaoMs.HasValue)
            {
                partesResumo.Add($"CPU de compilação: {statement.CpuCompilacaoMs.Value:N2} ms");
            }
            if (statement.MemoriaCompilacaoKb.HasValue)
            {
                partesResumo.Add($"Memória de compilação: {statement.MemoriaCompilacaoKb.Value:N0} KB");
            }
            if (statement.MemoriaConcedidaKb.HasValue)
            {
                partesResumo.Add($"Memória concedida (grant): {statement.MemoriaConcedidaKb.Value:N0} KB");
            }
            lblInfoResumo.Text = string.Join("   |   ", partesResumo);

            gridAlertas.DataSource = null;
            gridAlertas.DataSource = statement.TodosOsAvisos;

            gridIndices.DataSource = null;
            gridIndices.DataSource = statement.IndicesFaltantes.OrderByDescending(i => i.Impacto).ToList();

            // A grade de operadores mostra a ordenação por custo (não a
            // ordem de "pré-ordem" da árvore usada internamente em
            // Modulo_PlanoExecucao.ColetarOperadores) — por isso não usa
            // DescricaoIndentada aqui (a indentação só faz sentido quando a
            // ordem reflete a árvore); a coluna "Nível na Árvore" continua
            // dando o contexto de profundidade sem depender da ordem.
            gridOperadores.DataSource = null;
            gridOperadores.DataSource = statement.Operadores.OrderByDescending(o => o.CustoExclusivo).ToList();

            gridScans.DataSource = null;
            gridScans.DataSource = statement.Operadores
                .Where(o => o.EhScan)
                .OrderByDescending(o => o.CustoExclusivo)
                .ToList();

            diagramaPlano.CarregarOperador(statement.OperadorRaiz);
            lblDetalheOperadorTexto.Text = "Clique numa caixa do diagrama à esquerda para ver os detalhes do operador aqui.";
        }

        gridStatements.SelectionChanged += (_, _) =>
        {
            if (gridStatements.CurrentRow?.DataBoundItem is PlanoStatementDto statement)
            {
                ExibirStatement(statement);
            }
        };

        btnCopiarScript.Click += (_, _) =>
        {
            if (gridIndices.CurrentRow?.DataBoundItem is PlanoIndiceFaltanteDto indice)
            {
                Clipboard.SetText(indice.ScriptCriacao);
                lblAcoesIndiceStatus.ForeColor = Color.FromArgb(30, 120, 60);
                lblAcoesIndiceStatus.Text = "Script copiado para a área de transferência.";
            }
            else
            {
                lblAcoesIndiceStatus.ForeColor = Color.FromArgb(140, 20, 20);
                lblAcoesIndiceStatus.Text = "Selecione uma linha na grade \"Índices Sugeridos\" primeiro.";
            }
        };

        btnCriarIndiceAutomatico.Click += async (_, _) =>
        {
            // Trava contra duplo clique: o botão é desabilitado ANTES da
            // confirmação, para que um segundo clique não abra um segundo
            // diálogo nem dispare a mesma operação destrutiva duas vezes em
            // paralelo (mesma ideia do SetBotoesAplicar do Comparador).
            btnCriarIndiceAutomatico.Enabled = false;
            try
            {
                if (gridIndices.CurrentRow?.DataBoundItem is not PlanoIndiceFaltanteDto indice)
                {
                    lblAcoesIndiceStatus.ForeColor = Color.FromArgb(140, 20, 20);
                    lblAcoesIndiceStatus.Text = "Selecione uma linha na grade \"Índices Sugeridos\" primeiro.";
                    return;
                }

                var servidorAtual = _conectarSql.ConfiguracaoAtual?.ServerName ?? "(desconhecido)";
                var confirmar = MessageBox.Show(this,
                    $"Criar o seguinte índice em \"{indice.TabelaCompleta}\"?\n\n{indice.ScriptCriacao}\n\n" +
                    $"O script vai rodar no banco \"{indice.Banco}\" (o mesmo indicado pelo plano de execução) " +
                    $"usando a conexão atual — servidor \"{servidorAtual}\". Confirme que é o servidor certo antes " +
                    "de continuar: o arquivo .sqlplan pode ter sido gerado em outro ambiente, e este botão executa " +
                    "o script de verdade (não é só uma simulação).\n\n" +
                    "Atenção: a criação de um índice pode deixar o banco de dados lento enquanto estiver em " +
                    "andamento (bloqueios e uso intenso de CPU/disco), especialmente em tabelas grandes.",
                    "Confirmar criação de índice", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirmar != DialogResult.Yes)
                {
                    return;
                }

                await ExecutarManutencaoIndiceAsync($"Criar índice — {indice.TabelaCompleta}",
                    (progresso, ct) => _moduloPlanoExecucao.CriarIndiceAsync(indice, progresso, ct),
                    "Atenção: a criação do índice pode deixar o banco de dados lento (bloqueios e uso intenso de " +
                    "CPU/disco) até terminar, especialmente em tabelas grandes. Se preferir, clique em \"Cancelar " +
                    "operação\" abaixo — é seguro, o SQL Server desfaz a criação sem deixar o índice pela metade.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnCriarIndiceAutomatico.Enabled = true;
            }
        };

        btnEnviarPlano.Click += async (_, _) =>
        {
            using var dialogo = new OpenFileDialog
            {
                Title = "Selecionar arquivo de plano de execução",
                Filter = "Planos de execução (*.sqlplan)|*.sqlplan|Todos os arquivos (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialogo.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var nomeArquivo = Path.GetFileName(dialogo.FileName);
            try
            {
                Cursor = Cursors.WaitCursor;
                lblStatusPlano.ForeColor = CorTextoSecundario;
                lblStatusPlano.Text = $"Analisando \"{nomeArquivo}\"...";

                var statements = await _moduloPlanoExecucao.AnalisarArquivoAsync(dialogo.FileName, CancellationToken.None);

                gridStatements.DataSource = null;
                gridStatements.DataSource = statements;

                lblStatusPlano.ForeColor = CorTextoSecundario;
                lblStatusPlano.Text = statements.Count == 1
                    ? $"1 instrução encontrada em \"{nomeArquivo}\"."
                    : $"{statements.Count} instruções encontradas em \"{nomeArquivo}\".";

                if (statements.Count > 0)
                {
                    gridStatements.ClearSelection();
                    gridStatements.Rows[0].Selected = true;
                    if (gridStatements.Rows[0].Cells.Count > 0)
                    {
                        gridStatements.CurrentCell = gridStatements.Rows[0].Cells[0];
                    }
                    ExibirStatement(statements[0]);
                }
                else
                {
                    txtConsulta.Text = string.Empty;
                    lblInfoResumo.Text = "Selecione uma instrução na grade acima.";
                    gridAlertas.DataSource = null;
                    gridIndices.DataSource = null;
                    gridOperadores.DataSource = null;
                    gridScans.DataSource = null;
                    diagramaPlano.CarregarOperador(null);
                    lblDetalheOperadorTexto.Text = "Clique numa caixa do diagrama à esquerda para ver os detalhes do operador aqui.";
                }
            }
            catch (Exception ex)
            {
                lblStatusPlano.ForeColor = Color.FromArgb(178, 34, 34);
                lblStatusPlano.Text = $"Falha ao analisar \"{nomeArquivo}\".";
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        };
    }

    /// <summary>
    /// "Profiler" (Manutenção &gt; Profiler) — reproduz a grade do SQL
    /// Server Profiler clássico usando Extended Events por baixo dos panos
    /// (ver remarks de <see cref="Modulo_Profiler"/> sobre o motivo). Três
    /// abas: "Configuração" (escolhe os eventos e os filtros de exibição
    /// usados pela captura ao vivo), "Executar" (inicia/para a sessão no
    /// servidor e mostra os eventos chegando, com opção de salvar em CSV) e
    /// "Analisar arquivo" (lê um .xel — ou um .csv já exportado por esta
    /// mesma tela — 100% offline, com seu próprio conjunto de filtros).
    ///
    /// ATENÇÃO: funcionalidade nova, que executa DDL ao vivo contra o
    /// servidor (CREATE/ALTER/DROP EVENT SESSION) e não pôde ser testada
    /// contra uma instância real de SQL Server neste ambiente de
    /// desenvolvimento — ver remarks de Modulo_Profiler para o detalhe
    /// completo do risco.
    /// </summary>
    private void MostrarPaginaProfiler()
    {
        lblTituloPagina.Text = "Manutenção > Profiler";

        var cartao = CriarCartao();
        var fonteSecao = new Font("Segoe UI", 12F, FontStyle.Bold);
        var fonteAviso = new Font("Segoe UI", 8.5F, FontStyle.Italic);
        var fonteRotulo = new Font("Segoe UI", 9F);

        // O texto ficou mais longo nesta rodada (frase nova sobre .trc não
        // suportado) e agora quebra em várias linhas. Uma rodada anterior
        // tentou posicionar tabProfiler (abaixo) lendo lblExplicacaoProfiler.Bottom
        // depois do label pronto — mas isso quebrou de um jeito pior ainda
        // (rodando de verdade: a faixa de abas "Configuração/Executar/Analisar
        // arquivo" e o título "Configuração da captura" sumiram da tela,
        // sinal de que o Label.Bottom lido ali não refletia a altura real
        // do texto quebrado em várias linhas — provavelmente por causa da
        // ordem/momento em que o AutoSize recalcula, algo que não vale a
        // pena depurar a fundo). Em vez de depender do Label recalcular
        // sozinho, a altura é calculada aqui de forma explícita e
        // determinística com TextRenderer.MeasureText (mesma técnica já
        // usada no desenho da dica/hover do diagrama de plano de execução),
        // usando a MESMA largura (1000) e a MESMA fonte do label — assim
        // tabProfiler.Location.Y (mais abaixo) sempre bate com a altura
        // real do texto, não importa quantas linhas ele ocupe.
        const int LarguraExplicacaoProfiler = 1000;
        var textoExplicacaoProfiler =
            "Reproduz a grade do SQL Server Profiler clássico — mas usa Extended Events por baixo dos " +
            "panos, porque o Profiler/SQL Trace clássico está descontinuado desde o SQL Server 2016 e não " +
            "funciona no Azure SQL Database. Requer a permissão ALTER ANY EVENT SESSION no servidor " +
            "conectado. Funcionalidade nova, ainda não testada contra um servidor real — se \"Iniciar " +
            "captura\" falhar, copie a mensagem de erro completa (ela inclui o script que falhou) para " +
            "reportar o problema. A aba \"Analisar arquivo\" abre .xel (Extended Events) ou .csv " +
            "exportado por esta mesma tela — não é possível abrir um .trc do Profiler clássico: não " +
            "existe nenhuma biblioteca oficial da Microsoft para ler esse formato binário legado de " +
            "forma offline num app .NET moderno como este (confirmado depois de tentar; ver o comentário " +
            "em FerramentasDBA.Classes.csproj para o detalhe técnico).";
        var alturaExplicacaoProfiler = TextRenderer.MeasureText(
            textoExplicacaoProfiler, fonteAviso, new Size(LarguraExplicacaoProfiler, int.MaxValue), TextFormatFlags.WordBreak).Height;

        var lblExplicacaoProfiler = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(LarguraExplicacaoProfiler, 0),
            Location = new Point(0, 0),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = textoExplicacaoProfiler
        };

        // As 18 colunas do Profiler clássico — mesmos pares (nome exibido,
        // Name da DataGridViewColumn correspondente em ConfigurarColunasProfiler)
        // usados tanto para montar a lista "Colunas a exibir" quanto para
        // aplicar a visibilidade escolhida na grade (AplicarVisibilidadeColunas).
        var colunasProfilerDisponiveis = new (string Exibicao, string NomeColuna)[]
        {
            ("EventClass", "colEventClass"), ("TextData", "colTextData"), ("ApplicationName", "colApplicationName"),
            ("NTUserName", "colNTUserName"), ("LoginName", "colLoginName"), ("CPU", "colCpu"),
            ("Reads", "colReads"), ("Writes", "colWrites"), ("Duration", "colDuration"),
            ("ClientProcessID", "colClientProcessID"), ("SPID", "colSpid"), ("StartTime", "colStartTime"),
            ("EndTime", "colEndTime"), ("DatabaseID", "colDatabaseID"), ("DatabaseName", "colDatabaseName"),
            ("HostName", "colHostName"), ("RowCounts", "colRowCounts"), ("ServerName", "colServerName")
        };

        // Altura fixa do painel devolvido por CriarPainelConfigFiltro —
        // usada tanto lá dentro quanto por quem posiciona o que vem depois
        // dele (lblConfigAviso, btnAplicarFiltro, gridAnalise), pra não
        // espalhar o mesmo número mágico em vários lugares.
        const int AlturaPainelConfigFiltro = 446;

        // ------------------------------------------------------------
        // Monta o trio "lista de eventos + colunas a exibir + filtros de
        // exibição" — usado tanto na aba "Configuração" (para a captura ao
        // vivo) quanto na aba "Analisar arquivo" (com seu próprio conjunto
        // independente, aplicado sobre o arquivo carregado). Coordenadas
        // relativas a (0,0) do painel devolvido — quem usa só reposiciona o
        // Location do próprio painel.
        // ------------------------------------------------------------
        (Panel Painel, CheckedListBox ListaEventos, CheckedListBox ListaColunas, TextBox TxtApp, TextBox TxtBanco, TextBox TxtLogin,
            TextBox TxtHost, TextBox TxtTexto, NumericUpDown NumDuracao, CheckBox ChkSoUsuario, CheckBox ChkExcluirProprio)
            CriarPainelConfigFiltro(string tituloEventos, bool marcarPadraoStandard)
        {
            var painel = new Panel { Location = new Point(0, 0), Size = new Size(1000, AlturaPainelConfigFiltro) };

            var lblEventos = new Label { Text = tituloEventos, AutoSize = true, Location = new Point(0, 0), Font = fonteSecao, ForeColor = CorTitulo };
            var listaEventos = new CheckedListBox
            {
                Location = new Point(0, 30),
                Size = new Size(300, 180),
                CheckOnClick = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            foreach (var evento in Modulo_Profiler.CatalogoEventos)
            {
                var indice = listaEventos.Items.Add(evento.NomeExibicao);
                if (marcarPadraoStandard && evento.PadraoStandard)
                {
                    listaEventos.SetItemChecked(indice, true);
                }
            }

            var lblFiltros = new Label
            {
                Text = "Filtros de exibição (aplicados aqui no aplicativo, nunca no servidor)",
                AutoSize = true, Location = new Point(320, 0), Font = fonteSecao, ForeColor = CorTitulo
            };

            var lblApp = new Label { Text = "Aplicativo contém:", AutoSize = true, Location = new Point(320, 34), Font = fonteRotulo, ForeColor = CorTitulo };
            var txtApp = new TextBox { Location = new Point(320, 54), Size = new Size(320, 24) };

            var lblBanco = new Label { Text = "Banco contém:", AutoSize = true, Location = new Point(660, 34), Font = fonteRotulo, ForeColor = CorTitulo };
            var txtBanco = new TextBox { Location = new Point(660, 54), Size = new Size(320, 24) };

            var lblLogin = new Label { Text = "Login contém:", AutoSize = true, Location = new Point(320, 88), Font = fonteRotulo, ForeColor = CorTitulo };
            var txtLogin = new TextBox { Location = new Point(320, 108), Size = new Size(320, 24) };

            var lblHost = new Label { Text = "Host contém:", AutoSize = true, Location = new Point(660, 88), Font = fonteRotulo, ForeColor = CorTitulo };
            var txtHost = new TextBox { Location = new Point(660, 108), Size = new Size(320, 24) };

            var lblTexto = new Label { Text = "Texto do comando contém:", AutoSize = true, Location = new Point(320, 142), Font = fonteRotulo, ForeColor = CorTitulo };
            var txtTexto = new TextBox { Location = new Point(320, 162), Size = new Size(660, 24) };

            var lblDuracao = new Label { Text = "Duração mínima (ms):", AutoSize = true, Location = new Point(320, 196), Font = fonteRotulo, ForeColor = CorTitulo };
            var numDuracao = new NumericUpDown { Location = new Point(320, 216), Size = new Size(120, 24), Minimum = 0, Maximum = 3600000, Value = 0, ThousandsSeparator = true };

            // As duas checkboxes ficam numa faixa própria, abaixo tanto da
            // lista de eventos (termina em y=210) quanto do campo "Duração
            // mínima" (numDuracao termina em y=240, x=320) — com AutoSize,
            // o texto comprido de chkSoUsuario passava fácil dos 320px de
            // largura e ficava por cima do numDuracao quando as duas
            // estavam lado a lado na mesma faixa (bug visto rodando de
            // verdade: "Somente comandos..." cortado atrás da caixa "0" de
            // duração). Ficando numa faixa cheia, sem nada à direita, isso
            // não acontece mais, custando só um pouco mais de altura no
            // painel (ver AlturaPainelConfigFiltro).
            var chkSoUsuario = new CheckBox
            {
                Text = "Somente comandos (oculta eventos cujo texto começa com \"SET \")",
                AutoSize = true, Location = new Point(0, 250), Font = fonteRotulo, ForeColor = CorTitulo
            };
            var chkExcluirProprio = new CheckBox
            {
                Text = "Excluir consultas desta própria ferramenta",
                AutoSize = true, Location = new Point(0, 276), Font = fonteRotulo, ForeColor = CorTitulo, Checked = true
            };

            // "Colunas a exibir" — a pedido do usuário, controla só quais
            // colunas aparecem na grade (a lista NÃO reduz o que é
            // capturado/lido nem o que vai pro CSV — CSV continua sempre
            // com as 18 colunas completas, pra um "salvar e reabrir" nunca
            // perder dado por causa de uma coluna escondida na hora de
            // salvar). Todas marcadas por padrão. MultiColumn pra caber as
            // 18 numa faixa baixa, em vez de uma lista alta de rolagem.
            var lblColunas = new Label { Text = "Colunas a exibir na grade", AutoSize = true, Location = new Point(0, 306), Font = fonteSecao, ForeColor = CorTitulo };
            var listaColunas = new CheckedListBox
            {
                Location = new Point(0, 336),
                Size = new Size(980, 96),
                MultiColumn = true,
                ColumnWidth = 165,
                CheckOnClick = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            foreach (var coluna in colunasProfilerDisponiveis)
            {
                var indice = listaColunas.Items.Add(coluna.Exibicao);
                listaColunas.SetItemChecked(indice, true);
            }

            painel.Controls.AddRange(new Control[]
            {
                lblEventos, listaEventos,
                lblFiltros, lblApp, txtApp, lblBanco, txtBanco, lblLogin, txtLogin, lblHost, txtHost,
                lblTexto, txtTexto, lblDuracao, numDuracao, chkSoUsuario, chkExcluirProprio,
                lblColunas, listaColunas
            });

            return (painel, listaEventos, listaColunas, txtApp, txtBanco, txtLogin, txtHost, txtTexto, numDuracao, chkSoUsuario, chkExcluirProprio);
        }

        List<string> ObterEventosMarcados(CheckedListBox lista)
        {
            var marcados = new HashSet<string>(lista.CheckedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            return Modulo_Profiler.CatalogoEventos.Where(c => marcados.Contains(c.NomeExibicao)).Select(c => c.NomeEvento).ToList();
        }

        ProfilerFiltroDto MontarFiltro(
            CheckedListBox listaEventos, TextBox txtApp, TextBox txtBanco, TextBox txtLogin,
            TextBox txtHost, TextBox txtTexto, NumericUpDown numDuracao, CheckBox chkSoUsuario, CheckBox chkExcluirProprio) => new()
        {
            EventosSelecionados = ObterEventosMarcados(listaEventos),
            AplicativoContem = string.IsNullOrWhiteSpace(txtApp.Text) ? null : txtApp.Text.Trim(),
            BancoContem = string.IsNullOrWhiteSpace(txtBanco.Text) ? null : txtBanco.Text.Trim(),
            LoginContem = string.IsNullOrWhiteSpace(txtLogin.Text) ? null : txtLogin.Text.Trim(),
            HostContem = string.IsNullOrWhiteSpace(txtHost.Text) ? null : txtHost.Text.Trim(),
            TextoContem = string.IsNullOrWhiteSpace(txtTexto.Text) ? null : txtTexto.Text.Trim(),
            DuracaoMinimaMs = numDuracao.Value > 0 ? (long)numDuracao.Value : null,
            SomenteComandosDeUsuario = chkSoUsuario.Checked,
            ExcluirConsultasDoProprioApp = chkExcluirProprio.Checked
        };

        // Mesmas 18 colunas do Profiler clássico, nesta ordem, em qualquer
        // grade da tela (Executar/Analisar arquivo) — mesmo cabeçalho usado
        // pelo CSV exportado/importado (ver SalvarComoCsv/LerCsv), pra
        // manter os dois formatos alinhados.
        void ConfigurarColunasProfiler(DataGridView grid)
        {
            grid.AutoGenerateColumns = false;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colEventClass", HeaderText = "EventClass", DataPropertyName = nameof(ProfilerEventoDto.EventClass), FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTextData", HeaderText = "TextData", DataPropertyName = nameof(ProfilerEventoDto.TextData), FillWeight = 260 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colApplicationName", HeaderText = "ApplicationName", DataPropertyName = nameof(ProfilerEventoDto.ApplicationName), FillWeight = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colNTUserName", HeaderText = "NTUserName", DataPropertyName = nameof(ProfilerEventoDto.NTUserName), FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLoginName", HeaderText = "LoginName", DataPropertyName = nameof(ProfilerEventoDto.LoginName), FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colCpu", HeaderText = "CPU", DataPropertyName = nameof(ProfilerEventoDto.Cpu), FillWeight = 55, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colReads", HeaderText = "Reads", DataPropertyName = nameof(ProfilerEventoDto.Reads), FillWeight = 60, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colWrites", HeaderText = "Writes", DataPropertyName = nameof(ProfilerEventoDto.Writes), FillWeight = 60, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDuration", HeaderText = "Duration", DataPropertyName = nameof(ProfilerEventoDto.Duration), FillWeight = 70, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colClientProcessID", HeaderText = "ClientProcessID", DataPropertyName = nameof(ProfilerEventoDto.ClientProcessID), FillWeight = 90, DefaultCellStyle = new DataGridViewCellStyle { NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colSpid", HeaderText = "SPID", DataPropertyName = nameof(ProfilerEventoDto.Spid), FillWeight = 55, DefaultCellStyle = new DataGridViewCellStyle { NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colStartTime", HeaderText = "StartTime", DataPropertyName = nameof(ProfilerEventoDto.StartTime), FillWeight = 105, DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm:ss.fff", NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colEndTime", HeaderText = "EndTime", DataPropertyName = nameof(ProfilerEventoDto.EndTime), FillWeight = 105, DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm:ss.fff", NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDatabaseID", HeaderText = "DatabaseID", DataPropertyName = nameof(ProfilerEventoDto.DatabaseID), FillWeight = 75, DefaultCellStyle = new DataGridViewCellStyle { NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDatabaseName", HeaderText = "DatabaseName", DataPropertyName = nameof(ProfilerEventoDto.DatabaseName), FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colHostName", HeaderText = "HostName", DataPropertyName = nameof(ProfilerEventoDto.HostName), FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colRowCounts", HeaderText = "RowCounts", DataPropertyName = nameof(ProfilerEventoDto.RowCounts), FillWeight = 75, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", NullValue = "-" } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colServerName", HeaderText = "ServerName", DataPropertyName = nameof(ProfilerEventoDto.ServerName), FillWeight = 90, DefaultCellStyle = new DataGridViewCellStyle { NullValue = "-" } });
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        }

        // Aplica a seleção da lista "Colunas a exibir" (CriarPainelConfigFiltro)
        // como Visible de cada DataGridViewColumn correspondente — só
        // esconde/mostra, nunca remove a coluna (os dados continuam lá,
        // inclusive pro CSV, que sempre exporta as 18 colunas completas).
        void AplicarVisibilidadeColunas(DataGridView grid, CheckedListBox listaColunas)
        {
            var marcadas = new HashSet<string>(listaColunas.CheckedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var coluna in colunasProfilerDisponiveis)
            {
                if (grid.Columns[coluna.NomeColuna] is DataGridViewColumn colunaGrid)
                {
                    colunaGrid.Visible = marcadas.Contains(coluna.Exibicao);
                }
            }
        }

        // ------------------------------------------------------------
        // CSV: mesmo cabeçalho/ordem de ConfigurarColunasProfiler — usado
        // pelo botão "Salvar como CSV" (aba Executar) e, simetricamente,
        // reaberto pelo "Abrir arquivo" da aba Analisar arquivo. O parser
        // (DividirCsvEmLinhas) respeita aspas mesmo com vírgula/quebra de
        // linha DENTRO do campo (comum em TextData, que costuma ser um
        // comando SQL de várias linhas) — por isso lê o arquivo inteiro de
        // uma vez em vez de por linha (File.ReadAllLines quebraria um campo
        // assim ao meio).
        // ------------------------------------------------------------
        const string FormatoDataCsv = "yyyy-MM-dd HH:mm:ss.fff";

        string EscaparCsv(string? valor)
        {
            if (string.IsNullOrEmpty(valor)) return string.Empty;
            var precisaAspas = valor.Contains(',') || valor.Contains('"') || valor.Contains('\n') || valor.Contains('\r');
            var escapado = valor.Replace("\"", "\"\"");
            return precisaAspas ? $"\"{escapado}\"" : escapado;
        }

        void SalvarComoCsv(List<ProfilerEventoDto> eventos, string sugestaoNomeArquivo)
        {
            using var dialogoSalvar = new SaveFileDialog
            {
                Title = "Salvar captura do Profiler",
                Filter = "Arquivo CSV (*.csv)|*.csv|Todos os arquivos (*.*)|*.*",
                FileName = sugestaoNomeArquivo
            };
            if (dialogoSalvar.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                var linhas = new List<string>
                {
                    "EventClass,TextData,ApplicationName,NTUserName,LoginName,CPU,Reads,Writes,Duration,ClientProcessID,SPID,StartTime,EndTime,DatabaseID,DatabaseName,HostName,RowCounts,ServerName"
                };
                foreach (var evento in eventos)
                {
                    linhas.Add(string.Join(",", new[]
                    {
                        EscaparCsv(evento.EventClass),
                        EscaparCsv(evento.TextData),
                        EscaparCsv(evento.ApplicationName),
                        EscaparCsv(evento.NTUserName),
                        EscaparCsv(evento.LoginName),
                        EscaparCsv(evento.Cpu?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.Reads?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.Writes?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.Duration?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.ClientProcessID?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.Spid?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.StartTime?.ToString(FormatoDataCsv, CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.EndTime?.ToString(FormatoDataCsv, CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.DatabaseID?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.DatabaseName),
                        EscaparCsv(evento.HostName),
                        EscaparCsv(evento.RowCounts?.ToString(CultureInfo.InvariantCulture)),
                        EscaparCsv(evento.ServerName)
                    }));
                }

                File.WriteAllLines(dialogoSalvar.FileName, linhas, System.Text.Encoding.UTF8);
                MessageBox.Show(this, $"{eventos.Count} evento(s) salvo(s) em \"{Path.GetFileName(dialogoSalvar.FileName)}\".",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Não foi possível salvar o arquivo.\n\n{ex.Message}", "SQL Rocket",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        List<List<string>> DividirCsvEmLinhas(string texto)
        {
            var linhas = new List<List<string>>();
            var linhaAtual = new List<string>();
            var campoAtual = new System.Text.StringBuilder();
            var dentroAspas = false;
            for (int i = 0; i < texto.Length; i++)
            {
                var c = texto[i];
                if (dentroAspas)
                {
                    if (c == '"')
                    {
                        if (i + 1 < texto.Length && texto[i + 1] == '"') { campoAtual.Append('"'); i++; }
                        else dentroAspas = false;
                    }
                    else campoAtual.Append(c);
                }
                else if (c == '"') dentroAspas = true;
                else if (c == ',') { linhaAtual.Add(campoAtual.ToString()); campoAtual.Clear(); }
                else if (c == '\r') { /* ignora — \n cuida da quebra de linha */ }
                else if (c == '\n') { linhaAtual.Add(campoAtual.ToString()); campoAtual.Clear(); linhas.Add(linhaAtual); linhaAtual = new List<string>(); }
                else campoAtual.Append(c);
            }
            if (campoAtual.Length > 0 || linhaAtual.Count > 0)
            {
                linhaAtual.Add(campoAtual.ToString());
                linhas.Add(linhaAtual);
            }
            return linhas;
        }

        List<ProfilerEventoDto> LerCsv(string caminho)
        {
            var texto = File.ReadAllText(caminho, System.Text.Encoding.UTF8);
            var linhas = DividirCsvEmLinhas(texto);
            var resultado = new List<ProfilerEventoDto>();

            string? NuloSeVazio(string valor) => string.IsNullOrEmpty(valor) ? null : valor;
            long? ParseLongOuNulo(string valor) => long.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
            int? ParseIntOuNulo(string valor) => int.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
            DateTime? ParseDataOuNula(string valor) => DateTime.TryParseExact(valor, FormatoDataCsv, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;

            // Linha 0 é o cabeçalho.
            for (int i = 1; i < linhas.Count; i++)
            {
                var campos = linhas[i];
                if (campos.Count < 18 || (campos.Count == 1 && string.IsNullOrWhiteSpace(campos[0])))
                {
                    continue;
                }

                var inicio = ParseDataOuNula(campos[11]);
                resultado.Add(new ProfilerEventoDto
                {
                    EventClass = campos[0],
                    TextData = NuloSeVazio(campos[1]),
                    ApplicationName = NuloSeVazio(campos[2]),
                    NTUserName = NuloSeVazio(campos[3]),
                    LoginName = NuloSeVazio(campos[4]),
                    Cpu = ParseLongOuNulo(campos[5]),
                    Reads = ParseLongOuNulo(campos[6]),
                    Writes = ParseLongOuNulo(campos[7]),
                    Duration = ParseLongOuNulo(campos[8]),
                    ClientProcessID = ParseIntOuNulo(campos[9]),
                    Spid = ParseIntOuNulo(campos[10]),
                    StartTime = inicio,
                    EndTime = ParseDataOuNula(campos[12]),
                    DatabaseID = ParseIntOuNulo(campos[13]),
                    DatabaseName = NuloSeVazio(campos[14]),
                    HostName = NuloSeVazio(campos[15]),
                    RowCounts = ParseLongOuNulo(campos[16]),
                    ServerName = NuloSeVazio(campos[17]),
                    // CSV não guarda o timestamp bruto original (só
                    // Start/EndTime, já em hora local) — usado aqui de volta
                    // só pra ordenação/dedupe internos, sem precisão de
                    // milissegundos "de sobra" perdida (mesmo valor de
                    // StartTime, ou "agora" se nem esse existir).
                    TimestampBruto = inicio.HasValue ? new DateTimeOffset(inicio.Value) : DateTimeOffset.UtcNow
                });
            }

            return resultado;
        }

        // ------------------------------------------------------------
        // Aba "Configuração"
        // ------------------------------------------------------------
        var configCaptura = CriarPainelConfigFiltro("Eventos a capturar", marcarPadraoStandard: true);
        configCaptura.Painel.Location = new Point(0, 40);

        var lblConfigTitulo = new Label { Text = "Configuração da captura", AutoSize = true, Location = new Point(0, 0), Font = fonteSecao, ForeColor = CorTitulo };
        var lblConfigAviso = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 40 + AlturaPainelConfigFiltro + 10),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "A lista de eventos define o que a sessão de Extended Events captura no servidor (aba " +
                   "\"Executar\") — os demais filtros só afetam o que aparece na grade, e podem ser ajustados a " +
                   "qualquer momento, inclusive com a captura já em andamento."
        };

        var tabConfiguracao = new TabPage("Configuração") { AutoScroll = true };
        tabConfiguracao.Controls.AddRange(new Control[] { lblConfigTitulo, configCaptura.Painel, lblConfigAviso });

        // ------------------------------------------------------------
        // Aba "Executar"
        // ------------------------------------------------------------
        var lblExecucaoTitulo = new Label { Text = "Captura ao vivo", AutoSize = true, Location = new Point(0, 0), Font = fonteSecao, ForeColor = CorTitulo };

        var btnIniciar = CriarBotaoAcao("Iniciar captura");
        btnIniciar.Location = new Point(0, 32);

        var btnParar = CriarBotaoAcao("Parar captura");
        btnParar.Location = new Point(170, 32);
        btnParar.Enabled = false;
        btnParar.BackColor = Color.FromArgb(178, 34, 34);

        var btnSalvarCsv = CriarBotaoAcao("Salvar como CSV");
        btnSalvarCsv.Location = new Point(340, 32);

        // Limpeza de sessões de Extended Events deixadas para trás no servidor.
        // Necessário porque, se o programa for encerrado de forma anormal
        // (queda de rede, Gerenciador de Tarefas, travamento) com uma captura
        // ativa, a sessão continua rodando no SERVIDOR consumindo ring buffer
        // indefinidamente — e, como o nome de cada sessão leva um GUID próprio,
        // a execução seguinte do app não tinha como encontrá-la para remover.
        var btnLimparSessoes = CriarBotaoAcao("Limpar sessões antigas");
        btnLimparSessoes.Location = new Point(510, 32);
        btnLimparSessoes.Size = new Size(190, 34);
        btnSalvarCsv.Enabled = false;

        var lblIntervaloExec = new Label { Text = "Atualização:", AutoSize = true, Location = new Point(540, 17), Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = CorTitulo };
        var cmbIntervaloExec = new ComboBox { Location = new Point(618, 13), Size = new Size(120, 26), DropDownStyle = ComboBoxStyle.DropDownList };
        cmbIntervaloExec.Items.AddRange(new object[] { "1 segundo", "2 segundos", "5 segundos", "10 segundos" });
        cmbIntervaloExec.SelectedItem = "2 segundos";

        var lblStatusExecucao = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Location = new Point(0, 74),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Nenhuma captura em andamento. Escolha os eventos na aba \"Configuração\" e clique em \"Iniciar captura\"."
        };

        var gridExecucao = new DataGridView
        {
            Location = new Point(0, 102),
            Size = new Size(1000, 760),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        ConfigurarColunasProfiler(gridExecucao);

        var tabExecutar = new TabPage("Executar") { AutoScroll = true };
        tabExecutar.Controls.AddRange(new Control[]
        {
            lblExecucaoTitulo, btnIniciar, btnParar, btnSalvarCsv, btnLimparSessoes, lblIntervaloExec, cmbIntervaloExec,
            lblStatusExecucao, gridExecucao
        });

        // ------------------------------------------------------------
        // Aba "Analisar arquivo"
        // ------------------------------------------------------------
        var lblAnalisarTitulo = new Label { Text = "Analisar arquivo já capturado", AutoSize = true, Location = new Point(0, 0), Font = fonteSecao, ForeColor = CorTitulo };
        var btnAbrirArquivo = CriarBotaoAcao("Abrir arquivo (.xel/.csv)");
        btnAbrirArquivo.Location = new Point(0, 32);
        btnAbrirArquivo.Size = new Size(230, 34);

        var lblStatusArquivo = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Location = new Point(240, 40),
            Font = fonteAviso,
            ForeColor = CorTextoSecundario,
            Text = "Nenhum arquivo carregado ainda."
        };

        var configAnalise = CriarPainelConfigFiltro("Eventos a exibir", marcarPadraoStandard: false);
        configAnalise.Painel.Location = new Point(0, 78);

        var btnAplicarFiltro = CriarBotaoAcao("Aplicar filtro");
        btnAplicarFiltro.Location = new Point(0, 78 + AlturaPainelConfigFiltro + 8);
        btnAplicarFiltro.Enabled = false;

        var gridAnalise = new DataGridView
        {
            Location = new Point(0, 78 + AlturaPainelConfigFiltro + 50),
            Size = new Size(1000, 480),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        ConfigurarColunasProfiler(gridAnalise);

        var tabAnalisar = new TabPage("Analisar arquivo") { AutoScroll = true };
        tabAnalisar.Controls.AddRange(new Control[]
        {
            lblAnalisarTitulo, btnAbrirArquivo, lblStatusArquivo, configAnalise.Painel, btnAplicarFiltro, gridAnalise
        });

        // Y calculado a partir de alturaExplicacaoProfiler (medida acima com
        // TextRenderer.MeasureText), não de lblExplicacaoProfiler.Bottom —
        // ver o comentário junto da medição para o motivo.
        var tabProfiler = new TabControl { Location = new Point(0, alturaExplicacaoProfiler + 12), Size = new Size(1000, 900) };
        tabProfiler.TabPages.Add(tabConfiguracao);
        tabProfiler.TabPages.Add(tabExecutar);
        tabProfiler.TabPages.Add(tabAnalisar);

        cartao.Controls.AddRange(new Control[] { lblExplicacaoProfiler, tabProfiler });
        DefinirConteudo(cartao);

        // "Colunas a exibir" de cada aba controla a grade correspondente —
        // aplicada uma vez ao montar a tela e de novo a cada clique
        // (ItemCheck dispara ANTES do item mudar de estado de verdade, daí
        // o BeginInvoke pra adiar a releitura pra depois do clique aplicado).
        AplicarVisibilidadeColunas(gridExecucao, configCaptura.ListaColunas);
        AplicarVisibilidadeColunas(gridAnalise, configAnalise.ListaColunas);
        configCaptura.ListaColunas.ItemCheck += (_, _) => BeginInvoke(new Action(() => AplicarVisibilidadeColunas(gridExecucao, configCaptura.ListaColunas)));
        configAnalise.ListaColunas.ItemCheck += (_, _) => BeginInvoke(new Action(() => AplicarVisibilidadeColunas(gridAnalise, configAnalise.ListaColunas)));

        // ------------------------------------------------------------
        // Aba "Executar" — lógica de captura ao vivo
        // ------------------------------------------------------------
        var eventosCapturados = new List<ProfilerEventoDto>();
        DateTimeOffset? ultimoTimestampCapturado = null;
        var coletandoTick = false;

        // Token desta página (ver _ctsPaginaAtiva) — copiado por VALOR, usado
        // SÓ na coleta periódica de eventos (leitura do ring buffer). As
        // demais chamadas do Profiler continuam com CancellationToken.None de
        // propósito: criar (IniciarSessaoAsync), parar (PararSessaoAsync) e
        // remover sessões órfãs mexem em um recurso do SERVIDOR e precisam
        // terminar mesmo que o usuário saia da tela — cancelar no meio
        // deixaria uma sessão de Extended Events órfã rodando lá.
        var tokenPagina = TokenPaginaAtiva;

        void AtualizarGridExecucao()
        {
            var filtro = MontarFiltro(
                configCaptura.ListaEventos, configCaptura.TxtApp, configCaptura.TxtBanco, configCaptura.TxtLogin,
                configCaptura.TxtHost, configCaptura.TxtTexto, configCaptura.NumDuracao, configCaptura.ChkSoUsuario, configCaptura.ChkExcluirProprio);
            gridExecucao.DataSource = null;
            gridExecucao.DataSource = Modulo_Profiler.FiltrarEventos(eventosCapturados, filtro);
            btnSalvarCsv.Enabled = eventosCapturados.Count > 0;
        }

        async Task ColetarTickAsync()
        {
            if (coletandoTick) return;
            coletandoTick = true;
            try
            {
                var nomeServidor = _conectarSql.ConfiguracaoAtual?.ServerName;
                var (novos, ultimo) = await _moduloProfiler.ColetarEventosAsync(ultimoTimestampCapturado, null, nomeServidor, tokenPagina);
                if (tokenPagina.IsCancellationRequested || lblStatusExecucao.IsDisposed)
                {
                    return;
                }

                if (novos.Count > 0)
                {
                    eventosCapturados.AddRange(novos);
                    // Limite de segurança pra não crescer sem fim numa
                    // captura longa/barulhenta — mantém só os 20.000 mais
                    // recentes em memória (o ring buffer no servidor já tem
                    // um limite parecido).
                    if (eventosCapturados.Count > 20000)
                    {
                        eventosCapturados.RemoveRange(0, eventosCapturados.Count - 20000);
                    }
                    ultimoTimestampCapturado = ultimo;
                    AtualizarGridExecucao();
                }
                lblStatusExecucao.ForeColor = CorTextoSecundario;
                lblStatusExecucao.Text = $"Capturando... {eventosCapturados.Count} evento(s) — última atualização às {DateTime.Now:HH:mm:ss}.";
            }
            catch (OperationCanceledException)
            {
                // Usuário saiu da página no meio da coleta — esperado, não é erro.
            }
            catch (Exception ex)
            {
                if (tokenPagina.IsCancellationRequested || lblStatusExecucao.IsDisposed)
                {
                    return;
                }

                lblStatusExecucao.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatusExecucao.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                coletandoTick = false;
            }
        }

        int ObterIntervaloExecMs(string? opcao) => opcao switch
        {
            "1 segundo" => 1000,
            "2 segundos" => 2000,
            "5 segundos" => 5000,
            "10 segundos" => 10000,
            _ => 2000
        };

        btnLimparSessoes.Click += async (_, _) =>
        {
            btnLimparSessoes.Enabled = false;
            try
            {
                var orfas = await _moduloProfiler.ListarSessoesOrfasAsync();
                if (orfas.Count == 0)
                {
                    MessageBox.Show(this,
                        "Nenhuma sessão de captura antiga do SQL Rocket foi encontrada no servidor.",
                        "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var confirmacao = MessageBox.Show(this,
                    $"Foram encontradas {orfas.Count} sessão(ões) de captura antigas do SQL Rocket ainda ativas no " +
                    "servidor (sobra de execuções encerradas de forma anormal). Elas continuam consumindo memória do " +
                    $"servidor até serem removidas.\n\n{string.Join(Environment.NewLine, orfas)}\n\n" +
                    "Deseja removê-las agora?",
                    "SQL Rocket — sessões de captura antigas",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirmacao != DialogResult.Yes)
                {
                    return;
                }

                await _moduloProfiler.RemoverSessoesOrfasAsync(orfas);
                lblStatusExecucao.Text = $"{orfas.Count} sessão(ões) antiga(s) removida(s) do servidor.";
                MessageBox.Show(this, $"{orfas.Count} sessão(ões) removida(s) com sucesso.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnLimparSessoes.Enabled = true;
            }
        };

        btnIniciar.Click += async (_, _) =>
        {
            var eventosSelecionados = ObterEventosMarcados(configCaptura.ListaEventos);
            if (eventosSelecionados.Count == 0)
            {
                MessageBox.Show(this, "Selecione ao menos um evento na aba \"Configuração\" antes de iniciar a captura.",
                    "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnIniciar.Enabled = false;
            try
            {
                Cursor = Cursors.WaitCursor;
                lblStatusExecucao.ForeColor = CorTextoSecundario;
                lblStatusExecucao.Text = "Iniciando a sessão de captura no servidor...";

                await _moduloProfiler.IniciarSessaoAsync(eventosSelecionados, CancellationToken.None);

                eventosCapturados.Clear();
                ultimoTimestampCapturado = null;
                AtualizarGridExecucao();

                var timerExecucao = new System.Windows.Forms.Timer { Interval = ObterIntervaloExecMs(cmbIntervaloExec.SelectedItem as string) };
                timerExecucao.Tick += (_, _) => _ = ColetarTickAsync();
                _timerPaginaAtiva = timerExecucao;
                _limpezaAssincronaPaginaAtiva = () => _moduloProfiler.PararSessaoAsync();
                timerExecucao.Start();

                btnParar.Enabled = true;
                lblStatusExecucao.Text = "Captura iniciada — aguardando eventos...";

                _ = ColetarTickAsync();
            }
            catch (Exception ex)
            {
                btnIniciar.Enabled = true;
                lblStatusExecucao.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatusExecucao.Text = "Falha ao iniciar a captura.";
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        };

        btnParar.Click += async (_, _) =>
        {
            btnParar.Enabled = false;
            try
            {
                Cursor = Cursors.WaitCursor;
                if (_timerPaginaAtiva is not null)
                {
                    _timerPaginaAtiva.Stop();
                    _timerPaginaAtiva.Dispose();
                    _timerPaginaAtiva = null;
                }
                _limpezaAssincronaPaginaAtiva = null;
                await _moduloProfiler.PararSessaoAsync(CancellationToken.None);

                lblStatusExecucao.ForeColor = CorTextoSecundario;
                lblStatusExecucao.Text = $"Captura parada. {eventosCapturados.Count} evento(s) capturado(s) — use \"Salvar como CSV\" para exportar.";
            }
            catch (Exception ex)
            {
                lblStatusExecucao.ForeColor = Color.FromArgb(196, 55, 55);
                lblStatusExecucao.Text = ObterMensagemAmigavel(ex);
            }
            finally
            {
                btnIniciar.Enabled = true;
                Cursor = Cursors.Default;
            }
        };

        btnSalvarCsv.Click += (_, _) => SalvarComoCsv(eventosCapturados, $"Profiler_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        void RefiltrarExecucao(object? sender, EventArgs e) => AtualizarGridExecucao();
        configCaptura.TxtApp.TextChanged += RefiltrarExecucao;
        configCaptura.TxtBanco.TextChanged += RefiltrarExecucao;
        configCaptura.TxtLogin.TextChanged += RefiltrarExecucao;
        configCaptura.TxtHost.TextChanged += RefiltrarExecucao;
        configCaptura.TxtTexto.TextChanged += RefiltrarExecucao;
        configCaptura.NumDuracao.ValueChanged += RefiltrarExecucao;
        configCaptura.ChkSoUsuario.CheckedChanged += RefiltrarExecucao;
        configCaptura.ChkExcluirProprio.CheckedChanged += RefiltrarExecucao;
        // ItemCheck dispara ANTES do item realmente mudar de estado (o novo
        // valor só está em e.NewValue) — BeginInvoke adia a releitura da
        // lista pra depois do clique já ter sido aplicado de verdade.
        configCaptura.ListaEventos.ItemCheck += (_, _) => BeginInvoke(new Action(AtualizarGridExecucao));

        // ------------------------------------------------------------
        // Aba "Analisar arquivo" — lógica
        // ------------------------------------------------------------
        var eventosArquivoCarregado = new List<ProfilerEventoDto>();

        void AtualizarGridAnalise()
        {
            var filtro = MontarFiltro(
                configAnalise.ListaEventos, configAnalise.TxtApp, configAnalise.TxtBanco, configAnalise.TxtLogin,
                configAnalise.TxtHost, configAnalise.TxtTexto, configAnalise.NumDuracao, configAnalise.ChkSoUsuario, configAnalise.ChkExcluirProprio);
            gridAnalise.DataSource = null;
            gridAnalise.DataSource = Modulo_Profiler.FiltrarEventos(eventosArquivoCarregado, filtro);
        }

        btnAbrirArquivo.Click += async (_, _) =>
        {
            using var dialogo = new OpenFileDialog
            {
                Title = "Selecionar arquivo do Profiler",
                Filter = "Todos os formatos suportados (*.xel;*.csv)|*.xel;*.csv|" +
                         "Arquivos de Extended Events (*.xel)|*.xel|" +
                         "Arquivos CSV exportados (*.csv)|*.csv|Todos os arquivos (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialogo.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var nomeArquivo = Path.GetFileName(dialogo.FileName);
            try
            {
                Cursor = Cursors.WaitCursor;
                lblStatusArquivo.ForeColor = CorTextoSecundario;
                lblStatusArquivo.Text = $"Lendo \"{nomeArquivo}\"...";

                // .trc precisa de um caso explícito: sem ele, cai no "_"
                // (tratado como .xel) e o XELite tenta ler o binário do
                // .trc como se fosse .xel, falhando com uma mensagem
                // confusa ("File is missing magic bits") que não explica o
                // motivo real (não é arquivo incompleto — é formato
                // diferente, sem suporte nenhum aqui, ver csproj/doc do
                // Modulo_Profiler). Detectado assim, o motivo real aparece
                // pro usuário em vez do erro genérico de .xel.
                var extensao = Path.GetExtension(dialogo.FileName);
                if (string.Equals(extensao, ".trc", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Arquivos .trc (formato binário do SQL Server Profiler/SQL Trace clássico) não são " +
                        "suportados por esta tela. Não existe nenhuma biblioteca oficial da Microsoft para ler " +
                        "esse formato de forma offline num app .NET moderno como este (confirmado depois de " +
                        "tentar implementar isso numa rodada anterior — ver o comentário em " +
                        "FerramentasDBA.Classes.csproj para o detalhe técnico). Só .xel (Extended Events) e .csv " +
                        "(exportado por esta mesma tela) são suportados.");
                }

                eventosArquivoCarregado = extensao.ToLowerInvariant() switch
                {
                    ".csv" => LerCsv(dialogo.FileName),
                    _ => await _moduloProfiler.AnalisarArquivoXelAsync(dialogo.FileName, null, CancellationToken.None)
                };

                AtualizarGridAnalise();
                btnAplicarFiltro.Enabled = true;

                // A leitura de .xel tem um teto de eventos para um arquivo muito
                // grande não derrubar o programa por falta de memória. Quando o
                // teto é atingido o usuário PRECISA saber que está vendo só o
                // começo do arquivo — caso contrário concluiria coisas erradas a
                // partir de um conjunto truncado sem perceber.
                if (_moduloProfiler.UltimaLeituraAtingiuLimite)
                {
                    lblStatusArquivo.ForeColor = Color.FromArgb(178, 34, 34);
                    lblStatusArquivo.Text =
                        $"ATENÇÃO: arquivo truncado — foram carregados os {eventosArquivoCarregado.Count} primeiros " +
                        $"eventos de \"{nomeArquivo}\", que tem MAIS eventos do que isso.";
                    MessageBox.Show(this,
                        $"Este arquivo tem mais de {Modulo_Profiler.MaximoEventosArquivo:N0} eventos.\n\n" +
                        $"Foram carregados apenas os {eventosArquivoCarregado.Count:N0} primeiros — o restante NÃO está " +
                        "na grade. Para analisar o arquivo inteiro, gere capturas menores (filtre os eventos na " +
                        "sessão, ou use arquivos rotativos menores).",
                        "SQL Rocket — arquivo truncado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    lblStatusArquivo.ForeColor = CorTextoSecundario;
                    lblStatusArquivo.Text = $"{eventosArquivoCarregado.Count} evento(s) carregado(s) de \"{nomeArquivo}\".";
                }
            }
            catch (Exception ex)
            {
                lblStatusArquivo.ForeColor = Color.FromArgb(178, 34, 34);
                lblStatusArquivo.Text = $"Falha ao ler \"{nomeArquivo}\".";
                MessageBox.Show(this, ObterMensagemAmigavel(ex), "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        };

        btnAplicarFiltro.Click += (_, _) => AtualizarGridAnalise();
    }

    // ----------------------------------------------------------------
    // Auxiliares visuais
    // ----------------------------------------------------------------

    /// <summary>
    /// Guarda a posição de rolagem e a linha selecionada de uma grade ANTES
    /// de trocar o DataSource — par de <see cref="RestaurarEstadoGrade"/>.
    ///
    /// BUG DE PRODUÇÃO nas telas de Active Monitor (Local e Azure): elas
    /// refazem "grid.DataSource = filtrado.ToList()" a cada tick do timer
    /// (1 em 1 segundo), e essas duas grades são as únicas do app que NÃO
    /// usam AutoGenerateColumns = false — ou seja, as colunas são
    /// regeneradas do zero a cada segundo e a grade volta para o topo, sem
    /// nenhuma linha selecionada, exatamente enquanto o usuário tenta ler ou
    /// clicar em um processo. Resultado prático: era impossível rolar a lista
    /// ou manter um processo selecionado com a atualização automática ligada.
    ///
    /// A linha é identificada por uma CHAVE ESTÁVEL do próprio registro
    /// (SESSION ID no Local, SPID no Azure) e não pelo índice da linha — o
    /// conteúdo e a ordem da lista mudam entre um tick e outro, então o
    /// índice não significa a mesma sessão.
    ///
    /// (Passo futuro, separado: declarar explicitamente todas as colunas
    /// dessas duas grades e ligar AutoGenerateColumns = false, eliminando a
    /// regeneração de colunas. Não foi feito aqui porque exige listar
    /// coluna por coluna dos dois DTOs — esquecer uma apagaria a coluna da
    /// tela silenciosamente.)
    /// </summary>
    private static (int? chaveSelecionada, int primeiraLinhaVisivel) CapturarEstadoGrade(
        DataGridView grade, Func<object, int?> obterChave)
    {
        int? chave = grade.CurrentRow?.DataBoundItem is { } item ? obterChave(item) : null;

        // Devolve -1 quando a grade está vazia — tratado em RestaurarEstadoGrade.
        var primeiraLinhaVisivel = grade.FirstDisplayedScrollingRowIndex;

        return (chave, primeiraLinhaVisivel);
    }

    /// <summary>
    /// Devolve a seleção e a rolagem capturadas por
    /// <see cref="CapturarEstadoGrade"/> depois da troca de DataSource.
    /// A seleção é restaurada ANTES da rolagem de propósito: atribuir
    /// CurrentCell rola a grade sozinho para deixar a célula visível, então
    /// fazer o contrário desfaria a posição recém-restaurada.
    /// </summary>
    private static void RestaurarEstadoGrade(
        DataGridView grade, Func<object, int?> obterChave, int? chaveSelecionada, int primeiraLinhaVisivel)
    {
        if (grade.Rows.Count == 0)
        {
            return;
        }

        if (chaveSelecionada is not null)
        {
            foreach (DataGridViewRow linha in grade.Rows)
            {
                if (linha.DataBoundItem is not { } item || obterChave(item) != chaveSelecionada)
                {
                    continue;
                }

                // Precisa ser uma célula VISÍVEL: atribuir CurrentCell de uma
                // coluna oculta lança InvalidOperationException.
                var celula = linha.Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.Visible);
                if (celula is not null)
                {
                    try
                    {
                        grade.CurrentCell = celula;
                    }
                    catch (InvalidOperationException)
                    {
                        // Grade em estado que não aceita mudar a célula atual
                        // agora — perder a seleção é aceitável, travar não.
                    }
                }

                break;
            }
        }

        // FirstDisplayedScrollingRowIndex lança se receber um índice fora do
        // intervalo da lista ATUAL (que muda a cada atualização) — por isso o
        // clamp e o try/catch.
        if (primeiraLinhaVisivel >= 0)
        {
            try
            {
                grade.FirstDisplayedScrollingRowIndex = Math.Min(primeiraLinhaVisivel, grade.Rows.Count - 1);
            }
            catch (Exception)
            {
                // Idem acima: rolagem é conforto visual, nunca motivo de erro.
            }
        }
    }

    // AutoScroll = true: quando o conteúdo do cartão (ex: formulário + painel
    // de mensagens de Backup/Restore) é maior que a área visível, o próprio
    // cartão ganha uma barra de rolagem em vez de cortar o conteúdo — assim
    // dá sempre para rolar até o fim, mesmo sem redimensionar/maximizar a
    // janela. Não afeta as páginas que usam um único filho Dock=Fill (grid,
    // PropertyGrid etc.), pois esse filho nunca ultrapassa a área visível.
    private static Panel CriarCartao() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.White,
        Padding = new Padding(20),
        BorderStyle = BorderStyle.FixedSingle,
        AutoScroll = true
    };

    private static Button CriarBotaoAcao(string texto) => new()
    {
        Text = texto,
        Width = 160,
        Height = 34,
        Margin = new Padding(0),
        FlatStyle = FlatStyle.Flat,
        BackColor = CorAccent,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        Cursor = Cursors.Hand
    };

    /// <summary>
    /// Painel de resumo usado nas telas de "Comparador" (Índices e Banco de
    /// dados), posicionado no topo ao lado da escolha de Origem/Destino —
    /// mostra de forma destacada se a comparação deu tudo igual ou quantas
    /// diferenças/ausências foram encontradas, em vez de só o texto
    /// pequeno em itálico do rodapé (que continua existindo para as
    /// mensagens de "Comparando..."/"Aplicado(s)..."/erro). Cada tela
    /// chama <see cref="AtualizarResumoComparacao"/> depois de comparar e
    /// <see cref="LimparResumoComparacaoAposFalha"/> se a comparação falhar.
    /// </summary>
    private static (Panel Painel, Label Total, Label Iguais, Label Diferentes, Label Ausentes) CriarPainelResumoComparacao()
    {
        var painel = new Panel
        {
            Location = new Point(660, 0),
            Size = new Size(380, 170),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White
        };

        var lblTitulo = new Label
        {
            Text = "Resultado da comparação",
            AutoSize = true,
            Location = new Point(14, 12),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = CorTitulo
        };

        var lblTotal = new Label
        {
            Text = "Clique em \"Comparar\" para ver o resultado.",
            AutoSize = true,
            MaximumSize = new Size(350, 0),
            Location = new Point(14, 36),
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = CorTextoSecundario
        };

        var lblIguais = new Label { AutoSize = true, Location = new Point(14, 78), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = Color.FromArgb(30, 120, 60) };
        var lblDiferentes = new Label { AutoSize = true, Location = new Point(14, 102), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = Color.FromArgb(140, 80, 0) };
        var lblAusentes = new Label { AutoSize = true, Location = new Point(14, 126), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = Color.FromArgb(140, 20, 20) };

        painel.Controls.Add(lblTitulo);
        painel.Controls.Add(lblTotal);
        painel.Controls.Add(lblIguais);
        painel.Controls.Add(lblDiferentes);
        painel.Controls.Add(lblAusentes);

        return (painel, lblTotal, lblIguais, lblDiferentes, lblAusentes);
    }

    /// <summary>Preenche o painel de <see cref="CriarPainelResumoComparacao"/> depois de uma comparação bem-sucedida.</summary>
    private static void AtualizarResumoComparacao(
        Label lblTotal, Label lblIguais, Label lblDiferentes, Label lblAusentes,
        int total, int iguais, int diferentes, int ausentes, string nomeItemPlural)
    {
        if (total == 0)
        {
            lblTotal.Text = $"Nenhum(a) {nomeItemPlural} encontrado(a).";
            lblTotal.ForeColor = CorTextoSecundario;
        }
        else if (diferentes == 0 && ausentes == 0)
        {
            lblTotal.Text = "Tudo igual!";
            lblTotal.ForeColor = Color.FromArgb(30, 120, 60);
        }
        else
        {
            lblTotal.Text = $"{diferentes + ausentes} diferença(s) em {total} {nomeItemPlural}";
            lblTotal.ForeColor = CorTitulo;
        }

        lblIguais.Text = $"{iguais} igual(is)";
        lblDiferentes.Text = $"{diferentes} diferente(s)";
        lblAusentes.Text = $"{ausentes} ausente(s)";
    }

    /// <summary>Limpa os contadores e destaca em vermelho o painel de resumo depois de uma comparação que falhou (a mensagem de erro em si já aparece num MessageBox à parte).</summary>
    private static void LimparResumoComparacaoAposFalha(Label lblTotal, Label lblIguais, Label lblDiferentes, Label lblAusentes)
    {
        lblTotal.Text = "Falha ao comparar.";
        lblTotal.ForeColor = Color.FromArgb(140, 20, 20);
        lblIguais.Text = string.Empty;
        lblDiferentes.Text = string.Empty;
        lblAusentes.Text = string.Empty;
    }

    private static Panel CriarPainelMensagem(string mensagem)
    {
        var cartao = CriarCartao();

        // AutoSize + MaximumSize com altura 0 (largura fixa, altura livre) faz
        // o Label crescer verticalmente conforme o texto — combinado com
        // AutoScroll = true no cartão (CriarCartao), um texto mais longo do
        // que a janela sempre pode ser rolado até o fim, em vez de cortado.
        // Marca "SQL Rocket" (pedido do usuário: "preencher toda a tela com
        // a imagem"): a tela de boas-vindas é PREENCHIDA com a imagem do
        // foguete atrás do texto (versão anterior mostrava um logo pequeno
        // ABAIXO do texto, num tamanho fixo — ver "Rodada anterior" no doc
        // do projeto). PictureBoxSizeMode.Zoom/ImageLayout.Zoom deixariam
        // faixas em branco nas bordas (a imagem é bem mais alta que larga,
        // e a tela normalmente é bem mais larga que alta, já que Menu_Raiz
        // abre maximizada) — e Stretch preencheria tudo mas distorceria a
        // imagem. Por isso o desenho é feito manualmente no Paint do cartão,
        // em modo "cover" (ver DesenharImagemCover) — mesma técnica de
        // desenho customizado (GDI+ puro) já usada em
        // GraficoFaixa/PlanoExecucaoDiagrama.
        // A imagem é compartilhada por todo o app e o resultado redimensionado
        // fica em cache (ver _fundoBoasVindas/_fundoBoasVindasEscalado) — antes
        // cada montagem desta página carregava um bitmap novo de ~8 MB.
        cartao.Paint += (_, e) => DesenharFundoBoasVindas(e.Graphics, cartao.ClientSize, cartao.BackColor);
        // Panel não redesenha sozinho ao redimensionar — sem isso a imagem
        // ficaria "congelada" no tamanho do primeiro desenho ao
        // maximizar/restaurar a janela.
        cartao.Resize += (_, _) => cartao.Invalidate();

        // AutoSize + MaximumSize com altura 0 (largura fixa, altura livre) faz
        // o Label crescer verticalmente conforme o texto — combinado com
        // AutoScroll = true no cartão (CriarCartao), um texto mais longo do
        // que a janela sempre pode ser rolado até o fim, em vez de cortado.
        // BackColor branco (em vez de transparente) garante que o texto
        // continua legível mesmo com a imagem de fundo bem atrás dele.
        var lbl = new Label
        {
            Text = mensagem,
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Font = new Font("Segoe UI", 10F),
            ForeColor = Color.FromArgb(80, 90, 110),
            BackColor = Color.White,
            TextAlign = ContentAlignment.TopLeft,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        cartao.Controls.Add(lbl);

        return cartao;
    }

    /// <summary>
    /// Desenha uma imagem preenchendo TODA a área informada, preservando a
    /// proporção original (nunca distorce) — equivalente ao
    /// <c>background-size: cover</c> do CSS: a imagem é ampliada pela MAIOR
    /// das duas razões (largura/altura) até cobrir a área inteira,
    /// centralizada, cortando o excesso que sobrar de um dos lados (fora da
    /// área visível). Diferente de <see cref="PictureBoxSizeMode.Zoom"/>/
    /// <see cref="ImageLayout.Zoom"/> (preservam a proporção mas podem
    /// deixar faixas em branco) e de <c>Stretch</c> (preenche tudo mas
    /// distorce a proporção). Usada na tela de boas-vindas — ver
    /// <see cref="CriarPainelMensagem"/>.
    /// </summary>
    private static void DesenharImagemCover(Graphics g, Size area, Image imagem)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        var escala = Math.Max(area.Width / (float)imagem.Width, area.Height / (float)imagem.Height);
        var largura = imagem.Width * escala;
        var altura = imagem.Height * escala;
        var x = (area.Width - largura) / 2f;
        var y = (area.Height - altura) / 2f;

        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(imagem, x, y, largura, altura);
    }

    /// <summary>
    /// Desenha o fundo da tela de boas-vindas em modo "cover" (ver
    /// <see cref="DesenharImagemCover"/>) reaproveitando um cache do resultado
    /// JÁ redimensionado para <paramref name="area"/> — ver
    /// <see cref="_fundoBoasVindasEscalado"/>. O cache é refeito apenas quando
    /// o tamanho da área muda (maximizar/restaurar/arrastar a borda da
    /// janela); nos demais Paints o desenho é uma cópia 1:1, sem
    /// redimensionamento. Como o cache é pintado antes com
    /// <paramref name="corFundo"/> (a mesma cor que o próprio cartão pintaria
    /// atrás da imagem), o resultado visual é exatamente o de antes.
    /// </summary>
    private static void DesenharFundoBoasVindas(Graphics g, Size area, Color corFundo)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        var cache = _fundoBoasVindasEscalado;
        if (cache is null || cache.Width != area.Width || cache.Height != area.Height)
        {
            // Monta o novo cache ANTES de descartar o antigo: se o
            // redimensionamento falhar (ex.: memória), a tela continua com a
            // imagem anterior em vez de ficar sem fundo nenhum.
            var novo = new Bitmap(area.Width, area.Height);
            using (var gCache = Graphics.FromImage(novo))
            {
                gCache.Clear(corFundo);
                DesenharImagemCover(gCache, area, FundoBoasVindas);
            }

            cache?.Dispose();
            cache = novo;
            _fundoBoasVindasEscalado = novo;
        }

        g.DrawImageUnscaled(cache, 0, 0);
    }

    /// <summary>"Badge" estilo shields.io (usado na página Sobre) — um rótulo colorido com texto curto.</summary>
    private static Label CriarBadge(string texto, Color cor) => new()
    {
        Text = texto,
        AutoSize = true,
        Padding = new Padding(8, 3, 8, 3),
        Margin = new Padding(0, 0, 8, 0),
        BackColor = cor,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 8F, FontStyle.Bold)
    };

    /// <summary>Título de seção estilo README (## Seção) — usado na página Sobre.</summary>
    private static Label CriarTituloSecaoSobre(string texto) => new()
    {
        Text = texto,
        AutoSize = true,
        Font = new Font("Segoe UI", 13F, FontStyle.Bold),
        ForeColor = CorTitulo,
        Margin = new Padding(0, 4, 0, 8)
    };

    /// <summary>Corpo de seção estilo README (lista com marcadores) — usado na página Sobre.</summary>
    private static Label CriarTextoSecaoSobre(string texto) => new()
    {
        Text = texto,
        AutoSize = true,
        MaximumSize = new Size(700, 0),
        Font = new Font("Segoe UI", 10F),
        ForeColor = Color.FromArgb(60, 70, 90),
        Margin = new Padding(0, 0, 0, 22)
    };

    private void DefinirConteudo(Control conteudo)
    {
        // Controls.Clear() remove os controles da página anterior da árvore
        // visual, mas NÃO os descarta nem para Timers "soltos" dentro deles
        // — sem isso, a página de Active Monitor (que usa um
        // System.Windows.Forms.Timer para atualização automática) ficaria
        // consultando o banco em segundo plano indefinidamente, mesmo depois
        // do usuário navegar para outra tela do menu.
        if (_timerPaginaAtiva is not null)
        {
            _timerPaginaAtiva.Stop();
            _timerPaginaAtiva.Dispose();
            _timerPaginaAtiva = null;
        }

        // Mesma ideia do Timer acima, mas para um recurso do SERVIDOR (ver
        // XML doc do campo) — disparada em "melhor esforço" (sem esperar o
        // resultado) para não travar a troca de página enquanto uma sessão
        // de Extended Events é removida do servidor.
        if (_limpezaAssincronaPaginaAtiva is not null)
        {
            var limpeza = _limpezaAssincronaPaginaAtiva;
            _limpezaAssincronaPaginaAtiva = null;
            _ = limpeza();
        }

        // ATENÇÃO — Controls.Clear() DESLIGA os controles da página anterior
        // da árvore visual mas NÃO os descarta: cada página cria de 3 a 15
        // Fonts próprias, mais todos os controles/grades, e nada disso é
        // liberado até o GC coletar. O Dispose() do conteúdo que sai NÃO foi
        // adicionado aqui de propósito, e isso é uma dívida CONHECIDA, não um
        // esquecimento:
        //
        // Este arquivo tem ~140 "await" em continuações que escrevem direto em
        // controles da página (grades, labels de status, combos). Descartar a
        // página anterior enquanto qualquer uma dessas continuações ainda está
        // a caminho troca um vazamento silencioso por ObjectDisposedException
        // em produção — e, no pior caso, a exceção estoura DE DENTRO do próprio
        // catch, porque vários deles reportam o erro escrevendo justamente no
        // label de status que acabou de ser descartado.
        //
        // O que já foi feito nesta rodada (pré-requisito do Dispose): o token
        // de página (_ctsPaginaAtiva) cancela as consultas ao navegar, e as
        // continuações das telas de leitura já saem sem tocar em nada quando
        // "token.IsCancellationRequested || controle.IsDisposed". Para o
        // Dispose ficar seguro falta estender essa mesma proteção a TODAS as
        // continuações do arquivo (inclusive as das telas destrutivas, que de
        // propósito NÃO recebem o token e portanto continuam podendo voltar
        // depois da troca de página). Enquanto isso não for feito, vazar
        // memória é preferível a derrubar o app no meio de um restore.
        pnlCorpoConteudo.Controls.Clear();
        conteudo.Dock = DockStyle.Fill;
        pnlCorpoConteudo.Controls.Add(conteudo);
    }

    /// <summary>
    /// Preenche o combo "Banco de Dados" de uma página com a lista de bancos
    /// da instância conectada.
    ///
    /// Existiam SEIS cópias byte a byte desta função (uma função local
    /// <c>CarregarBancosAsync</c> dentro de cada construtor de página:
    /// Fragmentação, Índices Nunca Utilizados, Índices Sugeridos, Índices Mais
    /// Utilizados, Top 25 Consultas e Limpeza de Arquivos), cada uma com o
    /// próprio <c>catch</c> vazio. Consequência real: um login sem a permissão
    /// VIEW ANY DATABASE (ou qualquer outra falha na consulta) fazia o combo
    /// "Banco" aparecer VAZIO em seis telas diferentes sem nenhuma explicação,
    /// e arrumar isso exigia mexer em seis lugares. Agora é um lugar só.
    /// </summary>
    /// <param name="combo">
    /// Combo a preencher. Em caso de falha ele continua editável (dá para
    /// digitar o nome do banco na mão), então a falha nunca BLOQUEIA a tela —
    /// a correção aqui é só explicar por que a lista está vazia.
    /// </param>
    /// <param name="aoFalhar">
    /// Como esta página reporta erro — normalmente escrever no label de status
    /// dela. Quando null, cai no MessageBox padrão do arquivo.
    /// </param>
    /// <param name="ct">
    /// Token da página (ver <see cref="_ctsPaginaAtiva"/>). Consulta de
    /// LEITURA, portanto pode (e deve) ser abortada quando o usuário sai da
    /// tela.
    /// </param>
    private async Task PreencherComboBancosAsync(ComboBox combo, Action<string>? aoFalhar = null, CancellationToken ct = default)
    {
        try
        {
            var bancos = await _moduloAdmin.ObterBancosDadosAsync(ct);

            // A página pode ter sido trocada enquanto a consulta rodava — aí
            // o combo já não está mais na tela e não há nada a preencher.
            if (ct.IsCancellationRequested || combo.IsDisposed)
            {
                return;
            }

            combo.Items.Clear();
            combo.Items.AddRange(bancos.ToArray());
        }
        catch (OperationCanceledException)
        {
            // Usuário saiu da página no meio da consulta: cancelamento é o
            // comportamento esperado, não um erro para mostrar a ele.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || combo.IsDisposed)
            {
                return;
            }

            var mensagem = "Não foi possível listar os bancos de dados (" + ObterMensagemAmigavel(ex) +
                ") — digite o nome do banco diretamente no campo \"Banco de Dados\" para continuar.";

            if (aoFalhar is not null)
            {
                aoFalhar(mensagem);
            }
            else
            {
                MessageBox.Show(this, mensagem, "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>
    /// SQL Server error 4901: ALTER TABLE ... ADD de uma coluna NOT NULL
    /// sem DEFAULT numa tabela que já tem linhas — situação esperada em
    /// "Comparador &gt; Banco de dados" quando a coluna de origem é NOT
    /// NULL (esta ferramenta não rastreia/gera DEFAULT, fora de escopo
    /// documentado desde a implementação do comparador). Traduzido à parte
    /// porque é o erro mais comum de "Aplicar" nessa tela.
    /// </summary>
    private const int SqlErroAddColumnNotNullSemDefault = 4901;

    /// <summary>
    /// Pedido do usuário (funcionário testou e reportou): as telas de
    /// "Backup" (Full e Restore) usam SaveFileDialog/OpenFileDialog do
    /// Windows para escolher os caminhos — essas janelas só enxergam os
    /// discos DO COMPUTADOR ONDE A FERRAMENTA ESTÁ RODANDO, mas o caminho
    /// escolhido vira o "TO DISK"/"FROM DISK"/"MOVE ... TO" do T-SQL, que é
    /// resolvido pelo SERVIÇO do SQL Server — quando a instância conectada
    /// é remota, o disco mostrado no diálogo não tem nada a ver com o
    /// disco onde o caminho de fato precisa existir.
    ///
    /// Retorna null quando a instância é local (situação em que o diálogo
    /// nativo do Windows já mostra o disco certo, sem nada a avisar) OU
    /// quando não foi possível determinar isso (falha ao consultar
    /// SERVERPROPERTY — nesse caso não bloqueia a tela, apenas não avisa).
    /// Quando a instância é remota, tenta complementar o aviso com a
    /// listagem real de unidades/espaço livre do SERVIDOR via
    /// Modulo_Admin.ObterUnidadesDiscoServidorAsync (xp_fixeddrives) — se
    /// essa consulta falhar (ex.: extended procedure desabilitada por
    /// política de segurança), o aviso ainda é mostrado, só sem a lista de
    /// discos, deixando isso explícito em vez de fingir que não há disco
    /// nenhum no servidor.
    /// </summary>
    private async Task<string?> ObterAvisoInstanciaRemotaAsync()
    {
        bool remota;
        try
        {
            remota = await _moduloAdmin.InstanciaEhRemotaAsync();
        }
        catch
        {
            return null;
        }

        if (!remota)
        {
            return null;
        }

        string resumoDiscos;
        try
        {
            var unidades = await _moduloAdmin.ObterUnidadesDiscoServidorAsync();
            resumoDiscos = unidades.Count > 0
                ? string.Join("\n", unidades.Select(u => $"  {u.Unidade}:\\  —  {u.LivreMb:N0} MB livres"))
                : "  (nenhuma unidade fixa retornada pelo servidor)";
        }
        catch (Exception ex)
        {
            resumoDiscos = $"  (não foi possível consultar os discos do servidor: {ObterMensagemAmigavel(ex)})";
        }

        return
            "Atenção: esta ferramenta está conectada a uma instância REMOTA do SQL Server.\n\n" +
            "Por isso a janela de seleção de arquivo NÃO vai abrir aqui: ela mostraria os discos DESTE " +
            "computador, não os do SERVIDOR, e o caminho final precisa existir no disco do servidor, não " +
            "aqui. Digite o caminho manualmente no campo de texto (ex.: \"D:\\Backups\\meubanco.bak\", " +
            "usando um disco/pasta que exista NO SERVIDOR, com base na lista abaixo). Ao clicar em " +
            "\"Executar\" o caminho informado é conferido de verdade no servidor antes de prosseguir.\n\n" +
            "Discos encontrados no servidor:\n" + resumoDiscos;
    }

    /// <summary>
    /// Pedido do usuário (funcionário testou): quando a instância é remota,
    /// os 4 botões de navegação de arquivo de "Backup"/"Restore" mostram o
    /// aviso de <see cref="ObterAvisoInstanciaRemotaAsync"/> e NÃO abrem o
    /// diálogo nativo do Windows — só o aviso, com o caminho a ser digitado
    /// manualmente no campo de texto. Histórico: uma primeira versão apenas
    /// retornava sem abrir nada, o que o funcionário reportou como "dá a
    /// mensagem mas não abre nada" (pareceu bug); a versão seguinte sempre
    /// abria o diálogo depois do aviso, mas o funcionário reportou que a
    /// janela ainda mostrava o disco local/rede mapeada (confuso, levava a
    /// escolher um caminho errado); o comportamento final, pedido
    /// explicitamente pelo usuário, é este: só o aviso, sem diálogo, quando a
    /// instância é remota. Em instância LOCAL o diálogo continua abrindo
    /// normalmente (nesse caso ele já mostra o disco certo), e esta função
    /// trata uma eventual falha de permissão do Windows ao abri-lo.
    /// </summary>
    private static void MostrarAvisoSemPermissao(IWin32Window owner, Exception ex)
    {
        MessageBox.Show(owner,
            "O usuário do Windows que está rodando esta ferramenta não tem permissão para acessar esse local " +
            $"({ex.GetType().Name}: {ex.Message}).\n\n" +
            "Tente escolher outra pasta, rodar a ferramenta com um usuário que tenha permissão de acesso a " +
            "esse disco/pasta, ou digitar o caminho manualmente no campo de texto.",
            "SQL Rocket — sem permissão", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string ObterMensagemAmigavel(Exception ex) => ex switch
    {
        NotImplementedException => "Esta funcionalidade ainda não foi implementada nesta versão.",
        SqlException sqlEx when sqlEx.Number == SqlErroAddColumnNotNullSemDefault =>
            "Não foi possível adicionar a coluna: ela é NOT NULL na origem, mas a tabela de destino já tem " +
            "linhas — nesse caso o SQL Server exige que a coluna nova tenha um valor padrão (DEFAULT), algo que " +
            "o Comparador de Banco de Dados não gerencia (fora do escopo desta ferramenta). Para aplicar essa " +
            "coluna: crie-a manualmente como NULL no destino, preencha os dados existentes e só depois altere " +
            "para NOT NULL — ou adicione um DEFAULT a ela antes de tentar aplicar de novo.\n\n" +
            $"Mensagem original do SQL Server: {sqlEx.Message}",
        _ => ex.Message
    };
}
