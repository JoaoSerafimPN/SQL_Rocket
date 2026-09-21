namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um operador (&lt;RelOp&gt;) dentro da árvore do plano de execução — ex.:
/// Index Seek, Hash Match, Nested Loops, Sort. Preenchido em
/// Modulo_PlanoExecucao.ColetarOperadores, que percorre a árvore
/// recursivamente (os operadores filhos não são filhos diretos no XML —
/// ficam dentro de um elemento "wrapper" que varia conforme o operador do
/// pai, ex.: &lt;NestedLoops&gt;, &lt;Hash&gt;, &lt;Sort&gt;, &lt;Top&gt;).
/// </summary>
public sealed class PlanoOperadorDto
{
    public int NodeId { get; set; }

    /// <summary>Ex.: "Index Seek", "Hash Match", "Nested Loops", "Clustered Index Scan".</summary>
    public string PhysicalOp { get; set; } = string.Empty;

    /// <summary>
    /// Ex.: "Inner Join", "Aggregate" — a operação lógica (pode diferir da
    /// física; ex.: um Hash Match físico pode ser um Inner Join lógico).
    /// </summary>
    public string LogicalOp { get; set; } = string.Empty;

    /// <summary>
    /// Custo estimado ACUMULADO deste operador + toda a subárvore abaixo
    /// dele (EstimatedTotalSubtreeCost) — sempre maior ou igual ao de
    /// qualquer operador dentro da sua própria subárvore, então não serve
    /// sozinho para achar "o gargalo" (ver <see cref="CustoExclusivo"/>).
    /// </summary>
    public decimal CustoSubTreeTotal { get; set; }

    /// <summary>
    /// Custo do PRÓPRIO operador, descontando o que já foi contabilizado
    /// pelos filhos imediatos (CustoSubTreeTotal deste operador menos a
    /// soma do CustoSubTreeTotal dos filhos imediatos). Não existe como
    /// atributo no XML do SQL Server — é calculado em
    /// Modulo_PlanoExecucao.ColetarOperadores porque é uma métrica muito
    /// mais útil para achar "onde está o gargalo" do que o custo acumulado
    /// (que é sempre dominado pelo operador raiz da árvore). Usado para
    /// ordenar a grade "Operadores mais custosos".
    /// </summary>
    public decimal CustoExclusivo { get; set; }

    /// <summary>
    /// <see cref="CustoExclusivo"/> deste operador como percentual do custo
    /// total do statement (0 a 100) — a mesma informação que o SSMS mostra
    /// embaixo de cada ícone no plano gráfico (ex.: "Cost: 13%"), calculada
    /// aqui como CustoExclusivo / custo do operador raiz do statement.
    /// Preenchida em Modulo_PlanoExecucao.ColetarOperadores.
    /// </summary>
    public decimal PercentualCusto { get; set; }

    /// <summary>Usado como coluna da grade — ex.: "13,4%", no mesmo formato que o SSMS mostra no plano gráfico.</summary>
    public string DescricaoPercentualCusto => $"{PercentualCusto:N1}%";

    public decimal LinhasEstimadas { get; set; }

    /// <summary>
    /// Linhas realmente retornadas na execução (soma de ActualRows de
    /// todas as threads em RunTimeCountersPerThread) — só existe quando o
    /// plano é REAL (veio com "Include Actual Execution Plan"), null em
    /// plano estimado.
    /// </summary>
    public decimal? LinhasReais { get; set; }

    public bool EhScan { get; set; }
    public bool EhSeek { get; set; }

    /// <summary>
    /// Tabela/índice acessado — só preenchido para operadores de acesso a
    /// dados (Index Scan, Index Seek, Table Scan, Clustered Index Scan/Seek),
    /// obtido do &lt;Object&gt; dentro de &lt;IndexScan&gt;/&lt;TableScan&gt;.
    /// </summary>
    public string? ObjetoAlvo { get; set; }

    /// <summary>
    /// Profundidade na árvore (0 = operador raiz do statement) — usada
    /// para indentar a grade de operadores e deixar a hierarquia visível.
    /// </summary>
    public int Profundidade { get; set; }

    /// <summary>Avisos (&lt;Warnings&gt;) registrados especificamente neste operador (não confundir com os avisos gerais do statement, em PlanoStatementDto.Avisos).</summary>
    public List<PlanoAvisoDto> Avisos { get; set; } = new();

    /// <summary>
    /// Diferença grande entre estimado e real (só quando o plano é real)
    /// costuma indicar estatísticas desatualizadas — heurística própria:
    /// real mais de 10x maior/menor que o estimado, com um piso de 100
    /// linhas para evitar ruído em números pequenos (ex.: 1 estimado vs.
    /// 15 reais não é um problema de verdade).
    /// </summary>
    public bool EstimativaMuitoDivergente =>
        LinhasReais.HasValue && LinhasEstimadas > 0 && LinhasReais.Value > 100 &&
        (LinhasReais.Value > LinhasEstimadas * 10 || LinhasReais.Value < LinhasEstimadas / 10);

    /// <summary>Usado como coluna da grade "Operadores mais custosos".</summary>
    public string DescricaoDivergencia => EstimativaMuitoDivergente ? "Sim — considere atualizar estatísticas" : "-";

