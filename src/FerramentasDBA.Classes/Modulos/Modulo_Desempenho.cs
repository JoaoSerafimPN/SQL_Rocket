using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// Monitoramento de performance. Atende aos submenus "Manutenção > Desempenho"
/// (Top N consultas) e "Manutenção > Monitoramento" (Active Monitor SQL
/// Local, Active Monitor SQL Azure) — os dois menus compartilham este mesmo
/// módulo de negócio, só a navegação na sidebar é separada.
///
/// IMPORTANTE: o Active Monitor SQL Azure NÃO reaproveita mais as consultas
/// do Local (<see cref="ObterActiveMonitorLocalAsync"/>/<see cref="ObterMetricasOverviewAsync"/>).
/// Depois de três tentativas sucessivas de adaptar/reaproveitar o script do
/// Local baterem no mesmo erro de permissão no ambiente Azure do usuário
/// ("VIEW SERVER PERFORMANCE STATE permission was denied on object
/// 'server', database 'master'"), o usuário forneceu um conjunto de scripts
/// PRÓPRIOS, mais simples e já confirmados por ele como funcionando nesse
/// ambiente: <see cref="ObterMetricasRecursoAzureAsync"/> (painel Overview,
/// via sys.dm_db_resource_stats) e <see cref="ObterProcessosAzureAsync"/>
/// (grade Processes, via sys.dm_exec_requests/sys.dm_exec_sessions). Os dois
/// métodos executam "SET NOCOUNT ON;" logo após abrir a conexão (pedido
/// explícito do usuário).
///
/// <see cref="ObterConexoesAgrupadasAsync"/> (painel "Conexões por
/// Aplicação/Usuário") não é específico do Azure — é uma consulta genérica
/// sobre sys.dm_exec_sessions, sem nenhuma DMV restrita — por isso é
/// reaproveitado tal e qual pelo Active Monitor SQL Local (a pedido do
/// usuário, depois de ver o painel funcionando no Azure) e pelo Azure.
/// </summary>
public class Modulo_Desempenho
{
    private static readonly int[] OpcoesTopValidas = { 15, 25, 50, 100 };

    /// <summary>
    /// Colunas retornadas por <see cref="TextoConsultaProcessosLocal"/> —
    /// script exato fornecido pelo usuário para o Active Monitor SQL Local.
    /// (Não é mais compartilhado com o Azure — ver comentário na classe.)
    /// </summary>
    private const string ColunasSelecaoProcessosAtivos =
        "   [SESSION ID]    = s.session_id, " +
        "   [USER Process]  = CONVERT(CHAR(1), s.is_user_process), " +
        "   [Login]         = s.login_name, " +
        "   [Comando exec]  = st.text, " +
        "   {0} " +
        "   [Task State]    = ISNULL(t.task_state, N''), " +
        "   [Command]       = ISNULL(r.command, N''), " +
        "   [Application]   = ISNULL(s.program_name, N''), " +
        "   [Wait TIME (ms)]     = ISNULL(w.wait_duration_ms, 0), " +
        "   [Wait TYPE]     = ISNULL(w.wait_type, N''), " +
        "   [Wait Resource] = ISNULL(w.resource_description, N''), " +
        "   [Blocked BY]    = ISNULL(CONVERT (VARCHAR, w.blocking_session_id), ''), " +
        // O teste "é head blocker" era feito com um LEFT JOIN em
        // sys.dm_exec_requests (alias r2) por blocking_session_id — e esse JOIN
        // devolvia UMA LINHA POR SESSÃO BLOQUEADA: um head blocker segurando 12
        // sessões aparecia 12 vezes na grade. Como o valor da coluna só depende
        // de EXISTIR alguém bloqueado por esta sessão (e não de quantos são), o
        // teste virou EXISTS — mesmo resultado, sem multiplicar linhas.
        "   [Head Blocker]  = " +
        "        CASE " +
        "            WHEN (r.blocking_session_id = 0 OR r.session_id IS NULL) " +
        "                 AND EXISTS (SELECT 1 FROM sys.dm_exec_requests r2 WHERE r2.blocking_session_id = s.session_id) THEN '1' " +
        "            ELSE '' " +
        "        END, " +
        "   [Total CPU (ms)] = s.cpu_time, " +
        "   [Total Physical I/O (MB)]   = (s.reads + s.writes) * 8 / 1024, " +
        // s.memory_usage é INT (páginas de 8 KB) e "* 8192" era aritmética INT:
        // estourava acima de ~262.143 páginas (≈2 GB concedidos a UMA sessão),
        // e o erro de overflow derrubava a CONSULTA INTEIRA — a grade parava de
        // atualizar exatamente durante o incidente de memória a ser investigado.
        // CAST para BIGINT antes de multiplicar; "* 8192 / 1024" também era
        // redundante (é 1 página = 8 KB).
        "   [Memory USE (KB)]  = CAST(s.memory_usage AS BIGINT) * 8, " +
        "   [OPEN Transactions] = ISNULL(r.open_transaction_count,0), " +
        "   [Login TIME]    = s.login_time, " +
        "   [LAST Request START TIME] = s.last_request_start_time, " +
        "   [Host Name]     = ISNULL(s.host_name, N''), " +
        "   [Net Address]   = ISNULL(c.client_net_address, N''), " +
        "   [Execution Context ID] = ISNULL(t.exec_context_id, 0), " +
        "   [Request ID] = ISNULL(r.request_id, 0), " +
        "   [Workload GROUP] = N'' ";

