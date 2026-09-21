namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Definição estrutural de uma coluna de tabela, coletada de um dos dois
/// bancos sendo comparados em "Comparador &gt; Banco de dados". Preenchida
/// em Modulo_Comparador.ObterDefinicoesColunasAsync e usada tanto para
/// decidir "igual"/"diferente" quanto para montar CREATE TABLE/ADD
/// COLUMN/ALTER COLUMN ao aplicar uma alteração.
/// </summary>
public sealed class ColunaTabelaDto
{
    public string Esquema { get; set; } = string.Empty;
    public string Tabela { get; set; } = string.Empty;
    public string NomeColuna { get; set; } = string.Empty;

    /// <summary>Posição da coluna na tabela (sys.columns.column_id) — usada só para preservar a ordem original ao montar um CREATE TABLE do zero.</summary>
    public int Posicao { get; set; }

    public string TipoDado { get; set; } = string.Empty;

    /// <summary>Tamanho em caracteres/bytes para tipos char/varchar/nchar/nvarchar/binary/varbinary (-1 = MAX); null para tipos sem tamanho.</summary>
    public int? MaxLength { get; set; }

    /// <summary>Precisão para tipos decimal/numeric; null para os demais.</summary>
    public byte? Precisao { get; set; }

    /// <summary>Escala para tipos decimal/numeric; null para os demais.</summary>
    public byte? Escala { get; set; }

    public bool Nulavel { get; set; }
    public bool IsIdentity { get; set; }

    public string TabelaCompleta => $"{Esquema}.{Tabela}";

    /// <summary>Resumo legível da definição, usado nas colunas "Definição na Origem/no Destino" da grade de comparação.</summary>
    public string Resumo
    {
        get
        {
            var tamanho = MaxLength.HasValue
                ? $"({(MaxLength.Value == -1 ? "MAX" : MaxLength.Value.ToString())})"
                : Precisao.HasValue
                    ? $"({Precisao}, {Escala ?? 0})"
                    : string.Empty;

            var texto = $"{TipoDado}{tamanho} {(Nulavel ? "NULL" : "NOT NULL")}";
            if (IsIdentity)
            {
                texto += " IDENTITY";
            }
            return texto;
        }
    }
}
