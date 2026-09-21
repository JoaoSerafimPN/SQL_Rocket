using System.Globalization;
using System.Xml.Linq;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// A ANÁLISE do plano de execução (arquivo .sqlplan — XML no formato
/// "ShowPlan" exportado pelo SQL Server Management Studio, tanto estimado
/// quanto real/"Include Actual Execution Plan") é 100% offline, sem nenhuma
/// conexão com banco — mesmo padrão do Modulo_Deadlock (Manutenção &gt;
/// Headblock e DeadLock): o usuário envia o arquivo, tudo é extraído a
/// partir dele.
///
/// Cobre as 4 áreas de análise pedidas pelo usuário: resumo geral do
/// statement, operadores mais custosos (por "custo exclusivo" — ver
/// <see cref="ColetarOperadores"/>), Scans em vez de Seeks, e alertas/
/// sugestões de índice (&lt;Warnings&gt; e &lt;MissingIndexes&gt;).
///
/// A única operação que PRECISA de uma conexão real é
/// <see cref="CriarIndiceAsync"/> (criar automaticamente um dos índices
/// sugeridos) — por isso, diferente do Modulo_Deadlock, este módulo recebe
/// um <see cref="Conectar_SQL"/> no construtor (mesmo usado pelos outros
/// módulos "ao vivo" da tela, como Modulo_Indices), mas nada nele é usado
/// durante a análise em si.
/// </summary>
public sealed class Modulo_PlanoExecucao
{
    // Namespace padrão de todo elemento do XML do ShowPlan — sem isso,
    // XElement.Element/Elements/Descendants não encontra nada (o XML usa
    // um namespace default, não prefixado).
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>
    /// Profundidade máxima da árvore de &lt;RelOp&gt; percorrida por
    /// <see cref="ColetarOperadores"/>.
    ///
    /// POR QUÊ: a travessia é recursiva, e planos gerados por ORM (dezenas
    /// ou centenas de UNION ALL/OR aninhados) chegam a árvores
    /// profundíssimas. Sem um limite, a recursão estoura a pilha e o
    /// StackOverflowException do .NET NÃO PODE ser capturado — o processo
    /// inteiro morre na hora, sem mensagem nenhuma, levando junto o
    /// aplicativo do usuário. 500 níveis é bem acima de qualquer plano
    /// real (um plano humano raramente passa de algumas dezenas) e bem
    /// abaixo do que a pilha padrão de 1 MB aguenta para este método.
    /// </summary>
    private const int ProfundidadeMaximaOperadores = 500;

    private readonly Conectar_SQL _conexao;

    public Modulo_PlanoExecucao(Conectar_SQL conexao)
    {
        _conexao = conexao;
    }

    /// <summary>
    /// Elementos filhos "de metadados" de um &lt;RelOp&gt; que NUNCA
    /// contêm outro &lt;RelOp&gt; operador de verdade dentro deles — usados
    /// por <see cref="ObterFilhosImediatosRelOp"/> para não vasculhar
    /// dentro de listas de colunas/predicados à toa. Não precisa ser uma
    /// lista exaustiva: o pior caso de esquecer um nome aqui é só
    /// percorrer um pouco mais de XML sem achar nenhum &lt;RelOp&gt; extra
    /// (Elements(Ns + "RelOp") simplesmente não encontra nada lá dentro).
    /// </summary>
    private static readonly HashSet<string> ElementosSemOperadorFilho = new(StringComparer.OrdinalIgnoreCase)
    {
        "OutputList", "Warnings", "MemoryFractions", "RunTimeInformation",
        "DefinedValues", "GroupBy", "OrderBy", "ActionColumn", "Predicate",
        "ProbeColumn", "PassThru", "SeekPredicates", "SeekPredicateNew",
        "Object", "ColumnReference", "ScalarOperator", "Identifier",
        "ColumnReferenceList",
    };

