namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Indica o tipo de ambiente de destino da conexão, usado pelos módulos de
/// negócio para adaptar consultas (algumas DMVs/comandos diferem entre
/// SQL Server local e Azure SQL Database).
/// </summary>
public enum AmbienteSqlType
{
    Local = 0,
    AzureSql = 1
}

/// <summary>
/// Método de autenticação utilizado na conexão com o SQL Server.
/// </summary>
public enum TipoAutenticacao
{
    SqlServerAuthentication = 0,
    WindowsAuthentication = 1,
    AzureActiveDirectory = 2
}
