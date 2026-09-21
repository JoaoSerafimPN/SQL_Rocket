using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.XEvent.XELite;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// O "Profiler" (Manutenção &gt; Profiler) reproduz a grade clássica do SQL
/// Server Profiler, mas por baixo dos panos usa Extended Events (XE), não a
/// SQL Trace legada que o Profiler de verdade usa — a Microsoft descontinuou
/// a SQL Trace/sp_trace_create desde o SQL Server 2016 (sem suporte nenhum
/// no Azure SQL Database), então XE é a opção correta e moderna.
///
/// Cobre as 3 abas da tela: "Configuração" (só monta o <see cref="ProfilerFiltroDto"/>
/// e a lista de eventos, não usa nada deste módulo diretamente), "Executar"
/// (<see cref="IniciarSessaoAsync"/> cria e inicia uma sessão de XE dedicada
/// no servidor; <see cref="ColetarEventosAsync"/> é chamado periodicamente
/// por um Timer na tela para ler o que já foi capturado; <see cref="PararSessaoAsync"/>
/// para e remove a sessão) e "Analisar arquivo" (<see cref="AnalisarArquivoXelAsync"/>
/// lê um .xel já existente, sem precisar de conexão nenhuma).
///
/// DECISÃO DE PROJETO IMPORTANTE — todo o filtro (<see cref="ProfilerFiltroDto"/>)
/// é aplicado do lado do CLIENTE, nunca como predicado WHERE dentro da DDL
/// "CREATE EVENT SESSION": ver o comentário completo em ProfilerFiltroDto.
/// A sessão criada aqui SEMPRE captura os eventos selecionados por inteiro,
/// sem filtro nenhum no servidor — só a mesma lista fixa de 8 "actions"
/// (colunas) é anexada a cada evento, também sem variação por evento.
///
/// ATENÇÃO — RISCO: esta é a única funcionalidade desta ferramenta que
/// executa DDL ao vivo contra um servidor de produção (CREATE/ALTER/DROP
/// EVENT SESSION) e que não pôde ser compilada nem testada neste ambiente de
/// desenvolvimento (sem uma instância de SQL Server disponível) — a sintaxe
/// foi conferida com cuidado, mas qualquer divergência de versão/edição do
/// SQL Server só vai aparecer no primeiro uso real. Ver Modulo_Profiler.IniciarSessaoAsync.
/// </summary>
public sealed class Modulo_Profiler
{
    /// <summary>
    /// Lista fixa e curada dos eventos de Extended Events oferecidos na aba
    /// "Configuração" — cobre os eventos mais usados do template "Standard"
    /// do Profiler clássico. Deliberadamente NÃO inclui xml_deadlock_report
    /// (já coberto pela tela "Headblock e DeadLock", que já sabe interpretar
    /// esse evento em detalhe — duplicar aqui só adicionaria complexidade
    /// sem um benefício real).
    ///
    /// <see cref="ProfilerEventoCatalogoItem.PadraoStandard"/> — a pedido do
    /// usuário, só "SQL:BatchCompleted" vem marcado por padrão (antes eram 5
    /// eventos, o subconjunto "Standard" do Profiler clássico); os demais
    /// continuam disponíveis na lista, só não pré-marcados.
    /// </summary>
    public static readonly IReadOnlyList<ProfilerEventoCatalogoItem> CatalogoEventos = new List<ProfilerEventoCatalogoItem>
    {
        new() { NomeEvento = "sql_batch_completed", NomeExibicao = "SQL:BatchCompleted", PadraoStandard = true },
        new() { NomeEvento = "rpc_completed", NomeExibicao = "RPC:Completed", PadraoStandard = false },
        new() { NomeEvento = "login", NomeExibicao = "Audit Login", PadraoStandard = false },
        new() { NomeEvento = "logout", NomeExibicao = "Audit Logout", PadraoStandard = false },
        new() { NomeEvento = "existing_connection", NomeExibicao = "Existing Connection", PadraoStandard = false },
        new() { NomeEvento = "rpc_starting", NomeExibicao = "RPC:Starting", PadraoStandard = false },
        new() { NomeEvento = "sql_batch_starting", NomeExibicao = "SQL:BatchStarting", PadraoStandard = false },
        new() { NomeEvento = "error_reported", NomeExibicao = "Exception", PadraoStandard = false },
        new() { NomeEvento = "attention", NomeExibicao = "Attention", PadraoStandard = false },
    };

