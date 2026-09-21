using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using FerramentasDBA.Classes.Modulos;
using Xunit;

namespace FerramentasDBA.Classes.Tests;

/// <summary>
/// Testes dos dois membros estáticos e puros do Profiler:
/// <see cref="Modulo_Profiler.MontarDdlCriarSessao"/> (o texto que vai ser
/// executado no servidor) e <see cref="Modulo_Profiler.FiltrarEventos"/>
/// (filtro sempre aplicado do lado do cliente, por decisão de projeto).
/// Nenhum dos dois toca em conexão.
/// </summary>
public class ModuloProfilerTests
{
    private static ProfilerEventoDto Evento(
        string eventClass,
        string? aplicativo = null,
        string? texto = null,
        long? duracao = null,
        string? banco = null,
        string? login = null,
        string? host = null) =>
        new()
        {
            EventClass = eventClass,
            ApplicationName = aplicativo,
            TextData = texto,
            Duration = duracao,
            DatabaseName = banco,
            LoginName = login,
            HostName = host,
        };

    private static int ContarOcorrencias(string texto, string trecho)
    {
        int total = 0;
        int posicao = texto.IndexOf(trecho, StringComparison.Ordinal);
        while (posicao >= 0)
        {
            total++;
            posicao = texto.IndexOf(trecho, posicao + trecho.Length, StringComparison.Ordinal);
        }
        return total;
    }

    // ---------------------------------------------------------------- DDL

    [Fact]
    public void MontarDdlCriarSessao_UmEvento_GeraDdlCompletaComAlvoRingBuffer()
    {
        var ddl = Modulo_Profiler.MontarDdlCriarSessao("Minha_Sessao", new[] { "sql_batch_completed" });

        Assert.StartsWith("CREATE EVENT SESSION [Minha_Sessao] ON SERVER", ddl);
        Assert.Contains("ADD EVENT sqlserver.sql_batch_completed(", ddl);
        Assert.Contains("ADD TARGET package0.ring_buffer(SET max_memory=(20480))", ddl);
        // STARTUP_STATE=OFF é o que impede a sessão de voltar sozinha depois
        // de um restart do serviço, caso ela fique para trás no servidor.
        Assert.Contains("STARTUP_STATE=OFF", ddl);
        Assert.Contains("EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS", ddl);
    }

    [Fact]
    public void MontarDdlCriarSessao_QualquerEvento_AnexaAsMesmasAcoesGlobais()
    {
        var ddl = Modulo_Profiler.MontarDdlCriarSessao("S", new[] { "login", "rpc_completed" });

        // As "actions" são as colunas extras (aplicativo, host, login...) sem
        // as quais a grade do Profiler fica sem as colunas mais úteis.
        Assert.Contains("sqlserver.client_app_name", ddl);
        Assert.Contains("sqlserver.client_hostname", ddl);
        Assert.Contains("sqlserver.database_name", ddl);
        Assert.Contains("sqlserver.session_id", ddl);
        // Um bloco ACTION por evento — as ações não variam de evento para evento.
        Assert.Equal(2, ContarOcorrencias(ddl, "ACTION ("));
    }

    [Fact]
    public void MontarDdlCriarSessao_VariosEventos_SeparaComVirgulaSemSobrarUmaAntesDoTarget()
    {
        // Uma vírgula a mais (ou a menos) aqui é erro de sintaxe no servidor,
        // e só apareceria como falha ao clicar em "Executar".
        var ddl = Modulo_Profiler
            .MontarDdlCriarSessao("S", new[] { "sql_batch_completed", "rpc_completed" })
            .Replace("\r\n", "\n");

        Assert.Contains("),\nADD EVENT sqlserver.rpc_completed(", ddl);
        Assert.Contains(")\nADD TARGET package0.ring_buffer", ddl);
        Assert.DoesNotContain("),\nADD TARGET", ddl);
    }

    // ------------------------------------------------------------- Filtro

