using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno da grade "Processes" do Active Monitor SQL Azure —
/// script fornecido pelo usuário depois que as versões anteriores (baseadas
/// no mesmo script do Active Monitor SQL Local, com sys.sysprocesses, e
/// depois em variações com sys.dm_os_tasks/sys.dm_os_waiting_tasks)
/// continuaram batendo em "VIEW SERVER PERFORMANCE STATE permission was
/// denied..." no ambiente Azure do usuário.
/// </summary>
/// <remarks>
/// Bem mais simples que <see cref="ProcessoAtivoDto"/> (Local): só
/// sys.dm_exec_requests INNER JOIN sys.dm_exec_sessions (mais
/// sys.dm_exec_sql_text) — por ser INNER JOIN em sys.dm_exec_requests, só
/// aparecem sessões com uma requisição ATIVA no momento da consulta
/// (sessões ociosas, sem comando rodando, não aparecem — diferente do
/// Local, que lista TODAS as sessões via LEFT JOIN). O filtro
/// "s.is_user_process = 1" já exclui tarefas internas do SQL Server
/// (equivalente ao que o checkbox "Somente comandos de usuário" faz no
/// Local por cima da lista completa) e "r.session_id &lt;&gt; @@SPID"
/// exclui a própria sessão desta consulta.
///
/// Não existe uma coluna de banco de dados por linha neste script (ao
/// contrário do Local) — por isso a tela do Azure filtra só por
/// Usuário/Aplicação, sem filtro de Banco.
/// </remarks>
public sealed class ProcessoAtivoAzureDto
{
    [DisplayName("SPID")]
    public int Spid { get; set; }

    [DisplayName("Status")]
    public string Status { get; set; } = string.Empty;

    // 0 = não bloqueado (mesma convenção do script do Local).
    [DisplayName("Bloqueado Por")]
    public int BloqueadoPor { get; set; }

    [DisplayName("CPU Time (ms)")]
    public int CpuTimeMs { get; set; }

    [DisplayName("Tempo Total (ms)")]
    public int TempoTotalMs { get; set; }

    [DisplayName("Tipo Espera")]
    public string TipoEspera { get; set; } = string.Empty;

    [DisplayName("Tempo Espera (ms)")]
    public int TempoEsperaMs { get; set; }

    // Texto completo da instrução em execução — largura fixada e clique
    // para abrir em janela separada em Menu_Raiz (mesmo padrão já usado
    // para "Comando exec" no Local e para o plano de execução em
    // Desempenho > Top 25 consultas).
    [DisplayName("Query em Execução (clique para abrir)")]
    public string? QueryEmExecucao { get; set; }

    [DisplayName("Aplicação")]
    public string Aplicacao { get; set; } = string.Empty;

    [DisplayName("Usuário")]
    public string Usuario { get; set; } = string.Empty;
}
