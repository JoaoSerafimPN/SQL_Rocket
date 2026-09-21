using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno de "Monitoramento &gt; Active Monitor SQL Local", a
/// partir do script fornecido pelo usuário (sys.dm_exec_sessions LEFT JOIN
/// sys.dm_exec_connections/sys.dm_exec_requests/sys.dm_os_tasks, mais o
/// cálculo de "Head Blocker" via auto-join em sys.dm_exec_requests para
/// achar quem está bloqueando outras sessões, e sys.dm_os_waiting_tasks
/// para tempo/tipo/recurso de espera — o mesmo conjunto de DMVs usado pela
/// tela "Processes" do Activity Monitor nativo do SSMS). Preenchido em
/// Modulo_Desempenho.ObterActiveMonitorLocalAsync.
/// </summary>
/// <remarks>
/// Todos os nomes de propriedade/coluna aqui seguem o alias em INGLÊS do
/// script original (ex.: <see cref="SessionId"/> para "SESSION ID"), para
/// deixar claro que cada coluna veio de um alias específico do script, sem
/// tradução — só os comentários/[DisplayName] auxiliares são em português.
///
/// <see cref="BlockedBy"/>/<see cref="HeadBlocker"/> não vêm normalizados
/// como booleano — são exatamente a string que o script produz ("" ou "0"
/// quando não há bloqueio, o session_id de quem bloqueia quando há; "1" ou
/// "" para Head Blocker). Menu_Raiz.MostrarPaginaActiveMonitorLocal usa
/// essas duas colunas para destacar visualmente (cor de fundo) as linhas
/// bloqueadas/bloqueadoras na grade — isso é só apresentação, não altera os
/// dados retornados pelo script.
/// </remarks>
public sealed class ProcessoAtivoDto
{
    [DisplayName("Session Id")]
    public int SessionId { get; set; }

    [DisplayName("User Process")]
    public string UserProcess { get; set; } = string.Empty;

    [DisplayName("Login")]
    public string Login { get; set; } = string.Empty;

    // Texto completo do comando em execução — largura fixada e clique para
    // abrir em janela separada em Menu_Raiz (mesmo padrão já usado para o
    // plano de execução em Desempenho > Top 25 consultas), já que pode ser
    // um batch inteiro bem longo.
    [DisplayName("Comando exec (clique para abrir)")]
    public string? ComandoExec { get; set; }

    [DisplayName("Database")]
    public string Database { get; set; } = string.Empty;

    [DisplayName("Task State")]
    public string TaskState { get; set; } = string.Empty;

    [DisplayName("Command")]
    public string Command { get; set; } = string.Empty;

    [DisplayName("Application")]
    public string Application { get; set; } = string.Empty;

    [DisplayName("Wait Time (ms)")]
    public long WaitTimeMs { get; set; }

    [DisplayName("Wait Type")]
    public string WaitType { get; set; } = string.Empty;

    [DisplayName("Wait Resource")]
    public string WaitResource { get; set; } = string.Empty;

    [DisplayName("Blocked By")]
    public string BlockedBy { get; set; } = string.Empty;

    [DisplayName("Head Blocker")]
    public string HeadBlocker { get; set; } = string.Empty;

    [DisplayName("Total CPU (ms)")]
    public int TotalCpuMs { get; set; }

    [DisplayName("Total Physical I/O (MB)")]
    public long TotalIoFisicoMb { get; set; }

    [DisplayName("Memory Use (KB)")]
    // long (não int): a coluna vem como BIGINT do servidor desde a correção do
    // overflow aritmético em Modulo_Desempenho (uma sessão pode passar de 2 GB).
    public long MemoriaUsoKb { get; set; }

    [DisplayName("Open Transactions")]
    public int TransacoesAbertas { get; set; }

    [DisplayName("Login Time")]
    public DateTime LoginTime { get; set; }

    [DisplayName("Last Request Start Time")]
    public DateTime UltimoRequestInicio { get; set; }

    [DisplayName("Host Name")]
    public string HostName { get; set; } = string.Empty;

    [DisplayName("Net Address")]
    public string NetAddress { get; set; } = string.Empty;

    [DisplayName("Execution Context Id")]
    public int ExecutionContextId { get; set; }

    [DisplayName("Request Id")]
    public int RequestId { get; set; }

    [DisplayName("Workload Group")]
    public string WorkloadGroup { get; set; } = string.Empty;
}
