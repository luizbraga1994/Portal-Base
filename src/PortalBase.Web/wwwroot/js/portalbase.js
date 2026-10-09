// Portal Base — utilitários de UI mínimos (tema + foco).
// Regra: nenhuma lógica de negócio aqui. Só apresentação.
// Carregado no <head> (script-src 'self' na CSP) para aplicar o tema antes do
// primeiro paint e evitar "flash" de tema errado.
(function () {
  try {
    var t = localStorage.getItem('pdr-theme');
    if (t === 'light' || t === 'dark') {
      document.documentElement.setAttribute('data-theme', t);
    }
  } catch { /* sem armazenamento: tema do sistema aplica */ }
})();

window.portalBase = {
  // Tema: 'light' | 'dark' | 'system'. Persistido por navegador (conveniência do usuário).
  setTheme(theme) {
    try {
      const root = document.documentElement;
      if (theme === 'system') {
        root.removeAttribute('data-theme');
        localStorage.removeItem('pdr-theme');
      } else {
        root.setAttribute('data-theme', theme);
        localStorage.setItem('pdr-theme', theme);
      }
    } catch { /* armazenamento indisponível: ignora, tema padrão aplica */ }
  },
  getTheme() {
    try { return localStorage.getItem('pdr-theme') || 'system'; }
    catch { return 'system'; }
  },
  // Tema que está valendo agora: o escolhido ou, sem escolha, o do sistema operacional.
  effectiveTheme() {
    const t = document.documentElement.getAttribute('data-theme');
    if (t === 'light' || t === 'dark') { return t; }
    try { return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'; }
    catch { return 'light'; }
  },
  // Botão da barra superior: troca para o oposto do tema EFETIVO (com o sistema escuro, o 1º clique vai para o claro).
  toggleTheme() {
    this.setTheme(this.effectiveTheme() === 'dark' ? 'light' : 'dark');
  },
  focus(selector) {
    try { document.querySelector(selector)?.focus(); } catch { /* no-op */ }
  },

  // Focus trap acessível para modais (Prompt 74, §7). Sem IDs fixos: opera sobre o ELEMENTO do modal.
  // activate: registra o gatilho (foco anterior), leva o foco ao elemento inicial e prende Tab/Shift+Tab
  // ao modal. deactivate: solta a armadilha e RESTAURA o foco ao elemento que abriu o modal.
  focusTrap: {
    _traps: new Map(),
    _selecionaveis: 'a[href], area[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    _foco(container) {
      try {
        const nos = container.querySelectorAll(this._selecionaveis);
        return Array.prototype.filter.call(nos, function (n) {
          return !n.hasAttribute('disabled') && n.getAttribute('aria-hidden') !== 'true'
            && (n.offsetWidth > 0 || n.offsetHeight > 0 || n === document.activeElement);
        });
      } catch { return []; }
    },
    activate(container, inicial, chave) {
      try {
        if (!container || !chave) { return; }
        // Se já havia uma armadilha com essa chave (reabertura), solta antes sem restaurar foco.
        const anterior = this._traps.get(chave);
        if (anterior) { try { anterior.container.removeEventListener('keydown', anterior.handler, true); } catch { /* no-op */ } }

        const gatilho = document.activeElement; // elemento que abriu o modal (para restaurar depois).
        const self = this;
        const handler = function (e) {
          if (e.key !== 'Tab') { return; }
          const lista = self._foco(container);
          if (lista.length === 0) { e.preventDefault(); return; }
          const primeiro = lista[0];
          const ultimo = lista[lista.length - 1];
          const ativo = document.activeElement;
          const dentro = container.contains(ativo);
          if (e.shiftKey) {
            if (ativo === primeiro || !dentro) { e.preventDefault(); ultimo.focus(); }
          } else if (ativo === ultimo || !dentro) {
            e.preventDefault(); primeiro.focus();
          }
        };
        container.addEventListener('keydown', handler, true);
        this._traps.set(chave, { container: container, handler: handler, gatilho: gatilho });

        // Foco inicial: elemento indicado, senão o primeiro selecionável.
        const alvo = inicial || this._foco(container)[0];
        if (alvo && typeof alvo.focus === 'function') { alvo.focus(); }
      } catch { /* best-effort: acessibilidade nunca deve quebrar a ação */ }
    },
    deactivate(chave) {
      try {
        const trap = this._traps.get(chave);
        if (!trap) { return; }
        try { trap.container.removeEventListener('keydown', trap.handler, true); } catch { /* no-op */ }
        this._traps.delete(chave);
        // Restaura o foco ao elemento que abriu o modal (se ainda estiver no documento).
        const g = trap.gatilho;
        if (g && typeof g.focus === 'function' && document.contains(g)) { g.focus(); }
      } catch { /* no-op */ }
    }
  }
};

