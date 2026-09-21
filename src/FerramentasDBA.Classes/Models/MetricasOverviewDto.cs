namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Amostra das 4 métricas do painel "Overview" de Monitoramento &gt; Active
/// Monitor SQL Local (% Processor Time, Waiting Tasks, Database I/O, Batch
/// Requests/sec — mesmo painel do Activity Monitor nativo do SSMS).
/// Preenchida em Modulo_Desempenho.ObterMetricasOverviewAsync, a partir do
/// script fornecido pelo usuário (Modulo_Desempenho.TextoMetricasOverviewLocal).
/// </summary>
/// <remarks>
/// Diferente de uma versão anterior deste DTO, <see cref="DatabaseIoMBps"/>
/// e <see cref="BatchRequestsPorSegundo"/> JÁ VÊM PRONTOS como taxa por
/// segundo — o script embute um WAITFOR DELAY de 1 segundo e calcula o
/// delta entre duas capturas dos contadores cumulativos server-side, então
/// a tela (Menu_Raiz.MostrarPaginaActiveMonitor) não precisa mais guardar a
/// amostra anterior nem calcular delta/tempo decorrido no cliente.
///
/// TODAS as 4 propriedades de métrica são nullable, mas hoje como um grupo
/// só: o script é executado como UM lote único (DECLARE/SET/SELECT em
/// várias instruções), então uma falha em qualquer instrução (DMV
/// indisponível, permissão negada, timeout) deixa as 4 nulas de uma vez —
/// não é mais possível uma métrica falhar isoladamente enquanto as outras 3
/// aparecem (isso só era possível no desenho anterior, com 4 consultas
/// separadas). A tela mostra "N/D" nos 4 gráficos nesse caso, sem derrubar
/// o resto da atualização (grade de processos, Recent Expensive Queries,
/// Conexões por Aplicação/Usuário).
/// </remarks>
public sealed class MetricasOverviewDto
{
    public double? PercentualProcessadorSql { get; set; }

    public int? TarefasEsperando { get; set; }

    public double? DatabaseIoMBps { get; set; }

    public long? BatchRequestsPorSegundo { get; set; }

    public DateTime ColetadoEm { get; set; } = DateTime.Now;
}
