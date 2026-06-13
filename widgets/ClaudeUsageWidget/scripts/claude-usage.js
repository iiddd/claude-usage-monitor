/* Shared rendering module for Claude Code Usage widget.
   Used by both index.html (iCUE widget) and preview.html (browser preview).
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
        if (diffMs < 60000) return 'resetting...';

        var totalMinutes = Math.floor(diffMs / 60000);
        var totalHours   = Math.floor(totalMinutes / 60);
        var days         = Math.floor(totalHours / 24);
        var hours        = totalHours % 24;
        var minutes      = totalMinutes % 60;

        if (days >= 1)       return days + 'd ' + hours + 'h';
        if (totalHours >= 1) return totalHours + 'h ' + minutes + 'm';
        return totalMinutes + 'm';
    }

    function ensureBlocks(barEl) {
        if (!barEl || barEl.querySelector('.block')) return;
        for (var i = 0; i < 10; i++) {
            var b = document.createElement('div');
            b.className = 'block';
            barEl.appendChild(b);
        }
    }

    function applyBlocks(barEl, utilization) {
        var color     = getColor(utilization);
        var threshold = utilization * 10;
        var blocks    = barEl.querySelectorAll('.block');

        blocks.forEach(function (block, i) {
            block.classList.remove('block-pulse');

            if (i < Math.floor(threshold)) {
                block.style.background = color;
                if (utilization >= 0.9) block.classList.add('block-pulse');
            } else if (i === Math.floor(threshold) && threshold % 1 > 0.001) {
                var frac = (threshold % 1) * 100;
                block.style.background =
                    'linear-gradient(to right, ' + color + ' ' + frac + '%, #2C2C2C ' + frac + '%)';
            } else {
                block.style.background = '#2C2C2C';
            }
        });
    }

    function flashBar(barEl) {
        if (!barEl) return;
        barEl.style.transition = 'none';
        barEl.style.opacity    = '0';
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                barEl.style.transition = 'opacity 0.2s ease-in';
                barEl.style.opacity    = '1';
            });
        });
    }

    function renderRow(barId, pctId, resetId, utilization, resetsAt) {
        var barEl   = document.getElementById(barId);
        var pctEl   = document.getElementById(pctId);
        var resetEl = document.getElementById(resetId);
        if (!barEl || !pctEl || !resetEl) return;

        ensureBlocks(barEl);
        flashBar(barEl);
        applyBlocks(barEl, utilization);

        var color = getColor(utilization);
        pctEl.textContent = Math.round(utilization * 100) + '%';
        pctEl.style.color = color;
        pctEl.classList.toggle('pct-pulse', utilization >= 0.9);

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
        renderData: renderData,
        renderError: renderError,
        renderLoading: renderLoading,
        renderStale: renderStale,
        renderPlaceholder: renderPlaceholder,
        formatTimeUntil: formatTimeUntil
    };
})(window);
