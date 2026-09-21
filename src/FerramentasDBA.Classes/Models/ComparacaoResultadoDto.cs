namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Representa uma linha de diferença encontrada por um comparador
/// (de índices ou de estrutura de banco de dados).
/// Estrutura genérica reaproveitável pelos dois comparadores do menu.
/// </summary>
public sealed class ComparacaoResultadoDto
{
    public string ObjetoComparado { get; set; } = string.Empty; // ex: nome do índice ou da tabela
    public string OrigemA { get; set; } = string.Empty;
    public string OrigemB { get; set; } = string.Empty;
    public string TipoDivergencia { get; set; } = string.Empty; // AUSENTE_EM_A, AUSENTE_EM_B, DIVERGENTE
    public string? DetalheDivergencia { get; set; }
}
