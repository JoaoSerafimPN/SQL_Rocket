using System.Text;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using FerramentasDBA.Classes.Modulos;
using Xunit;

namespace FerramentasDBA.Classes.Tests;

/// <summary>
/// Testes de <see cref="Modulo_PlanoExecucao.AnalisarXmlBruto"/> — a análise
/// do .sqlplan é 100% offline, então dá para exercitá-la inteira com um XML
/// montado aqui mesmo, sem nenhum servidor.
///
/// O construtor exige um <see cref="Conectar_SQL"/>, mas ele só é usado por
/// CriarIndiceAsync (que não é testado aqui, porque precisa de conexão) — um
/// Conectar_SQL recém-criado, sem ConfigurarConexao, nunca abre nada.
/// </summary>
public class ModuloPlanoExecucaoTests
{
    private const string NamespaceShowPlan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static Modulo_PlanoExecucao CriarModulo() => new(new Conectar_SQL());

    /// <summary>Embrulha o miolo de um statement no esqueleto mínimo de um .sqlplan.</summary>
    private static string MontarPlano(string statementsXml) =>
        $"<ShowPlanXML xmlns=\"{NamespaceShowPlan}\" Version=\"1.539\" Build=\"16.0.1000.6\">" +
        "<BatchSequence><Batch><Statements>" + statementsXml +
        "</Statements></Batch></BatchSequence></ShowPlanXML>";

    private const string PlanoNestedLoops = """
        <StmtSimple StatementText="SELECT * FROM dbo.Pedido p JOIN dbo.Item i ON i.PedidoId = p.Id"
                    StatementType="SELECT" StatementSubTreeCost="1.5" StatementEstRows="100">
          <QueryPlan>
            <MemoryGrantInfo GrantedMemory="1024" />
            <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join"
                   EstimatedTotalSubtreeCost="1.5" EstimateRows="100">
              <NestedLoops>
                <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan"
                       EstimatedTotalSubtreeCost="1.0" EstimateRows="100">
                  <IndexScan Ordered="false" Storage="RowStore">
                    <Object Database="[Vendas]" Schema="[dbo]" Table="[Pedido]" Index="[PK_Pedido]" />
                  </IndexScan>
                </RelOp>
                <RelOp NodeId="2" PhysicalOp="Index Seek" LogicalOp="Index Seek"
                       EstimatedTotalSubtreeCost="0.2" EstimateRows="1">
                  <IndexScan Ordered="true" Storage="RowStore">
                    <Object Database="[Vendas]" Schema="[dbo]" Table="[Item]" Index="[IX_Item_PedidoId]" />
                  </IndexScan>
                </RelOp>
              </NestedLoops>
            </RelOp>
          </QueryPlan>
        </StmtSimple>
        """;

    [Fact]
    public void AnalisarXmlBruto_PlanoSimples_EncontraUmStatementComSeusDados()
    {
        var statements = CriarModulo().AnalisarXmlBruto(MontarPlano(PlanoNestedLoops));

        var stmt = Assert.Single(statements);
        Assert.Equal("SELECT", stmt.StatementType);
        Assert.Contains("FROM dbo.Pedido", stmt.StatementText);
        Assert.Equal(1.5m, stmt.CustoSubTree);
        Assert.Equal(100m, stmt.LinhasEstimadas);
        Assert.Equal((decimal?)1024m, stmt.MemoriaConcedidaKb);
    }

    [Fact]
    public void AnalisarXmlBruto_PlanoSimples_AchataOsOperadoresEmPreOrdem()
    {
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(PlanoNestedLoops))[0];

        Assert.Equal(3, stmt.Operadores.Count);
        Assert.Equal("Nested Loops", stmt.Operadores[0].PhysicalOp);
        Assert.Equal("Clustered Index Scan", stmt.Operadores[1].PhysicalOp);
        Assert.Equal("Index Seek", stmt.Operadores[2].PhysicalOp);

