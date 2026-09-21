namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma sugestão de índice ausente (&lt;MissingIndexGroup&gt;/&lt;MissingIndex&gt;)
/// que o próprio SQL Server registrou ao compilar o plano — é uma sugestão
/// do otimizador, não uma garantia de que o índice vai ajudar (sempre
/// avaliar o impacto de escrita antes de criar em produção). Preenchido em
/// Modulo_PlanoExecucao.ParsearIndiceFaltante.
/// </summary>
public sealed class PlanoIndiceFaltanteDto
{
    /// <summary>
    /// Percentual de melhoria estimado pelo SQL Server (ex.: 22.87
    /// significa "22,87%") — quanto maior, mais o otimizador acha que esse
    /// índice ajudaria nesta consulta específica. Vem do atributo "Impact"
    /// do &lt;MissingIndexGroup&gt;.
    /// </summary>
    public decimal Impacto { get; set; }

    public string Banco { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string Tabela { get; set; } = string.Empty;

    /// <summary>
    /// Colunas usadas em predicados de igualdade (ex.: WHERE Coluna =
    /// @valor) — viram a chave do índice, na ordem em que aparecem no XML.
    /// </summary>
    public List<string> ColunasIgualdade { get; set; } = new();

    /// <summary>
    /// Colunas usadas em predicados de desigualdade (ex.: WHERE Coluna &gt;
    /// @valor) — também viram chave do índice, depois das de igualdade.
    /// </summary>
    public List<string> ColunasDesigualdade { get; set; } = new();

    /// <summary>
    /// Colunas só "carregadas" pelo índice (INCLUDE) — não fazem parte da
    /// chave, servem apenas para evitar um Key Lookup na tabela.
    /// </summary>
    public List<string> ColunasInclude { get; set; } = new();

    /// <summary>Usado como coluna da grade "Índices Sugeridos" — ex.: "dbo.Vendas".</summary>
    public string TabelaCompleta => string.IsNullOrEmpty(Schema) ? Tabela : $"{Schema}.{Tabela}";

    /// <summary>Usado como coluna da grade "Índices Sugeridos" — colunas de igualdade seguidas das de desigualdade, na ordem em que formam a chave do índice.</summary>
    public string ColunasChaveDescricao => string.Join(", ", ColunasIgualdade.Concat(ColunasDesigualdade));

    /// <summary>Usado como coluna da grade "Índices Sugeridos".</summary>
    public string ColunasIncludeDescricao => string.Join(", ", ColunasInclude);

    /// <summary>Nome sugerido para o índice — gerado aqui, não vem do SQL Server.</summary>
    public string NomeSugerido =>
        $"IX_{Tabela}_{string.Join("_", ColunasIgualdade.Concat(ColunasDesigualdade))}";

    /// <summary>
    /// Script T-SQL pronto (CREATE INDEX ...) para criar o índice sugerido
    /// — gerado aqui seguindo a mesma lógica que o SQL Server Management
    /// Studio usa ao mostrar "Missing Index Details": colunas de igualdade
    /// e desigualdade formam a chave (nessa ordem), colunas de INCLUDE vão
    /// no INCLUDE. Sempre com um comentário lembrando de avaliar o impacto
    /// de escrita antes de rodar em produção.
    /// </summary>
    public string ScriptCriacao
    {
        get
        {
            var chave = ColunasIgualdade.Concat(ColunasDesigualdade).ToList();
            var chaveTexto = chave.Count > 0
                ? string.Join(", ", chave.Select(c => $"[{c}]"))
                : "/* nenhuma coluna de chave identificada */";
            var includeTexto = ColunasInclude.Count > 0
                ? $"\nINCLUDE ({string.Join(", ", ColunasInclude.Select(c => $"[{c}]"))})"
                : string.Empty;

            var tabelaCompleta = string.IsNullOrEmpty(Schema)
                ? $"[{Banco}].[{Tabela}]"
                : $"[{Banco}].[{Schema}].[{Tabela}]";

            return
                $"-- Sugestão automática do SQL Server (impacto estimado: {Impacto:0.##}%)\n" +
                "-- Avalie o custo de escrita (INSERT/UPDATE/DELETE) antes de criar em produção.\n" +
                $"CREATE NONCLUSTERED INDEX [{NomeSugerido}]\nON {tabelaCompleta} ({chaveTexto}){includeTexto};";
        }
    }
}