    /// <summary>
    /// As 8 "actions" (colunas extras) do pacote "sqlserver", anexadas
    /// identicamente a TODO evento selecionado (sem variação por evento) —
    /// confirmadas como aplicáveis a qualquer evento deste catálogo.
    /// </summary>
    private static readonly string[] AcoesGlobais =
    {
        "sqlserver.client_app_name",
        "sqlserver.client_hostname",
        "sqlserver.client_pid",
        "sqlserver.database_id",
        "sqlserver.database_name",
        "sqlserver.nt_username",
        "sqlserver.server_principal_name",
        "sqlserver.session_id",
    };

    /// <summary>
    /// ApplicationName padrão usado pelo Microsoft.Data.SqlClient quando a
    /// connection string não define um explicitamente — é o caso de TODA
    /// conexão aberta por esta ferramenta (Conectar_SQL.ObterStringConexao
    /// não define "Application Name"). Usado por <see cref="ProfilerFiltroDto.ExcluirConsultasDoProprioApp"/>
    /// para reconhecer e ocultar da grade as próprias consultas desta
    /// ferramenta (ex.: o polling do próprio "Executar" lendo o ring
    /// buffer) — sem isso, a captura ao vivo entraria em "loop visual".
    /// </summary>
    // Antes era "Core Microsoft SqlClient Data Provider" — o Application Name
    // PADRÃO do driver, que é o mesmo de qualquer outra aplicação .NET que não
    // configure o seu. Resultado: com o filtro "esconder as consultas do
    // próprio app" ligado (o uso normal), a captura também escondia as
    // consultas dos OUTROS aplicativos .NET do cliente — justamente os que o
    // DBA estava tentando investigar. Agora a ferramenta se identifica com um
    // nome próprio na connection string (Conectar_SQL.NomeAplicacao) e o
    // filtro casa só com ela.
    public const string NomeAplicativoProprio = Conectar_SQL.NomeAplicacao;

    /// <summary>
    /// Teto de eventos lidos de um arquivo .xel numa única análise. Existe para
    /// um arquivo muito grande não derrubar o programa por falta de memória —
    /// cada evento carrega o texto completo do comando, então um .xel de alguns
    /// GB não cabe na memória do processo.
    /// </summary>
    public const int MaximoEventosArquivo = 50_000;

    private const string MensagemLimiteAtingido = "Limite de eventos por arquivo atingido.";

    /// <summary>
    /// Indica se a última chamada a <see cref="AnalisarArquivoXelAsync"/> parou
    /// no teto de <see cref="MaximoEventosArquivo"/> eventos — ou seja, o
    /// arquivo tem MAIS eventos do que os devolvidos. A tela usa isso para
    /// avisar o usuário de que está vendo só o começo do arquivo, em vez de
    /// deixá-lo concluir coisas erradas a partir de um conjunto truncado.
    /// </summary>
    public bool UltimaLeituraAtingiuLimite { get; private set; }

    /// <summary>
    /// Prefixo fixo do nome de toda sessão de XE criada por esta ferramenta
    /// (o sufixo é um Guid curto, único por execução) — é o que permite
    /// reconhecer, depois, sessões deixadas para trás por uma execução
    /// anterior (ver <see cref="ListarSessoesOrfasAsync"/>).
    /// </summary>
    public const string PrefixoNomeSessao = "FerramentasDBA_Profiler_";

    /// <summary>
    /// O mesmo <see cref="PrefixoNomeSessao"/>, mas já escrito como padrão
    /// de LIKE do T-SQL: "_" é curinga de UM caractere qualquer no LIKE, então
    /// precisa virar "[_]" para casar literalmente com o sublinhado do nome —
    /// sem isso o LIKE seria bem mais abrangente do que o pretendido e
    /// poderia alcançar sessões de terceiros com nome parecido.
    /// </summary>
    private const string PadraoLikeNomeSessao = "FerramentasDBA[_]Profiler[_]%";

    private readonly Conectar_SQL _conexao;

    public Modulo_Profiler(Conectar_SQL conexao)
    {
        _conexao = conexao;
    }

    /// <summary>Nome da sessão de XE atualmente em execução criada por esta instância (null quando nenhuma captura está ativa).</summary>
    public string? NomeSessaoAtual { get; private set; }

