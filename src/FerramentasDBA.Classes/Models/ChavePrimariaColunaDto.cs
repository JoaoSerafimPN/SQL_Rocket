namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma coluna que participa da chave primária REAL de uma tabela, lida
/// diretamente dos metadados do SQL Server (sys.indexes/sys.index_columns/
/// sys.columns, filtrando is_primary_key = 1) — nunca por convenção de nome
/// como "PK_ID" ou "Id". Usada na tela Manutenção &gt; Limpeza de Arquivos
/// para decidir a ordenação de "primeiro/último registro" e para habilitar a
/// limpeza "por ID": essas duas funcionalidades só ficam disponíveis quando
/// a tabela selecionada tem exatamente 1 coluna na chave primária — sem
/// chave primária ou com chave primária composta (mais de 1 coluna) desabilita
/// ambas, com uma mensagem explicando o motivo em vez de adivinhar qual
/// coluna usar. Preenchida em Modulo_Manutencao.ObterChavePrimariaAsync.
/// </summary>
public sealed class ChavePrimariaColunaDto
{
    public string NomeColuna { get; set; } = string.Empty;
    public string TipoDado { get; set; } = string.Empty;

    /// <summary>Posição da coluna dentro da chave primária (sys.index_columns.key_ordinal) — 1 para chave simples.</summary>
    public int Posicao { get; set; }
}
