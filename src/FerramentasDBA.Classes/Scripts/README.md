# Scripts T-SQL

Esta pasta guarda os scripts `.sql` que serão usados pelos módulos de
negócio (`Modulos/*`) em vez de T-SQL embutido diretamente no código C#.
Ainda **não há nenhum script aqui** — a estrutura foi criada antecipadamente
para receber os arquivos que serão enviados depois.

## Organização

Uma subpasta por módulo de negócio, no mesmo nome usado em `Modulos/`:

```
Scripts/
├── Indices/        -> consumido por Modulo_Indices.cs
├── Desempenho/      -> consumido por Modulo_Desempenho.cs
├── Admin/           -> consumido por Modulo_Admin.cs (backup, restore, collation, logs, usuários)
├── Informacoes/     -> consumido por Modulo_Informacoes.cs
└── Comparador/      -> consumido por Modulo_Comparador.cs
```

## Como um script chega ao programa

1. O arquivo `.sql` é colocado na subpasta correspondente (ex:
   `Scripts/Admin/BackupFull.sql`).
2. O `FerramentasDBA.Classes.csproj` já está configurado para embutir
   automaticamente qualquer `.sql` dentro de `Scripts/**` como recurso do
   assembly (`EmbeddedResource`) — não precisa editar o `.csproj` a cada
   script novo.
3. O método de negócio correspondente carrega o texto do script em tempo de
   execução via `ScriptLoader.CarregarScriptAsync("Admin/BackupFull.sql")`
   (ver `Infraestrutura/ScriptLoader.cs`) e o executa através de
   `Conectar_SQL`/`SqlCommand`.

## Convenção de nomes sugerida

`NomeDaOperacao.sql`, em PascalCase, refletindo o método que vai consumi-lo
— por exemplo:

- `Scripts/Indices/Fragmentacao.sql` → `Modulo_Indices.ObterFragmentacaoIndicesAsync`
- `Scripts/Admin/BackupFull.sql` → `Modulo_Admin.ExecutarBackupFullAsync`
- `Scripts/Desempenho/Top25Consultas.sql` → `Modulo_Desempenho.ObterTop25ConsultasCustosasAsync`

Isso é só uma sugestão — quando os scripts chegarem, ajustamos o nome/
organização conforme fizer mais sentido.
