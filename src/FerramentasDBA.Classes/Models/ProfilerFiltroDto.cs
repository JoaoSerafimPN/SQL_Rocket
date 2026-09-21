namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Filtro aplicado às linhas do "Profiler" (Manutenção &gt; Profiler) — usado
/// tanto na aba "Executar" (captura ao vivo) quanto na aba "Analisar
/// arquivo". IMPORTANTE: por decisão de projeto, TODO o filtro aqui é
/// aplicado do lado do CLIENTE (em Modulo_Profiler, depois de ler os
/// eventos), nunca como predicado WHERE dentro da sessão de Extended
/// Events no servidor — evita o risco de uma cláusula WHERE malformada ou
/// com nome de campo incompatível travar a criação da sessão (DDL que não
/// pode ser testada neste ambiente). O preço dessa escolha é que, numa
/// captura muito "barulhenta" (banco com muita atividade), o servidor
/// entrega todos os eventos e o filtro só reduz o que aparece na grade —
/// aceitável para uma ferramenta de diagnóstico pontual como esta.
/// </summary>
public sealed class ProfilerFiltroDto
{
    /// <summary>
    /// Nomes de evento de Extended Events (ex.: "sql_batch_completed") a
    /// capturar/mostrar — ver Modulo_Profiler.CatalogoEventos para a lista
    /// completa disponível e o mapeamento com o nome estilo Profiler
    /// clássico (ex.: "SQL:BatchCompleted").
    /// </summary>
    public List<string> EventosSelecionados { get; set; } = new();

    /// <summary>Só mostra linhas cujo ApplicationName contenha este texto (case-insensitive). Vazio/null = não filtra.</summary>
    public string? AplicativoContem { get; set; }

    /// <summary>Só mostra linhas cujo DatabaseName contenha este texto (case-insensitive). Vazio/null = não filtra.</summary>
    public string? BancoContem { get; set; }

    /// <summary>Só mostra linhas cujo LoginName contenha este texto (case-insensitive). Vazio/null = não filtra.</summary>
    public string? LoginContem { get; set; }

    /// <summary>Só mostra linhas cujo HostName contenha este texto (case-insensitive). Vazio/null = não filtra.</summary>
    public string? HostContem { get; set; }

    /// <summary>Só mostra linhas cujo TextData contenha este texto (case-insensitive). Vazio/null = não filtra.</summary>
    public string? TextoContem { get; set; }

    /// <summary>Só mostra linhas com Duration (em milissegundos) maior ou igual a este valor. Linhas sem Duration (eventos "instantâneos", ex. Audit Login) NÃO são excluídas por este filtro — a comparação só se aplica quando a linha tem duração.</summary>
    public long? DuracaoMinimaMs { get; set; }

    /// <summary>
    /// Quando marcado, oculta comandos internos do próprio SQL Server/driver
    /// (heurística simples: TextData começando com "SET " isolado, ou
    /// eventos sem TextData nenhum já são tratados à parte) — nesta
    /// primeira versão é aplicado apenas como filtro de "tem texto de
    /// comando" para eventos do tipo Completed/Starting; eventos de
    /// conexão/login continuam aparecendo normalmente pois não são
    /// "comandos".
    /// </summary>
    public bool SomenteComandosDeUsuario { get; set; }

    /// <summary>
    /// Quando marcado (padrão true), oculta da grade as próprias consultas
    /// desta ferramenta (ex.: o polling do "Executar" lendo o ring buffer,
    /// ou qualquer outra tela do app usando a mesma conexão) — reconhecidas
    /// pelo ApplicationName padrão do SqlClient para este processo (ver
    /// Modulo_Profiler.NomeAplicativoProprio). Sem isso, a captura ao vivo
    /// entraria em "loop visual" mostrando a si mesma.
    /// </summary>
    public bool ExcluirConsultasDoProprioApp { get; set; } = true;
}