    /// <summary>Lê o arquivo .sqlplan do disco e analisa — ver <see cref="AnalisarXmlBruto"/>.</summary>
    public async Task<List<PlanoStatementDto>> AnalisarArquivoAsync(string caminhoArquivo, CancellationToken cancellationToken = default)
    {
        // Recusa arquivos grandes demais ANTES de tentar carregar: a análise lê
        // o arquivo inteiro em memória e monta uma árvore XML por cima (várias
        // vezes o tamanho do texto). Sem esta checagem, um .sqlplan muito grande
        // derrubava o processo por falta de memória, sem mensagem nenhuma.
        var info = new FileInfo(caminhoArquivo);
        if (info.Exists && info.Length > TamanhoMaximoArquivoBytes)
        {
            var tamanhoMb = info.Length / 1024d / 1024d;
            var limiteMb = TamanhoMaximoArquivoBytes / 1024d / 1024d;
            throw new InvalidOperationException(
                $"O arquivo .sqlplan tem {tamanhoMb:N0} MB, acima do limite de {limiteMb:N0} MB que a análise " +
                "offline consegue carregar em memória com segurança. Exporte o plano de uma consulta específica " +
                "em vez do lote inteiro e tente de novo.");
        }

        string conteudo = await File.ReadAllTextAsync(caminhoArquivo, cancellationToken).ConfigureAwait(false);
        return AnalisarXmlBruto(conteudo);
    }

    /// <summary>
    /// Analisa o XML de um plano de execução (.sqlplan) já em memória —
    /// separado de <see cref="AnalisarArquivoAsync"/> para poder ser
    /// testado/reusado sem depender de um arquivo em disco. Um único
    /// arquivo pode conter vários batches com vários statements cada; cada
    /// statement (StmtSimple/StmtCursor/StmtCond/StmtUseDb — todos
    /// identificados pelo atributo comum StatementText) vira um
    /// PlanoStatementDto na lista retornada.
    /// </summary>
    public List<PlanoStatementDto> AnalisarXmlBruto(string xml)
    {
        XElement raiz;
        try
        {
            raiz = XElement.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "O arquivo enviado não é um XML válido de plano de execução (.sqlplan). Confira se o arquivo não " +
                "foi cortado/truncado ao salvar, e se realmente é um .sqlplan (não um .sql ou uma captura de " +
                "outro tipo). Detalhe técnico: " + ex.Message, ex);
        }

        var statements = raiz.Descendants()
            .Where(e => (e.Name.LocalName == "StmtSimple" || e.Name.LocalName == "StmtCursor" ||
                         e.Name.LocalName == "StmtCond" || e.Name.LocalName == "StmtUseDb")
                        && e.Attribute("StatementText") != null)
            .ToList();

        if (statements.Count == 0)
        {
            throw new InvalidOperationException(
                "Não foi encontrado nenhum statement (StmtSimple/StmtCursor/...) neste arquivo. Confirme que é um " +
                "arquivo .sqlplan exportado do SQL Server Management Studio (botão direito no plano gráfico > " +
                "\"Save Execution Plan As...\", ou Ctrl+M/\"Include Actual Execution Plan\" antes de rodar a " +
                "consulta).");
        }