    /// <summary>
    /// Cria e inicia, no servidor conectado, uma sessão de Extended Events
    /// dedicada a esta captura (nome único por execução, ex.:
    /// "FerramentasDBA_Profiler_a1b2c3d4") com os eventos selecionados e as
    /// 8 actions fixas, gravando num ring buffer em memória (não em disco —
    /// simplicidade e sem exigir permissão de escrita em pasta do servidor;
    /// a limitação é que o ring buffer tem um tamanho máximo e descarta os
    /// eventos mais antigos quando cheio — aceitável para uma sessão de
    /// diagnóstico pontual, não para uma captura de longuíssima duração).
    /// STARTUP_STATE=OFF (não sobrevive a um restart do serviço do SQL
    /// Server) é proposital: uma sessão órfã "ressuscitando" sozinha depois
    /// de um restart seria pior do que simplesmente precisar iniciar de novo.
    /// </summary>
    public async Task IniciarSessaoAsync(IReadOnlyCollection<string> eventosSelecionados, CancellationToken ct = default)
    {
        if (eventosSelecionados is null || eventosSelecionados.Count == 0)
        {
            throw new InvalidOperationException("Selecione ao menos um evento para capturar na aba \"Configuração\".");
        }

        var nomesValidos = new HashSet<string>(CatalogoEventos.Select(e => e.NomeEvento), StringComparer.OrdinalIgnoreCase);
        var selecionadosValidos = eventosSelecionados
            .Where(e => nomesValidos.Contains(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selecionadosValidos.Count == 0)
        {
            throw new InvalidOperationException("Nenhum dos eventos selecionados é reconhecido.");
        }

        // Garante que não sobra uma sessão anterior desta mesma instância
        // "perdida" antes de criar uma nova (idempotente — não faz nada se
        // não houver nenhuma).
        await PararSessaoAsync(ct).ConfigureAwait(false);

        var nomeSessao = PrefixoNomeSessao + Guid.NewGuid().ToString("N")[..8];
        var ddlCriar = MontarDdlCriarSessao(nomeSessao, selecionadosValidos);

        await using var conexao = await _conexao.AbrirConexaoAsync(ct).ConfigureAwait(false);

        // Defensivo: a limpeza preventiva acima só enxerga uma sessão desta
        // MESMA instância (NomeSessaoAtual). Aqui o servidor é consultado de
        // verdade pelo nome exato que está prestes a ser criado — se uma
        // execução anterior tivesse deixado esse nome para trás, o CREATE
        // falharia com "already exists". O nome tem um Guid, então a colisão é
        // praticamente impossível, mas a checagem é barata e idempotente.
        await TentarRemoverSessaoAsync(conexao, nomeSessao, ct).ConfigureAwait(false);

        await using (var comandoCriar = conexao.CreateCommand())
        {
            comandoCriar.CommandText = ddlCriar;
            comandoCriar.CommandTimeout = 30;
            try
            {
                await comandoCriar.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    $"Falha ao criar a sessão de captura (Extended Events) no servidor. Detalhe do SQL Server: " +
                    $"\"{ex.Message}\". Causas mais prováveis: falta da permissão ALTER ANY EVENT SESSION para o " +
                    "login atual, ou uma edição/versão do SQL Server sem suporte a algum recurso usado por esta " +
                    "sessão. Script que falhou:\n" + ddlCriar, ex);
            }
        }

        await using (var comandoIniciar = conexao.CreateCommand())
        {
            comandoIniciar.CommandText = $"ALTER EVENT SESSION [{nomeSessao}] ON SERVER STATE = START;";
            comandoIniciar.CommandTimeout = 30;
            try
            {
                await comandoIniciar.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                await TentarRemoverSessaoAsync(conexao, nomeSessao, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"A sessão de captura foi criada, mas falhou ao iniciar (já removida do servidor). Detalhe: \"{ex.Message}\".", ex);
            }
        }

        NomeSessaoAtual = nomeSessao;
    }

    /// <summary>
    /// Monta a DDL "CREATE EVENT SESSION" — separado de <see cref="IniciarSessaoAsync"/>
    /// só para poder ser exibido/copiado pela tela (ex.: um botão "Ver script"),
    /// mesmo princípio já usado em Modulo_PlanoExecucao para o script de
    /// criação de índice.
    /// </summary>
    public static string MontarDdlCriarSessao(string nomeSessao, IReadOnlyCollection<string> eventosSelecionados)
    {
        var acoes = string.Join(",\n            ", AcoesGlobais);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE EVENT SESSION [{nomeSessao}] ON SERVER");

        var lista = eventosSelecionados.ToList();
        for (int i = 0; i < lista.Count; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"ADD EVENT sqlserver.{lista[i]}(");
            sb.AppendLine($"    ACTION (\n            {acoes}\n        )");
            sb.Append(')');
            sb.AppendLine(i < lista.Count - 1 ? "," : string.Empty);
        }

