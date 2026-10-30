# Floci GCP com .NET Aspire

Biblioteca para configurar Pub/Sub e Cloud Storage no Floci pelo AppHost. A API demonstra os SDKs Google para .NET; o provisionamento dos recursos fica em `src/Floci.Hosting.Gcp`.

## Executar

Requisitos: SDK .NET 10, Docker com containers Linux e bundle da CLI do Aspire disponível para o SDK.

```powershell
dotnet run --project FlociGcpTest.AppHost
```

O AppHost declara `gcp`, `gcp-init`, `apiservice` e a UI do Floci. A API aguarda a conclusão de `gcp-init`. Use os endereços mostrados no dashboard; o perfil atual da API usa `http://localhost:5009` e a UI usa a porta `14500`.

A pasta local e a UI são configuradas diretamente em [AppHost.cs](FlociGcpTest.AppHost/AppHost.cs), pelas chamadas `WithBucketFolder` e `WithFlociUI`. Não há parâmetros `Floci:BucketFolder` ou `Floci:EnableUI` na aplicação atual.

O AppHost mantém `AspireUseCliBundle=true`. O SDK descobre o bundle e grava os caminhos de DCP e Dashboard nos metadados da aplicação compilada. Os testes verificam esses metadados e a existência dos executáveis. Após mudar a configuração ou restaurar o projeto, compile antes de usar `dotnet test --no-build`.

As imagens seguem os padrões do Community Toolkit. Não é necessário configurar credenciais Google nem instalar gcloud. Os clientes Pub/Sub do playground usam `EmulatorOnly`; o cliente de Storage usa o endpoint local sem autenticação.

## Configurar no Aspire

```csharp
using Floci.Hosting.Gcp;

var builder = DistributedApplication.CreateBuilder(args);
var api = builder.AddProject<Projects.MyApi>("api")
    .WithHttpHealthCheck("/health");

var gcp = builder.AddFlociGcpEnvironment("gcp", projectId: "floci-local")
    .WithBuckets("test-bucket")
    .WithBucketFolder("../bucket-data")
    .WithPubSubConfigs(new PubSubConfig("floci-local",
        new MessageConfig("test-topic", "push-subscription"),
        new MessageConfig("test-topic", "pull-subscription")))
    .WithPushEndpoint("push-subscription", api.GetEndpoint("http"), "/pubsub/push");

api.WithReference(gcp);
await builder.Build().RunAsync();
```

`AddFlociGcpEnvironment` reutiliza o container do Community Toolkit e registra a inicialização uma vez por emulador. Cada consumidor recebe as variáveis dos SDKs e aguarda sua conclusão por `WithReference(gcp)`.

`WithPushEndpoint` associa uma subscription declarada a um endpoint Aspire. A URL é resolvida no contexto de rede do container, com a porta alocada pelo Aspire. Cada API pode ter seu próprio destino. Para outro projeto, informe `projectId` nessa chamada. Para uma URL externa ao modelo Aspire, use `MessageConfig.PushEndpoint` com uma URI HTTP ou HTTPS absoluta.

Use `WithBucketConfigs(new BucketConfig("bucket-name", "other-project"))` para declarar buckets de outro projeto. O FluentValidation verifica configurações e vínculos antes da inicialização; nomes de recursos são validados pelos SDKs e pelo emulador.

### Criação e recursos existentes

Tópicos e subscriptions são criados por REST. Push, ack deadline, retry policy e dead-letter policy são enviados na criação da subscription. Essa interface preserva campos omitidos pelo mapeamento gRPC de criação nas imagens verificadas do Floci.

Recursos existentes são preservados: a biblioteca não consulta, compara nem atualiza subscriptions. Para aplicar outra configuração, recrie a subscription ou o estado local. Se persistir Pub/Sub e mudar a porta da API, o destino antigo também será preservado; use uma porta estável ou recrie a subscription.

