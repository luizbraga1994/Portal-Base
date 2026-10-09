# ADR — Identidade GLOBAL de usuário × Acesso por TENANT

> **Status:** ACEITO (definitivo). Consolidado no Prompt 74 a partir da análise
> iniciada no Prompt 73. Reclassifica o item de *débito técnico* para
> **BLOQUEIO DE DECISÃO DE PRODUTO/DOMÍNIO**.

## 1. Contexto

As telas `admin/usuarios` (P72) operam o **vínculo** do usuário com o tenant
corrente: conceder/revogar acesso e atribuir papel — governado pela permissão
`admin.usuarios`, que é **escopada ao tenant**. Ações reais existentes:
`GestaoAcesso` (P72).

O domínio de identidade tem dois níveis distintos:

- `Usuario` — **identidade GLOBAL** no catálogo **compartilhado**. Métodos de
  ciclo de vida global: `Ativar`, `Inativar`, `Bloquear`, `DesabilitarMfa`,
  `RevogarSessoes` (operam sobre `StatusUsuario`: Ativo/Inativo/Bloqueado).
- `UsuarioTenant` — **vínculo por tenant** (`StatusAcesso`: Ativo/Revogado). Um
  `Usuario` pode ter vínculo com **vários** tenants.

## 2. Pergunta

"Bloquear/inativar/reset-MFA do usuário" deve ser exposto em `admin/usuarios`?

## 3. Análise de autoridade

Expor a mudança de estado **global** sob a permissão de tenant `admin.usuarios`
permitiria que um administrador de **um** tenant bloqueasse/inativasse a
**identidade** do usuário e, com isso, afetasse o acesso dele a **outros**
tenants — uma **escalada cross-tenant**. Portanto o bloqueio em
`admin/usuarios` **não é ausência de funcionalidade**: é uma **fronteira de
autoridade correta** (fail-closed).

O ciclo de vida **global** de identidade pertence a uma **autoridade de
PLATAFORMA** (`IAutorizacaoPlataforma` — allowlist de administradores de
plataforma), **não** ao vínculo de tenant.

## 4. Estado da arquitetura de plataforma (auditoria P74)

`IAutorizacaoPlataforma` **existe** e já governa operações globais reais:
provisionamento de tenant, incidentes, backup/restauração, marca de plataforma,
e-mail de disparo e administradores da plataforma (ver `Permissoes.Plataforma`). **Porém**, o conjunto de
permissões de plataforma **não** contempla gestão de identidade global de
usuário — não há `plataforma.usuarios.gerir`, nem caso de uso, nem UI, nem
trilha de auditoria específica para essa operação.

Ou seja: a *infraestrutura de autorização de plataforma* existe, mas o
**contrato de produto/domínio** para "administração global de identidade de
usuário" **não** está definido.

## 5. Por que NÃO implementar agora (fabricar seria inseguro)

Habilitar bloquear/inativar/reset-MFA **global** exigiria, com segurança:

1. **Permissão de plataforma nova** (ex.: `plataforma.usuarios.gerir`) — e a
   definição de **quem** é o operador de plataforma para essa ação (modelo de
   suporte/operações), que é uma **decisão de produto** ainda não tomada.
2. **Caso de uso de plataforma** dedicado (ex.: `GestaoIdentidadeUsuario`),
   **fora** de `admin/usuarios`, com UI separada.
3. **Step-up de MFA** obrigatório para a operação (impacto: derruba o acesso do
   usuário a **todos** os tenants).
4. **Auditoria** específica (quem/o quê/quando/antes-depois), com semântica de
   incidente.
5. **Regras de recuperação** (quem reativa? SLA? notificação ao usuário?) —
   decisão de produto.

Nada disso pode ser **inventado** numa rodada de fechamento de passivo interno
(P74 §1: "não criar novas features de produto"; §14: "não fabricar; criar ADR
definitivo"). Fabricar semântica de plataforma sem contrato violaria as regras
transversais (autoridade correta, fail-closed, auditoria, MFA) por presunção.

## 6. Decisão

- **Manter BLOQUEADO** o ciclo de vida **global** de identidade em
  `admin/usuarios`. O domínio **não** é alterado para "remover o BLOCKED".
- **Reclassificar** o item: deixa de ser *débito técnico* e passa a
  **BLOQUEIO DE DECISÃO DE PRODUTO/DOMÍNIO** (categoria B do P74).
- **Pré-requisitos** para desbloquear (quando/se o produto decidir): itens 1–5
  da §5 acima. A infraestrutura de autorização de plataforma
  (`IAutorizacaoPlataforma`) está pronta para receber a nova permissão — o que
  falta é a **decisão de produto/domínio**, não a base técnica.

## 7. Consequência

`admin/usuarios` permanece **tenant-safe por autoridade**: administra apenas o
**vínculo** (conceder/revogar/papel). A identidade global permanece intocável
por administradores de tenant. Nenhuma escalada cross-tenant é possível pela UI
administrativa atual.

## 8. Revisão (07/10/2026) — decisão de produto: MFA no cadastro de usuários

O dono do produto decidiu que o cadastro de usuários (`/admin/usuarios`) deve
permitir **exigir** o MFA de um usuário e **remover** um MFA cadastrado (ex.:
celular perdido). A decisão desbloqueia **só esse** ponto do ciclo de vida
global, com os pré-requisitos da §5 atendidos assim:

| Pré-requisito (§5) | Como ficou |
|---|---|
| 1. Autoridade correta | Permissão `admin.usuarios` no contribuinte **e** `PodeGerirIdentidade`: a conta é exclusiva deste contribuinte (sem vínculo com outro, não é Administrador da plataforma) **ou** o ator é Administrador da plataforma. Sem isso: "não permitido" — nenhuma escalada cross-tenant. |
| 2. Caso de uso dedicado | `GestaoUsuarios.RemoverMfaAsync` / `DefinirMfaObrigatorioAsync` — o único ponto fora do domínio autorizado a chamar `Usuario.DesabilitarMfa` (o teste-guarda `GuardaIdentidadeGlobalTests` permite só este arquivo e confere as travas). `GestaoMfa` continua só com operações do próprio usuário. |
| 3. Step-up de MFA | Remover exige sessão com MFA satisfeito (`MfaRequeridoException` sem ele); o ator nunca remove o próprio MFA por aqui. |
| 4. Auditoria específica | `MfaRemovedByAdministrator` (quem, alvo, quando, contribuinte, nº de códigos de recuperação apagados) e `MfaRequirementChanged` (antes/depois). |
| 5. Recuperação | Remover apaga o segredo **e** os códigos de recuperação e rotaciona a marca de segurança (todas as sessões caem). Se o MFA é exigido, o usuário é levado a cadastrar um novo autenticador no próximo acesso (`/conta/cadastrar-mfa`), sem acessar nada antes. |

**Continua bloqueado:** bloquear a identidade global por administrador de
contribuinte, e qualquer alteração de identidade de conta compartilhada com
outro contribuinte sem o Administrador da plataforma.
