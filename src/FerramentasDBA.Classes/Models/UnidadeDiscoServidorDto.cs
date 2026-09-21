namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma unidade de disco fixa do SERVIDOR SQL Server e o espaço livre nela,
/// conforme retornado por <c>xp_fixeddrives</c> (ver
/// <c>Modulo_Admin.ObterUnidadesDiscoServidorAsync</c>) — usado nas telas de
/// Backup/Restore para mostrar ao usuário o disco real do servidor quando a
/// instância conectada é remota (o SaveFileDialog/OpenFileDialog do Windows
/// só enxerga o disco do computador onde a SQL Rocket está rodando,
/// não o do servidor).
/// </summary>
public sealed class UnidadeDiscoServidorDto
{
    /// <summary>Letra da unidade, sem os dois-pontos (ex.: "C", "D").</summary>
    public string Unidade { get; set; } = string.Empty;

    /// <summary>Espaço livre na unidade, em megabytes.</summary>
    public long LivreMb { get; set; }
}