        // Pai antes dos filhos, e os filhos um nível abaixo.
        Assert.Equal(0, stmt.Operadores[0].Profundidade);
        Assert.Equal(1, stmt.Operadores[1].Profundidade);
        Assert.Equal(1, stmt.Operadores[2].Profundidade);
    }

    [Fact]
    public void AnalisarXmlBruto_PlanoSimples_MontaTambemAArvoreDeOperadores()
    {
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(PlanoNestedLoops))[0];

        Assert.NotNull(stmt.OperadorRaiz);
        Assert.Equal("Nested Loops", stmt.OperadorRaiz!.PhysicalOp);
        Assert.Equal(2, stmt.OperadorRaiz.Filhos.Count);
        // As folhas de acesso a dados não leem de mais ninguém.
        Assert.Empty(stmt.OperadorRaiz.Filhos[0].Filhos);
    }

    [Fact]
    public void AnalisarXmlBruto_ScanESeek_SaoClassificadosPeloOperadorFisico()
    {
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(PlanoNestedLoops))[0];

        var scan = stmt.Operadores[1];
        var seek = stmt.Operadores[2];

        Assert.True(scan.EhScan);
        Assert.False(scan.EhSeek);
        Assert.False(seek.EhScan);
        Assert.True(seek.EhSeek);
        Assert.Equal(1, stmt.QuantidadeScans);
    }

    [Fact]
    public void AnalisarXmlBruto_OperadorDeAcessoADados_IdentificaTabelaEIndice()
    {
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(PlanoNestedLoops))[0];

        Assert.Equal("dbo.Pedido (índice PK_Pedido)", stmt.Operadores[1].ObjetoAlvo);
        Assert.Equal("RowStore", stmt.Operadores[1].Armazenamento);
        Assert.False(stmt.Operadores[1].Ordenado);
        Assert.True(stmt.Operadores[2].Ordenado);
    }

    [Fact]
    public void AnalisarXmlBruto_CustoExclusivo_DescontaOQueOsFilhosJaRespondem()
    {
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(PlanoNestedLoops))[0];

        // Raiz: 1.5 acumulado menos 1.0 + 0.2 dos filhos = 0.3 (20% de 1.5).
        Assert.Equal(0.3m, stmt.Operadores[0].CustoExclusivo, 4);
        Assert.Equal(20m, stmt.Operadores[0].PercentualCusto, 4);

        // Folha: sem filhos, o custo exclusivo é o acumulado inteiro.
        Assert.Equal(1.0m, stmt.Operadores[1].CustoExclusivo, 4);

        // É o scan que domina o custo, não a raiz — é isso que a grade
        // "Operadores mais custosos" precisa mostrar primeiro.
        Assert.Equal("Clustered Index Scan", stmt.OperadorMaisCustoso!.PhysicalOp);
    }

    [Fact]
    public void AnalisarXmlBruto_PlanoSemRunTimeInformation_EhTratadoComoEstimado()
    {
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(PlanoNestedLoops))[0];

        Assert.False(stmt.EhPlanoReal);
        Assert.Equal("Estimado (não executado)", stmt.DescricaoTipoPlano);
        Assert.Null(stmt.Operadores[0].LinhasReais);
    }

    [Fact]
    public void AnalisarXmlBruto_PlanoComRunTimeInformation_EhRealESomaAsLinhasDeTodasAsThreads()
    {
        const string statement = """
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="1" StatementEstRows="1">
              <QueryPlan>
                <RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan"
                       EstimatedTotalSubtreeCost="1" EstimateRows="10">
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="1" ActualRows="500" />
                    <RunTimeCountersPerThread Thread="2" ActualRows="300" />
                  </RunTimeInformation>
                  <TableScan Ordered="false">
                    <Object Database="[Vendas]" Schema="[dbo]" Table="[Log]" />
                  </TableScan>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
            """;

        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(statement))[0];

        Assert.True(stmt.EhPlanoReal);
        Assert.Equal("Real (executado)", stmt.DescricaoTipoPlano);
        Assert.Equal((decimal?)800m, stmt.Operadores[0].LinhasReais);
        // Estimou 10 e leu 800: divergência grande o bastante para virar
        // alerta de estatística desatualizada.
        Assert.True(stmt.Operadores[0].EstimativaMuitoDivergente);
        // Sem <Object> dentro de <TableScan>? Tem — então o alvo é conhecido.
        Assert.Equal("dbo.Log", stmt.Operadores[0].ObjetoAlvo);
    }

    [Fact]
    public void AnalisarXmlBruto_AvisoNoNivelDoStatement_EhClassificadoComoCritico()
    {
        const string statement = """
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="1" StatementEstRows="1">
              <QueryPlan>
                <Warnings>
                  <SpillToTempDb SpillLevel="1" />
                </Warnings>
                <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort"
                       EstimatedTotalSubtreeCost="1" EstimateRows="1" />
              </QueryPlan>
            </StmtSimple>
            """;

        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(statement))[0];

        var aviso = Assert.Single(stmt.Avisos);
        Assert.Equal("SpillToTempDb", aviso.Tipo);
        Assert.Equal("Crítico", aviso.Severidade);
        Assert.Equal("SpillLevel=1", aviso.DetalheAdicional);
        // Aviso do statement inteiro não pertence a nenhum operador.
        Assert.Null(aviso.NodeIdOperador);
        Assert.Equal(1, stmt.QuantidadeAvisos);
    }

    [Fact]
    public void AnalisarXmlBruto_AvisoDentroDeUmOperador_GuardaONodeIdDoOperador()
    {
        const string statement = """
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="1" StatementEstRows="1">
              <QueryPlan>
                <RelOp NodeId="7" PhysicalOp="Nested Loops" LogicalOp="Inner Join"
                       EstimatedTotalSubtreeCost="1" EstimateRows="1">
                  <Warnings NoJoinPredicate="true" />
                </RelOp>
              </QueryPlan>
            </StmtSimple>
            """;

        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(statement))[0];

        Assert.Empty(stmt.Avisos);
        var aviso = Assert.Single(stmt.Operadores[0].Avisos);
        Assert.Equal("NoJoinPredicate", aviso.Tipo);
        Assert.Equal("Crítico", aviso.Severidade);
        Assert.Equal((int?)7, aviso.NodeIdOperador);
        // A grade de alertas junta os dois níveis numa lista só.
        Assert.Single(stmt.TodosOsAvisos);
    }

    [Fact]
    public void AnalisarXmlBruto_ComIndicesFaltantes_SeparaColunasPorTipoDeUso()
    {
        const string statement = """
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="1" StatementEstRows="1">
              <QueryPlan>
                <MissingIndexes>
                  <MissingIndexGroup Impact="95.5">
                    <MissingIndex Database="[Vendas]" Schema="[dbo]" Table="[Pedido]">
                      <ColumnGroup Usage="EQUALITY">
                        <Column Name="[ClienteId]" ColumnId="1" />
                      </ColumnGroup>
                      <ColumnGroup Usage="INEQUALITY">
                        <Column Name="[DataPedido]" ColumnId="2" />
                      </ColumnGroup>
                      <ColumnGroup Usage="INCLUDE">
                        <Column Name="[Valor]" ColumnId="3" />
                      </ColumnGroup>
                    </MissingIndex>
                  </MissingIndexGroup>
                </MissingIndexes>
                <RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan"
                       EstimatedTotalSubtreeCost="1" EstimateRows="1" />
              </QueryPlan>
            </StmtSimple>
            """;

        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlano(statement))[0];

        var indice = Assert.Single(stmt.IndicesFaltantes);
        Assert.Equal(95.5m, indice.Impacto);
        Assert.Equal("Vendas", indice.Banco);
        Assert.Equal("dbo.Pedido", indice.TabelaCompleta);
        Assert.Equal(new[] { "ClienteId" }, indice.ColunasIgualdade);
        Assert.Equal(new[] { "DataPedido" }, indice.ColunasDesigualdade);
        Assert.Equal(new[] { "Valor" }, indice.ColunasInclude);
        // Igualdade primeiro, desigualdade depois — a ordem da chave.
        Assert.Equal("ClienteId, DataPedido", indice.ColunasChaveDescricao);
    }

    [Fact]
    public void AnalisarXmlBruto_VariosStatementsNoMesmoArquivo_DevolveUmPorStatement()
    {
        const string statements = """
            <StmtUseDb StatementText="USE [Vendas]" StatementType="USE DATABASE" />
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="1" StatementEstRows="1">
              <QueryPlan>
                <RelOp NodeId="0" PhysicalOp="Constant Scan" LogicalOp="Constant Scan"
                       EstimatedTotalSubtreeCost="1" EstimateRows="1" />
              </QueryPlan>
            </StmtSimple>
            """;

        var resultado = CriarModulo().AnalisarXmlBruto(MontarPlano(statements));

        Assert.Equal(2, resultado.Count);
        // "USE [Banco]" não tem plano de execução — mas continua aparecendo
        // na grade, só sem operadores.
        Assert.Empty(resultado[0].Operadores);
        Assert.Null(resultado[0].OperadorRaiz);
        Assert.Single(resultado[1].Operadores);
    }

    [Fact]
    public void AnalisarXmlBruto_XmlInvalido_LancaErroExplicandoOProblema()
    {
        var erro = Assert.Throws<InvalidOperationException>(
            () => CriarModulo().AnalisarXmlBruto("<ShowPlanXML><isso nao fecha"));

        Assert.Contains(".sqlplan", erro.Message);
    }

    [Fact]
    public void AnalisarXmlBruto_XmlValidoSemNenhumStatement_LancaErroExplicandoOProblema()
    {
        var erro = Assert.Throws<InvalidOperationException>(
            () => CriarModulo().AnalisarXmlBruto(MontarPlano(string.Empty)));

        Assert.Contains("statement", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalisarXmlBruto_ArvoreProfundaMasDentroDoLimite_AnalisaTudoSemAvisar()
    {
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlanoEncadeado(50))[0];

        Assert.Equal(50, stmt.Operadores.Count);
        Assert.DoesNotContain(stmt.TodosOsAvisos, a => a.Tipo == "ProfundidadeMaximaAtingida");
    }

    [Fact]
    public void AnalisarXmlBruto_ArvoreAcimaDoLimiteDeProfundidade_TruncaComAvisoEmVezDeEstourar()
    {
        // A travessia é recursiva: sem a guarda de profundidade isto viraria
        // um StackOverflowException, que o .NET não deixa capturar — o
        // processo (e o app do usuário junto) morreria na hora. O contrato
        // aqui é: devolve o que deu para analisar E avisa que truncou.
        var stmt = CriarModulo().AnalisarXmlBruto(MontarPlanoEncadeado(600))[0];

        Assert.True(stmt.Operadores.Count < 600, "a análise deveria ter parado antes do fim da árvore");
        Assert.True(stmt.Operadores.Count > 400, "a análise deveria ter ido fundo antes de parar");

        var aviso = Assert.Single(stmt.TodosOsAvisos, a => a.Tipo == "ProfundidadeMaximaAtingida");
        Assert.Equal("Crítico", aviso.Severidade);
        // O aviso precisa dizer que o que está abaixo NÃO entrou na análise,
        // senão o usuário confia num resultado parcial sem perceber.
        Assert.Contains("NÃO entraram", aviso.Descricao);

        // O operador mais fundo que sobrou é justamente o que carrega o aviso.
        var maisFundo = stmt.Operadores[^1];
        Assert.Equal((int?)maisFundo.NodeId, aviso.NodeIdOperador);
    }

    /// <summary>
    /// Monta um plano com <paramref name="niveis"/> operadores encadeados um
    /// dentro do outro (cada RelOp tem exatamente um filho), para exercitar
    /// a guarda de profundidade da travessia recursiva.
    /// </summary>
    private static string MontarPlanoEncadeado(int niveis)
    {
        var sb = new StringBuilder();
        sb.Append("<StmtSimple StatementText=\"SELECT 1\" StatementType=\"SELECT\" ")
          .Append("StatementSubTreeCost=\"1\" StatementEstRows=\"1\"><QueryPlan>");

        for (int i = 0; i < niveis; i++)
        {
            sb.Append("<RelOp NodeId=\"").Append(i).Append("\" PhysicalOp=\"Nested Loops\" ")
              .Append("LogicalOp=\"Inner Join\" EstimatedTotalSubtreeCost=\"1\" EstimateRows=\"1\">")
              .Append("<NestedLoops>");
        }

        for (int i = 0; i < niveis; i++)
        {
            sb.Append("</NestedLoops></RelOp>");
        }

        sb.Append("</QueryPlan></StmtSimple>");
        return MontarPlano(sb.ToString());
    }

    [Fact]
    public void ScriptCriacao_IndiceSugerido_MontaCreateIndexComChaveEInclude()
    {
        // É EXATAMENTE este texto que CriarIndiceAsync manda para o servidor
        // (o mesmo que a tela mostra no botão "Copiar script"), por isso vale
        // travar o formato.
        var indice = new PlanoIndiceFaltanteDto
        {
            Impacto = 95.5m,
            Banco = "Vendas",
            Schema = "dbo",
            Tabela = "Pedido",
            ColunasIgualdade = { "ClienteId" },
            ColunasDesigualdade = { "DataPedido" },
            ColunasInclude = { "Valor" },
        };

        var script = indice.ScriptCriacao;

        Assert.Equal("IX_Pedido_ClienteId_DataPedido", indice.NomeSugerido);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Pedido_ClienteId_DataPedido]", script);
        Assert.Contains("ON [Vendas].[dbo].[Pedido] ([ClienteId], [DataPedido])", script);
        Assert.Contains("INCLUDE ([Valor])", script);
        // O lembrete de avaliar o custo de escrita não pode sumir do script.
        Assert.Contains("INSERT/UPDATE/DELETE", script);
    }

    [Fact]
    public void ScriptCriacao_SemColunasDeInclude_NaoEmiteClausulaInclude()
    {
        var indice = new PlanoIndiceFaltanteDto
        {
            Banco = "Vendas",
            Schema = "dbo",
            Tabela = "Pedido",
            ColunasIgualdade = { "ClienteId" },
        };

        Assert.DoesNotContain("INCLUDE", indice.ScriptCriacao);
    }
}
