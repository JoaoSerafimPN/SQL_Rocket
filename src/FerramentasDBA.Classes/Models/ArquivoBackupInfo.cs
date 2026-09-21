namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um arquivo (linha) dentro de um arquivo de backup (.bak), conforme
/// retornado por <c>RESTORE FILELISTONLY</c>. Usado para descobrir o nome
/// lógico real dos arquivos de dados (MDF) e log (LDF) originais do banco,
/// necessário para montar as cláusulas <c>MOVE</c> de um <c>RESTORE DATABASE</c>
/// — o nome lógico é definido no banco de origem e não pode ser adivinhado
/// pelo usuário, então é sempre lido do próprio arquivo de backup.
/// </summary>
public sealed class ArquivoBackupInfo
{
    /// <summary>Nome lógico do arquivo dentro do banco de origem (coluna LogicalName).</summary>
    public string NomeLogico { get; set; } = string.Empty;

    /// <summary>Caminho físico original no servidor de onde o backup foi gerado (coluna PhysicalName). Apenas informativo.</summary>
    public string NomeFisicoOriginal { get; set; } = string.Empty;

    /// <summary>Tipo do arquivo (coluna Type): "D" = dados (MDF/NDF), "L" = log (LDF).</summary>
    public string Tipo { get; set; } = string.Empty;
}
