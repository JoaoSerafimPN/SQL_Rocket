using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno do "Top N consultas que mais consomem processamento"
/// (Desempenho > Top 25 consultas), a partir de sys.dm_exec_query_stats +
/// sys.dm_exec_sql_text + sys.dm_exec_query_plan.
/// Preenchido em Modulo_Desempenho.ObterTop25ConsultasCustosasAsync.
/// </summary>
/// <remarks>
/// Os tempos (TempoCpu.../TempoDecorrido...) vêm do SQL Server em
/// MICROSSEGUNDOS (não milissegundos) — é a unidade nativa dessas colunas em
/// sys.dm_exec_query_stats. Os atributos [DisplayName] controlam o
/// cabeçalho de cada coluna no grid (o DataGridView, com colunas
/// auto-geradas, respeita esse atributo do mesmo jeito que o PropertyGrid).
/// [Browsable(false)] em TextoCompleto esconde o texto completo do batch
/// (redundante com InstrucaoSql, que já traz só a instrução do plano) da
/// grade. PlanoExecucaoXml FICA visível como coluna propositalmente: a tela
/// (Menu_Raiz.MostrarPaginaTop25Consultas) fixa a largura dessa coluna e
/// trata o clique nela para abrir o plano completo em uma janela separada,
/// em vez de deixar a grade tentar se ajustar ao tamanho do XML.
/// </remarks>
public sealed class ConsultaCustosaDto
{
    [DisplayName("Banco de Dados")]
    public string? Banco { get; set; }

    [DisplayName("Execuções")]
    public long ContagemExecucoes { get; set; }

    [DisplayName("Gerações do Plano")]
    public long NumeroGeracoesPlano { get; set; }

    [DisplayName("Última Execução")]
    public DateTime UltimaExecucao { get; set; }

    [DisplayName("CPU Total (µs)")]
    public long TempoCpuTotal { get; set; }

    [DisplayName("CPU Última (µs)")]
    public long TempoCpuUltimo { get; set; }

    [DisplayName("CPU Mínima (µs)")]
    public long TempoCpuMinimo { get; set; }

    [DisplayName("CPU Máxima (µs)")]
    public long TempoCpuMaximo { get; set; }

    [DisplayName("Leituras Lógicas Total")]
    public long LeiturasLogicasTotal { get; set; }

    [DisplayName("Leituras Lógicas Última")]
    public long LeiturasLogicasUltima { get; set; }

    [DisplayName("Leituras Lógicas Mínima")]
    public long LeiturasLogicasMinima { get; set; }

    [DisplayName("Leituras Lógicas Máxima")]
    public long LeiturasLogicasMaxima { get; set; }

    [DisplayName("Leituras Físicas Total")]
    public long LeiturasFisicasTotal { get; set; }

    [DisplayName("Leituras Físicas Última")]
    public long LeiturasFisicasUltima { get; set; }

    [DisplayName("Leituras Físicas Mínima")]
    public long LeiturasFisicasMinima { get; set; }

    [DisplayName("Leituras Físicas Máxima")]
    public long LeiturasFisicasMaxima { get; set; }

    [DisplayName("Escritas Lógicas Total")]
    public long EscritasLogicasTotal { get; set; }

    [DisplayName("Escritas Lógicas Última")]
    public long EscritasLogicasUltima { get; set; }

    [DisplayName("Escritas Lógicas Mínima")]
    public long EscritasLogicasMinima { get; set; }

    [DisplayName("Escritas Lógicas Máxima")]
    public long EscritasLogicasMaxima { get; set; }

    [DisplayName("Tempo Decorrido Total (µs)")]
    public long TempoDecorridoTotal { get; set; }

    [DisplayName("Tempo Decorrido Último (µs)")]
    public long TempoDecorridoUltimo { get; set; }

    [DisplayName("Tempo Decorrido Mínimo (µs)")]
    public long TempoDecorridoMinimo { get; set; }

    [DisplayName("Tempo Decorrido Máximo (µs)")]
    public long TempoDecorridoMaximo { get; set; }

    [DisplayName("Instrução SQL")]
    public string InstrucaoSql { get; set; } = string.Empty;

    [Browsable(false)]
    public string? TextoCompleto { get; set; }

    [DisplayName("Plano de Execução (clique para abrir)")]
    public string? PlanoExecucaoXml { get; set; }
}
