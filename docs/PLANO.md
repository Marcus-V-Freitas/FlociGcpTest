# Evolução da biblioteca Floci GCP

Atualizado em 28/10/2026. Este documento registra o estado implementado e o que ainda falta. Os comandos e exemplos de uso estão no [README](../README.md).

## Implementado

| Área | Comportamento validado |
| --- | --- |
| Aspire | Inicialização única por emulador, consumidores aguardando `gcp-init`, endpoints dinâmicos e destinos distintos para duas APIs. |
| Pub/Sub | Criação REST de tópicos e subscriptions com políticas completas, push real e pull/ack. |
| Recursos existentes | Criação repetida preserva subscriptions e mensagens pendentes; não há reconciliação por update mask. |
| Cloud Storage | Criação idempotente de buckets, upload, listagem, leitura e exclusão pelo SDK .NET. |
| Pastas locais | Cada pasta representa um bucket; importação recursiva, nomes com acentos, buckets vazios e reimportação após alterações. |
| Persistência | Estado interno do emulador em pasta ou volume separado da entrada dos buckets. |
| Resiliência | Retry HTTP limitado, classificação de erros transitórios, falhas permanentes e cancelamento. |
| Diagnóstico | Estado de falha no Aspire, exceção original preservada e logs em arquivos próprios. |
| Testes | AAA, FluentAssertions, NSubstitute e integração com AppHost real. |
| Build | Cake, coleta dos processos filhos, relatório de cobertura e regressão dos metadados de DCP/Dashboard. |
| Caminhos | Fixtures na saída do build, pastas temporárias exclusivas e Cake independente do diretório de execução. |

## Decisões atuais

- Manter uma biblioteca em `src/Floci.Hosting.Gcp`, com uma API de uso próxima à MVFC.Aspire.Helpers.
- Reutilizar container, endpoints, variáveis e UI do Community Toolkit; manter as imagens padrão do toolkit.
- Resolver o destino push pelo endpoint Aspire no contexto de rede do container.
- Criar a subscription com todas as configurações. Preservar a existente; alterações exigem recriação explícita ou um ambiente novo.
- Manter retry, validação e ciclo de vida internos, sem opções públicas de provisionamento.
- Usar pastas e arquivos brutos para a entrada dos buckets. Snapshots internos do Floci ficam fora das fixtures.
- Manter o bundle da CLI habilitado e a descoberta normal pelo SDK, sem caminhos manuais nem supressão de avisos.
- Testar falhas reais e os efeitos observáveis; não acrescentar abstrações ou exceções artificiais para fechar percentuais.

## Limites que permanecem

A política de DLQ é provisionada, mas o encaminhamento de push permanentemente rejeitado não foi comprovado como suportado pela imagem anteriormente exercitada. Não simular esse comportamento na API nem criar teste que declare sucesso por apenas armazenar a política.

A importação de arquivos ocorre na inicialização. Não há sincronização contínua, exportação de uploads para a origem nem exclusão automática de objetos removidos do disco.

Persistir subscriptions também preserva os destinos de push. Uma mudança de porta requer destino estável ou recriação da subscription, pois a biblioteca não altera recursos existentes.

O uso atual é local e voltado a emuladores. A validação em Linux e em CI permanece como etapa de portabilidade; resultados locais em Windows não comprovam esses ambientes.

## Próximas integrações

| Prioridade | Candidatos | Verificação necessária antes de declarar suporte |
| --- | --- | --- |
| P1 | Firestore e Secret Manager | Criar recursos e executar escrita/leitura ou acesso a versões pelo SDK .NET contra o emulador real. |
| P2 | Cloud Tasks e Scheduler | Confirmar criação e entrega HTTP para um endpoint Aspire, incluindo redelivery e cancelamento quando aplicáveis. |
| P2 | Datastore, BigQuery, Logging e Monitoring | Selecionar operações mínimas e verificar o protocolo efetivamente usado pelo SDK .NET. |
| P3 | Cloud SQL, Cloud Run, Functions, Kafka e GKE | Avaliar dependências adicionais e isolamento de containers antes de integrar ao playground. |

Esses itens são propostas, não funcionalidades implementadas. Cada serviço deve entrar com configuração fluente simples, exemplo executável, testes AAA e uma integração real que comprove os comportamentos anunciados. Comparações com emuladores oficiais podem ajudar a separar uma falha da biblioteca de uma limitação do Floci, sem ampliar o cenário básico desnecessariamente.

## Referências

- [MVFC.Aspire.Helpers](https://github.com/Marcus-V-Freitas/MVFC.Aspire.Helpers): referência para o formato de uso.
- [Floci GCP](https://github.com/floci-io/floci-gcp): implementação do emulador.
- [CommunityToolkit Aspire](https://github.com/CommunityToolkit/Aspire): recurso de hospedagem reutilizado pela biblioteca.
