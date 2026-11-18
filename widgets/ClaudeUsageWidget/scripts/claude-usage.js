/**
 * @fileoverview Shared rendering module for the Claude Code Usage widget, used by both
 * index.html (iCUE widget) and the preview iframes. Polling, lifecycle and config are owned by
 * each host; this module only reads and writes the DOM and formats values.
 */
(function (global) {
    'use strict';

    /** Active rendering config. Every value is concrete; the defaults match the CSS theme. */
    var _cfg = {
        thresholdColorCoding: true,
        textColor:       '#FFFFFF',
        blockColor:      '#3B82F6',
        emptyBlockColor: '#484848',
        percentageColor: '#FFFFFF',
        colorLow:        '#10B981',
        colorMid:        '#F59E0B',
        colorHigh:       '#EF4444',
        backgroundColor: '#161616',
        transparency:    0
    };

    /** Last successfully rendered data, re-applied when the config changes mid-session. */
    var _lastData = null;

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


    function applyThemeVars() {
        var root = document.documentElement;
        root.style.setProperty('--bg-color',      _cfg.backgroundColor);
        root.style.setProperty('--widget-opacity', String(1 - _cfg.transparency / 100));
        root.style.setProperty('--text-color',     _cfg.textColor);
    }


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

    /** Returns a stable string key describing a block's visual state. */
    function blockKey(i, threshold, color) {
        var fi = Math.floor(threshold);
        if (i < fi) return 'f:' + color;
        if (i === fi && (threshold % 1) > 0.001)
            return 'p:' + Math.round((threshold % 1) * 10000) + ':' + color;
        return 'e';
    }

    /** Returns the CSS background value for a block state key. */
    function blockBg(key) {
        if (key === 'e') return _cfg.emptyBlockColor;
        if (key.charAt(0) === 'f') return key.slice(2);
        var parts = key.split(':');
        var frac  = parseInt(parts[1], 10) / 100;
        var color = parts[2];
        return 'linear-gradient(to right,' + color + ' ' + frac + '%,' + _cfg.emptyBlockColor + ' ' + frac + '%)';
    }

    function ensureBlocks(barEl) {
        if (!barEl || barEl.querySelector('.block')) return;
        for (var i = 0; i < 10; i++) {
            var b = document.createElement('div');
            b.className = 'block';
            barEl.appendChild(b);
        }
    }

    /**
     * Diff-based block updater.
     * - If utilization is unchanged from the last call, does nothing.
     * - Otherwise, only transitions blocks whose visual state changed.
     * - The first render is instant (no animation).
     * - Changed blocks fade out instantly, swap background, and fade in over 400 ms.
     * - One forced reflow per bar batches all block updates.
     * - configure() clears prevUtil, so after a color-scheme change the next call is a first
     *   render; the previous color can therefore safely be computed from the current config.
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

        forceReflow(barEl);

        toAnimate.forEach(function (b) {
            b.style.transition = 'opacity 400ms ease-in-out';
            b.style.opacity    = '1';
        });
    }

    /** Forces one layout pass so every block registers opacity 0 before its transition starts. */
    function forceReflow(el) {
        void el.offsetWidth;
    }

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

    /**
     * Applies a new rendering config. Only keys present in opts are updated.
     * Clears the block caches and re-renders at once if data is available,
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
        forgetRenderedUtilization();

        if (_lastData) renderData(_lastData);
    }

    /** Makes applyBlocks treat the next render as a first render, recomputing every block color. */
    function forgetRenderedUtilization() {
        var bars = document.querySelectorAll('.block-bar');
        for (var i = 0; i < bars.length; i++) {
            bars[i].dataset.prevUtil = '';
        }
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

    /**
     * Maps a state code to a distinct, actionable overlay. The vocabulary matches the service's
     * /status states, plus 'service_offline', which the host sets only when the fetch itself fails
     * (connection refused: the service is not running), as opposed to the service answering with
     * an error state. 'reason' may be a function of an optional detail (used by upstream_error to
     * show the upstream HTTP status).
     */
    var ERROR_STATES = {
        service_offline: {
            title:  'Service offline',
            reason: 'ClaudeUsageServer is not running'
        },
        reauth_required: {
            title:  'Login required',
            reason: 'run `claude login` in a terminal'
        },
        credentials_missing: {
            title:  'No credentials',
            reason: 'install & login to Claude Code'
        },
        refresh_failed: {
            title:  'Token refresh failing',
            reason: 'retrying — check service logs'
        },
        upstream_error: {
            title:  'Anthropic API error',
            reason: function (detail) {
                return detail ? ('HTTP ' + detail) : 'Anthropic API unavailable';
            }
        }
    };

    /**
     * Shows the error overlay.
     * @param {string} message A state code (see ERROR_STATES) or an unmapped raw string.
     * @param {*} [detail] Optional extra, e.g. the upstream HTTP status for upstream_error.
     */
    function renderError(message, detail) {
        var errorOverlay   = document.getElementById('error-overlay');
        var loadingOverlay = document.getElementById('loading-overlay');
        var title          = document.getElementById('error-title');
        var reason         = document.getElementById('error-reason');

        var mapped     = ERROR_STATES[message];
        var reasonText;
        if (mapped) {
            reasonText = (typeof mapped.reason === 'function')
                ? mapped.reason(detail)
                : mapped.reason;
        } else {
            reasonText = message || '';
        }

        if (loadingOverlay) loadingOverlay.style.display = 'none';
        if (title)  title.textContent  = mapped ? mapped.title : 'Unable to fetch usage';
        if (reason) reason.textContent = reasonText;
        if (errorOverlay) errorOverlay.style.display = 'flex';
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
