/* Shared rendering module for Claude Code Usage widget.
   Used by both index.html (iCUE widget) and preview iframes.
   Polling, lifecycle, and config are owned by each host — this module only
   reads/writes the DOM and formats values. */
(function (global) {
    'use strict';

    function getColor(utilization) {
        if (utilization < 0.6) return '#10B981';
        if (utilization < 0.8) return '#F59E0B';
        return '#EF4444';
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
     */
    function applyBlocks(barEl, utilization) {
        ensureBlocks(barEl);

        var prevRaw  = barEl.dataset.prevUtil;
        var prevUtil = (prevRaw !== undefined && prevRaw !== '') ? parseFloat(prevRaw) : NaN;
        var isFirst  = isNaN(prevUtil);

        if (!isFirst && Math.abs(prevUtil - utilization) < 0.00001) return;
        barEl.dataset.prevUtil = String(utilization);

        var color      = getColor(utilization);
        var threshold  = utilization * 10;
        var prevColor  = isFirst ? null : getColor(prevUtil);
        var prevThresh = isFirst ? NaN  : prevUtil * 10;
        var isCrit     = utilization >= 0.9;

        var blocks    = barEl.querySelectorAll('.block');
        var toAnimate = [];

        blocks.forEach(function (block, i) {
            var newKey   = blockKey(i, threshold, color);
            var newPulse = isCrit && (i < Math.floor(threshold));

            if (isFirst) {
                block.style.background = blockBg(newKey);
                block.classList.toggle('block-pulse', newPulse);
                return;
            }

            var oldKey   = blockKey(i, prevThresh, prevColor);
            var wasPulse = (prevUtil >= 0.9) && (i < Math.floor(prevThresh));

            if (newKey === oldKey && newPulse === wasPulse) return;

            block.classList.remove('block-pulse');
            block.style.transition = 'none';
            block.style.opacity    = '0';
            block.style.background = blockBg(newKey);
            block._newPulse = newPulse;
            toAnimate.push(block);
        });

        if (toAnimate.length === 0) return;

        /* Single reflow so all blocks register opacity:0 before transition starts. */
        void barEl.offsetWidth;

        toAnimate.forEach(function (b) {
            b.style.transition = 'opacity 400ms ease-in-out';
            b.style.opacity    = '1';
            var np = b._newPulse;
            delete b._newPulse;
            if (np) setTimeout(function () { b.classList.add('block-pulse'); }, 400);
        });
    }

    function renderRow(barId, pctId, resetId, utilization, resetsAt) {
        var barEl   = document.getElementById(barId);
        var pctEl   = document.getElementById(pctId);
        var resetEl = document.getElementById(resetId);
        if (!barEl || !pctEl || !resetEl) return;

        applyBlocks(barEl, utilization);

        /* Text: instant update, no animation. */
        var color = getColor(utilization);
        pctEl.textContent = Math.round(utilization * 100) + '%';
        pctEl.style.color = color;
        resetEl.textContent = formatTimeUntil(resetsAt);
    }

    function renderData(data) {
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
        renderData:        renderData,
        renderError:       renderError,
        renderLoading:     renderLoading,
        renderStale:       renderStale,
        renderPlaceholder: renderPlaceholder,
        formatTimeUntil:   formatTimeUntil
    };
})(window);
