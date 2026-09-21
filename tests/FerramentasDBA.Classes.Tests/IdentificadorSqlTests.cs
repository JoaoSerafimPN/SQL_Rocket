using FerramentasDBA.Classes.Infraestrutura;
using Xunit;

namespace FerramentasDBA.Classes.Tests;

/// <summary>
/// Testes de <see cref="IdentificadorSql"/>.
///
/// POR QUE ESTA CLASSE É A MAIS IMPORTANTE DE TESTAR: é ela que decide em
/// QUAL tabela um DELETE/ALTER vai bater. Se "auditoria.Movimento" virar
/// "[Movimento]", o comando cai no schema padrão (dbo) e apaga da tabela
/// errada — o bug real que motivou a existência do método Qualificar.
/// </summary>
public class IdentificadorSqlTests
{
    [Fact]
    public void Qualificar_NomeSimples_DevolveNomeEntreColchetes()
    {
        Assert.Equal("[Pedido]", IdentificadorSql.Qualificar("Pedido"));
    }

    [Fact]
    public void Qualificar_ComEsquema_DevolveNomeEmDuasPartes()
    {
        Assert.Equal("[auditoria].[Movimento]", IdentificadorSql.Qualificar("auditoria.Movimento"));
    }

    [Fact]
    public void Qualificar_ComEspacosEmVolta_IgnoraOsEspacos()
    {
        Assert.Equal("[dbo].[Pedido]", IdentificadorSql.Qualificar("   dbo.Pedido   "));
    }

    [Fact]
    public void Qualificar_NomeJaDelimitado_DevolveInalterado()
    {
        // Evita o "[[dbo].[Pedido]]" que sairia se o chamador já tivesse
        // delimitado o nome e este método delimitasse de novo.
        Assert.Equal("[dbo].[Pedido]", IdentificadorSql.Qualificar("[dbo].[Pedido]"));
    }

    [Fact]
    public void Qualificar_NomeSimplesJaDelimitado_DevolveInalterado()
    {
        Assert.Equal("[Pedido]", IdentificadorSql.Qualificar("[Pedido]"));
    }

    [Fact]
    public void Qualificar_NomeComColcheteDeFechamento_EscapaDuplicando()
    {
        // Sem duplicar o "]", o identificador terminaria cedo demais e o
        // resto do nome viraria comando solto — porta de entrada para
        // injeção de SQL.
        Assert.Equal("[Rela]]torio]", IdentificadorSql.Qualificar("Rela]torio"));
    }

    [Fact]
    public void Qualificar_EsquemaComObjetoContendoColchete_EscapaSoNaParteCerta()
    {
        Assert.Equal("[dbo].[Tab]]ela]", IdentificadorSql.Qualificar("dbo.Tab]ela"));
    }

    [Fact]
    public void Qualificar_NomeComPontoDepoisDoEsquema_SoSeparaNoPrimeiroPonto()
    {
        // Nome de tabela pode conter ponto (raro, mas válido): separar em
        // todos os pontos quebraria a qualificação.
        Assert.Equal("[dbo].[Minha.Tabela]", IdentificadorSql.Qualificar("dbo.Minha.Tabela"));
    }

    [Fact]
    public void Qualificar_NomeComecandoComPonto_NaoSeparaEmEsquemaVazio()
    {
        // "[].[Pedido]" não é um identificador válido no SQL Server, então
        // tratar tudo como um nome só é o comportamento seguro aqui.
        Assert.Equal("[.Pedido]", IdentificadorSql.Qualificar(".Pedido"));
    }

