namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma linha da grade "Top 25 maiores tabelas" da tela Manutenção &gt;
/// Limpeza de Arquivos — tamanho ocupado por uma tabela (dados e índices
/// separadamente), coletado via sys.tables/sys.indexes/sys.partitions/
/// sys.allocation_units (mesma DMV usada pelo procedimento padrão
/// sp_spaceused, aqui agrupado por tabela em vez de rodado uma vez por
/// tabela). Preenchida em Modulo_Manutencao.ObterTop25MaioresTabelasAsync.
/// </summary>
public sealed class TabelaTamanhoDto
{
    public string Esquema { get; set; } = string.Empty;
    public string Tabela { get; set; } = string.Empty;
    public long TotalLinhas { get; set; }

    /// <summary>Espaço ocupado pelos dados (HEAP/índice clustered) em MB.</summary>
    public decimal TamanhoDadosMB { get; set; }

    /// <summary>Espaço ocupado pelos índices não-clustered em MB.</summary>
    public decimal TamanhoIndicesMB { get; set; }

    public decimal TamanhoTotalMB => TamanhoDadosMB + TamanhoIndicesMB;

    public string TabelaCompleta => $"{Esquema}.{Tabela}";
}