// Barra superior e navegação lateral. O layout é renderizado no SERVIDOR (estático), então os botões não podem usar
// @onclick: são marcados com data-ui e tratados aqui por delegação de eventos (CSP script-src 'self', sem handlers
// inline). Só apresentação. Preferências por navegador (como o tema): menu recolhido. A navegação aprimorada do Blazor
// reescreve o DOM com o HTML do servidor, então o estado é reaplicado a cada página ('enhancedload').
// Grupos do menu (dropdowns): o servidor entrega aberto o grupo da tela atual; os que o usuário abriu à mão ficam
// guardados no navegador e são reabertos a cada página — até a próxima entrada no portal: a tela de login esquece
// essa lista, e quem entra vê só o grupo "01 Principal" aberto.
(function () {
  const CHAVE_NAV = 'pdr-nav';
  const CHAVE_GRUPOS = 'pdr-nav-grupos';

  function shell() { return document.querySelector('.app-shell'); }

  function recolhidaSalva() {
    try { return localStorage.getItem(CHAVE_NAV) === 'recolhida'; } catch { return false; }
  }

  function sincronizarAria(s) {
    const aberta = s.classList.contains('nav-open');
    const recolhida = s.classList.contains('collapsed');
    document.querySelectorAll('[data-ui="alternar-nav"]').forEach(function (b) { b.setAttribute('aria-expanded', String(aberta)); });
    document.querySelectorAll('[data-ui="recolher-nav"]').forEach(function (b) {
      b.setAttribute('aria-pressed', String(recolhida));
      b.setAttribute('aria-label', recolhida ? 'Expandir menu' : 'Recolher menu');
      b.setAttribute('title', recolhida ? 'Expandir menu' : 'Recolher menu');
    });
  }

  function definirGaveta(aberta) {
    const s = shell();
    if (!s) { return; }
    s.classList.toggle('nav-open', aberta);
    sincronizarAria(s);
  }

  function alternarRecolhida() {
    const s = shell();
    if (!s) { return; }
    const recolhida = !s.classList.contains('collapsed');
    s.classList.toggle('collapsed', recolhida);
    try {
      if (recolhida) { localStorage.setItem(CHAVE_NAV, 'recolhida'); } else { localStorage.removeItem(CHAVE_NAV); }
    } catch { /* sem armazenamento: vale só nesta página */ }
    sincronizarAria(s);
  }

  function gruposAbertos() {
    try {
      const v = JSON.parse(localStorage.getItem(CHAVE_GRUPOS) || '[]');
      return Array.isArray(v) ? v.filter(function (c) { return typeof c === 'string'; }) : [];
    } catch { return []; }
  }

  function esquecerGrupos() {
    try { localStorage.removeItem(CHAVE_GRUPOS); } catch { /* sem armazenamento: nada guardado */ }
  }

  function definirGrupo(grupo, aberto) {
    grupo.classList.toggle('is-open', aberto);
    const botao = grupo.querySelector('[data-ui="alternar-grupo"]');
    if (botao) { botao.setAttribute('aria-expanded', String(aberto)); }
  }

  function alternarGrupo(botao) {
    const grupo = botao.closest('[data-grupo]');
    if (!grupo) { return; }
    const aberto = !grupo.classList.contains('is-open');
    definirGrupo(grupo, aberto);
    const chave = grupo.getAttribute('data-grupo');
    const lista = gruposAbertos().filter(function (c) { return c !== chave; });
    if (aberto) { lista.push(chave); }
    try { localStorage.setItem(CHAVE_GRUPOS, JSON.stringify(lista)); } catch { /* sem armazenamento: vale só nesta página */ }
  }

  // Menu do usuário (nome na barra superior): Alterar senha / Sair. Fecha com Esc, clique fora, foco saindo dele e a
  // cada página nova (o servidor sempre o entrega fechado).
  function menuUsuario() { return document.getElementById('menu-usuario'); }

  function menuUsuarioAberto() { const m = menuUsuario(); return !!m && !m.hidden; }

  function definirMenuUsuario(aberto, devolverFoco) {
    const m = menuUsuario();
    const botao = document.querySelector('[data-ui="alternar-menu-usuario"]');
    if (!m || !botao) { return; }
    m.hidden = !aberto;
    botao.setAttribute('aria-expanded', String(aberto));
    if (!aberto && devolverFoco) { botao.focus(); }
  }

  // Aberto = o grupo da tela atual (marcado pelo servidor) ou um que o usuário abriu.
  function aplicarGrupos() {
    const abertos = gruposAbertos();
    document.querySelectorAll('.sidebar [data-grupo]').forEach(function (g) {
      definirGrupo(g, g.classList.contains('nav-group--ativo') || abertos.indexOf(g.getAttribute('data-grupo')) >= 0);
    });
  }

  // Estado salvo (tema, menu recolhido e grupos abertos) aplicado ao DOM que o servidor acabou de entregar; a gaveta
  // começa fechada.
  function aplicar() {
    try {
      const t = localStorage.getItem('pdr-theme');
      if (t === 'light' || t === 'dark') { document.documentElement.setAttribute('data-theme', t); }
    } catch { /* sem armazenamento: tema do sistema */ }
    const s = shell();
    if (!s) {
      // Tela de login (inclusive depois de Sair ou da sessão vencer): a próxima entrada começa com os grupos fechados.
      if (location.pathname.toLowerCase().indexOf('/conta/entrar') === 0) { esquecerGrupos(); }
      return;
    }
    s.classList.toggle('collapsed', recolhidaSalva());
    s.classList.remove('nav-open');
    sincronizarAria(s);
    aplicarGrupos();
    definirMenuUsuario(false, false);
  }

  document.addEventListener('click', function (e) {
    const origem = e.target instanceof Element ? e.target : null;
    if (!origem) { return; }
    if (menuUsuarioAberto() && !origem.closest('.user-menu')) { definirMenuUsuario(false, false); }
    const alvo = origem.closest('[data-ui]');
    if (alvo) {
      switch (alvo.getAttribute('data-ui')) {
        case 'alternar-tema': window.portalBase.toggleTheme(); return;
        case 'recolher-nav': alternarRecolhida(); return;
        case 'alternar-nav': { const s = shell(); definirGaveta(!(s && s.classList.contains('nav-open'))); return; }
        case 'fechar-nav': definirGaveta(false); return;
        case 'alternar-grupo': alternarGrupo(alvo); return;
        case 'alternar-menu-usuario': definirMenuUsuario(!menuUsuarioAberto(), false); return;
        default: return;
      }
    }
    // Escolher um item na gaveta do celular fecha a gaveta (a página seguinte já aparece inteira).
    if (origem.closest('.sidebar a')) { definirGaveta(false); }
  });

  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') { return; }
    if (menuUsuarioAberto()) { definirMenuUsuario(false, true); return; }
    const s = shell();
    if (s && s.classList.contains('nav-open')) { definirGaveta(false); }
  });

  // Tab para fora do menu do usuário também o fecha.
  document.addEventListener('focusout', function (e) {
    const caixa = document.querySelector('.user-menu');
    const destino = e.relatedTarget;
    if (caixa && menuUsuarioAberto() && destino instanceof Node && !caixa.contains(destino)) { definirMenuUsuario(false, false); }
  });

  document.addEventListener('DOMContentLoaded', function () {
    aplicar();
    // blazor.web.js (fim do <body>) já rodou quando o DOMContentLoaded dispara.
    try {
      if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', aplicar);
      }
    } catch { /* sem navegação aprimorada: cada página é carregada inteira e o DOMContentLoaded basta */ }
  });
})();
