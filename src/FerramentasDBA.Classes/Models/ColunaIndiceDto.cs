namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma coluna-chave de um índice (ordem já refletida pela posição na lista
/// de <see cref="IndiceDefinicaoDto.ColunasChave"/> — vem de
/// sys.index_columns.key_ordinal). Usada por "Comparador &gt; Índices".
/// </summary>
public sealed class ColunaIndiceDto
{
    public string Nome { get; set; } = string.Empty;
    public bool Descendente { get; set; }
}
