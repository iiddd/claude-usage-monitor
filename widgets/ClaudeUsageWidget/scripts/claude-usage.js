/* Shared rendering module for Claude Code Usage widget.
   Used by both index.html (iCUE widget) and preview iframes.
   Polling, lifecycle, and config are owned by each host — this module only
   reads/writes the DOM and formats values. */
(function (global) {
    'use strict';

    // Active rendering config. All values are always concrete — defaults match the CSS theme.
    var _cfg = {
        thresholdColorCoding: true,
        textColor:       '#FFFFFF',
        blockColor:      '#3B82F6',
        percentageColor: '#FFFFFF',
        colorLow:        '#10B981',
        colorMid:        '#F59E0B',
        colorHigh:       '#EF4444',
        backgroundColor: '#161616',
        transparency:    0
    };

    // Last successfully rendered data — re-applied when config changes mid-session.
    var _lastData = null;

    // ── Color helpers ──────────────────────────────────────────────────

    function thresholdColor(utilization) {
        if (utilization < 0.6) return _cfg.colorLow;
        if (utilization < 0.8) return _cfg.colorMid;
        return _cfg.colorHigh;
    }

    function resolveBlockColor(utilization) {
        return _cfg.thresholdColorCoding ? thresholdColor(utilization) : _cfg.blockColor;
    }

    function resolvePctColor(utilization) {
        return _cfg.thresholdColorCoding ? thresholdColor(utilization) : _cfg.percentageColor;
    }

    // ── Theme CSS variable application ─────────────────────────────────

    function applyThemeVars() {
        var root = document.documentElement;
        root.style.setProperty('--bg-color',      _cfg.backgroundColor);
        root.style.setProperty('--widget-opacity', String(1 - _cfg.transparency / 100));
        root.style.setProperty('--text-color',     _cfg.textColor);
    }

    // ── Time formatting ────────────────────────────────────────────────

    function formatTimeUntil(resetsAt) {
        var diffMs = new Date(resetsAt).getTime() - Date.now();
        if (diffMs < 0)      return 'idle';
        if (diffMs < 60000)  return 'resetting...';

        var totalMinutes = Math.floor(diffMs / 60000);
        var totalHours   = Math.floor(totalMinutes / 60);
        var days         = Math.floor(totalHours / 24);
        var hours        = totalHours % 24;
        var minutes      = totalMinutes % 60;

        if (days >= 1)       return days + 'd ' + hours + 'h';
        if (totalHours >= 1) return totalHours + 'h ' + minutes + 'm';
        return totalMinutes + 'm';
    }

    // ── Block rendering ────────────────────────────────────────────────

    /* Returns a stable string key describing a block's visual state. */
    function blockKey(i, threshold, color) {
        var fi = Math.floor(threshold);
        if (i < fi) return 'f:' + color;
        if (i === fi && (threshold % 1) > 0.001)
            return 'p:' + Math.round((threshold % 1) * 10000) + ':' + color;
        return 'e';
    }

    /* Returns the CSS background value for a block state key. */
    function blockBg(key) {
        if (key === 'e') return '#2C2C2C';
        if (key.charAt(0) === 'f') return key.slice(2);
        var parts = key.split(':');
        var frac  = parseInt(parts[1], 10) / 100;
        var color = parts[2];
        return 'linear-gradient(to right,' + color + ' ' + frac + '%,#2C2C2C ' + frac + '%)';
    }

    function ensureBlocks(barEl) {
        if (!barEl || barEl.querySelector('.block')) return;
        for (var i = 0; i < 10; i++) {
            var b = document.createElement('div');
            b.className = 'block';
            barEl.appendChild(b);
        }
    }

    /*
     * Diff-based block updater.
     * - If utilization is unchanged from last call, does nothing.
     * - Otherwise, only transitions blocks whose visual state changed.
     * - First render is instant (no animation).
     * - Changed blocks: fade out (instant), swap background, fade in 400ms.
     * - Single forced reflow per bar batches all block updates.
     * - configure() clears prevUtil so a color-scheme change forces a full re-render.
     */
    function applyBlocks(barEl, utilization) {
        ensureBlocks(barEl);

        var prevRaw  = barEl.dataset.prevUtil;
        var prevUtil = (prevRaw !== undefined && prevRaw !== '') ? parseFloat(prevRaw) : NaN;
        var isFirst  = isNaN(prevUtil);

        if (!isFirst && Math.abs(prevUtil - utilization) < 0.00001) return;
        barEl.dataset.prevUtil = String(utilization);

        var color      = resolveBlockColor(utilization);
        var threshold  = utilization * 10;
        // prevColor uses current _cfg — safe because configure() clears prevUtil,
        // making isFirst=true after any color scheme change.
        var prevColor  = isFirst ? null : resolveBlockColor(prevUtil);
        var prevThresh = isFirst ? NaN  : prevUtil * 10;

        var blocks    = barEl.querySelectorAll('.block');
        var toAnimate = [];

        blocks.forEach(function (block, i) {
            var newKey = blockKey(i, threshold, color);

            if (isFirst) {
                block.style.background = blockBg(newKey);
                return;
            }

            var oldKey = blockKey(i, prevThresh, prevColor);

            if (newKey === oldKey) return;

            block.style.transition = 'none';
            block.style.opacity    = '0';
            block.style.background = blockBg(newKey);
            toAnimate.push(block);
        });

        if (toAnimate.length === 0) return;

        /* Single reflow so all blocks register opacity:0 before transition starts. */
        void barEl.offsetWidth;

        toAnimate.forEach(function (b) {
            b.style.transition = 'opacity 400ms ease-in-out';
            b.style.opacity    = '1';
        });
    }

    // ── Row rendering ──────────────────────────────────────────────────

    function renderRow(barId, pctId, resetId, utilization, resetsAt) {
        var barEl   = document.getElementById(barId);
        var pctEl   = document.getElementById(pctId);
        var resetEl = document.getElementById(resetId);
        if (!barEl || !pctEl || !resetEl) return;

        applyBlocks(barEl, utilization);

        pctEl.textContent = Math.round(utilization * 100) + '%';
        pctEl.style.color = resolvePctColor(utilization);
        resetEl.textContent = formatTimeUntil(resetsAt);
    }

    // ── Public API ─────────────────────────────────────────────────────

    /*
     * Apply a new rendering config. Only keys present in opts are updated.
     * Clears block caches and immediately re-renders if data is available,
     * so color scheme changes take effect without waiting for the next poll.
     */
    function configure(opts) {
        var k;
        for (k in opts) {
            if (Object.prototype.hasOwnProperty.call(opts, k) && k in _cfg) {
                _cfg[k] = opts[k];
            }
        }

        applyThemeVars();

        // Clearing prevUtil forces applyBlocks to treat the next render as "first"
        // so it re-computes block colors against the new _cfg.
        var bars = document.querySelectorAll('.block-bar');
        for (var i = 0; i < bars.length; i++) {
            bars[i].dataset.prevUtil = '';
        }

        if (_lastData) renderData(_lastData);
    }

    function renderData(data) {
        _lastData = data;

        var errorOverlay   = document.getElementById('error-overlay');
        var loadingOverlay = document.getElementById('loading-overlay');
        if (errorOverlay)   errorOverlay.style.display   = 'none';
        if (loadingOverlay) loadingOverlay.style.display = 'none';

        renderRow('bar-5h', 'pct-5h', 'reset-5h',
            data.five_hour.utilization / 100, data.five_hour.resets_at);
        renderRow('bar-7d', 'pct-7d', 'reset-7d',
            data.seven_day.utilization / 100, data.seven_day.resets_at);
    }

    function renderError(message) {
        var errorOverlay   = document.getElementById('error-overlay');
        var loadingOverlay = document.getElementById('loading-overlay');
        var reason         = document.getElementById('error-reason');
        if (loadingOverlay) loadingOverlay.style.display = 'none';
        if (reason)         reason.textContent           = message || '';
        if (errorOverlay)   errorOverlay.style.display   = 'flex';
    }

    function renderLoading() {
        var loadingOverlay = document.getElementById('loading-overlay');
        var errorOverlay   = document.getElementById('error-overlay');
        if (errorOverlay)   errorOverlay.style.display   = 'none';
        if (loadingOverlay) loadingOverlay.style.display = 'flex';
    }

    function renderStale(isStale) {
        var dot = document.getElementById('status-dot');
        if (!dot) return;
        dot.classList.toggle('stale', !!isStale);
    }

    function renderPlaceholder() {
        var now = Date.now();
        renderData({
            five_hour: {
                utilization: 89.0,
                resets_at: new Date(now + 72 * 60000).toISOString()
            },
            seven_day: {
                utilization: 18.0,
                resets_at: new Date(now + (4 * 24 * 60 + 7 * 60) * 60000).toISOString()
            }
        });
    }

    global.ClaudeUsage = {
        configure:         configure,
        renderData:        renderData,
        renderError:       renderError,
        renderLoading:     renderLoading,
        renderStale:       renderStale,
        renderPlaceholder: renderPlaceholder,
        formatTimeUntil:   formatTimeUntil
    };
})(window);
