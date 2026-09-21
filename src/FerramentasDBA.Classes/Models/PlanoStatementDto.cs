namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um statement (uma instrução T-SQL) dentro de um plano de execução
/// (.sqlplan) do SQL Server — StmtSimple/StmtCursor/StmtCond/StmtUseDb no
/// XML do ShowPlan. Um mesmo arquivo .sqlplan pode conter vários batches e
/// vários statements por batch; cada um vira um PlanoStatementDto na lista
/// retornada por Modulo_PlanoExecucao.AnalisarArquivoAsync/AnalisarXmlBruto,
/// exibida na grade "Instruções encontradas" da tela Manutenção &gt;
/// Análise do plano de execução.
/// </summary>
public sealed class PlanoStatementDto
{
    public string StatementText { get; set; } = string.Empty;

    /// <summary>Ex.: "SELECT", "INSERT", "UPDATE" — vem do atributo StatementType do XML.</summary>
    public string StatementType { get; set; } = string.Empty;

    public decimal CustoSubTree { get; set; }
    public decimal LinhasEstimadas { get; set; }
    public decimal? TempoCompilacaoMs { get; set; }
    public decimal? CpuCompilacaoMs { get; set; }
    public decimal? MemoriaCompilacaoKb { get; set; }
    public decimal? MemoriaConcedidaKb { get; set; }

    /// <summary>
    /// True quando o plano tem &lt;RunTimeInformation&gt; (plano REAL,
    /// executado — "Include Actual Execution Plan" no SSMS); false quando é
    /// só ESTIMADO (nunca rodou de fato — os números de linhas/custo são só
    /// a previsão do otimizador, sem contadores reais de execução).
    /// </summary>
    public bool EhPlanoReal { get; set; }

    /// <summary>Versão em português de <see cref="EhPlanoReal"/>, usada no resumo exibido na tela.</summary>
    public string DescricaoTipoPlano => EhPlanoReal ? "Real (executado)" : "Estimado (não executado)";

    /// <summary>Lista ACHATADA de todos os operadores (pré-ordem, com <see cref="PlanoOperadorDto.Profundidade"/>) — usada pelas grades "Operadores mais custosos" e "Scans em vez de Seeks". Para a árvore de verdade (usada pelo diagrama gráfico), ver <see cref="OperadorRaiz"/>.</summary>
    public List<PlanoOperadorDto> Operadores { get; set; } = new();

    /// <summary>
    /// Operador raiz do statement, com <see cref="PlanoOperadorDto.Filhos"/>
    /// preenchido recursivamente — é a mesma árvore que <see cref="Operadores"/>
    /// representa de forma achatada, só que aqui como estrutura de verdade
    /// (pai/filho), usada pelo diagrama gráfico (aba "Gráfico") em vez das
    /// grades. Null quando o statement não tem plano (ex.: um "USE [Banco]").
    /// </summary>
    public PlanoOperadorDto? OperadorRaiz { get; set; }

    /// <summary>Avisos gerais do statement (vieram direto de &lt;QueryPlan&gt;/&lt;Warnings&gt;) — avisos de um operador específico ficam em Operadores[i].Avisos.</summary>
    public List<PlanoAvisoDto> Avisos { get; set; } = new();

    public List<PlanoIndiceFaltanteDto> IndicesFaltantes { get; set; } = new();

    /// <summary>Usado como coluna da grade "Instruções encontradas" — primeiros ~80 caracteres do texto do statement, numa linha só (sem quebras).</summary>
    public string ResumoTexto
    {
        get
        {
            var texto = StatementText.Replace("\r", " ").Replace("\n", " ").Trim();
            while (texto.Contains("  "))
            {
                texto = texto.Replace("  ", " ");
            }
            return texto.Length > 80 ? texto.Substring(0, 80) + "..." : texto;
        }
    }

    public int QuantidadeOperadores => Operadores.Count;

    /// <summary>Total de avisos, somando os gerais do statement com os de cada operador individual.</summary>
    public int QuantidadeAvisos => Avisos.Count + Operadores.Sum(o => o.Avisos.Count);

    public int QuantidadeIndicesFaltantes => IndicesFaltantes.Count;

    public int QuantidadeScans => Operadores.Count(o => o.EhScan);

    /// <summary>O operador com maior "custo exclusivo" da árvore inteira — normalmente o principal candidato a otimização.</summary>
    public PlanoOperadorDto? OperadorMaisCustoso => Operadores.OrderByDescending(o => o.CustoExclusivo).FirstOrDefault();

    /// <summary>Todos os avisos do statement (gerais + de cada operador), já combinados numa lista só — usado para preencher a grade "Alertas".</summary>
    public List<PlanoAvisoDto> TodosOsAvisos =>
        Avisos.Concat(Operadores.SelectMany(o => o.Avisos)).ToList();
}
