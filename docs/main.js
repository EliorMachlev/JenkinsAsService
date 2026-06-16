/* ============================================================
   JenkinsAsService Documentation — main.js
   Pure vanilla JS — no dependencies, no build tools
   ============================================================ */

(function () {
  'use strict';

  /* -----------------------------------------------------------
     THEME TOGGLE
     ----------------------------------------------------------- */
  const THEME_KEY = 'jas-docs-theme';

  function getPreferredTheme() {
    const stored = localStorage.getItem(THEME_KEY);
    if (stored) return stored;
    return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  }

  function applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem(THEME_KEY, theme);
    updateMermaidTheme(theme);
    updateThemeIcon(theme);
  }

  function updateThemeIcon(theme) {
    var btn = document.getElementById('theme-toggle');
    if (!btn) return;
    btn.setAttribute('aria-label', theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme');
    btn.innerHTML = theme === 'dark'
      ? '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>'
      : '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>';
  }

  function initTheme() {
    applyTheme(getPreferredTheme());
    var btn = document.getElementById('theme-toggle');
    if (btn) {
      btn.addEventListener('click', function () {
        var next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
        applyTheme(next);
      });
    }
  }

  /* -----------------------------------------------------------
     MOBILE SIDEBAR
     ----------------------------------------------------------- */
  function initSidebar() {
    var hamburger = document.getElementById('hamburger');
    var sidebar = document.getElementById('sidebar');
    var overlay = document.getElementById('sidebar-overlay');
    if (!hamburger || !sidebar) return;

    function toggle() {
      var isOpen = sidebar.classList.toggle('open');
      if (overlay) overlay.classList.toggle('open', isOpen);
      hamburger.setAttribute('aria-expanded', String(isOpen));
    }

    function close() {
      sidebar.classList.remove('open');
      if (overlay) overlay.classList.remove('open');
      hamburger.setAttribute('aria-expanded', 'false');
    }

    hamburger.addEventListener('click', toggle);
    if (overlay) overlay.addEventListener('click', close);

    // close on nav click (mobile)
    sidebar.querySelectorAll('a').forEach(function (a) {
      a.addEventListener('click', close);
    });
  }

  /* -----------------------------------------------------------
     ACTIVE NAV HIGHLIGHTING (sidebar)
     ----------------------------------------------------------- */
  function initActiveNav() {
    var links = document.querySelectorAll('.sidebar__nav a');
    if (!links.length) return;

    // Highlight based on current page filename
    var currentPage = location.pathname.split('/').pop() || 'index.html';
    links.forEach(function (link) {
      var href = link.getAttribute('href');
      if (href === currentPage || (currentPage === '' && href === 'index.html')) {
        link.classList.add('active');
      }
    });
  }

  /* -----------------------------------------------------------
     TABLE OF CONTENTS (auto-generated)
     ----------------------------------------------------------- */
  function initTOC() {
    var tocContainer = document.getElementById('toc');
    if (!tocContainer) return;

    var headings = document.querySelectorAll('.content h2, .content h3');
    if (headings.length < 2) {
      tocContainer.closest('.toc-sidebar').style.display = 'none';
      return;
    }

    var ul = document.createElement('ul');

    headings.forEach(function (heading) {
      // Ensure heading has an id
      if (!heading.id) {
        heading.id = heading.textContent.trim()
          .toLowerCase()
          .replace(/[^\w\s-]/g, '')
          .replace(/\s+/g, '-')
          .replace(/-+/g, '-');
      }

      var li = document.createElement('li');
      if (heading.tagName === 'H3') li.classList.add('toc-h3');

      var a = document.createElement('a');
      a.href = '#' + heading.id;
      a.textContent = heading.textContent;
      li.appendChild(a);
      ul.appendChild(li);
    });

    tocContainer.appendChild(ul);

    // Scroll-spy for TOC
    var tocLinks = tocContainer.querySelectorAll('a');
    if ('IntersectionObserver' in window) {
      var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            tocLinks.forEach(function (l) { l.classList.remove('active'); });
            var match = tocContainer.querySelector('a[href="#' + CSS.escape(entry.target.id) + '"]');
            if (match) match.classList.add('active');
          }
        });
      }, { rootMargin: '-80px 0px -70% 0px', threshold: 0 });

      headings.forEach(function (h) { observer.observe(h); });
    }
  }

  /* -----------------------------------------------------------
     COPY-TO-CLIPBOARD
     ----------------------------------------------------------- */
  function initCopyButtons() {
    document.querySelectorAll('pre').forEach(function (pre) {
      // Skip mermaid blocks
      if (pre.querySelector('.mermaid') || pre.classList.contains('mermaid')) return;

      var btn = document.createElement('button');
      btn.className = 'copy-btn';
      btn.textContent = 'Copy';
      btn.setAttribute('aria-label', 'Copy code to clipboard');

      btn.addEventListener('click', function () {
        var code = pre.querySelector('code');
        var text = code ? code.textContent : pre.textContent;
        navigator.clipboard.writeText(text).then(function () {
          btn.textContent = 'Copied!';
          btn.classList.add('copied');
          setTimeout(function () {
            btn.textContent = 'Copy';
            btn.classList.remove('copied');
          }, 2000);
        });
      });

      pre.style.position = 'relative';
      pre.appendChild(btn);
    });
  }

  /* -----------------------------------------------------------
     SEARCH
     ----------------------------------------------------------- */
  var searchIndex = null;

  function loadSearchIndex() {
    if (searchIndex) return Promise.resolve(searchIndex);
    return fetch('search-index.json')
      .then(function (r) { return r.json(); })
      .then(function (data) { searchIndex = data; return data; })
      .catch(function () { searchIndex = []; return []; });
  }

  function initSearch() {
    var overlay = document.getElementById('search-overlay');
    var input = document.getElementById('search-input');
    var results = document.getElementById('search-results');
    var openBtn = document.getElementById('search-btn');
    if (!overlay || !input || !results) return;

    var activeIndex = -1;
    var currentResults = [];

    function openSearch() {
      overlay.classList.add('open');
      input.value = '';
      results.innerHTML = '';
      activeIndex = -1;
      loadSearchIndex().then(function () { input.focus(); });
    }

    function closeSearch() {
      overlay.classList.remove('open');
      input.value = '';
      results.innerHTML = '';
      activeIndex = -1;
    }

    if (openBtn) openBtn.addEventListener('click', openSearch);

    // Keyboard shortcut: Ctrl+K or /
    document.addEventListener('keydown', function (e) {
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault();
        overlay.classList.contains('open') ? closeSearch() : openSearch();
      }
      if (e.key === '/' && !overlay.classList.contains('open') && !isInputFocused()) {
        e.preventDefault();
        openSearch();
      }
      if (e.key === 'Escape' && overlay.classList.contains('open')) {
        closeSearch();
      }
    });

    // Close on overlay background click
    overlay.addEventListener('click', function (e) {
      if (e.target === overlay) closeSearch();
    });

    // Search input handler
    input.addEventListener('input', function () {
      var q = input.value.trim().toLowerCase();
      results.innerHTML = '';
      activeIndex = -1;
      currentResults = [];

      if (!q || !searchIndex) return;

      var matches = searchIndex.filter(function (entry) {
        var haystack = (entry.title + ' ' + (entry.keywords || []).join(' ')).toLowerCase();
        return q.split(/\s+/).every(function (term) { return haystack.indexOf(term) !== -1; });
      });

      currentResults = matches;

      matches.forEach(function (entry, i) {
        var a = document.createElement('a');
        a.className = 'search-result';
        a.href = entry.url;
        a.innerHTML = '<div class="search-result__title">' + escapeHTML(entry.title) + '</div>'
          + '<div class="search-result__url">' + escapeHTML(entry.url) + '</div>';
        a.setAttribute('data-idx', i);
        a.addEventListener('click', function () { closeSearch(); });
        results.appendChild(a);
      });
    });

    // Arrow key navigation in results
    input.addEventListener('keydown', function (e) {
      var items = results.querySelectorAll('.search-result');
      if (!items.length) return;

      if (e.key === 'ArrowDown') {
        e.preventDefault();
        activeIndex = (activeIndex + 1) % items.length;
        highlightResult(items);
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        activeIndex = (activeIndex - 1 + items.length) % items.length;
        highlightResult(items);
      } else if (e.key === 'Enter' && activeIndex >= 0) {
        e.preventDefault();
        items[activeIndex].click();
      }
    });

    function highlightResult(items) {
      items.forEach(function (item, i) {
        item.classList.toggle('active', i === activeIndex);
      });
      if (items[activeIndex]) {
        items[activeIndex].scrollIntoView({ block: 'nearest' });
      }
    }
  }

  function isInputFocused() {
    var el = document.activeElement;
    return el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);
  }

  function escapeHTML(str) {
    var div = document.createElement('div');
    div.appendChild(document.createTextNode(str));
    return div.innerHTML;
  }

  /* -----------------------------------------------------------
     MERMAID
     ----------------------------------------------------------- */
  function updateMermaidTheme(theme) {
    if (typeof mermaid === 'undefined') return;
    mermaid.initialize({
      startOnLoad: false,
      theme: theme === 'dark' ? 'dark' : 'default',
      themeVariables: theme === 'dark' ? {
        primaryColor: '#533483',
        primaryTextColor: '#e0e0e0',
        primaryBorderColor: '#0f3460',
        lineColor: '#58a6ff',
        secondaryColor: '#16213e',
        tertiaryColor: '#1a1a2e'
      } : {
        primaryColor: '#8250df',
        primaryTextColor: '#1f2328',
        primaryBorderColor: '#0969da',
        lineColor: '#0969da',
        secondaryColor: '#f6f8fa',
        tertiaryColor: '#ffffff'
      }
    });
  }

  function initMermaid() {
    if (typeof mermaid === 'undefined') return;

    var theme = document.documentElement.getAttribute('data-theme') || 'dark';
    mermaid.initialize({
      startOnLoad: false,
      theme: theme === 'dark' ? 'dark' : 'default',
      themeVariables: theme === 'dark' ? {
        primaryColor: '#533483',
        primaryTextColor: '#e0e0e0',
        primaryBorderColor: '#0f3460',
        lineColor: '#58a6ff',
        secondaryColor: '#16213e',
        tertiaryColor: '#1a1a2e'
      } : {
        primaryColor: '#8250df',
        primaryTextColor: '#1f2328',
        primaryBorderColor: '#0969da',
        lineColor: '#0969da',
        secondaryColor: '#f6f8fa',
        tertiaryColor: '#ffffff'
      }
    });

    // Render all .mermaid blocks
    var blocks = document.querySelectorAll('.mermaid');
    if (!blocks.length) return;

    blocks.forEach(function (block, idx) {
      var id = 'mermaid-svg-' + idx;
      var graphDef = block.textContent;
      try {
        mermaid.render(id, graphDef).then(function (result) {
          block.innerHTML = result.svg;
        }).catch(function () {
          // Rendering failed — leave text content as-is
        });
      } catch (e) {
        // Sync error — leave text content
      }
    });
  }

  /* -----------------------------------------------------------
     SMOOTH SCROLL (anchor links)
     ----------------------------------------------------------- */
  function initSmoothScroll() {
    document.addEventListener('click', function (e) {
      var a = e.target.closest('a[href^="#"]');
      if (!a) return;
      var id = a.getAttribute('href').slice(1);
      var target = document.getElementById(id);
      if (target) {
        e.preventDefault();
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        history.pushState(null, '', '#' + id);
      }
    });
  }

  /* -----------------------------------------------------------
     INIT
     ----------------------------------------------------------- */
  function init() {
    initTheme();
    initSidebar();
    initActiveNav();
    initTOC();
    initCopyButtons();
    initSearch();
    initSmoothScroll();

    // Mermaid — wait for CDN script
    if (typeof mermaid !== 'undefined') {
      initMermaid();
    } else {
      // Retry once after a short delay in case CDN is loading
      window.addEventListener('load', function () {
        if (typeof mermaid !== 'undefined') initMermaid();
      });
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
