using FerramentasDBA.Classes.Modulos;
using Xunit;

namespace FerramentasDBA.Classes.Tests;

/// <summary>
/// Testes da única regra pura e estática do módulo Admin. O resto da classe
/// depende de conexão viva (backup, restore, collation, logins) e por isso
/// fica fora daqui — ver tests/README.md.
/// </summary>
public class ModuloAdminTests
{
    /// <summary>
    /// A entrada real destes testes é o texto que
    /// <c>SERVERPROPERTY('Edition')</c> devolve — por isso as edições
    /// aparecem aqui escritas exatamente como o SQL Server as reporta.
    /// Errar isso faz o BACKUP ... WITH COMPRESSION falhar numa Express
    /// (onde o comando nem existe) ou desperdiçar espaço numa Enterprise.
    /// </summary>
    [Theory]
    [InlineData("Enterprise Edition: Core-based Licensing (64-bit)", true)]
    [InlineData("Enterprise Edition (64-bit)", true)]
    [InlineData("Standard Edition (64-bit)", true)]
    [InlineData("Developer Edition (64-bit)", true)]
    [InlineData("Business Intelligence Edition (64-bit)", true)]
    [InlineData("SQL Azure", true)]
    [InlineData("Express Edition (64-bit)", false)]
    [InlineData("Express Edition with Advanced Services (64-bit)", false)]
    [InlineData("Web Edition (64-bit)", false)]
    public void SuportaCompressaoDeBackup_ReconheceAsEdicoesSemCompressao(string edicao, bool esperado)
    {
        Assert.Equal(esperado, Modulo_Admin.SuportaCompressaoDeBackup(edicao));
    }

    [Theory]
    [InlineData("EXPRESS EDITION (64-BIT)")]
    [InlineData("express edition (64-bit)")]
    public void SuportaCompressaoDeBackup_IgnoraMaiusculasEMinusculas(string edicao)
    {
        Assert.False(Modulo_Admin.SuportaCompressaoDeBackup(edicao));
    }

    [Fact]
    public void SuportaCompressaoDeBackup_EdicaoDesconhecidaOuVazia_AssumeQueSuporta()
    {
        // Se SERVERPROPERTY('Edition') vier vazio (falha na leitura), o
        // comportamento atual é tentar com compressão. É o menos ruim: o
        // pior caso é o servidor recusar o comando com uma mensagem clara,
        // em vez de gerar silenciosamente backups muito maiores.
        Assert.True(Modulo_Admin.SuportaCompressaoDeBackup(string.Empty));
    }
}