        sb.AppendLine("ADD TARGET package0.ring_buffer(SET max_memory=(20480))");
        sb.AppendLine("WITH (MAX_MEMORY=20480KB, EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS, MAX_DISPATCH_LATENCY=1 SECONDS, STARTUP_STATE=OFF);");
        return sb.ToString();
    }

    /// <summary>
    /// Lê o ring buffer da sessão atual (<see cref="NomeSessaoAtual"/>) e
    /// devolve só os eventos mais novos que <paramref name="apos"/> (usado
    /// pela tela para não repetir, a cada tick do Timer, eventos já
    /// mostrados) — já com <paramref name="filtro"/> aplicado do lado do
    /// cliente. Retorna também o timestamp do evento mais recente lido,
    /// para o chamador usar como <paramref name="apos"/> na próxima
    /// chamada.
    /// </summary>
    public async Task<(List<ProfilerEventoDto> Eventos, DateTimeOffset? UltimoTimestamp)> ColetarEventosAsync(
        DateTimeOffset? apos, ProfilerFiltroDto? filtro, string? nomeServidorParaExibir, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(NomeSessaoAtual))
        {
            return (new List<ProfilerEventoDto>(), apos);
        }

        await using var conexao = await _conexao.AbrirConexaoAsync(ct).ConfigureAwait(false);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = @"
SELECT CAST(xet.target_data AS XML) AS dados
FROM sys.dm_xe_session_targets xet
JOIN sys.dm_xe_sessions xs ON xs.address = xet.event_session_address
WHERE xs.name = @nome AND xet.target_name = 'ring_buffer';";
        comando.Parameters.AddWithValue("@nome", NomeSessaoAtual);
        comando.CommandTimeout = 30;

        object? resultado;
        try
        {
            resultado = await comando.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Falha ao ler os dados capturados da sessão de Extended Events. Detalhe: \"{ex.Message}\".", ex);
        }

        if (resultado is null || resultado is DBNull)
        {
            // A sessão pode ter sido removida por fora (ex.: outro DBA, ou
            // um restart do serviço) — melhor esforço: some silenciosamente
            // em vez de lançar erro a cada tick do Timer.
            return (new List<ProfilerEventoDto>(), apos);
        }

        var xmlTexto = resultado.ToString();
        if (string.IsNullOrWhiteSpace(xmlTexto))
        {
            return (new List<ProfilerEventoDto>(), apos);
        }

        XElement raiz;
        try
        {
            raiz = XElement.Parse(xmlTexto);
        }
        catch (Exception)
        {
            return (new List<ProfilerEventoDto>(), apos);
        }

        var eventos = new List<ProfilerEventoDto>();
        DateTimeOffset? maiorTimestamp = apos;

        foreach (var eventoEl in raiz.Elements("event"))
        {
            var timestampAttr = eventoEl.Attribute("timestamp")?.Value;
            if (string.IsNullOrEmpty(timestampAttr) || !DateTimeOffset.TryParse(
                    timestampAttr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                continue;
            }

            if (apos.HasValue && timestamp <= apos.Value)
            {
                continue;
            }

            var nomeEvento = eventoEl.Attribute("name")?.Value ?? string.Empty;
            string? ObterCampo(string nomeCampo)
            {
                // Tanto <data name="..."> (confirmado) quanto <action name="...">
                // (formato presumido igual, não confirmado diretamente na
                // documentação — mas arquiteturalmente consistente) têm um
                // <value> filho com o valor.
                var el = eventoEl.Elements().FirstOrDefault(e =>
                    (e.Name.LocalName == "data" || e.Name.LocalName == "action") &&
                    string.Equals(e.Attribute("name")?.Value, nomeCampo, StringComparison.OrdinalIgnoreCase));
                return el?.Element("value")?.Value;
            }

            eventos.Add(MontarEvento(nomeEvento, timestamp, ObterCampo, nomeServidorParaExibir));

            if (!maiorTimestamp.HasValue || timestamp > maiorTimestamp.Value)
            {
                maiorTimestamp = timestamp;
            }
        }

        eventos = eventos.OrderBy(e => e.TimestampBruto).ToList();
        if (filtro is not null)
        {
            eventos = AplicarFiltro(eventos, filtro);
        }

        return (eventos, maiorTimestamp);
    }

    /// <summary>
    /// Para (se estiver rodando) e remove a sessão de XE atual — IDEMPOTENTE
    /// e "melhor esforço": pode ser chamado mesmo sem nenhuma sessão ativa
    /// (não faz nada) e nunca lança exceção (uma falha ao limpar, ex.: rede
    /// já indisponível com o app fechando, não deve travar o encerramento
    /// da tela/aplicativo — a sessão fica órfã no servidor até ser removida
    /// manualmente ou o serviço reiniciar, já que foi criada com
    /// STARTUP_STATE=OFF).
    /// </summary>
    public async Task PararSessaoAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(NomeSessaoAtual))
        {
            return;
        }

        var nomeSessao = NomeSessaoAtual;
        NomeSessaoAtual = null;

        try
        {
            await using var conexao = await _conexao.AbrirConexaoAsync(ct).ConfigureAwait(false);
            await TentarRemoverSessaoAsync(conexao, nomeSessao, ct).ConfigureAwait(false);
        }
        catch
        {
            // Ver comentário no resumo do método — melhor esforço.
        }
    }

    private static async Task TentarRemoverSessaoAsync(SqlConnection conexao, string nomeSessao, CancellationToken ct)
    {
        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText = $@"
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @nome)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = @nome)
        ALTER EVENT SESSION [{nomeSessao}] ON SERVER STATE = STOP;
    DROP EVENT SESSION [{nomeSessao}] ON SERVER;