    /// <summary>
    /// FROM/JOINs do script do Active Monitor SQL Local (ver
    /// <see cref="TextoConsultaProcessosLocal"/>). O placeholder "{0}"
    /// existe só por causa do JOIN opcional em sys.sysprocesses — não tem
    /// mais relação com o Azure (que hoje usa um script totalmente
    /// diferente, ver comentário na classe).
    ///
    /// REGRA DESTE FROM: o resultado tem que ser UMA LINHA POR SESSÃO. Toda
    /// DMV aqui que devolve mais de uma linha por sessão (conexões MARS,
    /// tarefas de plano paralelo, tarefas em espera) é reduzida a uma linha
    /// representativa com ROW_NUMBER() antes de entrar no JOIN — do contrário
    /// a grade duplica o mesmo SPID dezenas de vezes exatamente na hora em que
    /// a tela importa (bloqueio com consulta paralela), o contador
    /// "N sessão(ões)" mente e os combos de filtro enchem de repetidos.
    /// </summary>
    private const string OrigemSelecaoProcessosAtivos =
        "FROM sys.dm_exec_sessions s " +
        // sys.dm_exec_connections devolve UMA LINHA POR CONEXÃO: com MARS
        // (MultipleActiveResultSets=True, comum em aplicação .NET) a mesma
        // sessão tem várias. Fica só a conexão mais antiga (a primária), que é
        // a que carrega o client_net_address exibido em [Net Address].
        "LEFT OUTER JOIN " +
        "( " +
        "    SELECT session_id, client_net_address, " +
        "           ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY connect_time) AS row_num " +
        "    FROM sys.dm_exec_connections " +
        ") c ON (s.session_id = c.session_id) AND c.row_num = 1 " +
        "LEFT OUTER JOIN sys.dm_exec_requests r ON (s.session_id = r.session_id) " +
        // sys.dm_os_tasks devolve UMA LINHA POR TAREFA: uma consulta em MAXDOP 8
        // gerava 9 linhas da MESMA sessão (a coordenadora + os workers). Fica UMA
        // tarefa representativa por (sessão, requisição), escolhida pela MAIOR
        // espera — de propósito, e não simplesmente a coordenadora
        // (exec_context_id = 0): numa consulta paralela bloqueada quem espera no
        // lock é um worker, então fixar a coordenadora esvaziaria justo as
        // colunas [Wait TYPE]/[Wait Resource]/[Blocked BY], que são o motivo de
        // a tela existir. Sem nenhuma espera, o desempate por exec_context_id
        // devolve a coordenadora (0), que é o esperado para uma sessão ociosa.
        "LEFT OUTER JOIN " +
        "( " +
        "    SELECT tsk.session_id, tsk.request_id, tsk.task_state, tsk.exec_context_id, tsk.task_address, " +
        "           ROW_NUMBER() OVER (PARTITION BY tsk.session_id, tsk.request_id " +
        "                              ORDER BY ISNULL(esp.MaiorEspera, -1) DESC, tsk.exec_context_id) AS row_num " +
        "    FROM sys.dm_os_tasks tsk " +
        "    OUTER APPLY " +
        "    ( " +
        "        SELECT MAX(wt.wait_duration_ms) AS MaiorEspera " +
        "        FROM sys.dm_os_waiting_tasks wt " +
        "        WHERE wt.waiting_task_address = tsk.task_address " +
        "    ) esp " +
        ") t ON (r.session_id = t.session_id AND r.request_id = t.request_id) AND t.row_num = 1 " +
        "LEFT OUTER JOIN " +
        "( " +
        "    SELECT *, ROW_NUMBER() OVER (PARTITION BY waiting_task_address ORDER BY wait_duration_ms DESC) AS row_num " +
        "    FROM sys.dm_os_waiting_tasks " +
        ") w ON (t.task_address = w.waiting_task_address) AND w.row_num = 1 " +
        "{0}" +
        "OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) st " +
        "ORDER BY s.session_id;";

    /// <summary>
    /// Script exato fornecido pelo usuário para o Active Monitor SQL Local —
    /// usa sys.sysprocesses (view de compatibilidade, indisponível no Azure
    /// SQL Database) só para resolver DB_NAME(p.dbid) por linha, já que uma
    /// instância local pode ter sessões conectadas a bancos diferentes.
    ///
    /// sys.sysprocesses também devolve UMA LINHA POR THREAD (coluna ecid) —
    /// mesmo problema de multiplicação de sys.dm_os_tasks descrito em
    /// <see cref="OrigemSelecaoProcessosAtivos"/> —, por isso o JOIN também
    /// passa por ROW_NUMBER(). Fica ecid = 0 (a thread coordenadora, a que
    /// representa a sessão); o dbid é o mesmo em todas as threads do SPID, de
    /// modo que o [DATABASE] exibido não muda.
    /// </summary>
    private static readonly string TextoConsultaProcessosLocal =
        "SELECT " +
        string.Format(ColunasSelecaoProcessosAtivos, "[DATABASE]      = ISNULL(db_name(p.dbid), N''), ") +
        string.Format(
            OrigemSelecaoProcessosAtivos,
            "LEFT OUTER JOIN " +
            "( " +
            "    SELECT spid, dbid, ROW_NUMBER() OVER (PARTITION BY spid ORDER BY ecid) AS row_num " +
            "    FROM sys.sysprocesses " +
            ") p ON (s.session_id = p.spid) AND p.row_num = 1 ");