    [Fact]
    public void FiltrarEventos_FiltroPadrao_EscondeAsConsultasDaPropriaFerramenta()
    {
        // Sem isso a captura ao vivo entra em "loop visual": o próprio
        // polling da tela aparece na grade a cada leitura do ring buffer.
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: Conectar_SQL.NomeAplicacao),
            Evento("SQL:BatchCompleted", aplicativo: "ERP do cliente"),
        };

        var resultado = Modulo_Profiler.FiltrarEventos(eventos, new ProfilerFiltroDto());

        var unico = Assert.Single(resultado);
        Assert.Equal("ERP do cliente", unico.ApplicationName);
    }

    [Fact]
    public void FiltrarEventos_ComExclusaoDoProprioAppDesligada_MantemAsConsultasDaFerramenta()
    {
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: Conectar_SQL.NomeAplicacao),
            Evento("SQL:BatchCompleted", aplicativo: "ERP do cliente"),
        };

        var resultado = Modulo_Profiler.FiltrarEventos(
            eventos, new ProfilerFiltroDto { ExcluirConsultasDoProprioApp = false });

        Assert.Equal(2, resultado.Count);
    }

    [Fact]
    public void FiltrarEventos_PorNomeDeEvento_CasaPeloNomeDeExibicaoDoCatalogo()
    {
        // O filtro guarda o nome do Extended Events ("sql_batch_completed"),
        // mas a grade mostra o nome estilo Profiler clássico
        // ("SQL:BatchCompleted") — a tradução tem que acontecer aqui.
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: "ERP"),
            Evento("RPC:Completed", aplicativo: "ERP"),
        };

        var filtro = new ProfilerFiltroDto
        {
            EventosSelecionados = { "sql_batch_completed" },
            ExcluirConsultasDoProprioApp = false,
        };

        var unico = Assert.Single(Modulo_Profiler.FiltrarEventos(eventos, filtro));
        Assert.Equal("SQL:BatchCompleted", unico.EventClass);
    }

    [Fact]
    public void FiltrarEventos_PorNomeDeEventoForaDoCatalogo_ComparaComONomeCru()
    {
        // Evento lido de um .xel que não está no catálogo curado: melhor
        // comparar com o próprio nome do que descartar tudo.
        var eventos = new[] { Evento("wait_info", aplicativo: "ERP") };

        var filtro = new ProfilerFiltroDto
        {
            EventosSelecionados = { "wait_info" },
            ExcluirConsultasDoProprioApp = false,
        };

        Assert.Single(Modulo_Profiler.FiltrarEventos(eventos, filtro));
    }

    [Fact]
    public void FiltrarEventos_PorAplicativo_IgnoraMaiusculasEMinusculasEBuscaPorParte()
    {
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: "ERP do Cliente"),
            Evento("SQL:BatchCompleted", aplicativo: "Folha de Pagamento"),
            Evento("SQL:BatchCompleted", aplicativo: null),
        };

        var filtro = new ProfilerFiltroDto
        {
            AplicativoContem = "erp",
            ExcluirConsultasDoProprioApp = false,
        };

        var unico = Assert.Single(Modulo_Profiler.FiltrarEventos(eventos, filtro));
        Assert.Equal("ERP do Cliente", unico.ApplicationName);
    }

    [Fact]
    public void FiltrarEventos_CriteriosDeTextoCombinados_SaoAplicadosJuntos()
    {
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: "ERP", banco: "Vendas", login: "sa", host: "MAQ1"),
            Evento("SQL:BatchCompleted", aplicativo: "ERP", banco: "Vendas", login: "app", host: "MAQ1"),
            Evento("SQL:BatchCompleted", aplicativo: "ERP", banco: "Financeiro", login: "sa", host: "MAQ1"),
        };

        var filtro = new ProfilerFiltroDto
        {
            BancoContem = "vendas",
            LoginContem = "sa",
            HostContem = "maq",
            ExcluirConsultasDoProprioApp = false,
        };

        // Só o primeiro evento atende aos três critérios ao mesmo tempo.
        var unico = Assert.Single(Modulo_Profiler.FiltrarEventos(eventos, filtro));
        Assert.Equal("sa", unico.LoginName);
        Assert.Equal("Vendas", unico.DatabaseName);
        Assert.Equal("MAQ1", unico.HostName);
    }

    [Fact]
    public void FiltrarEventos_PorDuracaoMinima_MantemOsEventosInstantaneos()
    {
        // Audit Login/Logout e afins não têm Duration: excluí-los aqui seria
        // esconder eventos que o filtro nem tem como avaliar.
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: "ERP", duracao: 5000),
            Evento("SQL:BatchCompleted", aplicativo: "ERP", duracao: 10),
            Evento("Audit Login", aplicativo: "ERP", duracao: null),
        };

        var filtro = new ProfilerFiltroDto
        {
            DuracaoMinimaMs = 1000,
            ExcluirConsultasDoProprioApp = false,
        };

        var resultado = Modulo_Profiler.FiltrarEventos(eventos, filtro);

        Assert.Equal(2, resultado.Count);
        Assert.Contains(resultado, e => e.EventClass == "Audit Login");
        Assert.DoesNotContain(resultado, e => e.Duration == 10);
    }

    [Fact]
    public void FiltrarEventos_PorTextoDoComando_DescartaEventosSemTexto()
    {
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: "ERP", texto: "SELECT * FROM VD_PEDIDOS"),
            Evento("SQL:BatchCompleted", aplicativo: "ERP", texto: "UPDATE VD_ITENS SET x = 1"),
            Evento("Audit Login", aplicativo: "ERP", texto: null),
        };

        var filtro = new ProfilerFiltroDto
        {
            TextoContem = "vd_pedidos",
            ExcluirConsultasDoProprioApp = false,
        };

        var unico = Assert.Single(Modulo_Profiler.FiltrarEventos(eventos, filtro));
        Assert.Equal("SELECT * FROM VD_PEDIDOS", unico.TextData);
    }

    [Fact]
    public void FiltrarEventos_SomenteComandosDeUsuario_DescartaOsSetDaSessao()
    {
        var eventos = new[]
        {
            Evento("SQL:BatchCompleted", aplicativo: "ERP", texto: "  SET ARITHABORT ON"),
            Evento("SQL:BatchCompleted", aplicativo: "ERP", texto: "SELECT 1"),
            // Sem texto nenhum (ex.: Audit Login) continua aparecendo: não é
            // um comando "de sessão", é outro tipo de evento.
            Evento("Audit Login", aplicativo: "ERP", texto: null),
        };

        var filtro = new ProfilerFiltroDto
        {
            SomenteComandosDeUsuario = true,
            ExcluirConsultasDoProprioApp = false,
        };

        var resultado = Modulo_Profiler.FiltrarEventos(eventos, filtro);

        Assert.Equal(2, resultado.Count);
        Assert.DoesNotContain(resultado, e => e.TextData != null && e.TextData.Contains("ARITHABORT"));
    }

    [Fact]
    public void FiltrarEventos_NaoAlteraAListaOriginal()
    {
        // A tela reaplica o filtro várias vezes sobre a MESMA lista lida do
        // arquivo — se o filtro consumisse a lista, o segundo clique em
        // "Aplicar filtro" mostraria menos linhas do que deveria.
        var eventos = new List<ProfilerEventoDto>
        {
            Evento("SQL:BatchCompleted", aplicativo: Conectar_SQL.NomeAplicacao),
            Evento("SQL:BatchCompleted", aplicativo: "ERP"),
        };

        Modulo_Profiler.FiltrarEventos(eventos, new ProfilerFiltroDto());
        var segundaPassada = Modulo_Profiler.FiltrarEventos(eventos, new ProfilerFiltroDto());

        Assert.Equal(2, eventos.Count);
        Assert.Single(segundaPassada);
    }

    [Fact]
    public void CatalogoEventos_TemApenasSqlBatchCompletedMarcadoPorPadrao()
    {
        // Pedido explícito do usuário: a tela abre com só esse evento
        // marcado, para a captura não vir "barulhenta" por padrão.
        var padrao = Modulo_Profiler.CatalogoEventos.Where(e => e.PadraoStandard).ToList();

        var unico = Assert.Single(padrao);
        Assert.Equal("sql_batch_completed", unico.NomeEvento);
        Assert.Equal("SQL:BatchCompleted", unico.NomeExibicao);
    }

    [Fact]
    public void CatalogoEventos_NaoInclueDeadlockPorqueTemTelaPropria()
    {
        Assert.DoesNotContain(Modulo_Profiler.CatalogoEventos, e => e.NomeEvento == "xml_deadlock_report");
    }
}
