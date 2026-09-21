namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um evento de Extended Events disponível para captura na tela "Profiler"
/// (Manutenção &gt; Profiler) — junto com o nome equivalente do SQL Server
/// Profiler clássico, para quem já conhece a ferramenta antiga reconhecer
/// de cara. Lista completa e fixa em Modulo_Profiler.CatalogoEventos —
/// curada a partir dos eventos mais usados do template "Standard" do
/// Profiler clássico; eventos de deadlock (xml_deadlock_report) foram
/// deliberadamente deixados de fora daqui porque já são cobertos pela tela
/// "Headblock e DeadLock" (Manutenção &gt; Headblock e DeadLock).
/// </summary>
public sealed class ProfilerEventoCatalogoItem
{
    /// <summary>Nome real do evento de Extended Events (ex.: "sql_batch_completed") — usado na DDL "CREATE EVENT SESSION".</summary>
    public string NomeEvento { get; set; } = string.Empty;

    /// <summary>Nome equivalente no SQL Server Profiler clássico (ex.: "SQL:BatchCompleted") — mostrado na interface e usado como valor da coluna "EventClass" na grade.</summary>
    public string NomeExibicao { get; set; } = string.Empty;

    /// <summary>Marcado por padrão na aba "Configuração" — mesmo subconjunto de eventos que o template "Standard" do Profiler clássico pré-seleciona.</summary>
    public bool PadraoStandard { get; set; }
}
