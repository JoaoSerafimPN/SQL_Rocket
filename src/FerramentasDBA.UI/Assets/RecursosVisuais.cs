using System.Reflection;

namespace FerramentasDBA.UI.Assets;

/// <summary>
/// Carrega os recursos visuais da marca "SQL Rocket" (ícone do app, logo
/// retangular usada dentro das telas, imagem de fundo da tela de
/// boas-vindas) embutidos como <c>EmbeddedResource</c> no
/// assembly — mesmo padrão já usado para os scripts .sql em
/// FerramentasDBA.Classes/Scripts (ver ScriptLoader) — para o executável
/// ficar autocontido, sem depender de arquivos soltos ao lado do .exe.
///
/// Observação sobre o ícone: <c>&lt;ApplicationIcon&gt;</c> no .csproj já
/// define o ícone do PRÓPRIO ARQUIVO .exe (o que aparece no Explorer/atalhos),
/// mas isso NÃO define automaticamente o <see cref="Form.Icon"/> de cada
/// janela em projetos no formato SDK (.NET 5+) — por isso cada Form chama
/// <see cref="CarregarIconeApp"/> explicitamente (título da janela, Alt+Tab,
/// barra de tarefas).
/// </summary>
internal static class RecursosVisuais
{
    private const string PrefixoRecurso = "FerramentasDBA.UI.Assets.";

    /// <summary>
    /// Ícone da aplicação (marca "SQL Rocket" — só o símbolo do foguete
    /// saindo do banco de dados, sem o wordmark, a pedido do usuário: o
    /// wordmark fica ilegível demais nos tamanhos pequenos de ícone), para
    /// uso em <see cref="Form.Icon"/>. Cada chamada devolve uma instância
    /// nova — o chamador (Form) é quem assume a posse dela e a descarta
    /// junto com o Dispose padrão do Form (o WinForms já descarta o Icon
    /// atribuído a Form.Icon automaticamente).
    /// </summary>
    public static Icon CarregarIconeApp()
    {
        using var stream = ObterStreamRecurso("AppIcon.ico");
        return new Icon(stream);
    }

    /// <summary>
    /// Imagem de fundo usada para PREENCHER a tela de boas-vindas
    /// (Menu_Raiz.MostrarBoasVindas), a pedido do usuário. NÃO é a arte
    /// original "crua" (recortada até a área não-transparente, essa fica bem
    /// mais alta que larga — 1240×1064 — e a tela de boas-vindas normalmente
    /// é bem mais larga que alta, já que Menu_Raiz abre MAXIMIZADA; um
    /// "cover" direto da arte crua nessas proporções exigiria um zoom tão
    /// grande pra cobrir a largura toda que só um pedaço borrado apareceria,
    /// cortado — testado e descartado antes de chegar nesta versão). Em vez
    /// disso, este arquivo já é uma composição pronta: uma tela larga
    /// (1920×1080, proporção 16:9, perto da proporção típica da janela
    /// maximizada) com a marca — a versão COM o wordmark "SQLRocket" (a
    /// pedido do usuário, substituindo a versão anterior que usava só o
    /// símbolo do foguete sem o texto), redimensionada com boa qualidade
    /// para 760px de altura (~886px de largura, mantendo a proporção
    /// original) — inteira e centralizada sobre o mesmo "corFundo"
    /// cinza-claro usado no resto do app. Pensada para ser desenhada em modo
    /// "cover" (ver <c>Menu_Raiz.DesenharImagemCover</c>) — preenche a área
    /// toda preservando a proporção original (sem esticar/distorcer),
    /// recortando só uma faixa das bordas quando a proporção real da janela
    /// não bate exatamente com 16:9 — e não com
    /// <see cref="PictureBoxSizeMode.Zoom"/>/<c>Stretch</c> comuns de
    /// <see cref="PictureBox"/>, que deixariam faixas em branco ou
    /// distorceriam a imagem.
    /// </summary>
    public static Image CarregarFundoBoasVindas()
    {
        using var stream = ObterStreamRecurso("WelcomeBackground.png");
        return Image.FromStream(stream);
    }

    /// <summary>
    /// Logo da marca "SQL Rocket" em formato RETANGULAR (recortado só até a
    /// própria arte, SEM o preenchimento quadrado usado no ícone — ver
    /// <see cref="CarregarIconeApp"/>), com o wordmark "SQLRocket" incluído.
    /// Usada no pequeno quadro reservado no cartão de login, ao lado do
    /// título (Login_SQL.Designer.cs). Cada chamada devolve uma instância
    /// nova (mesmo motivo de <see cref="CarregarIconeApp"/>);
    /// quem usa num <see cref="PictureBox"/> deve marcar
    /// <c>PictureBoxSizeMode.Zoom</c> para preservar a proporção original ao
    /// redimensionar.
    /// </summary>
    public static Image CarregarLogoMarca()
    {
        using var stream = ObterStreamRecurso("Logo.png");
        return Image.FromStream(stream);
    }

    private static Stream ObterStreamRecurso(string nomeArquivo)
    {
        var nomeRecurso = PrefixoRecurso + nomeArquivo;
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream(nomeRecurso)
            ?? throw new InvalidOperationException(
                $"Recurso embutido '{nomeArquivo}' não encontrado (esperado: '{nomeRecurso}'). " +
                "Confirme se o arquivo está em Assets/ e listado como EmbeddedResource no .csproj.");
    }
}
