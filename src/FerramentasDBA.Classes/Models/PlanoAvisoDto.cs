namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um aviso (&lt;Warnings&gt;) do plano de execução — podem aparecer tanto
/// no nível do statement inteiro (filho direto de &lt;QueryPlan&gt;) quanto
/// dentro de um operador específico (filho de um &lt;RelOp&gt;, caso em que
/// <see cref="NodeIdOperador"/> é preenchido). Preenchido em
/// Modulo_PlanoExecucao.ExtrairAvisos.
/// </summary>
public sealed class PlanoAvisoDto
{
    /// <summary>
    /// Nome técnico do aviso, ex.: "NoJoinPredicate", "SpillToTempDb",
    /// "ColumnsWithNoStatistics", "Wait", "PlanAffectingConvert" — útil
    /// para quem quiser pesquisar o termo na documentação da Microsoft.
    /// </summary>
    public string Tipo { get; set; } = string.Empty;

    /// <summary>Explicação em português, já pronta para exibir na tela.</summary>
    public string Descricao { get; set; } = string.Empty;

    /// <summary>
    /// "Crítico" ou "Atenção" — heurística própria (não vem do SQL Server)
    /// para ajudar a priorizar qual aviso olhar primeiro.
    /// </summary>
    public string Severidade { get; set; } = "Atenção";

    /// <summary>
    /// NodeId do operador dono deste aviso, quando o aviso veio de dentro
    /// de um &lt;RelOp&gt; específico; null quando é um aviso geral do
    /// statement (veio direto de &lt;QueryPlan&gt;).
    /// </summary>
    public int? NodeIdOperador { get; set; }

    /// <summary>
    /// Dado técnico extra (ex.: "SpillLevel=1", tipo/tempo de espera,
    /// expressão convertida) — não obrigatório, só quando ajuda a entender
    /// o aviso além do texto de <see cref="Descricao"/>.
    /// </summary>
    public string? DetalheAdicional { get; set; }
}
