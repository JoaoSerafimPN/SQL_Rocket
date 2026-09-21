using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FerramentasDBA.Classes.Models;

namespace FerramentasDBA.Classes.Infraestrutura;

/// <summary>
/// Guarda e lê as conexões salvas da tela de login.
///
/// POR QUE EXISTE: até a v1.0 a tela vinha com servidor/usuário/senha fixos no
/// código-fonte. Isso foi removido ao publicar o repositório (senha em código
/// aberto é problema sério), e o efeito colateral foi ter que redigitar tudo a
/// cada abertura. Esta classe resolve o caso de uso sem trazer o problema de
/// volta: os dados ficam no perfil do usuário, fora do repositório, e a senha
/// é cifrada pelo Windows.
///
/// ONDE FICA: <c>%APPDATA%\SQL Rocket\conexoes.json</c> — por usuário do
/// Windows, nunca ao lado do executável (um .exe numa pasta compartilhada não
/// pode virar um depósito de credenciais de todo mundo).
///
/// COMO A SENHA É PROTEGIDA: <see cref="ProtectedData"/> (DPAPI) com escopo
/// <see cref="DataProtectionScope.CurrentUser"/>. A chave é derivada da conta
/// do Windows, então o arquivo só é legível pela MESMA conta na MESMA máquina;
/// copiá-lo para outro computador não devolve nada. Não é um cofre de senhas
/// corporativo — é o mesmo mecanismo que o Windows usa para credenciais
/// salvas, e é adequado para uma ferramenta desktop de uso pessoal. Se algum
/// dia isso precisar ser compartilhado por uma equipe, o caminho certo é
/// autenticação integrada do Windows (já suportada aqui) ou um cofre externo,
/// não afrouxar este arquivo.
///
/// Windows-only por causa do DPAPI — o que não é limitação prática, já que o
/// aplicativo é WinForms.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConexoesSalvas
{
    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Caminho completo do arquivo de conexões. Exposto para a tela poder
    /// mostrar ao usuário onde os dados dele estão (e para ele poder apagar o
    /// arquivo à mão, se quiser).
    /// </summary>
    public static string CaminhoArquivo => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SQL Rocket",
        "conexoes.json");

    /// <summary>
    /// Lê as conexões salvas. Nunca lança: um arquivo inexistente devolve lista
    /// vazia, e um arquivo corrompido/ilegível também — perder o atalho de
    /// conexão é um aborrecimento, impedir o usuário de abrir a tela de login
    /// por causa disso seria bem pior.
    /// </summary>
    public static List<ConexaoSalvaDto> Listar()
    {
        try
        {
            var caminho = CaminhoArquivo;
            if (!File.Exists(caminho))
            {
                return new List<ConexaoSalvaDto>();
            }

            var json = File.ReadAllText(caminho, Encoding.UTF8);
            var lista = JsonSerializer.Deserialize<List<ConexaoSalvaDto>>(json, OpcoesJson);
            return lista?.Where(c => !string.IsNullOrWhiteSpace(c.Nome)).ToList()
                   ?? new List<ConexaoSalvaDto>();
        }
        catch
        {
            return new List<ConexaoSalvaDto>();
        }
    }

    /// <summary>
    /// Grava (ou substitui, quando já existe uma com o mesmo nome) uma conexão.
    /// A comparação de nome ignora maiúsculas/minúsculas, para não acabar com
    /// "Produção" e "produção" na mesma lista.
    /// </summary>
    /// <param name="conexao">Dados da conexão; a senha já deve vir protegida (ver <see cref="ProtegerSenha"/>).</param>
    public static void Salvar(ConexaoSalvaDto conexao)
    {
        ArgumentNullException.ThrowIfNull(conexao);

        if (string.IsNullOrWhiteSpace(conexao.Nome))
        {
            throw new ArgumentException("A conexão precisa de um nome.", nameof(conexao));
        }

        var lista = Listar();
        lista.RemoveAll(c => string.Equals(c.Nome, conexao.Nome, StringComparison.OrdinalIgnoreCase));
        lista.Add(conexao);
        Gravar(lista);
    }

    /// <summary>Remove a conexão com o nome informado (ignora se não existir).</summary>
    public static void Excluir(string nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
        {
            return;
        }

        var lista = Listar();
        if (lista.RemoveAll(c => string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Gravar(lista);
        }
    }

    /// <summary>
    /// Cifra a senha com DPAPI e devolve em Base64, pronta para
    /// <see cref="ConexaoSalvaDto.SenhaProtegida"/>. Senha vazia devolve nulo
    /// (é o caso "não quero guardar a senha").
    /// </summary>
    public static string? ProtegerSenha(string? senha)
    {
        if (string.IsNullOrEmpty(senha))
        {
            return null;
        }

        var protegida = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(senha), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protegida);
    }

    /// <summary>
    /// Desfaz <see cref="ProtegerSenha"/>. Devolve nulo quando não há senha
    /// guardada ou quando o texto não pode ser decifrado — o caso típico é o
    /// arquivo ter sido copiado de outro computador ou de outro usuário do
    /// Windows, e nesse cenário o certo é a tela pedir a senha, não quebrar.
    /// </summary>
    public static string? RevelarSenha(string? senhaProtegida)
    {
        if (string.IsNullOrWhiteSpace(senhaProtegida))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(senhaProtegida), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static void Gravar(List<ConexaoSalvaDto> lista)
    {
        var caminho = CaminhoArquivo;
        var pasta = Path.GetDirectoryName(caminho);
        if (!string.IsNullOrWhiteSpace(pasta))
        {
            Directory.CreateDirectory(pasta);
        }

        File.WriteAllText(caminho, JsonSerializer.Serialize(lista, OpcoesJson), Encoding.UTF8);
    }
}