END";
            comando.Parameters.AddWithValue("@nome", nomeSessao);
            comando.CommandTimeout = 30;
            await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Melhor esforço — ver PararSessaoAsync.
        }
    }

    /// <summary>
    /// Lista, no servidor conectado, as sessões de XE criadas por esta
    /// ferramenta que estão ÓRFÃS — ou seja, todas as que têm o
    /// <see cref="PrefixoNomeSessao"/>, exceto a que esta instância está
    /// usando agora (<see cref="NomeSessaoAtual"/>), se houver.
    ///
    /// POR QUÊ: o nome da sessão é único por execução e a limpeza preventiva
    /// de <see cref="IniciarSessaoAsync"/>/<see cref="PararSessaoAsync"/> só
    /// consegue agir sobre <see cref="NomeSessaoAtual"/>, que é um campo em
    /// MEMÓRIA. Depois de um encerramento anormal (queda do aplicativo, fim
    /// de processo pelo Gerenciador de Tarefas, queda de rede), esse campo
    /// nasce nulo na execução seguinte e a sessão antiga continua RODANDO no
    /// servidor de produção indefinidamente — consumindo o ring buffer e
    /// capturando eventos sem ninguém lendo. Este método é o único caminho
    /// para descobrir esses restos; a tela decide quando oferecer a limpeza.
    /// </summary>
    /// <returns>Nomes das sessões órfãs encontradas (lista vazia se não houver nenhuma).</returns>
    public async Task<List<string>> ListarSessoesOrfasAsync(CancellationToken ct = default)
    {
        var orfas = new List<string>();

        await using var conexao = await _conexao.AbrirConexaoAsync(ct).ConfigureAwait(false);
        await using var comando = conexao.CreateCommand();

        // sys.server_event_sessions lista as sessões DEFINIDAS no servidor
        // (rodando ou não) — é a visão certa aqui, porque uma sessão órfã
        // parada continua ocupando a definição e impedindo/atrapalhando o
        // diagnóstico; sys.dm_xe_sessions mostraria só as em execução.
        comando.CommandText = $@"
SELECT name
FROM sys.server_event_sessions
WHERE name LIKE '{PadraoLikeNomeSessao}'
ORDER BY name;";
        comando.CommandTimeout = 30;

        try
        {
            await using var leitor = await comando.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await leitor.ReadAsync(ct).ConfigureAwait(false))
            {
                var nome = leitor.GetString(0);

                // A sessão da captura em andamento NESTA instância não é
                // órfã — quem cuida dela é PararSessaoAsync.
                if (!string.IsNullOrEmpty(NomeSessaoAtual) &&
                    string.Equals(nome, NomeSessaoAtual, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                orfas.Add(nome);
            }
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                "Falha ao consultar as sessões de captura (Extended Events) existentes no servidor. Detalhe do " +
                $"SQL Server: \"{ex.Message}\". Causa mais provável: falta da permissão VIEW SERVER STATE (ou " +
                "VIEW SERVER SECURITY STATE, em versões mais novas) para o login atual.", ex);
        }

        return orfas;
    }

    /// <summary>
    /// Remove do servidor as sessões de XE informadas (normalmente as que
    /// vieram de <see cref="ListarSessoesOrfasAsync"/>), parando antes cada
    /// uma que ainda estiver em execução.
    ///
    /// RESILIENTE DE PROPÓSITO: uma sessão que falhe ao ser removida (ex.:
    /// outro DBA já a removeu no meio do caminho, ou falta de permissão
    /// pontual) NÃO interrompe as demais — todas são tentadas, e só no fim,
    /// se houve alguma falha, uma única exceção é lançada nomeando
    /// exatamente quais não puderam ser removidas. As que deram certo já
    /// estão removidas quando isso acontece.
    /// </summary>
    public async Task RemoverSessoesOrfasAsync(IEnumerable<string>? nomes, CancellationToken ct = default)
    {
        var lista = (nomes ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (lista.Count == 0)
        {
            return;
        }

        var falhas = new List<string>();

        await using var conexao = await _conexao.AbrirConexaoAsync(ct).ConfigureAwait(false);

        foreach (var nome in lista)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await using var comando = conexao.CreateCommand();

                // O nome não pode ir como parâmetro dentro de ALTER/DROP
                // EVENT SESSION (é um identificador, não um valor), por isso
                // vai interpolado — daí o EscaparColchetes, que impede que um
                // "]" no nome feche o delimitador antes da hora.
                var nomeEscapado = IdentificadorSql.EscaparColchetes(nome);
                comando.CommandText = $@"
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @nome)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = @nome)
        ALTER EVENT SESSION [{nomeEscapado}] ON SERVER STATE = STOP;
    DROP EVENT SESSION [{nomeEscapado}] ON SERVER;
