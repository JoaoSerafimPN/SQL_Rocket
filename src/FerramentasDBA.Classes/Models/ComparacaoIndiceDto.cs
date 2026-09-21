namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma linha da grade de "Comparador &gt; Índices" — um índice (casado por
/// tabela + nome) e sua situação entre o banco de ORIGEM (o banco da
/// própria instância conectada na ferramenta) e o de DESTINO (outro banco
/// da mesma instância, ou de um servidor totalmente diferente).
/// <see cref="Status"/> é um destes 4 valores fixos: "Igual", "Diferente",
/// "Ausente na Origem" ou "Ausente no Destino" — usados tanto para colorir
/// a linha na grade (Diferente = amarelo, Ausente* = vermelho, Igual = sem
/// destaque) quanto para decidir o que "Aplicar" faz nela. Preenchida em
/// Modulo_Comparador.CompararIndicesAsync, consumida por
/// Modulo_Comparador.AplicarIndicesAsync.
/// </summary>
public sealed class ComparacaoIndiceDto
{
    public string Tabela { get; set; } = string.Empty;
    public string NomeIndice { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string DescricaoOrigem { get; set; } = string.Empty;
    public string DescricaoDestino { get; set; } = string.Empty;

    /// <summary>Definição completa do índice na Origem — null quando "Ausente na Origem".</summary>
    public IndiceDefinicaoDto? DefinicaoOrigem { get; set; }

    /// <summary>Definição completa do índice no Destino — null quando "Ausente no Destino".</summary>
    public IndiceDefinicaoDto? DefinicaoDestino { get; set; }
}
