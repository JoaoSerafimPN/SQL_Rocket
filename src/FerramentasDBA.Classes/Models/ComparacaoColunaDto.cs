namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma linha da grade de "Comparador &gt; Banco de dados" — uma coluna
/// (casada por tabela + nome) e sua situação entre o banco de ORIGEM (o
/// banco da própria instância conectada na ferramenta) e o de DESTINO
/// (outro banco da mesma instância, ou de um servidor totalmente
/// diferente). <see cref="Status"/> é um destes 4 valores fixos: "Igual",
/// "Diferente", "Ausente na Origem" ou "Ausente no Destino" — usados tanto
/// para colorir a linha na grade (Diferente = amarelo, Ausente* =
/// vermelho, Igual = sem destaque) quanto para decidir o que "Aplicar" faz
/// nela. Preenchida em Modulo_Comparador.CompararBancosAsync, consumida
/// por Modulo_Comparador.AplicarBancosAsync.
/// </summary>
public sealed class ComparacaoColunaDto
{
    public string Tabela { get; set; } = string.Empty;
    public string NomeColuna { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string DescricaoOrigem { get; set; } = string.Empty;
    public string DescricaoDestino { get; set; } = string.Empty;

    /// <summary>Definição completa da coluna na Origem — null quando "Ausente na Origem".</summary>
    public ColunaTabelaDto? DefinicaoOrigem { get; set; }

    /// <summary>Definição completa da coluna no Destino — null quando "Ausente no Destino".</summary>
    public ColunaTabelaDto? DefinicaoDestino { get; set; }
}
