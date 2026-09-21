using FerramentasDBA.Classes.Models;
using FerramentasDBA.Classes.Modulos;
using Xunit;

namespace FerramentasDBA.Classes.Tests;

/// <summary>
/// Testes da análise offline de deadlock / head blocking.
///
/// O parse de XML em si (AnalisarXmlBruto) é privado, então o ponto de
/// entrada testável é AnalisarArquivoXmlAsync — que recebe um caminho de
/// arquivo. Cada teste escreve o XML num arquivo temporário próprio e o
/// apaga em seguida; nada aqui depende de um arquivo versionado no repo nem
/// de conexão com o SQL Server (o Modulo_Deadlock nem recebe um
/// Conectar_SQL, justamente por ser 100% offline).
/// </summary>
public class ModuloDeadlockTests
{
    private static async Task<List<DeadlockEventoDto>> AnalisarAsync(string xml)
    {
        var caminho = Path.Combine(Path.GetTempPath(), $"sqlrocket-teste-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(caminho, xml);
        try
        {
            return await new Modulo_Deadlock().AnalisarArquivoXmlAsync(caminho);
        }
        finally
        {
            File.Delete(caminho);
        }
    }

    private const string DeadlockCompleto = """
        <event name="xml_deadlock_report" package="sqlserver" timestamp="2024-05-10T12:34:56.789Z">
          <data name="xml_report">
            <value>
              <deadlock>
                <victim-list>
                  <victimProcess id="process1" />
                </victim-list>
                <process-list>
                  <process id="process1" spid="53" hostname="MAQ-CAIXA" loginname="app_vendas" clientapp="ERP">
                    <executionStack>
                      <frame procname="CloudADM.dbo.TR_VD_PEDIDOS_UPD">UPDATE VD_PEDIDOS SET status = 1</frame>
                    </executionStack>
                    <inputbuf>EXEC dbo.GravaPedido</inputbuf>
                  </process>
                  <process id="process2" spid="71" hostname="MAQ-FISCAL" loginname="app_fiscal" clientapp="Integrador">
                    <executionStack>
                      <frame procname="adhoc">SELECT * FROM VD_PEDIDOSPLUS</frame>
                    </executionStack>
                    <inputbuf />
                  </process>
                </process-list>
                <resource-list>
                  <keylock objectname="CloudADM.dbo.VD_PEDIDOS" mode="X">
                    <owner-list>
                      <owner id="process2" mode="X" />
                    </owner-list>
                    <waiter-list>
                      <waiter id="process1" mode="S" />
                    </waiter-list>
                  </keylock>
                </resource-list>
              </deadlock>
            </value>
          </data>
        </event>
        """;

    [Fact]
    public async Task AnalisarArquivoXmlAsync_Deadlock_IdentificaOEventoEOsProcessos()
    {
        var eventos = await AnalisarAsync(DeadlockCompleto);

        var evento = Assert.Single(eventos);
        Assert.Equal("Deadlock", evento.TipoEvento);
        Assert.Equal("Deadlock", evento.DescricaoTipoEvento);
        Assert.Equal(2, evento.QuantidadeProcessos);
        Assert.Equal("53, 71", evento.ResumoSpids);
        // O XML original fica guardado para conferência.
        Assert.Contains("VD_PEDIDOS", evento.XmlBruto);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_Deadlock_MarcaComoVitimaSoQuemEstaNaVictimList()
    {
        var evento = (await AnalisarAsync(DeadlockCompleto))[0];

        var vitima = evento.Processos.Single(p => p.Spid == "53");
        var vencedor = evento.Processos.Single(p => p.Spid == "71");

        Assert.True(vitima.EhVitima);
        Assert.False(vencedor.EhVitima);
        Assert.Equal("MAQ-CAIXA", vitima.HostName);
        Assert.Equal("app_vendas", vitima.LoginName);
        Assert.Equal("ERP", vitima.ClientApp);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_Deadlock_ResumeOComandoEAOrigemDeCadaProcesso()
    {
        var evento = (await AnalisarAsync(DeadlockCompleto))[0];

        var vitima = evento.Processos.Single(p => p.Spid == "53");
        var vencedor = evento.Processos.Single(p => p.Spid == "71");

        Assert.Equal("UPDATE na VD_PEDIDOS", vitima.OperacaoPrimaria);
        // Nome com "TR_" é tratado como gatilho (heurística por convenção).
        Assert.Equal("Trigger TR_VD_PEDIDOS_UPD", vitima.GatilhoOuCodigo);

        Assert.Equal("SELECT de VD_PEDIDOSPLUS", vencedor.OperacaoPrimaria);
        Assert.Equal("Ad-hoc query (Consulta direta)", vencedor.GatilhoOuCodigo);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_Deadlock_CruzaProcessosComAListaEdeRecursos()
    {
        var evento = (await AnalisarAsync(DeadlockCompleto))[0];

        var vitima = evento.Processos.Single(p => p.Spid == "53");
        var vencedor = evento.Processos.Single(p => p.Spid == "71");

        // Quem aparece como <owner> do recurso está RETENDO o bloqueio.
        Assert.Equal("Lock Exclusivo (X) na VD_PEDIDOS", vencedor.BloqueioRetidoDescricao);
        Assert.Equal(string.Empty, vencedor.BloqueioSolicitadoDescricao);

        // SUSPEITA: quem aparece como <waiter> está SOLICITANDO o bloqueio,
        // e o modo pedido está no próprio <waiter mode="S">. A descrição,
        // porém, é montada a partir do atributo "mode" do RECURSO
        // (<keylock mode="X">), então a tela mostra "Lock Exclusivo (X)"
        // para um processo que na verdade pediu um lock de leitura (S). Na
        // minha leitura, "Bloqueio Solicitado" deveria usar o mode do
        // próprio waiter (e "Bloqueio Retido", o do owner).
        Assert.Equal("Lock Exclusivo (X) na VD_PEDIDOS", vitima.BloqueioSolicitadoDescricao);
        Assert.Equal(string.Empty, vitima.BloqueioRetidoDescricao);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_DeadlockDentroDeEvent_UsaOTimestampDoEvento()
    {
        var evento = (await AnalisarAsync(DeadlockCompleto))[0];

        Assert.NotNull(evento.Timestamp);
        // O timestamp do XML é UTC e é convertido para a hora local da
        // máquina — voltar para UTC tem que devolver exatamente o original.
        Assert.Equal(
            new DateTime(2024, 5, 10, 12, 34, 56, 789, DateTimeKind.Utc),
            evento.Timestamp!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_VariosEventosSemRaizUnica_LeTodosMesmoAssim()
    {
        // Caso real: o usuário cola direto o resultado de uma consulta com
        // várias linhas, o que produz vários <event> sem um elemento raiz
        // envolvendo todos — XML tecnicamente inválido, mas recuperável.
        var eventos = await AnalisarAsync(DeadlockCompleto + DeadlockCompleto);

        Assert.Equal(2, eventos.Count);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_BlockedProcessReport_SeparaBloqueadoDeBloqueador()
    {
        const string xml = """
            <blocked-process-report>
              <blocked-process>
                <process id="processA" spid="61" waitresource="KEY: 7:72057594043039744 (a1b2c3d4e5f6)"
                         lockMode="S" hostname="MAQ-CAIXA" loginname="app_vendas" clientapp="ERP">
                  <executionStack>
                    <frame procname="adhoc">SELECT * FROM VD_PEDIDOS</frame>
                  </executionStack>
                  <inputbuf>SELECT * FROM VD_PEDIDOS</inputbuf>
                </process>
              </blocked-process>
              <blocking-process>
                <process id="processB" spid="62" hostname="MAQ-FISCAL" loginname="app_fiscal" clientapp="Integrador">
                  <executionStack>
                    <frame procname="CloudADM.dbo.PR_ATUALIZA_PEDIDO">UPDATE VD_PEDIDOS SET status = 2</frame>
                  </executionStack>
                  <inputbuf />
                </process>
              </blocking-process>
            </blocked-process-report>
            """;

        var evento = Assert.Single(await AnalisarAsync(xml));

        Assert.Equal("BlockedProcess", evento.TipoEvento);
        Assert.Equal("Bloqueio (Head Blocking)", evento.DescricaoTipoEvento);
        Assert.Equal(2, evento.Processos.Count);

        var bloqueado = evento.Processos[0];
        var bloqueador = evento.Processos[1];

        // EhVitima aqui significa "processo prejudicado" = o que ficou esperando.
        Assert.True(bloqueado.EhVitima);
        Assert.False(bloqueador.EhVitima);
        Assert.Equal("61", bloqueado.Spid);
        Assert.Equal("62", bloqueador.Spid);
        Assert.Equal("Procedure PR_ATUALIZA_PEDIDO", bloqueador.GatilhoOuCodigo);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_BlockedProcessReport_DescreveORecursoEsperadoPeloBloqueado()
    {
        const string xml = """
            <blocked-process-report>
              <blocked-process>
                <process id="processA" spid="61" waitresource="KEY: 7:72057594043039744 (a1b2c3d4e5f6)" lockMode="S">
                  <inputbuf>SELECT * FROM VD_PEDIDOS</inputbuf>
                </process>
              </blocked-process>
              <blocking-process>
                <process id="processB" spid="62">
                  <inputbuf>UPDATE VD_PEDIDOS SET status = 2</inputbuf>
                </process>
              </blocking-process>
            </blocked-process-report>
            """;

        var evento = Assert.Single(await AnalisarAsync(xml));
        var bloqueado = evento.Processos[0];
        var bloqueador = evento.Processos[1];

        Assert.Equal(
            "Lock de Leitura (S) em KEY: 7:72057594043039744 (a1b2c3d4e5f6)",
            bloqueado.BloqueioSolicitadoDescricao);

        // Este tipo de relatório não diz QUAL lock o bloqueador está
        // segurando — inventar um valor aqui seria pior que deixar vazio.
        Assert.Equal(string.Empty, bloqueador.BloqueioRetidoDescricao);
        Assert.Equal(string.Empty, bloqueador.BloqueioSolicitadoDescricao);

        // Sem <frame>, o resumo vem do inputbuf.
        Assert.Equal("UPDATE na VD_PEDIDOS", bloqueador.OperacaoPrimaria);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_XmlSoltoSemEvent_FicaSemTimestamp()
    {
        const string xml = """
            <blocked-process-report>
              <blocked-process>
                <process id="processA" spid="61">
                  <inputbuf>SELECT 1</inputbuf>
                </process>
              </blocked-process>
            </blocked-process-report>
            """;

        var evento = Assert.Single(await AnalisarAsync(xml));

        // Não há de onde tirar o horário — melhor nulo do que inventado.
        Assert.Null(evento.Timestamp);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_XmlValidoSemDeadlockNemBloqueio_LancaErroExplicandoOProblema()
    {
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AnalisarAsync("<events><event name=\"sql_batch_completed\" /></events>"));

        Assert.Contains("blocked_process_report", erro.Message);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_ConteudoQueNaoEhXml_LancaErroExplicandoOProblema()
    {
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AnalisarAsync("isto aqui nao e xml nenhum <<<"));

        Assert.Contains("XML válido", erro.Message);
    }

    [Fact]
    public async Task AnalisarArquivoXmlAsync_ArquivoInexistente_LancaFileNotFound()
    {
        var caminho = Path.Combine(Path.GetTempPath(), $"sqlrocket-nao-existe-{Guid.NewGuid():N}.xml");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => new Modulo_Deadlock().AnalisarArquivoXmlAsync(caminho));
    }
}