    /// <summary>
    /// Script fornecido pelo usuário para o painel "Overview" do Active
    /// Monitor SQL Local — substitui as 4 consultas separadas usadas antes
    /// (uma por métrica, com a taxa por segundo calculada no cliente a
    /// partir do delta entre duas amostras consecutivas) por UM único lote
    /// que já calcula a taxa server-side: captura os contadores cumulativos
    /// de Batch Requests/sec (sys.dm_os_performance_counters) e de I/O
    /// (sys.dm_io_virtual_file_stats), espera exatamente 1 segundo (WAITFOR
    /// DELAY), captura os mesmos contadores de novo e retorna o delta já
    /// pronto — mais o %CPU instantâneo (sys.dm_os_ring_buffers) e a
    /// contagem de Waiting Tasks (sys.dm_os_waiting_tasks JOIN
    /// sys.dm_exec_sessions, filtrando só sessões de usuário e excluindo
    /// uma lista de wait_types "de sistema"/ociosos — mesmo filtro que o
    /// Activity Monitor nativo do SSMS usa, atualizado pelo usuário depois
    /// de uma versão anterior mais simples deste script, que só excluía
    /// SLEEP_%). Ver <see cref="MetricasOverviewDto"/>.
    /// </summary>
    /// <remarks>
    /// Diferente das outras consultas do módulo, isto NÃO é um SELECT
    /// único — é um lote com DECLARE/SET/SELECT em várias instruções,
    /// incluindo o WAITFOR DELAY '00:00:01' (por isso cada chamada a
    /// <see cref="ObterMetricasOverviewAsync"/> leva pelo menos 1 segundo
    /// só nessa consulta — ela roda em paralelo com as outras 3 da tela via
    /// Task.WhenAll em Menu_Raiz.AtualizarTudoAsync, então não soma ao
    /// tempo das demais). Por ser um lote único, uma falha em QUALQUER
    /// instrução (DMV indisponível, permissão negada) derruba o lote
    /// inteiro — diferente do desenho anterior, que isolava cada métrica em
    /// seu próprio try/catch; esse é o script exato fornecido pelo usuário,
    /// e a isolação por métrica não é possível preservando o lote como foi
    /// pedido (ver try/catch único em <see cref="ObterMetricasOverviewAsync"/>).
    /// </remarks>
    private const string TextoMetricasOverviewLocal =
        "SET NOCOUNT ON; " +
        "DECLARE @BatchRequests_Start BIGINT, @IO_BytesRead_Start BIGINT, @IO_BytesWritten_Start BIGINT; " +
        "SELECT @BatchRequests_Start = cntr_value " +
        "FROM sys.dm_os_performance_counters " +
        "WHERE counter_name = 'Batch Requests/sec' " +
        "  AND object_name LIKE '%:SQL Statistics%'; " +
        "SELECT " +
        "    @IO_BytesRead_Start = SUM(num_of_bytes_read), " +
        "    @IO_BytesWritten_Start = SUM(num_of_bytes_written) " +
        "FROM sys.dm_io_virtual_file_stats(NULL, NULL); " +
        "WAITFOR DELAY '00:00:01'; " +
        "DECLARE @BatchRequests_End BIGINT, @IO_BytesRead_End BIGINT, @IO_BytesWritten_End BIGINT; " +
        "SELECT @BatchRequests_End = cntr_value " +
        "FROM sys.dm_os_performance_counters " +
        "WHERE counter_name = 'Batch Requests/sec' " +
        "  AND object_name LIKE '%:SQL Statistics%'; " +
        "SELECT " +
        "    @IO_BytesRead_End = SUM(num_of_bytes_read), " +
        "    @IO_BytesWritten_End = SUM(num_of_bytes_written) " +
        "FROM sys.dm_io_virtual_file_stats(NULL, NULL); " +
        "DECLARE @CPU_Percent INT; " +
        "SELECT TOP 1 " +
        "    @CPU_Percent = record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int') " +
        "FROM ( " +
        "    SELECT timestamp, CONVERT(xml, record) AS record " +
        "    FROM sys.dm_os_ring_buffers " +
        "    WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' " +
        "      AND record LIKE '%<SystemHealth>%' " +
        ") AS x " +
        "ORDER BY timestamp DESC; " +
        "DECLARE @WaitingTasks INT; " +
        "SELECT @WaitingTasks = COUNT(*) " +
        "FROM sys.dm_os_waiting_tasks wt " +
        "JOIN sys.dm_exec_sessions s ON wt.session_id = s.session_id " +
        "WHERE s.is_user_process = 1 " +
        "  AND wt.wait_type NOT IN ( " +
        "    'REQUEST_FOR_DEADLOCK_SEARCH', 'XE_TIMER_EVENT', 'CHECKPOINT_QUEUE', " +
        "    'CHKPT', 'DISPATCHER_QUEUE_SEMAPHORE', 'LAZYWRITER_SLEEP', " +
        "    'BROKER_TO_FLUSH', 'BROKER_TASK_STOP', 'DIRTY_PAGE_POLL', " +
        "    'HADR_FILESTREAM_IOMGR_IOCOMPLETION', 'SP_SERVER_DIAGNOSTICS_SLEEP', " +
        "    'SQLTRACE_BUFFER_FLUSH', 'WAITFOR', 'WAIT_FOR_RESULTS', 'SLEEP_TASK', " +
        "    'SLEEP_SYSTEMTASK', 'SLEEP_BPOOL_FLUSH', 'BROKER_EVENTHANDLER' " +
        "); " +
        "SELECT " +
        "    SYSUTCDATETIME() AS [Data_Hora_UTC], " +
        "    @CPU_Percent AS [% Processor Time], " +
        "    @WaitingTasks AS [Waiting Tasks], " +
        "    CAST(((@IO_BytesRead_End - @IO_BytesRead_Start) + (@IO_BytesWritten_End - @IO_BytesWritten_Start)) / 1024.0 / 1024.0 AS DECIMAL(10,2)) AS [Database I/O (MB/sec)], " +
        "    (@BatchRequests_End - @BatchRequests_Start) AS [Batch Requests/sec];";

    /// <summary>
    /// Script fornecido pelo usuário para a grade "Processes" do Active
    /// Monitor SQL Azure — bem mais simples que o do Local: só
    /// sys.dm_exec_requests INNER JOIN sys.dm_exec_sessions (mais
    /// sys.dm_exec_sql_text para o texto da instrução). Ver
    /// <see cref="ProcessoAtivoAzureDto"/> para o porquê do shape diferente
    /// (sem coluna de banco de dados, só sessões com requisição ATIVA).
    /// </summary>
    private const string TextoConsultaProcessosAzure =
        "SELECT " +
        "    r.session_id AS [SPID], " +
        "    r.status AS [Status], " +
        "    r.blocking_session_id AS [Bloqueado_Por], " +
        "    r.cpu_time AS [CPU_Time_ms], " +
        "    r.total_elapsed_time AS [Tempo_Total_ms], " +
        "    r.wait_type AS [Tipo_Espera], " +
        "    r.wait_time AS [Tempo_Espera_ms], " +
        "    SUBSTRING(st.text, (r.statement_start_offset/2)+1, " +
        "        ((CASE r.statement_end_offset " +
        "          WHEN -1 THEN DATALENGTH(st.text) " +
        "         ELSE r.statement_end_offset " +
        "         END - r.statement_start_offset)/2) + 1) AS [Query_Em_Execucao], " +
        "    s.program_name AS [Aplicacao], " +
        "    s.login_name AS [Usuario] " +
        "FROM sys.dm_exec_requests r " +
        "INNER JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id " +
        "CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st " +
        "WHERE s.is_user_process = 1 " +
        "  AND r.session_id <> @@SPID " +
        "ORDER BY r.cpu_time DESC;";

