using FerramentasDBA.Classes.Modulos;
using Xunit;

namespace FerramentasDBA.Classes.Tests;

/// <summary>
/// Testes da única regra pura e estática do módulo Manutenção. Toda a
/// limpeza em si (shrink, exclusão por data/ID) precisa de conexão e fica
/// fora daqui — ver tests/README.md.
/// </summary>
public class ModuloManutencaoTests
{
    /// <summary>
    /// A entrada é o nome do tipo vindo de <c>sys.types.name</c> (via
    /// ObterChavePrimariaAsync). É esta regra que habilita a limpeza "por
    /// ID", que monta uma comparação NUMÉRICA (&lt;=) — liberar um tipo não
    /// inteiro aqui geraria um WHERE com conversão implícita, com resultado
    /// imprevisível numa exclusão em massa.
    /// </summary>
    [Theory]
    [InlineData("tinyint")]
    [InlineData("smallint")]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("INT")]
    [InlineData("BigInt")]
    public void EhTipoNumericoInteiro_TiposInteiros_DevolveVerdadeiro(string tipoDado)
    {
        Assert.True(Modulo_Manutencao.EhTipoNumericoInteiro(tipoDado));
    }

    [Theory]
    [InlineData("decimal")]
    [InlineData("numeric")]
    [InlineData("money")]
    [InlineData("float")]
    [InlineData("bit")]
    [InlineData("uniqueidentifier")]
    [InlineData("varchar")]
    [InlineData("nvarchar")]
    [InlineData("datetime")]
    [InlineData("")]
    public void EhTipoNumericoInteiro_DemaisTipos_DevolveFalso(string tipoDado)
    {
        Assert.False(Modulo_Manutencao.EhTipoNumericoInteiro(tipoDado));
    }

    [Fact]
    public void EhTipoNumericoInteiro_NomeComEspacos_DevolveFalso()
    {
        // A comparação é exata (sem Trim) — na prática o valor vem de
        // sys.types.name, que nunca traz espaços.
        Assert.False(Modulo_Manutencao.EhTipoNumericoInteiro(" int "));
    }
}