    [Fact]
    public void Qualificar_NomeTerminandoComPonto_NaoSeparaEmObjetoVazio()
    {
        // Mesmo motivo do teste acima: "[dbo].[]" seria inválido.
        Assert.Equal("[dbo.]", IdentificadorSql.Qualificar("dbo."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Qualificar_NomeVazioOuEmBranco_LancaArgumentException(string nome)
    {
        Assert.Throws<ArgumentException>(() => IdentificadorSql.Qualificar(nome));
    }

    [Fact]
    public void Qualificar_NomeNulo_LancaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => IdentificadorSql.Qualificar(null!));
    }

    [Fact]
    public void Qualificar_TextoMaliciosoEntreColchetes_EhEscapado()
    {
        // O atalho "já veio delimitado, devolve como está" existe para não
        // delimitar duas vezes um nome vindo de QUOTENAME. Ele NÃO pode ser
        // um buraco: um texto que apenas comece com "[" e termine com "]" —
        // mas feche o identificador no meio e emende outro comando — tem que
        // ser tratado como nome cru e escapado, não repassado inteiro.
        const string malicioso = "[x]; DROP TABLE Alvo; --]";

        var resultado = IdentificadorSql.Qualificar(malicioso);

        Assert.NotEqual(malicioso, resultado);
        // O "]" que fecharia o identificador aparece dobrado, então o texto
        // seguinte continua sendo parte do NOME e não vira comando.
        Assert.Equal("[[x]]; DROP TABLE Alvo; --]]]", resultado);
    }

    [Theory]
    [InlineData("[dbo].[Pedido]")]
    [InlineData("[Pedido]")]
    [InlineData("[dbo].[Min]]ha]")]
    public void Qualificar_NomeJaDelimitadoBemFormado_PassaDireto(string nome)
    {
        // Saída de QUOTENAME (inclusive com "]" dobrado dentro do nome):
        // delimitar de novo geraria "[[dbo].[Pedido]]", que não resolve.
        Assert.Equal(nome, IdentificadorSql.Qualificar(nome));
    }

    [Fact]
    public void Separar_NomeSimples_DevolveEsquemaNulo()
    {
        var (esquema, objeto) = IdentificadorSql.Separar("Pedido");

        Assert.Null(esquema);
        Assert.Equal("Pedido", objeto);
    }

    [Fact]
    public void Separar_ComEsquema_DevolveAsDuasPartes()
    {
        var (esquema, objeto) = IdentificadorSql.Separar("auditoria.Movimento");

        Assert.Equal("auditoria", esquema);
        Assert.Equal("Movimento", objeto);
    }

    [Fact]
    public void Separar_ObjetoComPonto_SoSeparaNoPrimeiroPonto()
    {
        var (esquema, objeto) = IdentificadorSql.Separar("dbo.Minha.Tabela");

        Assert.Equal("dbo", esquema);
        Assert.Equal("Minha.Tabela", objeto);
    }

    [Fact]
    public void Separar_ComEspacosEmVolta_IgnoraOsEspacos()
    {
        var (esquema, objeto) = IdentificadorSql.Separar("  vendas.Pedido  ");

        Assert.Equal("vendas", esquema);
        Assert.Equal("Pedido", objeto);
    }

    [Fact]
    public void Separar_NomeTerminandoComPonto_NaoSeparaEmObjetoVazio()
    {
        var (esquema, objeto) = IdentificadorSql.Separar("dbo.");

        Assert.Null(esquema);
        Assert.Equal("dbo.", objeto);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Separar_NomeVazioOuEmBranco_LancaArgumentException(string nome)
    {
        Assert.Throws<ArgumentException>(() => IdentificadorSql.Separar(nome));
    }

    [Fact]
    public void Separar_NomeJaDelimitado_HojeDevolveAsPartesComOsColchetes()
    {
        // SUSPEITA: Qualificar reconhece e respeita um nome já delimitado,
        // mas Separar não — devolve "[dbo]" e "[Pedido]" COM os colchetes.
        // Como o retorno de Separar é usado para comparar com
        // sys.tables.name / sys.schemas.name (que guardam o nome puro), um
        // nome delimitado nunca casaria e a consulta de metadados voltaria
        // vazia. Na minha leitura o certo seria Separar tirar os colchetes,
        // ficando coerente com Qualificar.
        var (esquema, objeto) = IdentificadorSql.Separar("[dbo].[Pedido]");

        Assert.Equal("[dbo]", esquema);
        Assert.Equal("[Pedido]", objeto);
    }

    [Theory]
    [InlineData("Pedido", "Pedido")]
    [InlineData("", "")]
    [InlineData("Rela]torio", "Rela]]torio")]
    [InlineData("]]", "]]]]")]
    [InlineData("[Pedido]", "[Pedido]]")]
    public void EscaparColchetes_DuplicaSomenteOColcheteDeFechamento(string entrada, string esperado)
    {
        // O "[" de abertura não precisa de escape dentro de [...] — só o "]"
        // encerra o identificador.
        Assert.Equal(esperado, IdentificadorSql.EscaparColchetes(entrada));
    }

    [Theory]
    [InlineData("O'Brien", "O''Brien")]
    [InlineData("Pedido", "Pedido")]
    [InlineData("", "")]
    [InlineData("''", "''''")]
    [InlineData("x' OR '1'='1", "x'' OR ''1''=''1")]
    public void EscaparAspaSimples_DuplicaTodasAsAspas(string entrada, string esperado)
    {
        Assert.Equal(esperado, IdentificadorSql.EscaparAspaSimples(entrada));
    }
}
