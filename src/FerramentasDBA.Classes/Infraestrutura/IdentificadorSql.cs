namespace FerramentasDBA.Classes.Infraestrutura;

/// <summary>
/// Utilitários para montar identificadores T-SQL (nomes de tabela, coluna,
/// índice, login…) dentro de comandos construídos dinamicamente. Identificador
/// não pode ser parametrizado em T-SQL — então a defesa é escapar e delimitar
/// corretamente, e é isso que esta classe centraliza.
///
/// Existe porque o mesmo <c>EscaparColchetes</c> estava duplicado em quatro
/// módulos (Admin, Índices, Manutenção, Comparador): uma correção aplicada em
/// um deles não chegava nos outros.
/// </summary>
public static class IdentificadorSql
{
    /// <summary>
    /// Escapa o caractere de fechamento de colchete, para o valor poder ser
    /// usado com segurança dentro de <c>[...]</c>.
    /// </summary>
    public static string EscaparColchetes(string valor) => valor.Replace("]", "]]");

    /// <summary>
    /// Escapa aspa simples, para o valor poder ser usado dentro de um literal
    /// <c>N'...'</c>.
    /// </summary>
    public static string EscaparAspaSimples(string valor) => valor.Replace("'", "''");

    /// <summary>
    /// Devolve o nome de um objeto já delimitado e pronto para concatenar num
    /// comando: <c>"vendas.Pedido"</c> vira <c>[vendas].[Pedido]</c> e
    /// <c>"Pedido"</c> vira <c>[Pedido]</c>.
    ///
    /// Motivo (bug real encontrado na revisão da v1.0): as telas listavam as
    /// tabelas por <c>sys.tables.name</c>, SEM o schema, e montavam o comando
    /// como <c>[Nome]</c> — que o SQL Server resolve no schema PADRÃO do
    /// usuário (normalmente <c>dbo</c>). Num banco com <c>dbo.Movimento</c> e
    /// <c>auditoria.Movimento</c>, o combo mostrava "Movimento" duas vezes, sem
    /// como distinguir, e a exclusão em lote apagava da <c>dbo</c> mesmo quando
    /// o usuário tinha escolhido a outra. Agora as listagens devolvem o nome
    /// qualificado e é este método que monta o identificador.
    ///
    /// Só separa no PRIMEIRO ponto: um nome de tabela pode conter ponto (é raro,
    /// mas é válido), e nesse caso a origem qualificada continua correta —
    /// <c>"dbo.Minha.Tabela"</c> vira <c>[dbo].[Minha.Tabela]</c>.
    /// </summary>
    public static string Qualificar(string nomeObjeto)
    {
        if (string.IsNullOrWhiteSpace(nomeObjeto))
        {
            throw new ArgumentException("Nome de objeto não informado.", nameof(nomeObjeto));
        }

        var nome = nomeObjeto.Trim();

        // Já veio delimitado pelo chamador (ex.: "[dbo].[Pedido]", vindo de um
        // QUOTENAME) — devolve como está para não delimitar duas vezes.
        //
        // A checagem NÃO pode ser só "começa com [ e termina com ]": um texto
        // como "[x]; DROP TABLE Alvo; --]" satisfaz isso e atravessaria inteiro,
        // sem escape nenhum — justamente o ataque que esta classe existe para
        // impedir. Hoje todos os chamadores passam nomes lidos dos metadados do
        // próprio SQL Server, mas a garantia não pode depender de quem chama.
        // Por isso o atalho só vale quando o texto é REALMENTE uma sequência de
        // identificadores delimitados bem formados (ver EhSequenciaDelimitada);
        // qualquer outra coisa é tratada como nome cru e escapada normalmente.
        if (EhSequenciaDelimitada(nome))
        {
            return nome;
        }

        var separador = nome.IndexOf('.');
        if (separador <= 0 || separador == nome.Length - 1)
        {
            return $"[{EscaparColchetes(nome)}]";
        }

        var esquema = nome[..separador];
        var objeto = nome[(separador + 1)..];
        return $"[{EscaparColchetes(esquema)}].[{EscaparColchetes(objeto)}]";
    }

    /// <summary>
    /// Diz se o texto já é uma sequência de identificadores delimitados bem
    /// formados — <c>[a]</c> ou <c>[a].[b]</c> — no formato que o
    /// <c>QUOTENAME</c> do SQL Server produz.
    ///
    /// "Bem formado" aqui significa: começa em <c>[</c>, cada parte fecha em
    /// <c>]</c>, um <c>]</c> dentro do nome aparece dobrado (<c>]]</c>), e
    /// entre uma parte e outra só existe um ponto. Assim <c>[x]; DROP TABLE
    /// Alvo; --]</c> é recusado (o <c>]</c> do meio não está dobrado e há texto
    /// solto fora dos colchetes) e acaba escapado como nome comum.
    /// </summary>
    private static bool EhSequenciaDelimitada(string nome)
    {
        var i = 0;
        var partes = 0;

        while (i < nome.Length)
        {
            if (nome[i] != '[')
            {
                return false;
            }

            i++; // consome o "["

            var fechou = false;
            while (i < nome.Length)
            {
                if (nome[i] == ']')
                {
                    // "]]" é um colchete escapado DENTRO do nome; um "]" sozinho
                    // fecha o identificador.
                    if (i + 1 < nome.Length && nome[i + 1] == ']')
                    {
                        i += 2;
                        continue;
                    }

                    i++; // consome o "]" que fecha
                    fechou = true;
                    break;
                }

                i++;
            }

            if (!fechou)
            {
                return false;
            }

            partes++;

            if (i == nome.Length)
            {
                return partes is 1 or 2;
            }

            // Só um ponto pode separar duas partes ([esquema].[objeto]).
            if (nome[i] != '.' || partes >= 2)
            {
                return false;
            }

            i++; // consome o "."
        }

        return false;
    }

    /// <summary>
    /// Separa um nome possivelmente qualificado em (esquema, objeto). O esquema
    /// vem nulo quando o nome não é qualificado — nesse caso o chamador deve
    /// deixar o SQL Server resolver pelo schema padrão, ou filtrar por todos.
    /// Útil para as consultas de metadados (<c>sys.tables</c>/<c>sys.indexes</c>),
    /// que precisam comparar esquema e nome em colunas separadas.
    /// </summary>
    public static (string? Esquema, string Objeto) Separar(string nomeObjeto)
    {
        if (string.IsNullOrWhiteSpace(nomeObjeto))
        {
            throw new ArgumentException("Nome de objeto não informado.", nameof(nomeObjeto));
        }

        var nome = nomeObjeto.Trim();
        var separador = nome.IndexOf('.');
        if (separador <= 0 || separador == nome.Length - 1)
        {
            return (null, nome);
        }

        return (nome[..separador], nome[(separador + 1)..]);
    }
}