    /// <summary>
    /// Script fornecido pelo usuário para o painel "Overview" do Active
    /// Monitor SQL Azure, via sys.dm_db_resource_stats (database-scoped,
    /// não exige "VIEW SERVER PERFORMANCE STATE"). Ver
    /// <see cref="MetricasRecursoAzureDto"/>.
    /// </summary>
    private const string TextoMetricasRecursoAzure =
        "SELECT TOP 1 " +
        "    SYSUTCDATETIME() AS [Data_Hora_UTC], " +
        "    avg_cpu_percent AS [CPU_%], " +
        "    avg_memory_usage_percent AS [Memoria_%], " +
        "    avg_data_io_percent AS [Data_IO_%], " +
        "    avg_log_write_percent AS [Log_Write_%], " +
        "    max_worker_percent AS [Workers_%], " +
        "    max_session_percent AS [Sessoes_%] " +
        "FROM sys.dm_db_resource_stats " +
        "ORDER BY end_time DESC;";

    /// <summary>
    /// Script fornecido pelo usuário para o painel "Conexões por
    /// Aplicação/Usuário" do Active Monitor (Local e Azure) — contagem de
    /// conexões de usuário ativas agrupadas por program_name/login_name.
    /// Só sys.dm_exec_sessions, sem nenhuma DMV restrita a permissões de
    /// Azure — por isso funciona igual nas duas telas. Ver
    /// <see cref="ConexaoAgrupadaDto"/>.
    /// </summary>
    private const string TextoConexoesAgrupadas =
        "SELECT " +
        "    program_name AS [Aplicacao], " +
        "    login_name AS [Usuario], " +
        "    COUNT(*) AS [Total_Conexoes] " +
        "FROM sys.dm_exec_sessions " +
        "WHERE is_user_process = 1 " +
        "GROUP BY program_name, login_name " +
        "ORDER BY Total_Conexoes DESC;";

    private readonly Conectar_SQL _conexao;

    public Modulo_Desempenho(Conectar_SQL conexao)
    {
        _conexao = conexao;
    }

