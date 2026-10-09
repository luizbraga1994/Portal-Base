# PortalBase.Domain

Núcleo do negócio. **Sem** dependências de infraestrutura (EF, ASP.NET, MySQL,
HTTP, Serilog, OTel, APIs externas). Testável em memória.

## O que vem no template

| Namespace | Conteúdo |
|---|---|
| `Comum` | `Entidade<TId>`, `IRaizAgregado`, `Dinheiro` (`DECIMAL(19,2)`, nunca float), `Cnpj`, guardas `Exigir`. |
| `Excecoes` | `DominioException`, `EntradaInvalidaException`, `EstadoInvalidoException`. |
| `Catalogo` | Tenant (contribuinte), usuário, papéis, permissões, vínculos — o banco de CATÁLOGO central. |
| `Arquivos` | Metadados de arquivo enviado (estado, hash, resultado do antivírus) — banco do TENANT. |
| `Backup` | Registro de backup/restauração. |
| `Incidentes` | Incidente de segurança (governança de plataforma). |
| `Marca` | Marca visual (logo) da plataforma e por tenant. |
| `Notificacoes` | Configuração do e-mail de disparo. |
| `Tarefas` | Tarefa em segundo plano durável (fila no catálogo). |

## Onde entra o domínio do seu portal

Crie um namespace por agregado (ex.: `Pedidos`, `Contratos`), com entidades encapsuladas (sem setters
públicos, fábricas que validam, invariantes no próprio agregado). Persistência no banco do TENANT:
configuração EF em `Infrastructure/Persistencia/BaseTenant/Configuracoes` + `DbSet` no
`PortalBaseTenantDbContext` + migration do tenant (ver `docs/COMO-USAR-O-TEMPLATE.md`).