As extensões do toolkit, incluindo `WithDataVolume`, `WithDataBindMount` e `WithFlociUI`, continuam disponíveis.

### Pastas locais como buckets

`WithBucketFolder` recebe o mesmo layout de entrada usado no helper de Cloud Storage:

```text
bucket-data/
  veragi/
    samples/
      local.txt
  outro-bucket/
    documento.json
```

Cada subpasta imediata representa um bucket. Os arquivos são enviados pelo SDK na inicialização, preservando bytes e subpastas. Assim, `veragi/samples/local.txt` se torna o objeto `samples/local.txt` do bucket `veragi`.

Buckets encontrados no disco são criados automaticamente, inclusive os vazios. Buckets declarados explicitamente mantêm o projeto configurado; os demais usam o projeto padrão. Arquivos diretamente na raiz de `bucket-data` são ignorados.

Caminhos relativos são resolvidos a partir da pasta do AppHost. O playground usa `../bucket-data`, na raiz do repositório. Reinicie o AppHost após adicionar ou alterar arquivos: cada inicialização reenviará os arquivos locais, substituindo objetos de mesmo nome. Remover um arquivo local não exclui sua cópia de um emulador persistido. Uploads pela API não são exportados para a pasta de entrada.

Para persistir os uploads e o estado dos serviços, configure outra pasta, fora da entrada dos buckets:

```csharp
gcp.WithDataBindMount("./emulator-state");
```

Essa pasta contém o estado interno do Floci. Snapshots JSON do emulador não são fixtures de arquivos locais e não devem ser colocados dentro dos buckets.

### Fixtures e exemplos HTTP

Apenas estes arquivos compõem os exemplos e fixtures de arquivos locais:

| Arquivo | Uso |
| --- | --- |
| `bucket-data/veragi/samples/local.txt` | Exemplo carregado pelo AppHost e consultado pelo `.http`. |
| `FlociGcpTest.AppHost/storage-fixture/test-bucket/samples/local.txt` | Fixture usada pelo teste de importação e persistência. |
| `FlociGcpTest.AppHost/storage-fixture/veragi/samples/local.txt` | Entrada adicional importada pela suíte e disponível para demonstração. |

Os testes copiam a fixture para uma pasta temporária própria. Eles também criam arquivos para verificar mudanças no disco e buckets vazios.

Para usar a fixture manualmente, altere o argumento no AppHost para `.WithBucketFolder("./storage-fixture")` e reinicie. A aplicação não lê essa escolha de argumentos da linha de comando.

Abra [os exemplos HTTP](FlociGcpTest.ApiService/FlociGcpTest.ApiService.http). As últimas requisições usam `LocalBucketName=veragi` para listar e ler `samples/local.txt`, sem upload pela API. Os endpoints de Storage aceitam `bucketName` opcional; quando omitido, usam `test-bucket`.

| Rota | Operação |
| --- | --- |
| `POST /pubsub/publish` | Publica uma string JSON em `test-topic` e retorna o ID. |
| `GET /pubsub/received` | Consulta as mensagens recebidas por push em memória. |
| `GET /pubsub/pull` | Consome e confirma mensagens da subscription pull. |
| `POST /storage/upload?fileName=hello.txt` | Grava uma string JSON como objeto. |
| `GET /storage/list?bucketName=veragi` | Lista objetos do bucket escolhido. |
| `GET /storage/download?bucketName=veragi&fileName=samples/local.txt` | Lê o conteúdo pelo playground. |
| `DELETE /storage/object?fileName=hello.txt` | Exclui um objeto. |

Para conferir a entrega real, compare o ID retornado por `/pubsub/publish` com `/pubsub/received`.

## Retry, falhas e logs

O provisionamento usa uma política Polly para falhas HTTP transitórias: até sete tentativas, espera inicial de 500 ms, backoff exponencial, jitter e teto de cinco segundos entre tentativas. A inicialização completa tem orçamento de dois minutos e respeita cancelamento.