    /// <summary>Usado como coluna da grade de operadores — ex.: "Index Seek — Vendas.IX_Vendas_Data".</summary>
    public string Descricao => string.IsNullOrEmpty(ObjetoAlvo) ? PhysicalOp : $"{PhysicalOp} — {ObjetoAlvo}";

    /// <summary>Texto indentado conforme <see cref="Profundidade"/>, para a coluna "Operador" da grade deixar a hierarquia da árvore visível (ex.: "····Index Seek").</summary>
    public string DescricaoIndentada => new string('·', Profundidade * 2) + (Profundidade > 0 ? " " : string.Empty) + Descricao;

    /// <summary>
    /// Filhos diretos deste operador na árvore do plano (ver
    /// Modulo_PlanoExecucao.ObterFilhosImediatosRelOp) — vazio num
    /// operador-folha (ex.: um Index Seek/Scan, que não lê de mais
    /// ninguém). Usado pelo diagrama gráfico (Manutenção > Análise do
    /// plano de execução > aba "Gráfico"); a grade "Operadores mais
    /// custosos" usa a lista achatada em PlanoStatementDto.Operadores
    /// (com <see cref="Profundidade"/>), não esta árvore.
    /// </summary>
    public List<PlanoOperadorDto> Filhos { get; set; } = new();

    // ---- Campos abaixo: mesma informação que o SSMS mostra no tooltip
    // (dica) ao passar o mouse sobre um operador no plano gráfico — usados
    // pela dica (hover) do diagrama (PlanoExecucaoDiagrama), não pelas
    // grades. Todos preenchidos em Modulo_PlanoExecucao.ColetarOperadores.

    /// <summary>"Estimated I/O Cost" do SSMS — atributo EstimateIO do &lt;RelOp&gt;. Null quando o atributo não existe no XML.</summary>
    public decimal? CustoIO { get; set; }

    /// <summary>"Estimated CPU Cost" do SSMS — atributo EstimateCPU do &lt;RelOp&gt;. Null quando o atributo não existe no XML.</summary>
    public decimal? CustoCPU { get; set; }

    /// <summary>"Estimated Row Size" do SSMS, em bytes — atributo AvgRowSize do &lt;RelOp&gt;.</summary>
    public decimal? TamanhoLinhaBytes { get; set; }

    /// <summary>
    /// "Estimated Execution Mode" do SSMS ("Row" ou "Batch") — atributo
    /// EstimatedExecutionMode do &lt;RelOp&gt;; null em planos de versões
    /// do SQL Server sem execução em lote (batch mode), onde o atributo
    /// simplesmente não existe no XML.
    /// </summary>
    public string? ModoExecucaoEstimado { get; set; }

    /// <summary>
    /// "Storage" do SSMS ("RowStore" ou "ColumnStore") — atributo Storage
    /// de &lt;IndexScan&gt;/&lt;TableScan&gt;; só existe em operadores de
    /// acesso a dados (null nos demais).
    /// </summary>
    public string? Armazenamento { get; set; }

    /// <summary>
    /// "Ordered" do SSMS — atributo Ordered de
    /// &lt;IndexScan&gt;/&lt;TableScan&gt;; só existe em operadores de
    /// acesso a dados (null nos demais).
    /// </summary>
    public bool? Ordenado { get; set; }

    /// <summary>
    /// "Estimated Number of Rows to be Read" do SSMS — atributo
    /// EstimatedRowsRead do &lt;RelOp&gt; (só existe em planos gerados a
    /// partir do SQL Server 2016 SP1 em diante, e normalmente só aparece
    /// em scans/seeks com um predicado residual — quando o operador
    /// precisa LER mais linhas do que de fato devolve). Null quando o
    /// atributo não existe no XML.
    /// </summary>
    public decimal? LinhasParaLer { get; set; }

    /// <summary>
    /// "Estimated Number of Executions" do SSMS — quantas vezes este
    /// operador é invocado durante a execução do statement (relevante
    /// principalmente para o lado interno de um Nested Loops, invocado
    /// uma vez por linha do lado externo). NÃO existe como atributo direto
    /// no XML — calculado em Modulo_PlanoExecucao.ColetarOperadores como
    /// o número de execuções do operador PAI multiplicado pela soma de
    /// EstimateRebinds+EstimateRewinds deste próprio operador; o operador
    /// raiz do statement sempre tem valor 1.
    /// </summary>
    public decimal NumeroExecucoesEstimado { get; set; } = 1m;

    /// <summary>"Estimated Number of Rows for All Executions" do SSMS — <see cref="LinhasEstimadas"/> (que já é "por execução", como no SSMS) vezes <see cref="NumeroExecucoesEstimado"/>.</summary>
    public decimal LinhasEstimadasTodasExecucoes => LinhasEstimadas * NumeroExecucoesEstimado;

    /// <summary>
    /// "Output List" do SSMS — as colunas que este operador devolve para
    /// quem está acima dele na árvore, já formatadas como
    /// "Banco.Schema.Tabela.Coluna" (ou "Banco.Schema.Alias.Coluna" quando
    /// não há nome de tabela — ex.: uma coluna calculada por um Compute
    /// Scalar). Vem de &lt;OutputList&gt;&lt;ColumnReference&gt;.
    /// </summary>
    public List<string> ListaSaida { get; set; } = new();
}
