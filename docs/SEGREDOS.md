# Gerenciamento de Segredos e Cofre Gerenciado (Prompt 06)

Como o Portal Base guarda e obtém segredos sem NUNCA os expor em código, `appsettings`,
banco, logs ou auditoria. A aplicação conhece apenas **referências**; os valores vêm de um
**cofre gerenciado**, autenticado por **identidade da aplicação** (sem client secret estático).

## Princípio

O código conhece o **identificador** do segredo, nunca o **valor**. Ex.: a chave HMAC da
auditoria é resolvida por `IProvedorSegredos.ObterAsync(referencia)` — o valor vive só em
memória, obtido do cofre em runtime.

## Arquitetura e seleção por ambiente

`IProvedorSegredos` é a porta única (Application). A composição escolhe a implementação por
ambiente (`ConfiguracaoSegredos.AddSegredos`, seções 5-7):

| Ambiente | Provedor | Autenticação |
|---|---|---|
| **Production** (nuvem) | `ProvedorSegredosKeyVault` (Azure Key Vault) + cache | **Identidade gerenciada** (`DefaultAzureCredential`) — sem segredo estático |
| **Production** (servidor Windows/IIS) | `ProvedorSegredosCofreWindows` (`Vault:Modo=CofreWindows`) + cache | Arquivos cifrados por **DPAPI de máquina** em `Vault:DiretorioCofreWindows`, protegidos por ACL; geridos por `scripts/cofre-windows.ps1` |
| **Development/Test** | `ProvedorSegredosConfiguracao` (`Segredos:<ref>`) | ambiente/user-secrets, **nunca** em Produção |

**Fail-fast (seção 7):** em Produção, `Vault:Modo` deve ser `KeyVault` (com `Vault:Endpoint`) ou
`CofreWindows` (com `Vault:DiretorioCofreWindows`, só no Windows) — caso contrário o startup
**falha** (sem fallback silencioso, sem downgrade de segurança). O provedor de Dev/Test é
**proibido** em Produção.

## Vault (Azure Key Vault)

- **Tecnologia:** Azure Key Vault (Secrets).
- **Identidade:** `DefaultAzureCredential` → Managed Identity / Workload Identity — **nenhum
  client_id+client_secret armazenado na aplicação** (seções 22, 23).
- **Menor privilégio (seção 23):** a identidade de runtime só lê os segredos de que precisa;
  administração/deploy usam identidades separadas — nunca a conta administrativa como runtime.
- **Retry/timeout (seção 30):** `SecretClientOptions` com 3 tentativas exponenciais e timeout de
  rede; falha do cofre **propaga** (fail-closed), nunca vira fallback.
- **Config (não sensível):** `Vault:Modo`, `Vault:Endpoint`, `Vault:Ambiente`,
  `Vault:CacheTtlSegundos`. Nunca senha/segredo/chave.

## Nomenclatura (server-side, determinística)

`NomeSegredo` monta os nomes SEMPRE no servidor, a partir do ambiente e do **identificador
técnico** do tenant — nunca de valor do cliente e nunca com CNPJ/e-mail/razão social (seções 15,
24). Formato compatível com o cofre (`^[0-9a-zA-Z-]+$`, ≤127):

| Segredo | Nome lógico |
|---|---|
| HMAC da auditoria | `portalbase-{amb}-audit-hmac` |
| Proteção do TOTP | `portalbase-{amb}-security-totp-protection` |
| Hash de identidade | `portalbase-{amb}-security-identity-hash` |
| Senha do SMTP (tela da plataforma) | `portalbase-{amb}-email-smtp-password` |
| Segredo de integração (tenant) | `portalbase-{amb}-tenant-{tenantId:N}-integ-secret` |

O segredo de integração por tenant (`TipoSegredoTenant.SegredoIntegracao`) é o **modelo** do
padrão por tenant: cada segredo novo do seu domínio (ex.: `client_secret` de uma API, senha de um
ERP) ganha um tipo próprio, com nome derivado no servidor e permissão definida em
`AcessoSegredoTenant`.

Em Produção, as referências das chaves criptográficas (`Auditoria:ChaveIntegridadeRef`,
`SegurancaIdentidade:*`) apontam para estes nomes canônicos.

## Classificação de segredos

- **Criptográficos:** chave HMAC da auditoria; chave AES-256 de proteção do TOTP; hash de
  identidade. Vêm do cofre; nunca em `appsettings`/banco/log.
- **Integrações:** segredo de integração por tenant (`SegredoIntegracao`); senha do SMTP de
  disparo (definida pela tela de Configurações → E-mail SMTP).
- **Aplicação:** connection strings do catálogo/tenant (senha resolvida do cofre; a string final
  nunca é registrada).

## Isolamento por tenant

`IAcessoSegredoTenant` **deriva o nome do segredo do tenant autorizado** e autoriza no servidor
(para `SegredoIntegracao`, a permissão `admin.tenant`) antes de tocar o cofre — um cliente não
escolhe a referência e um tenant não alcança o segredo de outro (seção 24). Cada leitura ou
definição bem-sucedida é auditada (`SecretAccessed`/`SecretUpdated`) **sem** o valor.

## Cache e rotação

Cache em memória com **TTL curto** (padrão 300 s), nunca persistente nem serializado, com
invalidação para **rotação** (seções 11, 12). Rotacionar HMAC/TOTP/SMTP/segredos de integração é
trocar o valor no cofre — a aplicação obtém o novo pela mesma referência. Para chaves que já protegem dados
(ex.: AES do TOTP), a rotação segura exige **re-encriptação/versionamento** (coexistência
temporária) — endurecimento futuro; NÃO se implementa rotação destrutiva que torne dados
existentes indecifráveis.

## Development

O provedor de configuração é **explicitamente Dev/Test** e nunca roda em Produção. Regras:
nunca valores reais, nunca senha/chave real, nunca copiar segredos de produção, nunca
commitar segredos. Segredos locais vêm de variáveis de ambiente/user-secrets.

## Sem endpoints de segredo

Não há `GET /secrets/{name}` nem download de segredo/chave. A aplicação **usa** o segredo; o usuário
nunca o recebe. Segredos jamais vão para claims, sessão, HttpContext ou o circuito Blazor.

## Incident response (básico)

Suspeita de vazamento de um segredo: (1) **rotacionar** o valor no cofre (nova versão),
(2) invalidar o cache, (3) revisar a auditoria de acesso (`SecretAccessed`/`SecretUpdated`)
por tenant/ator/correlação, (4) para chaves que protegem dados, planejar re-encriptação com
coexistência de versões. Nunca apagar a trilha para "limpar".