Erros permanentes interrompem a inicialização, marcam `gcp-init` como `Exited` com código 1 e preservam a exceção original. Cancelamento do chamador encerra a inicialização sem relançar a exceção. Logs source-generated ficam separados em `ProvisioningLog` e `PlaygroundLog`.

A API reutiliza os clientes Google por DI e propaga cancelamento por requisição. O retry de entrega push pertence ao emulador, conforme a política da subscription. Falhas de infraestrutura continuam sendo exceções.

O playground apenas guarda os pushes em uma fila em memória para inspeção. Não implementa deduplicação, controle de tentativas ou regras de processamento; os dados são descartados quando a API encerra.

## Testes e cobertura

```powershell
# Sem Docker
dotnet test tests/Floci.Hosting.Gcp.Tests --filter "Category!=Integration"

# Suíte completa com Docker
dotnet test tests/Floci.Hosting.Gcp.Tests

# Build Release, testes e relatório de cobertura
dotnet tool restore
dotnet cake build.cake
```

A suíte usa AAA, FluentAssertions e NSubstitute. As integrações iniciam o AppHost real, removem a UI do modelo dos testes, isolam as pastas de dados e usam portas dinâmicas. Elas não dependem de flags ou de snapshots da pasta de exemplos.

Para CI/CD, os fixtures são copiados pelo build para `Fixtures` na saída dos testes. As pastas de persistência são temporárias, exclusivas por teste e removidas ao final. O Cake resolve seus caminhos a partir de `build.cake`, independentemente do diretório de execução. Os exemplos de texto mantêm LF também em checkouts Windows.

O agente precisa do SDK .NET 10 e de Docker com containers Linux. O teste de persistência usa bind mount: o daemon Docker precisa acessar a pasta temporária do agente. Em Docker remoto ou Docker-in-Docker, essa pasta deve ser compartilhada no mesmo caminho. Em Linux, somente a pasta temporária de estado recebe permissão de escrita para o usuário do container.

São verificados push em duas APIs, pull, políticas de criação, preservação de subscriptions existentes, Storage, importação e reimportação de arquivos locais, falha e cancelamento de inicialização. Os testes isolados cobrem validações, falhas HTTP, limites de retry e chamadas aos clientes Google.

O Cake coleta a biblioteca, a API e o AppHost, incluindo os processos filhos dos testes. Resultados:

- `artifacts/coverage-report/index.html`: relatório HTML.
- `artifacts/coverage-report/Summary.txt`: resumo.
- `artifacts/coverage/coverage.cobertura.xml`: coleta original.
- `artifacts/test-results`: resultados TRX.

```powershell
dotnet cake build.cake --integration=false
dotnet cake build.cake --configuration=Debug
dotnet cake build.cake --target=Build
dotnet cake build.cake --target=Test
dotnet cake build.cake --target=Report
```

`Report` usa a coleta existente. Uma execução sem integração mede apenas os caminhos unitários. O ramo residual observado no rethrow assíncrono de `GcpProvisioner` é gerado pelo compilador; a propagação da exceção e a preservação do stack são verificadas sem excluir o trecho da coleta.

## Limites e evolução

Na imagem Floci GCP `0.11.0` anteriormente exercitada, a política de dead-letter é armazenada, mas mensagens push permanentemente rejeitadas não são encaminhadas à DLQ após o limite. A biblioteca provisiona a política nativa; a suíte não anuncia esse encaminhamento como suportado. O comportamento depende da imagem escolhida pelo toolkit.

Esta biblioteca valida Pub/Sub e Cloud Storage. Outros serviços expostos pelo container ainda não têm provisionamento e compatibilidade .NET comprovados aqui. A integração foi exercitada em Windows; outras plataformas devem executar a mesma suíte. Consulte o [plano de evolução](docs/PLANO.md).