END";
                comando.Parameters.AddWithValue("@nome", nome);
                comando.CommandTimeout = 30;
                await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                falhas.Add($"\"{nome}\" ({ex.Message})");
            }
        }

        if (falhas.Count > 0)
        {
            throw new InvalidOperationException(
                $"{falhas.Count} de {lista.Count} sessão(ões) de captura não puderam ser removidas do servidor " +
                $"(as demais já foram removidas): {string.Join("; ", falhas)}. Causa mais provável: falta da " +
                "permissão ALTER ANY EVENT SESSION para o login atual.");
        }
    }

    /// <summary>
    /// Lê um arquivo .xel já existente (aba "Analisar arquivo") — reusa
    /// Microsoft.SqlServer.XEvent.XELite, mesmo pacote/padrão já usado em
    /// Modulo_Deadlock.AnalisarArquivoXelAsync, inclusive a mesma tolerância
    /// a arquivo incompleto (sessão que gerou o arquivo ainda estava ativa
    /// no momento do envio): se pelo menos um evento já foi lido com
    /// sucesso antes de uma falha no meio do arquivo, o resultado parcial é
    /// devolvido em vez de descartado.
    /// </summary>
    public async Task<List<ProfilerEventoDto>> AnalisarArquivoXelAsync(string caminhoArquivo, ProfilerFiltroDto? filtro, CancellationToken ct = default)
    {
        var resultado = new List<ProfilerEventoDto>();
        var xeStream = new XEFileEventStreamer(caminhoArquivo);
        var atingiuLimite = false;

        try
        {
            await xeStream.ReadEventStream(xevent =>
            {
                // Teto de eventos: sem ele, um .xel de vários GB (uma sessão que
                // rodou a noite inteira num servidor movimentado — justamente o
                // arquivo que se quer analisar) era carregado INTEIRO em memória,
                // cada evento com o texto completo do comando, até o processo
                // morrer por falta de memória. A captura ao vivo já tinha um
                // limite equivalente; só o caminho de arquivo era ilimitado.
                // Ao atingir o teto a leitura para e o chamador é avisado (ver
                // LimiteAtingido no retorno), em vez de truncar em silêncio.
                if (resultado.Count >= MaximoEventosArquivo)
                {
                    atingiuLimite = true;
                    throw new OperationCanceledException(MensagemLimiteAtingido);
                }

                var dto = MontarEventoDeXELite(xevent);
                if (dto is not null)
                {
                    resultado.Add(dto);
                }
                return Task.CompletedTask;
            }, ct);
        }
        // ORDEM IMPORTA: o "catch (Exception) when (...)" filtrado vem DEPOIS do
        // catch de cancelamento. Estava antes, e o filtro capturava também a
        // OperationCanceledException — então cancelar a leitura de um arquivo
        // grande devolvia os eventos parciais já lidos COMO SE fossem o arquivo
        // inteiro, e o usuário analisava uma fração dos dados achando que tinha
        // o conjunto completo.
        catch (OperationCanceledException) when (atingiuLimite)
        {
            // Teto atingido: não é erro nem cancelamento do usuário — devolve o
            // que foi lido, e o aviso vai no fim do método.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (resultado.Count > 0)
        {
            // Melhor esforço — ver XML doc do método.
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Não foi possível ler este arquivo .xel até o fim (erro: \"{ex.Message}\"). O motivo mais " +
                "provável é o arquivo estar incompleto — por exemplo, a captura ainda estava ativa no momento em " +
                "que o arquivo foi copiado/exportado. Tente novamente com um arquivo já finalizado (sessão parada).", ex);
        }

        if (resultado.Count == 0)
        {
            throw new InvalidOperationException("Nenhum evento reconhecido foi encontrado neste arquivo .xel.");
        }

        UltimaLeituraAtingiuLimite = atingiuLimite;

        resultado = resultado.OrderBy(e => e.TimestampBruto).ToList();
        return filtro is not null ? AplicarFiltro(resultado, filtro) : resultado;
    }

    /// <summary>
    /// Monta um <see cref="ProfilerEventoDto"/> a partir de um IXEvent do
    /// XELite (arquivo .xel) — devolve null (em vez de derrubar a leitura
    /// do arquivo inteiro) se este evento específico não puder ser
    /// interpretado por qualquer motivo inesperado.
    /// </summary>
    private static ProfilerEventoDto? MontarEventoDeXELite(IXEvent xevent)
    {
        try
        {
            var timestamp = ObterTimestampOffsetSeguro(xevent) ?? DateTimeOffset.UtcNow;

            string? ObterCampo(string nomeCampo)
            {
                // Não está confirmado se IXEvent.Fields mistura data fields e
                // actions no mesmo dicionário ou os mantém separados
                // (Modulo_Deadlock só usava .Fields, nunca precisou de uma
                // action) — por isso a busca é só em .Fields, com um nome não
                // encontrado simplesmente virando null (célula vazia na
                // grade), nunca um erro.
                return xevent.Fields.TryGetValue(nomeCampo, out var valor) && valor is not null
                    ? valor.ToString()
                    : null;
            }

            // Arquivo .xel: sem "servidor atualmente conectado" nenhum (pode
            // ter sido capturado em qualquer servidor) — ver XML doc de
            // ProfilerEventoDto.ServerName.
            return MontarEvento(xevent.Name, timestamp, ObterCampo, nomeServidorParaExibir: null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lê o timestamp de um IXEvent de forma defensiva, via "dynamic" —
    /// mesmo motivo/padrão de Modulo_Deadlock.ObterTimestampSeguro (a
    /// documentação pública do XELite não deixa claro se a propriedade é um
    /// DateTime ou DateTimeOffset).
    /// </summary>
    private static DateTimeOffset? ObterTimestampOffsetSeguro(IXEvent xevent)
    {
        try
        {
            dynamic bruto = xevent.Timestamp;
            return bruto is DateTimeOffset comOffset ? comOffset : new DateTimeOffset((DateTime)bruto, TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ponto único de mapeamento evento→DTO, compartilhado pelo caminho AO
    /// VIVO (ring buffer, via XML) e pelo caminho de ARQUIVO (.xel, via
    /// XELite) — cada um só precisa fornecer um <paramref name="obterCampo"/>
    /// que sabe ler um nome de campo/action bruto do seu próprio formato.
    /// Todo campo é lido de forma defensiva (nunca lança, sempre cai para
    /// null quando ausente ou não conversível).
    /// </summary>
    private static ProfilerEventoDto MontarEvento(
        string nomeEvento, DateTimeOffset timestamp, Func<string, string?> obterCampo, string? nomeServidorParaExibir)
    {
        var nomeExibicao = CatalogoEventos
            .FirstOrDefault(e => string.Equals(e.NomeEvento, nomeEvento, StringComparison.OrdinalIgnoreCase))
            ?.NomeExibicao ?? nomeEvento;

        string? textoComando = obterCampo("batch_text") ?? obterCampo("statement") ?? obterCampo("message");

        long? cpuMicro = ObterLong(obterCampo("cpu_time"));
        long? duracaoMicro = ObterLong(obterCampo("duration"));
        long? leiturasLogicas = ObterLong(obterCampo("logical_reads"));
        long? leiturasFisicas = ObterLong(obterCampo("physical_reads"));

        long? cpuMs = cpuMicro / 1000;
        long? duracaoMs = duracaoMicro / 1000;

        DateTime? inicio;
        DateTime? fim;
        if (duracaoMs.HasValue)
        {
            fim = timestamp.LocalDateTime;
            inicio = fim.Value.AddMilliseconds(-duracaoMs.Value);
        }
        else
        {
            inicio = timestamp.LocalDateTime;
            fim = null;
        }

        return new ProfilerEventoDto
        {
            EventClass = nomeExibicao,
            TextData = textoComando,
            ApplicationName = obterCampo("client_app_name"),
            NTUserName = obterCampo("nt_username"),
            LoginName = obterCampo("server_principal_name"),
            Cpu = cpuMs,
            Reads = leiturasLogicas ?? leiturasFisicas,
            Writes = ObterLong(obterCampo("writes")),
            Duration = duracaoMs,
            ClientProcessID = ObterInt(obterCampo("client_pid")),
            Spid = ObterInt(obterCampo("session_id")),
            StartTime = inicio,
            EndTime = fim,
            DatabaseID = ObterInt(obterCampo("database_id")),
            DatabaseName = obterCampo("database_name"),
            HostName = obterCampo("client_hostname"),
            RowCounts = ObterLong(obterCampo("row_count")),
            ServerName = nomeServidorParaExibir,
            TimestampBruto = timestamp,
        };
    }

    private static long? ObterLong(string? texto) =>
        long.TryParse(texto, NumberStyles.Any, CultureInfo.InvariantCulture, out var valor) ? valor : null;

    private static int? ObterInt(string? texto) =>
        int.TryParse(texto, NumberStyles.Any, CultureInfo.InvariantCulture, out var valor) ? valor : null;

    /// <summary>
    /// Versão pública de <see cref="AplicarFiltro"/> — usada pela tela
    /// (aba "Analisar arquivo") para reaplicar o filtro em memória, sobre a
    /// lista já lida uma vez de <see cref="AnalisarArquivoXelAsync"/>, sem
    /// precisar reler o arquivo do disco a cada clique em "Aplicar filtro".
    /// </summary>
    public static List<ProfilerEventoDto> FiltrarEventos(IEnumerable<ProfilerEventoDto> eventos, ProfilerFiltroDto filtro) =>
        AplicarFiltro(eventos.ToList(), filtro);

    /// <summary>
    /// Aplica, em memória, todos os critérios de <see cref="ProfilerFiltroDto"/>
    /// — ver o comentário no topo da classe e no próprio DTO sobre a decisão
    /// de nunca filtrar no servidor.
    /// </summary>
    private static List<ProfilerEventoDto> AplicarFiltro(List<ProfilerEventoDto> eventos, ProfilerFiltroDto filtro)
    {
        IEnumerable<ProfilerEventoDto> consulta = eventos;

        if (filtro.EventosSelecionados is { Count: > 0 })
        {
            var nomesExibicaoSelecionados = new HashSet<string>(
                filtro.EventosSelecionados.Select(nomeEvento =>
                    CatalogoEventos.FirstOrDefault(c => string.Equals(c.NomeEvento, nomeEvento, StringComparison.OrdinalIgnoreCase))
                        ?.NomeExibicao ?? nomeEvento),
                StringComparer.OrdinalIgnoreCase);
            consulta = consulta.Where(e => nomesExibicaoSelecionados.Contains(e.EventClass));
        }

        if (!string.IsNullOrWhiteSpace(filtro.AplicativoContem))
        {
            consulta = consulta.Where(e => e.ApplicationName?.Contains(filtro.AplicativoContem, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrWhiteSpace(filtro.BancoContem))
        {
            consulta = consulta.Where(e => e.DatabaseName?.Contains(filtro.BancoContem, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrWhiteSpace(filtro.LoginContem))
        {
            consulta = consulta.Where(e => e.LoginName?.Contains(filtro.LoginContem, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrWhiteSpace(filtro.HostContem))
        {
            consulta = consulta.Where(e => e.HostName?.Contains(filtro.HostContem, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrWhiteSpace(filtro.TextoContem))
        {
            consulta = consulta.Where(e => e.TextData?.Contains(filtro.TextoContem, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (filtro.DuracaoMinimaMs.HasValue)
        {
            consulta = consulta.Where(e => !e.Duration.HasValue || e.Duration.Value >= filtro.DuracaoMinimaMs.Value);
        }

        if (filtro.SomenteComandosDeUsuario)
        {
            consulta = consulta.Where(e =>
                string.IsNullOrEmpty(e.TextData) ||
                !e.TextData.TrimStart().StartsWith("SET ", StringComparison.OrdinalIgnoreCase));
        }

        if (filtro.ExcluirConsultasDoProprioApp)
        {
            consulta = consulta.Where(e => !string.Equals(e.ApplicationName, NomeAplicativoProprio, StringComparison.OrdinalIgnoreCase));
        }

        return consulta.ToList();
    }
}