        return statements.Select(ParsearStatement).ToList();
    }

    private PlanoStatementDto ParsearStatement(XElement stmtEl)
    {
        var dto = new PlanoStatementDto
        {
            StatementText = (string?)stmtEl.Attribute("StatementText") ?? string.Empty,
            StatementType = (string?)stmtEl.Attribute("StatementType") ?? "(desconhecido)",
            CustoSubTree = ObterDecimal(stmtEl.Attribute("StatementSubTreeCost")),
            LinhasEstimadas = ObterDecimal(stmtEl.Attribute("StatementEstRows")),
        };

        var queryPlanEl = stmtEl.Element(Ns + "QueryPlan");
        if (queryPlanEl is null)
        {
            // Statements como StmtUseDb ("USE [Banco]") não têm plano de
            // execução de verdade — só o texto e o tipo já são úteis o
            // suficiente para aparecer na grade "Instruções encontradas".
            return dto;
        }

        dto.TempoCompilacaoMs = ObterDecimalOuNull(queryPlanEl.Attribute("CompileTime"));
        dto.CpuCompilacaoMs = ObterDecimalOuNull(queryPlanEl.Attribute("CompileCPU"));
        dto.MemoriaCompilacaoKb = ObterDecimalOuNull(queryPlanEl.Attribute("CompileMemory"));
        dto.EhPlanoReal = queryPlanEl.Descendants(Ns + "RunTimeInformation").Any();

        var memGrantEl = queryPlanEl.Element(Ns + "MemoryGrantInfo");
        if (memGrantEl != null)
        {
            dto.MemoriaConcedidaKb = ObterDecimalOuNull(memGrantEl.Attribute("GrantedMemory"));
        }

        var relOpRaiz = queryPlanEl.Element(Ns + "RelOp");
        if (relOpRaiz != null)
        {
            dto.OperadorRaiz = ColetarOperadores(relOpRaiz, dto.Operadores, profundidade: 0, custoTotalStatement: dto.CustoSubTree, execucoesPai: 1m);
        }

        // Avisos em nível de statement — filho direto de <QueryPlan>, não
        // de um <RelOp> específico (esses últimos ficam em cada operador
        // dentro de dto.Operadores[i].Avisos).
        var avisosQueryPlanEl = queryPlanEl.Element(Ns + "Warnings");
        if (avisosQueryPlanEl != null)
        {
            dto.Avisos.AddRange(ExtrairAvisos(avisosQueryPlanEl, nodeIdOperador: null));
        }

        var missingIndexesEl = queryPlanEl.Element(Ns + "MissingIndexes");
        if (missingIndexesEl != null)
        {
            foreach (var grupoEl in missingIndexesEl.Elements(Ns + "MissingIndexGroup"))
            {
                var indiceEl = grupoEl.Element(Ns + "MissingIndex");
                if (indiceEl != null)
                {
                    dto.IndicesFaltantes.Add(ParsearIndiceFaltante(grupoEl, indiceEl));
                }
            }
        }

        return dto;
    }

    /// <summary>
    /// Percorre a árvore de operadores recursivamente, a partir do
    /// operador raiz do statement (profundidade 0), preenchendo
    /// <paramref name="destino"/> em ordem de "pré-ordem" (pai antes dos
    /// filhos) — a mesma ordem em que o SSMS desenha a árvore de cima para
    /// baixo. Cada operador filho não é filho direto do pai no XML: fica
    /// dentro de um elemento "wrapper" cujo nome varia conforme o operador
    /// físico do pai (ex.: &lt;NestedLoops&gt;, &lt;Hash&gt;, &lt;Sort&gt;,
    /// &lt;Top&gt;, &lt;Filter&gt;...) — ver <see cref="ObterFilhosImediatosRelOp"/>.
    /// </summary>
    /// <param name="custoTotalStatement">
    /// StatementSubTreeCost do statement inteiro (constante em toda a
    /// recursão) — denominador usado para calcular
    /// <see cref="PlanoOperadorDto.PercentualCusto"/> de cada operador, a
    /// mesma informação que o SSMS mostra como "Cost: N%" no plano gráfico.
    /// </param>
    /// <param name="execucoesPai">
    /// <see cref="PlanoOperadorDto.NumeroExecucoesEstimado"/> já calculado
    /// do operador PAI (1m na chamada raiz) — usado para calcular o mesmo
    /// campo deste operador (ver o comentário da propriedade).
    /// </param>
    /// <returns>
    /// O nó criado para <paramref name="relOpEl"/>, já com
    /// <see cref="PlanoOperadorDto.Filhos"/> preenchido recursivamente —
    /// usado por <see cref="ParsearStatement"/> para montar
    /// PlanoStatementDto.OperadorRaiz (a árvore de verdade, para o
    /// diagrama gráfico), além de continuar populando <paramref name="destino"/>
    /// com a mesma lista achatada de sempre (para as grades).
    /// </returns>
    private PlanoOperadorDto ColetarOperadores(XElement relOpEl, List<PlanoOperadorDto> destino, int profundidade, decimal custoTotalStatement, decimal execucoesPai)
    {
        var operador = new PlanoOperadorDto
        {
            NodeId = (int?)relOpEl.Attribute("NodeId") ?? -1,
            PhysicalOp = (string?)relOpEl.Attribute("PhysicalOp") ?? string.Empty,
            LogicalOp = (string?)relOpEl.Attribute("LogicalOp") ?? string.Empty,
            CustoSubTreeTotal = ObterDecimal(relOpEl.Attribute("EstimatedTotalSubtreeCost")),
            LinhasEstimadas = ObterDecimal(relOpEl.Attribute("EstimateRows")),
            Profundidade = profundidade,
            CustoIO = ObterDecimalOuNull(relOpEl.Attribute("EstimateIO")),
            CustoCPU = ObterDecimalOuNull(relOpEl.Attribute("EstimateCPU")),
            TamanhoLinhaBytes = ObterDecimalOuNull(relOpEl.Attribute("AvgRowSize")),
            ModoExecucaoEstimado = (string?)relOpEl.Attribute("EstimatedExecutionMode"),
            LinhasParaLer = ObterDecimalOuNull(relOpEl.Attribute("EstimatedRowsRead")),
        };

        // "Número de execuções estimado" (SSMS): não existe como atributo
        // direto — é o número de execuções do PAI multiplicado pela soma
        // de EstimateRebinds+EstimateRewinds deste próprio operador (um
        // operador "normal", invocado uma única vez pelo pai, já tem essa
        // soma valendo 1 no XML: rebind conta a 1ª invocação e as demais
        // reaproveitam o mesmo plano/parâmetros). Se algum dos dois
        // atributos não existir (planos bem antigos), a soma cai para 0 —
        // nesse caso assume-se 1 em vez de deixar a árvore inteira zerada
        // por causa de um valor ausente.
        var rebinds = ObterDecimalOuNull(relOpEl.Attribute("EstimateRebinds")) ?? 0m;
        var rewinds = ObterDecimalOuNull(relOpEl.Attribute("EstimateRewinds")) ?? 0m;
        var fatorExecucoes = rebinds + rewinds;
        if (fatorExecucoes <= 0m)
        {
            fatorExecucoes = 1m;
        }
        operador.NumeroExecucoesEstimado = execucoesPai * fatorExecucoes;

        var runtimeInfoEl = relOpEl.Element(Ns + "RunTimeInformation");
        if (runtimeInfoEl != null)
        {
            // "Linhas reais" soma os contadores de TODAS as threads
            // (RunTimeCountersPerThread) — visão "total" do operador, não
            // por thread individual (paralelismo não é detalhado aqui).
            var contadores = runtimeInfoEl.Elements(Ns + "RunTimeCountersPerThread").ToList();
            if (contadores.Count > 0)
            {
                operador.LinhasReais = contadores.Sum(c => ObterDecimal(c.Attribute("ActualRows")));
            }
        }

        // Objeto alvo (tabela/índice) — só existe em operadores de acesso a
        // dados (Index Scan, Index Seek, Table Scan, Clustered Index
        // Scan/Seek — todos usam o elemento <IndexScan>, o PhysicalOp no
        // <RelOp> pai é quem diz se é Scan ou Seek de verdade).
        var indexScanEl = relOpEl.Element(Ns + "IndexScan");
        var tableScanEl = relOpEl.Element(Ns + "TableScan");
        var objetoPaiEl = indexScanEl ?? tableScanEl;
        if (objetoPaiEl != null)
        {
            var objetoEl = objetoPaiEl.Element(Ns + "Object");
            if (objetoEl != null)
            {
                operador.ObjetoAlvo = DescreverObjeto(objetoEl);
            }

            // "Storage" (RowStore/ColumnStore) e "Ordered" do SSMS — vivem
            // no elemento <IndexScan>/<TableScan>, não no <RelOp> em si.
            operador.Armazenamento = (string?)objetoPaiEl.Attribute("Storage");
            operador.Ordenado = (bool?)objetoPaiEl.Attribute("Ordered");
        }

        operador.EhScan = operador.PhysicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase);
        operador.EhSeek = operador.PhysicalOp.Contains("Seek", StringComparison.OrdinalIgnoreCase);

        // "Output List" do SSMS — colunas que este operador devolve para
        // quem está acima dele na árvore.
        var outputListEl = relOpEl.Element(Ns + "OutputList");
        if (outputListEl != null)
        {
            operador.ListaSaida = outputListEl.Elements(Ns + "ColumnReference")
                .Select(DescreverColumnReferenceCompleta)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Avisos registrados especificamente neste operador (<RelOp><Warnings>...).
        var avisosOperadorEl = relOpEl.Element(Ns + "Warnings");
        if (avisosOperadorEl != null)
        {
            operador.Avisos.AddRange(ExtrairAvisos(avisosOperadorEl, operador.NodeId));
        }

        var filhos = ObterFilhosImediatosRelOp(relOpEl);

        // "Custo exclusivo": o que sobra do custo acumulado deste operador
        // depois de descontar o que os filhos imediatos já respondem —
        // arredondamento de ponto flutuante do próprio SQL Server às vezes
        // deixa esse valor um pouquinho negativo (ex.: -0.0000001), por
        // isso o Math.Max com 0 no lugar de confiar cegamente na subtração.
        var custoFilhos = filhos.Sum(f => ObterDecimal(f.Attribute("EstimatedTotalSubtreeCost")));
        operador.CustoExclusivo = Math.Max(0m, operador.CustoSubTreeTotal - custoFilhos);
        operador.PercentualCusto = custoTotalStatement > 0
            ? Math.Min(100m, operador.CustoExclusivo / custoTotalStatement * 100m)
            : 0m;

        destino.Add(operador);

        // Guarda de profundidade — ver ProfundidadeMaximaOperadores. Ao bater
        // no limite, a descida PARA aqui (a alternativa é o processo morrer
        // com StackOverflowException, que não é capturável). A truncagem não
        // é silenciosa: vira um aviso "Crítico" no próprio operador, que a
        // tela já mostra na grade de alertas (PlanoStatementDto.TodosOsAvisos)
        // e conta em "Avisos" — assim o usuário sabe que a análise abaixo
        // deste ponto está incompleta, em vez de confiar num resultado
        // parcial sem perceber. Preferido a lançar exceção: o resto do plano
        // (que é o que interessa, os operadores caros ficam no topo) continua
        // analisável.
        if (profundidade >= ProfundidadeMaximaOperadores && filhos.Count > 0)
        {
            operador.Avisos.Add(new PlanoAvisoDto
            {
                Tipo = "ProfundidadeMaximaAtingida",
                Descricao =
                    $"Este plano é profundo demais para ser analisado por inteiro: a análise foi interrompida " +
                    $"neste operador, no nível {profundidade} de aninhamento (limite de {ProfundidadeMaximaOperadores}). " +
                    "Os operadores abaixo dele NÃO entraram nas grades, nos custos nem nos demais avisos. " +
                    "Isso costuma acontecer com consultas geradas por ORM, com dezenas/centenas de UNION ALL ou " +
                    "OR aninhados — simplificar a consulta resolve tanto a análise quanto, normalmente, o " +
                    "próprio desempenho.",
                Severidade = "Crítico",
                NodeIdOperador = operador.NodeId,
                DetalheAdicional = $"Profundidade={profundidade}; Limite={ProfundidadeMaximaOperadores}",
            });

            return operador;
        }

        foreach (var filho in filhos)
        {
            operador.Filhos.Add(ColetarOperadores(filho, destino, profundidade + 1, custoTotalStatement, operador.NumeroExecucoesEstimado));
        }

        return operador;
    }

    /// <summary>
    /// Acha os &lt;RelOp&gt; que são operadores filhos DIRETOS deste
    /// operador — não estão soltos como filhos imediatos no XML, ficam um
    /// nível abaixo, dentro de um elemento "wrapper" (o nome do wrapper
    /// varia conforme o operador físico do pai: &lt;NestedLoops&gt;,
    /// &lt;Hash&gt;, &lt;Sort&gt;, &lt;Top&gt;, &lt;Filter&gt;, etc. — em
    /// vez de listar todos os nomes possíveis um por um, a lógica aqui é
    /// "qualquer filho direto que não seja um elemento de metadados
    /// conhecido (ver <see cref="ElementosSemOperadorFilho"/>) pode conter
    /// &lt;RelOp&gt; dentro dele — procura lá").
    /// </summary>
    private static List<XElement> ObterFilhosImediatosRelOp(XElement relOpEl)
    {
        var resultado = new List<XElement>();
        foreach (var filho in relOpEl.Elements())
        {
            if (ElementosSemOperadorFilho.Contains(filho.Name.LocalName))
            {
                continue;
            }
            resultado.AddRange(filho.Elements(Ns + "RelOp"));
        }
        return resultado;
    }

    /// <summary>
    /// Extrai os avisos de um elemento &lt;Warnings&gt; — parte como
    /// atributos booleanos diretos (NoJoinPredicate, SpatialGuess,
    /// UnmatchedIndexes, FullUpdateForOnlineIndexBuild), parte como
    /// elementos filhos (ColumnsWithNoStatistics, SpillToTempDb, Wait,
    /// PlanAffectingConvert) — schema confirmado na documentação do
    /// ShowPlan XML da Microsoft.
    /// </summary>
    private List<PlanoAvisoDto> ExtrairAvisos(XElement warningsEl, int? nodeIdOperador)
    {
        var resultado = new List<PlanoAvisoDto>();

        void Adicionar(string tipo, string descricao, string severidade = "Atenção", string? detalhe = null)
        {
            resultado.Add(new PlanoAvisoDto
            {
                Tipo = tipo,
                Descricao = descricao,
                Severidade = severidade,
                NodeIdOperador = nodeIdOperador,
                DetalheAdicional = detalhe,
            });
        }

        if ((bool?)warningsEl.Attribute("NoJoinPredicate") == true)
        {
            Adicionar("NoJoinPredicate",
                "Junção sem predicado (produto cartesiano) — cada linha de um lado é combinada com TODAS as " +
                "linhas do outro, o que pode gerar um número de linhas muito maior do que o esperado.",
                "Crítico");
        }
        if ((bool?)warningsEl.Attribute("SpatialGuess") == true)
        {
            Adicionar("SpatialGuess",
                "Estimativa de dados espaciais é um \"chute\" — o otimizador não tem estatísticas confiáveis " +
                "para esse tipo de dado, então a estimativa de linhas pode estar bem longe da realidade.");
        }
        if ((bool?)warningsEl.Attribute("UnmatchedIndexes") == true)
        {
            Adicionar("UnmatchedIndexes",
                "Existem índices filtrados que não puderam ser usados neste plano, porque o contexto de " +
                "execução (SET options da sessão) não é compatível com o filtro do índice.");
        }
        if ((bool?)warningsEl.Attribute("FullUpdateForOnlineIndexBuild") == true)
        {
            Adicionar("FullUpdateForOnlineIndexBuild",
                "Foi necessária uma atualização completa da tabela para uma criação de índice ONLINE.");
        }

        var colunasSemEstatisticaEl = warningsEl.Element(Ns + "ColumnsWithNoStatistics");
        if (colunasSemEstatisticaEl != null)
        {
            var colunas = colunasSemEstatisticaEl.Elements(Ns + "ColumnReference")
                .Select(DescreverColumnReference)
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();
            var listaColunas = colunas.Count > 0 ? string.Join(", ", colunas) : "(não identificadas)";
            Adicionar("ColumnsWithNoStatistics",
                $"Colunas sem estatísticas: {listaColunas} — o otimizador está estimando \"no escuro\" para " +
                "essas colunas, o que pode gerar um plano ruim. Considere atualizar as estatísticas " +
                "(UPDATE STATISTICS) ou criar um índice que as cubra.",
                "Atenção",
                colunas.Count > 0 ? listaColunas : null);
        }

        foreach (var spillEl in warningsEl.Elements(Ns + "SpillToTempDb"))
        {
            var nivel = (string?)spillEl.Attribute("SpillLevel") ?? "?";
            Adicionar("SpillToTempDb",
                $"Operação \"vazou\" para o tempdb (nível {nivel}) — a memória concedida para esta consulta não " +
                "foi suficiente e o SQL Server precisou usar disco (tempdb) para completar a operação, o que é " +
                "bem mais lento que operar só em memória.",
                "Crítico",
                $"SpillLevel={nivel}");
        }

        foreach (var waitEl in warningsEl.Elements(Ns + "Wait"))
        {
            var tipoEspera = (string?)waitEl.Attribute("WaitType") ?? "?";
            var tempoEspera = (string?)waitEl.Attribute("WaitTime") ?? "?";
            Adicionar("Wait",
                $"Espera registrada durante a execução: {tipoEspera} ({tempoEspera} ms).",
                "Atenção",
                $"{tipoEspera}={tempoEspera}ms");
        }

        foreach (var convertEl in warningsEl.Elements(Ns + "PlanAffectingConvert"))
        {
            var expressao = (string?)convertEl.Attribute("Expression") ?? "?";
            var problema = (string?)convertEl.Attribute("ConvertIssue") ?? "?";
            var ehCritico = string.Equals(problema, "Seek Plan", StringComparison.OrdinalIgnoreCase);
            Adicionar("PlanAffectingConvert",
                $"Conversão implícita de tipo afetando o plano: {expressao} (problema: {problema}) — geralmente " +
                "indica incompatibilidade de tipo entre a coluna e o parâmetro/literal comparado, o que pode " +
                "impedir o uso de um índice na coluna.",
                ehCritico ? "Crítico" : "Atenção",
                expressao);
        }

        return resultado;
    }

    private static string DescreverColumnReference(XElement colRefEl)
    {
        var tabela = SimplificarNomeObjeto((string?)colRefEl.Attribute("Table") ?? string.Empty);
        var coluna = SimplificarNomeObjeto((string?)colRefEl.Attribute("Column") ?? string.Empty);
        if (string.IsNullOrEmpty(coluna))
        {
            return string.Empty;
        }
        return string.IsNullOrEmpty(tabela) ? coluna : $"{tabela}.{coluna}";
    }

    /// <summary>
    /// Igual a <see cref="DescreverColumnReference"/>, mas para a "Output
    /// List" da dica (hover) do diagrama — inclui Banco e Schema além de
    /// Tabela/Coluna (ex.: "VixenMundoaVapor.dbo.ft_nfcestadourl.ds_uf"),
    /// e usa o Alias no lugar da Tabela quando ela não existe (ex.: uma
    /// coluna calculada por um Compute Scalar, que não vem de tabela
    /// nenhuma).
    /// </summary>
    private static string DescreverColumnReferenceCompleta(XElement colRefEl)
    {
        var banco = SimplificarNomeObjeto((string?)colRefEl.Attribute("Database") ?? string.Empty);
        var schema = SimplificarNomeObjeto((string?)colRefEl.Attribute("Schema") ?? string.Empty);
        var tabela = SimplificarNomeObjeto((string?)colRefEl.Attribute("Table") ?? string.Empty);
        var alias = SimplificarNomeObjeto((string?)colRefEl.Attribute("Alias") ?? string.Empty);
        var coluna = SimplificarNomeObjeto((string?)colRefEl.Attribute("Column") ?? string.Empty);

        if (string.IsNullOrEmpty(coluna))
        {
            return string.Empty;
        }

        var partes = new List<string>();
        if (!string.IsNullOrEmpty(banco))
        {
            partes.Add(banco);
        }
        if (!string.IsNullOrEmpty(schema))
        {
            partes.Add(schema);
        }
        if (!string.IsNullOrEmpty(tabela))
        {
            partes.Add(tabela);
        }
        else if (!string.IsNullOrEmpty(alias))
        {
            partes.Add(alias);
        }
        partes.Add(coluna);

        return string.Join(".", partes);
    }

    private PlanoIndiceFaltanteDto ParsearIndiceFaltante(XElement grupoEl, XElement indiceEl)
    {
        var dto = new PlanoIndiceFaltanteDto
        {
            Impacto = ObterDecimal(grupoEl.Attribute("Impact")),
            Banco = SimplificarNomeObjeto((string?)indiceEl.Attribute("Database") ?? string.Empty),
            Schema = SimplificarNomeObjeto((string?)indiceEl.Attribute("Schema") ?? string.Empty),
            Tabela = SimplificarNomeObjeto((string?)indiceEl.Attribute("Table") ?? string.Empty),
        };

        foreach (var grupoColunasEl in indiceEl.Elements(Ns + "ColumnGroup"))
        {
            var uso = (string?)grupoColunasEl.Attribute("Usage") ?? string.Empty;
            var colunas = ObterColunasDoGrupo(grupoColunasEl);

            if (string.Equals(uso, "EQUALITY", StringComparison.OrdinalIgnoreCase))
            {
                dto.ColunasIgualdade.AddRange(colunas);
            }
            else if (string.Equals(uso, "INEQUALITY", StringComparison.OrdinalIgnoreCase))
            {
                dto.ColunasDesigualdade.AddRange(colunas);
            }
            else if (string.Equals(uso, "INCLUDE", StringComparison.OrdinalIgnoreCase))
            {
                dto.ColunasInclude.AddRange(colunas);
            }
        }

        return dto;
    }

    private static List<string> ObterColunasDoGrupo(XElement grupoColunasEl)
    {
        return grupoColunasEl.Elements(Ns + "Column")
            .Select(c => SimplificarNomeObjeto((string?)c.Attribute("Name") ?? string.Empty))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
    }

    private static string DescreverObjeto(XElement objetoEl)
    {
        var schema = SimplificarNomeObjeto((string?)objetoEl.Attribute("Schema") ?? string.Empty);
        var tabela = SimplificarNomeObjeto((string?)objetoEl.Attribute("Table") ?? string.Empty);
        var indice = SimplificarNomeObjeto((string?)objetoEl.Attribute("Index") ?? string.Empty);

        var partes = new List<string>();
        if (!string.IsNullOrEmpty(tabela))
        {
            partes.Add(!string.IsNullOrEmpty(schema) ? $"{schema}.{tabela}" : tabela);
        }
        if (!string.IsNullOrEmpty(indice))
        {
            partes.Add($"(índice {indice})");
        }
        return partes.Count > 0 ? string.Join(" ", partes) : "(objeto não identificado)";
    }

    /// <summary>Remove os colchetes ("[dbo]" -&gt; "dbo") que o ShowPlan XML usa em nomes de objeto/coluna.</summary>
    private static string SimplificarNomeObjeto(string nome) =>
        string.IsNullOrEmpty(nome) ? nome : nome.Trim('[', ']');

    /// <summary>
    /// Executa de verdade o script de criação (CREATE NONCLUSTERED INDEX)
    /// de um dos índices sugeridos no plano — reaproveita exatamente o
    /// texto de <see cref="PlanoIndiceFaltanteDto.ScriptCriacao"/> (o mesmo
    /// mostrado na tela e copiado pelo botão "Copiar script"), então o que
    /// aparece na tela é exatamente o que roda no servidor (mesmo princípio
    /// de Modulo_Indices.CriarIndiceSugeridoAsync).
    ///
    /// ATENÇÃO: diferente da análise (100% offline, a partir só do arquivo
    /// .sqlplan), este método troca a conexão atual para o banco indicado
    /// pelo PRÓPRIO plano (<see cref="PlanoIndiceFaltanteDto.Banco"/>) — se
    /// o arquivo .sqlplan enviado foi gerado em um servidor diferente do
    /// que está conectado agora, o índice seria criado no banco errado
    /// (mesmo nome, servidor diferente). A tela avisa disso e mostra o
    /// servidor atualmente conectado antes de confirmar, mas cabe ao
    /// usuário conferir que é o servidor certo.
    /// </summary>
    public async Task CriarIndiceAsync(PlanoIndiceFaltanteDto indice, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        mensagens?.Report($"Criando índice \"{indice.NomeSugerido}\" em \"{indice.TabelaCompleta}\"...");

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct).ConfigureAwait(false);
        MudarBancoSeInformado(conexao, indice.Banco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = indice.ScriptCriacao;
        comando.CommandTimeout = 0; // criação de índice pode demorar em tabelas grandes
        await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        mensagens?.Report("Índice criado com sucesso.");
    }

    /// <summary>Troca a conexão já aberta para o banco informado (equivalente a um "USE [banco]") — mesmo helper usado em Modulo_Indices.</summary>
    private static void MudarBancoSeInformado(SqlConnection conexao, string? nomeBanco)
    {
        if (string.IsNullOrWhiteSpace(nomeBanco))
        {
            return;
        }

        try
        {
            conexao.ChangeDatabase(nomeBanco);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Não foi possível trocar para o banco \"{nomeBanco}\" (indicado pelo plano de execução) na " +
                $"conexão atual — confirme que esse banco existe no servidor conectado. Detalhe técnico: {ex.Message}", ex);
        }
    }

    private static decimal ObterDecimal(XAttribute? attr) => ObterDecimalOuNull(attr) ?? 0m;

    private static decimal? ObterDecimalOuNull(XAttribute? attr)
    {
        if (attr is null)
        {
            return null;
        }
        return decimal.TryParse(attr.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var valor)
            ? valor
            : null;
    }
    /// <summary>
    /// Tamanho máximo (em bytes) de um .sqlplan aceito para análise — mesma
    /// razão do limite em Modulo_Deadlock: o arquivo é carregado inteiro em
    /// memória e vira uma árvore XML várias vezes maior.
    /// </summary>
    private const long TamanhoMaximoArquivoBytes = 100L * 1024 * 1024;

}