    /// <summary>
    /// Retorna as <paramref name="top"/> consultas que mais consomem
    /// processamento (ordenadas por leituras físicas totais), a partir do
    /// plan cache — mesma consulta usada por scripts clássicos de tuning
    /// ("Listagem 5"): sys.dm_exec_query_stats + sys.dm_exec_sql_text +
    /// sys.dm_exec_query_plan, com o banco de dados de cada plano via
    /// sys.databases.
    /// </summary>
    /// <param name="top">Quantidade de linhas (tela restringe a 15/25/50/100; qualquer outro valor recebido aqui é normalizado para 25).</param>
    /// <param name="nomeBanco">
    /// Quando informado, filtra apenas os planos daquele banco (equivalente a
    /// descomentar o "WHERE DB.name = 'DATABASE_NAME'" do script original).
    /// Quando nulo/vazio (padrão), traz de todos os bancos da instância.
    /// </param>
    /// <remarks>
    /// BUG DE PRODUÇÃO reportado pelo usuário (tela Monitoramento > Active
    /// Monitor SQL Local, painel "Recent Expensive Queries" — chama este
    /// método a cada atualização): a versão original do script (fiel ao
    /// clássico "Listagem 5" de tuning) fazia
    /// <c>CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle)</c> ANTES do
    /// <c>TOP</c>/<c>ORDER BY</c> — ou seja, o SQL Server decodificava o XML
    /// do plano de execução de TODO plano em cache da instância (podem ser
    /// dezenas de milhares numa instância de produção movimentada) só para
    /// descartar quase tudo depois e devolver os 15-100 primeiros. Num banco
    /// de desenvolvimento pequeno (plan cache pequeno) isso passava
    /// despercebido; em produção, essa decodificação de plano é a parte mais
    /// cara da consulta de longe, e com o filtro por banco (`WHERE
    /// DB.name = @banco`, dependente do CROSS APPLY de sql_text) o otimizador
    /// não conseguia adiar/empurrar o TOP pra antes dos CROSS APPLY. Corrigido
    /// reestruturando em duas etapas: a subconsulta interna já aplica
    /// TOP+ORDER BY (e o filtro de banco) usando só sys.dm_exec_query_stats +
    /// sys.dm_exec_sql_text (bem mais barato) — só DEPOIS, já com o conjunto
    /// reduzido a no máximo <paramref name="top"/> linhas, é que
    /// sys.dm_exec_query_plan é chamado (OUTER, não CROSS, para não perder
    /// uma linha se o plano específico não tiver mais XML disponível nesse
    /// instante). Resultado esperado: a mesma informação de antes, mas sem
    /// decodificar plano nenhum a mais do que o necessário para exibir.
    /// </remarks>
    public async Task<List<ConsultaCustosaDto>> ObterTop25ConsultasCustosasAsync(
        int top = 25, string? nomeBanco = null, CancellationToken ct = default)
    {
        var topValidado = Array.IndexOf(OpcoesTopValidas, top) >= 0 ? top : 25;
        var filtrarPorBanco = !string.IsNullOrWhiteSpace(nomeBanco);

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT " +
            "    r.Banco, " +
            "    r.ContagemExecucoes, " +
            "    r.NumeroGeracoesPlano, " +
            "    r.UltimaExecucao, " +
            "    r.TempoCpuTotal, " +
            "    r.TempoCpuUltimo, " +
            "    r.TempoCpuMinimo, " +
            "    r.TempoCpuMaximo, " +
            "    r.LeiturasLogicasTotal, " +
            "    r.LeiturasLogicasUltima, " +
            "    r.LeiturasLogicasMinima, " +
            "    r.LeiturasLogicasMaxima, " +
            "    r.LeiturasFisicasTotal, " +
            "    r.LeiturasFisicasUltima, " +
            "    r.LeiturasFisicasMinima, " +
            "    r.LeiturasFisicasMaxima, " +
            "    r.EscritasLogicasTotal, " +
            "    r.EscritasLogicasUltima, " +
            "    r.EscritasLogicasMinima, " +
            "    r.EscritasLogicasMaxima, " +
            "    r.TempoDecorridoTotal, " +
            "    r.TempoDecorridoUltimo, " +
            "    r.TempoDecorridoMinimo, " +
            "    r.TempoDecorridoMaximo, " +
            "    r.InstrucaoSql, " +
            "    r.TextoCompleto, " +
            "    p.query_plan AS PlanoExecucaoXml " +
            "FROM ( " +
            "    SELECT TOP (@top) " +
            "        qs.plan_handle, " +
            "        DB.name AS Banco, " +
            "        qs.execution_count AS ContagemExecucoes, " +
            "        qs.plan_generation_num AS NumeroGeracoesPlano, " +
            "        qs.last_execution_time AS UltimaExecucao, " +
            "        qs.total_worker_time AS TempoCpuTotal, " +
            "        qs.last_worker_time AS TempoCpuUltimo, " +
            "        qs.min_worker_time AS TempoCpuMinimo, " +
            "        qs.max_worker_time AS TempoCpuMaximo, " +
            "        qs.total_logical_reads AS LeiturasLogicasTotal, " +
            "        qs.last_logical_reads AS LeiturasLogicasUltima, " +
            "        qs.min_logical_reads AS LeiturasLogicasMinima, " +
            "        qs.max_logical_reads AS LeiturasLogicasMaxima, " +
            "        qs.total_physical_reads AS LeiturasFisicasTotal, " +
            "        qs.last_physical_reads AS LeiturasFisicasUltima, " +
            "        qs.min_physical_reads AS LeiturasFisicasMinima, " +
            "        qs.max_physical_reads AS LeiturasFisicasMaxima, " +
            "        qs.total_logical_writes AS EscritasLogicasTotal, " +
            "        qs.last_logical_writes AS EscritasLogicasUltima, " +
            "        qs.min_logical_writes AS EscritasLogicasMinima, " +
            "        qs.max_logical_writes AS EscritasLogicasMaxima, " +
            "        qs.total_elapsed_time AS TempoDecorridoTotal, " +
            "        qs.last_elapsed_time AS TempoDecorridoUltimo, " +
            "        qs.min_elapsed_time AS TempoDecorridoMinimo, " +
            "        qs.max_elapsed_time AS TempoDecorridoMaximo, " +
            "        (SUBSTRING(s2.text, qs.statement_start_offset / 2, " +
            "            ((CASE WHEN qs.statement_end_offset = -1 THEN (LEN(CONVERT(nvarchar(MAX), s2.text)) * 2) " +
            "                   ELSE qs.statement_end_offset END) - qs.statement_start_offset) / 2) " +
            "        ) AS InstrucaoSql, " +
            "        s2.text AS TextoCompleto " +
            "    FROM sys.dm_exec_query_stats AS qs " +
            "    CROSS APPLY sys.dm_exec_sql_text(qs.plan_handle) AS s2 " +
            "    LEFT JOIN sys.databases AS DB ON DB.database_id = s2.dbid " +
            (filtrarPorBanco ? "    WHERE DB.name = @banco " : string.Empty) +
            "    ORDER BY qs.total_physical_reads DESC " +
            ") AS r " +
            "OUTER APPLY sys.dm_exec_query_plan(r.plan_handle) AS p " +
            "ORDER BY r.LeiturasFisicasTotal DESC;";

        comando.Parameters.AddWithValue("@top", topValidado);
        if (filtrarPorBanco)
        {
            comando.Parameters.AddWithValue("@banco", nomeBanco);
        }

        var resultado = new List<ConsultaCustosaDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new ConsultaCustosaDto
            {
                Banco = leitor.IsDBNull(leitor.GetOrdinal("Banco")) ? null : leitor.GetString(leitor.GetOrdinal("Banco")),
                ContagemExecucoes = leitor.GetInt64(leitor.GetOrdinal("ContagemExecucoes")),
                // plan_generation_num é bigint no SQL Server (não int) — GetInt32 aqui
                // causava InvalidCastException em qualquer execução real.
                NumeroGeracoesPlano = leitor.GetInt64(leitor.GetOrdinal("NumeroGeracoesPlano")),
                UltimaExecucao = leitor.GetDateTime(leitor.GetOrdinal("UltimaExecucao")),
                TempoCpuTotal = leitor.GetInt64(leitor.GetOrdinal("TempoCpuTotal")),
                TempoCpuUltimo = leitor.GetInt64(leitor.GetOrdinal("TempoCpuUltimo")),
                TempoCpuMinimo = leitor.GetInt64(leitor.GetOrdinal("TempoCpuMinimo")),
                TempoCpuMaximo = leitor.GetInt64(leitor.GetOrdinal("TempoCpuMaximo")),
                LeiturasLogicasTotal = leitor.GetInt64(leitor.GetOrdinal("LeiturasLogicasTotal")),
                LeiturasLogicasUltima = leitor.GetInt64(leitor.GetOrdinal("LeiturasLogicasUltima")),
                LeiturasLogicasMinima = leitor.GetInt64(leitor.GetOrdinal("LeiturasLogicasMinima")),
                LeiturasLogicasMaxima = leitor.GetInt64(leitor.GetOrdinal("LeiturasLogicasMaxima")),
                LeiturasFisicasTotal = leitor.GetInt64(leitor.GetOrdinal("LeiturasFisicasTotal")),
                LeiturasFisicasUltima = leitor.GetInt64(leitor.GetOrdinal("LeiturasFisicasUltima")),
                LeiturasFisicasMinima = leitor.GetInt64(leitor.GetOrdinal("LeiturasFisicasMinima")),
                LeiturasFisicasMaxima = leitor.GetInt64(leitor.GetOrdinal("LeiturasFisicasMaxima")),
                EscritasLogicasTotal = leitor.GetInt64(leitor.GetOrdinal("EscritasLogicasTotal")),
                EscritasLogicasUltima = leitor.GetInt64(leitor.GetOrdinal("EscritasLogicasUltima")),
                EscritasLogicasMinima = leitor.GetInt64(leitor.GetOrdinal("EscritasLogicasMinima")),
                EscritasLogicasMaxima = leitor.GetInt64(leitor.GetOrdinal("EscritasLogicasMaxima")),
                TempoDecorridoTotal = leitor.GetInt64(leitor.GetOrdinal("TempoDecorridoTotal")),
                TempoDecorridoUltimo = leitor.GetInt64(leitor.GetOrdinal("TempoDecorridoUltimo")),
                TempoDecorridoMinimo = leitor.GetInt64(leitor.GetOrdinal("TempoDecorridoMinimo")),
                TempoDecorridoMaximo = leitor.GetInt64(leitor.GetOrdinal("TempoDecorridoMaximo")),
                InstrucaoSql = leitor.IsDBNull(leitor.GetOrdinal("InstrucaoSql")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("InstrucaoSql")),
                TextoCompleto = leitor.IsDBNull(leitor.GetOrdinal("TextoCompleto")) ? null : leitor.GetString(leitor.GetOrdinal("TextoCompleto")),
                PlanoExecucaoXml = leitor.IsDBNull(leitor.GetOrdinal("PlanoExecucaoXml")) ? null : leitor.GetString(leitor.GetOrdinal("PlanoExecucaoXml"))
            });
        }

        return resultado;
    }

    /// <summary>
    /// Retorna as sessões/processos ativos de uma instância SQL Server local
    /// (Active Monitor &gt; Processes), a partir do script exato fornecido
    /// pelo usuário: sys.dm_exec_sessions LEFT JOIN sys.dm_exec_connections/
    /// sys.dm_exec_requests/sys.dm_os_tasks, LEFT JOIN em sys.dm_os_waiting_tasks
    /// (com ROW_NUMBER para pegar só a espera mais longa por task, evitando
    /// linhas duplicadas em queries paralelas) para tempo/tipo/recurso de
    /// espera, auto-LEFT JOIN em sys.dm_exec_requests (r2) para achar quem é
    /// "Head Blocker" (bloqueia outros mas não está bloqueado), e OUTER APPLY
    /// sys.dm_exec_sql_text para o texto do comando em execução.
    /// </summary>
    /// <remarks>
    /// O filtro comentado no script original
    /// ("--where s.login_name = 'UserItaboaMateriais' and r.command is not null")
    /// não foi reativado aqui — em vez de um parâmetro de usuário fixo, a
    /// tela (Menu_Raiz.MostrarPaginaActiveMonitorLocal) traz TODAS as sessões
    /// e filtra por banco/usuário localmente (client-side), o que permite
    /// trocar o filtro sem precisar consultar o SQL Server de novo a cada
    /// troca — importante numa tela com atualização automática frequente.
    ///
    /// s.session_id é SMALLINT (não INT) em sys.dm_exec_sessions — lido com
    /// GetInt16 (mesma classe de cuidado já aplicada às colunas BIGINT das
    /// DMVs de índice neste módulo/Modulo_Indices: usar o getter tipado
    /// errado causa InvalidCastException em qualquer execução real).
    /// </remarks>
    public async Task<List<ProcessoAtivoDto>> ObterActiveMonitorLocalAsync(CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = TextoConsultaProcessosLocal;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        return await LerProcessosAtivosAsync(leitor, ct);
    }

    /// <summary>
    /// Retorna as linhas da grade "Processes" do Active Monitor SQL Azure —
    /// script próprio do usuário (ver <see cref="TextoConsultaProcessosAzure"/>
    /// e <see cref="ProcessoAtivoAzureDto"/>), depois que três tentativas
    /// sucessivas de reaproveitar/adaptar o script do Local bateram no
    /// mesmo erro de permissão no ambiente Azure do usuário.
    /// </summary>
    /// <remarks>
    /// Executa "SET NOCOUNT ON;" logo após abrir a conexão (pedido explícito
    /// do usuário) antes da consulta principal.
    /// </remarks>
    public async Task<List<ProcessoAtivoAzureDto>> ObterProcessosAzureAsync(CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await DefinirNoCountOnAsync(conexao, ct);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = TextoConsultaProcessosAzure;

        var resultado = new List<ProcessoAtivoAzureDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new ProcessoAtivoAzureDto
            {
                // session_id/blocking_session_id são SMALLINT em
                // sys.dm_exec_requests -> GetInt16 (mesmo cuidado já
                // aplicado ao script do Local).
                Spid = leitor.GetInt16(leitor.GetOrdinal("SPID")),
                Status = leitor.IsDBNull(leitor.GetOrdinal("Status")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Status")),
                BloqueadoPor = leitor.IsDBNull(leitor.GetOrdinal("Bloqueado_Por")) ? 0 : leitor.GetInt16(leitor.GetOrdinal("Bloqueado_Por")),
                CpuTimeMs = leitor.IsDBNull(leitor.GetOrdinal("CPU_Time_ms")) ? 0 : leitor.GetInt32(leitor.GetOrdinal("CPU_Time_ms")),
                TempoTotalMs = leitor.IsDBNull(leitor.GetOrdinal("Tempo_Total_ms")) ? 0 : leitor.GetInt32(leitor.GetOrdinal("Tempo_Total_ms")),
                TipoEspera = leitor.IsDBNull(leitor.GetOrdinal("Tipo_Espera")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Tipo_Espera")),
                TempoEsperaMs = leitor.IsDBNull(leitor.GetOrdinal("Tempo_Espera_ms")) ? 0 : leitor.GetInt32(leitor.GetOrdinal("Tempo_Espera_ms")),
                QueryEmExecucao = leitor.IsDBNull(leitor.GetOrdinal("Query_Em_Execucao")) ? null : leitor.GetString(leitor.GetOrdinal("Query_Em_Execucao")),
                Aplicacao = leitor.IsDBNull(leitor.GetOrdinal("Aplicacao")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Aplicacao")),
                Usuario = leitor.IsDBNull(leitor.GetOrdinal("Usuario")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Usuario"))
            });
        }

        return resultado;
    }

    /// <summary>
    /// Retorna a amostra mais recente do painel "Overview" do Active
    /// Monitor SQL Azure, via sys.dm_db_resource_stats (ver
    /// <see cref="TextoMetricasRecursoAzure"/> e <see cref="MetricasRecursoAzureDto"/>).
    /// </summary>
    /// <remarks>
    /// A consulta inteira roda dentro de um try/catch — se
    /// sys.dm_db_resource_stats ainda não tiver nenhuma linha (banco
    /// recém-criado) ou alguma outra falha pontual ocorrer, retorna um DTO
    /// com todas as propriedades nulas ("N/D" na tela) em vez de derrubar a
    /// atualização inteira (mesmo cuidado já aplicado ao Overview do Local).
    /// Executa "SET NOCOUNT ON;" logo após abrir a conexão.
    /// </remarks>
    public async Task<MetricasRecursoAzureDto> ObterMetricasRecursoAzureAsync(CancellationToken ct = default)
    {
        var resultado = new MetricasRecursoAzureDto();

        try
        {
            await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
            await DefinirNoCountOnAsync(conexao, ct);

            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = TextoMetricasRecursoAzure;

            await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
            if (await leitor.ReadAsync(ct))
            {
                resultado.DataHoraUtc = leitor.IsDBNull(leitor.GetOrdinal("Data_Hora_UTC")) ? null : leitor.GetDateTime(leitor.GetOrdinal("Data_Hora_UTC"));
                resultado.CpuPercent = leitor.IsDBNull(leitor.GetOrdinal("CPU_%")) ? null : Convert.ToDouble(leitor.GetValue(leitor.GetOrdinal("CPU_%")));
                resultado.MemoriaPercent = leitor.IsDBNull(leitor.GetOrdinal("Memoria_%")) ? null : Convert.ToDouble(leitor.GetValue(leitor.GetOrdinal("Memoria_%")));
                resultado.DataIoPercent = leitor.IsDBNull(leitor.GetOrdinal("Data_IO_%")) ? null : Convert.ToDouble(leitor.GetValue(leitor.GetOrdinal("Data_IO_%")));
                resultado.LogWritePercent = leitor.IsDBNull(leitor.GetOrdinal("Log_Write_%")) ? null : Convert.ToDouble(leitor.GetValue(leitor.GetOrdinal("Log_Write_%")));
                resultado.WorkersPercent = leitor.IsDBNull(leitor.GetOrdinal("Workers_%")) ? null : Convert.ToDouble(leitor.GetValue(leitor.GetOrdinal("Workers_%")));
                resultado.SessoesPercent = leitor.IsDBNull(leitor.GetOrdinal("Sessoes_%")) ? null : Convert.ToDouble(leitor.GetValue(leitor.GetOrdinal("Sessoes_%")));
            }
        }
        catch
        {
            // sys.dm_db_resource_stats sem linhas ainda, ou outra falha
            // pontual — DTO fica com tudo nulo, tela mostra "N/D".
        }

        return resultado;
    }

    /// <summary>
    /// Retorna a contagem de conexões de usuário ativas agrupadas por
    /// Aplicação/Usuário, para o painel "Conexões por Aplicação/Usuário" do
    /// Active Monitor — usado tanto pelo Local quanto pelo Azure (ver
    /// <see cref="TextoConexoesAgrupadas"/> e <see cref="ConexaoAgrupadaDto"/>).
    /// </summary>
    /// <remarks>
    /// Executa "SET NOCOUNT ON;" logo após abrir a conexão.
    /// </remarks>
    public async Task<List<ConexaoAgrupadaDto>> ObterConexoesAgrupadasAsync(CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await DefinirNoCountOnAsync(conexao, ct);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = TextoConexoesAgrupadas;

        var resultado = new List<ConexaoAgrupadaDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new ConexaoAgrupadaDto
            {
                Aplicacao = leitor.IsDBNull(leitor.GetOrdinal("Aplicacao")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Aplicacao")),
                Usuario = leitor.IsDBNull(leitor.GetOrdinal("Usuario")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Usuario")),
                TotalConexoes = leitor.GetInt32(leitor.GetOrdinal("Total_Conexoes"))
            });
        }

        return resultado;
    }

    /// <summary>
    /// Executa "SET NOCOUNT ON;" na conexão recém-aberta — pedido explícito
    /// do usuário para as consultas do Active Monitor SQL Azure, executado
    /// como o primeiro comando de cada conexão usada por
    /// <see cref="ObterProcessosAzureAsync"/>, <see cref="ObterMetricasRecursoAzureAsync"/>
    /// e <see cref="ObterConexoesAgrupadasAsync"/> (essa última também
    /// chamada pelo Active Monitor SQL Local, ver remarks da classe).
    /// </summary>
    private static async Task DefinirNoCountOnAsync(SqlConnection conexao, CancellationToken ct)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SET NOCOUNT ON;";
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Lê os resultados de <see cref="TextoConsultaProcessosLocal"/> para
    /// uma lista de <see cref="ProcessoAtivoDto"/>.
    /// </summary>
    private static async Task<List<ProcessoAtivoDto>> LerProcessosAtivosAsync(SqlDataReader leitor, CancellationToken ct)
    {
        var resultado = new List<ProcessoAtivoDto>();
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new ProcessoAtivoDto
            {
                // smallint -> GetInt16 (ver comentário no <remarks> de ObterActiveMonitorLocalAsync).
                SessionId = leitor.GetInt16(leitor.GetOrdinal("SESSION ID")),
                UserProcess = leitor.IsDBNull(leitor.GetOrdinal("USER Process")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("USER Process")).Trim(),
                Login = leitor.IsDBNull(leitor.GetOrdinal("Login")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Login")),
                ComandoExec = leitor.IsDBNull(leitor.GetOrdinal("Comando exec")) ? null : leitor.GetString(leitor.GetOrdinal("Comando exec")),
                Database = leitor.IsDBNull(leitor.GetOrdinal("DATABASE")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("DATABASE")),
                TaskState = leitor.IsDBNull(leitor.GetOrdinal("Task State")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Task State")),
                Command = leitor.IsDBNull(leitor.GetOrdinal("Command")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Command")),
                Application = leitor.IsDBNull(leitor.GetOrdinal("Application")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Application")),
                // wait_duration_ms é BIGINT em sys.dm_os_waiting_tasks -> GetInt64.
                WaitTimeMs = leitor.IsDBNull(leitor.GetOrdinal("Wait TIME (ms)")) ? 0 : leitor.GetInt64(leitor.GetOrdinal("Wait TIME (ms)")),
                WaitType = leitor.IsDBNull(leitor.GetOrdinal("Wait TYPE")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Wait TYPE")),
                WaitResource = leitor.IsDBNull(leitor.GetOrdinal("Wait Resource")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Wait Resource")),
                BlockedBy = leitor.IsDBNull(leitor.GetOrdinal("Blocked BY")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Blocked BY")),
                HeadBlocker = leitor.IsDBNull(leitor.GetOrdinal("Head Blocker")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Head Blocker")),
                TotalCpuMs = leitor.IsDBNull(leitor.GetOrdinal("Total CPU (ms)")) ? 0 : leitor.GetInt32(leitor.GetOrdinal("Total CPU (ms)")),
                // (reads + writes) são BIGINT em sys.dm_exec_sessions -> soma continua BIGINT -> GetInt64.
                TotalIoFisicoMb = leitor.IsDBNull(leitor.GetOrdinal("Total Physical I/O (MB)")) ? 0 : leitor.GetInt64(leitor.GetOrdinal("Total Physical I/O (MB)")),
                // BIGINT no script (ver [Memory USE (KB)] em ColunasSelecaoProcessosAtivos) -> GetInt64.
                MemoriaUsoKb = leitor.IsDBNull(leitor.GetOrdinal("Memory USE (KB)")) ? 0 : leitor.GetInt64(leitor.GetOrdinal("Memory USE (KB)")),
                TransacoesAbertas = leitor.IsDBNull(leitor.GetOrdinal("OPEN Transactions")) ? 0 : leitor.GetInt32(leitor.GetOrdinal("OPEN Transactions")),
                LoginTime = leitor.GetDateTime(leitor.GetOrdinal("Login TIME")),
                UltimoRequestInicio = leitor.GetDateTime(leitor.GetOrdinal("LAST Request START TIME")),
                HostName = leitor.IsDBNull(leitor.GetOrdinal("Host Name")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Host Name")),
                NetAddress = leitor.IsDBNull(leitor.GetOrdinal("Net Address")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Net Address")),
                ExecutionContextId = leitor.IsDBNull(leitor.GetOrdinal("Execution Context ID")) ? 0 : leitor.GetInt32(leitor.GetOrdinal("Execution Context ID")),
                RequestId = leitor.IsDBNull(leitor.GetOrdinal("Request ID")) ? 0 : leitor.GetInt32(leitor.GetOrdinal("Request ID")),
                WorkloadGroup = leitor.IsDBNull(leitor.GetOrdinal("Workload GROUP")) ? string.Empty : leitor.GetString(leitor.GetOrdinal("Workload GROUP"))
            });
        }

        return resultado;
    }

    /// <summary>
    /// Retorna as 4 métricas do painel "Overview" do Active Monitor SQL
    /// Local (% Processor Time, Waiting Tasks, Database I/O, Batch
    /// Requests/sec), já prontas para exibir (a taxa de I/O e de Batch
    /// Requests já vem calculada em <see cref="TextoMetricasOverviewLocal"/>,
    /// server-side, via um WAITFOR DELAY de 1 segundo dentro do próprio
    /// script) — nenhum cálculo de delta entre amostras é mais necessário
    /// no cliente (diferente do desenho anterior a esta versão).
    /// </summary>
    /// <remarks>
    /// Por ser um lote único (ver remarks de <see cref="TextoMetricasOverviewLocal"/>),
    /// o try/catch aqui é ÚNICO para as 4 métricas — uma falha em qualquer
    /// instrução do lote (DMV indisponível, permissão negada, timeout) deixa
    /// as 4 nulas, e a tela mostra "N/D" nos 4 gráficos, sem derrubar o
    /// resto da atualização (grade de processos, Recent Expensive Queries,
    /// Conexões por Aplicação/Usuário) via Task.WhenAll em
    /// Menu_Raiz.AtualizarTudoAsync.
    /// </remarks>
    public async Task<MetricasOverviewDto> ObterMetricasOverviewAsync(CancellationToken ct = default)
    {
        var resultado = new MetricasOverviewDto();
        try
        {
            await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = TextoMetricasOverviewLocal;
            // O script tem um WAITFOR DELAY '00:00:01' embutido — folga no
            // timeout do comando pra não estourar em instâncias mais lentas
            // (o padrão do SqlCommand, 30s, já cobre isso com folga; fica
            // explícito aqui só pra documentar a expectativa).
            comando.CommandTimeout = 30;

            await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
            if (await leitor.ReadAsync(ct))
            {
                var ordCpu = leitor.GetOrdinal("% Processor Time");
                var ordEspera = leitor.GetOrdinal("Waiting Tasks");
                var ordIo = leitor.GetOrdinal("Database I/O (MB/sec)");
                var ordBatch = leitor.GetOrdinal("Batch Requests/sec");

                resultado.PercentualProcessadorSql = leitor.IsDBNull(ordCpu) ? null : Convert.ToDouble(leitor.GetValue(ordCpu));
                resultado.TarefasEsperando = leitor.IsDBNull(ordEspera) ? null : Convert.ToInt32(leitor.GetValue(ordEspera));
                resultado.DatabaseIoMBps = leitor.IsDBNull(ordIo) ? null : Convert.ToDouble(leitor.GetValue(ordIo));
                resultado.BatchRequestsPorSegundo = leitor.IsDBNull(ordBatch) ? null : Convert.ToInt64(leitor.GetValue(ordBatch));
            }
        }
        catch
        {
            // Falha em qualquer instrução do lote (ver remarks) — as 4
            // métricas ficam nulas, tela mostra "N/D" nos 4 gráficos.
        }

        resultado.ColetadoEm = DateTime.Now;
        return resultado;
    }
}
