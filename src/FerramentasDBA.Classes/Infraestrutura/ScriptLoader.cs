using System.Reflection;

namespace FerramentasDBA.Classes.Infraestrutura;

/// <summary>
/// Carrega scripts T-SQL (.sql) embutidos como recursos no assembly, a
/// partir da pasta /Scripts (organizada por módulo: Indices, Desempenho,
/// Admin, Informacoes, Comparador — ver Scripts/README.md).
///
/// O .csproj já embute automaticamente qualquer .sql dentro de Scripts/**,
/// então basta colocar o arquivo na subpasta correta e chamar
/// CarregarScriptAsync com o caminho relativo (ex: "Admin/BackupFull.sql").
/// </summary>
public static class ScriptLoader
{
    private const string PrefixoRecurso = "FerramentasDBA.Classes.Scripts.";

    /// <summary>
    /// Lê o conteúdo de um script embutido.
    /// </summary>
    /// <param name="caminhoRelativo">
    /// Caminho relativo à pasta Scripts, com "/" ou "\" (ex: "Admin/BackupFull.sql").
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// O script não foi encontrado como recurso embutido (arquivo ausente ou
    /// ainda não embutido — confira se ele está em Scripts/** no projeto).
    /// </exception>
    public static async Task<string> CarregarScriptAsync(string caminhoRelativo, CancellationToken ct = default)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var nomeRecurso = ConverterParaNomeRecurso(caminhoRelativo);

        await using var stream = assembly.GetManifestResourceStream(nomeRecurso);
        if (stream is null)
        {
            throw new FileNotFoundException(
                $"Script '{caminhoRelativo}' não encontrado como recurso embutido " +
                $"(esperado: '{nomeRecurso}'). Confirme se o arquivo está em Scripts/{caminhoRelativo}.");
        }

        using var leitor = new StreamReader(stream);
        return await leitor.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Lista os caminhos relativos (ex: "Admin/BackupFull.sql") de todos os
    /// scripts atualmente embutidos no assembly. Útil para diagnóstico.
    /// </summary>
    public static IEnumerable<string> ListarScriptsDisponiveis()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var nomeRecurso in assembly.GetManifestResourceNames())
        {
            if (!nomeRecurso.StartsWith(PrefixoRecurso, StringComparison.Ordinal) ||
                !nomeRecurso.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // "FerramentasDBA.Classes.Scripts.Admin.BackupFull.sql" ->
            // separa o restante em partes e junta as intermediárias com "/",
            // preservando o nome do arquivo (que pode ter pontos antes do .sql).
            var restante = nomeRecurso[PrefixoRecurso.Length..];
            var partes = restante.Split('.');
            var pasta = string.Join('/', partes[..^2]);
            var arquivo = string.Join('.', partes[^2..]);
            yield return string.IsNullOrEmpty(pasta) ? arquivo : $"{pasta}/{arquivo}";
        }
    }

    private static string ConverterParaNomeRecurso(string caminhoRelativo)
    {
        // Recursos embutidos usam o padrão "<RootNamespace>.<Pasta>.<Arquivo>",
        // com "/" e "\" virando "." — ex: "Admin/BackupFull.sql" vira
        // "FerramentasDBA.Classes.Scripts.Admin.BackupFull.sql".
        var caminhoNormalizado = caminhoRelativo.Replace('\\', '/').TrimStart('/');
        var partes = caminhoNormalizado.Split('/');
        return PrefixoRecurso + string.Join('.', partes);
    }
}
