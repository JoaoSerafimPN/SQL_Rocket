namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Amostra do painel "Overview" do Active Monitor SQL Azure, a partir de
/// sys.dm_db_resource_stats — script fornecido pelo usuário depois que a
/// mistura de DMVs usada anteriormente (sys.dm_os_ring_buffers,
/// sys.dm_os_waiting_tasks, sys.dm_io_virtual_file_stats,
/// sys.dm_os_performance_counters) bateu em "VIEW SERVER PERFORMANCE
/// STATE permission was denied..." no ambiente Azure do usuário.
/// </summary>
/// <remarks>
/// sys.dm_db_resource_stats é database-scoped (não exige "VIEW SERVER
/// PERFORMANCE STATE", que é uma permissão de escopo de SERVIDOR) e já
/// retorna as 6 métricas prontas em PERCENTUAL (0-100) — diferente das
/// métricas antigas (bytes/contadores cumulativos), não precisa de nenhum
/// cálculo de taxa/delta entre amostras aqui. Preenchido em
/// Modulo_Desempenho.ObterMetricasRecursoAzureAsync.
///
/// Todas as propriedades são nullable: essa DMV pode não ter nenhuma linha
/// ainda num banco recém-criado (~1h de retenção, uma amostra a cada ~15s),
/// e a consulta inteira roda dentro de um try/catch (mesmo padrão já usado
/// nas métricas do Local/versão anterior do Overview do Azure) — uma falha
/// aqui não derruba a atualização da tela, só mostra "N/D" nos gráficos.
/// </remarks>
public sealed class MetricasRecursoAzureDto
{
    public DateTime? DataHoraUtc { get; set; }

    public double? CpuPercent { get; set; }

    public double? MemoriaPercent { get; set; }

    public double? DataIoPercent { get; set; }

    public double? LogWritePercent { get; set; }

    public double? WorkersPercent { get; set; }

    public double? SessoesPercent { get; set; }
}
