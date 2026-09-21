namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Definição estrutural de um índice "solto" (criado via CREATE INDEX —
/// não um índice apoiado em PRIMARY KEY/UNIQUE CONSTRAINT, fora do escopo
/// de "Comparador &gt; Índices"), coletada de um dos dois bancos sendo
/// comparados. Preenchida em Modulo_Comparador.ObterDefinicoesIndicesAsync
/// e usada tanto para decidir "igual"/"diferente" quanto para montar o
/// CREATE INDEX ao aplicar uma alteração.
/// </summary>
public sealed class IndiceDefinicaoDto
{
    public string Esquema { get; set; } = string.Empty;
    public string Tabela { get; set; } = string.Empty;
    public string NomeIndice { get; set; } = string.Empty;
    public bool Unico { get; set; }
    public string TipoIndice { get; set; } = string.Empty; // "CLUSTERED" ou "NONCLUSTERED" (sys.indexes.type_desc)
    public List<ColunaIndiceDto> ColunasChave { get; set; } = new();
    public List<string> ColunasIncluidas { get; set; } = new();

    public string TabelaCompleta => $"{Esquema}.{Tabela}";

    /// <summary>Resumo legível da definição, usado nas colunas "Definição na Origem/no Destino" da grade de comparação.</summary>
    public string Resumo
    {
        get
        {
            var colunasChave = string.Join(", ", ColunasChave.Select(c => c.Nome + (c.Descendente ? " DESC" : " ASC")));
            var texto = $"{(Unico ? "UNIQUE " : string.Empty)}{TipoIndice} ({colunasChave})";
            if (ColunasIncluidas.Count > 0)
            {
                texto += $" INCLUDE ({string.Join(", ", ColunasIncluidas)})";
            }
            return texto;
        }
    }
}
