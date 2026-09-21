using FerramentasDBA.UI.Forms;

namespace FerramentasDBA.UI;

internal static class Program
{
    /// <summary>
    /// Ponto de entrada da aplicação. Exibe a tela de login (Login_SQL);
    /// após autenticação bem-sucedida, o fluxo seguirá para Menu_Raiz (MDI).
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Rede de segurança para exceções que escapam de qualquer handler:
        // sem isto, uma falha não tratada (ex.: dentro de um "async void" de
        // clique, ou na montagem de uma página) mostrava o diálogo cru do
        // .NET, em inglês e com stack trace, e podia derrubar o processo no
        // meio de uma operação. Agora vira uma mensagem no mesmo padrão do
        // resto do app, e o programa continua de pé quando é seguro continuar.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MostrarErroNaoTratado(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => MostrarErroNaoTratado(e.ExceptionObject as Exception);

        Application.Run(new Login_SQL());
    }

    /// <summary>
    /// Mostra uma falha não tratada em formato legível e grava o detalhe
    /// técnico num arquivo ao lado do executável, para o suporte conseguir
    /// investigar depois (a mensagem da tela sozinha raramente basta).
    /// </summary>
    private static void MostrarErroNaoTratado(Exception? excecao)
    {
        var detalhe = excecao?.ToString() ?? "Erro desconhecido (sem detalhes disponíveis).";
        var caminhoLog = Path.Combine(AppContext.BaseDirectory, "SQLRocket_erros.log");

        try
        {
            File.AppendAllText(caminhoLog,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}{detalhe}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Não conseguir gravar o log não pode virar um segundo erro em
            // cima do primeiro — segue mostrando a mensagem na tela.
        }

        try
        {
            MessageBox.Show(
                "Ocorreu um erro inesperado no SQL Rocket." + Environment.NewLine + Environment.NewLine +
                (excecao?.Message ?? "Sem detalhes disponíveis.") + Environment.NewLine + Environment.NewLine +
                "Se alguma operação estava em andamento (backup, restore, shrink, exclusão em lote), confirme o " +
                "estado dela no SQL Server antes de repetir." + Environment.NewLine + Environment.NewLine +
                $"O detalhe técnico foi gravado em: {caminhoLog}",
                "SQL Rocket", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Sem UI disponível (ex.: falha durante o encerramento) — nada a fazer.
        }
    }
}
